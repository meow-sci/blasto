---
type: How-To
title: Change the follow target with changeControl and alert false, then write the captured position back
description: gatOS' director follows its own anchor with SetFollow(changeControl false, alert false) and restores the captured position and rotation because SetFollow teleports the camera, and uses the same pair to change tidal locking
category: camera
group: main-view
topic: follow-a-point-that-is-not-a-vehicle
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/programmable-camera-director]
techniques: [direct-camera-write]
hooks: [harmony-prefix KSA.GameViewport.OnFrame]
lifecycle: [frame:viewports]
game_types: [KSA.Camera, KSA.IFollowable, KSA.Program, KSA.Vehicle]
tags: [camera, follow, setfollow, teleport, tidal-locking]
---
`Camera.SetFollow` is the one call that changes what the camera follows, and it has two side effects a mod rarely wants: it teleports the camera, and it can change who the player controls.
This article covers **changing the follow target without a jump**: gatOS' director follows its own anchor and undoes the jump in the same breath. It uses the
anchor from [approach 1](follow-a-point-that-is-not-a-vehicle-1.md).

## Background: how the game does it

```csharp
// current/decomp/KSA/Camera.cs L602-L631
public void SetFollow(IFollowable target, bool tidalLocking, bool changeControl = true, bool alert = true)
{
	_wreckageMarker = null;
	bool flag = _following != target;
	double3 double5 = (double)(float)target.MeanRadius * 2.5 * GetForwardEcl();
	PositionEcl = target.GetPositionEcl() + double5;
	_following = target;
	_tidalLocking = tidalLocking;
	if (flag && alert)
	{
		AlertFollowing();
	}
	if (changeControl)
	{
		Program.ControlledVehicle = target as Vehicle;
	}
}

public void Unfollow(bool changeControl = true)
{
	double3 positionEcl = PositionEcl;
	_following = null;
	_wreckageMarker = null;
	PositionEcl = positionEcl;
	_tidalLocking = false;
	if (changeControl)
	{
		Program.ControlledVehicle = null;
	}
}
```

- The camera moves to `target.GetPositionEcl() + MeanRadius * 2.5 * forward` (the radius narrowed through `float`).
- `alert` raises a "Following ..." alert when the target changed; `changeControl` (default true) sets `Program.ControlledVehicle = target as Vehicle`, which is null for a non-vehicle.
- `Unfollow` preserves `PositionEcl`.
- `TidalLocking` has no setter. `SetFollow` writes it, and `ToggleTidalLocking()` flips it in place without moving the camera ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L851-L854)), so `if (camera.TidalLocking != wanted) camera.ToggleTidalLocking();` is enough when only the flag changes. gatOS re-issues `SetFollow` instead and writes the position back (its notes record `SetFollow` as the only writer).

## Lifecycle and timing

Done at `Take` in the director's update, called from the prefix on the main `GameViewport.OnFrame` (`frame:viewports`), before the controller and `Camera.OnFrame` of that viewport, so the
teleport is undone before anything uses it ([reference, approach 4](../../controllers/understand-camera-modes-and-controllers/overview.md)).

## Change the follow target without a jump

1. **Capture the pose first** (`PositionEcl`, `LocalRotation`) and seed the anchor at the captured position, so the follow starts where the camera is:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs L234-L245, L256-L256
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
// ...
_followable.Hold(_restorePositionEcl);
```

2. **Follow with both flags false, then write the capture back**, which undoes the 2.5 radii teleport (2.5 m for the 1 m anchor):

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs L258-L262
var owned = viewport.BaseCamera;
owned.NoRotation = false; // insurance: a stuck map flag would re-interpret every write below
owned.SetFollow(_followable, tidalLocking: false, changeControl: false, alert: false);
owned.PositionEcl = _restorePositionEcl; // undo SetFollow's 2.5 m teleport
owned.LocalRotation = _restoreLocalRotation;
```

3. **Use the same pair to change tidal locking**: the `camera/tidal` command re-issues `SetFollow` with the new flag on both the base and map cameras, then restores each position, and is refused while the director owns the camera ([gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs#L1082-L1156)).

4. **On release, re-follow the old target only if it is still live**, else unfollow:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs L376-L380
if (CameraTargets.IsLive(_restoreFollowing) && _restoreFollowing is { } following)
    camera.SetFollow(following, _restoreTidal, changeControl: false, alert: false);
else
    camera.Unfollow(changeControl: false);
```

## Complete example

- [gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs#L213-L300) (Take) and [gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs#L353-L404) (Restore)

## Variations and gotchas

- **Not unfollowing keeps the engine's bookkeeping live.** gatOS follows its anchor rather than unfollowing so `NearbyCelestial`, `CurrentAltitudeKm` and `ClampCamera` keep working per camera (the notes in `CameraDirector`).
- **Read the pose first.** After `SetFollow` the camera is already at the target, so a reading taken afterwards is the destination, not the start ([Borrow the main camera, hand it back and arbitrate between camera users](../borrow-the-main-camera-and-hand-it-back/overview.md)).
- **Without `alert: false`** every take raises an alert in the footage.

## See also

- [Topic overview](overview.md); [approach 1](follow-a-point-that-is-not-a-vehicle-1.md), [4](follow-a-point-that-is-not-a-vehicle-4.md), [5](follow-a-point-that-is-not-a-vehicle-5.md)
- [Programmable camera director](../../../../mods/gatos/features/programmable-camera-director.md)
- [Borrow the main camera, hand it back and arbitrate between camera users](../borrow-the-main-camera-and-hand-it-back/overview.md), [Taking control of another vehicle](../../../vehicles-and-parts/vehicles/take-control-of-another-vehicle/overview.md)
