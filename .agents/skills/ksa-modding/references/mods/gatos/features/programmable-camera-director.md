---
type: Mod Feature
title: Programmable camera director
description: Take ownership of KSA's main camera by installing a FixedController subclass through a reflected protected setter, parking the viewport in Fixed mode, and composing each frame's pose from tracks, overrides and a captured baseline inside the engine's own viewport pass
mod: gatos
category: camera
techniques: [reflection, harmony-prefix, harmony-postfix, controller-subclass]
hooks:
  - harmony-prefix KSA.GameViewport.OnFrame
  - harmony-postfix KSA.GameViewport.OnFrame
  - reflection KSA.GameViewport.FixedController
  - reflection KSA.ViewportBase.Mode
lifecycle: [frame:viewports]
game_types:
  - KSA.GameViewport
  - KSA.ViewportBase
  - KSA.FixedController
  - KSA.Camera
  - KSA.CameraMode
  - KSA.Program
  - KSA.KittenEva
  - KSA.IFollowable
tags: [camera, cinematics, controller, reflection, harmony, tracks, splines, easing]
---

`/sim/camera` makes gatOS the sole writer of the main viewport's camera: take and release
ownership, place the camera in one of six reference frames relative to vessels, parts or bodies,
aim at targets with offsets, set field of view and roll, and play keyframed JSON tracks with
splines and easing. The KSA-side technique is the interesting part: it drives the camera with no
per-frame Harmony write, by subclassing the stock fixed-camera controller so the pose is applied in
phase with the engine's own matrix rebuild.

## How it works

**In-phase write** ([gatOS.GameMod/Game/Ksa/Camera/CameraPoseController.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraPoseController.cs#L52-L122)):
`GameViewport.OnFrame` runs `GetActiveController().OnFrame(this, dt)` and then
`GetCamera().OnFrame(dt)`, which rebuilds view and view-projection matrices
([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L124), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L487-L497)).
`CameraPoseController : FixedController` overrides `OnFrame`: it reads the followed object's
`GetPositionEcl()` at that instant, asks the director for an offset and rotation, and writes
`Camera.PositionEcl = followedEcl + offset` and `Camera.LocalRotation = rotation`, the same two
writes the stock controller makes, in the same order. Measuring the offset against the anchor sampled
in that same call removes a one-frame lag that an earlier "write in a prefix" design showed. With no
pose source it behaves exactly like stock, and its `OnSwitchOn` suppresses the "Fixed Camera" alert
only for gatOS's own park.

**Installing it** ([gatOS.GameMod/Game/Ksa/Camera/ViewportSeam.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/ViewportSeam.cs#L28-L109)): since 5402
`GameViewport.FixedController` and `ViewportBase.Mode` are `{ get; protected set; }`. `ViewportSeam`
fetches the non-public setters once with `GetProperty(...).GetSetMethod(nonPublic: true)` and invokes
them to install the controller and park the viewport in `CameraMode.Fixed` without going through the
player's mode-switch path. The stock controller is saved and restored at release.

**Ownership** ([gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs#L213-L262)): the director
captures a baseline pose, follow target, mode and tidal locking at take, points the base camera at
its own `IFollowable` anchor with
`SetFollow(_followable, tidalLocking: false, changeControl: false, alert: false)` and writes the
captured position back (because `SetFollow` teleports); see
[Synthetic camera follow anchor](synthetic-camera-follow-anchor.md). Every frame
([gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs#L522-L560)) it checks that it still owns the camera:
if the controller faulted, another mod replaced the fixed controller, the mode changed or the follow
target changed, it stands down rather than fight a second writer. Release blends back over
`camera_release_blend_s` and restores everything it captured.

**Composition** (game-free, `gatOS.SimFs/Camera/`): each channel (position, aim, roll, FOV) is
`Track ?? Override ?? Baseline` ([gatOS.SimFs/Camera/CameraState.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Camera/CameraState.cs#L270-L313)). Tracks are JSON
keyframes evaluated with Catmull-Rom or cubic Bezier position splines, slerp or squad orientation and
easing functions ([gatOS.SimFs/Camera/Splines.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Camera/Splines.cs)); a playing track registers in the schedule
registry as `kind = camera-track`, so the same pause, scrub, rate and loop controls apply
([gatOS.SimFs/Camera/Playback.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Camera/Playback.cs#L62-L66)). Anchor translation is applied exactly; only the
relative component is smoothed. The map's zoom is exposed separately as `/sim/camera/map/scope`.

## Lifecycle and timing

- `frame:viewports`. The Harmony prefix on the main `GameViewport.OnFrame` first runs the
  sampler, schedule tick and command drain (so camera commands and timed cues drained this frame
  affect this frame), then `DriveCamera` prepares the pose; KSA then calls the installed
  controller's `OnFrame` (the write) and `Camera.OnFrame` (the matrices). The postfix publishes KSA's
  final, clamped transform for read-back ([gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L574-L590)).
- This runs every rendered frame, including with the HUD hidden, because `OnFrameViewports` is
  outside the `DrawUI` block.
- The director, its state and the controller are main-thread only; transports only enqueue
  commands and read a volatile `CameraStore.Status`.

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Camera/CameraPoseController.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraPoseController.cs) | in-phase `FixedController` subclass |
| [gatOS.GameMod/Game/Ksa/Camera/ViewportSeam.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/ViewportSeam.cs) | reflected protected setters |
| [gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs) | ownership, stand-down, release, pose source |
| [gatOS.GameMod/Game/Ksa/Camera/CameraViewportPatch.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraViewportPatch.cs) | main-viewport prefix/postfix |
| [gatOS.GameMod/Game/Ksa/Camera/CameraFrames.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraFrames.cs) | the six reference frames |
| [gatOS.GameMod/Game/Ksa/Camera/CameraTargets.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraTargets.cs) | resolving vessels, parts and bodies |
| [gatOS.SimFs/Camera/CameraState.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Camera/CameraState.cs) | three-layer compositor |
| [gatOS.SimFs/Camera/TrackParser.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Camera/TrackParser.cs) | JSON track format |
| [plans/CAMERA_ASBUILT.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/plans/CAMERA_ASBUILT.md) | as-built design notes |

## Snippets

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraPoseController.cs L87-L114
    public override void OnFrame(IViewport inViewport, double inDeltaTime)
    {
        if (Pose is not { } source || Faulted)
        {
            base.OnFrame(inViewport, inDeltaTime);
            return;
        }

        if (Camera.Following is not { } following)
            return;

        try
        {
            var followedEcl = following.GetPositionEcl();
            if (!source.TryPose(Camera, followedEcl, inDeltaTime, out var offset, out var rotation))
                return; // hold: the camera keeps riding the followable on its previous offset

            Camera.PositionEcl = followedEcl + offset;
            Camera.LocalRotation = rotation;

            // Keep the stock input fields coherent with what was applied, so any fallback into
            // base.OnFrame (fault, source cleared) continues from this pose instead of a stale or —
            // fatally — zero CameraRotation.
            CameraOffset = offset;
            var forward = (-double3.UnitZ).Transform(rotation);
            if (forward.IsFinite() && !forward.IsNearlyZero())
                CameraRotation = forward;
        }
```

```csharp
// gatOS.GameMod/Game/Ksa/Camera/ViewportSeam.cs L33-L39
    private static readonly MethodInfo? FixedControllerSetter =
        typeof(GameViewport).GetProperty(nameof(GameViewport.FixedController), AnyInstance)
            ?.GetSetMethod(nonPublic: true);

    private static readonly MethodInfo? ModeSetter =
        typeof(ViewportBase).GetProperty(nameof(ViewportBase.Mode), AnyInstance)
            ?.GetSetMethod(nonPublic: true);
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `GameViewport.OnFrame(double)`, `FixedController`, `BaseCamera` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L30-L124) | hook, controller slot, camera |
| `ViewportBase.Mode` | [current/decomp/KSA/ViewportBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportBase.cs#L36-L36) | park in `CameraMode.Fixed` |
| `FixedController.OnFrame`, `CameraOffset`, `CameraRotation`, `OnSwitchOn` | [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L7-L60) | subclassed controller |
| `Camera.PositionEcl`, `LocalRotation`, `Following`, `SetFollow`, `OnFrame` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L110-L160) | pose write and follow |
| `KittenEva.PrepareWorker` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L319-L330) | reads the main camera for EVA input direction |

## Pitfalls and version notes

- 5402 deleted `KSA.Viewport`; `Mode` and `FixedController` became protected-set properties,
  hence the reflection seam. Both are unchanged in 5554.
- IVA and Map ownership are unsupported: their controllers overwrite their own contexts each frame.
- While gatOS owns the camera, EVA "forward" follows the shot, because `KittenEva.PrepareWorker`
  takes `CameraForwardCce` from `Program.GetMainCamera()`.
- `ortho_height` cannot be restored on release (no public getter, per gatOS notes).
- The repo records a driver bug found in the first live pass and a fix whose live recheck is
  pending; the `AGENTS.md` text still describes an older end-of-frame driver, while the code uses the
  in-phase controller described here.

## See also

- [Synthetic camera follow anchor](synthetic-camera-follow-anchor.md)
- [Timed command scheduler](timed-command-scheduler.md)
- [Frame hooks with an F2-proof fallback](frame-hooks-and-f2-fallback.md)
- [External API clients and SDK](external-api-clients.md)
- [Projected sticker decals](sticker-projected-decals.md)
- [gatOS overview](../overview.md)
- [Park the viewport in Fixed through the Mode setter, suppress the entry alert and keep CameraRotation coherent](../../../howto/camera/controllers/drive-the-main-camera-in-fixed-mode/drive-the-main-camera-in-fixed-mode-3.md) - how-to article demonstrating this feature
- [Fetch the protected setters once with GetSetMethod(nonPublic true) and park the viewport in Fixed](../../../howto/camera/controllers/replace-a-viewport-camera-controller/replace-a-viewport-camera-controller-2.md) - how-to article demonstrating this feature
- [Run per-frame camera work from a prefix on the main viewport's GameViewport.OnFrame](../../../howto/camera/controllers/understand-camera-modes-and-controllers/understand-camera-modes-and-controllers-4.md) - how-to article demonstrating this feature
- [Offset against the followed object sampled in the same call, with sampling and publishing on GameViewport.OnFrame](../../../howto/camera/controllers/write-the-camera-pose-in-phase-with-the-frame/write-the-camera-pose-in-phase-with-the-frame-2.md) - how-to article demonstrating this feature
- [Capture the baseline pose, follow, mode and tidal locking, blend back on release and detect a second writer](../../../howto/camera/main-view/borrow-the-main-camera-and-hand-it-back/borrow-the-main-camera-and-hand-it-back-4.md) - how-to article demonstrating this feature
- [Change the follow target with changeControl and alert false, then write the captured position back](../../../howto/camera/main-view/follow-a-point-that-is-not-a-vehicle/follow-a-point-that-is-not-a-vehicle-3.md) - how-to article demonstrating this feature
- [Compose each frame's pose from keyframed tracks, manual overrides and a captured baseline](../../../howto/camera/special-views/play-cinematic-camera-tracks/play-cinematic-camera-tracks-1.md) - how-to article demonstrating this feature
- [Release with a blend and shape tracks with named easing functions](../../../howto/camera/special-views/play-cinematic-camera-tracks/play-cinematic-camera-tracks-4.md) - how-to article demonstrating this feature

