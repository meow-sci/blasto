---
type: How-To
title: Add the up vector the stock FixedController lacks and solve up and aim together for a mod-simulated body
description: KSArmory's controller subclass supplies the roll the stock FixedController derives from the followed object's type, which is wrong for a round the mod simulates itself
category: camera
group: controllers
topic: drive-the-main-camera-in-fixed-mode
approach: 6
difficulty: advanced
prerequisites: [starmap, reflection]
demonstrated_by: [mods/ksarmory/features/chase-camera-on-a-round]
techniques: [controller-subclass]
hooks: [subclass KSA.FixedController.OnFrame]
lifecycle: [frame:viewports, frame:draw-ui]
game_types: [KSA.FixedController, KSA.IFollowable, KSA.OrbitView, KSA.Camera, KSA.CameraReferenceFrame]
tags: [camera, roll, up-vector, fixed-camera, chase, mod-simulated]
---
KSArmory follows a round it simulates itself with a custom `IFollowable`
([chasing a fast body](../../main-view/chase-a-fast-body-with-the-main-camera/overview.md)). Following such an object makes the stock fixed camera roll the
horizon by the site's angle from the ecliptic pole, because `GetFrame2Ecl` knows only `Vehicle`, `Celestial` and `VehicleEditingSpace`. This article
covers **controlling roll with an up vector** for that case: a `FixedController` subclass that takes up as an input and solves up and aim together.

## Background: how the game does it

```csharp
// current/decomp/KSA/FixedController.cs L36-L46
OrbitView orbitView = following.OrbitView;
doubleQuat frame2Ecl = GetFrame2Ecl(following, orbitView.ReferenceFrame);
double3 b = double3.UnitZ.Transform(frame2Ecl);
double3 cameraRotation = CameraRotation;
double3 a = double3.Cross(cameraRotation, b).Normalized();
double3 cameraRotation2 = CameraRotation;
double3 upEcl2 = double3.Cross(a, cameraRotation2).Normalized();
doubleQuat localRotation = KSA.Camera.LookAtRotation(cameraRotation2, upEcl2);
double3 positionEcl = following.GetPositionEcl() + CameraOffset;
Camera.PositionEcl = positionEcl;
Camera.LocalRotation = localRotation;
```

`GetFrame2Ecl` returns the following for an unrecognised followed type ([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L50-L100)):

```csharp
// current/decomp/KSA/FixedController.cs L95-L99
else
{
	result = ((!(focused is VehicleEditingSpace vehicleEditingSpace)) ? doubleQuat.Identity : doubleQuat.Concatenate(doubleQuat.CreateFromAxisAngle(double3.UnitX, Math.PI), vehicleEditingSpace.Asmb2Ecl));
}
return result;
```

so for a mod followable the roll axis is ecliptic +Z, whatever `OrbitView.ReferenceFrame` says; the horizon is level only where the ecliptic pole, the local
vertical and the view happen to be coplanar. A view along that axis divides by zero.

## Lifecycle and timing

Installed lazily from the GUI-hook step (`frame:draw-ui`); runs every frame in `frame:viewports`, asking the chase camera for its pose
inside `OnFrame` ([Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../write-the-camera-pose-in-phase-with-the-frame/overview.md)).

## Control roll with an up vector

1. **Install the subclass** ([Replace a viewport's camera controller and take input through it](../replace-a-viewport-camera-controller/overview.md)): `KsaWorld.LevelTheHorizon` writes the backing field and checks it
   ([src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5435-L5467)).

2. **Ask the pose source for up as well as aim.** The chase computes local up from gravity at the round and returns both in `TryPose`;
   with no up given, the controller behaves as stock:

```csharp
// src/KSArmory/Ksa/LevelHorizonController.cs L123-L164
public override void OnFrame(IViewport inViewport, double inDeltaTime)
{
    AskThePoseSource();

    double3 forward = Vec.Unit(CameraRotation);

    // The previous frame's answer is carried through the singularity rather than handing back
    // to the engine's rule there. Switching rule is what flips the picture: a view along its
    // own up has no roll, so the two conventions disagree by half a turn, and creeping past
    // that point swaps between them. See SightPicture.TryStableUp.
    // No up given at all is the caller saying it has no opinion, which is KSA's own rule and
    // not a singular case to carry through. Forgetting the last one matters: keeping it would
    // resume a stale roll the moment stabilising is switched back on.
    if (Vec.Len2(Vec.Unit(UpEcl)) < 0.5)
    {
        Forget();
        base.OnFrame(inViewport, inDeltaTime);
        return;
    }

    // How fast the wanted up may pull the carried one. Fast enough that levelling looks
    // immediate on any ordinary slew, slow enough that it cannot snap: at 180 deg/s a frame
    // moves it three degrees, which is below what anyone sees as a jump.
    double step = LevelRateRad * Math.Clamp(inDeltaTime, 0.0, 0.1);

    if (!SightPicture.TryStableUp(forward, UpEcl, _lastUp, step, out double3 up))
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

3. **Use a carried, rate-limited up** (`SightPicture.TryStableUp`, the same algorithm as [approach 5](drive-the-main-camera-in-fixed-mode-5.md)) so
   passing the vertical never flips the picture.

4. **Without the subclass, refuse a near-axis forward.** If the install failed, `TryLookFromMainViewport` returns false for a forward within about 2.6 degrees of
   ecliptic +Z, and the chase releases the view ([src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5536-L5550)).

## Complete example

- [src/KSArmory/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LevelHorizonController.cs), [src/KSArmory/Sim/SightPicture.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SightPicture.cs#L136-L168)
- [src/KSArmory/Ksa/ChaseCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ChaseCamera.cs): the pose source

## Variations and gotchas

- **Doc drift in the mod.** `ChaseView.LeanOffAxis` documents the stock singular axis as ecliptic +Z while `ChaseCamera` passes local up as that argument; with the subclass installed nothing is singular ([Chase camera riding a mod-simulated round](../../../../mods/ksarmory/features/chase-camera-on-a-round.md)).
- **Up need not be perpendicular.** `LookAtRotation` orthogonalises the pair, so up is a reference rather than a constraint.
- **Reflection name.** A rename of the backing field degrades to the stock controller with a warning.

## See also

- [Topic overview](overview.md); [approach 4](drive-the-main-camera-in-fixed-mode-4.md), [5](drive-the-main-camera-in-fixed-mode-5.md)
- [Chase camera riding a mod-simulated round](../../../../mods/ksarmory/features/chase-camera-on-a-round.md)
- [Chase a projectile or other fast body with the main camera](../../main-view/chase-a-fast-body-with-the-main-camera/overview.md), [Make the camera follow a point or mod-simulated object, and change the follow target without a jump](../../main-view/follow-a-point-that-is-not-a-vehicle/overview.md)
