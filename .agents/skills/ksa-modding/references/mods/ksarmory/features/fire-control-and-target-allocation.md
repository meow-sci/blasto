---
type: Mod Feature
title: Fire control ladder and per-craft target allocation
description: Each weapon system decides once per simulated step whether to launch through an ordered ladder of named gates (platform, magazine, lock, drives, IFF, gimbal, salvo, envelope) that also reports why it holds, counting rounds per target per craft from what is in the air
mod: ksarmory
category: vehicles-and-parts
techniques: [starmap-lifecycle, harmony-prefix, gate-ladder, reference-keyed-dictionary, frame-ordering]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Vehicle, KSA.Universe, KSA.Program]
tags: [fire-control, salvo, allocation, auto-engage, trigger, turret, settle, headless-tests]
---

KSArmory's fire control is a per-weapon decision made once per simulated step: given the radar's
lock, is this launcher clear to put another round on it right now? The answer comes from
`FireLadder.Holding`, an ordered list of gates whose **first** refusal is both the decision and a
human-readable reason ("drives still settling", "salvo committed", "target out of reach (24.1 km,
envelope 1.2-20.0 km)") shown beside the trigger. Each gate is marked as binding (also stops the
manual trigger) or auto-only (only stops auto-engage). A second layer, `Armaments`, rebuilds per
craft how many rounds are in the air against each target, so two launchers on one aircraft share
`RoundsPerTarget` instead of each firing a full salvo.[^ksarmory-fireladder][^ksarmory-armaments]

Everything that decides is in `Sim/` (`FireLadder`, `FireGate`, `TargetAllocation`,
`WeaponSelection`, `GuardState`) and covered by headless tests; `WeaponSystem` only samples the
world into a `FireConditions` record and acts on the verdict.[^ksarmory-fireladdertests]

## How it works

**Per-step order inside `WeaponSystem.Update`** (WeaponSystem.cs L812-L844):[^ksarmory-weaponsystem]

1. `FillIncoming` (other systems' rounds), `Radar.Scan`.
2. `AttributeRoundsToTracks`: every track's `RoundsAssigned` = the craft tally's
   `CommittedTo(handle)` when `Config.ShareTargetsAcrossWeapons` (default on), else this weapon's own
   flying rounds matched by `TargetRef`.
3. `UpdateTurret` (lay onto `Radar.Locked`, or the most urgent threat while no lock).
4. `UpdateRounds` (integrate rounds already in the air).
5. `UpdateFireControl` (missiles), `UpdateGunFireControl` (cannon).

**Missile decision** (`UpdateFireControl`):

- Build `FireConditions` once (one instant for every rung) and ask the ladder; log `Hold` only on
  change. A second ladder, `TriggerHold`, answers for the armament and target the trigger would use.
- Tick the salvo timer; run the reload cycle (`ReloadSeconds`) when the magazine is empty.
- If held, stop. If `AutoEngage` is off, stop: auto-engage is deliberately not a rung, so "clear to
  fire" still means the trigger works.
- Re-check IFF, salvo capacity, envelope, `Munition.Powered`, then `Fire(Radar.Locked)`.
- `Fire` refuses dead, friendly, or non-radiating (anti-radiation round) targets, calls `Commit`, and
  on success increments both the craft tally and the track at once, so the next system stepped in
  the same frame sees the round.
- `Commit` takes a tube, builds the launch, refuses a seeker round whose aimpoint is outside its
  gimbal (`FireGate.CanGuideOntoAimpoint`, tube returned), creates an `Interceptor` or `Slug`, and
  sets `_salvoTimer = Profile.SalvoSpacing` (0.45 s default).

**The ladder** (`FireLadder.Holding`), first match wins:

| Rung | Reason text | Binds trigger |
|---|---|---|
| Platform, launcher part | "no platform", "no launcher resolved on this craft" | yes |
| Magazine | "reloading (n s)", "missiles are switched off", "out of rounds", "between salvos" | yes |
| Unpowered store loaded | (clear: releases ignore every rung below) | n/a |
| Belt (gun-only) | "cannon are switched off", "belt empty", "drives still settling" | yes |
| Nothing locked | "nothing detected", "no firing solution yet (n track(s))", "X is not on the radar" | missiles yes, guns no |
| Settle | "drives still settling", "the cannon has the bearing", "the cursor has the bearing" | yes |
| Lock | "no lock" | missiles yes |
| Anti-radiation | "X is not radiating" | missiles yes |
| IFF | "target is not engageable (IFF)" | missiles yes |
| Seeker gimbal | "X is N deg off the tube, past the seeker's M deg" | yes |
| Dwell / threat | "no firing solution yet" | no |
| Rounds per target | "salvo committed" | no |
| Envelope | "target out of reach (r km, envelope a-b km)" | no |

**Settle-on-aim.** `IsLaid` = `FireGate.IsLaid(aiming, trains, drivesAccepted, assembliesResolved,
settled)`: true when nothing is being laid; otherwise both axes within 0.05 rad of the order
(`Turret.OnTarget`) for `SettleSeconds` (0.35 s default) unbroken, the engine still accepting the
drive writes, and the pod subpart found. `FireGate.MissilesMayFire` holds a launch-along-tube missile
while the ring is laid on the cannon's lead or the cursor instead of the target.[^ksarmory-firegate][^ksarmory-turret][^ksarmory-launcherprofile]

**Cannon.** `UpdateGunFireControl` wants to fire on a manual trigger or on auto-engage with a lock
inside `Shell.MinRange..MaxRange`; it latches `_burstTrack` at the start of a burst so a flickering
lock does not cut it, and `FireGate.BurstMayContinue` lets an operator burst run to its end while an
automatic one stops when off lay or the target dies.

**Allocation.** `Armaments.Refresh` clears every craft's `TargetAllocation`, hands it to each crewed
system (`CraftRounds`) and commits each flying round's `TargetRef` (reference identity, so a craft or
a round both count). A round with no target (a bomb on a coordinate) commits nothing. Craft no
longer alive or no longer armed are pruned. Nothing is incremented or decremented across frames,
so a round that hit, burst, went loose or lost lock simply stops being counted.[^ksarmory-targetallocation]

**Selection and guard** (`WeaponSelection`, `GuardState`): the trigger steps through stations of one
store group with `NextStation`, which starts after the last one fired so a symmetric pair alternates;
the row's guard switch reads `Off`, `Partly` or `Guarding` over every system that can auto-engage
and any click short of `Guarding` turns all of them on.[^ksarmory-weaponselection][^ksarmory-guardstate][^ksarmory-ui]

**Defaults**: `RoundsPerTarget = 2`, `AutoEngage` per installation, `SalvoSpacing = 0.45 s`,
`SettleSeconds = 0.35 s`.[^ksarmory-systemconfig]

## Lifecycle and timing

`Armaments.Refresh` (KSArmoryMod.cs L774) and the fire loop `foreach ... e.Weapon.Update(step,
_airborne)` (L776) run in `StepSimulation`, inside the gate that only runs on a frame where
`KsaWorld.ConsumeSimStep()` returned a new engine step, after `CollectTeams` (L759),
`CollectAirborne` (L763) and the countermeasure update (L768); `UpdateLoose` (L784) then steps the
rounds of systems whose craft died. `step` is the engine step clamped by the round-faithful limit.[^ksarmory-mod]

`StepSimulation` runs from `StepOnce`, claimed by `[StarMapAfterGui]` (postfix on
`Program.OnDrawUiViewports`, Program.cs L2268) on normal frames, by the Harmony prefix on
`Program.OnFrameCelestials` (L2320) when the UI is hidden, or by `[StarMapAfterOnFrame]` as the last
fallback. All of these run after `PrepareFrame` applied the vehicle step
(`Universe.ApplyVehicleSolvers`, Universe.cs L1922, `_lastSimStep = _nextSimStep` L1948) and
dispatched the next (`ExecuteNextVehicleSolvers`, Program.cs L2220).[^ksa-program][^ksa-universe]

What that implies:

- The lock, platform pose and `Vehicle.Asmb2Ego` used to place a launch are the state at
  `Universe.GetElapsedTime()` (Universe.cs L2399), the end of the step just applied.[^ksa-vehicle]
- **Fire control runs after the round update.** A round created in this step is first integrated
  in the next frame's `UpdateRounds`, when the platform sample has advanced with it; integrating it
  in its launch frame bakes a frame of ecliptic motion (658.78 m at 0.04 s in the mod's
  measurement) into its travel permanently (docs/FRAMES-AND-EPOCHS.md L122-L125).[^ksarmory-frames]
- **The tally is built before any system fires.** A tally rebuilt while systems are being stepped
  would under-count by roster iteration order; `Fire` patches the live tally for the rest of the
  frame.
- **Manual fire is a frame later.** The FIRE button (`UiWeapons.FireGroup`, L78-L88) calls
  `FireAtLock` from the panel, which `OnAfterGui` draws after `StepOnce`; a missile is created
  there and first integrated next frame, and a cannon trigger only sets `_manualTrigger`, which the
  next step's `UpdateGunFireControl` reads.[^ksarmory-uiweapons]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L812-L844) | `Update`: scan, attribute, turret, rounds, then fire control |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L1012-L1100) | `CraftRounds`, `AttributeRoundsToTracks`, `UpdateFireControl` |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L596-L635) | `IsLaid`, `GunsAreLaid`, `Aiming` |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L1432-L1520) | `UpdateGunFireControl`: burst latch, belt reload |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L2199-L2231) | `Fire(Track)`: refusals, commit, immediate tally |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L2444-L2540) | `Commit`: tube, gimbal gate, round creation, salvo timer |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L2800-L2840) | `TriggerTarget`, `FireAtLock` (manual trigger) |
| [src/KSArmory/Sim/FireLadder.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/FireLadder.cs) | `FireConditions`, `FireHold`, the ordered ladder |
| [src/KSArmory/Sim/FireGate.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/FireGate.cs) | Laid, burst continuation, cannon owns the bearing, seeker gimbal |
| [src/KSArmory/Sim/Turret.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Turret.cs#L124-L141) | On-target tolerance and settle timer |
| [src/KSArmory/Ksa/Armaments.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Armaments.cs) | Per-craft tally rebuilt every step, pruned on death |
| [src/KSArmory/Sim/TargetAllocation.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TargetAllocation.cs) | Reference-keyed rounds-in-the-air count |
| [src/KSArmory/Sim/WeaponSelection.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/WeaponSelection.cs) | Wrapping selector, alternating station |
| [src/KSArmory/Sim/GuardState.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/GuardState.cs) | Craft-level auto-engage tri-state |
| [src/KSArmory/Ksa/Ui/UiWeapons.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiWeapons.cs#L76-L120) | FIRE button: next station of the group |
| [src/KSArmory/Sim/Config.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Config.cs#L206-L219) | `ShareTargetsAcrossWeapons` |
| [tests/KSArmory.Tests/TargetAllocationTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/TargetAllocationTests.cs) | Allocation cases |

## Snippets

```csharp
// src/KSArmory/Ksa/WeaponSystem.cs L1079-L1100
        if (Hold is not null) return;

        // The switch that separates shooting on its own from shooting when told. Gated here rather
        // than in the ladder above so that "clear to fire" still means the trigger will work.
        if (!_policy.AutoEngage) return;

        Track target = Radar.Locked!;
        if (!ThreatModel.MayEngage(target, _policy.Iff)) return;
        if (!ThreatModel.HasSalvoCapacity(target, _policy.RoundsPerTarget)) return;

        // Detection reaches 36 km; the round reaches 20 km. Without this the system empties
        // itself at contacts it cannot possibly catch: an 8.7 km crossing shot expires at 22 s
        // having never closed.
        if (!ThreatModel.InEngagementEnvelope(target, Munition)) return;

        // A released store has no business being launched at a track: it leaves the rack and
        // falls, and the log would record a shot at something it was never going to reach. A tail
        // kit does not change that -- it steers onto a fixed point, and cannot chase anything.
        if (!Munition.Powered) return;

        Fire(target);
    }
```

```csharp
// src/KSArmory/Sim/FireLadder.cs L203-L239
        // A contact that is not closing, or not yet held for the lock time. The trigger fires at
        // whatever is locked, which is what locking a passer-by with a shift-click is for.
        if (!now.HasFiringSolution)
        {
            return AutoOnly($"no firing solution yet ({now.TrackCount} track(s))");
        }

        // Economy, not capability: another round on this target would be spent rather than wasted,
        // and an operator who asks for it has decided that. The trigger does not consult it.
        if (!ThreatModel.HasSalvoCapacity(locked, policy.RoundsPerTarget)) return AutoOnly("salvo committed");

        if (!ThreatModel.InEngagementEnvelope(locked, munition))
        {
            // With the numbers: "out of reach" reads as too far, and the usual cause is a target
            // that came inside the minimum instead.
            //
            // Auto-engage's own, for the same reason as the salvo count above. The floor is about a
            // round still boosting and the ceiling about one expiring short, and neither stops the
            // shot happening -- so the trigger fires and the round flies, which is what made this
            // line read as a fault.
            return AutoOnly($"target out of reach ({locked.Range / 1000.0:F1} km, envelope "
                            + $"{munition.MinRange / 1000f:F1}-"
                            + $"{munition.MaxRange / 1000f:F1} km)");
        }

        return null;
    }

    // Stops the round leaving the rail, so it stops the operator too.
    private static FireHold Binding(string reason) => new(reason, BindsTrigger: true);

    // Stops fire control shooting on its own, and nothing else.
    private static FireHold AutoOnly(string reason) => new(reason, BindsTrigger: false);

    // A missile needs a target to leave the rail; a burst goes where the guns are laid, so for a
    // gun everything about the target is automatic fire's.
    private static FireHold Targeting(bool tubes, string reason) => tubes ? Binding(reason) : AutoOnly(reason);
```

```csharp
// src/KSArmory/Ksa/Armaments.cs L45-L68
    public void Refresh(IEnumerable<WeaponSystems.Entry> systems)
    {
        ArgumentNullException.ThrowIfNull(systems);

        foreach (TargetAllocation a in _byCraft.Values) a.Clear();

        foreach (WeaponSystems.Entry entry in systems)
        {
            if (entry.Weapon.Platform is not { } craft) continue;

            TargetAllocation tally = For(craft);

            // Handed to the system here rather than by the caller, so it cannot be stepped with a
            // tally from a craft it has since been rehomed off.
            entry.Weapon.CraftRounds = tally;

            foreach (IProjectile round in entry.Weapon.Rounds)
            {
                if (round.State == RoundState.Flying) tally.Commit(round.TargetRef);
            }
        }

        Prune(systems);
    }
```

```csharp
// src/KSArmory/Sim/FireGate.cs L19-L21
    public static bool IsLaid(bool aiming, bool trains, bool drivesAccepted,
                              bool assembliesResolved, bool settled)
        => !aiming || !trains || (drivesAccepted && assembliesResolved && settled);
```

## Game APIs touched

Fire control is mod-side arithmetic; its only engine contact is reading the state it decides on.

| API | Decompiled file | Use |
|---|---|---|
| `Vehicle.Asmb2Ego`, `Vehicle.GetPositionEcl`, `Vehicle.GetVelocityEcl` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L502) | Launch attitude and frame of a new round (L954-L963) |
| `Universe.ApplyVehicleSolvers`, `Universe.ExecuteNextVehicleSolvers`, `Universe.GetElapsedTime` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L2135) | Why every decision is made on end-of-step state (L2399) |
| `Program.OnDrawUiViewports`, `Program.OnFrameCelestials` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2320) | The two hooks the step normally runs from |

## Pitfalls and version notes

- **Over-commitment is per craft, not per weapon.** Without the shared tally two rails each obey
  `RoundsPerTarget` and the target gets twice as many rounds. `ShareTargetsAcrossWeapons = false`
  restores per-weapon counting on purpose.
- **Update the tally on launch, not only on rebuild.** Systems on one craft are stepped in sequence
  in the same frame; `Fire` commits immediately for that reason.
- **Do not make auto-engage a ladder rung.** It would stop the ladder at a switch that blocks
  nothing the operator asked for and hide every real reason below it.
- **A gun-only mount must skip the missile rungs**, or its empty-by-construction magazine reports
  "out of rounds" forever while the cannon fire.
- **README vs code.** README.md L242 says the launcher "commits Rounds per target rounds before
  re-evaluating"; the code re-evaluates each step and counts only rounds still flying, with
  `SalvoSpacing` between launches.[^ksarmory-readme]
- **`ShotGroup` and `SalvoProbe` are not fire control.** `ShotGroup`/`ShotBoard` score a scripted
  ballistic salvo (used by `BallisticScenario`) and `SalvoProbe` lets a MIRV warhead borrow a
  sibling's release probe (used by `IcbmComputer`); see the ballistic computer and scenario runner
  features.[^ksarmory-shotgroup][^ksarmory-salvoprobe]

## See also

- [Radar search cone, masking and lock dwell](./radar-search-and-lock.md)
- [CPA threat classification](./cpa-threat-classification.md)
- [IFF teams and allegiance](./iff-teams-and-allegiance.md)
- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Articulated mount subparts](./articulated-mount-subparts.md)
- [Unguided ballistic lead](./unguided-ballistic-lead.md)
- [Cursor-ray world click tools](./cursor-ray-world-click-tools.md)
- [Operator panel in ImGui](./operator-panel-imgui.md)
- [MIRV bus trim and release](./mirv-bus-trim-and-release.md)
- [Unattended scenario runner](./unattended-scenario-runner.md)
- [Ecliptic frames and epochs](./ecliptic-frames-and-epochs.md)
- [Gate automated fire with an ordered ladder and a per-craft target tally](../../../howto/vehicles-and-parts/craft-tracking/target-and-gate-automated-weapons/target-and-gate-automated-weapons-3.md) - how-to article demonstrating this feature

[^ksarmory-fireladder]: src/KSArmory/Sim/FireLadder.cs
[^ksarmory-armaments]: src/KSArmory/Ksa/Armaments.cs
[^ksarmory-fireladdertests]: tests/KSArmory.Tests/FireLadderTests.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-firegate]: src/KSArmory/Sim/FireGate.cs
[^ksarmory-turret]: src/KSArmory/Sim/Turret.cs
[^ksarmory-launcherprofile]: src/KSArmory/Sim/LauncherProfile.cs
[^ksarmory-targetallocation]: src/KSArmory/Sim/TargetAllocation.cs
[^ksarmory-weaponselection]: src/KSArmory/Sim/WeaponSelection.cs
[^ksarmory-guardstate]: src/KSArmory/Sim/GuardState.cs
[^ksarmory-ui]: src/KSArmory/Ksa/Ui/Ui.cs
[^ksarmory-systemconfig]: src/KSArmory/Sim/SystemConfig.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-frames]: docs/FRAMES-AND-EPOCHS.md
[^ksarmory-uiweapons]: src/KSArmory/Ksa/Ui/UiWeapons.cs
[^ksarmory-readme]: README.md
[^ksarmory-shotgroup]: src/KSArmory/Sim/ShotGroup.cs
[^ksarmory-salvoprobe]: src/KSArmory/Sim/SalvoProbe.cs
[^ksa-program]: KSA.Program (decompiled)
[^ksa-universe]: KSA.Universe (decompiled)
[^ksa-vehicle]: KSA.Vehicle (decompiled)
