---
type: Mod Feature
title: Welds that pin one vessel to another
description: Rigidly attach a source vessel to a target vessel or one of its parts by re-teleporting the source onto the anchor pose every frame after the vehicle solver has finished
mod: gatos
category: physics
techniques: [starmap-lifecycle, custom-physics, kinematic-teleport]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame]
game_types: [KSA.Vehicle, KSA.Orbit, KSA.Part, KSA.Universe, KSA.SimStep, KSA.JobSystems, KSA.IParentBody]
tags: [welds, teleport, orbit, kinematics, cheats, vehicle-solver]
---

`/sim/debug/welds` (ported from the unscience mod's garrys-torch library) welds one vessel onto
another: the source keeps an exact offset and, optionally, orientation relative to the target's
centre of mass or a chosen part, including chains of welds. KSA has no joint between separate
vehicles, so gatOS does it kinematically: every frame it computes where the source should be and
teleports it there with the target's velocity.

## How it works

`WeldManager` holds the registry of `WeldEntry` objects (source, target, optional target part by
`InstanceId`, position offset, orientation, lock-rotation flag). `Update` does
([gatOS.GameMod/Game/Ksa/Welds/WeldManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Welds/WeldManager.cs#L106-L144)):

1. Return immediately when there are no welds (zero cost when unused).
2. `JobSystems.VehicleSolver.Wait()` so no worker is touching vehicle state.
3. Drop welds whose source or target vanished; re-resolve the target part by `InstanceId` each
   tick (robust to staging; a removed part falls back to the vehicle body frame).
4. Process welds in topological order so a target is updated before anything welded to it
   (`WeldEngine.TopologicalSort`, input order on a cycle).

`WeldEngine.UpdateWeld` ([gatOS.GameMod/Game/Ksa/Welds/WeldEngine.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Welds/WeldEngine.cs#L39-L109)):

- Drops the weld if source and target have different parent bodies.
- Anchor pose: target CCI position plus, when a part is set, the part offset
  `part.PositionVehicleAsmb - target.CenterOfMassAsmb` rotated by `GetBody2Cci()`, and orientation
  `Concatenate(part.Asmb2VehicleAsmb, tgtBody2Cci)`.
- New source state: anchor position plus the stored offset in the anchor frame, target velocity;
  orientation either locked to the anchor (converted to CCE with `Parent.GetCci2Cce()`) or the
  source's own.
- Builds a conic with
  `Orbit.CreateFromStateCci(parent, Universe.GetJobSimStep(Program.GetPlayerDeltaTime()).NextTime, pos, vel, color)`
  and calls `source.Teleport(orbit, body2Cce, bodyRates)` then `UpdatePerFrameData()`.

`weld_here` captures the current relative pose with the inverse math (`CapturePose`) so a vessel can
be welded where it already sits.

## Lifecycle and timing

- Driven from `Mod.DriveWelds`, called by `DrivePostSolver` in `[StarMapAfterGui]` (or from
  `[StarMapAfterOnFrame]` when the HUD is hidden) ([gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L487-L500)).
- At `frame:draw-ui` nothing has joined the vehicle step dispatched in this frame's `PrepareFrame`,
  so it may still be running (gatOS's comment that the workers "have finished" by this hook is wrong).
  `JobSystems.VehicleSolver.Wait()` blocks the main thread until it completes; that stall happens
  only while at least one weld exists.
- `Vehicle.Teleport` detaches the source through `RemoveFromCurrentBubble()` ->
  `PhysicsBubble.RemoveVehicle`, which drops its `VehicleUpdateState` from the bubble's
  `_vehicleStates` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2312-L2330), [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L242-L251)). The worker iterates that
  list while it runs, which is why the wait is required; the engine itself only exposes the bubble
  list while the `VehicleUpdateTask` is idle (`SyncWindowBubbles` throws otherwise,
  [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L158-L167)).
- The detach is also why the weld survives the next frame's `ApplyVehicleSolvers`: the finished
  step's results are written back per bubble by `PhysicsBubble.ApplyResultsToVehicles`, which walks
  `_vehicleStates` ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L881-L909)), so the source's stale result is never
  applied over the teleport. A plain kinematic write made after the same wait would be overwritten.
  The detached source is re-bubbled as an orphan at the next dispatch (`Universe.PrepareVehicleWorkers`).
- The anchor is read from the target's main-thread state, which is the step applied at the start of
  this frame. The orbit is stamped with `Universe.GetJobSimStep(...).NextTime`, which here equals the
  end time of the step that was in flight (same `dtPlayer`, same `_achievedSpeedFraction`), rather
  than `Universe.GetElapsedTime()`; the gatOS notes say the worker otherwise logs a body/origin time
  mismatch.
- No Harmony patch is needed; the driver self-gates to one branch when the registry is empty.

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Welds/WeldManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Welds/WeldManager.cs) | registry, solver wait, per-frame driver |
| [gatOS.GameMod/Game/Ksa/Welds/WeldEngine.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Welds/WeldEngine.cs) | anchor math, teleport, capture, topological sort |
| [gatOS.GameMod/Game/Ksa/Welds/WeldEntry.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Welds/WeldEntry.cs) | weld record |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L487-L500) | `DriveWelds` with fault latch |
| [garrys-torch.lib/WeldEngine.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldEngine.cs) | the unscience original the math was ported from |

## Snippets

```csharp
// gatOS.GameMod/Game/Ksa/Welds/WeldEngine.cs L78-L108
        double3 newSrcPosCci = anchorPosCci + double3.Transform(entry.Position, anchorBody2Cci);
        double3 newSrcVelCci = tgtVelCci;

        doubleQuat cci2Cce = source.Parent.GetCci2Cce();
        doubleQuat newSrcBody2Cce;
        double3 newBodyRates;
        if (entry.LockRotation)
        {
            doubleQuat newSrcBody2Cci = doubleQuat.Concatenate(entry.Orientation, anchorBody2Cci);
            newSrcBody2Cce = doubleQuat.NormalizeOrZero(doubleQuat.Concatenate(newSrcBody2Cci, cci2Cce));
            newBodyRates = target.BodyRates;
        }
        else
        {
            doubleQuat srcBody2Cci = doubleQuat.NormalizeOrZero(source.GetBody2Cci());
            newSrcBody2Cce = doubleQuat.NormalizeOrZero(doubleQuat.Concatenate(srcBody2Cci, cci2Cce));
            newBodyRates = source.BodyRates;
            if (!IsFinite(newBodyRates))
                newBodyRates = new double3(0, 0, 0);
        }

        // Stamp the orbit with the time the just-completed worker tick advanced to, NOT
        // GetElapsedTime() (the PREVIOUS tick's end). Teleport sets body.Time = orbit StateTime;
        // using the next-tick time keeps the source aligned with the queued solver origin (see the
        // unscience notes) — otherwise the worker logs a "SnapToLeader body/origin time" mismatch.
        UniverseTime tickEndTime = Universe.GetJobSimStep(Program.GetPlayerDeltaTime()).NextTime;
        Orbit newOrbit = Orbit.CreateFromStateCci(
            source.Parent, tickEndTime, newSrcPosCci, newSrcVelCci, source.Orbit.OrbitLineColor);
        source.Teleport(newOrbit, newSrcBody2Cce, newBodyRates);
        source.UpdatePerFrameData();
        return true;
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `JobSystems.VehicleSolver.Wait()` | [current/decomp/KSA/JobSystems.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/JobSystems.cs) | block until the in-flight step finishes so the bubble list can be changed |
| `Universe.GetJobSimStep(double).NextTime` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2615-L2615) | orbit epoch for the teleport |
| `Orbit.CreateFromStateCci` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1611-L1611) | conic from state vector |
| `Vehicle.Teleport`, `UpdatePerFrameData` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2241-L2270) | apply pose and velocity |
| `PhysicsBubble.RemoveVehicle`, `ApplyResultsToVehicles` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L242-L251) | detach on teleport, so the pending step result skips the source |
| `Part.PositionVehicleAsmb`, `Asmb2VehicleAsmb` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L708-L740) | part anchor pose |
| `IParentBody.GetCci2Cce()` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L51-L51) | CCI to CCE for `Body2Cce` |

## Pitfalls and version notes

- Each tick's teleport detaches the source from its physics bubble; since 5348 bubble trimming,
  merging and splitting happen on the solver worker (revs 5331/5339 per gatOS notes), so welds
  drive a bubble split/merge every frame. gatOS lists live confirmation as pending in its validation
  record.
- Welds work only within one SOI (same parent body).
- Teleporting into a NaN target state is guarded: the weld skips the tick instead of propagating NaN.
- `Vehicle.Teleport` replaces the source's flight plan. Neither the weld driver nor gatOS's
  solver-phase teleport waits on `JobSystems.NearestOrbitAndPerformanceWorker`, whose
  `NearestOrbitPointJob` (queued in `PrepareFrame`, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2196-L2201)) reads the
  controlled vehicle's flight plan in `CelestialSystem.SetNearestOrbitPoint`
  ([current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L473-L485)). Welding the controlled vessel can therefore race that job
  (not observed in the repo; inferred from the decomp).
- The validation checklist for welds is still marked not run in-game in the repo.

## See also

- [Debug cheats for teleport, refuel, warp and impulse](debug-cheats-teleport-refuel-warp.md)
- [IVA cabin physics in an own Bepu world](iva-cabin-physics-bepu.md)
- [Frame hooks with an F2-proof fallback](frame-hooks-and-f2-fallback.md)
- [Part and module state reads](part-module-state-reads.md)
- [gatOS overview](../overview.md)
- [VehicleSolver.Wait then teleport stamped with the next sim step](../../../howto/physics/kinematics/weld-one-vessel-to-another/weld-one-vessel-to-another-2.md) - how-to article demonstrating this feature
- [Join JobSystems.VehicleSolver before mutating from a frame hook](../../../howto/physics/solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-4.md) - how-to article demonstrating this feature
- [Wait on JobSystems.VehicleSolver before mutating vehicles](../../../howto/vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/change-vehicles-without-racing-the-solver-2.md) - how-to article demonstrating this feature

