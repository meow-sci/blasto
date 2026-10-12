---
type: How-To
title: Follow on the main viewport without taking control, with follow and mode kept as separate halves
description: KSArmory's TryFollowOnMainViewport points the main camera at a mod object with SetFollow(changeControl false), because following a non-Vehicle with the default would null the player's controlled vehicle
category: camera
group: main-view
topic: follow-a-point-that-is-not-a-vehicle
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/borrow-main-view-camera]
techniques: [state-record-and-restore]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Camera, KSA.IFollowable, KSA.Program, KSA.Vehicle]
tags: [camera, follow, changecontrol, controlled-vehicle, borrow]
---
Following something and controlling something are two different things in KSA, but `Camera.SetFollow` couples them by default. KSArmory wraps the safe form once in `KsaWorld.TryFollowOnMainViewport`, and every borrower goes through it. This article covers
**changing the follow target without a jump** as it applies to a borrowed main view: follow and mode are separate halves, and following a non-`Vehicle` with `changeControl: true` would null the player's controlled vehicle.

## Background: how the game does it

`SetFollow(target, tidalLocking, changeControl = true, alert = true)` ends with `if (changeControl) Program.ControlledVehicle = target as Vehicle;` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L602-L618)). For a round or any other
mod object that evaluates to null, so the player loses their craft and the flight HUD with it. `Program.ControlledVehicle` is its own property ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502-L516)); setting it does not change what any camera follows.

## Lifecycle and timing

Called from the borrower's step in the GUI hook (`frame:draw-ui`) after the view was recorded and the player's pose was read; the new follow shows from the next viewport pass. The jump `SetFollow` makes is rendered unless the
old position is written back before the frame ends ([approach 3](overview.md), [article](follow-a-point-that-is-not-a-vehicle-3.md)).

## Change the follow target without a jump

1. **Wrap the safe call once:**

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L5250-L5266
public static bool TryFollowOnMainViewport(IFollowable target)
{
    try
    {
        if (Program.MainViewport?.GetCamera() is not { } camera) return false;

        // changeControl false, or Program.ControlledVehicle becomes `target as Vehicle`, which
        // for anything that is not a vehicle is null -- the player loses their craft.
        camera.SetFollow(target, tidalLocking: false, changeControl: false, alert: false);
        return true;
    }
    catch (Exception e)
    {
        Log.Warn($"could not follow: {e.Message}");
        return false;
    }
}
```

2. **Record, read the pose, follow, place:** the chase records the main view, reads where the player's eye is *before* attaching the round, follows, and writes the old position back so the frame after `SetFollow` does not render beside the missile:

```csharp
// src/KSArmory/Ksa/ChaseCamera.cs L465-L500
_saved = KsaWorld.RememberMainView();
if (!_saved.Valid)
{
    // Said, not swallowed. A silent return here is indistinguishable from the chase
    // being switched off, and looks like rounds simply stopping being followed.
    Log.Warn("chase: cannot read the main view, not taking it");
    return;
}

// Read where the player had the view BEFORE anything is attached to the round.
// Camera.SetFollow sets PositionEcl to the followed object plus 2.5 mean radii, and
// a round's mean radius is one metre -- so the instant the follow is swapped the
// camera is 2.5 m from the missile. Reading afterwards gives that, not the player's
// pose, and the transition then eases from its own destination: no travel at all,
// just the aim swinging from a point already at the round.
bool hasPose = KsaWorld.TryMainCameraPose(out double3 wasEcl, out _);

_followed.Track(round, system);

if (!KsaWorld.TryFollowOnMainViewport(_followed))
{
    Log.Warn("chase: the view refused to follow the round");
    _saved = default;
    return;
}

if (hasPose)
{
    // Undo the jump SetFollow just made. This frame's view matrix was built in the
    // viewport pass, which is over, and the controller does not pick up the offset
    // until the next frame -- so without this one frame renders from beside the
    // missile before the transition has begun.
    KsaWorld.TryPlaceMainCamera(wasEcl);

    _fromOffset = wasEcl - system.PlatformEcl;
    _blend = 0.0;
```

3. **Only if the view is already on the right craft.** The chase takes the view only when `MainViewFollows(system.Platform)`, so a site elsewhere cannot hijack it.

4. **Give the follow back separately from the mode**, and only if it is still the borrower's ([Borrow the main camera, hand it back and arbitrate between camera users](../borrow-the-main-camera-and-hand-it-back/overview.md)).

## Complete example

- [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5250-L5266) and [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4746-L4762)
- [src/KSArmory/Ksa/ChaseCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ChaseCamera.cs#L465-L500)

## Variations and gotchas

- **Follow and mode are separate halves.** The View menu changes the mode and keeps the follow; a vessel switch changes the follow and keeps the mode.
- **A follow target can disappear.** Restoring over a disposed vehicle holds the view on a dead craft; `CanFollow` refuses it.
- **Do not pass `alert: true`** from a per-frame call; the alert raises when the target changed, and the borrower changes it on take.

## See also

- [Topic overview](overview.md); [approach 2](follow-a-point-that-is-not-a-vehicle-2.md), [3](follow-a-point-that-is-not-a-vehicle-3.md), [5](follow-a-point-that-is-not-a-vehicle-5.md)
- [Borrow the player's main view and hand it back](../../../../mods/ksarmory/features/borrow-main-view-camera.md)
- [Borrow the main camera, hand it back and arbitrate between camera users](../borrow-the-main-camera-and-hand-it-back/overview.md), [Taking control of another vehicle](../../../vehicles-and-parts/vehicles/take-control-of-another-vehicle/overview.md)
