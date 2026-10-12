---
type: Game Feature
title: Create and free shapes in KSA's global Bepu shape registry
description: "Why ConstraintSim.UnlockShapes throws while the vehicle step runs, and the three ways mods create, cache and free Bepu shapes: at the dispatch window, after waiting on the solver, or by blocking until the step ends"
category: physics
group: colliders
topic: mutate-the-shared-bepu-shape-registry
demonstrated_by:
  - mods/ksagolf/features/course-colliders-as-clutter
  - mods/ksagolf/features/launch-site-surface-raycast
  - mods/unscience/features/sphinx-static-colliders
  - mods/unscience/features/pebbles-clutter-colliders
  - mods/ksacars/features/spawn-parked-craft
lifecycle:
  - frame:dispatch-solvers
  - frame:apply-solvers
  - physics:bepu
  - frame:draw-ui
  - frame:after-on-frame
  - frame:prepare
game_types: [KSA.ConstraintSim, KSA.Universe, KSA.ShapesUnlock, BepuPhysics.Collidables.Shapes]
tags: [physics, bepu, shapes, registry, unlock, sync-point, colliders]
---

Every Bepu simulation KSA runs (one per physics bubble) shares one `Shapes` registry. A collider shape lives in it as
a `TypedIndex`, and the registry is read by every bubble's worker thread during the vehicle step, so KSA does not
allow it to change while that step runs. A mod that builds a `Box`, a `Mesh` or a `ConvexHull`, that spawns a
vehicle (which builds its compound collider), or that frees a shape has to do it at a moment when no step is
stepping. This topic covers that rule and the three timings mods use.

## Engine background

`ConstraintSim` holds a `_shapesState` integer: 0 when free, `-1` while a vehicle update is stepping, otherwise the
managed thread id that unlocked it. The entry points are:

```csharp
// current/decomp/KSA/ConstraintSim.cs L111-L133
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
```

```csharp
// current/decomp/KSA/ConstraintSim.cs L135-L166
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

	internal static void RelockShapes()
	{
		if (Volatile.Read(in _shapesState) != Environment.CurrentManagedThreadId)
		{
			throw new InvalidOperationException("A ShapesUnlock must be disposed on the thread that opened it");
		}
		if (--_shapesUnlockDepth == 0)
		{
			Interlocked.Increment(ref _shapesVersion);
			Volatile.Write(ref _shapesState, 0);
		}
	}
```

`VehicleUpdateTask.Run` brackets the whole step with `ConstraintSim.BeginVehicleUpdate()` (line 263) and
`EndVehicleUpdate()` in a `finally` (line 296).[^ksa-vehicleupdatetask] `ShapesUnlock` is a disposable struct
exposing `Shapes` and `BufferPool` while the registry is yours; disposing it relocks.[^ksa-shapesunlock]
`UnlockShapes()` throws "The shapes registry cannot be mutated while the vehicle update is stepping; stage the change
and apply it at the frame sync point" if a step holds it, and "already unlocked by another thread" if another
thread has it. It is re-entrant on one thread (a depth counter), so nested engine calls inside your lock are fine.
`UnlockShapesBlocking()` spins until the step releases the registry.

Where those moments are in the frame (see the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)):

| Window | What is true | How a mod gets there |
|---|---|---|
| After `Universe.ApplyVehicleSolvers`, before `Universe.ExecuteNextVehicleSolvers` queues the step | The last step is finished and applied; the next is not queued; the registry is free | A Harmony prefix on `ExecuteNextVehicleSolvers` (main thread, `frame:dispatch-solvers`) |
| Any time after calling `JobSystems.VehicleSolver.Wait()` and `ClothSolvers.Wait()` | The in-flight step has finished (do not apply its results on top of a changed craft) | Wait from a frame hook, then use `UnlockShapes()` |
| Any time, blocking | The call spins until `EndVehicleUpdate` | `UnlockShapesBlocking()` from a frame hook; stalls the main thread for the rest of the step |

KSA itself does registry work in the first window: the body of `ExecuteNextVehicleSolvers` runs `SyncGroundClutter`,
which creates terrain-block shapes under `UnlockShapes()`, and vehicles are created in `ApplyInputEvents`, in the settled window.

## Approaches

| Article | Mod | Technique | Trade-off |
|---|---|---|---|
| [1: Create shapes in an `ExecuteNextVehicleSolvers` prefix and cache by size](mutate-the-shared-bepu-shape-registry-1.md) | KSAGolf | main-thread-sync-point | Never stalls anyone and never races; changes take effect one frame later, and the copy-out pattern lets you read shapes too |
| [2: Add and free statics' shapes only while the solver is idle](mutate-the-shared-bepu-shape-registry-2.md) | unscience (Sphinx, Pebbles) | bepu-physics | Works from UI-driven edits; the main thread waits on both solver pools before touching shapes, and retirement frees shapes |
| [3: `UnlockShapesBlocking` around vehicle construction](mutate-the-shared-bepu-shape-registry-3.md) | KSACars | reflection | One-line change that lets a frame hook build a vehicle; it stalls the main thread until the step ends |

## See also

- [Add mod-owned static colliders that vessels and kittens collide with](../add-static-colliders-to-physics-bubbles/overview.md), the main consumer of shapes
- [Mutate vehicles at a physics-safe point](../../solver/mutate-vehicles-at-a-physics-safe-point/overview.md), the same windows for vehicle state
- [Use ground clutter as colliders](../give-ground-clutter-custom-colliders/overview.md)

[^ksa-vehicleupdatetask]: KSA.VehicleUpdateTask (decompiled, build 2026.10.10.5554)
[^ksa-shapesunlock]: KSA.ShapesUnlock (decompiled, build 2026.10.10.5554)
