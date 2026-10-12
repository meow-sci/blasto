---
type: How-To
title: Hold the shapes lock around a vehicle build
description: Wrap vehicle creation and part-tree edits in ConstraintSim.UnlockShapesBlocking so a build from a frame hook waits for the vehicle step instead of throwing
category: vehicles-and-parts
group: vehicles
topic: change-vehicles-without-racing-the-solver
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/spawn-parked-craft, mods/ksarmory/features/spawn-and-teleport-test-craft]
techniques: [shapes-registry-lock, starmap-lifecycle]
hooks: [starmap StarMapAfterOnFrame, using ConstraintSim.UnlockShapesBlocking()]
lifecycle: [frame:after-on-frame, frame:draw-ui, physics:vehicle-solver]
game_types: [KSA.ConstraintSim, KSA.ShapesUnlock, KSA.Vehicle, KSA.VehicleUpdateTask]
tags: [shapes-lock, bepu, spawn, threading]
---
Outcome: any code that builds a `Vehicle`, or edits a part tree that is attached to one, runs without
`InvalidOperationException: The shapes registry cannot be mutated while the vehicle update is stepping`.
KSACars and KSArmory both use this for spawning; the spawn recipes themselves are in
[Spawn a vehicle from code (from the vehicle library or a hand-built part)](../spawn-a-vehicle-at-runtime/overview.md). This article is the lock mechanism.

Prerequisite: StarMap hooks. Demonstrated by [ksacars/spawn-parked-craft](../../../../mods/ksacars/features/spawn-parked-craft.md) and
[ksarmory/spawn-and-teleport-test-craft](../../../../mods/ksarmory/features/spawn-and-teleport-test-craft.md).

## Background: how the game does it

The Bepu shape registry is process-wide static state in `ConstraintSim` (`GlobalShapes`), guarded by a
single integer, `_shapesState`: 0 free, -1 held by the vehicle step, otherwise the id of the main-thread
owner. The vehicle task takes it for the whole step:

```csharp
// current/decomp/KSA/VehicleUpdateTask.cs L261-266, L294-297
	public void Run()
	{
		ConstraintSim.BeginVehicleUpdate();
		try
		{
			RunnerProfiler?.BeginJob(Profiler.Tag.VehicleUpdate);
// ...
		finally
		{
			ConstraintSim.EndVehicleUpdate();
			_isRunning = false;
```

The three public ways in:

```csharp
// current/decomp/KSA/ConstraintSim.cs L111-153
	public static void BeginVehicleUpdate()
	{
		if (Interlocked.CompareExchange(ref _shapesState, -1, 0) != 0)
		{
			throw new InvalidOperationException("Cannot start the vehicle update while the shapes registry is unlocked for mutation");
		}
	}

	public static void EndVehicleUpdate()
	{
		Volatile.Write(ref _shapesState, 0);
	}

	public static ShapesUnlock UnlockShapes()
	{
		int currentManagedThreadId = Environment.CurrentManagedThreadId;
		if (Volatile.Read(in _shapesState) != currentManagedThreadId && Interlocked.CompareExchange(ref _shapesState, currentManagedThreadId, 0) != 0)
		{
			throw new InvalidOperationException((Volatile.Read(in _shapesState) == -1) ? "The shapes registry cannot be mutated while the vehicle update is stepping; stage the change and apply it at the frame sync point" : "The shapes registry is already unlocked by another thread; shapes mutation is single-threaded");
		}
		_shapesUnlockDepth++;
		return default(ShapesUnlock);
	}

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

Building a `Vehicle` reaches the plain `UnlockShapes()` through
`UpdateAfterPartTreeModification` -> `UpdateCollisionGeometry` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1894-L1950)[^ksa-vehicle], and disposing one reaches it through `BepuHandles`, so
neither works while the step is stepping. Nested `UnlockShapes` calls on the owning thread are
re-entrant (`_shapesUnlockDepth`), which is why a `using` around the whole build is enough.

## Lifecycle and timing

| Hook | Worker state | What `UnlockShapesBlocking` does |
|---|---|---|
| `StarMapAfterOnFrame`, `StarMapAfterGui`, any ImGui button | the step dispatched by this frame's `PrepareFrame` is running or finished | spins (`SpinWait`) until `EndVehicleUpdate`, then takes the lock |
| `InputEvents.ApplyInputEvents` (stock spawn path) | idle (just applied) | takes the lock immediately |
| A worker-thread patch | stepping | do not call it; it would wait for itself |

The wait is bounded by one worker step and costs a main-thread stall on that frame only. Release the
lock before the frame ends: `ConstraintSim.BeginVehicleUpdate` throws "Cannot start the vehicle update
while the shapes registry is unlocked for mutation" if you still hold it when
`ExecuteNextVehicleSolvers` runs. The `using` guarantees that when you do the work in one hook call. See
[game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) for the surrounding frame order.

## Steps

1. Take the lock with `using` immediately before the first operation that touches shapes. KSACars:

```csharp
// src/KSACars/Ksa/CraftSpawner.cs L35-42
            using ShapesUnlock shapes = ConstraintSim.UnlockShapesBlocking();
            Vehicle craft = Vehicle.CreateVehicle(system, platform.Body2Cce, bodyRates: default,
                                                  parent, id, parts.Root, orbit);
            parent.Children.Add(craft);
            craft.Parts.RecomputeAllDerivedData();
            craft.UpdateAfterPartTreeModification();
            craft.UpdatePerFrameData();
            craft.TeleportToLocation(body, latitudeDeg, longitudeDeg);
```

2. Keep everything that mutates the world inside the block: `CreateVehicle`, `Children.Add`,
   `RecomputeAllDerivedData`, `UpdateAfterPartTreeModification`, `UpdatePerFrameData`. Do work that does
   not need the lock (library lookups, orbit maths) before the `using`, as the mod does.
3. KSArmory takes the lock earlier, before assembling the part tree, because `PartTree.DeepCopy`
   and part creation also touch collider shapes. Its comment records the failure it avoids:

```csharp
// src/KSArmory/Ksa/TestTarget.cs L166-170
            // Held from the parts until the drone is placed. Building a vehicle mutates the shapes
            // registry, the vehicle worker holds that for its whole run, and a hook of this mod can
            // land inside the run. Racing it builds half a vehicle, which the game does not survive;
            // this waits the run out instead, at the cost of one worker step on a button press.
            using ShapesUnlock shapes = ConstraintSim.UnlockShapesBlocking();
```

4. Wrap the whole in `try/catch` and log: an exception thrown while the lock is held still releases it
   (`Dispose` -> `RelockShapes`), but must not escape into the frame.
5. Verify in the log that no `The shapes registry cannot be mutated` exception appears and that the
   follow-up line (for example `parked '<id>' ...`) does.

## Complete example

- [src/KSACars/Ksa/CraftSpawner.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CraftSpawner.cs#L17-L52)[^ksacars-craftspawner]
- [src/KSArmory/Ksa/TestTarget.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TestTarget.cs#L156-L182)[^ksarmory-testtarget]

## Variations and gotchas

- **`UnlockShapes()` is not the same.** It throws instead of waiting; use it only in code that runs in the
  settled window (`ApplyInputEvents`) or that already knows the step is idle.
- **Another thread owns it.** `UnlockShapesBlocking` still throws "already unlocked by another thread" if a
  background thread of yours holds the lock. Take it on the main thread only.
- **Dispose on the opening thread.** `RelockShapes` throws if a `ShapesUnlock` is disposed on a different
  thread, so never carry the `using` into an `async` continuation.
- **The lock does not protect against other races.** It makes the shape registry safe; the worker may
  still be enumerating bubble vehicle lists (see article 2) or copying properties (articles 5-7).
- **Cost.** Each use blocks until the current worker step ends; batch several builds into one lock
  (KSAStructures builds one piece of furniture per pass for this reason, see
  [Spawn a vehicle from code (from the vehicle library or a hand-built part)](../spawn-a-vehicle-at-runtime/overview.md)).

## See also

- [Overview](overview.md), [Article 2: wait on the solver](change-vehicles-without-racing-the-solver-2.md), [Article 3: safe destruction](change-vehicles-without-racing-the-solver-3.md)
- [ksacars/spawn-parked-craft](../../../../mods/ksacars/features/spawn-parked-craft.md), [ksarmory/spawn-and-teleport-test-craft](../../../../mods/ksarmory/features/spawn-and-teleport-test-craft.md)
- [Spawn a vehicle from code (from the vehicle library or a hand-built part)](../spawn-a-vehicle-at-runtime/overview.md)

[^ksacars-craftspawner]: KSACars: src/KSACars/Ksa/CraftSpawner.cs
[^ksarmory-testtarget]: KSArmory: src/KSArmory/Ksa/TestTarget.cs
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
