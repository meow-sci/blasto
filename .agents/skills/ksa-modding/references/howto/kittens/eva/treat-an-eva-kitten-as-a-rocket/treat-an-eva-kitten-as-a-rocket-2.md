---
type: How-To
title: Call the base Vehicle handlers through a Harmony reverse patch onto a static stub
description: Iron Man replaces KittenEva.OnKey and ProcessInput with prefixes that call the vessel implementations through static stubs that Harmony reverse-patches with a copy of the base Vehicle method bodies, because a cast to Vehicle would dispatch virtually back into the patched override
category: kittens
group: eva
topic: treat-an-eva-kitten-as-a-rocket
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/iron-man-kitten-rocket-flight]
techniques: [harmony-reverse-patch, harmony-prefix]
hooks:
  - harmony-reverse-patch KSA.Vehicle.OnKey
  - harmony-reverse-patch KSA.Vehicle.ProcessInput
  - harmony-prefix KSA.KittenEva.OnKey
  - harmony-prefix KSA.KittenEva.ProcessInput
lifecycle: [frame:input]
game_types: [KSA.KittenEva, KSA.Vehicle, KSA.InputEvents]
tags: [kitten, harmony, reverse-patch, base-call, virtual-override, input]
---
`KittenEva` is a `Vehicle` that overrides `OnKey` and `ProcessInput` so the keyboard drives EVA actions. To make an enabled kitten respond to the *vessel* keys you need to call the base implementations on a kitten instance. In C#, `((Vehicle)kitten).OnKey(e)` is a **virtual** call: it dispatches straight back to `KittenEva.OnKey`, which your own prefix has replaced. This article shows the first of two ways around that: a Harmony **reverse patch** that copies the base method body into a static stub you can call. The second way, which also keeps other mods' patches on the base running, is [approach 3](treat-an-eva-kitten-as-a-rocket-3.md). Prerequisites: Harmony and an understanding of virtual dispatch ([skip the original with a prefix](../../../patching/harmony/skip-the-original-method-with-a-prefix/overview.md)).

## Background: how the game does it

- `Vehicle.OnKey(GlfwKeyEvent)` and `Vehicle.ProcessInput(InputAction, GlfwKeyAction, GlfwModifier)` are `public virtual`; `KittenEva` overrides both ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3274), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3536), [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L110), [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L187)).
- `OnKey` is called from `Program.DispatchKeyEvent` on the controlled vehicle during `Glfw.PollEvents`; `ProcessInput` from `InputEvents.VehicleInputData.Apply` in `ApplyInputEvents` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1992-L2000)). Both run on the main thread in the input phase.
- A Harmony *reverse patch* (`CreateReversePatcher(original, standin)`) rewrites a stub method so its body is a copy of the original's (with the original's transpilers applied if requested), turning the original into something you can call non-virtually as a static.

## Lifecycle and timing

Installed once at mod start (the patches are inactive until a kitten is enabled). The prefixes run on the input phase (`frame:input`), main thread. Because the stub contains a *copy* of the base body, other mods' prefixes and postfixes on `Vehicle.OnKey` / `Vehicle.ProcessInput` are **not** in the copy; they would still run for non-kitten vehicles but are bypassed on the stub path. That is acceptable when no other mod patches these two base methods, and it is the reason [approach 3](treat-an-eva-kitten-as-a-rocket-3.md) exists ([game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#input-timing)).

## Steps

1. **Declare static stubs with the base signatures** plus the instance as the first parameter. They throw if the reverse patch was not installed; mark them `NoInlining` so the JIT does not inline the throw:

```csharp
// iron-man.lib/IronManFlightPatches.cs L125-L131
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool VehicleOnKey(Vehicle vehicle, GlfwKeyEvent keyEvent) =>
        throw new InvalidOperationException("iron-man: Vehicle.OnKey reverse patch was not installed");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VehicleProcessInput(Vehicle vehicle, InputAction action, GlfwKeyAction keyAction, GlfwModifier modifiers) =>
        throw new InvalidOperationException("iron-man: Vehicle.ProcessInput reverse patch was not installed");
```

2. **Create the reverse patches** against the exact base methods (looked up with parameter types, failing with `MissingMethodException`), then patch the kitten overrides and the worker snapshot:

```csharp
// iron-man.lib/IronManFlightPatches.cs L18-L49
    public static void Apply(Harmony harmony)
    {
        if (_applied) return;
        try
        {
            // Reverse patches preserve the nonvirtual Vehicle implementation. Casting an EVA
            // to Vehicle and calling OnKey/ProcessInput would dispatch back into these prefixes.
            harmony.CreateReversePatcher(
                RequiredMethod(typeof(Vehicle), nameof(Vehicle.OnKey), typeof(GlfwKeyEvent)),
                new HarmonyMethod(RequiredMethod(typeof(IronManFlightPatches), nameof(VehicleOnKey))))
                .Patch();
            harmony.CreateReversePatcher(
                RequiredMethod(typeof(Vehicle), nameof(Vehicle.ProcessInput), typeof(InputAction), typeof(GlfwKeyAction), typeof(GlfwModifier)),
                new HarmonyMethod(RequiredMethod(typeof(IronManFlightPatches), nameof(VehicleProcessInput))))
                .Patch();

            harmony.Patch(RequiredMethod(typeof(VehicleUpdateState), nameof(VehicleUpdateState.PrepareFromVehicle)),
                postfix: new HarmonyMethod(typeof(IronManFlightPatches), nameof(PreparePostfix)));
            harmony.Patch(RequiredMethod(typeof(KittenEva), nameof(KittenEva.OnKey), typeof(GlfwKeyEvent)),
                prefix: new HarmonyMethod(typeof(IronManFlightPatches), nameof(OnKeyPrefix)));
            harmony.Patch(RequiredMethod(typeof(KittenEva), nameof(KittenEva.ProcessInput), typeof(InputAction), typeof(GlfwKeyAction), typeof(GlfwModifier)),
                prefix: new HarmonyMethod(typeof(IronManFlightPatches), nameof(ProcessInputPrefix)));
            IronManRenderPatches.Apply(harmony);
            _applied = true;
            Console.WriteLine("iron-man: flight patches applied (inactive until an EVA is enabled)");
        }
        catch
        {
            Remove(harmony);
            throw;
        }
    }
```

3. **Call the stubs from prefixes that skip the original.** For a non-enabled kitten the prefix returns `true` and the stock EVA handler runs; for an enabled one the stub (the vessel base) runs, any exception is logged and the prefix returns `false`. After a partially queued input action do not run a second handler, so on failure `__result` is `true` (consumed) for keys:

```csharp
// iron-man.lib/IronManFlightPatches.cs L95-L123
    private static bool OnKeyPrefix(KittenEva __instance, GlfwKeyEvent keyEvent, ref bool __result)
    {
        if (!IsEnabled(__instance)) return true;
        try
        {
            __result = VehicleOnKey(__instance, keyEvent);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"iron-man: vessel key handling failed: {ex.Message}");
            // Do not run a second handler after a partially queued input action.
            __result = true;
        }
        return false;
    }

    private static bool ProcessInputPrefix(KittenEva __instance, InputAction action, GlfwKeyAction keyAction, GlfwModifier modifiers)
    {
        if (!IsEnabled(__instance)) return true;
        try
        {
            VehicleProcessInput(__instance, action, keyAction, modifiers);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"iron-man: vessel input processing failed: {ex.Message}");
        }
        return false;
    }
```

4. **Unpatch carefully.** `Remove` does not unpatch the reverse patches (the stubs simply stay) and removes the prefixes by name; restoring worker snapshots needs the solver joined first (see [approach 1](treat-an-eva-kitten-as-a-rocket-1.md), step 9).

5. **Check the result.** The managed test fixture asserts that with the mode off the EVA handler runs, and with it on exactly one base handler runs and the EVA handler does not, with no recursion ([iron-man-flight.tests/Program.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man-flight.tests/Program.cs#L40-L50)).

## Complete example

- [iron-man.lib/IronManFlightPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManFlightPatches.cs#L13-L143) - the whole class.
- [iron-man-flight.tests/Program.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man-flight.tests/Program.cs#L6-L76) - the managed fixture that checks both gates.
- Feature page: [Iron Man kitten rocket flight](../../../../mods/unscience/features/iron-man-kitten-rocket-flight.md).

## Variations and gotchas

- **A copy of the base body.** If another mod patches `Vehicle.OnKey` or `ProcessInput`, its changes are not in the stub. Use [approach 3](treat-an-eva-kitten-as-a-rocket-3.md) when other mods may patch the base.
- **Stubs must be `NoInlining` and non-trivial.** A stub the JIT inlines would never be rewritten.
- **A cast does not help.** `((Vehicle)kitten).OnKey(...)` compiles to `callvirt` and lands in `KittenEva.OnKey` (your prefix) again.
- **Reverse patches are applied once** and kept; the unload path leaves them in place.
- **Exceptions.** The prefixes catch everything; a throw in the input path would otherwise skip the frame's input processing.

## See also

- [Fly an EVA kitten as an ordinary rocket vessel](overview.md), [approach 1](treat-an-eva-kitten-as-a-rocket-1.md), [approach 3](treat-an-eva-kitten-as-a-rocket-3.md)
- [Skip the original method with a prefix](../../../patching/harmony/skip-the-original-method-with-a-prefix/overview.md), [Coexist with other mods patching the same method](../../../patching/harmony/coexist-with-other-mods-patching-the-same-method/overview.md)
