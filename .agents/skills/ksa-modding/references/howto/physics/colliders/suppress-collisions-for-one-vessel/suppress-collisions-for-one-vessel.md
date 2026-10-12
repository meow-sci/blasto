---
type: How-To
title: BodyReference.SetShape(default) in a prefix with a restoring finalizer
description: Remove each suppressed source vessel's collidable from the Bepu broad phase for one constraint-sim pass with SetShape(default), and put the captured TypedIndex back in a Harmony finalizer even when the pass throws
category: physics
group: colliders
topic: suppress-collisions-for-one-vessel
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/garrys-torch-scoped-collision-suppression]
techniques: [harmony-finalizer, harmony-prefix, bepu-physics]
hooks:
  - harmony-prefix KSA.ConstraintSim.DetectCollisions
  - harmony-prefix KSA.ConstraintSim.Simulate
  - harmony-finalizer KSA.ConstraintSim.DetectCollisions
  - harmony-finalizer KSA.ConstraintSim.Simulate
lifecycle: [frame:prepare, physics:vehicle-solver, physics:bepu]
game_types:
  - KSA.ConstraintSim
  - KSA.VehicleUpdateState
  - BepuPhysics.BodyReference
  - BepuPhysics.Collidables.TypedIndex
tags: [physics, bepu, collisions, suppression, welds, harmony, finalizer, volatile-set]
---

Outcome: chosen vessels keep simulating (modules, actuators, lights, integration) but do not touch other vessels,
terrain, static objects or ground clutter, and nobody else's collisions change. unscience's Garry's Torch makes welded
sources collision-free by default this way, with a per-weld "Collisions" checkbox to opt back in. Prerequisites:
StarMap and Harmony. The decision of *which* vessels is published from the main thread; the removal is done on the
worker, per pass.

## Background: how the game does it

Both entry points bracket all rigid-body collision work for one simulation, and both call Bepu synchronously:

```csharp
// current/decomp/KSA/ConstraintSim.cs L870-L902
	public void DetectCollisions(double dt)
	{
		_contactPassDt = (float)dt;
		BeginContactPass();
		try
		{
			Simulation.PredictBoundingBoxes((float)dt, _workerDispatcher);
			Simulation.CollisionDetection((float)dt, _workerDispatcher);
		}
		catch
		{
			_isPoisoned = true;
			throw;
		}
		UpdateIsAnyConstrained();
	}

	public void Simulate(double intraStepTime, ref readonly SimStep simStep)
	{
		IntraStepTime = intraStepTime;
		SimStep = simStep;
		_contactPassDt = (float)simStep.DeltaTime;
		BeginContactPass();
		try
		{
			Simulation.Timestep((float)simStep.DeltaTime, _workerDispatcher);
		}
		catch
		{
			_isPoisoned = true;
			throw;
		}
		_clutterStatics?.ApplyPendingHits(this);
```

Bepu's `Bodies.SetShape` decides broad-phase membership from whether the shape exists:

```csharp
// current/decomp/BepuPhysics/Bodies.cs L349-L379
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void UpdateForShapeChange(BodyHandle handle, int activeBodyIndex, TypedIndex oldShape, TypedIndex newShape)
	{
		if (oldShape.Exists != newShape.Exists)
		{
			ref BodySet activeSet = ref ActiveSet;
			if (newShape.Exists)
			{
				ref BodyDynamics reference = ref activeSet.DynamicsState[activeBodyIndex];
				AddCollidableToBroadPhase(handle, in reference.Motion.Pose, in reference.Inertia.Local, ref activeSet.Collidables[activeBodyIndex]);
			}
			else
			{
				RemoveCollidableFromBroadPhase(ref activeSet.Collidables[activeBodyIndex]);
			}
		}
	}

	public void SetShape(BodyHandle handle, TypedIndex newShape)
	{
		ref BodyMemoryLocation reference = ref HandleToLocation[handle.Value];
		if (reference.SetIndex > 0)
		{
			awakener.AwakenBody(handle);
		}
		ref Collidable reference2 = ref ActiveSet.Collidables[reference.Index];
		TypedIndex shape = reference2.Shape;
		reference2.Shape = newShape;
		UpdateForShapeChange(handle, reference.Index, shape, newShape);
		UpdateBounds(handle);
	}
```

So `SetShape(default)` (a non-existent `TypedIndex`) removes the collidable from the broad phase, and `SetShape(shape)`
re-adds it with updated bounds. Assigning `Collidable.Shape` directly would leave a stale broad-phase entry ("ghost").
The shape data is never disposed, so animated or scaled compounds come back exactly as they were. Filtering pairs in the
narrow phase instead does not work: `NarrowPhaseCallbacks` methods are inlined into a generic struct parameter.

## Lifecycle and timing

| Step | Where | Thread |
|---|---|---|
| Publish the source set | The `PhysicsFrameHook` handoff in `Program.PrepareFrame`, after the weld teleports | Main, `frame:prepare` |
| `ExecuteNextVehicleSolvers` queues the vehicle task | `Program.PrepareFrame` | Main |
| Island jobs step bubbles: `FullPhysicsStepUntil` calls `DetectCollisions`, `FullPhysicsConstrainedStep` calls `Simulate` | `VehicleUpdateTask.RunIsland` on `JobSystems.VehicleWorkerPool` | Vehicle workers, `physics:vehicle-solver` |
| Prefix, Bepu fan-out, finalizer | Inside each `ConstraintSim` call | The bubble's owning worker brackets Bepu's worker fan-out, `physics:bepu` |

Why it is safe: each `ConstraintSim` is driven by one island job at a time and calls Bepu synchronously, so the prefix runs
before Bepu dispatches its workers and the finalizer after they return. The brackets cover everything collision-related:
`BeginContactPass` (part contact load, clutter statics) runs inside both methods, and `Simulate` also runs
`ApplyPendingHits` and `PartContactLoad.Apply` before the finalizer, so a suppressed source accumulates no contact load for
part failure either. The set is published only at the handoff, when no vehicle worker is running, and replaced by reference.
See [mutating at the handoff](../../solver/mutate-vehicles-at-a-physics-safe-point/overview.md).

## Steps

1. Publish an immutable set at the handoff. Build a new `HashSet<Vehicle>` with reference equality of sources whose weld
   is enabled, has `Collisions == false`, both vessels alive and sharing a parent, and swap it in with `Volatile.Write`.
   Workers only ever see a complete, never-mutated set:

```csharp
// garrys-torch.lib/WeldCollisionPatches.cs L55-L66
    public static void Publish(IReadOnlyList<WeldEntry> welds)
    {
        var sources = new HashSet<Vehicle>(ReferenceEqualityComparer.Instance);
        foreach (var weld in welds)
            if (weld.WeldEnabled && !weld.Collisions && !weld.Source.IsDisposed && !weld.Target.IsDisposed
                && weld.Source.Parent == weld.Target.Parent)
                sources.Add(weld.Source);
        Volatile.Write(ref _sources, sources);
    }

    public static void Clear() =>
        Volatile.Write(ref _sources, new HashSet<Vehicle>(ReferenceEqualityComparer.Instance));
```

2. Resolve the two targets by exact signature: `ConstraintSim.DetectCollisions(double)` and
   `ConstraintSim.Simulate(double, ref readonly SimStep)` (`typeof(SimStep).MakeByRefType()` matches `in`/`ref readonly`).
   A missing target throws and the whole patch set rolls back.
3. In the prefix, read the set with `Volatile.Read`, return at once when empty, otherwise walk `HandleToState`, and for each
   state whose `ReadOnlyVehicle` is a suppressed source remember `Collidable.Shape` if it exists and call `SetShape(default)`.
   The list of `(handle, shape)` pairs is the Harmony `__state`. In the finalizer, restore every captured shape; a finalizer
   returning `void` does not swallow the original exception, so a failing pass still fails, but with its shapes back:

```csharp
// garrys-torch.lib/WeldCollisionPatches.cs L68-L104
    private static void BeforePass(ConstraintSim __instance, out List<(BodyHandle Body, TypedIndex Shape)>? __state)
    {
        __state = null;
        var sources = Volatile.Read(ref _sources);
        if (sources.Count == 0) return;

        // These entry points run on the bubble's owning worker before Bepu dispatches its workers.
        // SetShape updates the broad phase; changing Collidable.Shape directly would leave ghosts.
        foreach (var (handle, state) in __instance.HandleToState)
        {
            if (!sources.Contains(state.ReadOnlyVehicle)) continue;
            var body = __instance.Simulation.Bodies[handle];
            var shape = body.Collidable.Shape;
            if (!shape.Exists) continue;
            (__state ??= new()).Add((handle, shape));
            body.SetShape(default);
        }
    }

    private static void AfterPass(ConstraintSim __instance, List<(BodyHandle Body, TypedIndex Shape)>? __state)
    {
        if (__state == null) return;
        // Harmony finalizers also run on exceptions, including partially completed prefixes.
        // Preserve the actual shape, including animated/scaled compounds; never dispose its data.
        foreach (var (handle, shape) in __state)
        {
            try
            {
                var body = __instance.Simulation.Bodies[handle];
                body.SetShape(shape);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"garrys-torch: failed to restore collision shape: {ex}");
            }
        }
    }
```

4. Keep the empty-set path to a single volatile read, and allocate the state list only when a suppressed body is
   present in that sim.
5. Apply UI changes at the next handoff: disabling, removing or auto-unwelding a weld takes effect at the next publish, not mid-step.

## Complete example

| File | Role |
|---|---|
| [garrys-torch.lib/WeldCollisionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldCollisionPatches.cs#L19-L104) | Patch targets, `Publish`, `BeforePass`, `AfterPass` |
| [garrys-torch.lib/GarrysTorchPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/GarrysTorchPatches.cs) | The handoff that calls `Publish` |

## Variations and gotchas

- Not a full ghost mode: ocean forces, aerodynamics, gravity and non-contact damage still apply; only Bepu rigid-body
  contacts (vessels, terrain, statics, clutter) are suppressed.
- `Bodies.SetShape` wakes a sleeping body before changing its shape, so a suppressed source is woken by every pass.
- Both methods must keep bracketing all rigid-body collision work and keep running on the bubble's owning worker; re-check
  `ConstraintSim` and `Bodies.SetShape` on every game update.
- A throw in the prefix after some shapes were captured is still restored, because the finalizer runs with the partial `__state`.
- If the vessel moves between bubbles mid-step, the capture is per `ConstraintSim`, so restoration uses the same simulation.

## See also

- [Overview and comparison](overview.md)
- [Weld one vessel to another](../../kinematics/weld-one-vessel-to-another/weld-one-vessel-to-another-1.md)
- Mod feature: [Garry's Torch scoped collision suppression](../../../../mods/unscience/features/garrys-torch-scoped-collision-suppression.md)
- [Write worker-thread-safe patches](../../solver/write-worker-thread-safe-solver-patches/overview.md)
