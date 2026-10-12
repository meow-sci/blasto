---
type: Mod Feature
title: Mod-simulated rounds integrated in sub-steps inside the frame
description: Missiles, shells and bombs are not KSA vehicles but plain C# objects the mod integrates itself, in fixed sub-steps across each applied simulation step, from the same pass that draws them
mod: ksarmory
category: physics
techniques: [starmap-lifecycle, harmony-prefix, custom-physics, consume-sim-step, sim-boundary]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Universe, KSA.Vehicle, KSA.Celestial, KSA.CelestialSystem, KSA.Astronomical, KSA.Program]
tags: [projectiles, integration, sub-stepping, ecliptic-frame, epochs, custom-physics, rounds]
---

Every round KSArmory fires (a guided missile, a 20 mm shell, a B61, a reentry vehicle) lives only
inside the mod. It is a C# object implementing `IProjectile`, held in its launcher's
`WeaponSystem`, integrated by the mod in fixed sub-steps across each simulation step the engine
applied, and reaped by the mod when it detonates, expires or is shot down. KSA never sees it as a
vehicle; what the player sees is a hidden launcher subpart whose transform the mod writes each
frame.[^ksarmory-readme]

The README gives the reasons: sub-frame accuracy (at 2 km/s of closing speed one frame is about
67 m, far more than any fuse radius) and not touching the save. The same pattern works for any mod
that needs many short-lived, fast objects without the cost and save-file footprint of real
vehicles.

## How it works

**One interface, two flight models.** `IProjectile` is the whole contract the game side
uses.[^ksarmory-iprojectile]

| Member | Meaning |
|---|---|
| `PositionEcl`, `VelocityEcl` | Absolute ecliptic state (carries the planet's ~29.8 km/s) |
| `OffsetFromPlatform` | `PositionEcl - platformEcl` measured after the step; this, not `PositionEcl`, is drawn |
| `TravelSinceLaunch`, `VelocityLocal`, `DistanceFlown` | Frame-independent quantities for drawing, orientation and telemetry |
| `FaithfulStepSeconds` | Longest step the round can take right now (shorter in air) |
| `DetonationElapsedInFrame` | When it burst, negative, between `-dt` and 0, relative to the end-of-step world sample |
| `State` | `Flying`, `Detonated`, `Expired`, `ShotDown` (`ShootDown()` is the only external transition) |
| `Reanchor(delta)` | Moves every stored offset (current, launch, trail) onto a new anchor at once |
| `Update(dt, target, gravity, frameVelocityEcl, platformEcl, munition, density)` | Advance by `dt` simulated seconds |

- `Interceptor` is the guided model: integrate, guide (see
  [proportional navigation](./proportional-navigation-guidance.md)), fuse.[^ksarmory-interceptor]
- `Slug` is the unguided model (gun shell, bomb, reentry vehicle, tail-kit store): ballistics, drag,
  optional tail-kit steering, contact/time/height/ground fuses.[^ksarmory-slug]
- Both live in `Sim/`, which references no KSA type, so the headless test project links them
  wholesale.

**One driver for game and rig.** `RoundDriver.Fly` is "the one place a round is advanced by one
frame". It assigns a `RoundFields` record of per-sub-step lookups (gravity, air density, air
velocity, ground test, ground drift, reach classifier) onto the round, then calls `Update`. The game
passes the real lookups; the test rig passes its own; `RoundFields.Held` (all null) exists only to
price what re-reading costs.[^ksarmory-rounddriver]

**Sub-step sizing.**

| Round | Sub-steps per frame | Step `h` |
|---|---|---|
| `Interceptor` | `clamp(ceil(dt / 0.005), 1, 64)` | `dt / steps` (5 ms nominal) |
| `Slug` | `min(munition.MaxSubSteps, max(1, ceil(dt / munition.SubStep)))` | `dt / steps` |

- `MunitionProfile.SubStep` defaults to 5 ms (`Interceptor.SubStep`); `MaxSubSteps` scales so
  `SubStep * MaxSubSteps` still spans `MaxFaithfulStepSeconds` (default 0.32 s = 5 ms x 64).
  A reentry vehicle can ask for 1 ms; a 150-shell burst stays at 5 ms.[^ksarmory-munition]
- The frame step itself is clamped before any round sees it: `step = min(dtSim, faithful)`, where
  `faithful` is the smallest `MaxFaithfulStepSeconds` of any round in the air
  (`WeaponSystems.FaithfulStep`). Time beyond the clamp is discarded and logged
  (`ReportOverrun`); the warp policy then slows the world using each round's
  `FaithfulStepSeconds`, which drops to 0.05 s once air density exceeds `1e-4` of sea
  level.[^ksarmory-mod][^ksarmory-weaponsystems][^ksarmory-simclock]

**The per-frame order inside one system** (`WeaponSystem.Update`): fill the incoming list, radar
scan, turret, `UpdateRounds(dt)`, then fire control. Rounds go before fire control on purpose: a
round fired this frame is first integrated next frame, otherwise one frame of ecliptic motion is
baked into its `TravelSinceLaunch` forever (658.78 m measured at age 0.04 s).[^ksarmory-weaponsystem]

**`UpdateRounds`** walks rounds back to front:

1. Reap `ShotDown` rounds (killed inside another system's update) and raise `RoundEnded`.
2. Sample gravity, medium density and air velocity at the round's own position (see
   [point gravity and atmosphere](./point-gravity-and-atmosphere-model.md)).
3. For a `Slug`, attach the contact list (every vehicle except the platform, built once per
   system per frame) and the shared [hull test](./hull-triangle-hit-test.md).
4. `RoundDriver.Fly(round, dt, Seek(round, SampleTarget(round), dt), ...)`; the target is sampled
   and run through the seeker (see [seekers](./seekers-uplink-and-laser-homing.md)).
5. `Detonated` calls `Detonate` (blast, damage, effects; see
   [blast damage](./blast-damage-and-part-health.md)); `Expired` announces closest approach,
   distance and final speed. Both drop the round.
6. `ApplyPendingKills()` destroys whatever the bursts ruled dead (see
   [safe destruction](./safe-vehicle-destruction-and-part-failure.md)).

**Reaping a round with no natural end.** A shell that misses is swept up by
`MunitionProfile.MaxFlightSeconds` (default 30 s). A round the ground stops is reaped on
trajectory instead (`RoundReach.Classify`): outside the atmosphere ceiling a Kepler periapsis above
the ceiling is `Impossible` (expire now); below the floor radius it is `Landing` (the ground will end
it, clock held); `Coasting` also holds the clock; only `Arriving` and `Unknown` run
it.[^ksarmory-roundreach][^ksarmory-slug]

**Rounds as contacts.** Before any system updates, `CollectAirborne(step)` snapshots every flying
round in the world (crewed and loose systems) into `RoundContact`s, carried forward by
`VelocityEcl * step` so they sit at the end-of-step phase every KSA vehicle sample has. Without the
single snapshot, a round would read start- or end-of-step depending on which system asked first,
and the roster is a dictionary. Each contact also carries a coasting acceleration (body pull plus
own drag) so a gun can lead it.[^ksarmory-mod][^ksarmory-roundcontact]

**Rounds outlive their launcher.** When the platform dies, `GoLoose` re-anchors every offset onto
the parent body's position sampled at the same instant and the system keeps flying them through
`UpdateLoose` (rounds only, no radar or fire control).[^ksarmory-weaponsystem]

## Lifecycle and timing

| Where | What happens | Decomp |
|---|---|---|
| `Program.PrepareFrame` | Waits on `JobSystems.VehicleSolver` (L2158), `Universe.ApplyVehicleSolvers()` (L2167) | Program.cs[^ksa-program] |
| `Universe.ApplyVehicleSolvers` | `ApplyResultsToMainThread()` L1947, `_lastSimStep = _nextSimStep` L1948, `CurrentSystem.UpdatePerFrameData()` L1950 | Universe.cs[^ksa-universe] |
| `UpdatePerFrameData` | Walks the system parent-first (`CelestialSystem` L274) writing `Celestial._positionEcl` (L591-L608) and `Vehicle._positionEcl/_velocityEcl` (L2645-L2651) | [^ksa-celestialsystem][^ksa-celestial][^ksa-vehicle] |
| `Program.PrepareFrame` | `Universe.ExecuteNextVehicleSolvers(dtPlayer, jobSimStep)` L2220 queues the next step on workers | Program.cs, Universe.cs L2126-L2138 |
| `Program.OnFrame` L2265-L2269 | `if (DrawUI) { OnDrawUiFrame; OnDrawUiViewports }`; `[StarMapAfterGui]` postfixes the latter: **rounds step here** | Program.cs |
| `Program.OnFrame` L2320 | `OnFrameCelestials(dtPlayer)` (defined L2701); `PreRenderHook` prefix steps here when the UI pass did not run | Program.cs |
| After `FrameNumber++` L2332 | `[StarMapAfterOnFrame]`: last-resort step, and always releases the `FrameLatch` | Program.cs |

- `StepOnce` runs once per frame from whichever hook claims `FrameLatch` first. The GUI hook is
  preferred because the step and the draw then share an epoch; with F2 (`Program.DrawUI` false,
  L526) the `OnFrameCelestials` prefix steps before the render; if that patch failed to install, the
  `OnFrame` postfix steps after the render and rounds are drawn one frame late.[^ksarmory-mod][^ksarmory-prerenderhook][^ksarmory-framelatch]
- **What the step consumes.** `KsaWorld.ConsumeSimStep()` reads `Universe.GetLastSimStep()`
  (L2381) and deduplicates on its `NextTime`, so a round integrates exactly the interval the
  engine applied this frame, never `dtPlayer * speed`, and nothing when the step did not
  advance.[^ksarmory-ksaworld]
- **What state is valid.** Every vehicle and celestial sample read in the step is the end of the
  step just applied (`GetElapsedTime() => _lastSimStep.NextTime`, Universe.cs L2399), while the
  round's pre-step position is the start of it. That is why every target and ground sample is
  back-dated by `dt` inside the sub-step loop.[^ksarmory-frames]
- **What is running concurrently.** The next vehicle step was dispatched at Program.cs L2220 and is
  running on worker threads for the whole hook. Rounds touch no vehicle state, so integrating
  them needs no lock; destroying a vehicle does (see
  [safe destruction](./safe-vehicle-destruction-and-part-failure.md)).
- **Drawing is not gated on the clock.** `SyncRoundBodies` runs outside the "did time advance"
  gate on every rendered frame, so bodies follow the platform through frames that advanced no
  simulated time.[^ksarmory-mod]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Sim/IProjectile.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/IProjectile.cs) | The round contract, KSA-free |
| [src/KSArmory/Sim/RoundDriver.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/RoundDriver.cs#L45-L119) | `RoundFields` lookups and `RoundDriver.Fly`, the single advance path |
| [src/KSArmory/Sim/Interceptor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Interceptor.cs#L341-L400) | Guided round: sub-step loop, offset measured after the step |
| [src/KSArmory/Sim/Slug.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Slug.cs#L464-L565) | Unguided round: sub-step loop, optional second-order step, reaper |
| [src/KSArmory/Sim/RoundReach.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/RoundReach.cs) | Trajectory-based reaper for rounds the ground stops |
| [src/KSArmory/Sim/MunitionProfile.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/MunitionProfile.cs#L232-L317) | `MaxFlightSeconds`, `MaxFaithfulStepSeconds`, `PreferredStepSeconds`, `SubStepSeconds` |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L3380-L3500) | `UpdateRounds`: sampling, `Fly`, reaping, detonation |
| [src/KSArmory/Ksa/WeaponSystems.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystems.cs#L95-L153) | `FaithfulStep` (integration clamp) and `WarpTargetStep` (world speed) |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L675-L800) | `StepSimulation`: consume step, clamp, airborne census, fire loop, loose rounds |
| [src/KSArmory/Ksa/RoundContact.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/RoundContact.cs) | A round published as a sensor contact |

## Snippets

```csharp
// src/KSArmory/Sim/RoundDriver.cs L90-L119
    public static void Fly(IProjectile round, double dt, TargetState? target,
                           double3 gravity, double3 frameVelocityEcl, double3 platformEcl,
                           MunitionProfile munition, double mediumDensityRatio,
                           in RoundFields fields)
    {
        // Assigned rather than assigned-if-present: a caller that means "hold this field" says so
        // by passing nothing for it, and that has to be distinguishable from a caller that simply
        // did not reach this line.
        if (round is Slug slug)
        {
            slug.GravityAt = fields.GravityAt;
            slug.AirDensityAt = fields.AirDensityAt;
            slug.Ground = fields.Ground;
            slug.GroundCentreDriftAt = fields.GroundCentreDriftAt;
            slug.GroundQueryDriftAt = fields.GroundQueryDriftAt;
            slug.ApproachAt = fields.ApproachAt;
            slug.AirVelocityAt = fields.AirVelocityAt;
        }
        else if (round is Interceptor missile)
        {
            // The air, and the ground for one that stops on it. An interceptor's flight is dominated
            // by its own guidance rather than by gravity, but the medium decides whether it flies at
            // all: held for the frame, a round low over the sea reads as under it.
            missile.AirDensityAt = fields.AirDensityAt;
            missile.Ground = fields.Ground;
            missile.GroundCentreDriftAt = fields.GroundCentreDriftAt;
        }

        round.Update(dt, target, gravity, frameVelocityEcl, platformEcl, munition, mediumDensityRatio);
    }
```

```csharp
// src/KSArmory/Sim/Interceptor.cs L363-L391
        int steps = Math.Clamp((int)Math.Ceiling(dt / SubStep), 1, MaxSubSteps);
        double h = dt / steps;
        double elapsed = 0.0;

        for (int i = 0; i < steps && State == RoundState.Flying; i++)
        {
            double density = AirDensityAt?.Invoke(PositionEcl, elapsed - dt) ?? mediumDensityRatio;
            if (!double.IsFinite(density) || density < 0.0) density = mediumDensityRatio;

            Step(h, elapsed, dt, target, gravity, frameVelocityEcl, munition, density);
            elapsed += h;
        }

        // Measured AFTER the step, against the platform sampled this same frame, with no
        // extrapolation whatsoever.
        //
        // ...
        OffsetFromPlatform = PositionEcl - platformEcl;
```

```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L1326-L1338
    private void AddAirborne(IReadOnlyList<IProjectile> rounds, string firedBy, string? team,
                             KSA.Vehicle? anchor, Celestial? body, double step)
    {
        for (int i = 0; i < rounds.Count; i++)
        {
            IProjectile r = rounds[i];
            if (r.State != RoundState.Flying) continue;

            double3 at = r.PositionEcl + r.VelocityEcl * step;
            _airborne.Add(new RoundContact(r, firedBy, team, anchor, at, r.VelocityEcl,
                                           CoastingAcceleration(r, body, at)));
        }
    }
```

## Game APIs touched

| API | Decompiled file | Used for |
|---|---|---|
| `Universe.GetLastSimStep()` (L2381), `SimStep.DeltaTime/NextTime` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs) | The interval to integrate, deduplicated on `NextTime` |
| `Universe.GetElapsedTime()` (L2399) | same | Epoch of every world sample (`_lastSimStep.NextTime`) |
| `Astronomical.GetPositionEcl()` / `GetVelocityEcl()` (L366, L380), overridden by `Vehicle` (L954, L960) and `Celestial` (L386, L403) | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs) | Platform anchor, targets, contacts, the loose-round anchor body |
| `Vehicle.MeanRadius` (L535, `BoundingSphereRadiusBody`) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs) | Contact sphere radius before the hull test |
| `Vehicle.UpdatePerFrameData()` (L2645) | same | Where the end-of-step `_positionEcl` the mod reads is written |
| `Program.OnFrameCelestials(double)` (L2701, private) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs) | Harmony prefix target for hidden-UI frames |
| `Program.DrawUI` (L526) | same | Why the GUI hook alone is not enough |

## Pitfalls and version notes

- **Never difference across instants.** A round integrated to the end of the step and a platform
  sampled at the end of the same step cancel the ~29.8 km/s carrier; any other pairing leaks
  `v * dstep` (30 m per ms of frame wobble). The mod measures `OffsetFromPlatform` after the
  step with no extrapolation.[^ksarmory-frames]
- **A carry belongs to a consumer.** `AddAirborne` carries rounds forward by a step because its
  consumers compare against end-of-step vehicle samples. Applying the same carry to the round's
  own gravity/density lookups was flown and cost 2 km of divergence.[^ksarmory-frames]
- **Gate on the applied step, not `Universe.IsPaused()`** (L1799, `_simulationSpeed == 0.0`):
  the frame the speed drops to zero still applies one real step.[^ksa-universe]
- **Doc drift.** `docs/FRAMES-AND-EPOCHS.md` cites `Universe.cs:1681-1683` and `Program.cs:2012/2096`
  from an older build; in 5554 the same code is Universe.cs L1947-L1950 and Program.cs
  L2167/L2268. It also says "the simulation runs in `OnAfterGui`"; the code now runs it from
  whichever of three hooks claims `FrameLatch` first.
- **Time beyond the clamp is lost, not deferred.** A long frame (scene load, a hitch at high warp)
  discards the excess for every round; the warp policy only prevents the next one.
- **Rounds are invisible to the engine.** Nothing KSA does (collisions, the save, the map view) knows
  they exist; terrain is only tested where the profile sets `HitsTerrain` (missiles always ask, see
  [terrain test](./terrain-ground-impact-test.md)), and structures are never collided with.[^ksarmory-readme]

## See also

- [Dual-hook simulation step](./dual-hook-simulation-step.md)
- [Consume the applied sim step](./consume-applied-sim-step.md)
- [Ecliptic frames and epochs](./ecliptic-frames-and-epochs.md)
- [Analytic sub-frame fuses](./analytic-subframe-fuses.md)
- [Rounds as hidden launcher subparts](./rounds-as-hidden-launcher-subparts.md)
- [Round body draw hooks](./round-body-draw-hooks.md)
- [Timewarp policy during engagements](./timewarp-policy-during-engagements.md)
- [Unguided ballistic lead](./unguided-ballistic-lead.md)
- [Chase a round with a RoundFollowable on a level-horizon controller, the pose solved in the viewport pass](../../../howto/camera/main-view/chase-a-fast-body-with-the-main-camera/chase-a-fast-body-with-the-main-camera-1.md) - how-to article demonstrating this feature
- [IProjectile objects with fixed 5 ms sub-steps and a faithful-step clamp](../../../howto/physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-1.md) - how-to article demonstrating this feature
- [Share one driver seam between the game and the headless rig and test with varying steps](../../../howto/testing-and-tooling/offline-tests/run-mod-simulation-in-a-headless-rig/run-mod-simulation-in-a-headless-rig-2.md) - how-to article demonstrating this feature

[^ksarmory-readme]: README.md, How it works
[^ksarmory-iprojectile]: src/KSArmory/Sim/IProjectile.cs
[^ksarmory-interceptor]: src/KSArmory/Sim/Interceptor.cs
[^ksarmory-slug]: src/KSArmory/Sim/Slug.cs
[^ksarmory-rounddriver]: src/KSArmory/Sim/RoundDriver.cs
[^ksarmory-munition]: src/KSArmory/Sim/MunitionProfile.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-weaponsystems]: src/KSArmory/Ksa/WeaponSystems.cs
[^ksarmory-simclock]: src/KSArmory/Sim/SimClock.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-roundreach]: src/KSArmory/Sim/RoundReach.cs
[^ksarmory-roundcontact]: src/KSArmory/Ksa/RoundContact.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksarmory-prerenderhook]: src/KSArmory/Ksa/PreRenderHook.cs
[^ksarmory-framelatch]: src/KSArmory/Sim/FrameLatch.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-frames]: docs/FRAMES-AND-EPOCHS.md (author notes)
