---
type: How-To
title: Set CameraRotation before the mode and restore the previous mode explicitly because Fixed has no player exit
description: KSArmory records the main view's mode, follow target and field of view before its first write, and is the only thing that can put the mode back
category: camera
group: controllers
topic: drive-the-main-camera-in-fixed-mode
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/borrow-main-view-camera]
techniques: [state-record-and-restore]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials]
lifecycle: [frame:draw-ui, frame:celestials, frame:viewports]
game_types: [KSA.FixedController, KSA.GameViewport, KSA.ViewportBase, KSA.Camera, KSA.CameraMode, KSA.IFollowable]
tags: [camera, fixed-camera, restore, camera-mode, state-record]
---
Because nothing the player does leaves `Fixed`, a mod that puts the main view there owes the player the way back.
KSArmory solves it with a three-field record taken **before the first write** and one place that restores the
parts that are still the mod's. This article is the mode-restore half; the follow target and the ranked claims
are in [Borrow the main camera, hand it back and arbitrate between camera users](../../main-view/borrow-the-main-camera-and-hand-it-back/overview.md). Prerequisites: StarMap.

## Background: how the game does it

`Fixed` is the only mode with no player exit: `FixedController.OnKey` returns false and `GameViewport.NextCameraMode`
returns false from `Fixed` ([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L120-L123), [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L314-L330)). Leaving needs `SetCameraMode`,
which runs the incoming and outgoing controllers' `OnSwitchOn`/`OnSwitchOff` and clears the controlled vehicle's held
input ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L302-L312)). Two things in the camera that the player owns are not restored by the mode: the follow
target (`Camera.Following`) and the field of view (the zoom keys clamp to 15-120 degrees,
[current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L455-L462)).

## Lifecycle and timing

Recording and restoring both run in the mod's per-step method: from the `[StarMapAfterGui]` hook (`frame:draw-ui`), or
from a prefix on `Program.OnFrameCelestials` while the HUD is hidden (`frame:celestials`), so a borrowed view is handed back even
with the HUD off. The mode change itself is immediate; the camera moves at the next `frame:viewports`.

## Steps

1. **Record first.** The record holds what the view follows, its mode and its field of view in degrees:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L5135-L5148
public static MainView RememberMainView()
{
    try
    {
        if (Program.MainViewport is not { } viewport) return default;

        return new MainView(viewport.GetCamera()?.Following, viewport.Mode, MainViewFovDeg(), true);
    }
    catch (Exception e)
    {
        Log.Warn($"could not read the main view: {e.Message}");
        return default;
    }
}
```

   A record taken after `SetCameraMode(Fixed)` or `SetFollow` describes the borrowed state, and restoring from it leaves the
   player at a fixed point.

2. **Write rotation before the mode** as in [approach 1](drive-the-main-camera-in-fixed-mode-1.md) (same method in KSArmory:
   [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5522-L5575)).

3. **Restore the mode explicitly**, field of view first so a handover frame is drawn at the player's field:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L5615-L5637
public static bool BeginRestoreMainView(MainView saved)
{
    if (!saved.Valid) return false;

    try
    {
        if (Program.MainViewport is not { } viewport) return false;

        StopDrivingMainView();

        // Before the mode, so a frame drawn during the handover is drawn at the player's own
        // field rather than at the sight's.
        TrySetMainViewFov(saved.FovDeg);

        if (viewport.Mode != saved.Mode) viewport.SetCameraMode(saved.Mode);
        return true;
    }
    catch (Exception e)
    {
        Log.Warn($"could not restore the main view: {e.Message}");
        return false;
    }
}
```

4. **When the player takes one half, restore only the other.** A vessel switch changes the follow and leaves `Fixed`
   standing; a menu change takes the mode and leaves the follow. `StandDownMainView` returns the field of view in
   either case and restores the mode only if it is still `Fixed`:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L5229-L5243
public static bool StandDownMainView(MainView saved, IFollowable? followed)
{
    if (!saved.Valid) return false;

    bool modeIsOurs = MainViewIsFixed();
    bool followIsOurs = MainViewFollows(followed);

    StopDrivingMainView();
    TrySetMainViewFov(saved.FovDeg);

    if (followIsOurs && CanFollow(saved.Following)) RestoreFollow(saved);
    if (modeIsOurs) RestoreMainViewMode(saved);

    return modeIsOurs;
}
```

   `MainViewIsFixed()` ([src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5061-L5071)) is the test.

5. **Stop asking after a stand-down**, or the next step takes the view straight back.

## Complete example

- [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5135-L5148), [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5206-L5243), [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5615-L5637)
- [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L988-L1004): release on unload

## Variations and gotchas

- **KSAGolf restores the mode unconditionally**, so a player who switched mode by hand while golf held the view gets the old
  mode back; KSArmory restores it only if it is still `Fixed` ([Borrowing the main view and handing it back](../../../../mods/ksagolf/features/borrowed-chase-camera.md)).
- **Every take and hand-back clears held input**, and entering `Fixed` raises the entry alert.
- **A refused restore keeps the record and retries**; both KSArmory borrowers give up after 180 refused frames.

## See also

- [Topic overview](overview.md); [approach 1](drive-the-main-camera-in-fixed-mode-1.md), [3](drive-the-main-camera-in-fixed-mode-3.md)
- [Borrow the player's main view and hand it back](../../../../mods/ksarmory/features/borrow-main-view-camera.md)
- [Borrow the main camera, hand it back and arbitrate between camera users](../../main-view/borrow-the-main-camera-and-hand-it-back/overview.md), [Override the camera field of view and zoom past the stock clamp](../../main-view/override-the-camera-field-of-view/overview.md)
