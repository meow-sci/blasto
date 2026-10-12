---
type: How-To
title: Convert a standing kitten's model space to the ecliptic or a camera's ego frame
description: A KittenFrame helper repeats the engine's model-to-world chain (avatar scale, two quarter turns, minus the centre of mass, Body2Cce, vehicle position) so any model-space point converts to the ecliptic or to a given camera's ego frame without reading a drawn matrix
category: kittens
group: animation
topic: pose-kitten-bones-with-an-anim-processor
approach: 5
difficulty: advanced
prerequisites: [starmap, reflection]
demonstrated_by: [mods/ksagolf/features/putting-address-pose, mods/ksastructures/features/first-person-kitten-view]
techniques: [reflection]
hooks: [reflection KSA.KittenRenderable._characterAvatar]
lifecycle: [frame:render, frame:draw-ui, frame:viewports]
game_types: [KSA.KittenEva, KSA.KittenRenderable, KSA.CharacterAvatar, KSA.Vehicle, RenderCore.Animation.Skeleton]
tags: [kitten, model-space, coordinates, frames, ecliptic, ego]
---
A bone position read in `UpdateSkeleton` is in the kitten's model space: centimetres, +Y up, +Z the way the kitten faces, +X its left. A camera, a ball or a target is in the ecliptic. To aim an arm at a ball, or to put a first-person camera on a head bone, you convert between them. Both KSAGolf and KSAStructures carry the same small helper, `KittenFrame`, which repeats the chain the engine draws the kitten through instead of reading a drawn matrix. This article explains the chain and the helper. Prerequisites: [reach the avatar](../../characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-1.md) and a basic understanding of the game's frames ([coordinate frames and epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)).

## Background: how the game does it

The kitten is drawn at a matrix built in two places:

- `KittenRenderable.ModelToBodyMatrix()` is `scale(avatar Core.Scale) * RotationX(-90 deg) * RotationZ(-90 deg)`: model centimetres to the vehicle's body (assembly) frame in metres ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L107-L110)). `Core.Scale` is 0.01 for the stock kitten.
- `KittenEva.UpdateRenderData` takes the vehicle's world matrix for the drawing camera (`GetWorldMatrix(camera)`) and **subtracts the centre of mass** (`value.Translation -= float3.Transform(CenterOfMassAsmbF, value) - value.Translation`), so a kitten is drawn at its origin less its centre of mass; a point measured off the model's drawn origin is therefore a centre-of-mass offset out ([current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L1062-L1077)).
- `KittenRenderable.UpdateRenderData` then sets `CharacterModel.Transform = ModelToBodyMatrix * transform` ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L346-L353)).

So model to ecliptic is: scale, quarter turn about X, quarter turn about Z, minus the centre of mass, rotate by `Body2Cce`, add the vehicle's ecliptic position. A matrix read back from a drawn kitten belongs to whichever camera drew it last (the transform is camera-relative), which is why the mods rebuild it.

## Lifecycle and timing

| Use | Where | Lifecycle |
|---|---|---|
| Read a bone in model space | `IAnimProcessor.UpdateSkeleton`, per view | `frame:render` |
| Convert it to the ecliptic and pose a camera | a camera controller `OnFrame`, using the head recorded by the last render | `frame:viewports` |
| Convert a ball or target into model space for a stance | a StarMap GUI hook | `frame:draw-ui` |

The head position is recorded during the render and read in the next frame's viewport pass, so the eye lags the bone by one frame; the position of the vehicle used in the conversion must be the same frame's ([first-person topic](../../../camera/special-views/first-person-camera-for-a-kitten/overview.md)).

## Steps

1. **Keep the constants the engine uses.** The two quarter turns as one quaternion, the scale from the avatar (default 0.01 when the avatar cannot be read), and the vehicle's centre of mass:

```csharp
// src/KSAGolf/Ksa/KittenFrame.cs L18-L28
    private static readonly doubleQuat ModelToBody = doubleQuat.Concatenate(
        doubleQuat.CreateFromAxisAngle(new double3(1, 0, 0), -Math.PI / 2.0),
        doubleQuat.CreateFromAxisAngle(new double3(0, 0, 1), -Math.PI / 2.0));

    private static double ScaleOf(KittenEva kitten) => KittenPoseProbe.AvatarOf(kitten)?.Core.Scale ?? 0.01;

    private static double3 CentreOfMass(Vehicle v)
    {
        float3 c = v.CenterOfMassAsmbF;
        return new double3(c.X, c.Y, c.Z);
    }
```

2. **Model point to ecliptic, and direction to ecliptic.** A point is scaled, turned into the body frame, shifted by the negative centre of mass, rotated by `Body2Cce` and added to the vehicle position; a direction skips scale, centre of mass and position:

```csharp
// src/KSAGolf/Ksa/KittenFrame.cs L30-L41
    /// <summary>A model-space point, in the ecliptic.</summary>
    public static double3 ToEcl(KittenEva kitten, double3 modelCm) => ToEcl(kitten, modelCm, kitten.Body2Cce);

    private static double3 ToEcl(KittenEva kitten, double3 modelCm, doubleQuat body2Cce)
    {
        double3 body = double3.Transform(modelCm * ScaleOf(kitten), ModelToBody) - CentreOfMass(kitten);
        return kitten.GetPositionEcl() + double3.Transform(body, body2Cce);
    }

    /// <summary>A model-space direction, in the ecliptic.</summary>
    public static double3 DirectionToEcl(KittenEva kitten, double3 model)
        => double3.Transform(double3.Transform(model, ModelToBody), kitten.Body2Cce);
```

3. **Model to a camera's ego frame.** The matrix the engine draws the kitten with from a given camera is the same chain as a `float4x4`, ending with the vehicle's position relative to that camera, so any camera (main view or a camera window) can be handled without reading a drawn matrix:

```csharp
// src/KSAGolf/Ksa/KittenFrame.cs L43-L52
    /// <summary>Model space to a camera's frame, the matrix the engine draws the kitten with from that camera.</summary>
    public static float4x4 ModelToEgo(KittenEva kitten, double3 cameraEcl)
    {
        float scale = (float)ScaleOf(kitten);
        double3 com = CentreOfMass(kitten);
        return float4x4.CreateScale(scale) * float4x4.CreateRotationX(-MathF.PI / 2f) * float4x4.CreateRotationZ(-MathF.PI / 2f)
               * float4x4.CreateTranslation(float3.Pack(-com))
               * float4x4.CreateFromQuaternion(floatQuat.Pack(kitten.Body2Cce))
               * float4x4.CreateTranslation(float3.Pack(kitten.GetPositionEcl() - cameraEcl));
    }
```

4. **The inverse, for targets.** An ecliptic point (a ball) to model space, and an ecliptic direction (the target direction) to model space, for aiming:

```csharp
// src/KSAGolf/Ksa/KittenFrame.cs L54-L63
    /// <summary>An ecliptic point, in model space.</summary>
    public static double3 FromEcl(KittenEva kitten, double3 ecl)
    {
        double3 body = double3.Transform(ecl - kitten.GetPositionEcl(), doubleQuat.Inverse(kitten.Body2Cce)) + CentreOfMass(kitten);
        return double3.Transform(body, doubleQuat.Inverse(ModelToBody)) / ScaleOf(kitten);
    }

    /// <summary>An ecliptic direction, in model space.</summary>
    public static double3 DirectionFromEcl(KittenEva kitten, double3 ecl)
        => double3.Transform(double3.Transform(ecl, doubleQuat.Inverse(kitten.Body2Cce)), doubleQuat.Inverse(ModelToBody));
```

5. **Use it.** KSAGolf converts the ball and the target direction into model space to compute the stance (`Stance.TryFor(ballModel, targetModel, ...)`), KSAStructures converts the head bone to the ecliptic to place the camera:

```csharp
// src/KSAStructures/Ksa/FirstPerson.cs L118-L120
        double3 heading = (north * Math.Cos(_look.Yaw)) + (east * Math.Sin(_look.Yaw));
        double3 headEcl = KittenFrame.ToEcl(kitten, head) - KsaWorld.PositionEcl(kitten) + followedEcl;
        double3 eye = headEcl + (heading * (EyeForwardCm / 100.0)) + (up * (EyeUpCm / 100.0));
```

6. **Verify against the engine.** KITTEN-ARMS.md records that skeleton space and drawn space fit to 34 nm in a least-squares comparison of bone positions; a quick check is to compare `ToEcl` of the head bone against the camera-projected head in a screenshot.

## Complete example

- [src/KSAGolf/Ksa/KittenFrame.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KittenFrame.cs#L16-L64) - the helper with `ToEcl`, `DirectionToEcl`, `ModelToEgo`, `FromEcl`, `DirectionFromEcl`.
- [src/KSAStructures/Ksa/KittenFrame.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KittenFrame.cs#L16-L71) - the same helper with its own `AvatarOf`.
- [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L575-L592) - converting a ball and target into model space for a stance.
- Feature pages: [Putting address pose](../../../../mods/ksagolf/features/putting-address-pose.md), [First-person kitten view](../../../../mods/ksastructures/features/first-person-kitten-view.md).

## Variations and gotchas

- **Centre of mass.** Forgetting it puts every point half a kitten out. The mods read `Vehicle.CenterOfMassAsmbF` and rebuild the chain each call.
- **Seated kittens use a different anchor.** The model origin of a seated kitten hangs 0.63 m below the seat's eye point along the seat's up axis (`SeatedOffset`), so a seat's conversion is a seat-axes mapping, not this chain ([two-bone IK](pose-kitten-bones-with-an-anim-processor-4.md)).
- **Same-frame inputs.** Convert with the vehicle position and `Body2Cce` of the same frame as the head you recorded; a mismatch shifts the result by velocity times the time difference.
- **Floating origin.** Ecliptic positions are large; do subtractions in double before narrowing to float, as the helper does for `ModelToEgo`.
- **Do not read the drawn matrix.** `CharacterModel.Transform` is relative to the camera that drew last.

## See also

- [Pose a kitten's bones and aim its limbs with an IAnimProcessor](overview.md), [approach 6](pose-kitten-bones-with-an-anim-processor-6.md)
- [Understand KSA coordinate frames and epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md), [Choose a render frame and anchor effects](../../../rendering/overlays/choose-a-render-frame-and-anchor-effects/overview.md)
- [Put the camera in an EVA kitten's eyes](../../../camera/special-views/first-person-camera-for-a-kitten/overview.md)
