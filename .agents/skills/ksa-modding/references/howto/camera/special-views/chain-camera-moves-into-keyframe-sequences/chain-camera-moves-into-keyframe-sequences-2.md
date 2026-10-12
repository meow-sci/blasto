---
type: How-To
title: Write eased single moves as IKeyframeAnimation classes driven from the OnFrame prefix
description: Ten built-in moves share an interface, an easing helper and target-relative geometry; each captures its start state at Initialize and writes the live camera in Update, using absolute angles from the captured start to avoid drift
category: camera
group: special-views
topic: chain-camera-moves-into-keyframe-sequences
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/camera-controller-override-camera-animations]
techniques: [harmony-prefix]
hooks: [harmony-prefix KSA.OrbitController.OnFrame, harmony-prefix KSA.FlyController.OnFrame]
lifecycle: [frame:viewports, frame:draw-ui]
game_types: [KSA.Controller, KSA.Camera, KSA.Transform3D, KSA.OrbitController, KSA.FlyController, KSA.IFollowable]
tags: [camera, animation, easing, orbit, zoom, shake, unscience]
---
A camera move is a small class: it learns the starting pose when it begins, computes a pose from elapsed time every frame, writes the live camera and reports when it has finished. This article is about **eased single moves**. Camera Controller Override has ten of them (zoom in and out, zoom to an offset,
spiral in and out, orbit, orbit with a wobble, shake, pan and rotate) and they all share one interface. Queuing them into a sequence is [approach 1](chain-camera-moves-into-keyframe-sequences-1.md).

## Background: how the game does it

The moves write through `Controller.Camera`, the public `Camera` field of the Orbit or Free controller ([current/decomp/KSA/Controller.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Controller.cs#L12)), and read the target from `Camera.Following` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L158)). Orientation is built with `Camera.LookAtRotation(forward, up)` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L203-L209)). Writes land because they are made
in a prefix that skips the stock controller for the frame ([approach 4](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md)); `Camera.OnFrame` still applies the terrain clamp afterwards ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L633-L639)).

## Lifecycle and timing

`frame:viewports`, in the prefix, before `Camera.OnFrame`. The followed target is re-read every frame (`Following.GetPositionEcl()`), so moves follow a moving vessel. Time is the player's `inDeltaTime`. Starting and stopping come from ImGui buttons in `frame:draw-ui`, effective next frame.

## Eased single moves

1. **The interface.** `Initialize` captures the start state, `Update` writes the camera and returns true when finished, `Reset` clears runtime state, and an optional `LookAtTargetProvider` overrides the look target:

```csharp
// camera-controller-override.lib/Animation/IKeyframeAnimation.cs L13-L18, L63-L77
public interface IKeyframeAnimation
{
    /// <summary>
    /// Display name for the animation (e.g., "Zoom Out", "Orbit").
    /// </summary>
    string Name { get; }
    // ...
    /// <summary>
    /// Initialize the animation state before first Update call.
    /// Captures starting position, rotation, and any other runtime state.
    /// </summary>
    /// <param name="controller">The current camera controller.</param>
    /// <param name="transform">The camera transform.</param>
    void Initialize(Controller controller, Transform3D transform);

    /// <summary>
    /// Update the animation for the current frame.
    /// </summary>
    /// <param name="controller">The current camera controller.</param>
    /// <param name="transform">The camera transform to modify.</param>
    /// <param name="deltaTime">Time elapsed since last frame in seconds.</param>
    /// <param name="elapsedTime">Total elapsed time since animation started in seconds.</param>
```

2. **Share the geometry.** The target is `controller.Camera.Following.GetPositionEcl()`, orientation comes from `Camera.LookAtRotation`, and the helpers live in one class:

```csharp
// camera-controller-override.lib/Animation/AnimationHelpers.cs L32-L47
public static double3 GetTargetPosition(Controller controller, double3 fallback = default)
    => controller?.Camera?.Following?.GetPositionEcl() ?? fallback;

/// <summary>
/// Orient the transform to look at a target position.
/// </summary>
/// <param name="transform">The transform to modify.</param>
/// <param name="targetPos">The position to look at in ecliptic coordinates.</param>
public static void LookAtTarget(Transform3D transform, double3 targetPos)
{
    double3 lookDirection = targetPos - transform.PositionEcl;
    if (lookDirection.LengthSquared() > 0.0001)
    {
        double3 currentUp = double3.UnitY.Transform(transform.LocalRotation);
        transform.LocalRotation = Camera.LookAtRotation(lookDirection, currentUp);
    }
```

3. **Ease with a helper.** `Linear`, `EaseIn` (`t^p`), `EaseOut` (`1-(1-t)^p`) and `EaseInOut` (piecewise, separate powers for each half) in [ksa-abstractions.lib/EasingHelper.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/EasingHelper.cs).

4. **Use absolute angles from the captured start.** Accumulating per-frame rotations introduced floating-point error, so the orbit stores the original offset and recomputes the total rotation from eased progress every frame, re-anchored to the current target:

```csharp
// camera-controller-override.lib/Animation/Animations/OrbitAnimation.cs L50-L73
public void Initialize(Controller controller, Transform3D transform)
{
    // Get current state to determine orbit axis
    double3 targetPos = AnimationHelpers.GetTargetPosition(controller);
    double3 currentOffset = transform.PositionEcl - targetPos;

    // Validate radius
    double radius = currentOffset.Length();
    if (radius < 0.01)
    {
        Console.WriteLine("camera-controller-override: Orbit radius too small, cancelling.");
        _isInitialized = false;
        return;
    }

    // Store starting offset to avoid cumulative rotation error
    _startOffset = currentOffset;

    // Calculate orbit axis (perpendicular to offset - determines rotation direction)
    _orbitAxis = AnimationHelpers.CalculateOrbitAxis(currentOffset, transform.LocalRotation);
    _isInitialized = true;

    Console.WriteLine($"[OrbitAnimation] Initialize: pos={transform.PositionEcl}, target={targetPos}, radius={radius:F2}");
}
```

```csharp
// camera-controller-override.lib/Animation/Animations/OrbitAnimation.cs L83-L121
// Get current eased progress (0.0 to 1.0)
double t = Math.Min(1.0, elapsedTime / DurationSeconds);
double currentEasedProgress = AnimationHelpers.ApplyEasing(t, Easing, EasingPowerStart, EasingPowerEnd);

// Snap to exactly 1.0 on completion to eliminate floating-point drift
if (elapsedTime >= DurationSeconds)
{
    currentEasedProgress = 1.0;
}

// FIXED: Calculate TOTAL rotation angle from start (not incremental)
// This eliminates cumulative floating-point error
double totalAngleDegrees = Degrees * currentEasedProgress;
double totalAngleRadians = totalAngleDegrees * Math.PI / 180.0;

// FIXED: Apply rotation to ORIGINAL starting offset
double3 k = _orbitAxis;
double cos = Math.Cos(totalAngleRadians);
double sin = Math.Sin(totalAngleRadians);
double3 rotatedOffset = _startOffset * cos 
    + double3.Cross(k, _startOffset) * sin 
    + k * double3.Dot(k, _startOffset) * (1.0 - cos);

// Get CURRENT target position - target may be moving!
double3 currentTargetPos = AnimationHelpers.GetTargetPosition(controller);

// Update position relative to CURRENT target (not starting target)
// This allows orbit to follow a moving target while still using absolute rotation
transform.PositionEcl = currentTargetPos + rotatedOffset;

// Log on first frame
if (elapsedTime < deltaTime * 1.5)
{
    Console.WriteLine($"[OrbitAnimation] First frame: elapsed={elapsedTime:F4}, totalAngle={totalAngleDegrees:F4}°");
}

// Maintain look-at behavior
double3 lookAtTarget = LookAtTargetProvider?.Invoke(controller) ?? currentTargetPos;
AnimationHelpers.LookAtTarget(transform, lookAtTarget);
```

5. **Snap to exactly 1.0 on completion** (`elapsedTime >= DurationSeconds`) to remove drift, and return true.

6. **Know which moves are incremental.** Zoom moves apply the per-frame delta of eased progress; spiral zoom takes distance by eased total minus distance travelled; standalone shake integrates yaw deltas and its easing values are stored but not applied ([camera-controller-override.lib/Animation/Animations/ShakeAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/Animations/ShakeAnimation.cs)).

## Complete example

- [camera-controller-override.lib/Animation/Animations/ZoomInAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/Animations/ZoomInAnimation.cs), [camera-controller-override.lib/Animation/Animations/OrbitAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/Animations/OrbitAnimation.cs), [camera-controller-override.lib/Animation/Animations/PanAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/Animations/PanAnimation.cs), [camera-controller-override.lib/Animation/Animations/RotateAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/Animations/RotateAnimation.cs)
- [camera-controller-override.lib/CameraControllerOverridePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/CameraControllerOverridePatches.cs): the prefix

## Variations and gotchas

- **Zoom stops 1 m short** of the target so the camera does not pass through it; orbit refuses a radius under 1 cm.
- **Initialization can fail**; a failed `Initialize` makes `Update` return true at once (skipped) with a log line.
- **Moves act relative to the followed target**; with nothing followed, `GetTargetPosition` falls back to the default (zero), so follow something first.
- **Two `OrbitController` classes exist** (`KSA` and `RenderCore.Input.Controllers`); `using KSA;` selects the flight camera.
- **A prefix patch resolved by name** silently does nothing if a future build renames or overloads `OnFrame`.

## See also

- [Topic overview](overview.md); [approach 1](chain-camera-moves-into-keyframe-sequences-1.md)
- [Cinematic camera moves by pre-empting the Orbit and Fly controllers](../../../../mods/unscience/features/camera-controller-override-camera-animations.md)
- [Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md), [Play scripted cinematic camera tracks with blends and easing](../play-cinematic-camera-tracks/overview.md)
