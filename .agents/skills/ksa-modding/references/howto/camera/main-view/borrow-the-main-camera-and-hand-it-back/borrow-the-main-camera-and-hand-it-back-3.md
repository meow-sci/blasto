---
type: How-To
title: Record the main view before the first write when flying another craft or a first-person kitten
description: KSAStructures records mode, follow target and field of view with RememberMainView before borrowing the view for the office computer and the first-person kitten and hands them back with TryHandBackMainView from Leave
category: camera
group: main-view
topic: borrow-the-main-camera-and-hand-it-back
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksastructures/features/computer-flies-nearest-craft
  - mods/ksastructures/features/first-person-kitten-view
techniques: [state-record-and-restore]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:viewports]
game_types: [KSA.Program, KSA.Camera, KSA.CameraMode, KSA.GameViewport, KSA.KittenEva, KSA.Vehicle]
tags: [camera, borrow, restore, first-person, controlled-vehicle]
---
KSAStructures borrows the main view in two features: the office computer, which flies the nearest craft while the kitten sits at the monitor, and the first-person kitten view. Both
use the record and hand-back helpers in `KsaWorld`; their value here is how short the borrower side can be when the helpers do the work, and
when exactly each borrower records, re-asserts and leaves. The helpers are the same as in
[approach 1](borrow-the-main-camera-and-hand-it-back-1.md) and [2](borrow-the-main-camera-and-hand-it-back-2.md).

## Background: how the game does it

Setting `Program.ControlledVehicle` and the camera's follow are independent: the property does not change what any camera follows, which is what lets the player fly one craft
while watching another. `SetFollow(..., changeControl: true)` is the call that couples them ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L602-L618)). `GameViewport.NextCameraMode` returns false from
`Fixed`, so the first-person view re-asserts `Fixed` every step it holds the view ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L314-L330)).

## Lifecycle and timing

Both borrowers run from the mod's step in the `[StarMapAfterGui]` hook (`frame:draw-ui`) with the hidden-UI fallbacks, outside the flight gate so the view is handed back on the way out. The
eye pose is asked again inside the controller in `frame:viewports` ([Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md)).

## Steps

1. **First-person kitten: record on the first borrowed frame, before the first write.** `_borrowed` guards it so the record is taken once:

```csharp
// src/KSAStructures/Ksa/FirstPerson.cs L40-L80
public void Drive()
{
    try
    {
        KittenEva? flown = config.FirstPerson && KsaWorld.InFlightScene && KsaWorld.ControlledVehicle is KittenEva k
                           && KsaWorld.IsAlive(k) ? k : null;

        if (!ReferenceEquals(flown, _kitten)) Leave();
        if (flown is null) return;

        if (_pose is null)
        {
            if (KittenFrame.AvatarOf(flown) is not { } avatar) return;

            _kitten = flown;
            _pose = new EyesPose(flown, avatar);
            avatar.Core.CharacterModel.AnimProcessors.Add(_pose);
        }

        if (!_borrowed)
        {
            _saved = KsaWorld.RememberMainView();
            _borrowed = true;
            if (TryLocalFrame(flown, out _, out double3 n, out double3 e))
            {
                double3 facing = KittenFrame.DirectionToEcl(flown, new double3(0, 0, 1));
                _look.LookTowards(Math.Atan2(Vec.Dot(facing, e), Vec.Dot(facing, n)), 0.0);
            }
        }

        FirstPersonHook.Body = _pose;
        FirstPersonHook.Heading = Heading(flown);

        if (TryPose(KsaWorld.PositionEcl(flown), out double3 offset, out double3 forward, out double3 up, out double fov))
            KsaWorld.TryLookFromMainViewport(offset, forward, up, fov, this);
    }
    catch (Exception e)
    {
        Log.Warn($"first person: {e.Message}");
    }
}
```

2. **Drive each step.** `TryLookFromMainViewport(..., this)` re-asserts `Fixed` and the pose source ([src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L695-L748)).

3. **Leave hands everything back**: the pose processor is removed from the kitten's avatar and the view is returned; every failure is swallowed because the kitten may be gone:

```csharp
// src/KSAStructures/Ksa/FirstPerson.cs L82-L101
public void Leave()
{
    FirstPersonHook.Body = null;
    FirstPersonHook.Heading = null;

    try
    {
        if (_pose is not null && _kitten is not null && KittenFrame.AvatarOf(_kitten) is { } avatar)
            avatar.Core.CharacterModel.AnimProcessors.Remove(_pose);
        if (_borrowed) KsaWorld.TryHandBackMainView(_saved, _kitten, out _, out _);
    }
    catch
    {
        // The kitten is gone; there is nothing to give back to.
    }

    _pose = null;
    _kitten = null;
    _borrowed = false;
}
```

4. **Office computer: record, switch control, restore.** The computer records the view, sets `Program.ControlledVehicle` to the played craft (so keys reach it), and on log-off puts control back to the chair before handing the view back:

```csharp
// src/KSAStructures/Ksa/OfficeComputer.cs L82-L106
private void Start(Vehicle chair)
{
    Vehicle? nearest = null;
    double best = double.MaxValue;
    double3 here = KsaWorld.PositionEcl(chair);
    foreach (Vehicle v in KsaWorld.Vehicles)
    {
        if (v is KittenEva || ReferenceEquals(v, chair) || !KsaWorld.IsAlive(v) || v.Id is Office.ChairId or Office.MonitorId) continue;

        double d = Vec.Len(KsaWorld.PositionEcl(v) - here);
        if (d < best) (best, nearest) = (d, v);
    }

    if (nearest is null) { Log.Info("office computer: there is no craft to play"); return; }
    if (!KsaWorld.TryOpenCameraWindow(out _window)) { Log.Warn("office computer: no camera window to show the game in"); return; }

    KsaWorld.TryHideViewportWindow(_window);
    KsaWorld.ResizeViewport(_window, new int2(768, 416));
    _shown = -1;
    _saved = KsaWorld.RememberMainView();
    _chair = chair;
    _played = nearest;
    Program.ControlledVehicle = nearest;
    Log.Info($"office computer: playing {KsaWorld.DisplayName(nearest)}, {best:F0} m away");
}
```

```csharp
// src/KSAStructures/Ksa/OfficeComputer.cs L108-L131
public void Stop()
{
    if (!Playing) return;

    try
    {
        ShowOnTheMesh(live: false);
        if (_window >= 0) KsaWorld.CloseCameraWindow(_window);
        if (KsaWorld.IsAlive(_chair))
        {
            Program.ControlledVehicle = _chair;
            KsaWorld.TryHandBackMainView(_saved, _chair, out _, out _);
        }
    }
    catch (Exception e)
    {
        Log.Warn($"office computer: could not log off cleanly ({e.Message})");
    }

    _window = -1;
    _chair = null;
    _played = null;
    Log.Info("office computer: logged off");
}
```

## Complete example

- [src/KSAStructures/Ksa/FirstPerson.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPerson.cs), [src/KSAStructures/Ksa/OfficeComputer.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeComputer.cs)
- [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L486-L499), [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L557-L566)

## Variations and gotchas

- **`ControlledVehicle` and the camera are independent**, so anything else that assumes "the camera follows the controlled vehicle" (UI panels, gauges) shows the wrong thing while a borrower is active.
- **Kitten view needs its follow to stay on the kitten**; `Leave` hands the view back only if the followed object is still the borrower's ([Put the camera in an EVA kitten's eyes, with mouse look and look-driven walking](../../special-views/first-person-camera-for-a-kitten/overview.md)).
- **No stand-down ladder.** KSAStructures does not implement the `ViewClaim` rules: a player who changes the mode while first person is on has `Fixed` re-asserted next step.

## See also

- [Topic overview](overview.md); [approach 1](borrow-the-main-camera-and-hand-it-back-1.md), [2](borrow-the-main-camera-and-hand-it-back-2.md), [4](borrow-the-main-camera-and-hand-it-back-4.md)
- [Fly the nearest craft from the office computer](../../../../mods/ksastructures/features/computer-flies-nearest-craft.md), [First-person view for a kitten on foot](../../../../mods/ksastructures/features/first-person-kitten-view.md)
- [Taking control of another vehicle](../../../vehicles-and-parts/vehicles/take-control-of-another-vehicle/overview.md), [Put the camera in an EVA kitten's eyes, with mouse look and look-driven walking](../../special-views/first-person-camera-for-a-kitten/overview.md)
