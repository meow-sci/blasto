---
type: How-To
title: Explicit parameter arrays with MakeByRefType for overloaded and by-reference targets
description: Pick one overload out of several same-named game methods by passing the full parameter list to AccessTools.Method, writing every in, ref and out parameter as Type.MakeByRefType(), so the lookup is neither ambiguous nor fooled by a later overload
category: patching
group: internals
topic: patch-a-private-game-method-by-name
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksaero/features/computedrag-call-site-transpiler
  - mods/ksa-advancedflightcomputer/features/validated-reflection-handles
techniques: [accesstools-lookup, parameter-array, byref-types]
hooks: [reflection KSA.PhysicsStates.ComputeDerivatives, reflection KSA.PatchedConic.FindClosestApproaches]
lifecycle: [startup:all-mods-loaded, physics:vehicle-solver]
game_types: [KSA.PhysicsStates, KSA.ReadOnlyPhysicsStates, KSA.PatchedConic]
tags: [harmony, accesstools, overload, byref, in-parameter, ambiguous-match]
---

`AccessTools.Method(type, "Name")` finds a method by name only, and fails on overloads. `PhysicsStates.ComputeDerivatives` is the standard example in KSA: the same name is an instance forwarder and a
static ten-parameter body, and the static one takes four `in` structs. KSAero and KSA-AdvancedFlightComputer both resolve such targets with an explicit parameter list, and write every `in`, `ref` and `out`
parameter as `typeof(T).MakeByRefType()`, because that is how reflection sees them.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by KSAero in [ComputeDrag call-site transpiler](../../../../mods/ksaero/features/computedrag-call-site-transpiler.md) and by KSA-AdvancedFlightComputer in [Validated reflection handles with per-feature tags](../../../../mods/ksa-advancedflightcomputer/features/validated-reflection-handles.md).

## Background: how the game does it

`ComputeDerivatives` exists in two forms on `PhysicsStates`: an instance method that forwards to the static one with `in Origin, in Kinematic, in Props, in Environment`, and the static body itself. A third
copy on `ReadOnlyPhysicsStates` forwards to the static body as well:

```csharp
// current/decomp/KSA/PhysicsStates.cs L951-L956
public readonly Disturbances ComputeDerivatives(double baseIntraStepTime, double dt, double3 positionOriginBub, double3 velocityOriginBub, ReadOnlySpan<ActiveNozzle> thrusters, ReadOnlySpan<ActiveChute> chutes)
{
	return ComputeDerivatives(in Origin, in Kinematic, in Props, in Environment, baseIntraStepTime, dt, positionOriginBub, velocityOriginBub, thrusters, chutes);
}

public static Disturbances ComputeDerivatives(in BubbleOrigin origin, in KinematicStates kinematic, in VehicleProperties props, in PhysicsEnvironment environment, double baseIntraStepTime, double dt, double3 positionOriginBub, double3 velocityOriginBub, ReadOnlySpan<ActiveNozzle> thrusters, ReadOnlySpan<ActiveChute> chutes)
```

Source: [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L951-L956)[^ksa-physicsstates]

```csharp
// current/decomp/KSA/ReadOnlyPhysicsStates.cs L116-L119
public Disturbances ComputeDerivatives(double baseIntraStepTime, double dt, double3 positionOriginBub, double3 velocityOriginBub, ReadOnlySpan<ActiveNozzle> thrusters, ReadOnlySpan<ActiveChute> chutes)
{
	return PhysicsStates.ComputeDerivatives(in Origin, in Kinematic, in Props, in Environment, baseIntraStepTime, dt, positionOriginBub, velocityOriginBub, thrusters, chutes);
}
```

Source: [current/decomp/KSA/ReadOnlyPhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ReadOnlyPhysicsStates.cs#L116-L119)[^ksa-readonlyphysicsstates]

The same style appears in the Transfer Planner's neighbour `PatchedConic.FindClosestApproaches`, a private instance method whose last two parameters are a `ref` and an `out`:

```csharp
// current/decomp/KSA/PatchedConic.cs L440
private void FindClosestApproaches(Span<Encounter> encounters, ref int encounterCount, IOrbiter secondBody, ref UniverseTime expiryGameTime, out UniverseTime searchedUntil)
```

Source: [current/decomp/KSA/PatchedConic.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PatchedConic.cs#L440)[^ksa-patchedconic]

## Lifecycle and timing

Install: `startup:all-mods-loaded`, on the main thread; the lookups are `static readonly` fields, resolved when the class is first touched. Use: the method found is patched once at install time, and from
then on runs wherever the game calls it (`physics:vehicle-solver` on vehicle workers for `ComputeDerivatives`). See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Run code inside the physics step on worker threads](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md).

## Steps

1. **List the parameter types in order, by-reference ones as `MakeByRefType()`.** KSAero resolves the static body and, in the same shape, the private `ComputeDrag`:

```csharp
// KSAero/AirForcePatch.cs L28-L46
private delegate void ComputeDragFn(in BubbleOrigin origin, in KinematicStates kinematic, in VehicleProperties props,
    in PhysicsEnvironment environment, ref Disturbances disturbances, double3 airVelocityBub, double density,
    double dragCdA, double surfaceArea, double dt);

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
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L28-L46)[^ksaero-airforcepatch]

   `in`, `ref` and `out` all compile to a by-reference parameter, so each is the same type to reflection. By-value parameters (`double`, `double3`, `ReadOnlySpan<ActiveNozzle>`) are written plainly.

2. **Prove the behaviour once on a throwaway class.** A local test with Lib.Harmony 2.4.2 on a type with an instance forwarder and a static `in` body:

```text
// observed output of a local test (an experiment project, not part of any mod repository)
by name only: AmbiguousMatchException
parameter list with plain S: null (in parameters are by-reference)
parameter list with S.MakeByRefType(): Int32 ComputeDerivatives(S ByRef, Double, Double)
exact parameter list excludes the forwarder: IsStatic=True
```

   Without a list the lookup is ambiguous; with a list of plain value types it returns null; with `MakeByRefType()` it finds exactly the static body.

3. **Fail loudly if any handle is null.** The mod throws before patching so a rename becomes a refused install rather than a half-applied one ([Throw on a bad match, UnpatchAll(own id), publish the instance on success](../../safety/refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-1.md)):

```csharp
// KSAero/AirForcePatch.cs L52-L59
internal static void Apply(Harmony harmony)
{
    if (ComputeDerivatives == null || ComputeDrag == null || AtmosphericDensity == null)
        throw new MissingMemberException("PhysicsStates.ComputeDerivatives, PhysicsStates.ComputeDrag or PhysicsEnvironment.AtmosphericDensity not found.");

    _stockDrag = AccessTools.MethodDelegate<ComputeDragFn>(ComputeDrag);
    harmony.Patch(ComputeDerivatives, transpiler: new HarmonyMethod(typeof(AirForcePatch), nameof(Transpiler)));
}
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L52-L59)

4. **Do the same for by-ref instance methods.** KSA-AdvancedFlightComputer lists all five parameters of `PatchedConic.FindClosestApproaches`, two of them by reference, and tags the handle as a soft anchor so a miss only
   disables its own optional patch:

```csharp
// AdvancedFlightComputer/Core/GameReflection.cs L176-L187
// This guard prevents the stock search from using a second body with a NaN Period.
// The last parameter is the out searchedUntil, which reflection sees as a by-ref type.
[UsedBy(Feature.SoftAnchor)]
public static readonly MethodInfo? PatchedConic_FindClosestApproaches =
    AccessTools.Method(typeof(PatchedConic), "FindClosestApproaches", new[]
    {
        typeof(Span<Encounter>),
        typeof(int).MakeByRefType(),
        typeof(IOrbiter),
        typeof(UniverseTime).MakeByRefType(),
        typeof(UniverseTime).MakeByRefType(),
    });
```

Source: [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L176-L187)[^ksa-advancedflightcomputer-gamereflection]

5. **Patch the body, not a forwarder, when you want every caller.** Both `ComputeDerivatives` forwarders call the static body, so one patch on it covers the instance forwarder and `ReadOnlyPhysicsStates`. A
   patch on a forwarder would miss the other path.

## Complete example

| File | Role |
|---|---|
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L28-L59) | Parameter arrays, null check, apply |
| [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L176-L187) | A by-ref list for an instance method |

## Variations and gotchas

- **A list also guards against later overloads.** A game update that adds a new `ComputeDerivatives(...)` no longer makes a name-only lookup throw `AmbiguousMatchException`, and a list with the old parameters keeps selecting the same method.
- **Generic and span types.** `ReadOnlySpan<T>` is passed as `typeof(ReadOnlySpan<ActiveNozzle>)`; it is a by-value struct.
- **Types that are `internal` or nested.** If a parameter type is not nameable from your assembly, reach it through the parameters of another found member, or by `AccessTools.TypeByName`.
- **Order matters.** The array is positional; a swapped pair of `double` parameters still binds if the types match, so verify against the decompiled signature.
- **`AggressiveInlining` targets.** `ComputeDrag` is `AggressiveInlining`, so a prefix or postfix on it can silently never run; KSAero rewrites the call site in the caller instead ([Rewrite the one caller's call site instead of the inlined callee](../patch-around-aggressive-inlining/patch-around-aggressive-inlining-1.md)).

## See also

- [HarmonyPatch attribute with a string method name, applied by PatchAll](patch-a-private-game-method-by-name-1.md), [Manual AccessTools lookup with a degrade path when the method is gone](patch-a-private-game-method-by-name-2.md), [Attribute argument types that bind by assignability](patch-a-private-game-method-by-name-4.md), [TargetMethods with per-name AccessTools lookups and the __args array](patch-a-private-game-method-by-name-5.md), [Reading a private instance field with a triple-underscore parameter](patch-a-private-game-method-by-name-6.md), [Prefer __instance and a public member over a field injector that matches nothing](patch-a-private-game-method-by-name-7.md)
- [Patching private and overloaded game methods by name](overview.md)
- [ComputeDrag call-site transpiler](../../../../mods/ksaero/features/computedrag-call-site-transpiler.md), [Validated reflection handles with per-feature tags](../../../../mods/ksa-advancedflightcomputer/features/validated-reflection-handles.md)
- [AccessTools.MethodDelegate with a delegate type that mirrors in and ref modifiers](../call-private-game-members-through-reflection-and-delegates/call-private-game-members-through-reflection-and-delegates-4.md) for calling the same private static through a delegate
- [Same-signature static replacement, operand-only edit](../../transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-1.md)

[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-readonlyphysicsstates]: KSA.ReadOnlyPhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-patchedconic]: KSA.PatchedConic (decompiled, build 2026.10.10.5554)
[^ksaero-airforcepatch]: KSAero/AirForcePatch.cs
[^ksa-advancedflightcomputer-gamereflection]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/GameReflection.cs
