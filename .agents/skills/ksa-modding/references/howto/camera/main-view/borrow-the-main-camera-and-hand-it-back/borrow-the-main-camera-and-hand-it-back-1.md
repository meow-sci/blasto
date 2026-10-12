---
type: How-To
title: Record mode, follow target and field of view before the first write, stand down when the player changes either half and restore the halves separately
description: KSArmory's KsaWorld helpers record the main view before borrowing it, hand it back in two halves, step from the GUI hook, a hidden-HUD prefix and an after-frame fallback, and forget a recording across a save load
category: camera
group: main-view
topic: borrow-the-main-camera-and-hand-it-back
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksarmory/features/borrow-main-view-camera]
techniques: [state-record-and-restore, harmony-prefix, harmony-postfix, starmap-lifecycle]
hooks:
  - starmap StarMapAfterGui
  - starmap StarMapAfterOnFrame
  - harmony-prefix KSA.Program.OnFrameCelestials
  - harmony-postfix KSA.Program.OnGameLoaded
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame, frame:viewports, event:save]
game_types:
  - KSA.Program
  - KSA.Camera
  - KSA.CameraMode
  - KSA.GameViewport
  - KSA.IFollowable
  - KSA.Vehicle
  - KSA.FixedController
tags: [camera, borrow, restore, stand-down, save-load, hud-hidden]
---
The main view belongs to the player. A mod that borrows it (a chase, an optic, a scripted shot) must be able to give it
back exactly, and must notice when the player took it first. KSArmory's `KsaWorld` is the reference implementation:
one record taken before the first write, two independent halves (the camera mode and the follow target) plus the field of
view, and three kinds of exit. Prerequisites: StarMap and Harmony. Several borrowers sharing the view are in the later
**Arbitrate between features and mods** articles ([5](borrow-the-main-camera-and-hand-it-back-5.md)).

## Background: how the game does it

What a borrower changes is the **follow target** (`Camera.SetFollow`), the **camera mode** (`GameViewport.SetCameraMode`) and the
**field of view** (`SetFieldOfView`). The player can take either half back without the other: the View menu changes the mode
and keeps the follow, and a vessel switch changes the follow and keeps the mode ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3690-L3702)). `Fixed`
cannot be left from the keyboard ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L314-L330)), and the zoom keys clamp the field to 15-120 degrees
([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L455-L462)), so a mod must give both back itself. `SetFollow` teleports the camera to 2.5 mean radii from the
target and, with `changeControl: true`, replaces `Program.ControlledVehicle` by `target as Vehicle` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L602-L618)).
When a followed craft is destroyed the engine points the cameras at a successor or a `WreckageMarker`, and a save load
re-follows from the save ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L832-L849)).

## Lifecycle and timing

| Work | Where | Value |
|---|---|---|
| Take, drive, stand down, release | `StepOnce`, whichever hook reaches it first: `[StarMapAfterGui]` (preferred), a prefix on `Program.OnFrameCelestials` while the HUD is hidden, `[StarMapAfterOnFrame]` as the fallback | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` |
| Save load | Harmony postfix on `Program.OnGameLoaded` latches a flag; the next `StepOnce` consumes it | `event:save` |
| The write takes effect | The next viewport pass | `frame:viewports` |

Hiding the HUD (F2) stops the GUI hooks, so a mod that only restates the view from `StarMapAfterGui` leaves the player riding a
frozen offset while the world carries on ([Keep mod UI and per-frame work running when the HUD is hidden (F2)](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md), [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)). `SetCameraMode` is
synchronous but the pose moves only at the next viewport pass.

## Steps

1. **Record before writing.** A recording taken after `SetCameraMode(Fixed)` or `SetFollow` describes the borrowed state:

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

2. **Read the player's pose before attaching anything**, because `SetFollow` moves the camera; then put it back so the frame
   that follows does not render beside the target:

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

3. **Hand the view back in two halves, field of view and mode first, follow only if it is still the mod's.** The follow is read
   *before* anything is written, because the restore changes the answer:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L5206-L5215
public static bool TryHandBackMainView(MainView saved, IFollowable? followed,
                                       out bool mode, out bool follow)
{
    bool followIsOurs = MainViewFollows(followed);

    mode = BeginRestoreMainView(saved);
    follow = !followIsOurs || !CanFollow(saved.Following) || RestoreFollow(saved);

    return mode && follow;
}
```

   `CanFollow` refuses a disposed vehicle (restoring over the engine's own wreckage follow would hold the view on a dead craft).

4. **Stand down when the player took a half.** Give back the field of view and whichever half the player did *not* take, and stop asking:

```csharp
// src/KSArmory/Ksa/ChaseCamera.cs L315-L332
// The player taking the view back is a decision, not a fault.
//
// Ahead of the hold below, and keyed on holding the view rather than on having a round: the
// linger after a burst is ChaseView.LingerSeconds during which the view is still this
// camera's -- and for a nuclear one that is most of a minute rather than three seconds. A
// vessel switched in that window has to be noticed here -- Release reaching it first puts
// the player back on the craft they have just left. The half they did not take still goes
// back: a vessel switch leaves the view in Fixed, which no input can leave.
if (_saved.Valid && !StillOurs())
{
    bool modeIsOurs = KsaWorld.StandDownMainView(_saved, _followed);
    PassOverEverythingFlying(system);
    EndEngagement();
    LetGo();
    Log.Info($"chase: the view was taken over by hand ({(modeIsOurs ? "vessel" : "camera mode")}), "
             + "standing down");
    return;
}
```

5. **Retry refused restores, bounded.** A refused hand-back keeps the recording and tries again each call for 180 frames
   ([src/KSArmory/Ksa/ChaseCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ChaseCamera.cs#L207-L236)).

6. **Keep stepping with the HUD hidden and forget across a load.** `StepOnce` is claimed once per frame from whichever hook comes first, and consumes
   the load flag before stepping ([src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L324-L355)). `DriveCameras` sits outside the flight gate so a view is
   handed back on the way out of flight ([src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L433-L485)).

## Complete example

- [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5135-L5266): `RememberMainView`, `RestoreFollow`, `TryHandBackMainView`, `StandDownMainView`, `TryFollowOnMainViewport`
- [src/KSArmory/Ksa/ChaseCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ChaseCamera.cs) and [src/KSArmory/Ksa/SightCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/SightCamera.cs): the two borrowers
- [src/KSArmory/Ksa/WorldReloadHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WorldReloadHook.cs) and [src/KSArmory/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/PreRenderHook.cs): the two Harmony hooks

## Variations and gotchas

- **Stop asking after a stand-down**, or the next frame is a plain take and the view blinks back to the mod.
- **Watch both halves.** A borrower that checks only the mode keeps writing an offset measured from one craft onto another after a vessel switch.
- **Forget, not restore, after a save load.** `Universe.DeserializeSave` unfollows every viewport camera and `Camera.DeserializeSave` sets the save's own follow and mode, so a recording describes a scene that no longer exists. The field of view is still returned. KSArmory notes that its chase calls `Release` where a comment says "forget" (observed in code, not measured).
- **Every take and hand-back clears held input** and entering `Fixed` raises the entry alert.

## See also

- [Topic overview](overview.md); [approach 2](borrow-the-main-camera-and-hand-it-back-2.md), [3](borrow-the-main-camera-and-hand-it-back-3.md), [4](borrow-the-main-camera-and-hand-it-back-4.md), [5](borrow-the-main-camera-and-hand-it-back-5.md)
- [Borrow the player's main view and hand it back](../../../../mods/ksarmory/features/borrow-main-view-camera.md), [Chase camera riding a mod-simulated round](../../../../mods/ksarmory/features/chase-camera-on-a-round.md)
- [Make the camera follow a point or mod-simulated object, and change the follow target without a jump](../follow-a-point-that-is-not-a-vehicle/overview.md), [Drive the main camera through Fixed mode without crashing it, with a stable up vector](../../controllers/drive-the-main-camera-in-fixed-mode/overview.md), [Taking control of another vehicle](../../../vehicles-and-parts/vehicles/take-control-of-another-vehicle/overview.md), [Replace a viewport's camera controller and take input through it](../../controllers/replace-a-viewport-camera-controller/overview.md)
