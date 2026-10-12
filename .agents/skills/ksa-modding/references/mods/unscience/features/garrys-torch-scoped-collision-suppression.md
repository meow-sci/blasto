---
type: Mod Feature
title: Per-vessel collision suppression by bracketing ConstraintSim passes
description: Garry's Torch removes welded sources from Bepu broad-phase collision for the duration of each ConstraintSim.DetectCollisions and Simulate call with a prefix that calls BodyReference.SetShape(default) and a finalizer that restores the exact shape
mod: unscience
submod: garrys-torch
category: physics
techniques: [harmony-prefix, harmony-finalizer, bepu-physics]
hooks:
  - harmony-prefix KSA.ConstraintSim.DetectCollisions(double)
  - harmony-finalizer KSA.ConstraintSim.DetectCollisions(double)
  - harmony-prefix KSA.ConstraintSim.Simulate(double, in SimStep)
  - harmony-finalizer KSA.ConstraintSim.Simulate(double, in SimStep)
  - harmony-transpiler KSA.Program.PrepareFrame (shared PhysicsFrameHook, publishes the source set)
lifecycle: [frame:prepare, physics:vehicle-solver, physics:bepu]
game_types:
  - KSA.ConstraintSim
  - KSA.VehicleUpdateState
  - KSA.PhysicsBubble
  - KSA.NarrowPhaseCallbacks
  - BepuPhysics.BodyReference
  - BepuPhysics.Bodies
  - BepuPhysics.Collidables.Collidable
  - BepuPhysics.Collidables.TypedIndex
tags: [bepu, collisions, broad-phase, worker-thread, finalizer, weld, threading]
---

A welded source vessel is held overlapping or touching its target every frame. With contacts on,
that produces violent contact impulses, dents and (since KSA 5402) structural part failure. Garry's
Torch therefore makes welded sources collision-free by default: they keep simulating (modules,
actuators, lights, integration), but they do not touch other vessels, terrain, static objects or
ground clutter. Each weld has a "Collisions" checkbox (and API/preset field) to opt back in.[^garrys-torch-lib-readme]

Mechanism in one line: Harmony prefixes on the two `ConstraintSim` entry points that run Bepu
collision work remove each suppressed source body's shape with `BodyReference.SetShape(default)`
(which takes the collidable out of the broad phase), and finalizers on the same methods put the
captured `TypedIndex` back, even when the pass throws.

## How it works

1. **Publish** at the frame handoff ([garrys-torch.lib/WeldCollisionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldCollisionPatches.cs#L55-L66)).
   In the `finally` of `GarrysTorchPatches.Update`, `Publish(submod.Welds)` builds a new
   `HashSet<Vehicle>` (reference equality) of sources whose weld is enabled, has `Collisions == false`,
   both vessels are alive and share a parent, and swaps it in with `Volatile.Write`. Workers only ever
   see a complete, never-mutated set; disabling, removing or auto-unwelding a weld takes effect at the
   next publish. With no submod, `Clear()` publishes an empty set.
2. **Patch targets** ([garrys-torch.lib/WeldCollisionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldCollisionPatches.cs#L19-L42)). Resolved by
   exact signature: `ConstraintSim.DetectCollisions(double)` and
   `ConstraintSim.Simulate(double, ref readonly SimStep)` (`typeof(SimStep).MakeByRefType()` matches
   `in`/`ref readonly`). A missing target throws and the whole Garry's Torch patch set rolls back.
3. **Before the pass** ([garrys-torch.lib/WeldCollisionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldCollisionPatches.cs#L68-L85)). Read the set
   with `Volatile.Read`; return immediately when empty. Otherwise walk `__instance.HandleToState`
   (Bepu body handle -> `VehicleUpdateState`), and for each state whose `ReadOnlyVehicle` is a suppressed
   source, fetch `Simulation.Bodies[handle]`, remember `Collidable.Shape` if it exists, and call
   `SetShape(default)`. The list of `(handle, shape)` pairs is the Harmony `__state`.
4. **After the pass** ([garrys-torch.lib/WeldCollisionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldCollisionPatches.cs#L87-L104)). A Harmony
   finalizer restores every captured shape with `SetShape(shape)`, logging but not throwing on a
   failed restore. A finalizer that returns `void` does not swallow the original exception, so a failing
   pass still fails, but with its shapes back.

Why `SetShape` and not editing `Collidable.Shape` directly: in Bepu, `Bodies.SetShape` calls
`UpdateForShapeChange`, which removes the collidable from the broad phase when the new shape does not
exist and re-adds it when it does, then updates bounds. Writing the field alone would leave a stale
broad-phase entry (the code comment calls these "ghosts").[^bepu-physics-bodies] The shape's data is
never disposed, so animated or scaled compounds come back exactly as they were.

Why not filter pairs in the narrow phase: KSA's `NarrowPhaseCallbacks` is an internal readonly struct
whose `AllowContactGeneration`/`ConfigureContactManifold` methods are `AggressiveInlining` and are
consumed as a generic struct parameter by Bepu, so a Harmony patch on them cannot be relied on to run.
The `ConstraintSim` methods are ordinary instance methods that bracket all rigid-body collision
dispatch for one simulation.[^ksa-narrow-phase-callbacks]

## Lifecycle and timing

| Step | Where | Thread |
|---|---|---|
| Publish the source set | `PhysicsFrameHook` handoff in `Program.PrepareFrame`, after the weld teleports | Main, `frame:prepare` |
| `ExecuteNextVehicleSolvers` queues `VehicleUpdateTask` | `Program.PrepareFrame` L2220 | Main |
| Island jobs step bubbles; `PhysicsBubble.FullPhysicsStepUntil` calls `ConstraintSim.DetectCollisions`, `FullPhysicsConstrainedStep` calls `ConstraintSim.Simulate` | `VehicleUpdateTask.RunIsland` on `JobSystems.VehicleWorkerPool` | Vehicle workers, `physics:vehicle-solver` |
| Prefix, Bepu `PredictBoundingBoxes`/`CollisionDetection` or `Timestep` fan-out, finalizer | Inside each `ConstraintSim` call | The bubble's owning worker brackets the Bepu worker fan-out, `physics:bepu` |

What makes this safe (checked in the 5554 decomp):

- Each `ConstraintSim` is driven by one island job at a time, and both entry points call Bepu
  synchronously with the sim's `_workerDispatcher`, so the prefix runs before Bepu dispatches its
  worker threads and the finalizer after they return.[^ksa-constraint-sim]
- The brackets cover everything collision-related in the step: `BeginContactPass` (part contact load,
  clutter statics) runs inside both methods, and `Simulate` also runs `ClutterStatics.ApplyPendingHits`
  and `PartContactLoad.Apply` after `Timestep`, still before the finalizer. A suppressed source therefore
  accumulates no contact load for `PartFailure` either.[^ksa-constraint-sim]
- The set is published only at the handoff, when no vehicle worker is running, and replaced by
  reference; the worker-side code reads it but never enumerates the mutable weld list.
- Because weld teleports happen at the same handoff, the set and the poses used by the next step
  always agree.

## Key code

| Path | Role |
|---|---|
| [garrys-torch.lib/WeldCollisionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldCollisionPatches.cs) | Targets, publish, prefix and finalizer |
| [garrys-torch.lib/GarrysTorchPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/GarrysTorchPatches.cs#L10-L44) | Installs the patches before the frame hook; publishes after each weld update |
| [garrys-torch.lib/WeldEntry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldEntry.cs#L25-L26) | `Collisions` flag, default false |
| [garrys-torch.lib/PresetManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/PresetManager.cs#L116) | Preset `collisions` key, missing means false |
| [garrys-torch.tests/CollisionChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.tests/CollisionChecks.cs) | Real Bepu bodies: suppression, unaffected pairs, exact shape restore, exception restore, unload |
| [garrys-torch.tests/CollisionFixture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.tests/CollisionFixture.cs) | Managed `ConstraintSim` stand-in around a real Bepu `Simulation` |
| [garrys-torch.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/README.md) | Authors' description of the collision option and its limits |

## Snippets

Publishing an immutable set at the handoff:

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

Scoped removal and exact restoration:

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

The two bracketed entry points in the game (build 5554):

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

Why `SetShape` is the right lever: shape existence drives broad-phase membership (Bepu as shipped
with build 5554):

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

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `ConstraintSim.DetectCollisions(double)` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L870-L885) | Bracketed pass (bounding-box prediction and collision detection) |
| `ConstraintSim.Simulate(double, ref readonly SimStep)` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L887-L902) | Bracketed pass (timestep, clutter hits, contact load) |
| `ConstraintSim.HandleToState`, `ConstraintSim.Simulation` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L52-L54) | Body handle to vessel identity |
| `VehicleUpdateState.ReadOnlyVehicle` | [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L14) | Identity compared with the published set |
| `PhysicsBubble.FullPhysicsStepUntil` / `FullPhysicsConstrainedStep` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1391) | Callers of the two passes on vehicle workers |
| `BodyReference.SetShape(TypedIndex)`, `BodyReference.Collidable` | [current/decomp/BepuPhysics/BodyReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/BepuPhysics/BodyReference.cs#L195-L198) | Remove and restore the collidable |
| `Bodies.SetShape` / `UpdateForShapeChange` | [current/decomp/BepuPhysics/Bodies.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/BepuPhysics/Bodies.cs#L349-L379) | Broad-phase add/remove, wake, bounds update |
| `NarrowPhaseCallbacks` | [current/decomp/KSA/NarrowPhaseCallbacks.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NarrowPhaseCallbacks.cs#L12-L24) | Why the narrow phase is not patched |

## Pitfalls and version notes

- Not a full "ghost" mode: ocean forces, aerodynamics, gravity and non-contact damage still apply to
  the source; only Bepu rigid-body contacts (vessels, terrain, statics, clutter) are suppressed.
- `Bodies.SetShape` wakes a sleeping body before changing its shape, so a suppressed source is woken by
  every pass.[^bepu-physics-bodies]
- Both methods must keep bracketing all rigid-body collision work and keep running on the bubble's
  owning worker; the authors list re-checking `ConstraintSim` and `Bodies.SetShape` (L350-L379 in their
  5482 notes, L349-L379 here) on every update. In 5554 the bodies match their 5482 description.
- Collisions toggled in the UI apply at the next handoff, not mid-step.
- The prefix allocates a list only when a suppressed body is present in that sim; the empty-set path
  is a single volatile read.

## See also

- [Garry's Torch vehicle welding](./garrys-torch-vehicle-welding.md)
- [Physics-safe vessel mutation at the PrepareFrame solver handoff](./lib-physics-frame-hook.md)
- [The Tick indestructibility](./the-tick-vessel-indestructibility.md) (keeps contacts, removes destruction)
- [Godzilla collider scaling](./godzilla-collider-scaling.md) (changes shapes at the handoff instead)
- [Kitchen Sink G-load protection](./kitchen-sink-g-load-protection.md)
- [Garry's Torch sub-mod](../submods/garrys-torch.md) and [mod overview](../overview.md)
- [Restore what a prefix changed around one game call](../../../howto/patching/harmony/run-cleanup-or-recover-from-exceptions-with-a-finalizer/run-cleanup-or-recover-from-exceptions-with-a-finalizer-1.md) - how-to article demonstrating this feature
- [Bracket the non-inlined methods that enclose an inlined struct callback](../../../howto/patching/internals/patch-around-aggressive-inlining/patch-around-aggressive-inlining-4.md) - how-to article demonstrating this feature
- [BodyReference.SetShape(default) in a prefix with a restoring finalizer](../../../howto/physics/colliders/suppress-collisions-for-one-vessel/suppress-collisions-for-one-vessel.md) - how-to article demonstrating this feature
- [BeforePhysics callback at the PrepareFrame handoff](../../../howto/physics/kinematics/weld-one-vessel-to-another/weld-one-vessel-to-another-1.md) - how-to article demonstrating this feature

[^garrys-torch-lib-readme]: garrys-torch.lib/README.md
[^bepu-physics-bodies]: BepuPhysics.Bodies (decompiled, build 2026.10.10.5554)
[^ksa-narrow-phase-callbacks]: KSA.NarrowPhaseCallbacks (decompiled, build 2026.10.10.5554)
[^ksa-constraint-sim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
