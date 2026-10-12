---
type: Mod Feature
title: Main-camera field-of-view override with lens presets
description: Glass drives the main viewport camera's FOV from a static FovController and keeps it there with two main-camera-only prefixes, one on Camera.ChangeFieldOfView that swallows the stock FOV keys and one on Camera.UpdateProjection that writes the private _fovRadians field before every projection rebuild
mod: unscience
submod: glass
category: camera
techniques: [harmony-prefix, reflection]
hooks:
  - harmony-prefix KSA.Camera.ChangeFieldOfView (resolved by name)
  - harmony-prefix KSA.Camera.UpdateProjection (resolved by name)
  - reflection KSA.Camera._fovRadians
  - starmap StarMapBeforeGui (via unscience ISubmod.Update, FovController.ApplyFov)
  - unscience ISaveParticipantSource (scene save record glass)
lifecycle: [frame:draw-ui, frame:input, event:load]
game_types: [KSA.Camera, KSA.Program, KSA.ViewportRegistry, KSA.GameSettings, KSA.GameViewport]
tags: [camera, fov, projection, lens, harmony, reflection]
---

Glass gives the player a camera lens selector: a combo of eight named presets from "Super Telephoto
(200mm)" (15 degrees) to "Fisheye (10mm)" (120 degrees), plus a drag field for any value. Picking one
takes over the main camera's field of view until the override is turned off; the stock FOV keys stop
working while it is active, and other cameras keep their own FOV.[^glass-readme][^glass-submod]

Mechanism in one line: `FovController.ApplyFov()` calls `Program.GetMainCamera().SetFieldOfView(deg)`
every frame, and a prefix on `Camera.UpdateProjection` re-injects the override into the private
`_fovRadians` field before any rebuild of the main camera's projection, so no other code path
(window resize, settings apply, the FOV keys) can change it back.[^fov-controller][^glass-patches]

## How it works

`FovController` (static, in `glass.lib`, usable by other mods without the UI):[^fov-controller]

| Member | Behavior |
|---|---|
| `IsOverrideActive`, `OverrideFovDegrees` | Process-wide state |
| `SetFov(deg)` | Clamp to [1, 179] degrees and activate |
| `ResetToDefault()` | 50 degrees, active |
| `DisableOverride()` | Hand FOV back to the game (does not restore a previous value) |
| `GetCurrentFovDegrees()` | `Program.GetMainCamera().GetFieldOfView()` (radians) converted to degrees |
| `ApplyFov()` | If active, `Program.GetMainCamera().SetFieldOfView(clamped)` (degrees) |

`GlassPatches.Apply`:[^glass-patches]

- `_fovRadiansField = AccessTools.Field(typeof(Camera), "_fovRadians")`.
- Prefix on `Camera.ChangeFieldOfView(float)`: returns `false` (skip) when the override is active and
  the instance is a main-viewport camera. That method is the stock handler for the FOV increase and
  decrease key bindings, and clamps to 15-120 degrees.[^ksa-camera]
- Prefix on `Camera.UpdateProjection()`: when active and the instance is a main-viewport camera, writes
  `OverrideFovDegrees` converted to radians into `_fovRadians` with `FieldInfo.SetValue`; the original
  then builds the reverse-Z perspective matrix from it.
- Both prefixes test `ViewportRegistry.IsMainCamera(camera)`, which is true for the main viewport's
  `BaseCamera` or `MapCamera`.[^ksa-viewportregistry]

Who calls `UpdateProjection` on the main camera in 5554: `SetFieldOfView` (Glass itself and
`GameSettings.ApplyTo(Program.GetCamera())` when settings are applied), `ChangeFieldOfView`,
`Camera.Resize` (from `GameViewport` when the window or viewport is resized), `SetOrthographic` and
`SetOrthoHalfHeight`. The prefix covers all of them.[^ksa-camera][^ksa-gamesettings][^ksa-gameviewport]

UI (`GlassSubmod`): the preset combo and a `DragInt` (clamped to 10-200, then to 179 by `SetFov`). The
labels are focal-length names for familiar angles; no sensor-size math is involved. `Update` calls
`ApplyFov()` every frame; `Dispose` disables the override.[^glass-submod]

Scene save (record `glass`, restore order 200): `SavedLens(Enabled, Degrees)`; validation requires
1-179; restore sets the state and calls `ApplyFov()` on the reconstructed main camera. Reset disables
the override and returns to 50.[^glass-saves]

## Lifecycle and timing

| Code | When | Notes |
|---|---|---|
| `GlassSubmod.Update` -> `FovController.ApplyFov()` | `frame:draw-ui` (`StarMapBeforeGui` or the hidden-HUD replay) | Main thread. Rebuilds the main camera's projection right away |
| `UpdateProjection` prefix | Whenever the game rebuilds a camera projection: settings apply, resize, `SetFieldOfView` | Main thread |
| `ChangeFieldOfView` prefix | `frame:input`: GLFW key event -> `Program.OnKey` -> `DispatchKeyEvent` (L1756) -> `InputViewport.GetCamera().OnKey` (L1765) -> `Camera.OnKey` | `Camera.OnKey` still returns `true`, so the key stays consumed |
| Save restore | `event:load`, after native reconstruction | Re-applies to the new main camera |

`Camera.OnFrame` (in `frame:viewports`, before the UI phase) multiplies the view by `_vp.projection`
to build the view-projection and frustum planes. A projection change made in `frame:draw-ui` is
therefore fully reflected in those from the next frame's `Camera.OnFrame`. In steady state Glass writes
the same value every frame, so nothing lags.[^ksa-camera][^ksa-program]

## Key code

| Path | Role |
|---|---|
| [glass.lib/GlassPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/GlassPatches.cs#L18-L70) | Field lookup, both prefixes, main-camera gate |
| [glass.lib/FovController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/FovController.cs#L10-L57) | Reusable FOV API |
| [glass.lib/GlassSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/GlassSubmod.cs#L17-L35) | Lens presets and per-frame apply |
| [glass.lib/GlassSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/GlassSubmod.Saves.cs#L9-L26) | Scene save participant |
| [glass/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass/Patcher.cs#L11-L26) | Standalone host wiring (F9 window) |

## Snippets

```csharp
// glass.lib/GlassPatches.cs L18-L70
    public static void Apply(Harmony harmony)
    {
        _fovRadiansField = AccessTools.Field(typeof(Camera), "_fovRadians");
// ...
        _cameraChangeFieldOfView = AccessTools.Method(typeof(Camera), "ChangeFieldOfView");
        _cameraUpdateProjection = AccessTools.Method(typeof(Camera), "UpdateProjection");

        harmony.Patch(_cameraChangeFieldOfView, prefix: new HarmonyMethod(_changeFovPrefix));
        harmony.Patch(_cameraUpdateProjection, prefix: new HarmonyMethod(_updateProjectionPrefix));
// ...
    private static bool ChangeFieldOfViewPrefix(Camera __instance)
    {
        if (!FovController.IsOverrideActive || !IsMainCamera(__instance)) return true;
        return false;
    }

    // Inject our target FOV value into the camera before UpdateProjection runs.
    private static void UpdateProjectionPrefix(Camera __instance)
    {
        if (!FovController.IsOverrideActive) return;
// ...
        if (!IsMainCamera(__instance)) return;
        if (_fovRadiansField == null) return;
        float targetRadians = (float)(FovController.OverrideFovDegrees * (Math.PI / 180.0));
        _fovRadiansField.SetValue(__instance, targetRadians);
    }

    private static bool IsMainCamera(Camera camera)
        => ViewportRegistry.IsMainCamera(camera);
```

The game's FOV surface (degrees in, radians stored, 15-120 clamp only on the key path):

```csharp
// current/decomp/KSA/Camera.cs L417-L485
	public void SetFieldOfView(float fovDegrees)
	{
		_fovRadians = (float)((double)fovDegrees * (Math.PI / 180.0));
		UpdateProjection();
	}
// ...
	public void ChangeFieldOfView(float change)
	{
		double num = (double)_fovRadians * (180.0 / Math.PI);
		num += (double)change;
		num = Math.Clamp(num, 15.0, 120.0);
		_fovRadians = (float)(num * (Math.PI / 180.0));
		UpdateProjection();
	}
// ...
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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Camera._fovRadians` (private float, default 0.87266463 rad = 50 degrees) | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L53) | Written by reflection |
| `Camera.SetFieldOfView(float degrees)`, `GetFieldOfView()` (radians) | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L417-L421) | Apply and read back (`GetFieldOfView` L790) |
| `Camera.ChangeFieldOfView(float)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L455-L462) | Prefixed; only caller is `Camera.OnKey` (L856, calls at L864/L869) |
| `Camera.UpdateProjection()` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L471-L485) | Prefixed |
| `Program.GetMainCamera()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L631-L634) | `MainViewport.GetCamera()` (map camera in map mode) |
| `ViewportRegistry.IsMainCamera(Camera)` | [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L101-L105) | Main-camera gate |
| `GameSettings.ApplyTo(Camera)` | [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L2445-L2448) | Settings FOV write that the prefix overrides |
| `GameViewport` resize path (`BaseCamera.Resize`, `MapCamera.Resize`) | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L266-L267) | Another projection rebuild covered by the prefix |

## Pitfalls and version notes

- `_fovRadians` and both method names are looked up by string; a rename breaks FOV control silently at
  runtime with no compile error. The authors re-check this field on every game update; it is unchanged
  in 5554 (L53).[^scope-camera]
- Glass used to affect every camera. After the 5402 viewport rework it would have overwritten thumbnail,
  portrait, docking and Hot Pursuit cameras, so both prefixes and `FovController` now target only the
  main camera.[^scope-camera]
- `GetMainCamera()` returns the map camera while the main viewport is in map mode, so the override also
  sets the map camera's FOV there.
- `DisableOverride()` leaves the last applied FOV on the camera; it only stops enforcing it. The game's
  own value comes back on the next settings apply or FOV key press.
- Values above 120 or below 15 are reachable only through Glass; the stock key path clamps to that
  range.
- `ApplyFov` runs from the UI phase, so it keeps working with the HUD hidden only because unscience
  replays `Update` from the hidden-HUD hook; the standalone host stops applying while the HUD is
  hidden, but the `UpdateProjection` prefix still holds the value.

## See also

- [Hot Pursuit part-mounted cameras](./hot-pursuit-part-mounted-cameras.md) (per-camera FOV that Glass must not touch)
- [Camera animations](./camera-controller-override-camera-animations.md)
- [Hidden-HUD frame hook](./lib-hidden-ui-frame-hook.md)
- [Scene save coordinator](./lib-scene-save-coordinator.md)
- [Sub-mod: Glass](../submods/glass.md)
- [Mod overview](../overview.md)
- [Gate Harmony patches on Camera so they act on the main viewport's camera only](../../../howto/camera/controllers/understand-camera-modes-and-controllers/understand-camera-modes-and-controllers-3.md) - how-to article demonstrating this feature
- [Hold a lens field of view with prefixes on Camera.UpdateProjection and ChangeFieldOfView, main camera only](../../../howto/camera/main-view/override-the-camera-field-of-view/override-the-camera-field-of-view-1.md) - how-to article demonstrating this feature
- [Re-inject a private field before a rebuild so nothing can change it back](../../../howto/patching/harmony/skip-the-original-method-with-a-prefix/skip-the-original-method-with-a-prefix-3.md) - how-to article demonstrating this feature

[^glass-readme]: glass/README.md
[^glass-submod]: glass.lib/GlassSubmod.cs
[^fov-controller]: glass.lib/FovController.cs
[^glass-patches]: glass.lib/GlassPatches.cs
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-viewportregistry]: KSA.ViewportRegistry (decompiled, build 2026.10.10.5554)
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^glass-saves]: glass.lib/GlassSubmod.Saves.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^scope-camera]: scope/camera.md (authors' camera integration analysis)
