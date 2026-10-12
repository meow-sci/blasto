---
type: How-To
title: TargetMethods with per-name AccessTools lookups and the __args array
description: Patch several private static game methods in one class by yielding each AccessTools.Method lookup from TargetMethods and reading the argument you need from the object[] __args, so a prefix needs no knowledge of the parameter types it cannot name
category: patching
group: internals
topic: patch-a-private-game-method-by-name
approach: 5
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ohmystars/features/attitude-mode-handoff-patches]
techniques: [harmony-target-methods, harmony-prefix, args-array]
hooks:
  - harmony-prefix KSA.PhysicsBubble.ApplyMmuAttitudeTarget
  - harmony-prefix KSA.PhysicsBubble.ResetMmuAttitudeTarget
  - harmony-prefix KSA.KittenEva.HandleKittenAction
lifecycle: [startup:all-mods-loaded, physics:vehicle-solver]
game_types: [KSA.PhysicsBubble, KSA.VehicleUpdateState, KSA.KittenEva, KSA.Vehicle]
tags: [harmony, targetmethods, args, private-static, worker-thread, prefix]
---

One patch class can hook several methods if it supplies `TargetMethods()`, and one prefix can serve methods of different signatures if it takes `object[] __args` instead of typed parameters. Oh My Stars uses both for
two private static methods of `PhysicsBubble`, `ApplyMmuAttitudeTarget` and `ResetMmuAttitudeTarget`. A prefix with `__args` also does not need the types of `in` parameters, which a mod cannot always name.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by Oh My Stars in [Attitude-mode hand-off patches](../../../../mods/ohmystars/features/attitude-mode-handoff-patches.md).

## Background: how the game does it

While a kitten's MMU (maneuvering unit) is thrusting, the vehicle worker overwrites the flight computer's attitude target every physics substep, then resets it once when the MMU stops. Both steps are private static
methods of `PhysicsBubble`, called from `AdvanceKittenLocomotion` on a vehicle worker:

```csharp
// current/decomp/KSA/PhysicsBubble.cs L1891
private static void ApplyMmuAttitudeTarget(VehicleUpdateState vehicleState, in LocomotionCommand command, in KittenLocomotionTuning tuning)
```

Source: [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1891)[^ksa-physicsbubble]

```csharp
// current/decomp/KSA/PhysicsBubble.cs L1950-L1951
private static void ResetMmuAttitudeTarget(VehicleUpdateState vehicleState)
{
```

Source: [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1950-L1951)

```csharp
// current/decomp/KSA/PhysicsBubble.cs L1997-L2007
bool flag = vehicleState.LocomotionState.Mode == LocomotionMode.Mmu && vehicleState.IsControlled && !vehicleState.CharacterControlInputs.DirectControl;
bool flag2 = vehicleState.ManualControlInputs.ThrusterCommandFlags.HasTranslate();
if (flag && flag2)
{
	ApplyMmuAttitudeTarget(vehicleState, in vehicleState.LocomotionCommand, in vehicleState.LocomotionTuning);
}
else if (!flag && vehicleState.LocomotionState.MmuAttitudeApplied)
{
	ResetMmuAttitudeTarget(vehicleState);
	vehicleState.LocomotionState.MmuAttitudeApplied = false;
}
```

Source: [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1997-L2007)

A third target in the same mod, `KittenEva.HandleKittenAction`, is `protected override` ([current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L218)[^ksa-kitteneva]); the attribute names it by string.

## Lifecycle and timing

Install: `startup:all-mods-loaded`, from `ApplyPatches`, one `CreateClassProcessor(...).Patch()` per class. Run: both MMU methods execute in `physics:vehicle-solver`, on a vehicle worker thread, once per substep
while the MMU drives the kitten (apply) and once when it stops (reset). The prefix therefore touches only data that tolerates a worker ([Patches on planner tasks that run on pooled threads](../../harmony/patch-methods-that-run-on-background-threads/patch-methods-that-run-on-background-threads-1.md)). See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Run code inside the physics step on worker threads](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md).

## Steps

1. **Apply each class individually.** `CreateClassProcessor(typeof(X)).Patch()` applies exactly that class, so one failing class does not stop the next (the `PatchAll` loop would):

```csharp
// src/StarOrientationObserver.cs L9-L19
public static void ApplyPatches(Harmony harmony) {
    harmony.CreateClassProcessor(typeof(PrepareWorkerPatch)).Patch();
    harmony.CreateClassProcessor(typeof(TrackTargetPatch)).Patch();
    harmony.CreateClassProcessor(typeof(RateHoldPatch)).Patch();
    harmony.CreateClassProcessor(typeof(SetNullRotPatch)).Patch();
    harmony.CreateClassProcessor(typeof(SetStabilizationPatch)).Patch();
    harmony.CreateClassProcessor(typeof(SetEnumPatch)).Patch();
    harmony.CreateClassProcessor(typeof(ToggleEnumPatch)).Patch();
    harmony.CreateClassProcessor(typeof(KittenActionPatch)).Patch();
    harmony.CreateClassProcessor(typeof(KittenMmuPatch)).Patch();
}
```

Source: [src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs#L9-L19)[^ohmystars-starorientationobserver]

2. **Declare the class with a bare `[HarmonyPatch]` and yield the targets.** `TargetMethods()` returns every `MethodBase` to patch. The mod looks each up by name and yields only the non-null ones, so a rename of one
   method leaves the other patched:

```csharp
// src/StarOrientationObserver.cs L91-L101
[HarmonyPatch]
private static class KittenMmuPatch {
    private static IEnumerable<MethodBase> TargetMethods() {
        MethodBase? apply = AccessTools.Method(typeof(PhysicsBubble), "ApplyMmuAttitudeTarget");
        if(apply != null)
            yield return apply;

        MethodBase? reset = AccessTools.Method(typeof(PhysicsBubble), "ResetMmuAttitudeTarget");
        if(reset != null)
            yield return reset;
    }
```

Source: [src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs#L91-L101)

3. **Take the arguments you need from `__args`.** `object[] __args` holds every argument boxed, in declaration order. `__args[0]` is the `VehicleUpdateState`; `in` parameters need no mention:

```csharp
// src/StarOrientationObserver.cs L103-L114
private static bool Prefix(object[] __args) {
    try {
        Vehicle? vehicle = (__args?[0] as VehicleUpdateState)?.ReadOnlyVehicle;
        if(vehicle != null) {
            OhMyStarsWindow.DetachOrientedStar(vehicle);
        }
    } catch(Exception ex) {
        DefaultCategory.Log.Warning($"[OhMyStars] KittenMmu Prefix: {ex}");
    }

    return true;
}
```

Source: [src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs#L103-L114)

   The same prefix serves both methods because `ResetMmuAttitudeTarget` also has the `VehicleUpdateState` first. Returning true runs the original.

4. **Name a protected method with a string in the attribute.** `HandleKittenAction` is `protected override`, so the type is `typeof(KittenEva)` and the name a string:

```csharp
// src/StarOrientationObserver.cs L71-L82
[HarmonyPatch(typeof(KittenEva), "HandleKittenAction")]
private static class KittenActionPatch {
    static void Prefix(KittenEva __instance, KittenEvaAction action) {
        try {
            if(action is KittenEvaAction.ManualTrim or KittenEvaAction.RateTrim) {
                OhMyStarsWindow.ClearOrientedStar(__instance.FlightComputer);
            }
        } catch(Exception ex) {
            DefaultCategory.Log.Warning($"[OhMyStars] KittenAction Prefix: {ex}");
        }
    }
}
```

Source: [src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs#L71-L82)

5. **Know what a missing target does.** A local test with Lib.Harmony 2.4.2 shows both behaviours of `TargetMethods`:

```text
// observed output of a local test against Lib.Harmony 2.4.2 (not part of any mod repository)
empty TargetMethods: HarmonyException: Patching exception in method null | inner: Undefined target method for patch method static System.Boolean RenamedTargets::Prefix(System.Object[] __args)
  prefix: __args[0]=veh __args.Length=3 __args[1]=Cmd
  original Apply
```

   A class whose `TargetMethods` yields nothing throws at patch time, so the skip-nulls pattern only protects a class with at least one surviving target. The second line shows `__args` boxing a three-parameter
   method including an `in` struct. Wrap `CreateClassProcessor(...).Patch()` in a `try`/`catch` per class if the feature is optional ([CreateClassProcessor per patch class, required versus optional](../../safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-1.md)); Oh My Stars does not.

## Complete example

| File | Role |
|---|---|
| [src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs#L9-L19) | Per-class apply |
| [src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs#L91-L115) | `TargetMethods` and the `__args` prefix |

## Variations and gotchas

- **`__0` instead of `__args`.** A positional `VehicleUpdateState __0` works when the parameter type is nameable; a local test confirmed `__0` binds by position. `__args` is the choice when you cannot name the
  types or want one prefix over differing signatures.
- **Mutate the object, not the array slot.** The prefix above only reads `__args[0]` and acts on the `Vehicle` it exposes; it does not rely on writing back through the array.
- **The target runs on a worker.** `ApplyMmuAttitudeTarget` runs per substep on a vehicle worker, so mutating plain collections from the prefix races the main thread ([Patching game methods that run on worker and pool threads](../../harmony/patch-methods-that-run-on-background-threads/overview.md)).
- **Two overloads of one name** make `AccessTools.Method(type, name)` throw `AmbiguousMatchException`; pass parameter types ([Explicit parameter arrays with MakeByRefType for overloaded and by-reference targets](patch-a-private-game-method-by-name-3.md)).
- **Unpatch.** Oh My Stars has no `[StarMapUnload]`; StarMap's id-less `UnpatchAll()` removes the patches at exit ([UnpatchAll(ownId) from a main-thread hook after nulling delegates](../../safety/remove-your-patches-safely-at-runtime-and-at-exit/remove-your-patches-safely-at-runtime-and-at-exit-1.md)).

## See also

- [HarmonyPatch attribute with a string method name, applied by PatchAll](patch-a-private-game-method-by-name-1.md), [Manual AccessTools lookup with a degrade path when the method is gone](patch-a-private-game-method-by-name-2.md), [Explicit parameter arrays with MakeByRefType for overloaded and by-reference targets](patch-a-private-game-method-by-name-3.md), [Attribute argument types that bind by assignability](patch-a-private-game-method-by-name-4.md), [Reading a private instance field with a triple-underscore parameter](patch-a-private-game-method-by-name-6.md), [Prefer __instance and a public member over a field injector that matches nothing](patch-a-private-game-method-by-name-7.md)
- [Patching private and overloaded game methods by name](overview.md)
- [Attitude-mode hand-off patches](../../../../mods/ohmystars/features/attitude-mode-handoff-patches.md)
- [Patch the non-inlined entry points that reach the inlined method too](../patch-around-aggressive-inlining/patch-around-aggressive-inlining-2.md) for the inlined flight-computer methods the same mod patches

[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-kitteneva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
[^ohmystars-starorientationobserver]: OhMyStars/src/StarOrientationObserver.cs
