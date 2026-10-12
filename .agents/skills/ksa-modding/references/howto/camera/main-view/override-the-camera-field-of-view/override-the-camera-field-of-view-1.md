---
type: How-To
title: Hold a lens field of view with prefixes on Camera.UpdateProjection and ChangeFieldOfView, main camera only
description: Glass writes the private _fovRadians in a prefix on UpdateProjection and swallows the stock zoom keys in a prefix on ChangeFieldOfView, both gated on ViewportRegistry.IsMainCamera, with lens presets driven from a static FovController and saved with the scene
category: camera
group: main-view
topic: override-the-camera-field-of-view
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/glass-fov-override]
techniques: [harmony-prefix]
hooks: [harmony-prefix KSA.Camera.UpdateProjection, harmony-prefix KSA.Camera.ChangeFieldOfView]
lifecycle: [frame:draw-ui, frame:input, frame:viewports]
game_types: [KSA.Camera, KSA.Program, KSA.ViewportRegistry, KSA.GameSettings]
tags: [camera, field-of-view, harmony, lens, projection, glass]
---
The field of view the player sees is `Camera._fovRadians`, rebuilt into the projection by `UpdateProjection`. Glass (a unscience submod) gives the player a lens selector and makes it stick against every other path that touches the projection: window resizes, settings applies and the stock zoom keys.
It does this with two Harmony prefixes and a small static controller. Prerequisites: StarMap and Harmony. The main-camera gating is the subject of
[this reference topic](../../controllers/understand-camera-modes-and-controllers/overview.md) ([approach 3](../../controllers/understand-camera-modes-and-controllers/understand-camera-modes-and-controllers-3.md)).

## Background: how the game does it

```csharp
// current/decomp/KSA/Camera.cs L417-L421
public void SetFieldOfView(float fovDegrees)
{
	_fovRadians = (float)((double)fovDegrees * (Math.PI / 180.0));
	UpdateProjection();
}
```

```csharp
// current/decomp/KSA/Camera.cs L455-L462
public void ChangeFieldOfView(float change)
{
	double num = (double)_fovRadians * (180.0 / Math.PI);
	num += (double)change;
	num = Math.Clamp(num, 15.0, 120.0);
	_fovRadians = (float)(num * (Math.PI / 180.0));
	UpdateProjection();
}
```

```csharp
// current/decomp/KSA/Camera.cs L471-L485
public void UpdateProjection()
{
	if (_orthographic)
	{
		_vp.projection = ReverseDepthBufferUtils.CreateOrthographicReverseZ(_orthoHalfHeight * 2f * AspectRatio, _orthoHalfHeight * 2f, NearPlane, 50000f);
	}
	else
	{
		_vp.projection = ReverseDepthBufferUtils.CreatePerspectiveFieldOfViewReverseZ(_fovRadians, AspectRatio, NearPlane, FarPlane);
	}
	if (!float4x4.Invert(_vp.projection, out _vpInv.projection))
	{
		throw new Exception("Tried to assign non-invertible projection matrix");
	}
}
```

- `SetFieldOfView` takes **degrees**, stores radians, does not clamp and rebuilds the projection; `GetFieldOfView` returns **radians** ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L790-L793)).
- `ChangeFieldOfView` is the stock zoom key handler and clamps to 15-120 degrees; `Camera.OnKey` runs before the active controller in every mode ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L856-L874)).
- `UpdateProjection` rebuilds the matrix from `_fovRadians`, `AspectRatio` and the planes; it is called from `SetFieldOfView`, `ChangeFieldOfView`, `Resize`, `SetOrthographic` and `SetOrthoHalfHeight`.
- The matrix builder throws `ArgumentOutOfRangeException` for a field of zero or at least half a turn ([current/decomp/RenderCore/ReverseDepthBufferUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ReverseDepthBufferUtils.cs#L8-L13)).

## Lifecycle and timing

| Code | Runs | Value |
|---|---|---|
| `FovController.ApplyFov()` | Every frame from the Glass sub-mod's `Update` (`StarMapBeforeGui` or the hidden-HUD replay) | `frame:draw-ui` |
| `UpdateProjection` prefix | Whenever the game rebuilds a projection: settings apply, resize, `SetFieldOfView` | any of `frame:input`, `frame:draw-ui`, `frame:viewports` |
| `ChangeFieldOfView` prefix | GLFW key event to `Program.OnKey` to `Camera.OnKey` | `frame:input` |

`UpdateProjection` replaces the projection matrix at once, but `Camera.OnFrame` builds the combined view-projection only in `frame:viewports`. A field changed in `frame:draw-ui` therefore leaves the two out of step for the rest of that frame: `Program.UpdateShaderData` packs `MVP.projection` and `MVP.viewProjection` separately ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2863-L2867)), and the view-projection catches up at the next `Camera.OnFrame`. In steady state Glass writes the same value every frame, so nothing lags
([game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)).

## Steps

1. **Keep state process-wide and clamp it.** Glass allows 1 to 179 degrees (the engine's builder throws at 180) and defaults to 50:

```csharp
// glass.lib/FovController.cs L10-L24
public static class FovController
{
    public const float MinFov = 1f;
    public const float MaxFov = 179f;
    public const float DefaultFov = 50f;

    public static bool IsOverrideActive { get; set; } = false;
    public static float OverrideFovDegrees { get; set; } = DefaultFov;

    /// <summary>Sets the FOV override to <paramref name="degrees"/>, clamping to [MinFov, MaxFov].</summary>
    public static void SetFov(float degrees)
    {
        OverrideFovDegrees = MathF.Max(MinFov, MathF.Min(MaxFov, degrees));
        IsOverrideActive = true;
    }
```

2. **Apply it to the main camera each frame:**

```csharp
// glass.lib/FovController.cs L51-L56
public static void ApplyFov()
{
    if (!IsOverrideActive) return;
    float clampedFov = MathF.Max(MinFov, MathF.Min(MaxFov, OverrideFovDegrees));
    Program.GetMainCamera().SetFieldOfView(clampedFov);
}
```

3. **Patch `UpdateProjection` to inject the value before the original builds the matrix, and `ChangeFieldOfView` to swallow the stock keys**, both only for the main camera:

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

4. **Look up the private field by name once** (`AccessTools.Field(typeof(Camera), "_fovRadians")`) and fail soft if it is missing ([glass.lib/GlassPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/GlassPatches.cs#L18-L32)).

5. **Save it with the scene** as `SavedLens(Enabled, Degrees)`, validating 1-179 and re-applying after the camera is reconstructed ([glass.lib/GlassSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/GlassSubmod.Saves.cs#L9-L22)).

## Complete example

- [glass.lib/GlassPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/GlassPatches.cs), [glass.lib/FovController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/FovController.cs), [glass.lib/GlassSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/GlassSubmod.cs)

## Variations and gotchas

- **String lookups.** `_fovRadians` and both method names are found by string; a rename breaks FOV control silently.
- **`GetMainCamera()` is the map camera in map mode**, so `ApplyFov` also sets the map camera's field there.
- **`DisableOverride()` leaves the last applied FOV** on the camera; it only stops enforcing it, so the game's own value returns on the next settings apply or FOV key.
- **Values outside 15-120 are out of the player's reach.** Only code can set them (Glass here, `SetFieldOfView` in the other approaches).
- **Scope.** Without the main-camera gate the override would overwrite thumbnail, portrait, docking and Hot Pursuit cameras ([Main-camera field-of-view override with lens presets](../../../../mods/unscience/features/glass-fov-override.md)).
- **With the HUD hidden** `ApplyFov` runs only if the host replays `Update`; the `UpdateProjection` prefix still holds the value.

## See also

- [Topic overview](overview.md); [approach 2](override-the-camera-field-of-view-2.md), [3](override-the-camera-field-of-view-3.md)
- [Main-camera field-of-view override with lens presets](../../../../mods/unscience/features/glass-fov-override.md)
- [Understand KSA camera modes, controllers, the viewport frame order and which camera is which](../../controllers/understand-camera-modes-and-controllers/overview.md), [Borrow the main camera, hand it back and arbitrate between camera users](../borrow-the-main-camera-and-hand-it-back/overview.md), [Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](../../secondary-viewports/aim-a-secondary-viewport-camera/overview.md)
