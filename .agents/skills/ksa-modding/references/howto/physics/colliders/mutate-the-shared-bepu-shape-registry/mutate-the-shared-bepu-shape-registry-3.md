---
type: How-To
title: UnlockShapesBlocking around vehicle construction
description: Build a vehicle from a frame hook by taking the shape registry with UnlockShapesBlocking, which spins until the in-flight step releases it, and attach the new craft to its parent so it is visited each frame
category: physics
group: colliders
topic: mutate-the-shared-bepu-shape-registry
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/spawn-parked-craft]
techniques: [reflection, solver-wait]
hooks: [starmap StarMapAfterOnFrame]
lifecycle: [frame:after-on-frame, frame:prepare]
game_types: [KSA.ConstraintSim, KSA.ShapesUnlock, KSA.Vehicle, KSA.Orbit, KSA.Celestial]
tags: [physics, bepu, shapes, registry, blocking, vehicle-spawn, orbit]
---

Outcome: your mod can build a new `Vehicle` (whose compound collider needs the shape registry) from a StarMap
frame hook without throwing "the shapes registry cannot be mutated while the vehicle update is stepping". KSACars
parks a car on the ground this way in response to a command. Prerequisites: StarMap. The cost is a one-off
stall of the main thread until the in-flight vehicle step finishes.

## Background: how the game does it

`Vehicle.CreateVehicle` builds the collision compound, and `UpdateAfterPartTreeModification` reaches
`UpdateCollisionGeometry`, which takes the plain `ConstraintSim.UnlockShapes()`.[^ksa-constraintsim] A
StarMap frame hook runs after `ExecuteNextVehicleSolvers` has queued the step, so that plain call throws.
`UnlockShapesBlocking()` instead spins until `EndVehicleUpdate()`, then takes the registry for the calling thread;
the nested `UnlockShapes()` inside the vehicle build is re-entrant for the same thread:

```csharp
// current/decomp/KSA/ConstraintSim.cs L135-L153
	public static ShapesUnlock UnlockShapesBlocking()
	{
		int currentManagedThreadId = Environment.CurrentManagedThreadId;
		SpinWait spinWait = default(SpinWait);
		while (true)
		{
			if (Volatile.Read(in _shapesState) == currentManagedThreadId || Interlocked.CompareExchange(ref _shapesState, currentManagedThreadId, 0) == 0)
			{
				_shapesUnlockDepth++;
				return default(ShapesUnlock);
			}
			if (Volatile.Read(in _shapesState) != -1)
			{
				break;
			}
			spinWait.SpinOnce();
		}
		throw new InvalidOperationException("The shapes registry is already unlocked by another thread; shapes mutation is single-threaded");
	}
```

KSA's own vehicle creation (`InputEvents.CreateVehicleData`) instead runs in `ApplyInputEvents`, in the settled
window after `ApplyVehicleSolvers`, where the registry is free and nothing has to wait.[^ksa-inputevents]

## Lifecycle and timing

| Step | Where | Physics |
|---|---|---|
| Spawn command handled | `StarMapAfterOnFrame` (`frame:after-on-frame`), main thread, after `PostRender` | vehicle step in flight |
| Shape lock taken | `UnlockShapesBlocking` spins until `VehicleUpdateTask.Run` calls `EndVehicleUpdate` | waits for the step to finish |
| Craft built, attached, per-frame data filled | same call | step finished but not yet applied |
| Teleport applied | next frame's `PrepareFrame` -> `InputEvents.ApplyInputEvents` | settled |
| First physics step | next frame's `ExecuteNextVehicleSolvers` | dispatched |

Because `StarMapAfterOnFrame` runs while the step is in flight, a spawn there stalls the main thread until it
completes. KSACars spawns once per command, so this is a single hitch. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and
[mutating vehicles at a safe point](../../solver/mutate-vehicles-at-a-physics-safe-point/overview.md).

## Steps

1. Give the craft an orbit to exist on (the `Vehicle` constructor throws without an orbit or situation). Take the
   flown craft's ecliptic position 500 m along its local up and its ecliptic velocity, convert to the parent's
   inertial frame and call `Orbit.CreateFromStateCci(parent, Universe.GetElapsedTime(), posCci, velCci, colour)`.
2. Take the registry blocking, build the vehicle inside the lock, attach it and finish its per-frame data:

```csharp
// src/KSACars/Ksa/CraftSpawner.cs L32-L45
            Orbit orbit = Orbit.CreateFromStateCci(parent, Universe.GetElapsedTime(), posCci, velCci,
                                                   new byte4(255, 80, 80, 255));

            using ShapesUnlock shapes = ConstraintSim.UnlockShapesBlocking();
            Vehicle craft = Vehicle.CreateVehicle(system, platform.Body2Cce, bodyRates: default,
                                                  parent, id, parts.Root, orbit);
            parent.Children.Add(craft);
            craft.Parts.RecomputeAllDerivedData();
            craft.UpdateAfterPartTreeModification();
            craft.UpdatePerFrameData();
            craft.TeleportToLocation(body, latitudeDeg, longitudeDeg);

            Log.Info($"parked '{id}' ({craftName}) at {latitudeDeg:F4}, {longitudeDeg:F4} on {body.Id}");
            return craft;
```

   `parent.Children.Add(craft)` is essential: `CelestialSystem.UpdatePerFrameData` walks the orbiter tree from each
   non-orbiting parent, so a vehicle that is not in `Children` is never visited and its per-frame position stays
   unset. KSA's own spawn path does the same two calls.
3. Set it down with `Vehicle.TeleportToLocation(body, lat, lon)`, which queues a `TeleportInputData` on
   `InputEvents.TeleportInputBuffer` for the next frame's `ApplyInputEvents`. The teleport's start state is computed at
   `Universe.GetNextSimStep().NextTime`.
4. Dispose the lock by leaving the `using` scope; the shapes version counter is bumped when the depth returns to zero.
5. Do not take the lock from a worker thread, or hold it across a frame: `UnlockShapesBlocking` throws if another
   thread holds it, and a held lock makes the next `BeginVehicleUpdate` throw.

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/CraftSpawner.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CraftSpawner.cs#L12-L60) | `SpawnParked`: orbit, lock, build, attach, teleport |
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L358-L389) | `TryPlaceOnSurface` via `TeleportToLocation` |

## Variations and gotchas

- If you can wait a frame, queue an `InputEvents.CreateVehicleData`-style request and build in the settled window
  instead; no stall.
- A prefix on `ExecuteNextVehicleSolvers` is the other registry-free window ([approach 1](mutate-the-shared-bepu-shape-registry-1.md)).
- Building a craft while a step is in flight also races the step's reads of the vehicle list unless you wait; the
  blocking unlock covers the shapes, so verify nothing else you touch is read by workers.
- Spawn from `StarMapAfterOnFrame`, not from `StarMapAfterGui`, only if you accept the hitch; neither avoids it.

## See also

- [Overview and comparison](overview.md)
- [Create shapes in a prefix](mutate-the-shared-bepu-shape-registry-1.md), [wait on solvers](mutate-the-shared-bepu-shape-registry-2.md)
- Mod feature: [KSACars spawn parked craft](../../../../mods/ksacars/features/spawn-parked-craft.md)
- [Place a vessel on a surface at a latitude and longitude](../../kinematics/place-a-vessel-on-a-surface-at-latitude-and-longitude/overview.md)
- The topic spawn-a-vehicle-at-runtime (other batch) covers vehicle spawning in full

[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
