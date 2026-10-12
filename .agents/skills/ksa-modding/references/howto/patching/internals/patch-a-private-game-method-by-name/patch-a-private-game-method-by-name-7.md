---
type: How-To
title: Prefer __instance and a public member over a field injector that matches nothing
description: Hook the Orbit and Fly controllers' OnFrame with one prefix that reads the camera from the public Controller.Camera through __instance, after a ___Transform field injector that matched no field made Harmony throw and abort the supermod's remaining patches
category: patching
group: internals
topic: patch-a-private-game-method-by-name
approach: 7
difficulty: beginner
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/camera-controller-override-camera-animations]
techniques: [harmony-prefix, instance-parameter, failure-isolation]
hooks: [harmony-prefix KSA.OrbitController.OnFrame, harmony-prefix KSA.FlyController.OnFrame]
lifecycle: [startup:all-mods-loaded, frame:viewports]
game_types: [KSA.Controller, KSA.OrbitController, KSA.FlyController, KSA.Camera]
tags: [harmony, prefix, instance, field-injection, patch-time-failure, tryapply]
---

Before reaching for a `___field` injector, check whether the instance already exposes the value. unscience's camera animation first took the camera's transform through `Transform3D ___Transform`; no class in the
controller hierarchy has a field of that name, so Harmony threw when it applied the patch and the rest of the supermod's patches never ran. The fix is a prefix that takes `Controller __instance` and reads the
public `Camera` field.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by unscience in [Cinematic camera moves by pre-empting the Orbit and Fly controllers](../../../../mods/unscience/features/camera-controller-override-camera-animations.md).

## Background: how the game does it

`Controller` is the abstract base of the flight camera controllers; its camera is a public field, and `OnFrame` is virtual:

```csharp
// current/decomp/KSA/Controller.cs L8-L14
public abstract class Controller : IReceivesFrameUpdate, IKeyListener, IMouseButtonListener, IScrollListener, ICursorPosListener, ICursorEnterListener
{
	protected readonly string Name;

	public Camera Camera;

	public const float DEAD_ZONE = 0.15f;
```

Source: [current/decomp/KSA/Controller.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Controller.cs#L8-L14)[^ksa-controller]

```csharp
// current/decomp/KSA/Controller.cs L62-L64
public virtual void OnFrame(IViewport inViewport, double inDeltaTime)
{
}
```

Source: [current/decomp/KSA/Controller.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Controller.cs#L62-L64)

`OrbitController` and `FlyController` each override it ([current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L487)[^ksa-orbitcontroller], [current/decomp/KSA/FlyController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlyController.cs#L653)[^ksa-flycontroller]). `Camera` derives from `Transform3D`, but neither it nor any controller declares a field named `Transform`.

## Lifecycle and timing

Install: unscience's `[StarMapAllModsLoaded]` method (`Mod.OnFullyLoaded`) calls `Patcher.Patch()`, which applies this feature through `TryApply` (`startup:all-mods-loaded`). Run: the prefix executes in `frame:viewports`, on the main thread, inside `OnFrameViewports` for every Orbit or Fly controller (main viewport and secondary viewports). Writing the camera pose there
is early enough; `Camera.OnFrame` bakes it into the view straight afterwards. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Hook any frame point with Harmony patches on Program and Universe](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md).

## Steps

1. **Recognise the failure.** A prefix that asks for a field that does not exist is rejected when Harmony prepares the patch, not when the game runs it. A local test with Lib.Harmony 2.4.2 on classes shaped like
   these:

```text
// observed output of a local test (an experiment project, not part of any mod repository)
old form: ArgumentException: No such field defined in class OrbitController, ht, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null (Parameter 'Transform')
  prefix __instance=OrbitController Camera ok=True
  stock OrbitController.OnFrame
  prefix __instance=FlyController Camera ok=True
  stock FlyController.OnFrame
```

2. **Take `__instance` of the base type and read the member through it.** One prefix serves both overrides because `Controller` is their common base, and the parameter `inDeltaTime` matches the original's name:

```csharp
// camera-controller-override.lib/CameraControllerOverridePatches.cs L42-L64
private static bool OnFramePrefix(Controller __instance, double inDeltaTime)
{
    try
    {
        if (_sequencePlayer != null && _sequencePlayer.State == PlaybackState.Playing)
        {
            // The camera transform IS the controller's public `Camera` field
            // (KSA.Camera : Transform3D). KSA.Controller/OrbitController/FlyController have
            // no private `Transform` field, so the previous `Transform3D ___Transform`
            // Harmony field injector bound to nothing and threw at patch time — which also
            // aborted the rest of the supermod's patch chain. Reading __instance.Camera
            // mutates the real camera by reference, so animation moves the live view.
            bool shouldSkip = _sequencePlayer.Update(__instance, __instance.Camera, inDeltaTime);
            return !shouldSkip;
        }
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"camera-controller-override: Error in prefix: {ex.Message}");
        return true;
    }
}
```

Source: [camera-controller-override.lib/CameraControllerOverridePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/CameraControllerOverridePatches.cs#L42-L64)[^unscience-cameracontrolleroverridepatches]

   The comment at the top of that block records the history: the `Transform3D ___Transform` injector "bound to nothing and threw at patch time", which "also aborted the rest of the supermod's patch chain".

3. **Resolve the targets and null-check them.** `Apply` finds both overrides by name and patches whichever resolved:

```csharp
// camera-controller-override.lib/CameraControllerOverridePatches.cs L20-L34
public static void Apply(Harmony harmony)
{
    var prefix = new HarmonyMethod(typeof(CameraControllerOverridePatches)
        .GetMethod(nameof(OnFramePrefix), BindingFlags.Static | BindingFlags.NonPublic));

    var orbitOnFrame = AccessTools.Method(typeof(OrbitController), "OnFrame");
    var flyOnFrame = AccessTools.Method(typeof(FlyController), "OnFrame");

    if (orbitOnFrame != null)
        harmony.Patch(orbitOnFrame, prefix: prefix);
    if (flyOnFrame != null)
        harmony.Patch(flyOnFrame, prefix: prefix);

    Console.WriteLine("camera-controller-override.lib: patches applied");
}
```

Source: [camera-controller-override.lib/CameraControllerOverridePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/CameraControllerOverridePatches.cs#L20-L34)

4. **Isolate the feature at the call site.** unscience applies every feature through `TryApply`, so a patch-time exception costs one feature, not the rest:

```csharp
// unscience/Patcher.cs L105-L115
private static void TryApply(string feature, Action apply)
{
    try
    {
        apply();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"unscience: Failed to apply {feature} patches: {ex.Message}");
    }
}
```

Source: [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L105-L115)[^unscience-patcher]

5. **Unpatch by id.** `Remove` calls `harmony.UnpatchAll(harmony.Id)`; because unscience shares one Harmony instance, that one call removes every feature's patches ([UnpatchAll(ownId) from a main-thread hook after nulling delegates](../../safety/remove-your-patches-safely-at-runtime-and-at-exit/remove-your-patches-safely-at-runtime-and-at-exit-1.md)).

## Complete example

| File | Role |
|---|---|
| [camera-controller-override.lib/CameraControllerOverridePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/CameraControllerOverridePatches.cs) | Apply, prefix reading `__instance.Camera`, remove |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L56-L82) | `TryApply` per feature |

## Variations and gotchas

- **Prefer a public member over a field injector.** `___name` couples the patch to a private name the game can rename; `__instance.Camera` couples it to a public API.
- **Silent no-op for a missing method.** Here the lookup is only null-checked: if a build renames or overloads `OnFrame`, the feature does nothing and logs nothing. Log the null case, as in [Manual AccessTools lookup with a degrade path when the method is gone](patch-a-private-game-method-by-name-2.md).
- **A prefix on a base-typed parameter patches every instance.** It runs for every Orbit or Fly controller in every viewport, so while a sequence plays the others are skipped too.
- **Parameter names are part of the contract.** A name that matches neither a parameter of the original nor an injection prefix is rejected at patch time with `Parameter "..." not found in method ...`.
- **Two `OrbitController` classes** exist in build 5554 (`KSA` and `RenderCore.Input.Controllers`); the `using KSA;` binding selects the flight-camera one.

## See also

- [HarmonyPatch attribute with a string method name, applied by PatchAll](patch-a-private-game-method-by-name-1.md), [Manual AccessTools lookup with a degrade path when the method is gone](patch-a-private-game-method-by-name-2.md), [Explicit parameter arrays with MakeByRefType for overloaded and by-reference targets](patch-a-private-game-method-by-name-3.md), [Attribute argument types that bind by assignability](patch-a-private-game-method-by-name-4.md), [TargetMethods with per-name AccessTools lookups and the __args array](patch-a-private-game-method-by-name-5.md), [Reading a private instance field with a triple-underscore parameter](patch-a-private-game-method-by-name-6.md)
- [Patching private and overloaded game methods by name](overview.md)
- [Cinematic camera moves by pre-empting the Orbit and Fly controllers](../../../../mods/unscience/features/camera-controller-override-camera-animations.md)
- [CreateClassProcessor per patch class, required versus optional](../../safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-1.md)

[^ksa-controller]: KSA.Controller (decompiled, build 2026.10.10.5554)
[^ksa-orbitcontroller]: KSA.OrbitController (decompiled, build 2026.10.10.5554)
[^ksa-flycontroller]: KSA.FlyController (decompiled, build 2026.10.10.5554)
[^unscience-cameracontrolleroverridepatches]: unscience/camera-controller-override.lib/CameraControllerOverridePatches.cs
[^unscience-patcher]: unscience/Patcher.cs
