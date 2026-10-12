---
type: How-To
title: Aim an unfollowed Fixed camera on a secondary viewport with LookAt and the platform's local up
description: KSArmory's camera window unfollows every frame, sets Fixed and writes the pose with Camera.LookAt using the optical head's boresight as the up vector so the horizon sits level
category: camera
group: controllers
topic: drive-the-main-camera-in-fixed-mode
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/director-camera-window]
techniques: [direct-camera-write]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:viewports]
game_types: [KSA.FixedController, KSA.GameViewport, KSA.IGameViewport, KSA.Camera, KSA.CameraMode, KSA.Program]
tags: [camera, fixed-camera, lookat, secondary-viewport, up-vector, roll]
---
The simplest way to avoid every `FixedController` trap is to follow nothing: with no followed object,
`FixedController.OnFrame` does nothing, so the pose the mod writes with `Camera.LookAt` stands. KSArmory's optical-director camera window does
this on a leased secondary viewport. The second half of this article covers **controlling roll with an up
vector**: the `up` argument of `LookAt` is the platform's local up (the head's boresight), so the horizon
sits level without a custom controller. The window and lease are in [Lease a secondary viewport for a mod camera and show it in a window](../../secondary-viewports/borrow-a-secondary-viewport/overview.md) and the rebuild of matrices in [Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](../../secondary-viewports/aim-a-secondary-viewport-camera/overview.md).

## Background: how the game does it

`FixedController.OnFrame` only acts when `Camera.Following` is not null ([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L22-L26)). `Camera.LookAt(position, target,
up)` sets `PositionEcl` and then `LocalRotation = LookAtRotation(target - PositionEcl, up)` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L203-L220)); `LookAtRotation`
normalises the forward and the cross product, so an up parallel to the forward would throw.

## Lifecycle and timing

The aim is written from the `[StarMapAfterGui]` step (`frame:draw-ui`), after this frame's `OnFrameViewports`; the mod then
rebuilds the matrices itself (`camera.OnFrame(dt)`) before the render ([the aiming topic](../../secondary-viewports/aim-a-secondary-viewport-camera/overview.md)). The next
`frame:viewports` runs a no-op controller and `Camera.OnFrame`, which keeps the pose (apart from the terrain clamp).

## Steps

1. **Unfollow every frame, then set `Fixed`.** A follow can arrive at any time (vessel next/previous act on the *hovered*
   viewport), and with one attached the controller would place the camera at `following + CameraOffset`, an offset this
   path never writes:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L5705-L5714
try
{
    if (viewport.GetCamera() is { Following: not null } followed)
    {
        followed.Unfollow(changeControl: false);
    }
}
catch { /* a view that cannot be unfollowed is still worth trying to aim */ }

if (viewport.Mode != CameraMode.Fixed) viewport.SetCameraMode(CameraMode.Fixed);
```

2. **Write position and orientation together with `LookAt`**, passing the up you want:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L5719-L5722
// Position and orientation together: setting one without the other leaves a frame
// drawn from the old place looking the new way.
camera.LookAt(eyeEcl, eyeEcl + Vec.Unit(forwardEcl) * 1000.0, upEcl);
```

## Control roll with an up vector

3. **Pass the platform's local up.** The caller hands the head's `Boresight` (local up at the platform), so the horizon is level instead
   of rolling with the ecliptic:

```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L1157-L1164
// A window has no player's field to borrow, so the magnification is taken against the
// engine's default, which is what the window shows at x1.
double fovDeg = SightZoom.FovDegreesFor(SightZoom.DefaultFovDeg, policy.Magnification);

// Local "up" at the launcher, which is what the boresight already is — so the horizon
// sits level rather than rolling with the ecliptic.
bool took = KsaWorld.TryLookFromViewport(policy.Viewport, eye, forward,
                                         system.Boresight, fovDeg, dt);
```

4. **Keep the forward off the up.** `LookAtRotation` divides by zero for a forward parallel to the up; the helper rejects
   non-finite or zero forwards but the caller must keep the sight line away from straight up/down when the up is local vertical.

## Complete example

- [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5679-L5755)
- [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L1109-L1170): `DriveWindow`

## Variations and gotchas

- **Map mode hands back the map camera**, and moving it moves the map, not the world; Fixed draws the scene from wherever the camera is.
- **Unfollowed cameras draw neighbours at their analytic position**, not the physics-bubble position (`Camera.GetPositionEgo` only uses the bubble branch while following a vehicle in the same bubble, [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236-L250)); anchor the eye on the platform as this window's camera draws it.
- **The unfollow keeps `PositionEcl`** (`Unfollow` writes it back), so the transition does not jump ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L620-L631)).
- **This path is not a replacement for a controller** when the camera must follow something: once followed, `CameraOffset` and `CameraRotation` are what count.

## See also

- [Topic overview](overview.md); [approach 5](drive-the-main-camera-in-fixed-mode-5.md), [6](drive-the-main-camera-in-fixed-mode-6.md)
- [Director camera windows on leased secondary viewports](../../../../mods/ksarmory/features/director-camera-window.md)
- [Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](../../secondary-viewports/aim-a-secondary-viewport-camera/overview.md), [Lease a secondary viewport for a mod camera and show it in a window](../../secondary-viewports/borrow-a-secondary-viewport/overview.md), [Override the camera field of view and zoom past the stock clamp](../../main-view/override-the-camera-field-of-view/overview.md)
