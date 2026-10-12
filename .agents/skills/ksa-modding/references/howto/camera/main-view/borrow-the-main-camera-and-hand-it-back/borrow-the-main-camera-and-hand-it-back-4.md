---
type: How-To
title: Capture the baseline pose, follow, mode and tidal locking, blend back on release and detect a second writer
description: gatOS' director captures everything it will change at Take, restores it after a release blend, and stands down rather than fight when the controller faulted, another mod replaced it, or the mode or follow changed
category: camera
group: main-view
topic: borrow-the-main-camera-and-hand-it-back
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony, reflection]
demonstrated_by: [mods/gatos/features/programmable-camera-director]
techniques: [controller-subclass, state-record-and-restore]
hooks: [harmony-prefix KSA.GameViewport.OnFrame]
lifecycle: [frame:viewports]
game_types: [KSA.Camera, KSA.CameraMode, KSA.GameViewport, KSA.FixedController, KSA.IFollowable, KSA.Program]
tags: [camera, borrow, restore, blend, stand-down, gatos]
---
gatOS lets a guest program take the main camera, play shots and release it. The director captures everything it will change, restores all of it after an optional blend, and checks every frame that it still owns the camera. If it does not, it
**stands down and leaves the reclaimer's change alone** instead of fighting. The last half of this article covers
**arbitrating between features and mods**. Prerequisites: StarMap, Harmony and the controller install ([Replace a viewport's camera controller and take input through it](../../controllers/replace-a-viewport-camera-controller/overview.md)).

## Background: how the game does it

The camera has more state than the mode and the follow target: `Following`, `TidalLocking`, `NoRotation`, `LocalPosition`, `LocalRotation`, the field of view (radians from `GetFieldOfView`, degrees into `SetFieldOfView`) and the
orthographic flag ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L43-L60), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L417-L421), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L790-L793)). `TidalLocking` has no setter, so changing it takes a `SetFollow` (or `ToggleTidalLocking`). `SetFollow` teleports the camera ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L602-L618)), so the
director undoes the jump.

## Lifecycle and timing

`Take`, `Update` and `Restore` run on the main thread from the prefix on the main `GameViewport.OnFrame` (`frame:viewports`), i.e. before the controller and `Camera.OnFrame` of the same viewport, where mode and follow changes are safe. The pose itself is applied
by the controller afterwards ([Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md)).

## Steps

1. **Capture at Take, before any write.** Everything the director will change, plus a crash guard so a fallback into stock `OnFrame` never sees a zero rotation:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs L234-L245
_restoreCamera = active;
_restoreMode = viewport.Mode;
_restoreFollowing = active.Following;
_restoreTidal = active.TidalLocking;
_restoreNoRotation = active.NoRotation;
_restoreLocalPosition = active.LocalPosition;
_restoreLocalRotation = active.LocalRotation;
_restoreFovDeg = active.GetFieldOfView() * RadToDeg; // getter is RADIANS, setter is DEGREES
_restoreOrtho = active.Orthographic;
_restorePositionEcl = active.PositionEcl;
_restoreFixedOffset = controller.CameraOffset;
_restoreFixedRotation = controller.CameraRotation;
```

2. **Take the camera with a mod-owned follow** and undo the teleport, then park in `Fixed`:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs L247-L264
// Crash guard before any path can run the stock OnFrame: a zero CameraRotation is the
// documented DivideByZero crash. Seeded from the capture so a fallback holds the player's view.
var capturedForward = (-double3.UnitZ).Transform(_restoreLocalRotation);
if (capturedForward.IsFinite() && !capturedForward.IsNearlyZero())
    controller.CameraRotation = capturedForward;
controller.CameraOffset = double3.Zero;
controller.ClearFault();
controller.Pose = this;

_followable.Hold(_restorePositionEcl);

var owned = viewport.BaseCamera;
owned.NoRotation = false; // insurance: a stuck map flag would re-interpret every write below
owned.SetFollow(_followable, tidalLocking: false, changeControl: false, alert: false);
owned.PositionEcl = _restorePositionEcl; // undo SetFollow's 2.5 m teleport
owned.LocalRotation = _restoreLocalRotation;

ParkMode(viewport);
```

3. **Release by blending to the captured pose** (`camera_release_blend_s`), then restore everything: controller fields, follow (or unfollow when the old target is gone), `NoRotation`, local position and rotation, field of view, orthographic flag and mode:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs L365-L388
var viewport = Program.MainViewport;
var camera = _restoreCamera ?? viewport.BaseCamera;

if (_controller is { } controller)
{
    controller.Pose = null;
    controller.CameraOffset = _restoreFixedOffset;
    if (_restoreFixedRotation.IsFinite() && !_restoreFixedRotation.IsNearlyZero())
        controller.CameraRotation = _restoreFixedRotation;
}

if (CameraTargets.IsLive(_restoreFollowing) && _restoreFollowing is { } following)
    camera.SetFollow(following, _restoreTidal, changeControl: false, alert: false);
else
    camera.Unfollow(changeControl: false);

camera.NoRotation = _restoreNoRotation;
camera.LocalPosition = _restoreLocalPosition;
camera.LocalRotation = _restoreLocalRotation;
camera.SetFieldOfView((float)_restoreFovDeg);
camera.SetOrthographic(_restoreOrtho);

if (_restoreMode == CameraMode.Map || !ViewportSeam.TrySetMode(viewport, _restoreMode))
    viewport.SetCameraMode(_restoreMode);
```

## Arbitrate between features and mods

4. **Every frame, check you still own the camera.** Four conditions, each a state gatOS did not create. On any, `StandDown` leaves the camera where the reclaimer put it:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs L536-L560
// Ownership-loss ladder (KSArmory's stand-down doctrine): a state gatOS did not create means
// someone else has the camera. Never fight a second writer frame-by-frame.
if (_controller is not { } controller || controller.Faulted)
{
    StandDown("the in-phase pose solve faulted");
    return;
}

if (!ReferenceEquals(viewport.FixedController, controller))
{
    StandDown("another mod replaced the fixed-camera controller");
    return;
}

if (viewport.Mode != CameraMode.Fixed)
{
    StandDown("the camera mode changed outside gatOS (the view was reclaimed)");
    return;
}

if (!ReferenceEquals(camera.Following, _followable))
{
    StandDown("the camera's follow target changed outside gatOS");
    return;
}
```

5. **A stand-down does not restore the captured pose.** Restoring over somebody else's change would be a second fight:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs L413-L429
private void StandDown(string reason)
{
    _phase = Phase.Idle;
    try
    {
        if (_timeCaptured)
            Universe.SetSimulationSpeed(_restoreSimSpeed, alert: false);
        if (_controller is { } controller)
            controller.Pose = null;
        playback?.Execute(new SimCommand("", CameraCommands.StopAction, SimCommand.NoOrdinal, 1));
        ModLog.Log.Info($"gatOS camera: standing down — {reason}.");
    }
    finally
    {
        ClearOwnershipState();
    }
}
```

## Complete example

- [gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs#L213-L300) (Take), [gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs#L353-L404) (Restore), [gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs#L413-L429) (StandDown)
- [gatOS.GameMod/Game/Ksa/Camera/CameraPoseController.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraPoseController.cs): the controller whose `Faulted` flag the director reads

## Variations and gotchas

- **Orthographic height cannot be restored** (no public getter); the notes in the repo say so.
- **Time scale.** If a shot changed the simulation speed, `Restore` and `StandDown` both put it back; that change is invisible to the reclaimer.
- **Map and IVA ownership are unsupported**; leaving Map goes through `SetCameraMode` so `MapController.OnSwitchOff` runs.
- **The follow is an anchor object**: detecting a changed follow is `ReferenceEquals(camera.Following, _followable)` ([Make the camera follow a point or mod-simulated object, and change the follow target without a jump](../follow-a-point-that-is-not-a-vehicle/overview.md)).
- **Wreckage.** When a followed vessel is destroyed the engine points cameras at wreckage; with the mod anchor followed instead this is not an issue, but a restored follow is checked with `IsLive` first.

## See also

- [Topic overview](overview.md); [approach 1](borrow-the-main-camera-and-hand-it-back-1.md), [3](borrow-the-main-camera-and-hand-it-back-3.md), [5](borrow-the-main-camera-and-hand-it-back-5.md)
- [Programmable camera director](../../../../mods/gatos/features/programmable-camera-director.md)
- [Make the camera follow a point or mod-simulated object, and change the follow target without a jump](../follow-a-point-that-is-not-a-vehicle/overview.md), [Play scripted cinematic camera tracks with blends and easing](../../special-views/play-cinematic-camera-tracks/overview.md)
