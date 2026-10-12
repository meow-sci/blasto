---
type: How-To
title: Wait on JobSystems.VehicleSolver before mutating vehicles
description: Join the vehicle solver scheduler before constructing kittens or teleporting vehicles from the UI pass, or stage the work like InputEvents.EvaSpawn
category: vehicles-and-parts
group: vehicles
topic: change-vehicles-without-racing-the-solver
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/doh-kitten-spawning, mods/gatos/features/welds-vessel-anchoring]
techniques: [job-barrier, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, JobSystems.VehicleSolver.Wait()]
lifecycle: [frame:draw-ui, frame:after-on-frame, frame:dispatch-solvers, frame:apply-solvers]
game_types:
  - KSA.JobSystems
  - KSA.Vehicle
  - KSA.KittenEva
  - KSA.PhysicsBubble
  - KSA.InputEvents
  - KSA.VehicleUpdateTask
tags: [job-system, barrier, kitten, teleport, welds, spawn]
---
Outcome: a vehicle is constructed, teleported or removed from a UI or frame hook without racing the
worker that is enumerating the same bubble lists. Two mods show it: unscience's `doh` spawns EVA kittens
from button handlers, and gatOS's weld engine teleports a vessel to its anchor every frame. Both call
`JobSystems.VehicleSolver.Wait()`; doh also recovers from a constructor that throws after the vehicle has
registered itself.

Prerequisite: StarMap. Demonstrated by [unscience/doh-kitten-spawning](../../../../mods/unscience/features/doh-kitten-spawning.md) and [gatos/welds-vessel-anchoring](../../../../mods/gatos/features/welds-vessel-anchoring.md).

## Background: how the game does it

- `JobSystems.VehicleSolver` is the orchestrator scheduler. `Universe.ExecuteNextVehicleSolvers` queues
  the `VehicleUpdateTask` on it [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2128-L2139)[^ksa-universe]; `VehicleUpdateTask.Run` fans out over
  `JobSystems.VehicleWorkerPool` inside a using-scoped parallel batch, so waiting the scheduler is
  enough to join the pool as well [current/decomp/KSA/JobSystems.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/JobSystems.cs#L1-L30)[^ksa-jobsystems] [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L261-L298)[^ksa-vehicleupdatetask].
- `PrepareFrame` waits on the same scheduler (L2158) and re-queues it at L2220
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2158-L2220)[^ksa-program]; every StarMap hook therefore runs while the step is in flight.
- Teleporting or disposing a vehicle removes it from its `PhysicsBubble` vehicle-state list. That list is
  read by the worker and, outside the step, exposed by `SyncWindowBubbles`, which throws unless the task
  is idle [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L158-L170).
- A vehicle constructed in the UI pass is not in the step already running; it is first snapshotted by the
  next `ExecuteNextVehicleSolvers` (an orphan, assigned a bubble by `AssignOrphanVehicles`).
- KSA's own EVA button avoids all of this by queueing `InputEvents.EvaSpawnData` on
  `InputEvents.EvaSpawnBuffer` [current/decomp/KSA/EVADoor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EVADoor.cs#L84-L90)[^ksa-evadoor]; `ApplyInputEvents` drains it in the settled window
  [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1113-L1140)[^ksa-inputevents].

## Lifecycle and timing

| Where | Wait needed? | Why |
|---|---|---|
| `StarMapAfterGui` button handler (doh) | yes | `PrepareFrame` already dispatched the next step |
| `StarMapAfterGui` every frame (gatOS welds) | yes, but cheap after the first | the stall is the same one `PrepareFrame` would take next frame, moved earlier |
| `InputEvents.ApplyInputEvents` buffers | no | the worker is idle in that window |
| Harmony prefix on `Universe.ExecuteNextVehicleSolvers` | no | still before the dispatch |

After `Wait()` the finished step has not been applied; its results are applied at the next frame's
`ApplyVehicleSolvers`. A teleport survives that apply because `Vehicle.Teleport` detaches the vehicle from
its bubble, and `PhysicsBubble.ApplyResultsToVehicles` only writes to vehicles still in the bubble. A
plain write to kinematics after the wait is overwritten. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. Put one helper in your mod and call it before every operation that creates, removes or teleports a
   vehicle. doh:

```csharp
// doh.lib/Spawning/KittenSpawner.cs L331-334
    private static void WaitForVehicleSolverIdle()
    {
        JobSystems.VehicleSolver?.Wait();
    }
```

2. Use it immediately before the constructor, then place the kitten with the same tail the stock EVA door
   uses (orbit from state vectors, `Teleport`, `Children.Add`, `UpdatePerFrameData`):

```csharp
// doh.lib/Spawning/KittenSpawner.cs L188-225
            // Calculate this kitten's position offset
            double3 chainOffset = pos.OffsetCci * (i + 1);
            double3 kittenPosCci = pos.BasePositionCci + chainOffset;
            double3 kittenVelCci = pos.VelocityCci;

            // Create KittenEva. The Vehicle ctor mutates the BepuPhysics shapes registry
            // (BepuHandles.Create -> ConstraintSim.UnlockShapes), which throws if the
            // background vehicle physics step is still running. Wait for it to go idle.
            WaitForVehicleSolverIdle();
            KittenEva kittenEva;
            try
            {
                kittenEva = new KittenEva(
                    system,
                    characterId,
                    pos.Body2Cce,
                    pos.BodyRates,
                    pos.Parent!,
                    kittenName,
                    backpackPart,
                    pos.ReferenceOrbit!);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"doh: KittenEva construction failed for '{kittenName}': {ex}");
                RemoveOrphanedKitten(system, kittenName);
                return Stopped(results, request.Count, $"Kitten construction failed: {ex.Message}");
            }

            // Create orbit and teleport
            var orbitColor = pos.ReferenceOrbit?.OrbitLineColor ?? new byte4(255, 200, 0, 255);
            var orbit = Orbit.CreateFromStateCci(
                pos.Parent!, pos.StateTime, kittenPosCci, kittenVelCci, orbitColor);
            kittenEva.Teleport(orbit, null, null);

            // Register with parent body
            pos.Parent!.Children.Add(kittenEva);
            kittenEva.UpdatePerFrameData();
```

3. Recover from a constructor that threw after registering. The `Astronomical` base constructor adds the
   vehicle to `CelestialSystem.All` before `KittenEva` builds its renderable, so a failure leaves a
   half-built vehicle that is updated every frame. doh removes it:

```csharp
// doh.lib/Spawning/KittenSpawner.cs L298-311
    private static void RemoveOrphanedKitten(CelestialSystem system, string kittenName)
    {
        if (!system.All.TryGet(kittenName, out Astronomical? orphan) || orphan == null) return;

        try { system.Deregister(orphan); }
        catch (Exception ex) { Console.WriteLine($"doh: Failed to deregister orphaned '{kittenName}': {ex.Message}"); }

        if (orphan is Vehicle vehicle)
        {
            try { vehicle.Dispose(); }
            catch (Exception ex) { Console.WriteLine($"doh: Failed to dispose orphaned '{kittenName}': {ex.Message}"); }
        }
        Console.WriteLine($"doh: Removed partially constructed kitten '{kittenName}'");
    }
```

4. For a per-frame driver, return early when there is nothing to do, then wait, then act. gatOS:

```csharp
// gatOS.GameMod/Game/Ksa/Welds/WeldManager.cs L106-114
    public void Update(double dt)
    {
        if (_welds.Count == 0)
            return;

        // Cheap once the workers (queued in PrepareFrame) have finished, which they have by this point.
        JobSystems.VehicleSolver.Wait();

        List<WeldEntry>? toRemove = null;
```

5. When you teleport, stamp the new orbit with the time of the step that just finished, not the previous
   tick:

```csharp
// gatOS.GameMod/Game/Ksa/Welds/WeldEngine.cs L99-108
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

6. Prefer the staging route where one exists. If your operation has a stock buffer
   (`CreateVehicleBuffer`, `EvaSpawnBuffer`, `TeleportInputBuffer`, `VehicleDestroyBuffer`), add to it and
   let `ApplyInputEvents` do the work; no wait is needed.

## Complete example

- [doh.lib/Spawning/KittenSpawner.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.cs#L139-L267)[^unscience-kittenspawner] - spawn loop, wait, orphan cleanup
- [gatOS.GameMod/Game/Ksa/Welds/WeldManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Welds/WeldManager.cs)[^gatos-weldmanager] and [gatOS.GameMod/Game/Ksa/Welds/WeldEngine.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Welds/WeldEngine.cs)[^gatos-weldengine] - per-frame teleport driver

## Variations and gotchas

- **`Wait()` blocks the main thread** until the step ends; it only moves the stall `PrepareFrame` would
  take next frame earlier, and nothing re-queues the solver in between.
- **Do not index into `GetAllVehicles()` across frames.** `LookupCollection.Deregister` swap-removes, so a
  stored index can point at a different vehicle after any removal. doh stores the `Vehicle` object.
- **gatOS's comment that workers "have finished" by the after-GUI hook is wrong**; the call is
  necessary on every frame with a weld.
- **Spawned objects are first stepped next frame**, and first drawn the frame after (vehicle lists are
  refreshed in `PrepareFrame`).
- **The wait does not take the shapes lock.** Construction opens the plain `UnlockShapes()`, which
  succeeds here only because nothing on the main thread re-dispatches the solver between your `Wait()` and
  your constructor (the only dispatch is `ExecuteNextVehicleSolvers` in `PrepareFrame`). If you cannot
  guarantee that, use the lock from article 1 as well.

## See also

- [Overview](overview.md), [Article 1](change-vehicles-without-racing-the-solver-1.md), [Article 3](change-vehicles-without-racing-the-solver-3.md)
- [unscience/doh-kitten-spawning](../../../../mods/unscience/features/doh-kitten-spawning.md), [gatos/welds-vessel-anchoring](../../../../mods/gatos/features/welds-vessel-anchoring.md)
- `spawn-an-eva-kitten-from-code`, [Spawn a vehicle from code (from the vehicle library or a hand-built part)](../spawn-a-vehicle-at-runtime/overview.md)

[^ksa-jobsystems]: current/decomp/KSA/JobSystems.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatetask]: current/decomp/KSA/VehicleUpdateTask.cs (decompiled, build 2026.10.10.5554)
[^ksa-universe]: current/decomp/KSA/Universe.cs (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: current/decomp/KSA/InputEvents.cs (decompiled, build 2026.10.10.5554)
[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
[^unscience-kittenspawner]: unscience: doh.lib/Spawning/KittenSpawner.cs
[^ksa-evadoor]: current/decomp/KSA/EVADoor.cs (decompiled, build 2026.10.10.5554)
[^gatos-weldengine]: gatOS: gatOS.GameMod/Game/Ksa/Welds/WeldEngine.cs
[^gatos-weldmanager]: gatOS: gatOS.GameMod/Game/Ksa/Welds/WeldManager.cs
