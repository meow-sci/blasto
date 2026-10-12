---
type: How-To
title: Gate Harmony patches on Camera so they act on the main viewport's camera only
description: Camera methods run for every viewport's camera, so a patch that writes the field of view or projection tests ViewportRegistry.IsMainCamera before touching the instance
category: camera
group: controllers
topic: understand-camera-modes-and-controllers
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/glass-fov-override]
techniques: [harmony-prefix]
hooks: [harmony-prefix KSA.Camera.ChangeFieldOfView, harmony-prefix KSA.Camera.UpdateProjection]
lifecycle: [frame:input, frame:draw-ui, frame:viewports]
game_types: [KSA.Camera, KSA.ViewportRegistry, KSA.GameViewport, KSA.IGameViewport]
tags: [camera, harmony, field-of-view, main-camera, viewport]
---
Every viewport has its own `Camera`, so a Harmony patch on a `Camera` method fires for the player's camera,
the four secondary camera windows, the two crew portraits and any camera another mod leases. This
article answers **Which camera is the main one?** for patch authors: Glass (a submod of unscience)
patches `Camera.ChangeFieldOfView` and `Camera.UpdateProjection` and returns early unless the instance
is the main viewport's camera. Prerequisites are StarMap and Harmony.

## Background: how the game does it

`Camera` is a plain class; each `GameViewport` constructs two (`BaseCamera`, `MapCamera`)
([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L87-L104)). Every method that changes the projection ends in `UpdateProjection`:
`SetFieldOfView`, `ChangeFieldOfView` (the zoom keys, clamped to 15-120 degrees), `Resize`,
`SetOrthographic` and `SetOrthoHalfHeight` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L417-L462)). Because the patched methods are instance
methods on the shared type, nothing in the patch tells you whose camera it is: you must ask.

The engine provides the test:

```csharp
// current/decomp/KSA/ViewportRegistry.cs L101-L105
public static bool IsMainCamera(Camera inCamera)
{
	IGameViewport mainViewport = _mainViewport;
	return mainViewport != null && (inCamera == mainViewport.BaseCamera || inCamera == mainViewport.MapCamera);
}
```

It is true for the main viewport's `BaseCamera` and `MapCamera`, and false for everything else
(secondary windows, portraits, docking cameras, other mods' cameras).

## Lifecycle and timing

| Patch | Fires from | Lifecycle |
|---|---|---|
| `Camera.ChangeFieldOfView` prefix | `Program.OnKey` -> `DispatchKeyEvent` -> `InputViewport.GetCamera().OnKey` when the FOV keys are held ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1764-L1768), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L856-L874)) | `frame:input` |
| `Camera.UpdateProjection` prefix | Any rebuild of a projection: settings apply, viewport resize, `SetFieldOfView`, orthographic changes | `frame:input`, `frame:draw-ui` or `frame:viewports`, whichever called it |

`Camera.OnFrame` multiplies the view by the projection each frame in `frame:viewports`
([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L487-L497)), so a projection changed earlier in the frame is used immediately and one
changed in the GUI hook from the next frame's `Camera.OnFrame`. Both prefixes run on the main thread.

## Steps

1. **Resolve the targets once** with `AccessTools` and keep the `MethodInfo`s for unpatching:

```csharp
// glass.lib/GlassPatches.cs L18-L47
public static void Apply(Harmony harmony)
{
    _fovRadiansField = AccessTools.Field(typeof(Camera), "_fovRadians");

    _changeFovPrefix = typeof(GlassPatches).GetMethod(nameof(ChangeFieldOfViewPrefix), BindingFlags.NonPublic | BindingFlags.Static)!;
    _updateProjectionPrefix = typeof(GlassPatches).GetMethod(nameof(UpdateProjectionPrefix), BindingFlags.NonPublic | BindingFlags.Static)!;

    _cameraChangeFieldOfView = AccessTools.Method(typeof(Camera), "ChangeFieldOfView");
    _cameraUpdateProjection = AccessTools.Method(typeof(Camera), "UpdateProjection");

    harmony.Patch(_cameraChangeFieldOfView, prefix: new HarmonyMethod(_changeFovPrefix));
    harmony.Patch(_cameraUpdateProjection, prefix: new HarmonyMethod(_updateProjectionPrefix));

    Console.WriteLine("glass.lib: patches applied");
}

public static void Remove(Harmony harmony)
{
    if (_cameraChangeFieldOfView != null && _changeFovPrefix != null)
        harmony.Unpatch(_cameraChangeFieldOfView, _changeFovPrefix);
    if (_cameraUpdateProjection != null && _updateProjectionPrefix != null)
        harmony.Unpatch(_cameraUpdateProjection, _updateProjectionPrefix);

    _cameraChangeFieldOfView = null;
    _cameraUpdateProjection = null;
    _changeFovPrefix = null;
    _updateProjectionPrefix = null;

    Console.WriteLine("glass.lib: patches removed");
}
```

2. **Write the prefixes with `Camera __instance` and test it first.** The first swallows the stock zoom
   keys while the override is active; the second injects the wanted value into the private `_fovRadians`
   field before the original builds the matrix. Both end quickly for any other camera:

```csharp
// glass.lib/GlassPatches.cs L49-L71
    // Block the game's FOV change input — we control FOV when override is active.
    private static bool ChangeFieldOfViewPrefix(Camera __instance)
    {
        if (!FovController.IsOverrideActive || !IsMainCamera(__instance)) return true;
        return false;
    }

    // Inject our target FOV value into the camera before UpdateProjection runs.
    private static void UpdateProjectionPrefix(Camera __instance)
    {
        if (!FovController.IsOverrideActive) return;
        // Glass is the player's lens for the main camera. Do not stomp the independent
        // projections of stock secondary viewports (for example Hot Pursuit cameras), which
        // explicitly own their per-camera FOV.
        if (!IsMainCamera(__instance)) return;
        if (_fovRadiansField == null) return;
        float targetRadians = (float)(FovController.OverrideFovDegrees * (Math.PI / 180.0));
        _fovRadiansField.SetValue(__instance, targetRadians);
    }

    private static bool IsMainCamera(Camera camera)
        => ViewportRegistry.IsMainCamera(camera);
}
```

3. **Do not rely on `Program.GetMainCamera()` inside a patch.** In Map mode it answers with the map
   camera, and during rendering `FrameViewport` can be a secondary viewport. `IsMainCamera` takes the
   instance Harmony hands you, which is what you need ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L631-L654)).

4. **Keep the state process-wide and cheap.** Glass keeps its on/off flag and degrees in a static
   `FovController` ([glass.lib/FovController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/FovController.cs)) so the prefixes cost one boolean
   test for every other camera.

## Complete example

- [glass.lib/GlassPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/GlassPatches.cs): the two patches
- [glass.lib/FovController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/FovController.cs): the shared state and `ApplyFov`
- [glass.lib/GlassSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/GlassSubmod.cs): the lens UI that calls it each frame

## Variations and gotchas

- **Scope creep.** Glass used to affect every camera; after the viewport rework of build 5402 it would
  have overwritten thumbnail, portrait, docking and Hot Pursuit cameras, so both prefixes were scoped
  ([Main-camera field-of-view override with lens presets](../../../../mods/unscience/features/glass-fov-override.md)). A mod that patches `UpdateProjection` for every camera breaks
  per-camera FOV for others, including [Live part-mounted cameras in leased stock secondary viewports](../../../../mods/unscience/features/hot-pursuit-part-mounted-cameras.md).
- **String lookups.** `_fovRadians` and both method names are found by string; a rename breaks the
  feature at runtime with no compile error. `Camera._fovRadians` is at [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L53).
- **Map camera counts as main.** `IsMainCamera` is also true for `MapCamera`, so the override applies in map mode too.
- **`Camera.OnKey` still returns true** after the prefix, so the key stays consumed.

## See also

- [Topic overview](overview.md), [approach 1](understand-camera-modes-and-controllers-1.md), [approach 2](understand-camera-modes-and-controllers-2.md), [approach 4](understand-camera-modes-and-controllers-4.md)
- [Main-camera field-of-view override with lens presets](../../../../mods/unscience/features/glass-fov-override.md)
- [Override the camera field of view and zoom past the stock clamp](../../main-view/override-the-camera-field-of-view/overview.md), [Lease a secondary viewport for a mod camera and show it in a window](../../secondary-viewports/borrow-a-secondary-viewport/overview.md)
