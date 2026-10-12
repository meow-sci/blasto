---
type: Mod Feature
title: Borrowing the main view and handing it back
description: "KSA-free rules for borrowing the player's main view: who may hold it, a hand-back in two halves (mode and follow) with the field of view always returned, a chase pose, KSA-style orbit drag, a carried roll and an evened-out step"
mod: ksagolf
category: camera
techniques: [headless-tests, camera-arbitration, controller-subclass]
hooks: [starmap StarMapAfterGui, subclass KSA.FixedController.OnFrame (via IViewPose)]
lifecycle: [frame:draw-ui, frame:viewports, frame:input]
game_types:
  - KSA.Camera
  - KSA.IGameViewport
  - KSA.GameViewport
  - KSA.CameraMode
  - KSA.IFollowable
  - KSA.Vehicle
  - KSA.FixedController
  - KSA.Universe
tags: [camera, chase-camera, view-borrowing, hand-back, roll, orbit, easing, headless, sim]
---

A mod that drives the player's main camera borrows something the player cannot easily take back.
`FixedController` reads no input, and the camera-mode key has no `Fixed` case.[^ksa-fixedcontroller][^ksa-gameviewport]
KSAGolf keeps the rules for borrowing in `Sim/` (no KSA types, unit-tested) and only the camera
writes in `Ksa/KsaWorld.cs`. Five pieces make up the kit:

| Piece | What it decides |
|---|---|
| `ViewClaim` | Who may hold the main view this frame, and what the loser does |
| `ChaseView` | Where a camera riding behind a body in flight stands and looks, how the player orbits it, how it eases in |
| `ChaseOrbit` | Right-drag and wheel around a chased body, with KSA's own orbit constants, easing back when let go |
| `StableUp` | The roll reference, carried from frame to frame so it turns rather than flips |
| `SmoothedStep` | An evened-out simulated step, for cosmetic eases only |

**What is wired in game at this ref.**

- `StableUp` levels every borrowed view through the
  [level-horizon controller](../features/level-horizon-camera-controller.md).
- `HeadLook` reuses `ChaseOrbit`'s drag constants.
- Every borrower (putting, first person, the course builder, the bridge's `frame`) uses the
  `MainView` record and the two-half hand-back in `KsaWorld`.

`ChaseView`, `ViewClaim`, `SmoothedStep` and `ChaseOrbit`'s own drag logic have **no caller in the
game code** at this ref; only their tests use them. The mod's CLAUDE.md lists them as what
"watching the ball" will build on.[^golf-claude] Their in-game counterparts live in KSArmory, whose
`ChaseCamera` and `SightCamera` call fuller versions of the same files.[^armory-chasecamera][^armory-sightcamera]

## How it works

**Recording and handing back (`KsaWorld`, in use).**

- `RememberMainView()` captures `MainView(Following, Mode, FovDeg, Valid)` **before** the first
  write. The field of view is part of the record because a borrower may zoom, and the player's zoom
  keys clamp at 15° and cannot widen back past a narrower value.[^ksa-camera]
- `TryHandBackMainView(saved, followed)`:
  1. First reads whether the view still follows what the borrower pointed it at.
  2. Calls `BeginRestoreMainView`, which takes the mod off the controller, restores the **FOV
     first** so a handover frame is drawn at the player's field, then sets the saved mode if it
     differs.
  3. Restores the follow only if the view still follows the borrower's object **and** the saved
     target can still be followed. A destroyed craft is the ordinary case, and the engine has
     already moved the view to the wreckage.[^golf-ksaworld][^ksa-universe]

**The full two-halves rule (`ViewClaim`, tested only).**

- `StillOurs(inTakenMode, followsWhatWePointedAt, outranked)` is true only if the view is in the
  mode the borrower set **and** on the object it pointed at, unless a stronger claim inside the mod
  is driving. Each half can be taken without the other: the camera-mode menu changes the mode and
  leaves the follow; switching vessels changes the follow and leaves the mode.
- `ForOptic` orders the rungs:
  1. **StandDown** if the player has taken the view (forget the recording, restore nothing).
  2. **Yield** to a stronger claim (write nothing, keep the recording).
  3. Otherwise **Hold** or **GiveBack**.
  4. When not holding, **Take** only when not outranked, else **Idle**.
- `OverlayPaints` ties painting an overlay to driving the picture.

The 20 tests in `ViewClaimTests` pin each rung, including the cases where a vessel switch or a mode
change takes the view.[^golf-viewclaim][^golf-test-viewclaim]

**Riding a body (`ChaseView`, tested only).**

- `TryPose` puts the eye `distanceBehind` back along the line from the body to what it is flying at,
  held within 80° of the flight path so a miss slides to the edge instead of whipping round. It lifts
  the eye by a lift vector carried from the last frame, so passing the vertical does not swap sides.
  It looks along that axis at the target's range.
- `LeanOffAxis` keeps every forward vector at least about 2.6° off ecliptic +Z, the axis the stock
  `FixedController` crosses against.
- `Orbit` turns eye, view and up together about the local vertical and the view's right, and gives
  up pitch (found by bisection) rather than going below a floor.
- `StandOff` closes the camera in on a root curve as range runs out.
- `TryBlend` smoothsteps the eye from the player's pose to the chase. It lerps the look between body
  and target **at equal depth**, so the turn spreads over the ease. It insists every point is sampled
  this frame.[^golf-chaseview]

**Looking around it (`ChaseOrbit`).**

- It mirrors KSA's orbit camera: right button, a 2 px² threshold before a press becomes a drag,
  0.003 rad per pixel, ×1.1 per wheel notch, zoom 0.25-6×, pitch ±80°.
- After 0.4 s without input it eases back at 3 per second.
- `Advance(seconds, buttonHeld)` ends a drag whose release was swallowed over a panel.
- It is meant to be advanced on `SimClock.Viewing`: player time that still stops while
  paused.[^golf-chaseorbit][^golf-test-chaseorbit]

**Roll and step.**

- `StableUp.Try` corrects the carried up towards the wanted one by at most `maxStep × authority`,
  where authority is the sine of the angle between the wanted up and the view.
- `SmoothedStep.Next` is an exponential average (weight 0.15) of the simulated step. It exists
  because KSA's step reports display pacing: 8.33 and 25 ms alternating on a 120 Hz screen at a
  nominal 60 fps. A paused or unusable step returns 0 and leaves the average alone, and the first
  step seeds it.[^golf-stableup][^golf-smoothedstep][^golf-test-smoothedstep]

## Lifecycle and timing

| Step | Where it runs | Lifecycle |
|---|---|---|
| `RememberMainView`, `TryLookFromMainViewport`, `TryHandBackMainView` | `Golf.DriveView`, `Golf.Suspended`, `Golf.LetGo`, the course builder's enter and leave, inside the step from the GUI postfix | `frame:draw-ui` |
| `StableUp.Try`, and any `IViewPose.TryPose` built on `ChaseView` | `LevelHorizonController.OnFrame` in `Program.OnFrameViewports` | `frame:viewports` |
| `IMouseDrag.Press` / `Move` / `Scroll` on a `ChaseOrbit` or `HeadLook` | Controller input overrides during `Glfw.PollEvents` | `frame:input` |
| Bridge hand-back on unload | `Bridge.Release` from `[StarMapUnload]`, which StarMap calls only at process exit, after the game has shut down | (shutdown) |

**Why the hand-back sits in the step.**

- The mode switch and `SetFollow` change what `OnFrameViewports` does on the next frame. Doing them
  from the GUI postfix, after this frame's viewport pass, means no frame is drawn half borrowed.
- The FOV is restored before the mode for the same reason.
- `ChaseOrbit` eases on the viewing clock (player time) because it answers a hand on the mouse. At a
  tenth of normal speed a simulated ease would leave the view off the body ten times as long.
- The chase pose itself should be computed in the controller's `OnFrame` against the followed
  position the engine passes in. A separation sampled in the GUI hook carries a frame of
  motion.[^golf-levelhorizon]

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Sim/ViewClaim.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/ViewClaim.cs) | `ViewAction` ladder, `StillOurs`, `OverlayPaints` |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1596-L1692) | `MainView` record, `RememberMainView`, `CanFollow`, `RestoreFollow`, `TryHandBackMainView` |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1876-L1923) | `StopDrivingMainView`, `BeginRestoreMainView` (FOV before mode) |
| [src/KSAGolf/Sim/ChaseView.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/ChaseView.cs) | Chase pose, orbit, lean off the singular axis, stand-off, blend |
| [src/KSAGolf/Sim/ChaseOrbit.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/ChaseOrbit.cs) | KSA-style drag and wheel with ease back |
| [src/KSAGolf/Sim/StableUp.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/StableUp.cs) | Carried roll |
| [src/KSAGolf/Sim/SmoothedStep.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/SmoothedStep.cs) | Evened step for cosmetic eases |
| [tests/KSAGolf.Tests/ViewClaimTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/ViewClaimTests.cs) | The ladder and the two halves |
| KSArmory [src/KSArmory/Ksa/ChaseCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ChaseCamera.cs#L200-L205) | In-game `StillOurs` consumer (mode is Fixed, follow is the round) |
| KSArmory [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5229-L5243) | `StandDownMainView`: restores only the half the player did not take |

## Snippets

```csharp
// src/KSAGolf/Sim/ViewClaim.cs L77-L87, L113-L114
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
    // ...
    public static bool StillOurs(bool inTakenMode, bool followsWhatWePointedAt, bool outranked)
        => inTakenMode && (outranked || followsWhatWePointedAt);
```

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1683-L1692, L1909-L1915
    public static bool TryHandBackMainView(MainView saved, IFollowable? followed,
                                           out bool mode, out bool follow)
    {
        bool followIsOurs = MainViewFollows(followed);

        mode = BeginRestoreMainView(saved);
        follow = !followIsOurs || !CanFollow(saved.Following) || RestoreFollow(saved);

        return mode && follow;
    }
    // ...
            StopDrivingMainView();

            // Before the mode, so a frame drawn during the handover is drawn at the player's own
            // field rather than at the borrower's.
            TrySetMainViewFov(saved.FovDeg);

            if (viewport.Mode != saved.Mode) viewport.SetCameraMode(saved.Mode);
```

```csharp
// src/KSAGolf/Sim/StableUp.cs L41-L64
        bool wanted = Across(forward, preferredUp, out double3 target, out double authority);
        bool carried = Across(forward, lastUp, out double3 held, out _);

        // Nothing carried yet, or nothing to carry towards: whichever exists is the answer.
        if (!carried) { up = target; return wanted; }
        if (!wanted) { up = held; return true; }

        // How far the wanted up may pull this frame. Scaled by how much of it lies across the
        // view: none of it does when the view is along it, and that is exactly when its direction
        // is meaningless — so it stops pulling instead of being switched away from.
        double step = Math.Max(0.0, maxStepRad) * authority;
        double apart = Vec.AngleBetween(held, target);

        if (apart <= step || step <= 0.0)
        {
            up = apart <= step ? target : held;
            return true;
        }

        double3 axis = Vec.Cross(held, target);
        if (Vec.Len2(axis) < 1e-18) { up = held; return true; }

        up = Vec.Unit(doubleQuat.CreateFromAxisAngle(Vec.Unit(axis), step) * held);
        return true;
```

```csharp
// src/KSAGolf/Sim/SmoothedStep.cs L34-L45
    public double Next(double stepSeconds)
    {
        // A paused or unusable step advances nothing and leaves the average alone, so what resumes
        // is what was running before rather than a decay towards zero.
        if (!double.IsFinite(stepSeconds) || stepSeconds <= 0.0) return 0.0;

        // Seeded rather than eased into from zero: a transition that started at a third of its
        // rate would crawl for its first few frames, which is the artefact this exists to remove.
        _seconds = _seconds > 0.0 ? _seconds + (stepSeconds - _seconds) * Weight : stepSeconds;

        return _seconds;
    }
```

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `Camera.Following` / `SetFollow(target, tidalLocking, changeControl, alert)` / `FollowWreckage` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L602) | Read and restore the follow half; the engine's own move to the wreckage (L595) |
| `Camera.GetFieldOfView` / `SetFieldOfView` / `ChangeFieldOfView` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L417-L462) | FOV handed back; the zoom keys' 15-120° clamp |
| `GameViewport.Mode` / `SetCameraMode` / `NextCameraMode` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L302-L330) | The mode half; no `Fixed` case in the camera-mode cycle |
| `FixedController.OnFrame` (cross with frame +Z) | [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L22-L48) | The singular axis `LeanOffAxis` keeps clear of |
| `Universe` vehicle destruction → `FollowWreckage` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1997-L2007) | Why a destroyed target's follow is left alone |

## Pitfalls and version notes

- **KSAGolf restores the mode unconditionally.** `TryHandBackMainView` checks the follow half before
  restoring it, but `BeginRestoreMainView` sets the saved mode whenever the current one differs. A
  player who switched mode by hand while golf held the view gets the old mode back. KSArmory's
  `StandDownMainView` restores the mode only if it is still `Fixed`; that is the rule `ViewClaim`
  encodes.[^golf-ksaworld][^armory-ksaworld] In practice golf re-asserts Fixed every frame it wants
  the view, so the case barely arises.
- **Stop asking, too.** `ViewAction.StandDown` documents that a borrower which stands down must also
  stop wanting the view, or the next frame is a plain `Take` and the view blinks back.
- **Sample, don't store.** `ChaseView.TryBlend` states that every point must be sampled this frame.
  The ecliptic is inertial, so a stored point falls about half a kilometre behind per frame at
  orbital speeds.
- **Trimmed copies.** These files are trimmed copies of KSArmory's. KSArmory's `ChaseView` also has
  stop-short, observer hand-off and burst framing. A fix in one repository does not reach the other.
- **Version notes.** Built against 2026.9.22.5482. `Camera.SetFollow`, `SetFieldOfView`,
  `ChangeFieldOfView`, `GameViewport.SetCameraMode` / `NextCameraMode` and the wreckage follow are
  unchanged in 2026.10.10.5554. `FixedController.OnFrame` gained a skip for character-portrait
  viewports, which does not affect the main view.[^ksa-fixedcontroller]

## See also

- [Level-horizon camera controller swapped into the main viewport](../features/level-horizon-camera-controller.md)
- [First-person kitten view that keeps the shadow](../features/first-person-kitten-view.md)
- [Putting session with a mouse-swung club](../features/putting-session.md)
- [Top-down minigolf course builder](../features/minigolf-course-builder.md)
- [Headless simulation tests behind a Sim/Ksa boundary](../features/headless-sim-tests.md)
- [Dual-hook frame stepping on simulated time](../features/dual-hook-frame-stepping.md)
- [Carry a rate-limited StableUp reference instead of switching up vectors near the vertical](../../../howto/camera/controllers/drive-the-main-camera-in-fixed-mode/drive-the-main-camera-in-fixed-mode-5.md) - how-to article demonstrating this feature
- [Match the orbit camera's drag feel with 0.003 rad per pixel, a squared-2 threshold, 1.1 per wheel notch and an ease back](../../../howto/camera/controllers/replace-a-viewport-camera-controller/replace-a-viewport-camera-controller-6.md) - how-to article demonstrating this feature
- [Hand back the field of view first and the follow only if it is still the borrower's, with explicit StandDown, Yield, Hold, GiveBack and Take decisions](../../../howto/camera/main-view/borrow-the-main-camera-and-hand-it-back/borrow-the-main-camera-and-hand-it-back-2.md) - how-to article demonstrating this feature
- [Hold a line to the target near the flight path with a carried lift and lean off the singular axis](../../../howto/camera/main-view/chase-a-fast-body-with-the-main-camera/chase-a-fast-body-with-the-main-camera-2.md) - how-to article demonstrating this feature
- [Smoothstep the eye, lerp the look at equal depth and pace the ease with an averaged step](../../../howto/camera/special-views/play-cinematic-camera-tracks/play-cinematic-camera-tracks-2.md) - how-to article demonstrating this feature

[^golf-viewclaim]: src/KSAGolf/Sim/ViewClaim.cs
[^golf-chaseview]: src/KSAGolf/Sim/ChaseView.cs
[^golf-chaseorbit]: src/KSAGolf/Sim/ChaseOrbit.cs
[^golf-stableup]: src/KSAGolf/Sim/StableUp.cs
[^golf-smoothedstep]: src/KSAGolf/Sim/SmoothedStep.cs
[^golf-ksaworld]: src/KSAGolf/Ksa/KsaWorld.cs
[^golf-levelhorizon]: src/KSAGolf/Ksa/LevelHorizonController.cs
[^golf-claude]: CLAUDE.md
[^golf-test-viewclaim]: tests/KSAGolf.Tests/ViewClaimTests.cs
[^golf-test-chaseorbit]: tests/KSAGolf.Tests/ChaseOrbitTests.cs
[^golf-test-smoothedstep]: tests/KSAGolf.Tests/SmoothedStepTests.cs
[^armory-chasecamera]: src/KSArmory/Ksa/ChaseCamera.cs
[^armory-sightcamera]: src/KSArmory/Ksa/SightCamera.cs
[^armory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-fixedcontroller]: KSA.FixedController (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
