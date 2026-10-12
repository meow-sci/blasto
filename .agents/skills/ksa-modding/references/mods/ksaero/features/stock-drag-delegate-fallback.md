---
type: Mod Feature
title: Stock ComputeDrag delegate fallback
description: A typed delegate with matching in and ref parameters, created once with AccessTools.MethodDelegate, lets the replacement force function call the private stock PhysicsStates.ComputeDrag whenever it cannot model the vehicle
mod: ksaero
category: patching
techniques: [reflection, harmony-transpiler, method-delegate]
hooks: [reflection KSA.PhysicsStates.ComputeDrag, harmony-transpiler KSA.PhysicsStates.ComputeDerivatives]
lifecycle: [startup:all-mods-loaded, physics:vehicle-solver]
game_types: [KSA.PhysicsStates, KSA.VehicleProperties]
tags: [harmony, accesstools, delegate, private-method, fallback]
---

`PhysicsStates.ComputeDrag` is `private static`, so a mod cannot call it directly. KSAero still needs
it: when the vehicle's bounding box cannot be turned into a valid cylinder, the replacement hands the
call back to stock drag instead of applying no air force at all. It does this through a delegate built
once at install time, so the hot path is a plain delegate invocation with no reflection.[^ksaero-airforcepatch]

## How it works

1. **Declare a delegate type that mirrors the target exactly**, including the parameter modifiers:
   four `in` structs, one `ref Disturbances`, then values
   ([KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L28-L30)). The stock signature is in
   [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1089-L1090).[^ksa-physicsstates]
2. **Resolve the private method** with `AccessTools.Method` and an explicit parameter list
   ([KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L40-L46)).
3. **Bind once.** `Apply` stores `AccessTools.MethodDelegate<ComputeDragFn>(ComputeDrag)` in a static
   field before installing the transpiler
   ([KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L57-L58)). For a static method the delegate
   is unbound; Harmony's `instance` and `virtualCall` arguments only apply to instance methods.
4. **Fall back in the replacement.** `ComputeAirForces` builds an `AeroBody` from
   `VehicleProperties.BoundingBoxAsmb`; if `AeroBody.IsValid` is false (length or diameter zero,
   negative or not finite) it forwards every argument unchanged to `_stockDrag` and returns
   ([KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L103-L108),
   [KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs#L35)).[^ksaero-aeromodel]

Because KSAero never patches `ComputeDrag` itself (only one call site of it), the delegate executes the
unmodified stock body: the fallback produces exactly what the game would have produced at that call.

## Lifecycle and timing

- **Created at `startup:all-mods-loaded`** on the main thread inside `AirForcePatch.Apply`, before
  `harmony.Patch` runs. The field is therefore set before any code path that reads it exists.
- **Invoked in `physics:vehicle-solver`** on vehicle worker threads, possibly from several threads at
  once. The delegate is immutable and the target is a pure static function of its arguments, so
  concurrent invocation is safe and allocation-free. See
  [Body-of-revolution aerodynamics](body-of-revolution-aerodynamics.md#lifecycle-and-timing) for the
  call chain.
- When the fallback triggers in practice: `Vehicle` sets `BoundingBoxAsmb` from collider bounds or
  part bounds and substitutes a 2 m box when both corners are zero
  ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1950-L1972)), so an invalid
  cylinder needs a degenerate box (a zero extent on some axis).[^ksa-vehicle]

## Key code

| Path | Role |
|---|---|
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L28-L30) | `ComputeDragFn` delegate type with `in`/`ref` parameters |
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L40-L50) | Private method lookup and the cached delegate field |
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L57) | `AccessTools.MethodDelegate` |
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L103-L108) | Fallback call |

## Snippets

```csharp
// KSAero/AirForcePatch.cs L28-L30
    private delegate void ComputeDragFn(in BubbleOrigin origin, in KinematicStates kinematic, in VehicleProperties props,
        in PhysicsEnvironment environment, ref Disturbances disturbances, double3 airVelocityBub, double density,
        double dragCdA, double surfaceArea, double dt);
```

```csharp
// KSAero/AirForcePatch.cs L99-L108
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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `PhysicsStates.ComputeDrag` | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1089-L1111) | Private stock drag, called through the delegate |
| `VehicleProperties.BoundingBoxAsmb` | [current/decomp/KSA/VehicleProperties.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleProperties.cs#L24) | Geometry whose validity decides the fallback |

## Pitfalls and version notes

- **Modifiers must match.** A delegate declared with plain value parameters where the method takes
  `in` or `ref` fails to bind. `in` and `ref` are both by-reference in metadata.
- **Do not reflect per call.** `MethodInfo.Invoke` on a worker thread every substep would allocate an
  argument array and box every struct; the cached delegate avoids both.
- **Calling a method you also patch.** KSAero can rely on the delegate reaching stock code because it
  does not patch `ComputeDrag`. If you patch a method and also want its original body, use a Harmony
  reverse patch instead; a delegate to a patched method calls the patched version.
- **Signature drift.** If a game update changes `ComputeDrag`'s parameters, the `AccessTools.Method`
  lookup returns null and `Apply` throws before patching (see
  [Patch refusal and rollback](patch-refusal-and-rollback.md)).

## See also

- [ComputeDrag call-site transpiler](computedrag-call-site-transpiler.md)
- [Body-of-revolution aerodynamics](body-of-revolution-aerodynamics.md)
- [Patch refusal and rollback](patch-refusal-and-rollback.md)
- [AccessTools.MethodDelegate with a delegate type that mirrors in and ref modifiers](../../../howto/patching/internals/call-private-game-members-through-reflection-and-delegates/call-private-game-members-through-reflection-and-delegates-4.md) - how-to article demonstrating this feature
- [Call-site transpiler on ComputeDerivatives writing the Disturbances accumulator](../../../howto/physics/aero-and-ground/replace-the-stock-aerodynamic-force-model/replace-the-stock-aerodynamic-force-model-1.md) - how-to article demonstrating this feature
- [Pure, allocation-free, never-throwing code over the in arguments](../../../howto/physics/solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-1.md) - how-to article demonstrating this feature

[^ksaero-airforcepatch]: KSAero/AirForcePatch.cs
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksaero-aeromodel]: KSAero/AeroModel.cs
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
