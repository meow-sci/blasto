---
type: How-To
title: Re-inject a private field before a rebuild so nothing can change it back
description: "Two Camera prefixes make a mod-owned field of view stick: ChangeFieldOfView is skipped for the main camera and UpdateProjection writes the override into the private _fovRadians just before the original builds the projection"
category: patching
group: harmony
topic: skip-the-original-method-with-a-prefix
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/glass-fov-override]
techniques: [harmony-prefix, reflection]
hooks:
  - harmony-prefix KSA.Camera.ChangeFieldOfView
  - harmony-prefix KSA.Camera.UpdateProjection
  - reflection KSA.Camera._fovRadians
lifecycle: [frame:input, frame:draw-ui, frame:viewports]
game_types: [KSA.Camera, KSA.ViewportRegistry, KSA.GameSettings, KSA.GameViewport, KSA.Program]
tags: [harmony, prefix, skip-original, camera, field-of-view, private-field]
---

The unscience `glass` sub-mod gives the player a lens selector. The hard part is not changing the field
of view; `Camera.SetFieldOfView` does that. The hard part is that the game rebuilds the projection from
the private `_fovRadians` in several unrelated places and each of them would silently undo the
override. Glass solves it with two prefixes: one *skips* the stock FOV keys for the main camera, and
one *injects* the override into `_fovRadians` immediately before every projection rebuild. The second
prefix returns `void`, so the original always runs; it is a "set the state, then let stock use it"
prefix rather than a replacement.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by unscience in
[Main-camera field-of-view override with lens presets](../../../../mods/unscience/features/glass-fov-override.md).

## Background: how the game does it

`Camera` stores the field of view as a private `float` in radians and builds the projection from it.
Setting it, changing it by key and resizing all end in `UpdateProjection()`:

```csharp
// current/decomp/KSA/Camera.cs L417-L421
public void SetFieldOfView(float fovDegrees)
{
	_fovRadians = (float)((double)fovDegrees * (Math.PI / 180.0));
	UpdateProjection();
}
```

Source: [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L417-L421)[^ksa-camera]

```csharp
// current/decomp/KSA/Camera.cs L455-L485
public void ChangeFieldOfView(float change)
{
	double num = (double)_fovRadians * (180.0 / Math.PI);
	num += (double)change;
	num = Math.Clamp(num, 15.0, 120.0);
	_fovRadians = (float)(num * (Math.PI / 180.0));
	UpdateProjection();
}

public void Resize(int2 newSize)
{
	FramebufferSize = newSize;
	AspectRatio = (float)FramebufferSize.X / (float)FramebufferSize.Y;
	UpdateProjection();
}

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

Source: [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L455-L485)

Who rebuilds the main camera's projection in build 2026.10.10.5554:

| Caller | Path to `UpdateProjection` |
|---|---|
| Settings apply | `GameSettings.ApplyTo(Camera)` calls `SetFieldOfView(FieldOfView)` ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L2445-L2448)[^ksa-gamesettings]) |
| Window or viewport resize | `GameViewport` calls `BaseCamera.Resize` and `MapCamera.Resize` ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L264-L268)[^ksa-gameviewport]) |
| FOV keys | `Camera.OnKey` calls `ChangeFieldOfView` for `InputAction.FieldOfViewIncrease` and `FieldOfViewDecrease` and still returns `true` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L856-L872)) |
| The mod itself | `SetFieldOfView` |

`ChangeFieldOfView` clamps to 15 to 120 degrees, so the stock keys can never reach the values Glass
offers (1 to 179). Every row ends in `UpdateProjection`, so that is where a single prefix covers them all.

`ViewportRegistry.IsMainCamera(Camera)` is true for the main viewport's `BaseCamera` or `MapCamera`
([current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L101-L105)[^ksa-viewportregistry]), which is how the mod avoids touching thumbnail, portrait or other
secondary cameras.

## Lifecycle and timing

| Code | When | Thread |
|---|---|---|
| `FovController.ApplyFov()` from `GlassSubmod.Update` | `frame:draw-ui`, every frame | main |
| `UpdateProjection` prefix | whenever the game rebuilds a camera projection: settings apply, resize, `SetFieldOfView` | main |
| `ChangeFieldOfView` prefix | `frame:input`: GLFW key event, `Program.OnKey`, `DispatchKeyEvent`, `Camera.OnKey` | main |

`Camera.OnFrame` (in `frame:viewports`) multiplies the view by the stored projection to build the
view-projection and frustum planes. A projection change made in `frame:draw-ui` is therefore picked up
by the next frame's `Camera.OnFrame`; in steady state the mod writes the same value every frame, so
nothing lags. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the order of those phases, and [Main-camera field-of-view override with lens presets](../../../../mods/unscience/features/glass-fov-override.md) for the
hidden-HUD caveat: `ApplyFov` runs from the UI phase, but the `UpdateProjection` prefix still holds
the value when the UI does not run.

## Steps

1. **Resolve the private field and both methods by name.** `AccessTools.Field` finds the private
   `_fovRadians`; a null result is checked later, so a rename degrades to "no injection" instead of
   throwing in the prefix:

```csharp
// glass.lib/GlassPatches.cs L11-L32
private static MethodInfo? _cameraChangeFieldOfView;
private static MethodInfo? _cameraUpdateProjection;
private static FieldInfo? _fovRadiansField;

private static MethodInfo? _changeFovPrefix;
private static MethodInfo? _updateProjectionPrefix;

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
```

Source: [glass.lib/GlassPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/GlassPatches.cs#L11-L32)[^unscience-glasspatches]

2. **Skip the stock FOV keys for the main camera.** A prefix that returns `false` stops
   `ChangeFieldOfView` from running. It tests the camera first, so other cameras and the "override off"
   state fall through to stock:

```csharp
// glass.lib/GlassPatches.cs L49-L54
// Block the game's FOV change input — we control FOV when override is active.
private static bool ChangeFieldOfViewPrefix(Camera __instance)
{
    if (!FovController.IsOverrideActive || !IsMainCamera(__instance)) return true;
    return false;
}
```

Source: [glass.lib/GlassPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/GlassPatches.cs#L49-L54)

   `Camera.OnKey` still returns `true`, so the key stays consumed. Without this prefix the key would
   still not win: `ChangeFieldOfView` ends in `UpdateProjection` (line 461 above), and the next prefix
   would overwrite whatever the key computed.

3. **Inject the override before every rebuild.** This prefix returns `void`, so the original always
   runs; it only changes the state the original reads. It writes the override, converted to
   radians, into `_fovRadians` through `FieldInfo.SetValue`. The original then builds the reverse-Z
   perspective matrix from it:

```csharp
// glass.lib/GlassPatches.cs L56-L70
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
```

Source: [glass.lib/GlassPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/GlassPatches.cs#L56-L70)

4. **Drive the value.** The public surface is a small static class with a clamp. `ApplyFov` calls the
   game's own setter, which triggers `UpdateProjection` and therefore the prefix:

```csharp
// glass.lib/FovController.cs L16-L24, L51-L56
public static bool IsOverrideActive { get; set; } = false;
public static float OverrideFovDegrees { get; set; } = DefaultFov;

/// <summary>Sets the FOV override to <paramref name="degrees"/>, clamping to [MinFov, MaxFov].</summary>
public static void SetFov(float degrees)
{
    OverrideFovDegrees = MathF.Max(MinFov, MathF.Min(MaxFov, degrees));
    IsOverrideActive = true;
}
// ...
public static void ApplyFov()
{
    if (!IsOverrideActive) return;
    float clampedFov = MathF.Max(MinFov, MathF.Min(MaxFov, OverrideFovDegrees));
    Program.GetMainCamera().SetFieldOfView(clampedFov);
}
```

Source: [glass.lib/FovController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/FovController.cs#L16-L56)[^unscience-fovcontroller]

5. **Install and remove.** The sub-mod owns one `Harmony("MeowSci.Glass")` instance and calls
   `GlassPatches.Apply`; `Remove` unpatches exactly the two prefix methods it added:

```csharp
// glass/Patcher.cs L7-L26
internal static class Patcher
{
    private static Harmony? _harmony;

    public static void Patch()
    {
        _harmony = new Harmony("MeowSci.Glass");
        GlassPatches.Apply(_harmony);
        HotkeyGuard.Patch(_harmony);
    }

    public static void Unload()
    {
        if (_harmony != null)
        {
            GlassPatches.Remove(_harmony);
            HotkeyGuard.Unpatch(_harmony);
        }
        _harmony = null;
    }
```

Source: [glass/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass/Patcher.cs#L7-L26)[^unscience-patcher]

## Complete example

| File | Role |
|---|---|
| [glass.lib/GlassPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/GlassPatches.cs) | Field lookup, both prefixes, main-camera gate |
| [glass.lib/FovController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/FovController.cs) | State and `ApplyFov` |
| [glass.lib/GlassSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/GlassSubmod.cs#L31-L35)[^unscience-glasssubmod] | Per-frame apply |
| [glass/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass/Patcher.cs) | Harmony instance and install |

## Variations and gotchas

- **Why inject instead of patching each caller.** Three unrelated callers rebuild the projection; one
  prefix on the choke point covers them all, including ones added in later builds.
- **String-named private field.** `_fovRadians` and both method names are looked up by string. A rename
  breaks FOV control at run time with no compile error; the field is unchanged in 5554
  ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L53)). See [Validating patch targets and detecting game-update breakage](../../safety/validate-patch-targets-and-detect-game-update-breakage/overview.md).
- **Gate by camera, not just by flag.** Glass once affected every camera; after the viewport rework in
  build 5402 that would have overwritten thumbnail and Hot Pursuit cameras, so both prefixes now check
  `ViewportRegistry.IsMainCamera`.
- **Map mode.** `Program.GetMainCamera()` returns the map camera in map mode, so the override applies
  there too.
- **Disabling does not restore.** `DisableOverride()` stops enforcing the value but leaves the last
  applied FOV on the camera until the game's next settings apply or FOV key press.
- **Another mod can still write the field.** Anything that sets `_fovRadians` and then calls
  `UpdateProjection` is overwritten by this prefix; one that builds a projection without calling
  `UpdateProjection` is not covered.

## See also

- [Reproduce the original body for marked objects and return false](skip-the-original-method-with-a-prefix-1.md), [Rewrite by-ref arguments and skip when a limit would throw](skip-the-original-method-with-a-prefix-2.md), [Mouse prefix that pairs presses with releases and repairs private latches](skip-the-original-method-with-a-prefix-4.md), [Keyboard and mouse-binding gates with a held-key model](skip-the-original-method-with-a-prefix-5.md)
- [Skipping Harmony prefixes](overview.md)
- [Main-camera field-of-view override with lens presets](../../../../mods/unscience/features/glass-fov-override.md)
- The topic override-the-camera-field-of-view builds a task around this technique
- [Patching private and overloaded game methods by name](../../internals/patch-a-private-game-method-by-name/overview.md) for the `___field` injection alternative to reflection

[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-viewportregistry]: KSA.ViewportRegistry (decompiled, build 2026.10.10.5554)
[^unscience-glasspatches]: unscience/glass.lib/GlassPatches.cs
[^unscience-fovcontroller]: unscience/glass.lib/FovController.cs
[^unscience-patcher]: unscience/glass/Patcher.cs
[^unscience-glasssubmod]: unscience/glass.lib/GlassSubmod.cs
