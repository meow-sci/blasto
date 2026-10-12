---
type: How-To
title: Gate automated fire with an ordered ladder and a per-craft target tally
description: Express fire permission as ordered named gates whose first refusal explains why, and share rounds-per-target across launchers through a tally rebuilt each step
category: vehicles-and-parts
group: craft-tracking
topic: target-and-gate-automated-weapons
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/fire-control-and-target-allocation]
techniques: [gate-ladder, reference-keyed-dictionary]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Vehicle, KSA.Universe, KSA.CelestialSystem, KSA.Program, KSA.Part, KSA.Celestial]
tags: [fire-control, gates, allocation, weapons, explainable]
---
Outcome: a per-weapon decision made once per simulated step: is this launcher clear to put another round on the locked target right now? The answer is the **first** refusal of an ordered list of gates, which is both the decision and a human-readable reason ("drives still settling",
"salvo committed", "target out of reach (24.1 km, envelope 1.2-20.0 km)"). Each gate is marked as binding (also stops the manual trigger) or auto-only. A second layer rebuilds per craft how many rounds are in the air against each target so two launchers on one aircraft share `RoundsPerTarget`.

Prerequisite: StarMap. Demonstrated by [ksarmory/fire-control-and-target-allocation](../../../../mods/ksarmory/features/fire-control-and-target-allocation.md); the lock comes from [article 2](target-and-gate-automated-weapons-2.md).

## Background: how the game does it

Fire control is mod-side arithmetic; its only engine contact is reading the state it decides on (`Vehicle.Asmb2Ego`, `GetPositionEcl`, `GetVelocityEcl`) at the end of the step just applied. Everything that decides lives in a game-free layer (`FireLadder`, `FireGate`, `TargetAllocation`) and is covered by headless tests; the
game-facing class only samples the world into a `FireConditions` record and acts on the verdict.

## Lifecycle and timing

Per step inside `WeaponSystem.Update`: `FillIncoming`, radar scan, attribute rounds to tracks, turret lay, **update rounds already in the air**, then fire control. Rounds are updated first so a round created this step is first integrated next frame, when the platform sample has advanced with it; integrating it in its launch frame would bake a frame of
ecliptic motion into its travel permanently (658.78 m of travel at 0.04 s in the mod's own measurement). The craft tally is built before any system fires; `Fire` patches the live tally for the rest of the frame. A manual FIRE press from the panel (drawn after `StepOnce` in the same `[StarMapAfterGui]` call) takes effect one frame later. `StepSimulation` is reached through `StepOnce` under `[StarMapAfterGui]` (`frame:draw-ui`; postfix on `Program.OnDrawUiViewports`, only while the HUD is shown), or with the UI hidden from a Harmony prefix on `Program.OnFrameCelestials` (`frame:celestials`),
or from `[StarMapAfterOnFrame]` as the last fallback (`frame:after-on-frame`). All run after `PrepareFrame` applied the vehicle step and dispatched the next, so positions and velocities are end-of-step values at one instant. Reading vehicle state here is safe (the workers write their own copies);
whatever is decided only reaches a craft in a later step. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

```csharp
// src/KSArmory/Ksa/WeaponSystem.cs L812-L839
public void Update(double dt, IReadOnlyList<IContact>? airborne = null)
{
    if (Platform is null || !KsaWorld.IsAlive(Platform)) return;

    if (double.IsFinite(dt) && dt > 0.0) _finTestSeconds += dt;

    _clock += dt;

    FillIncoming(airborne);

    Radar.Scan(Platform, Boresight, dt, _incoming);
    AttributeRoundsToTracks();
    UpdateTurret(dt);

    // Rounds before fire control, so a round fired this frame is not integrated until the
    // next one. TravelSinceLaunch differences two platform-relative offsets, which cancels the
    // platform's ~29.8 km/s only while the sample advances alongside the round. Integrating a
    // new round in its own launch frame leaves the sample still for one step, so a frame of
    // ecliptic motion lands in travel permanently: 658.78 m of travel at an age of 0.04 s on
    // a round doing 124 m/s.
    //
    // The cost is one frame before a new round moves, which is correct anyway: it is still in
    // the tube on the frame the trigger is pulled.
    UpdateRounds(dt);
    UpdateFireControl(dt);
    UpdateGunFireControl(dt);
    TrimEvents();

```

## Steps

1. **Write the gates as an ordered function returning a reason or null.** First match wins. Mark each as `Binding` (stops the trigger) or `AutoOnly`; gates about economy (salvo committed) and reach (envelope) are auto-only because the operator has decided to spend the round:

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

2. **Do not make auto-engage a rung.** It is checked after the ladder so "clear to fire" still means the trigger works:

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

3. **Gate on being laid before firing.** Both axes within tolerance for a settle time, the drives accepting writes and the pod subpart found:

```csharp
// src/KSArmory/Sim/FireGate.cs L19-L21
public static bool IsLaid(bool aiming, bool trains, bool drivesAccepted,
                          bool assembliesResolved, bool settled)
    => !aiming || !trains || (drivesAccepted && assembliesResolved && settled);
```

4. **Share targets across weapons.** Rebuild the tally from scratch each step from the rounds actually flying, keyed by reference identity; nothing is incremented or decremented across frames, so a round that hit, burst or lost lock simply stops being counted:

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

5. **Show the reason beside the trigger.** The text is the gate's reason; a second ladder, `TriggerHold`, answers for the armament and target the trigger would use.

## Complete example

- [src/KSArmory/Sim/FireLadder.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/FireLadder.cs#L1-L240)[^ksarmory-fireladder], [src/KSArmory/Sim/FireGate.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/FireGate.cs#L1-L60)[^ksarmory-firegate] and [src/KSArmory/Ksa/Armaments.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Armaments.cs#L1-L80)[^ksarmory-armaments].
- [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L812-L1100)[^ksarmory-weaponsystem] - `Update`, `UpdateFireControl`, `Fire`, `Commit`.

## Variations and gotchas

- **Over-commitment is per craft, not per weapon.** Without the shared tally two rails each obey `RoundsPerTarget` and the target gets twice as many rounds. `ShareTargetsAcrossWeapons = false` restores per-weapon counting on purpose.
- **Update the tally on launch, not only on rebuild.** Systems on one craft are stepped in sequence in the same frame.
- **A gun-only mount must skip the missile rungs**, or its empty-by-construction magazine reports "out of rounds" forever while the cannon fire.
- **Detection reaches farther than the round.** Without an envelope gate the system empties itself at contacts it cannot catch.
- **Tests belong on the ladder.** It is pure; every refusal text can be asserted.

## See also

- [Overview](overview.md); [Article 2](target-and-gate-automated-weapons-2.md)
- [ksarmory/fire-control-and-target-allocation](../../../../mods/ksarmory/features/fire-control-and-target-allocation.md)
- [Track which craft carry your part](../track-and-classify-craft/overview.md)

[^ksarmory-fireladder]: KSArmory: src/KSArmory/Sim/FireLadder.cs
[^ksarmory-firegate]: KSArmory: src/KSArmory/Sim/FireGate.cs
[^ksarmory-armaments]: KSArmory: src/KSArmory/Ksa/Armaments.cs
[^ksarmory-weaponsystem]: KSArmory: src/KSArmory/Ksa/WeaponSystem.cs
