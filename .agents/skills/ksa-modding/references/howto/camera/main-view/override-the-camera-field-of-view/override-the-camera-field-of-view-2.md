---
type: How-To
title: Call SetFieldOfView in degrees, read GetFieldOfView in radians and zoom past the player clamp
description: KSArmory clamps its own writes to 1 to 120 degrees because the projection throws outside 0 to 180, restates the field every frame because the zoom keys clamp at 15, and resets released camera windows to 50 degrees
category: camera
group: main-view
topic: override-the-camera-field-of-view
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/borrow-main-view-camera, mods/ksarmory/features/director-camera-window]
techniques: [direct-camera-write]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:viewports]
game_types: [KSA.Camera, KSA.Program, KSA.ViewportRegistry, KSA.IGameViewport]
tags: [camera, field-of-view, zoom, degrees, radians, clamp]
---
For most mods the right way to change the field of view is the plain one: call `Camera.SetFieldOfView(degrees)` on the camera you own. KSArmory's gunner's sight zooms the main view to a few degrees, and its camera windows zoom a secondary camera. The traps are
all in the units and the clamps. Prerequisites: StarMap.

## Background: how the game does it

| Member | Unit | Behaviour | Source |
|---|---|---|---|
| `SetFieldOfView(float)` | degrees | Stores radians, rebuilds the projection, **does not clamp** | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L417-L421) |
| `GetFieldOfView()` | radians | Returns `_fovRadians` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L790-L793) |
| `ChangeFieldOfView(float)` | degrees delta | The zoom keys; clamps 15-120 | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L455-L462) |
| `UpdateProjection()` | | Throws when the perspective builder rejects the field | [current/decomp/RenderCore/ReverseDepthBufferUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ReverseDepthBufferUtils.cs#L8-L13) |

The builder throws `ArgumentOutOfRangeException` for a field of zero or at least pi ([current/decomp/RenderCore/ReverseDepthBufferUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ReverseDepthBufferUtils.cs#L8-L13)); from a mod's frame hook that ends the process. The default field is 50 degrees (`_fovRadians = 0.87266463f`, [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L53)).
`GameSettings.ApplyTo` sets the settings slider's value (default 50) when each camera is created ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L677-L687)) and again whenever the player applies the settings window (`GameSettings.Apply` calls `ApplyTo(Program.GetCamera())`, [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L2420-L2447)), so nothing overwrites a mod's value every frame, but a settings apply does reset the main camera to the slider value.

## Lifecycle and timing

The write runs in the borrower's step in the GUI hook (`frame:draw-ui`). `SetFieldOfView` replaces the projection matrix at once, but the combined view-projection is rebuilt only by the next `Camera.OnFrame` (`frame:viewports`), or immediately if the mod calls `Camera.OnFrame` itself
([aiming a secondary camera](../../secondary-viewports/aim-a-secondary-viewport-camera/overview.md)). It must be rewritten every frame it is wanted: a player's zoom key goes through `ChangeFieldOfView` and clamps the field to 15 degrees, throwing away
anything narrower with no notification.

## Steps

1. **Clamp your own writes** to a hard safe range and skip the call when the value is already right:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L4047-L4066
public static bool TrySetMainViewFov(double degrees)
{
    if (!double.IsFinite(degrees)) return false;

    try
    {
        if (Program.MainViewport?.GetCamera() is not { } camera) return false;

        float wanted = (float)Math.Clamp(degrees, SightZoom.MinFovDeg, SightZoom.MaxFovDeg);
        if (Math.Abs(double.RadiansToDegrees(camera.GetFieldOfView()) - wanted) < 1e-3) return true;

        camera.SetFieldOfView(wanted);
        return true;
    }
    catch (Exception e)
    {
        Log.Warn($"could not set the field of view: {e.Message}");
        return false;
    }
}
```

2. **Define the range once.** Minimum one degree (far below any magnification offered and clear of the throw), maximum the engine's own 120, default 50:

```csharp
// src/KSArmory/Sim/SightZoom.cs L25-L31
public const double MinFovDeg = 1.0;

/// <summary>Widest field the camera may be asked for (deg), which is the engine's own ceiling.</summary>
public const double MaxFovDeg = 120.0;

/// <summary>What the camera is assumed to be showing when its own field cannot be read.</summary>
public const double DefaultFovDeg = 50.0;
```

3. **Convert when you read.** `GetFieldOfView()` is radians:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L4018-L4034
public static double MainViewFovDeg()
{
    try
    {
        if (Program.MainViewport?.GetCamera() is not { } camera) return SightZoom.DefaultFovDeg;

        double degrees = double.RadiansToDegrees(camera.GetFieldOfView());

        return double.IsFinite(degrees) && degrees > 0.0 && degrees < 180.0
            ? degrees
            : SightZoom.DefaultFovDeg;
    }
    catch
    {
        return SightZoom.DefaultFovDeg;
    }
}
```

4. **Give a released window back its default.** The player's zoom keys cannot reach below 15, so a window camera let go at one degree would be stuck; KSArmory resets released secondary cameras to 50:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L5806-L5816
public static void ResetViewportFov(int index)
{
    try
    {
        if (!TryViewport(index, out IGameViewport viewport)) return;
        if (viewport.Type != ViewportType.Secondary) return;

        viewport.BaseCamera?.SetFieldOfView((float)SightZoom.DefaultFovDeg);
    }
    catch { /* a window that cannot be reset is still a window */ }
}
```

5. **Set the field with the pose** for a window camera (clamped 1 to 120) in the same step ([src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5723-L5729)).

## Complete example

- [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4018-L4066), [src/KSArmory/Sim/SightZoom.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SightZoom.cs)
- [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L1096-L1104): the reset on release

## Variations and gotchas

- **Restore what the player had.** The main view's field belongs to the player; record it before the first write and give it back first ([approach 3](override-the-camera-field-of-view-3.md), [Borrow the main camera, hand it back and arbitrate between camera users](../borrow-the-main-camera-and-hand-it-back/overview.md)).
- **The map camera has its own field.** `Program.GetMainCamera()` is the map camera in Map mode; use the camera you drive.
- **A zoom key press loses a narrow field** with no notification, so restate the value each frame.
- **`ortho` is separate**: `SetOrthographic` and `SetOrthoHalfHeight` rebuild the projection too.

## See also

- [Topic overview](overview.md); [approach 1](override-the-camera-field-of-view-1.md), [3](override-the-camera-field-of-view-3.md)
- [Borrow the player's main view and hand it back](../../../../mods/ksarmory/features/borrow-main-view-camera.md), [Director camera windows on leased secondary viewports](../../../../mods/ksarmory/features/director-camera-window.md)
- [Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](../../secondary-viewports/aim-a-secondary-viewport-camera/overview.md), [Understand KSA camera modes, controllers, the viewport frame order and which camera is which](../../controllers/understand-camera-modes-and-controllers/overview.md)
