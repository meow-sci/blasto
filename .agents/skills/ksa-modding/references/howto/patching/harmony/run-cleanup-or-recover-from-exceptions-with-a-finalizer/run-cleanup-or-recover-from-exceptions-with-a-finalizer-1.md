---
type: How-To
title: Restore what a prefix changed around one game call
description: A prefix removes collidable shapes (or reveals interior part templates) for the length of one game call and hands the originals to a finalizer through __state, so the state is put back even when the call throws
category: patching
group: harmony
topic: run-cleanup-or-recover-from-exceptions-with-a-finalizer
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/unscience/features/garrys-torch-scoped-collision-suppression
  - mods/unscience/features/lib-iva-force-render
techniques: [harmony-finalizer, harmony-prefix]
hooks:
  - harmony-prefix KSA.ConstraintSim.DetectCollisions
  - harmony-finalizer KSA.ConstraintSim.DetectCollisions
  - harmony-prefix KSA.ConstraintSim.Simulate
  - harmony-finalizer KSA.ConstraintSim.Simulate
  - harmony-prefix KSA.PartTreeRenderData.Compose
  - harmony-finalizer KSA.PartTreeRenderData.Compose
lifecycle: [physics:vehicle-solver, physics:bepu, frame:render]
game_types: [KSA.ConstraintSim, KSA.PartTreeRenderData, KSA.VehicleUpdateState]
tags: [harmony, finalizer, prefix, state, rollback, bepu, collisions]
---

A postfix does not run when the original throws. A finalizer always does. That makes the finalizer the
right place to *undo* something a prefix did for the duration of one game call: if the call fails, the
game state is still put back. This article shows the pattern twice from the unscience mod. Garry's Torch
removes a welded vessel's collision shape from the Bepu broad phase for one collision pass and restores it
afterwards. `IvaForceRender` flips an `Internal` flag on shared part templates for one `Compose` call and
flips it back.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated in
[Per-vessel collision suppression by bracketing ConstraintSim passes](../../../../mods/unscience/features/garrys-torch-scoped-collision-suppression.md) and [Force IVA interior models to render outside IVA](../../../../mods/unscience/features/lib-iva-force-render.md).

## Background: how the game does it

`ConstraintSim` wraps one Bepu `Simulation` per physics bubble. Two instance methods bracket every
rigid-body collision pass: `DetectCollisions(double)` (bounding-box prediction and collision detection)
and `Simulate(double, ref readonly SimStep)` (the timestep). Both mark the sim poisoned and rethrow on
failure, so an exception is a realistic outcome:

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

Source: [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L870-L902)[^ksa-constraintsim]

`HandleToState` maps a Bepu body handle to the `VehicleUpdateState` that owns it ([current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L52-L56)).
Bepu's `Bodies.SetShape` changes broad-phase membership when the shape's existence changes, which is why
the mod uses it rather than writing `Collidable.Shape`:

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

Source: [current/decomp/BepuPhysics/Bodies.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/BepuPhysics/Bodies.cs#L349-L379)[^ksa-bodies]

For the render case, `PartTreeRenderData.Compose` appends part-model instances only when
`!Template.Internal || viewport is IVA` ([current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1258-L1266)[^ksa-parttreerenderdata],
[current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1300-L1303)).

## Lifecycle and timing

| Step | Where | Thread |
|---|---|---|
| Publish the set of suppressed vessels | the `PrepareFrame` handoff, once per frame | main (`frame:prepare`) |
| `ConstraintSim.DetectCollisions` / `Simulate` | `VehicleUpdateTask.RunIsland` on the vehicle worker pool, inside the bubble's step | vehicle workers (`physics:vehicle-solver`) |
| Prefix, Bepu worker fan-out, finalizer | inside each call; the prefix runs before Bepu dispatches its workers, the finalizer after they return | the bubble's owning worker, around `physics:bepu` |
| `Compose` prefix and finalizer | `Program.UpdateRenderingResources`, once per tree per visible viewport | main (`frame:render`) |

Each `ConstraintSim` is driven by one island job at a time and both entry points call Bepu
synchronously with the sim's own worker dispatcher, so the bracket sees no concurrent change to the
bodies it touched. The published set is replaced by reference at the handoff and only read here. See
[Run code inside the physics step on worker threads](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md) for the worker-thread view and [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the frame.

## Steps

1. **Pick the narrowest bracket.** The narrow-phase callbacks that actually decide contact generation are
   `AggressiveInlining` members of a struct used as a generic parameter, so a Harmony patch there cannot
   be relied on to run (see [Bracket the non-inlined methods that enclose an inlined struct callback](../../internals/patch-around-aggressive-inlining/patch-around-aggressive-inlining-4.md)). The two
   `ConstraintSim` methods are ordinary instance methods that enclose all of it.

2. **Resolve both targets by exact signature.** `ref readonly SimStep` is matched with
   `typeof(SimStep).MakeByRefType()`. A missing target throws and the whole patch set rolls back; one
   prefix method and one finalizer method serve both targets:

```csharp
// garrys-torch.lib/WeldCollisionPatches.cs L17-L24, L26-L42
private static readonly MethodInfo Prefix = AccessTools.Method(typeof(WeldCollisionPatches), nameof(BeforePass));
private static readonly MethodInfo Finalizer = AccessTools.Method(typeof(WeldCollisionPatches), nameof(AfterPass));
private static readonly MethodInfo[] Targets =
{
    AccessTools.Method(typeof(ConstraintSim), nameof(ConstraintSim.DetectCollisions), new[] { typeof(double) }),
    AccessTools.Method(typeof(ConstraintSim), nameof(ConstraintSim.Simulate),
        new[] { typeof(double), typeof(SimStep).MakeByRefType() }),
};
// ...
public static void Apply(Harmony harmony)
{
    try
    {
        foreach (var target in Targets)
        {
            if (target == null)
                throw new MissingMethodException("KSA ConstraintSim collision pass signature changed.");
            harmony.Patch(target, prefix: new HarmonyMethod(Prefix), finalizer: new HarmonyMethod(Finalizer));
        }
    }
    catch
    {
        Remove(harmony);
        throw;
    }
}
```

Source: [garrys-torch.lib/WeldCollisionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldCollisionPatches.cs#L17-L42)[^unscience-weldcollisionpatches]

3. **In the prefix, change the state and put the originals in `__state`.** `out` parameters named
   `__state` flow from the prefix to the postfix and finalizer. The prefix does nothing (no allocation)
   when the published set is empty; otherwise it walks the sim's bodies, records `(handle, shape)` and
   calls `SetShape(default)` so the collidable leaves the broad phase:

```csharp
// garrys-torch.lib/WeldCollisionPatches.cs L68-L86
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

```

Source: [garrys-torch.lib/WeldCollisionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldCollisionPatches.cs#L68-L86)

4. **In the finalizer, restore exactly what the prefix recorded.** The finalizer receives the same
   `__state`, runs even if the pass threw (or if the prefix itself threw part-way), restores each shape and
   logs, but never throws. It returns `void`, so the original exception continues to the caller, only now with
   the shapes back:

```csharp
// garrys-torch.lib/WeldCollisionPatches.cs L87-L104
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

Source: [garrys-torch.lib/WeldCollisionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldCollisionPatches.cs#L87-L104)

5. **Same shape for a render flag.** `IvaForceRender` reveals every internal template in a prefix on
   `PartTreeRenderData.Compose` and re-hides them in a finalizer, so an exception cannot leave interiors
   exposed. This finalizer returns `Exception?` and returns `__exception` unchanged, which is the explicit
   way to say "do not swallow":

```csharp
// ksa-abstractions.lib/IvaForceRender.cs L70-L76
// internal/IVA gate itself, without calling PartModel.AddInstance.
_composeOriginal = AccessTools.Method(typeof(PartTreeRenderData), nameof(PartTreeRenderData.Compose), new[] { typeof(Brutal.Numerics.double4x4).MakeByRefType(), typeof(bool), typeof(IViewport), typeof(int) })
    ?? throw new MissingMethodException(nameof(PartTreeRenderData), nameof(PartTreeRenderData.Compose));
_composePrefix = typeof(IvaForceRender).GetMethod(nameof(ComposePrefix), BindingFlags.NonPublic | BindingFlags.Static)!;
_composeFinalizer = typeof(IvaForceRender).GetMethod(nameof(ComposeFinalizer), BindingFlags.NonPublic | BindingFlags.Static)!;
harmony.Patch(_composeOriginal, prefix: new HarmonyMethod(_composePrefix), finalizer: new HarmonyMethod(_composeFinalizer));
IsInstalled = true;
```

Source: [ksa-abstractions.lib/IvaForceRender.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/IvaForceRender.cs#L70-L76)[^unscience-ivaforcerender]

```csharp
// ksa-abstractions.lib/IvaForceRender.cs L138-L157
private static void ComposePrefix(IViewport inViewport)
{
    if (KSA.Program.Editor == null || inViewport.Mode == CameraMode.IVA
        || !inViewport.HasAny(ViewportOptionFlags.RenderPartModels)) return;
    if (_internalTemplatesDirty) RebuildInternalTemplates();
    foreach (var template in _internalTemplates)
    {
        if (!template.Internal) continue;
        template.Internal = false;
        _revealed.Add(template);
    }
}

private static Exception? ComposeFinalizer(Exception? __exception)
{
    foreach (var template in _revealed)
        template.Internal = true;
    _revealed.Clear();
    return __exception;
}
```

Source: [ksa-abstractions.lib/IvaForceRender.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/IvaForceRender.cs#L138-L157)

6. **Verify.** With a welded source and "Collisions" off, the vessel passes through other vessels without
   contact impulses. The mod's headless tests cover suppression, exact shape restore and restore after an
   exception ([garrys-torch.tests/CollisionChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.tests/CollisionChecks.cs)[^unscience-collisionchecks]).

## Complete example

| File | Role |
|---|---|
| [garrys-torch.lib/WeldCollisionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldCollisionPatches.cs) | Publish, targets, prefix, finalizer |
| [ksa-abstractions.lib/IvaForceRender.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/IvaForceRender.cs#L62-L79) | Install the `Compose` prefix and finalizer |
| [ksa-abstractions.lib/IvaForceRender.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/IvaForceRender.cs#L138-L157) | Prefix and finalizer bodies |

## Variations and gotchas

- **A void finalizer does not swallow.** An `Exception`-returning finalizer replaces the exception with
  its return value: `null` swallows, `__exception` rethrows. Checked against Lib.Harmony 2.4.2 (see the
  overview).
- **Do not rely on a postfix for cleanup.** If the original throws, the postfix never runs and the state
  stays changed.
- **Finalizers also run for a prefix that threw.** The mod's own comment says the finalizer covers
  partially completed prefixes, and a test against Lib.Harmony 2.4.2 confirmed that a finalizer runs, sees the
  prefix's exception and that the original is then not called. Garry's finalizer therefore tolerates a
  `null` `__state`.
- **Restore the object you recorded, not a default.** Garry's Torch keeps the exact `TypedIndex`, so animated
  or scaled compounds come back as they were. The shape's data is never disposed.
- **Cost on the hot path.** `Simulate` and `DetectCollisions` run for every bubble every substep; the empty
  case is one `Volatile.Read`.
- **Not a full ghost mode.** Only Bepu rigid-body contacts are suppressed; aerodynamics, gravity and
  non-contact damage still apply. `Bodies.SetShape` wakes sleeping bodies.
- **Shared templates.** The IVA flip changes data shared by every instance of a model for the length of one
  call; it is safe only because the only readers are main-thread render paths.

## See also

- [Swallow an exception and finish the method's remaining work](run-cleanup-or-recover-from-exceptions-with-a-finalizer-2.md), [Prefix with __state and a finalizer that restores an edited input](run-cleanup-or-recover-from-exceptions-with-a-finalizer-3.md), [Prefix, postfix and finalizer as a transaction around loads](run-cleanup-or-recover-from-exceptions-with-a-finalizer-4.md), [ThreadStatic override set in try/finally around the mod's own submission](run-cleanup-or-recover-from-exceptions-with-a-finalizer-5.md), [Thread-static construction context scoping a transpiler and a prefix](run-cleanup-or-recover-from-exceptions-with-a-finalizer-6.md), [Mark the asker in a Priority.First prefix, clear it in a Priority.Last finalizer](run-cleanup-or-recover-from-exceptions-with-a-finalizer-7.md)
- [Harmony finalizers, cleanup and scoped overrides](overview.md)
- [Per-vessel collision suppression by bracketing ConstraintSim passes](../../../../mods/unscience/features/garrys-torch-scoped-collision-suppression.md), [Force IVA interior models to render outside IVA](../../../../mods/unscience/features/lib-iva-force-render.md)
- [Changing behaviour of a game method marked AggressiveInlining](../../internals/patch-around-aggressive-inlining/overview.md)
- Related tasks: suppress-collisions-for-one-vessel

[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-bodies]: BepuPhysics.Bodies (decompiled, build 2026.10.10.5554)
[^ksa-parttreerenderdata]: KSA.PartTreeRenderData (decompiled, build 2026.10.10.5554)
[^unscience-weldcollisionpatches]: unscience/garrys-torch.lib/WeldCollisionPatches.cs
[^unscience-ivaforcerender]: unscience/ksa-abstractions.lib/IvaForceRender.cs
[^unscience-collisionchecks]: unscience/garrys-torch.tests/CollisionChecks.cs
