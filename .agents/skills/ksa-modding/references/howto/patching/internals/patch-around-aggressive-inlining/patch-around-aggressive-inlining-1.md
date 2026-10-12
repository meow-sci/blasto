---
type: How-To
title: Rewrite the one caller's call site instead of the inlined callee
description: Change what PhysicsStates.ComputeDrag does for atmospheric drag by transpiling its caller ComputeDerivatives and swapping one call operand, which is correct whether or not the JIT inlined the AggressiveInlining callee
category: patching
group: internals
topic: patch-around-aggressive-inlining
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksaero/features/computedrag-call-site-transpiler]
techniques: [harmony-transpiler, call-site-rewrite]
hooks: [harmony-transpiler KSA.PhysicsStates.ComputeDerivatives]
lifecycle: [startup:all-mods-loaded, physics:vehicle-solver]
game_types: [KSA.PhysicsStates, KSA.PhysicsEnvironment]
tags: [harmony, transpiler, inlining, aggressiveinlining, call-site, physics-worker]
---

`PhysicsStates.ComputeDrag` is `AggressiveInlining`, so a prefix on it is not a dependable way to change drag. KSAero instead transpiles the one method that calls it, `ComputeDerivatives`, and changes the operand of a
single `call` instruction to point at its own method with the same signature. The decision does not depend on whether the JIT inlined anything: the caller's new body calls the replacement.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by KSAero in [ComputeDrag call-site transpiler](../../../../mods/ksaero/features/computedrag-call-site-transpiler.md).

## Background: how the game does it

`ComputeDrag` is private, static, and marked for aggressive inlining:

```csharp
// current/decomp/KSA/PhysicsStates.cs L1089-L1090
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private static void ComputeDrag(in BubbleOrigin origin, in KinematicStates kinematic, in VehicleProperties props, in PhysicsEnvironment environment, ref Disturbances disturbances, double3 airVelocityBub, double density, double dragCdA, double surfaceArea, double dt)
```

Source: [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1089-L1090)[^ksa-physicsstates]

`ComputeDerivatives` is its only caller, and calls it twice inside the `InPhysicsRadius` branch: once for the ocean and once for the air:

```csharp
// current/decomp/KSA/PhysicsStates.cs L1000-L1016
double3 velocityDirBody = double8.NormalizeOrZero().Transform(kinematic.Body2Phys.Inverse());
double num6 = props.AerodynamicCdABody.ComputeCdA(velocityDirBody);
if (environment.OceanVolume > 0.0 || environment.OceanSurfaceArea > 0.0)
{
	ComputeBuoyancy(in kinematic, ref disturbances2, gravitationBub, environment.OceanDensity, environment.OceanVolume);
	ComputeDrag(in origin, in kinematic, in props, in environment, ref disturbances2, double8, environment.OceanDensity, num6, environment.OceanSurfaceArea, dt);
}
if (environment.AtmosphericDensity > 0f && num6 > 0.0)
{
	double num7 = Math.Max(0.0, (double)props.TotalVolume - environment.OceanVolume);
	double num8 = Math.Max(0.0, (double)props.TotalSurfaceArea - environment.OceanSurfaceArea);
	if (num8 > 0.0 || num7 > 0.0)
	{
		ComputeDrag(in origin, in kinematic, in props, in environment, ref disturbances2, double8, environment.AtmosphericDensity, num6, num8, dt);
		ComputeBuoyancy(in kinematic, ref disturbances2, gravitationBub, environment.AtmosphericDensity, num7);
	}
}
```

Source: [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1000-L1016)

A prefix on `ComputeDrag` would change ocean drag too and could be bypassed; rewriting only the atmospheric call site is both selective and independent of the inlining decision.

## Lifecycle and timing

Install at `startup:all-mods-loaded`, on the main thread, before any vehicle step is dispatched, so no worker is inside the method while it is replaced. The rewritten call runs in `physics:vehicle-solver` on vehicle
worker threads, once per vehicle per substep. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Run code inside the physics step on worker threads](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md).

## Steps

1. **Establish what is marked and who calls it.** The attribute is on `ComputeDrag` ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1089-L1090)); a search of the decompile finds two call sites, both in `ComputeDerivatives`. If the method
   you care about has one caller, you need one transpiler; the caller itself is not marked.

2. **Resolve the caller and the callee exactly.** Both come from explicit parameter lists ([Explicit parameter arrays with MakeByRefType for overloaded and by-reference targets](../patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-3.md)), and the install throws if any lookup is null:

```csharp
// KSAero/AirForcePatch.cs L32-L59
private static readonly MethodInfo? ComputeDerivatives = AccessTools.Method(typeof(PhysicsStates), "ComputeDerivatives", new[]
{
    typeof(BubbleOrigin).MakeByRefType(), typeof(KinematicStates).MakeByRefType(),
    typeof(VehicleProperties).MakeByRefType(), typeof(PhysicsEnvironment).MakeByRefType(),
    typeof(double), typeof(double), typeof(double3), typeof(double3),
    typeof(ReadOnlySpan<ActiveNozzle>), typeof(ReadOnlySpan<ActiveChute>),
});

private static readonly MethodInfo? ComputeDrag = AccessTools.Method(typeof(PhysicsStates), "ComputeDrag", new[]
{
    typeof(BubbleOrigin).MakeByRefType(), typeof(KinematicStates).MakeByRefType(),
    typeof(VehicleProperties).MakeByRefType(), typeof(PhysicsEnvironment).MakeByRefType(),
    typeof(Disturbances).MakeByRefType(), typeof(double3),
    typeof(double), typeof(double), typeof(double), typeof(double),
});

private static readonly FieldInfo? AtmosphericDensity = AccessTools.Field(typeof(PhysicsEnvironment), nameof(PhysicsEnvironment.AtmosphericDensity));

private static ComputeDragFn? _stockDrag;

internal static void Apply(Harmony harmony)
{
    if (ComputeDerivatives == null || ComputeDrag == null || AtmosphericDensity == null)
        throw new MissingMemberException("PhysicsStates.ComputeDerivatives, PhysicsStates.ComputeDrag or PhysicsEnvironment.AtmosphericDensity not found.");

    _stockDrag = AccessTools.MethodDelegate<ComputeDragFn>(ComputeDrag);
    harmony.Patch(ComputeDerivatives, transpiler: new HarmonyMethod(typeof(AirForcePatch), nameof(Transpiler)));
}
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L32-L59)[^ksaero-airforcepatch]

3. **Find the call by what its arguments load, and swap only its operand.** The transpiler tests every instruction with `Calls(ComputeDrag)` and scans back for the density field load; the match changes
   `operand`, not the opcode, so the evaluation stack is unchanged:

```csharp
// KSAero/AirForcePatch.cs L61-L93
private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
{
    List<CodeInstruction> code = instructions.ToList();
    MethodInfo replacement = AccessTools.Method(typeof(AirForcePatch), nameof(ComputeAirForces));
    int replaced = 0;

    for (int i = 0; i < code.Count; i++)
    {
        if (code[i].Calls(ComputeDrag!) && PassesAtmosphericDensity(code, i))
        {
            code[i].operand = replacement;
            replaced++;
        }
    }

    // Anything else means the method has changed shape, and stock aero is the safe answer.
    if (replaced != 1)
        throw new InvalidOperationException($"Expected one atmospheric ComputeDrag call in ComputeDerivatives, found {replaced}.");

    return code;
}

private static bool PassesAtmosphericDensity(List<CodeInstruction> code, int callIndex)
{
    for (int j = callIndex - 1; j >= Math.Max(0, callIndex - ArgumentWindow); j--)
    {
        if (code[j].LoadsField(AtmosphericDensity!))
            return true;
        if (code[j].opcode == OpCodes.Call || code[j].opcode == OpCodes.Callvirt)
            return false;
    }
    return false;
}
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L61-L93)

   How the scan identifies the right call and why it stops at a call boundary: [Scan backwards through the argument loads to the previous call](../../transpilers/identify-a-call-by-its-argument-il/identify-a-call-by-its-argument-il-1.md). Refusing unless exactly one call matched: [Throw on a bad match, UnpatchAll(own id), publish the instance on success](../../safety/refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-1.md).

4. **Give the replacement the identical signature.** Same parameter types, `in`/`ref` modifiers and return type, so it drops into the call site as is:

```csharp
// KSAero/AirForcePatch.cs L95-L108
/// <summary>
/// Same signature as stock ComputeDrag, so it drops into the call site as is. Lift and drag act
/// through the centre of mass, and the rotational damping is stock's, unchanged.
/// </summary>
internal static void ComputeAirForces(in BubbleOrigin origin, in KinematicStates kinematic, in VehicleProperties props,
    in PhysicsEnvironment environment, ref Disturbances disturbances, double3 airVelocityBub, double density,
    double dragCdA, double surfaceArea, double dt)
{
    AeroBody body = BodyFor(in props);
    if (!body.IsValid)
    {
        _stockDrag!(in origin, in kinematic, in props, in environment, ref disturbances, airVelocityBub, density, dragCdA, surfaceArea, dt);
        return;
    }
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L95-L108)

## Complete example

| File | Role |
|---|---|
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L28-L93) | Resolve, apply, transpiler |

## Variations and gotchas

- **The rewrite is correct both ways.** If the JIT inlines the stock `ComputeDrag` into the old `ComputeDerivatives`, that body is no longer used; if it does not, the call goes to the replacement. The local test in [Changing behaviour of a game method marked AggressiveInlining](overview.md)
  shows a prefix on the callee being skipped for a caller compiled with optimizations before the patch, which this approach avoids by construction.
- **What is not covered.** The caller must not itself be inlined into something you can no longer patch; `ComputeDerivatives` is not marked and is large, so that is not the case in 5554.
- **Debug build.** `KSA.dll` in 5554 has `DisableOptimizations`; the KSAero authors note that the stock method may then not inline `ComputeDrag` at all, an observation about this build, not verified in the running game.
- **Another transpiler first.** Harmony re-runs all transpilers on the original IL whenever any mod patches the method; a transpiler that rewrites the same call before KSAero leaves it with zero matches and the install refuses ([Coexisting with other mods that patch the same method](../../harmony/coexist-with-other-mods-patching-the-same-method/overview.md)).
- **Selective by design.** The ocean call keeps stock drag; only the call whose arguments load `AtmosphericDensity` is replaced.

## See also

- [Patch the non-inlined entry points that reach the inlined method too](patch-around-aggressive-inlining-2.md), [Postfix the single non-inlined outer method instead of a per-vehicle callee](patch-around-aggressive-inlining-3.md), [Bracket the non-inlined methods that enclose an inlined struct callback](patch-around-aggressive-inlining-4.md)
- [Changing behaviour of a game method marked AggressiveInlining](overview.md)
- [ComputeDrag call-site transpiler](../../../../mods/ksaero/features/computedrag-call-site-transpiler.md)
- [Same-signature static replacement, operand-only edit](../../transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-1.md)
- [AccessTools.MethodDelegate with a delegate type that mirrors in and ref modifiers](../call-private-game-members-through-reflection-and-delegates/call-private-game-members-through-reflection-and-delegates-4.md) for the delegate that calls stock `ComputeDrag` as a fallback
- Related topic: replace-the-stock-aerodynamic-force-model

[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksaero-airforcepatch]: KSAero/AirForcePatch.cs
