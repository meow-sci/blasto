---
type: How-To
title: AccessTools.MethodDelegate with a delegate type that mirrors in and ref modifiers
description: Call the private static AggressiveInlining PhysicsStates.ComputeDrag from worker threads through an immutable delegate whose type repeats the in and ref modifiers, resolved with an explicit parameter array and bound before the transpiler is installed
category: patching
group: internals
topic: call-private-game-members-through-reflection-and-delegates
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksaero/features/stock-drag-delegate-fallback]
techniques: [method-delegate, reflection]
hooks: [reflection KSA.PhysicsStates.ComputeDrag]
lifecycle: [startup:all-mods-loaded, physics:vehicle-solver]
game_types: [KSA.PhysicsStates, KSA.PhysicsEnvironment, KSA.Disturbances]
tags: [reflection, accesstools, methoddelegate, in-ref, delegate, worker-thread]
---

`PhysicsStates.ComputeDrag` is `private static`, so a mod cannot call it. KSAero still needs it: when a vehicle's bounding box cannot be turned into a valid cylinder, its replacement drag hands the call
back to stock instead of applying no air force at all. It does this through a delegate built once at install time, so the hot path is a plain delegate invocation with no reflection and no per-call
allocation, from vehicle worker threads. The delegate type must mirror the target's parameter modifiers exactly.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by KSAero in [Stock ComputeDrag delegate fallback](../../../../mods/ksaero/features/stock-drag-delegate-fallback.md).

## Background: how the game does it

The target, with four `in` structs, one `ref Disturbances` and then values; it is `AggressiveInlining`:

```csharp
// current/decomp/KSA/PhysicsStates.cs L1089-L1090
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private static void ComputeDrag(in BubbleOrigin origin, in KinematicStates kinematic, in VehicleProperties props, in PhysicsEnvironment environment, ref Disturbances disturbances, double3 airVelocityBub, double density, double dragCdA, double surfaceArea, double dt)
```

Source: [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1089-L1090)[^ksa-physicsstates]

Because KSAero never patches `ComputeDrag` itself (it rewrites one call *site*, [Same-signature static replacement, operand-only edit](../../transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-1.md)), a delegate to it runs the unmodified stock body.

## Lifecycle and timing

- **Created at `startup:all-mods-loaded`,** on the main thread, inside `AirForcePatch.Apply`, before `harmony.Patch` runs; the field is set before any code path that reads it exists.
- **Invoked in `physics:vehicle-solver`,** on vehicle worker threads, possibly several at once. The delegate is immutable and the target is a pure static function of its arguments, so concurrent
  invocation is safe and allocation-free. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Run code inside the physics step on worker threads](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md).
- The fallback triggers when `BoundingBoxAsmb` yields a degenerate cylinder (a zero extent on some axis); `Vehicle` substitutes a 2 m box when both corners are zero.

## Steps

1. **Declare a delegate type that mirrors the target exactly,** including `in`/`ref`:

```csharp
// KSAero/AirForcePatch.cs L28-L30
private delegate void ComputeDragFn(in BubbleOrigin origin, in KinematicStates kinematic, in VehicleProperties props,
    in PhysicsEnvironment environment, ref Disturbances disturbances, double3 airVelocityBub, double density,
    double dragCdA, double surfaceArea, double dt);
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L28-L30)[^ksaero-airforcepatch]

2. **Resolve the private method with an explicit parameter array** and `MakeByRefType()` for every by-reference parameter. The explicit list also prevents a later overload with the same name from being picked:

```csharp
// KSAero/AirForcePatch.cs L40-L46
private static readonly MethodInfo? ComputeDrag = AccessTools.Method(typeof(PhysicsStates), "ComputeDrag", new[]
{
    typeof(BubbleOrigin).MakeByRefType(), typeof(KinematicStates).MakeByRefType(),
    typeof(VehicleProperties).MakeByRefType(), typeof(PhysicsEnvironment).MakeByRefType(),
    typeof(Disturbances).MakeByRefType(), typeof(double3),
    typeof(double), typeof(double), typeof(double), typeof(double),
});
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L40-L46)

3. **Bind once, before installing anything that could call it.** `AccessTools.MethodDelegate<ComputeDragFn>(ComputeDrag)` for a static method is unbound; `instance` and `virtualCall` arguments apply only to instance methods.
   Throw if the lookup is null so a missing method never becomes a null delegate:

```csharp
// KSAero/AirForcePatch.cs L50-L59
private static ComputeDragFn? _stockDrag;

internal static void Apply(Harmony harmony)
{
    if (ComputeDerivatives == null || ComputeDrag == null || AtmosphericDensity == null)
        throw new MissingMemberException("PhysicsStates.ComputeDerivatives, PhysicsStates.ComputeDrag or PhysicsEnvironment.AtmosphericDensity not found.");

    _stockDrag = AccessTools.MethodDelegate<ComputeDragFn>(ComputeDrag);
    harmony.Patch(ComputeDerivatives, transpiler: new HarmonyMethod(typeof(AirForcePatch), nameof(Transpiler)));
}
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L50-L59)

4. **Invoke it with the same modifiers.** The replacement forwards every argument unchanged:

```csharp
// KSAero/AirForcePatch.cs L102-L108
{
    AeroBody body = BodyFor(in props);
    if (!body.IsValid)
    {
        _stockDrag!(in origin, in kinematic, in props, in environment, ref disturbances, airVelocityBub, density, dragCdA, surfaceArea, dt);
        return;
    }
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L102-L108)

## Complete example

| File | Role |
|---|---|
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs) | Delegate type, lookup, bind, fallback call |
| [KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs#L35)[^ksaero-aeromodel] | `AeroBody.IsValid`, the condition that triggers the fallback |

## Variations and gotchas

- **Modifiers must match.** A delegate declared with plain value parameters where the method takes `in` or `ref` fails to bind. `in` and `ref` are both by-reference in metadata.
- **Do not reflect per call.** `MethodInfo.Invoke` on a worker every substep would allocate an argument array and box every struct; the cached delegate avoids both.
- **A delegate to a patched method calls the patched version.** KSAero can rely on the delegate reaching stock code because it does not patch `ComputeDrag`. If you patch a method and also want its original
  body, use a Harmony reverse patch instead.
- **Signature drift.** If a game update changes `ComputeDrag`'s parameters the lookup returns null and `Apply` throws before patching ([Throw on a bad match, UnpatchAll(own id), publish the instance on success](../../safety/refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-1.md)).
- **Inlining.** The delegate calls the method body directly; whether the JIT inlines `ComputeDrag` into `ComputeDerivatives` does not matter to it.

## See also

- [AccessTools static field refs bound once, replay stock's writes](call-private-game-members-through-reflection-and-delegates-1.md), [Plain GetField on internal registries and a generic holder for instance fields](call-private-game-members-through-reflection-and-delegates-2.md), [Typed FieldRefAccess, boxed private enum members and by-ref lookups](call-private-game-members-through-reflection-and-delegates-3.md), [Delegates over private editor helpers reused in a replacement drag](call-private-game-members-through-reflection-and-delegates-5.md), [UnsafeAccessor StaticField on the k__BackingField with UnsafeAccessorType](call-private-game-members-through-reflection-and-delegates-6.md), [Copied injector in a consumer mod with the same accessor](call-private-game-members-through-reflection-and-delegates-7.md)
- [Reading, writing and calling private game members](overview.md)
- [Stock ComputeDrag delegate fallback](../../../../mods/ksaero/features/stock-drag-delegate-fallback.md)
- [Same-signature static replacement, operand-only edit](../../transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-1.md), [Rewrite the one caller's call site instead of the inlined callee](../patch-around-aggressive-inlining/patch-around-aggressive-inlining-1.md)
- Related task: replace-the-stock-aerodynamic-force-model

[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksaero-airforcepatch]: KSAero/AirForcePatch.cs
[^ksaero-aeromodel]: KSAero/AeroModel.cs
