---
type: How-To
title: Treat the field of view as one half of a borrowed view and always return it first
description: KSAGolf records the main view's field of view with the mode and follow, clamps its own writes to 1 to 120 degrees and restores the field before the mode because SetFieldOfView does not clamp and the player's zoom keys cannot widen a narrow value
category: camera
group: main-view
topic: override-the-camera-field-of-view
approach: 3
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/level-horizon-camera-controller]
techniques: [state-record-and-restore]
hooks: [starmap StarMapAfterGui, subclass KSA.FixedController.OnFrame]
lifecycle: [frame:draw-ui, frame:viewports]
game_types: [KSA.Camera, KSA.Program, KSA.GameViewport, KSA.FixedController]
tags: [camera, field-of-view, restore, borrow]
---
A borrower that changes the field of view owes it back. KSAGolf makes the field a part of the main-view record, writes it with the pose, and restores it **before** the mode so the handover frame is drawn at the player's field. The record and
hand-back are the helpers described in [Borrow the main camera, hand it back and arbitrate between camera users](../borrow-the-main-camera-and-hand-it-back/overview.md); this article is the field's part in them.

## Background: how the game does it

`SetFieldOfView` does not clamp and rebuilds the projection immediately; the zoom keys (`ChangeFieldOfView`) clamp to 15-120 degrees, so a value narrower than 15 is thrown away by a key press and a player cannot widen back from it
([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L417-L421), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L455-L462)). The builder throws for a field of zero or at least half a turn, from wherever `UpdateProjection` was called, which can be a frame hook ([current/decomp/RenderCore/ReverseDepthBufferUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ReverseDepthBufferUtils.cs#L8-L13)).

## Lifecycle and timing

The record is taken in the first borrowed step (`frame:draw-ui`); the pose source restates the field every frame from inside the controller's `OnFrame` (`frame:viewports`) so it cannot be overwritten ([Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md)); the field is restored in the hand-back, before the mode switch.

## Steps

1. **Record it with the view:** the record carries `FovDeg` next to `Following` and `Mode`, read from the camera currently showing:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1220-L1236
public static double MainViewFovDeg()
{
    try
    {
        if (Program.MainViewport?.GetCamera() is not { } camera) return DefaultFovDeg;

        double degrees = double.RadiansToDegrees(camera.GetFieldOfView());

        return double.IsFinite(degrees) && degrees > 0.0 && degrees < 180.0
            ? degrees
            : DefaultFovDeg;
    }
    catch
    {
        return DefaultFovDeg;
    }
}
```

   ([src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1612-L1625) stores it as the third field.)

2. **Clamp every write.** The setter refuses non-finite values and clamps to 1-120:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1249-L1268
public static bool TrySetMainViewFov(double degrees)
{
    if (!double.IsFinite(degrees)) return false;

    try
    {
        if (Program.MainViewport?.GetCamera() is not { } camera) return false;

        float wanted = (float)Math.Clamp(degrees, MinFovDeg, MaxFovDeg);
        if (Math.Abs(double.RadiansToDegrees(camera.GetFieldOfView()) - wanted) < 1e-3) return true;

        camera.SetFieldOfView(wanted);
        return true;
    }
    catch (Exception e)
    {
        Log.Warn($"could not set the field of view: {e.Message}");
        return false;
    }
}
```

3. **State the field in the pose, every frame.** The pose source returns `fovDeg` and the controller applies it, so a borrower that says nothing does not inherit whatever the last one left (a few magnified degrees would make a chase fly down a straw):

```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L265-L292
private void AskThePoseSource()
{
    if (Pose is not { } source) return;
    if (Camera.Following is not { } following) return;

    try
    {
        if (!source.TryPose(following.GetPositionEcl(), out double3 offset,
                            out double3 forward, out double3 up, out double fovDeg))
        {
            return;
        }

        if (!Vec.IsFinite(offset) || Vec.Len2(forward) < 0.5) return;

        CameraOffset = offset;
        CameraRotation = forward;
        UpEcl = up;

        // Clamped rather than refused. The engine's own setter does not clamp and the
        // projection throws outside (0, pi) -- from in here, which is the engine's loop.
        KsaWorld.TrySetMainViewFov(fovDeg);
    }
    catch
    {
        // Keep whatever was written a frame ago rather than dropping the view.
    }
}
```

4. **Restore it first.** The hand-back sets the field, then the mode:

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

## Complete example

- [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1220-L1268), [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1901-L1923)
- [src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs#L265-L292)

## Variations and gotchas

- **Always give the player's field back**, even when the player took the mode or follow: the mod always changed it ([approach 2 of the borrowing topic](../borrow-the-main-camera-and-hand-it-back/borrow-the-main-camera-and-hand-it-back-2.md)).
- **KSAGolf clamps to 1-120**; Glass allows up to 179 through its own prefix ([approach 1](override-the-camera-field-of-view-1.md)).
- **Do not read the field after your first write** to learn what the player had.

## See also

- [Topic overview](overview.md); [approach 1](override-the-camera-field-of-view-1.md), [2](override-the-camera-field-of-view-2.md)
- [Level-horizon camera controller swapped into the main viewport](../../../../mods/ksagolf/features/level-horizon-camera-controller.md)
- [Borrow the main camera, hand it back and arbitrate between camera users](../borrow-the-main-camera-and-hand-it-back/overview.md), [Understand KSA camera modes, controllers, the viewport frame order and which camera is which](../../controllers/understand-camera-modes-and-controllers/overview.md), [Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](../../secondary-viewports/aim-a-secondary-viewport-camera/overview.md)
