---
type: How-To
title: Bracket the non-inlined methods that enclose an inlined struct callback
description: Suppress a vessel's rigid-body collisions without patching the AggressiveInlining NarrowPhaseCallbacks struct, by removing its Bepu shapes in a prefix on ConstraintSim.DetectCollisions and Simulate and restoring them in a finalizer, and what Sphinx does where it must patch the callback
category: patching
group: internals
topic: patch-around-aggressive-inlining
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/unscience/features/garrys-torch-scoped-collision-suppression
  - mods/unscience/features/sphinx-static-colliders
techniques: [harmony-prefix, harmony-finalizer, enclosing-method-bracket]
hooks:
  - harmony-prefix KSA.ConstraintSim.DetectCollisions
  - harmony-finalizer KSA.ConstraintSim.DetectCollisions
  - harmony-prefix KSA.ConstraintSim.Simulate
  - harmony-finalizer KSA.ConstraintSim.Simulate
lifecycle: [startup:all-mods-loaded, physics:vehicle-solver, physics:bepu]
game_types: [KSA.ConstraintSim, KSA.NarrowPhaseCallbacks, KSA.PhysicsBubble, KSA.Vehicle]
tags: [harmony, prefix, finalizer, bepu, inlining, struct-callback, worker-thread, collisions]
---

The place where Bepu decides whether two bodies may touch is KSA's `NarrowPhaseCallbacks`, an internal struct whose `AllowContactGeneration` is `AggressiveInlining` and is consumed by Bepu as a generic struct
parameter. A detour on it is the kind of patch that may never run. Garry's Torch gets the effect it wants, collision-free welded vessels, from two ordinary instance methods that enclose all rigid-body collision work:
a prefix takes the vessel's shape out of the broad phase and a finalizer puts it back, even when the pass throws.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by unscience in [Per-vessel collision suppression by bracketing ConstraintSim passes](../../../../mods/unscience/features/garrys-torch-scoped-collision-suppression.md); Sphinx's contrasting choice is in [Automatic box or mesh colliders for imported statics with scoped Bepu hooks](../../../../mods/unscience/features/sphinx-static-colliders.md).

## Background: how the game does it

The callback is an internal readonly struct and its filter is marked for inlining:

```csharp
// current/decomp/KSA/NarrowPhaseCallbacks.cs L12-L28
internal readonly struct NarrowPhaseCallbacks(ConstraintSim sim) : INarrowPhaseCallbacks
{
	public readonly ConstraintSim Sim = sim;

	private const float STOCK_TERRAIN_FRICTION_COEFFICIENT = 0.95f;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Initialize(Simulation simulation)
	{
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
	{
		if (a.Mobility != CollidableMobility.Dynamic && b.Mobility != CollidableMobility.Dynamic)
		{
			return false;
```

Source: [current/decomp/KSA/NarrowPhaseCallbacks.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NarrowPhaseCallbacks.cs#L12-L28)[^ksa-narrowphasecallbacks]

The enclosing methods are not marked. `DetectCollisions` runs Bepu's bounding-box prediction and collision detection; `Simulate` runs the timestep, then applies clutter hits and part contact loads:

```csharp
// current/decomp/KSA/ConstraintSim.cs L870-L904
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
	PartContactLoad.Apply(this, simStep.DeltaTime);
}
```

Source: [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L870-L904)[^ksa-constraintsim]

`PhysicsBubble` calls `DetectCollisions` during the full physics step ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1598)[^ksa-physicsbubble]) and `Simulate` in the constrained step, each on the bubble's owning worker.

## Lifecycle and timing

Install at `startup:all-mods-loaded`. Both prefixes and finalizers run in `physics:vehicle-solver` on the bubble's owning worker; Bepu's own worker fan-out (`physics:bepu`) happens inside the bracketed call, so the prefix
runs before Bepu dispatches and the finalizer after it returns. The set of vessels to suppress is published on the main thread at the result handoff and read here with `Volatile.Read`. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Run code inside the physics step on worker threads](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md).

## Steps

1. **Pick the non-inlined enclosing methods.** `ConstraintSim.DetectCollisions(double)` and `Simulate(double, ref readonly SimStep)` carry no inlining attribute and every collision dispatch for a simulation happens inside one of them.

2. **Resolve them by exact signature, and apply prefix and finalizer to both.** `typeof(SimStep).MakeByRefType()` matches the `ref readonly` parameter ([Explicit parameter arrays with MakeByRefType for overloaded and by-reference targets](../patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-3.md)); a missing target throws and the install removes whatever was applied:

```csharp
// garrys-torch.lib/WeldCollisionPatches.cs L12-L53
/// <summary>Keep welded sources simulated, but omit their shapes from rigid-body collision passes.</summary>
internal static class WeldCollisionPatches
{
    // Published only at the result/snapshot handoff. Workers never read mutable welds or UI lists.
    private static HashSet<Vehicle> _sources = new(ReferenceEqualityComparer.Instance);
    private static readonly MethodInfo Prefix = AccessTools.Method(typeof(WeldCollisionPatches), nameof(BeforePass));
    private static readonly MethodInfo Finalizer = AccessTools.Method(typeof(WeldCollisionPatches), nameof(AfterPass));
    private static readonly MethodInfo[] Targets =
    {
        AccessTools.Method(typeof(ConstraintSim), nameof(ConstraintSim.DetectCollisions), new[] { typeof(double) }),
        AccessTools.Method(typeof(ConstraintSim), nameof(ConstraintSim.Simulate),
            new[] { typeof(double), typeof(SimStep).MakeByRefType() }),
    };

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

    public static void Remove(Harmony harmony)
    {
        Clear();
        foreach (var target in Targets)
        {
            if (target == null) continue;
            harmony.Unpatch(target, Prefix);
            harmony.Unpatch(target, Finalizer);
        }
    }
```

Source: [garrys-torch.lib/WeldCollisionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldCollisionPatches.cs#L12-L53)[^unscience-weldcollisionpatches]

3. **Publish the suppressed set by reference swap.** Workers see a complete, never-mutated `HashSet<Vehicle>`:

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

Source: [garrys-torch.lib/WeldCollisionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldCollisionPatches.cs#L55-L66)

4. **In the prefix, take shapes out and remember them in `__state`.** The call is `SetShape(default)`, which removes the collidable from the broad phase; assigning `Collidable.Shape` directly would leave a stale entry:

```csharp
// garrys-torch.lib/WeldCollisionPatches.cs L68-L85
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

Source: [garrys-torch.lib/WeldCollisionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldCollisionPatches.cs#L68-L85)

5. **In the finalizer, put every shape back.** A Harmony finalizer also runs when the original throws; a `void` finalizer does not swallow the exception, so a failing pass still fails, but with its shapes restored
   ([Restore what a prefix changed around one game call](../../harmony/run-cleanup-or-recover-from-exceptions-with-a-finalizer/run-cleanup-or-recover-from-exceptions-with-a-finalizer-1.md)):

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

6. **Remove by patch method.** `Remove` unpatches the prefix and finalizer from each target, not the whole id, so other features sharing the Harmony instance keep their patches ([Unpatch by patch method on a shared Harmony instance](../../safety/remove-your-patches-safely-at-runtime-and-at-exit/remove-your-patches-safely-at-runtime-and-at-exit-3.md)).

## Complete example

| File | Role |
|---|---|
| [garrys-torch.lib/WeldCollisionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldCollisionPatches.cs) | Targets, apply, publish, prefix, finalizer |

## Variations and gotchas

- **Coarse by design.** The brackets suppress rigid-body contacts for the source body for the whole pass: vessels, terrain, statics and clutter. Ocean forces, aerodynamics and gravity still apply. `Bodies.SetShape` also wakes a sleeping body, so a suppressed source wakes every pass (feature page).
- **What the brackets also cover.** `BeginContactPass`, the clutter hit application and `PartContactLoad.Apply` run inside the two methods, so a suppressed source accumulates no contact load for part failure either.
- **Sphinx patches the callback anyway.** Placed static colliders need the narrow phase to accept contacts with them, which only the callback decides. Sphinx transpiles `NarrowPhaseCallbacks.AllowContactGeneration`, replacing its single `BepuHandles.IsGroundSurface` call, and throws unless exactly one call was replaced:

```csharp
// sphinx.lib/SphinxPhysicsPatches.cs L22-L26
var callback = typeof(ConstraintSim).Assembly.GetType("KSA.NarrowPhaseCallbacks", throwOnError: true)!;
var filter = AccessTools.Method(callback, "AllowContactGeneration",
    new[] { typeof(int), typeof(CollidableReference), typeof(CollidableReference), typeof(float).MakeByRefType() })
    ?? throw new MissingMethodException(callback.FullName, "AllowContactGeneration");
harmony.Patch(filter, transpiler: new HarmonyMethod(typeof(SphinxPhysicsPatches), nameof(ContactFilter)));
```

Source: [sphinx.lib/SphinxPhysicsPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxPhysicsPatches.cs#L22-L26)[^unscience-sphinxphysicspatches]

```csharp
// sphinx.lib/SphinxPhysicsPatches.cs L51-L69
private static IEnumerable<CodeInstruction> ContactFilter(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
{
    var original = AccessTools.Method(typeof(BepuHandles), nameof(BepuHandles.IsGroundSurface));
    var replacement = AccessTools.Method(typeof(SphinxPhysicsPatches), nameof(IsSurface));
    var sim = AccessTools.Field(__originalMethod.DeclaringType, "Sim")
        ?? throw new MissingFieldException("KSA.NarrowPhaseCallbacks", "Sim");
    int replaced = 0;
    foreach (var instruction in input)
    {
        if (!instruction.Calls(original)) { yield return instruction; continue; }
        var load = new CodeInstruction(OpCodes.Ldarg_0);
        load.MoveLabelsFrom(instruction); load.MoveBlocksFrom(instruction);
        yield return load;
        yield return new CodeInstruction(OpCodes.Ldfld, sim);
        yield return new CodeInstruction(OpCodes.Call, replacement);
        replaced++;
    }
    if (replaced != 1) throw new InvalidOperationException("Sphinx expected one stock ground filter call; collision hooks need a game-update review.");
}
```

Source: [sphinx.lib/SphinxPhysicsPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxPhysicsPatches.cs#L51-L69)

  The one-call check is a tripwire for the method changing shape; it does not detect inlining. The feature page says plainly that the authors keep live collision acceptance open and that nothing was run in the game, and a related
  postfix on the also-inlined `ConstraintSim.IsGroundSurfaceFor` has the same caveat.
- **Why this can still work.** The local test in [Changing behaviour of a game method marked AggressiveInlining](overview.md) found a callee patched before its callers were compiled was not bypassed, and `KSA.dll` carries `DisableOptimizations`; both are observations, not a guarantee.
- **Update review.** Both brackets must keep enclosing all collision work and keep running on the bubble's owning worker; the authors list re-checking `ConstraintSim` on every game update.

## See also

- [Rewrite the one caller's call site instead of the inlined callee](patch-around-aggressive-inlining-1.md), [Patch the non-inlined entry points that reach the inlined method too](patch-around-aggressive-inlining-2.md), [Postfix the single non-inlined outer method instead of a per-vehicle callee](patch-around-aggressive-inlining-3.md)
- [Changing behaviour of a game method marked AggressiveInlining](overview.md)
- [Per-vessel collision suppression by bracketing ConstraintSim passes](../../../../mods/unscience/features/garrys-torch-scoped-collision-suppression.md), [Automatic box or mesh colliders for imported statics with scoped Bepu hooks](../../../../mods/unscience/features/sphinx-static-colliders.md)
- [Harmony finalizers, cleanup and scoped overrides](../../harmony/run-cleanup-or-recover-from-exceptions-with-a-finalizer/overview.md)
- Related topic: suppress-collisions-for-one-vessel

[^ksa-narrowphasecallbacks]: KSA.NarrowPhaseCallbacks (decompiled, build 2026.10.10.5554)
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^unscience-weldcollisionpatches]: unscience/garrys-torch.lib/WeldCollisionPatches.cs
[^unscience-sphinxphysicspatches]: unscience/sphinx.lib/SphinxPhysicsPatches.cs
