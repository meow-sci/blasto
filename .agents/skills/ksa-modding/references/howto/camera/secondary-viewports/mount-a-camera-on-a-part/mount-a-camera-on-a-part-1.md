---
type: How-To
title: Pose a camera from the part's Asmb2Ego matrices in a FixedController.OnFrame prefix
description: Hot Pursuit composes the vehicle matrix in the main camera's ego space with the sub-part chain, transforms the mount normal by the inverse-transpose so scaled parts do not skew the aim, and writes the pose before Camera.OnFrame builds the matrices
category: camera
group: secondary-viewports
topic: mount-a-camera-on-a-part
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/hot-pursuit-part-mounted-cameras]
techniques: [harmony-prefix]
hooks: [harmony-prefix KSA.FixedController.OnFrame]
lifecycle: [frame:viewports, frame:draw-ui, frame:render]
game_types: [KSA.Part, KSA.Vehicle, KSA.Camera, KSA.IGameViewport, KSA.FixedController]
tags: [camera, part, mount, matrix, scale, hot-pursuit]
---
A camera bolted to a nose cone, a mast or a landing leg must ride the part through everything it does: the vessel rotating, sub-parts articulating, parts scaled. The engine already knows each part's transform; the mod's job is to compose it in the right space, keep the aim right on a non-uniformly scaled part and write it before the
matrices are built. Hot Pursuit does that for its secondary-viewport cameras. The viewport lease is [Lease a secondary viewport for a mod camera and show it in a window](../borrow-a-secondary-viewport/overview.md); the prefix and the nearby-body sync are [approach 3](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md) and [aiming a secondary camera](../aim-a-secondary-viewport-camera/overview.md).
Prerequisites: StarMap and Harmony.

## Background: how the game does it

- `Vehicle.GetMatrixAsmb2Ego(Camera)` is the vessel assembly-to-ego matrix for a given camera: `Asmb2Ego` rotation plus the vessel's ego position (`camera.GetPositionEgo(vehicle)`) less the rotated centre of mass ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270-L1273), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1276-L1281)).
- `Part.MatrixAsmb2Ego(in vehicleMatrix)` composes the part's scale, rotation and translation with its parent chain, so articulated sub-parts and scaled parts are followed exactly ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1213-L1224)).
- Ego space is camera-relative; `Camera.EgoToEcl` converts one point to absolute ecliptic ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L222-L226)). Working in ego space and converting once avoids subtracting huge ecliptic coordinates.

## Lifecycle and timing

`frame:viewports`: the prefix on `FixedController.OnFrame` runs immediately before `Camera.OnFrame` builds the matrices, with this frame's physics results applied and the part matrices stable on the main thread, so the feed has no one-frame lag behind the vessel. The secondary scene render uses the matrices later (`frame:render`);
the lease and target bookkeeping run in `Update` (`frame:draw-ui`) and the pose re-resolves the target itself because a part destroyed during `PrepareFrame` would otherwise leave last frame's disposed reference.

## Steps

1. **Store the mount in the part's own frame.** A mount point, a surface normal and a tangent (the camera's initial up), all in the sub-part's local assembly frame, plus the vehicle id and the part's `InstanceId` ([approach 3](overview.md), [article](mount-a-camera-on-a-part-3.md)).

2. **Use the main camera as the reference frame** (not the secondary camera's own previous pose, which would add a one-frame floating-origin error), compose the vessel and part matrices, and take the position through the part matrix:

```csharp
// hot-pursuit.lib/HotPursuitPose.cs L13-L45
internal static bool TryApply(HotPursuitCamera entry, IViewport viewport)
{
    if (entry.Vehicle is not { } vehicle || entry.Part is not { } part ||
        entry.Viewport is not { } ownedViewport || !ReferenceEquals(ownedViewport, viewport) ||
        !entry.Visible)
        return false;

    if (ownedViewport.BaseCamera.Following != vehicle)
    {
        // FixedController normally does this only when the slot is first configured. Reassert
        // it here too: KSA may clear/repoint a camera during scene changes or target reloads.
        ownedViewport.BaseCamera.SetFollow(vehicle, tidalLocking: true,
            changeControl: false, alert: false);
    }

    var referenceCamera = Program.MainViewport.GetCamera();
    var vehicleMatrix = vehicle.GetMatrixAsmb2Ego(referenceCamera);
    var partMatrix = part.MatrixAsmb2Ego(in vehicleMatrix);
    var mount = entry.MountPoint + entry.Translation;
    var positionEgo = double3.Transform(mount, partMatrix);
    var positionEcl = referenceCamera.EgoToEcl(positionEgo);

    if (!IsFinite(positionEcl))
        return false;

    if (!TryBuildBasis(entry, partMatrix, out var forwardEcl, out var upEcl,
            out var rightEcl))
        return false;

    // The basis is already in ECL and orthonormal. Applying Euler here avoids skewing camera
    // angles through a non-uniformly scaled part while retaining the scaled surface's true
    // inverse-transpose normal.
    ApplyEuler(ref forwardEcl, ref upEcl, ref rightEcl, entry.RotationDeg);
```

3. **Build the basis with the inverse-transpose for the normal.** Positions and tangents use the part matrix; the surface normal uses the inverse-transpose so the camera stays aimed along the true normal on a non-uniformly scaled face:

```csharp
// hot-pursuit.lib/HotPursuitPose.cs L79-L98
private static bool TryBuildBasis(HotPursuitCamera entry, double4x4 partMatrix,
    out double3 forward, out double3 up, out double3 right)
{
    forward = up = right = double3.Zero;
    if (!double4x4.Invert(partMatrix, out var inversePartMatrix))
        return false;

    // Positions/tangents use the full part matrix. Surface normals use inverse-transpose;
    // using the ordinary direction transform points the camera the wrong way on a
    // non-uniformly scaled face.
    var normalMatrix = double4x4.Transpose(inversePartMatrix);
    forward = Normalize(double3.TransformNormal(entry.SurfaceNormal, normalMatrix));
    up = double3.TransformNormal(entry.MountTangent, partMatrix);
    up -= forward * double3.Dot(up, forward);
    up = Normalize(up);
    if (forward.LengthSquared() <= BasisEpsilon || up.LengthSquared() <= BasisEpsilon)
        return false;
    right = Normalize(double3.Cross(forward, up));
    return right.LengthSquared() > BasisEpsilon;
}
```

4. **Apply the player's pitch, yaw and roll in the mount basis** (`ApplyEuler`), re-orthogonalise the up so `LookAtRotation` never receives a nearly parallel pair, and write position, rotation and field of view ([hot-pursuit.lib/HotPursuitPose.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitPose.cs#L101-L115)):

```csharp
// hot-pursuit.lib/HotPursuitPose.cs L46-L76

var forwardLength = forwardEcl.Length();
var upLength = upEcl.Length();
if (!double.IsFinite(forwardLength) || !double.IsFinite(upLength) ||
    forwardLength <= BasisEpsilon || upLength <= BasisEpsilon)
    return false;

forwardEcl /= forwardLength;
// Re-orthogonalize up after Euler/scale so LookAtRotation never receives a nearly parallel
// pair. A final fallback preserves a valid camera even on a badly scaled imported part.
upEcl -= forwardEcl * double3.Dot(upEcl, forwardEcl);
upLength = upEcl.Length();
if (!double.IsFinite(upLength) || upLength <= BasisEpsilon)
{
    upEcl = double3.Cross(rightEcl, forwardEcl);
    upLength = upEcl.Length();
}
if (!double.IsFinite(upLength) || upLength <= BasisEpsilon)
    return false;
upEcl /= upLength;

var camera = ownedViewport.BaseCamera;
camera.PositionEcl = positionEcl;
// GameViewport calls Camera.OnFrame after this prefix, which terrain-clamps the camera.
// Clamp once now as well so the celestial metrics below describe the final render pose;
// Camera.OnFrame's second clamp is then idempotent.
camera.ClampCamera();
HotPursuitCelestialState.Synchronize(camera);
camera.WorldRotation = Camera.LookAtRotation(forwardEcl, upEcl);
camera.SetFieldOfView(entry.FieldOfView);
return true;
```

## Complete example

- [hot-pursuit.lib/HotPursuitPose.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitPose.cs), [hot-pursuit.lib/HotPursuitPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitPatches.cs), [hot-pursuit.lib/HotPursuitSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.cs#L119-L145)

## Variations and gotchas

- **Terrain clamp.** `Camera.OnFrame` clamps the camera to 0.5 m above the surface, so mounts on a landed vessel's underside are lifted.
- **A part that leaves its vessel** (staging, structural failure) is no longer found under the stored vehicle id; the camera goes dormant ([approach 3](mount-a-camera-on-a-part-3.md); [Keeping an anchor on a part across staging, docking, editor changes and saves](../../../vehicles-and-parts/part-geometry/anchor-to-parts-across-vehicle-changes/overview.md)).
- **Feeds cost.** Each visible camera is a reduced extra scene render, without ocean, clouds, particles, exhaust, shadows or glass.
- **Members unchanged** between 5541 and 5554 per the feature page; `FixedController.OnFrame` is resolved with an explicit parameter list and `Apply` throws `MissingMethodException` if it disappears.

## See also

- [Topic overview](overview.md); [approach 2](mount-a-camera-on-a-part-2.md), [3](mount-a-camera-on-a-part-3.md)
- [Live part-mounted cameras in leased stock secondary viewports](../../../../mods/unscience/features/hot-pursuit-part-mounted-cameras.md)
- [Lease a secondary viewport for a mod camera and show it in a window](../borrow-a-secondary-viewport/overview.md), [Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](../aim-a-secondary-viewport-camera/overview.md), [Keeping an anchor on a part across staging, docking, editor changes and saves](../../../vehicles-and-parts/part-geometry/anchor-to-parts-across-vehicle-changes/overview.md), [Ray cast against part meshes and terrain, from the cursor or any origin](../../../physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md), [Pick the vessel, part, world quad or terrain point under the cursor](../../../ui/input/pick-objects-under-the-cursor/overview.md)
