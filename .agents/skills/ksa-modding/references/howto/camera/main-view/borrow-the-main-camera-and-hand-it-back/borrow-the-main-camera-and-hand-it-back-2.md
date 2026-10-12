---
type: How-To
title: Hand back the field of view first and the follow only if it is still the borrower's, with explicit StandDown, Yield, Hold, GiveBack and Take decisions
description: KSAGolf keeps the borrowing rules in a KSA-free Sim layer with a thin KsaWorld adapter, always returns the field of view and restores the follow only when it is unchanged; the ViewClaim decision table is headless-tested
category: camera
group: main-view
topic: borrow-the-main-camera-and-hand-it-back
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/borrowed-chase-camera]
techniques: [state-record-and-restore, camera-arbitration]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:viewports]
game_types: [KSA.Program, KSA.Camera, KSA.CameraMode, KSA.GameViewport, KSA.IFollowable, KSA.Vehicle]
tags: [camera, borrow, restore, fov, view-claim, headless-tests]
---
KSAGolf borrows the main view for putting, first person, the course builder and bridge shots. The mod's design rule is to keep the
decisions in `Sim/` with no KSA types (so a unit test can run them) and only the camera reads and writes in `Ksa/KsaWorld.cs`. This article
covers the hand-back order in the adapter and the decision table; the ranking between several claimants is the "Arbitrate between features and mods"
section below.

**Status at the pinned ref.** `ViewClaim`, `ChaseView`, `SmoothedStep` and `ChaseOrbit`'s drag logic have no caller in KSAGolf's game code; the
tests use them, and KSArmory runs fuller versions ([Borrowing the main view and handing it back](../../../../mods/ksagolf/features/borrowed-chase-camera.md)). What is wired is `RememberMainView`,
`TryHandBackMainView` and the two-half rule in `KsaWorld`.

## Background: how the game does it

The same engine facts as [approach 1](borrow-the-main-camera-and-hand-it-back-1.md): the follow and the mode are independent halves that the player can take separately,
`Fixed` has no key exit ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L314-L330)), and the zoom keys clamp at 15-120 degrees so a narrow field cannot be widened
back by the player ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L455-L462)). `SetFieldOfView` does not clamp and the projection builder throws for a field of view of 0 or of at least pi radians
(`CreatePerspectiveFieldOfViewReverseZ`), and `UpdateProjection` throws for a non-invertible projection ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L417-L421), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L471-L485)).

## Lifecycle and timing

`RememberMainView`, `TryLookFromMainViewport` and `TryHandBackMainView` run inside the step from the `[StarMapAfterGui]` hook
(`frame:draw-ui`), after this frame's `OnFrameViewports`. A mode switch only changes which controller the next viewport pass runs, but
`SetFollow` moves the camera at once (it places it 2.5 mean radii from the target), and the frame being finished is still rendered from that
position, so a borrower that follows something new must put the position back or accept one odd frame (approach 1 does the former). The FOV
is restored before the mode so a handover frame is drawn at the player's field ([game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)).

## Steps

1. **Record before the first write** (`RememberMainView`; the record carries `Following`, `Mode`, `FovDeg`, `Valid`):

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1612-L1625
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

2. **Read whether the follow is still yours before you write anything**, then restore in order: take the mod off the controller, FOV, then mode; follow only if it is still ours
   and restorable:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1683-L1692
public static bool TryHandBackMainView(MainView saved, IFollowable? followed,
                                       out bool mode, out bool follow)
{
    bool followIsOurs = MainViewFollows(followed);

    mode = BeginRestoreMainView(saved);
    follow = !followIsOurs || !CanFollow(saved.Following) || RestoreFollow(saved);

    return mode && follow;
}
```

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1901-L1923
public static bool BeginRestoreMainView(MainView saved)
{
    if (!saved.Valid) return false;

    try
    {
        if (Program.MainViewport is not { } viewport) return false;

        StopDrivingMainView();

        // Before the mode, so a frame drawn during the handover is drawn at the player's own
        // field rather than at the borrower's.
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

3. **Never restore over a destroyed craft.** The engine has already moved the view to the wreckage, so `CanFollow` says no for a disposed vehicle
   ([src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1636-L1641)).

4. **Return the field of view unconditionally**, since the mod always changed it. The field of view is part of the record because the
   player's zoom keys cannot widen a narrower value back.

## Arbitrate between features and mods

`ViewClaim` encodes the decisions as pure functions. The snippet below is KSArmory's copy, the one its borrowers call in game; KSAGolf's trimmed copy has the same shape but no in-game caller. `StillOurs` is true only if the view is in the mode the borrower set **and** on the object it pointed
at, unless a stronger claim inside the mod is driving; `ForOptic` orders the rungs:

```csharp
// src/KSArmory/Sim/ViewClaim.cs L77-L87
public static ViewAction ForOptic(bool wanted, bool resolved, bool outranked,
                                  bool holding, bool stillOurs)
{
    if (holding && !stillOurs) return ViewAction.StandDown;
    if (holding && outranked) return ViewAction.Yield;
    if (holding) return wanted && resolved ? ViewAction.Hold : ViewAction.GiveBack;

    // Never take it out from under a stronger claim. Waiting rather than queueing: the chase
    // gives the view back on its own, and the next frame's Take picks it up.
    return wanted && resolved && !outranked ? ViewAction.Take : ViewAction.Idle;
}
```

| Action | Meaning |
|---|---|
| `StandDown` | The player took the view: forget the recording, restore nothing, and stop asking |
| `Yield` | A stronger claim is driving: write nothing, keep the recording |
| `Hold` / `GiveBack` | Holding and still wanted (or not): drive again or put back |
| `Take` / `Idle` | Not holding: take only when nothing outranks it |

KSAGolf's copy ([src/KSAGolf/Sim/ViewClaim.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/ViewClaim.cs)) is tested by 20 tests in [tests/KSAGolf.Tests/ViewClaimTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/ViewClaimTests.cs); the in-game ladder is
[approach 5](borrow-the-main-camera-and-hand-it-back-5.md).

## Complete example

- [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1612-L1692): the adapter
- [src/KSAGolf/Sim/ViewClaim.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/ViewClaim.cs): the rules (tested only)
- [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs): the user (`DriveView`, `Suspended`, `LetGo`)

## Variations and gotchas

- **KSAGolf restores the mode unconditionally.** `TryHandBackMainView` checks the follow, but `BeginRestoreMainView` sets the saved mode whenever the current one differs, so a player who switched mode by hand while golf held the view gets the old mode back. KSArmory's `StandDownMainView` restores it only if it is still `Fixed`. Golf re-asserts `Fixed` every frame it wants the view, so it rarely arises.
- **Trimmed copies.** The golf files are trimmed copies of KSArmory's; a fix in one does not reach the other.
- **Sample, do not store.** The ecliptic is inertial, so a stored point falls about half a kilometre behind per frame at orbital speeds.

## See also

- [Topic overview](overview.md); [approach 1](borrow-the-main-camera-and-hand-it-back-1.md), [3](borrow-the-main-camera-and-hand-it-back-3.md), [5](borrow-the-main-camera-and-hand-it-back-5.md)
- [Borrowing the main view and handing it back](../../../../mods/ksagolf/features/borrowed-chase-camera.md)
- [Override the camera field of view and zoom past the stock clamp](../override-the-camera-field-of-view/overview.md), [Drive the main camera through Fixed mode without crashing it, with a stable up vector](../../controllers/drive-the-main-camera-in-fixed-mode/overview.md)
