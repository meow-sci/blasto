---
type: Mod Feature
title: Borrow the player's main view and hand it back
description: Mod camera owners borrow KSA's main view by recording its follow target, mode and field first, ranking claims with a pure ViewClaim ladder, standing down when the player changes either half, and restoring the halves separately
mod: ksarmory
category: camera
techniques: [view-claim-ladder, state-record-and-restore, orbit-angle-nudge, reflection]
hooks:
  - starmap StarMapAfterGui (via StepOnce)
  - harmony-prefix KSA.Program.OnFrameCelestials (via StepOnce, HUD hidden)
  - starmap StarMapAfterOnFrame (via StepOnce, fallback)
  - harmony-postfix KSA.Program.OnGameLoaded (save load, consumed in StepOnce)
  - starmap StarMapUnload
lifecycle: [frame:draw-ui, frame:celestials, frame:viewports, event:save-load]
game_types:
  - KSA.Program
  - KSA.Camera
  - KSA.GameViewport
  - KSA.IGameViewport
  - KSA.CameraMode
  - KSA.FixedController
  - KSA.OrbitController
  - KSA.OrbitView
  - KSA.IFollowable
  - KSA.Vehicle
  - KSA.Universe
  - KSA.WreckageMarker
tags: [main-view, camera-mode, follow, hand-back, orbit-camera, claim-ladder, save-load, fixed-camera]
---

The main view is the player's, and several things in KSArmory borrow it: the chase camera rides a
round, the sight camera looks through an optical director, and the developer file bridge holds it
at a pose for captures, with `CloudWatch` computing a pose pinned on a standing cloud for scripted
runs. All of them write through the same `KsaWorld` helpers.[^ksarmory-chasecamera][^ksarmory-sightcamera][^ksarmory-bridge][^ksarmory-cloudwatch]
A fifth, `WatchCamera`, never takes it at all: it swings KSA's own orbit camera round onto a craft by
writing the angles the orbit controller already reads, then lets go.[^ksarmory-watchcamera]

The borrowers share one protocol in `KsaWorld` and one engine-free rule set, `ViewClaim`, that decides
who may hold the view and what a borrower does when it is outranked or when the player takes the
view back. The point of the design is that the player is never stranded: KSA's `Fixed` camera mode
reads no input and has no key that leaves it, so a borrower that forgets to restore the mode leaves
the player stuck.[^ksarmory-viewclaim][^ksa-fixedcontroller][^ksa-gameviewport]

## How it works

### Two halves and a field of view

What a borrower changes is the **follow target** (`Camera.SetFollow`) and the **camera mode**
(`GameViewport.SetCameraMode`), plus the **field of view**. The player can take either half back
without the other: the View menu changes the mode and keeps the follow, a vessel switch (or any "go
to this craft") changes the follow and keeps the mode.[^ksarmory-viewclaim][^ksa-program]

| Step | Helper (`KsaWorld`) | Detail |
|---|---|---|
| Record | `RememberMainView()` returns `MainView(Following, Mode, FovDeg, Valid)` | Taken **before** the first write: setting `Fixed` and following something else both change what a later reading would see[^ksarmory-ksaworld] |
| Point | `TryFollowOnMainViewport(target)` | `SetFollow(target, tidalLocking: false, changeControl: false, alert: false)`; with `changeControl: true` the engine sets `Program.ControlledVehicle = target as Vehicle` (Camera.cs L602-L618)[^ksa-camera] |
| Drive | `TryLookFromMainViewport(offset, forward, up, fov, pose)` | Writes the fixed controller's `CameraRotation` (first), `CameraOffset`, up and pose source, the field of view, then `SetCameraMode(Fixed)`. The controller is the mod's subclass described in [the chase camera](./chase-camera-on-a-round.md)[^ksarmory-ksaworld][^ksarmory-levelhorizon] |
| Still ours? | `MainViewIsFixed()`, `MainViewFollows(target)` | Both halves checked, by identity of the followed object[^ksarmory-ksaworld] |
| Give back | `TryHandBackMainView(saved, followed, out mode, out follow)` | Reads whether the follow is still the borrower's *before* writing; restores field of view, then mode; restores the follow only if it is still ours and the saved target can still be followed[^ksarmory-ksaworld] |
| Stand down | `StandDownMainView(saved, followed)` | The player took one half: always give the field of view back, restore whichever half the player did *not* take[^ksarmory-ksaworld] |

`CanFollow` refuses a disposed `Vehicle`. That is the ordinary end of an engagement: when a followed
craft is destroyed, KSA itself re-points every camera following it at a successor craft or at a
`WreckageMarker` (Universe.cs L1997-L2006), and restoring over that would hold the view on a dead
vehicle.[^ksarmory-ksaworld][^ksa-universe]

### The claim ladder (`ViewClaim`)

| `ViewAction` | Meaning |
|---|---|
| `Idle` | Not holding, not wanting. Touch nothing |
| `Take` | Record, then drive |
| `Hold` | Holding and still entitled. Drive again |
| `GiveBack` | Holding and no longer entitled. Restore |
| `Yield` | A stronger claim is driving. Write nothing, **keep the recording** |
| `StandDown` | The player took the view back by hand. Forget the recording; the caller must also stop asking |

`ViewClaim.ForOptic` answers in a fixed order: stand down first (restoring over a view the player
reclaimed drags them off what they turned to), then yield (so a borrower that is no longer wanted
still waits for the stronger claim), then hold or give back, then take only if nothing outranks it.
`StillOurs` requires the taken mode **and** the original follow target, except that while outranked
neither half is evidence, because the stronger claim sets both itself.[^ksarmory-viewclaim]

`KSArmoryMod.DriveCameras` runs the owners in claim order every step: the watch camera, the bridge,
the chase, the camera windows, and last the sight, which is told whether the chase holds the view.
The chase is at the top of the ladder and is never outranked; it takes the view only when the view
already follows the firing craft, so a site elsewhere cannot hijack it.[^ksarmory-mod][^ksarmory-chasecamera]
When the chase takes the view from the sight, the sight yields with its recording intact, the chase
records the sight's pose (Fixed, following the launcher), and handing the chase back returns the
view to the sight, which resumes on the next frame. Swapping the two in `DriveCameras` would fly a
chase transition through the sight's magnified field.[^ksarmory-mod][^ksarmory-sightcamera]

`ViewClaim.SightPaints` ties the overlay to the same permission: on the main view the sight paints
only while it holds the view and is not outranked; a camera window is never contested.[^ksarmory-viewclaim]

### Release, stand down, forget

| Path | When | What is restored |
|---|---|---|
| `Release` | No longer wanted (optic switched off, the panel showing a craft with no director, the head unresolvable, the round gone), unload | Mode and field of view; the follow if it is still ours and restorable. A refused restore keeps the recording and retries; both borrowers give up after 180 refused frames[^ksarmory-sightcamera][^ksarmory-chasecamera] |
| `StandDown` | The player changed the mode or the follow | The other half and the field of view. `SightCamera` returns `ViewAction.StandDown` and `TakeMainView` sets the optic's `Viewport = -1`, or the next frame would be a fresh `Take`[^ksarmory-sightcamera][^ksarmory-mod] |
| `Forget` (`SightCamera`) | Out of flight, and after a save load (`ForgetTheWorld`) | Only the field of view; the controller is told to stop driving. Mode and follow belong to a scene that no longer exists[^ksarmory-sightcamera][^ksarmory-mod] |

Why forget on a save load: `Universe.DeserializeSave` unfollows every viewport camera (Universe.cs
L2448-L2454) and `Camera.DeserializeSave` then sets the save's own follow and camera mode (Camera.cs
L832-L849). The field of view is restored anyway, because it is a camera preference that outlives
scenes and a player's zoom keys cannot reach anything under 15 degrees (`Camera.ChangeFieldOfView`
clamps to 15-120).[^ksa-universe][^ksa-camera][^ksarmory-sightcamera] The load itself is seen
through a Harmony postfix on `Program.OnGameLoaded` that latches a flag the next `StepOnce` consumes.[^ksarmory-worldreloadhook]

On `[StarMapUnload]` the mod releases the watch, the chase and the sight, then puts KSA's own fixed
controller back (`KsaWorld.RestoreStockController`).[^ksarmory-mod]

### Watching without taking (`WatchCamera`)

Right-clicking a craft's name in the switcher calls `_watch.Watch(craft)`.[^ksarmory-ui] The orbit
angles live in two places: `Camera.Following.OrbitView.Azimuth/Elevation` is the stored pair a mouse
drag writes, and `OrbitController.Azimuth/Elevation` is an output sprung towards it every frame
(`MathEx.SpringInterpDriven`, OrbitController.cs L609-L610).[^ksa-orbitview][^ksa-orbitcontroller][^ksarmory-ksaworld]
Each step `WatchCamera.Apply`:

1. reads both pairs plus the camera basis (`KsaWorld.TryReadMainOrbit`); no orbit camera (map or
   fixed) ends the watch;
2. stands down if the stored pair differs from what it last wrote by more than 1e-6 rad, which is
   the player dragging (the orbit controller writes the stored pair on a drag, OrbitController.cs
   L384-L385);
3. solves the target angles against the *shown* pair, which built the basis being measured
   (`OrbitAim.TrySolve`), and eases the *stored* pair towards them at 4 per second
   (`OrbitAim.Ease`), clamping elevation to plus or minus pi/2 as the engine does;
4. lets go on arrival (0.004 rad on the shown pair), after 4 s, or when the craft is gone.[^ksarmory-watchcamera][^ksarmory-orbitaim]

No mode or follow changes, so there is nothing to hand back and the player keeps full control.

## Lifecycle and timing

| Value | Where (build 5554) | What runs |
|---|---|---|
| `frame:draw-ui` | `[StarMapAfterGui]` postfix on `Program.OnDrawUiViewports` (Program.cs L2268) runs `StepOnce`, whose last act is `DriveCameras` | Claim decisions, recordings, hand-backs, writes to the orbit angles and the fixed controller's fields[^ksarmory-mod] |
| `frame:celestials` | HUD hidden: Harmony prefix on `Program.OnFrameCelestials` (L2320) runs `StepOnce` | The same, so a hidden HUD still hands a view back |
| `frame:viewports` (next frame) | `OnFrameViewports` (L2263): the orbit controller springs towards the written angles; the fixed controller subclass asks its pose source (`SightCamera.TryPose`, `ChaseCamera.TryPose`) | The written state takes effect[^ksa-orbitcontroller][^ksarmory-levelhorizon] |
| `event:save-load` | `Program.OnGameLoaded` postfix latches; `StepOnce` consumes it before stepping and calls `ForgetTheWorld` | Sight forgotten, chase released[^ksarmory-worldreloadhook][^ksarmory-mod] |

`DriveCameras` sits outside `StepOnce`'s flight gate on purpose, so a view is still handed back on
the way out of flight; the scene, not the controlled craft, decides between release and forget,
because destroying the craft being flown clears `Program.ControlledVehicle` and leaves the scene
running.[^ksarmory-mod]

Everything here is written after this frame's controllers ran, so it takes effect in the next
frame's viewport pass. `SetCameraMode` itself is synchronous (`OnSwitchOff`, assign, `OnSwitchOn`,
`ClearHeldPlayerInput`, GameViewport.cs L302-L312); the pose moves only when the controller next
runs.[^ksa-gameviewport] Borrowers that need the current frame supply themselves as the pose source,
which the controller asks inside that pass (see [the chase camera](./chase-camera-on-a-round.md)).

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Sim/ViewClaim.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ViewClaim.cs#L4-L137) | `ViewAction`, `ForOptic`, `StillOurs`, `SightPaints` (engine-free, tested headlessly) |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5011-L5267) | Orbit read/write, `MainViewIsFixed`, `MainViewFollows`, `MainView`, `RememberMainView`, `TryHandBackMainView`, `StandDownMainView`, `TryFollowOnMainViewport` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5522-L5664) | `TryLookFromMainViewport`, `StopDrivingMainView`, `BeginRestoreMainView`, `RestoreMainViewMode` |
| [src/KSArmory/Ksa/SightCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/SightCamera.cs#L226-L415) | `Forget`, `Release`, `Apply` (the ladder applied), `StandDown`, `Take`, `Follow` |
| [src/KSArmory/Ksa/ChaseCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ChaseCamera.cs#L208-L333) | Chase release and stand-down |
| [src/KSArmory/Ksa/WatchCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WatchCamera.cs) | Orbit-angle nudge |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L433-L485) | `DriveCameras`, claim order and release-or-forget |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L1045-L1058) | `TakeMainView`: stand-down switches the optic off |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L362-L400) | `ForgetTheWorld` after a save load |

## Snippets

```csharp
// src/KSArmory/Sim/ViewClaim.cs L77-L137
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
    // ...
    public static bool SightPaints(bool opticSelected, bool onMainView, bool holding, bool outranked)
    {
        if (!opticSelected) return false;
        if (!onMainView) return true;

        return holding && !outranked;
    }
```

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L5206-L5243
    public static bool TryHandBackMainView(MainView saved, IFollowable? followed,
                                           out bool mode, out bool follow)
    {
        bool followIsOurs = MainViewFollows(followed);

        mode = BeginRestoreMainView(saved);
        follow = !followIsOurs || !CanFollow(saved.Following) || RestoreFollow(saved);

        return mode && follow;
    }
    // ...
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

```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L433-L484
    private void DriveCameras(double dt)
    {
        if (_roster is null || _ui is null) return;

        _watch.Apply(dt);
        _bridge?.DriveCamera();
        // ...
        if (_chase.RiddenSystem is { Platform: null } riding)
        {
            _chase.Apply(riding, enabled: true, dt, _lastSimStep, _config.FreezeChaseTransition,
                         _sight.BaseFovDeg);
        }
        else if (_roster.For(_ui.Focused) is { } selected && _roster.LastReleasing(selected) is var chased)
        {
            _chase.Apply(chased.Weapon, chased.Policy.ChaseRounds && KsaWorld.InFlightScene,
                         dt, _lastSimStep, _config.FreezeChaseTransition, _sight.BaseFovDeg);
        }
        else
        {
            _chase.Release();
        }

        DriveCameraWindows(dt);

        if (KsaWorld.InFlight && _heads?.Driving(_ui.Focused) is { } head)
        {
            TakeMainView(head.Head, head.Policy);
        }
        else if (KsaWorld.InFlightScene)
        {
            // ...
            _sight.Release();
        }
        else
        {
            // ...
            _sight.Forget();
        }
```

```csharp
// src/KSArmory/Ksa/WatchCamera.cs L98-L127
        // The player moved the view. It is theirs; stand down rather than dragging it back.
        if (_hasWritten
            && !OrbitAim.SameAim(view.StoredAzimuth, view.StoredElevation,
                                 _wroteAzimuth, _wroteElevation, HandoverRad))
        {
            Release();
            return;
        }

        // Solved against the shown angles, because those are what built the basis being measured.
        if (!OrbitAim.TrySolve(view.Forward, view.Right, view.ShownAzimuth, view.ShownElevation,
                               Vec.Unit(toTarget), out double toAzimuth, out double toElevation))
        {
            Release();
            return;
        }

        double azimuth = view.StoredAzimuth;
        double elevation = view.StoredElevation;
        OrbitAim.Ease(ref azimuth, ref elevation, toAzimuth, toElevation, Rate, dtPlayer);

        if (!KsaWorld.TryWriteMainOrbit(azimuth, elevation))
        {
            Release();
            return;
        }

        _wroteAzimuth = azimuth;
        _wroteElevation = Math.Clamp(elevation, -Math.PI / 2.0, Math.PI / 2.0);
        _hasWritten = true;
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Camera.SetFollow`, `Following`, `ChangeFieldOfView`, `SetFieldOfView`, `DeserializeSave` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L602-L618) | Follow and field of view; what a save load restores (L832-L849) |
| `GameViewport.Mode`, `SetCameraMode`, `NextCameraMode`, `FixedController`, `OrbitController` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L284-L330) | Mode half; `NextCameraMode` has no `Fixed` case |
| `FixedController.OnKey`, `OnSwitchOn` | [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L120-L143) | Reads no keys; raises the "fixed camera" alert on every switch in |
| `OrbitController.Azimuth`, `Elevation` (shown, sprung) | [current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L605-L611) | Read by the watch camera |
| `OrbitView.Azimuth`, `Elevation` (stored) | [current/decomp/KSA/OrbitView.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitView.cs#L7-L9) | Written by the watch camera |
| `Universe.DeserializeSave`, destroyed-vehicle camera hand-off | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2478) | Unfollow on load; re-follow on destruction (L1997-L2006) |
| `Program.MainViewport`, `GetMainCamera`, View menu camera items | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3692-L3695) | The player's way out of `Fixed` is `View > Orbit Camera` |

## Pitfalls and version notes

- **Fixed is a trap for the player.** `FixedController.OnKey` returns false and
  `GameViewport.NextCameraMode` has no `Fixed` case, so only an explicit `SetCameraMode` leaves it; the
  panel tells players that `View > Orbit Camera` takes the view back.[^ksa-fixedcontroller][^ksa-gameviewport][^ksarmory-uioptic][^ksa-program]
- **Record before writing.** A recording taken after `SetCameraMode(Fixed)` or `SetFollow` describes
  the borrowed state, and restoring from it leaves the player at a fixed point in space.[^ksarmory-ksaworld]
- **Watch both halves.** A borrower that checks only the mode keeps writing an offset measured from
  one craft onto another after a vessel switch.[^ksarmory-viewclaim]
- **Stop asking after a stand-down.** If the setting still requests the view, the next frame takes it
  straight back and the mod appears to refuse to let go.[^ksarmory-viewclaim][^ksarmory-mod]
- **Every take and hand-back clears held input.** `GameViewport.SetCameraMode` calls
  `Program.ControlledVehicle?.ClearHeldPlayerInput()`, and switching into `Fixed` raises KSA's "fixed
  camera" alert.[^ksa-gameviewport][^ksa-fixedcontroller]
- **The field of view is the borrower's to give back.** Nothing in either half restores it, and the
  zoom keys clamp at 15 degrees, so a sight let go at 1 degree would otherwise stay there.[^ksa-camera][^ksarmory-sightcamera]
- **The chase has no `Forget` (observed in code).** `ForgetTheWorld`'s comment says "Forget, not
  release" but it calls `_chase.Release()`, which restores the pre-chase camera mode over whatever the
  save just set. Not measured.[^ksarmory-mod][^ksarmory-chasecamera]
- **Unload order.** Cameras release before `RestoreStockController`, so nothing is mid-write when the
  controller is swapped back.[^ksarmory-mod]

## See also

- [Chase camera riding a round](./chase-camera-on-a-round.md)
- [Gunner's sight overlay](./gunner-sight-overlay.md)
- [Director camera windows](./director-camera-window.md)
- [Optical director heads](./optical-director-heads.md)
- [File command bridge and MCP](./file-command-bridge-and-mcp.md)
- [Detect a save load](./detect-save-load-ongameloaded.md)
- [Dual-hook simulation step](./dual-hook-simulation-step.md)
- [Fly another vehicle](./fly-another-vehicle.md)
- [Set CameraRotation before the mode and restore the previous mode explicitly because Fixed has no player exit](../../../howto/camera/controllers/drive-the-main-camera-in-fixed-mode/drive-the-main-camera-in-fixed-mode-2.md) - how-to article demonstrating this feature
- [Record mode, follow target and field of view before the first write, stand down when the player changes either half and restore the halves separately](../../../howto/camera/main-view/borrow-the-main-camera-and-hand-it-back/borrow-the-main-camera-and-hand-it-back-1.md) - how-to article demonstrating this feature
- [Rank several camera users with a pure ViewClaim ladder and drive the claimants in claim order](../../../howto/camera/main-view/borrow-the-main-camera-and-hand-it-back/borrow-the-main-camera-and-hand-it-back-5.md) - how-to article demonstrating this feature
- [Follow on the main viewport without taking control, with follow and mode kept as separate halves](../../../howto/camera/main-view/follow-a-point-that-is-not-a-vehicle/follow-a-point-that-is-not-a-vehicle-4.md) - how-to article demonstrating this feature
- [Call SetFieldOfView in degrees, read GetFieldOfView in radians and zoom past the player clamp](../../../howto/camera/main-view/override-the-camera-field-of-view/override-the-camera-field-of-view-2.md) - how-to article demonstrating this feature

[^ksarmory-viewclaim]: src/KSArmory/Sim/ViewClaim.cs
[^ksarmory-sightcamera]: src/KSArmory/Ksa/SightCamera.cs
[^ksarmory-chasecamera]: src/KSArmory/Ksa/ChaseCamera.cs
[^ksarmory-watchcamera]: src/KSArmory/Ksa/WatchCamera.cs
[^ksarmory-orbitaim]: src/KSArmory/Sim/OrbitAim.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-levelhorizon]: src/KSArmory/Ksa/LevelHorizonController.cs
[^ksarmory-bridge]: src/KSArmory/Ksa/Bridge.cs
[^ksarmory-cloudwatch]: src/KSArmory/Ksa/CloudWatch.cs
[^ksarmory-worldreloadhook]: src/KSArmory/Ksa/WorldReloadHook.cs
[^ksarmory-ui]: src/KSArmory/Ksa/Ui/Ui.cs
[^ksarmory-uioptic]: src/KSArmory/Ksa/Ui/UiOptic.cs
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-fixedcontroller]: KSA.FixedController (decompiled, build 2026.10.10.5554)
[^ksa-orbitcontroller]: KSA.OrbitController (decompiled, build 2026.10.10.5554)
[^ksa-orbitview]: KSA.OrbitView (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
