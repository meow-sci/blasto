---
type: How-To
title: Carry a rate-limited StableUp reference instead of switching up vectors near the vertical
description: KSAGolf's StableUp keeps last frame's up, orthogonal to the view, and rotates it toward the wanted one by at most a bounded step scaled by how much of the wanted up lies across the view, so no hard switch happens near vertical
category: camera
group: controllers
topic: drive-the-main-camera-in-fixed-mode
approach: 5
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/ksagolf/features/level-horizon-camera-controller
  - mods/ksastructures/features/level-horizon-camera-controller
  - mods/ksagolf/features/borrowed-chase-camera
techniques: [controller-subclass]
hooks: [subclass KSA.FixedController.OnFrame]
lifecycle: [frame:viewports]
game_types: [KSA.FixedController, KSA.Camera, KSA.OrbitView, KSA.IFollowable]
tags: [camera, roll, up-vector, fixed-camera, horizon, math]
---
The stock `FixedController` derives roll from the followed object's type, which is wrong for a mod-simulated body and rolls hard
where the view passes the roll axis. This article covers **controlling roll with an up vector** in a replacement controller:
a pure `StableUp` class (also used by the chase view) that carries the previous up forward and rotates it toward the wanted one at a
bounded rate, instead of choosing between two rules at a threshold. The controller that calls it is the one from
[Replace a viewport's camera controller and take input through it](../replace-a-viewport-camera-controller/overview.md).

## Background: how the game does it

Stock up is `Cross(Cross(CameraRotation, axis), CameraRotation)`, where `axis` is +Z of
`GetFrame2Ecl(following, OrbitView.ReferenceFrame)`; types the method does not know get `Identity`, so the axis is ecliptic +Z
([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L36-L43), [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L50-L100)). Nothing on `Camera`, `OrbitView`, `CameraOffset` or
`CameraRotation` overrides it. Looking along the axis makes the cross product zero, which throws.

## Lifecycle and timing

`StableUp.Try` runs inside the controller's `OnFrame` (`frame:viewports`) once per frame with `step = rate * clamp(dt, 0, 0.1)`.
The wanted up comes from the borrower's pose source asked in the same call ([Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../write-the-camera-pose-in-phase-with-the-frame/overview.md)).

## Control roll with an up vector

1. **Never switch rules at a threshold.** The mod measured a flip of 89 degrees of roll for 1.3 degrees of aim with the
   view exactly on a cutoff; moving the threshold moves the flip. `StableUp` has no cutoff:

```csharp
// src/KSAGolf/Sim/StableUp.cs L33-L65
public static bool Try(double3 forwardEcl, double3 preferredUp, double3 lastUp,
                               double maxStepRad, out double3 up)
{
    up = Vec.Zero;

    double3 forward = Vec.Unit(forwardEcl);
    if (Vec.Len2(forward) < 0.5) return false;

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
}
```

2. **Authority is the sine of the angle between the wanted up and the view**, computed by `Across`. A wanted up along the view
   has authority zero and stops pulling instead of being switched away from:

```csharp
// src/KSAGolf/Sim/StableUp.cs L69-L85
private static bool Across(double3 forward, double3 candidate, out double3 across,
                           out double authority)
{
    across = Vec.Zero;
    authority = 0.0;

    double3 unit = Vec.Unit(candidate);
    if (Vec.Len2(unit) < 0.5) return false;

    double3 rejected = Vec.RejectFrom(unit, forward);
    double length = Vec.Len(rejected);
    if (length < 1e-6) return false;

    across = rejected / length;
    authority = length;
    return true;
}
```

3. **Feed it from the controller and make the engine's two writes with that up.** The rate is pi rad/s times the frame (clamped to 0.1 s),
   unless the borrower says its up is exact (a view turned directly by the mouse):

```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L169-L191
    // How fast the wanted up may pull the carried one. Fast enough that levelling looks
    // immediate on any ordinary slew, slow enough that it cannot snap: at 180 deg/s a frame
    // moves it three degrees, which is below what anyone sees as a jump. A view the mouse turns
    // directly states its own up exactly, and a limit there is roll trailing a hard turn.
    double step = Pose?.UpIsExact == true ? Math.PI : LevelRateRad * Math.Clamp(inDeltaTime, 0.0, 0.1);

    if (!StableUp.Try(forward, UpEcl, _lastUp, step, out double3 up))
    {
        base.OnFrame(inViewport, inDeltaTime);
        return;
    }

    Probe(forward, up);
    _lastUp = up;

    if (Camera.Following is not { } following) return;

    // The same two writes the engine makes, in the same order, with the up substituted.
    // LookAtRotation orthogonalises the pair itself, so up is a reference rather than a
    // constraint and need not be perpendicular to the view.
    Camera.PositionEcl = following.GetPositionEcl() + CameraOffset;
    Camera.LocalRotation = Camera.LookAtRotation(forward, up);
}
```

4. **Keep the carried up between frames** in the controller (`_lastUp`) and `Forget()` it when the view is handed back, or a later borrow
   resumes a stale roll ([src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs#L62-L72)).

## Complete example

- [src/KSAGolf/Sim/StableUp.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/StableUp.cs) and its tests [tests/KSAGolf.Tests/StableUpTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/StableUpTests.cs)
- [src/KSAStructures/Sim/StableUp.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/StableUp.cs): the same class in KSAStructures
- [src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs#L39-L191)

## Variations and gotchas

- **Orthogonal output.** `up` is perpendicular to the forward, so handing it back next frame does not let it drift into the view.
- **Seeding.** A zero `lastUp` takes the wanted one outright; false is returned only for a view along its own up on the very frame it was taken.
- **Zero up means no opinion.** The controller then calls `base.OnFrame`, which is exactly the stock behaviour.
- **The same class appears in the chase view** ([Borrowing the main view and handing it back](../../../../mods/ksagolf/features/borrowed-chase-camera.md)); KSArmory's copy is `SightPicture.TryStableUp` ([approach 6](drive-the-main-camera-in-fixed-mode-6.md)).

## See also

- [Topic overview](overview.md); [approach 4](drive-the-main-camera-in-fixed-mode-4.md), [6](drive-the-main-camera-in-fixed-mode-6.md)
- [Level-horizon camera controller swapped into the main viewport](../../../../mods/ksagolf/features/level-horizon-camera-controller.md), [A replacement fixed camera controller with an up vector and an in-phase pose](../../../../mods/ksastructures/features/level-horizon-camera-controller.md)
- [Chase a projectile or other fast body with the main camera](../../main-view/chase-a-fast-body-with-the-main-camera/overview.md), [Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](../../secondary-viewports/aim-a-secondary-viewport-camera/overview.md)
