---
type: Mod Feature
title: Cinematic camera moves by pre-empting the Orbit and Fly controllers
description: A shared Harmony prefix on OrbitController.OnFrame and FlyController.OnFrame hands the controller's live Camera to an animation player while a sequence plays, so ten eased, target-relative moves (zooms, spirals, orbits, loopy orbit, shake, pan, rotate) replace the stock camera math for that frame
mod: unscience
submod: camera-controller-override
category: camera
techniques: [harmony-prefix, reflection]
hooks:
  - harmony-prefix KSA.OrbitController.OnFrame (resolved by name)
  - harmony-prefix KSA.FlyController.OnFrame (resolved by name)
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent, Play/Pause/Stop buttons)
lifecycle: [frame:viewports, frame:draw-ui]
game_types:
  - KSA.OrbitController
  - KSA.FlyController
  - KSA.Controller
  - KSA.Camera
  - KSA.Transform3D
  - KSA.IFollowable
  - KSA.GameViewport
  - KSA.Program
tags: [camera, animation, easing, cinematic, harmony, controllers]
---

Camera Controller Override (shown in the Unscience window as "Camera Animations") turns the flight
camera into a scripted cinematic camera: zoom toward or away from the followed vessel, spiral in or
out, orbit it, orbit with a vertical wobble, shake, pan, or turn the view, each with its own duration
and easing curve, chained into a sequence.[^cco-readme]

Mechanism in one line: one Harmony prefix is attached to both `OrbitController.OnFrame` and
`FlyController.OnFrame`; while a sequence is playing it passes the controller's public `Camera` field
to the animation player, which writes `PositionEcl` and `LocalRotation` directly, and returns `false`
so the stock controller math is skipped for that frame.[^cco-patches]

## How it works

1. `CameraControllerOverridePatches.Apply` resolves both targets with
   `AccessTools.Method(typeof(OrbitController), "OnFrame")` / `(typeof(FlyController), "OnFrame")` and
   patches the same static prefix onto each. The host sets the static `SequencePlayer` first (the
   standalone `Patcher` and `unscience/Patcher.cs` both do).[^cco-standalone-patcher][^unscience-patcher]
2. The prefix binds only `Controller __instance` and `double inDeltaTime`. When the player state is
   `Playing` it calls `KeyframeSequencePlayer.Update(__instance, __instance.Camera, inDeltaTime)` and
   returns `!shouldSkip`; any exception is logged and the stock controller runs.
3. Every animation implements `IKeyframeAnimation`: `Initialize(controller, transform)` captures the
   start state, `Update(controller, transform, deltaTime, elapsedTime)` writes the camera and returns
   `true` when finished, `Reset()` clears runtime state, and an optional
   `LookAtTargetProvider : Func<Controller, double3>` overrides the look target.[^cco-ikeyframe]
4. Shared helpers: the target is `controller.Camera.Following.GetPositionEcl()` (re-read every frame,
   so moves follow a moving vessel), orientation comes from `Camera.LookAtRotation(forward, up)`, and
   orbit axes are derived from the start offset and the camera's up vector.[^cco-helpers]
5. Easing is `EasingHelper.ApplyEasing(t, type, powerStart, powerEnd)`: `Linear`, `EaseIn` (`t^p`),
   `EaseOut` (`1-(1-t)^p`) and `EaseInOut` (piecewise, separate powers for each half).[^easing-helper]

The ten built-in moves:

| Class | Motion | Progress model |
|---|---|---|
| `ZoomInAnimation` / `ZoomOutAnimation` | Along the camera-target line by `speed x duration` metres; zoom-in stops 1 m short of the target | Per-frame delta of eased progress (`eased(t) - eased(t_prev)`)[^cco-zoom-in] |
| `ZoomInToOffsetAnimation` | Toward `target + (x, y, z)` (offset in ECL axes), look-at sliding from target to offset | Per-frame delta, 1 m guard[^cco-zoom-offset] |
| `SpiralZoomInAnimation` / `SpiralZoomOutAnimation` | Zoom plus a roll of the captured up vector around the look axis by `SpiralDegrees` (Rodrigues formula) | Distance by eased total minus distance travelled; angle absolute[^cco-spiral-in] |
| `OrbitAnimation` | Rotates the start offset about an axis perpendicular to the offset and camera up | Absolute: total angle from eased progress applied to the original offset, then re-anchored to the current target[^cco-orbit] |
| `LoopyOrbitAnimation` | Orbit plus `sin(phase) x amplitude x sin(pi t)` along the orbit axis | Absolute[^cco-loopy] |
| `ShakeAnimation` | Yaw oscillation about the camera's current up axis | Incremental yaw delta per frame; phase is linear in time (the easing values are stored but not applied)[^cco-shake] |
| `PanAnimation` | Translates by an offset in the camera's right/up/forward axes captured at start, keeps looking at the target | Absolute[^cco-pan] |
| `RotateAnimation` | Yaw/pitch of the look direction about start axes; position held at the start offset from the moving target | Absolute[^cco-rotate] |

The absolute form (recompute from the captured start every frame) is the authors' fix for drift: the
comments note that accumulating per-frame rotations introduced floating-point error.[^cco-orbit]

## Lifecycle and timing

Controller `OnFrame` is not called from `Program.OnFrameController`. In build 5554 that method only
reads `GetActiveController().GetCursorMode()` to set the GLFW cursor mode. The camera update happens
earlier, in `frame:viewports`:[^ksa-program][^ksa-gameviewport]

| Step | Location (5554) |
|---|---|
| `Program.OnFrame` -> `OnFrameViewports(dtPlayer)` | `Program.cs` L2263, defined L2608 |
| For every `ViewportRegistry.Views` entry: `viewport.OnFrame((float)dtPlayer)` | `Program.cs` L2622 |
| `GameViewport.OnFrame`: `GetActiveController().OnFrame(this, dt)` then `GetCamera().OnFrame(dt)` | `GameViewport.cs` L116-L119 |
| `GetActiveController()` maps `Mode` Orbit -> `OrbitController`, Free -> `FlyController` | `GameViewport.cs` L284-L295 |
| `Camera.OnFrame`: `ClampCamera()`, then view matrices and frustum planes | `Camera.cs` L487-L497 |
| `OnFrameController` (cursor mode only) | `Program.cs` L2316, defined L2520 |

What this means for the animation code:

- It runs on the main thread after `PrepareFrame`, so the followed vessel's main-thread state is the
  result applied this frame, and the next physics step is already running on workers. The animation
  only reads `Following.GetPositionEcl()` and writes the camera, which only the main thread touches.
- Writing the pose in the prefix is early enough: `Camera.OnFrame` runs immediately afterwards and bakes
  it into the view, projection and frustum used for culling and rendering this frame.
- `inDeltaTime` is the player (wall-clock) frame time, so sequences play at normal speed when the
  simulation is paused or time-warped.
- Play, Pause, Resume and Stop are ImGui buttons in `frame:draw-ui`, which runs after
  `OnFrameViewports`; the first animated frame is the next frame. `ISubmod.Update` is empty.

## Key code

| Path | Role |
|---|---|
| [camera-controller-override.lib/CameraControllerOverridePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/CameraControllerOverridePatches.cs#L20-L64) | Patch install, removal, the shared prefix |
| [camera-controller-override.lib/Animation/KeyframeSequencePlayer.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/KeyframeSequencePlayer.cs#L429-L549) | `Update(controller, transform, dt)` called by the prefix |
| [camera-controller-override.lib/Animation/IKeyframeAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/IKeyframeAnimation.cs#L13-L92) | Animation contract |
| [camera-controller-override.lib/Animation/AnimationHelpers.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/AnimationHelpers.cs#L32-L88) | Target lookup, look-at, orbit axis |
| [ksa-abstractions.lib/EasingHelper.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/EasingHelper.cs#L5-L29) | Shared easing curves |
| [camera-controller-override.lib/Animation/Animations/OrbitAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/Animations/OrbitAnimation.cs#L75-L131) | Absolute-angle orbit |
| [camera-controller-override.lib/Animation/Animations/ZoomInAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/Animations/ZoomInAnimation.cs#L63-L128) | Per-frame-delta zoom with distance guard |
| [camera-controller-override.lib/Animation/Animations/LoopyOrbitAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/Animations/LoopyOrbitAnimation.cs#L97-L166) | Orbit with enveloped oscillation |
| [camera-controller-override.lib/Animation/Animations/ShakeAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/Animations/ShakeAnimation.cs#L71-L120) | Incremental yaw shake |
| [camera-controller-override/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override/Patcher.cs#L13-L26) | Standalone host wiring |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L71-L75) | Supermod wiring |

## Snippets

The patch and the prefix. The same `HarmonyMethod` goes on both controllers:

```csharp
// camera-controller-override.lib/CameraControllerOverridePatches.cs L20-L64
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
// ...
    private static bool OnFramePrefix(Controller __instance, double inDeltaTime)
    {
        try
        {
            if (_sequencePlayer != null && _sequencePlayer.State == PlaybackState.Playing)
            {
// ...
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

Absolute-angle orbit around a moving target (Rodrigues rotation of the captured start offset):

```csharp
// camera-controller-override.lib/Animation/Animations/OrbitAnimation.cs L84-L111
        double t = Math.Min(1.0, elapsedTime / DurationSeconds);
        double currentEasedProgress = AnimationHelpers.ApplyEasing(t, Easing, EasingPowerStart, EasingPowerEnd);
        
        // Snap to exactly 1.0 on completion to eliminate floating-point drift
        if (elapsedTime >= DurationSeconds)
        {
            currentEasedProgress = 1.0;
        }
// ...
        double totalAngleDegrees = Degrees * currentEasedProgress;
        double totalAngleRadians = totalAngleDegrees * Math.PI / 180.0;
// ...
        double3 k = _orbitAxis;
        double cos = Math.Cos(totalAngleRadians);
        double sin = Math.Sin(totalAngleRadians);
        double3 rotatedOffset = _startOffset * cos 
            + double3.Cross(k, _startOffset) * sin 
            + k * double3.Dot(k, _startOffset) * (1.0 - cos);
        
        // Get CURRENT target position - target may be moving!
        double3 currentTargetPos = AnimationHelpers.GetTargetPosition(controller);
// ...
        transform.PositionEcl = currentTargetPos + rotatedOffset;
```

The game side of the seam: the controller runs, then the camera bakes its matrices.

```csharp
// current/decomp/KSA/GameViewport.cs L116-L124
	public override void OnFrame(double dt)
	{
		GetActiveController().OnFrame(this, dt);
		GetCamera().OnFrame(dt);
		if (this.HasAll(ViewportOptionFlags.HasAudio))
		{
			ViewportAudio(dt);
		}
	}
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `OrbitController.OnFrame(IViewport, double)` | [current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L487) | Prefixed; skipped while playing |
| `FlyController.OnFrame(IViewport, double)` | [current/decomp/KSA/FlyController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlyController.cs#L653) | Prefixed; skipped while playing |
| `Controller.Camera` (public field) | [current/decomp/KSA/Controller.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Controller.cs#L12) | The live camera handed to the animation |
| `GameViewport.OnFrame` / `GetActiveController` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L119) | Call order controller -> `Camera.OnFrame` |
| `Program.OnFrameViewports` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2608-L2622) | Per-viewport `OnFrame` loop |
| `Camera.Following`, `Camera.LookAtRotation(double3, double3)`, `Camera.OnFrame` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L158-L203) | Target, orientation, clamp and matrices |
| `Transform3D.PositionEcl`, `Transform3D.LocalRotation` | [current/decomp/KSA/Transform3D.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Transform3D.cs#L13-L15) | Written every animated frame (`Camera` overrides `PositionEcl`) |

## Pitfalls and version notes

- Both controllers write the whole pose every frame. `OrbitController.OnFrame` rebuilds
  `LocalRotation` and `PositionCce` from its own azimuth/elevation/distance, and
  `FlyController.OnFrame` rewrites `LocalRotation` from its cached `_offsetEcl`. When playback stops
  without return-to-start, the camera therefore jumps back to the stock controller's pose (orbit) or
  orientation (fly) on the next frame.[^ksa-orbitcontroller][^ksa-flycontroller] See
  [keyframe sequences](./camera-controller-override-keyframe-sequences.md) for the return-to-start blend.
- The prefix is installed on the classes, so it runs for every Orbit or Fly controller in every game
  viewport (main, the four secondary viewports, the two portrait viewports). While a sequence plays,
  the first controller to call it drives the animation and every other Orbit/Fly controller is
  skipped too, so stock secondary cameras in Orbit or Free mode freeze for the duration.
- IVA, Map and Fixed modes use other controllers and are never animated.
- `Camera.OnFrame` calls `ClampCamera()` after the prefix, so paths that dip below 0.5 m above terrain
  are pushed back up.[^ksa-camera]
- Targets are resolved by name and the result is only null-checked: if a future build renames or
  overloads `OnFrame`, the feature silently does nothing (no exception, no log).
- Two `OrbitController` classes exist in 5554 (`KSA` and `RenderCore.Input.Controllers`); the
  `using KSA;` binding selects the flight-camera one.
- History: an earlier prefix used a `Transform3D ___Transform` field injector that matched no field.
  Harmony throws for that when the patch is applied, which aborted the rest of the supermod's patch
  chain. The prefix now reads `__instance.Camera`, and unscience wraps every feature in
  `TryApply`.[^scope-camera]
- `CameraControllerOverridePatches.Remove` calls `harmony.UnpatchAll(harmony.Id)`. In unscience the
  Harmony instance is shared, so this one call removes every feature's patches at
  unload.[^unscience-patcher]
- Verified unchanged in 5554: both `OnFrame` overrides (`OrbitController.cs` L487,
  `FlyController.cs` L653), `Controller.Camera` (L12), `Camera.LookAtRotation` (L203). The 5402
  `Viewport` -> `IViewport` retype of `OnFrame` did not affect the prefix because it never binds the
  viewport argument.

## See also

- [Keyframe sequences, groups and saved recipes](./camera-controller-override-keyframe-sequences.md)
- [Glass FOV override](./glass-fov-override.md) (the other main-camera lens feature)
- [Hot Pursuit part-mounted cameras](./hot-pursuit-part-mounted-cameras.md) (uses `FixedController`, not affected)
- [Kitchen Sink IVA camera unlock](./kitchen-sink-iva-camera-unlock.md)
- [Sub-mod: Camera Controller Override](../submods/camera-controller-override.md)
- [Mod overview](../overview.md)
- [Pre-empt Orbit or Free by prefixing both controllers' OnFrame with one shared patch while a sequence plays](../../../howto/camera/controllers/write-the-camera-pose-in-phase-with-the-frame/write-the-camera-pose-in-phase-with-the-frame-4.md) - how-to article demonstrating this feature
- [Write eased single moves as IKeyframeAnimation classes driven from the OnFrame prefix](../../../howto/camera/special-views/chain-camera-moves-into-keyframe-sequences/chain-camera-moves-into-keyframe-sequences-2.md) - how-to article demonstrating this feature
- [Prefer __instance and a public member over a field injector that matches nothing](../../../howto/patching/internals/patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-7.md) - how-to article demonstrating this feature

[^cco-patches]: camera-controller-override.lib/CameraControllerOverridePatches.cs
[^cco-readme]: camera-controller-override/README.md
[^cco-standalone-patcher]: camera-controller-override/Patcher.cs
[^unscience-patcher]: unscience/Patcher.cs
[^cco-ikeyframe]: camera-controller-override.lib/Animation/IKeyframeAnimation.cs
[^cco-helpers]: camera-controller-override.lib/Animation/AnimationHelpers.cs
[^easing-helper]: ksa-abstractions.lib/EasingHelper.cs
[^cco-zoom-in]: camera-controller-override.lib/Animation/Animations/ZoomInAnimation.cs
[^cco-zoom-offset]: camera-controller-override.lib/Animation/Animations/ZoomInToOffsetAnimation.cs
[^cco-spiral-in]: camera-controller-override.lib/Animation/Animations/SpiralZoomInAnimation.cs
[^cco-orbit]: camera-controller-override.lib/Animation/Animations/OrbitAnimation.cs
[^cco-loopy]: camera-controller-override.lib/Animation/Animations/LoopyOrbitAnimation.cs
[^cco-shake]: camera-controller-override.lib/Animation/Animations/ShakeAnimation.cs
[^cco-pan]: camera-controller-override.lib/Animation/Animations/PanAnimation.cs
[^cco-rotate]: camera-controller-override.lib/Animation/Animations/RotateAnimation.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-orbitcontroller]: KSA.OrbitController (decompiled, build 2026.10.10.5554)
[^ksa-flycontroller]: KSA.FlyController (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^scope-camera]: scope/camera.md (authors' camera integration analysis)
