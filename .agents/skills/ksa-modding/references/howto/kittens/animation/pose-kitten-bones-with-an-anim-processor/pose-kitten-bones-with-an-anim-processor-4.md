---
type: How-To
title: Solve a two-bone arm in seat-relative model space with a clamped reach and an outward-and-down elbow
description: KSACars reads shoulder, elbow and wrist from Skeleton.WorldTransforms in UpdateSkeleton, aims the wrist 4.5 cm short of a grip, places the elbow by the law of cosines and writes both joints with SetWorldTransform, with the maths in a KSA-free class that has tests
category: kittens
group: animation
topic: pose-kitten-bones-with-an-anim-processor
approach: 4
difficulty: advanced
prerequisites: [starmap, reflection]
demonstrated_by: [mods/ksacars/features/driver-hands-steering-ik]
techniques: [two-bone-ik, anim-processor]
hooks: [engine-callback KSA.IAnimProcessor.UpdateSkeleton]
lifecycle: [frame:render]
game_types:
  - KSA.IAnimProcessor
  - KSA.IVASeat
  - KSA.KittenLocomotionTuning
  - RenderCore.Animation.Skeleton
  - KSA.KittenRenderable
tags: [kitten, animation, ik, two-bone, seated, model-space]
---
To put a kitten's wrist exactly on a target (a steering-wheel grip, a lever, a keyboard) you need a two-bone solve: given the shoulder, the target and the two segment lengths, find the elbow, then turn the upper arm and the forearm onto it. KSACars does this for its driving kitten. The geometry is a few lines of law-of-cosines maths in a class that knows nothing about the game, so it has unit tests; the game-facing half reads three world positions from the skeleton and writes two joints back. This article also covers the second thing every aim needs: converting a point from your own frame (here, a car part) into the kitten's model space. Prerequisites: [the world-transform processor](pose-kitten-bones-with-an-anim-processor-2.md).

## Background: how the game does it

- A seated kitten is drawn in its own model space, anchored to the seat: the `IVASeat` builds the kitten's matrix as the seat's rotation and the point `GetSeatedModelPointAsmb(KittenLocomotionTuning.Current.SeatedOffset, 0)`, i.e. the seat position plus `forward * X + right * Y + up * Z` of the offset, and the stock offset is `(0, 0, -0.63)`: the model origin sits 0.63 m below the seat's eye point along the seat's up axis ([current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L52-L57), [current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L80-L105), [current/decomp/KSA/KittenLocomotionTuning.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotionTuning.cs#L145-L149), [current/decomp/KSA/KittenLocomotionTuning.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotionTuning.cs#L216-L222)).
- Inside the model space: centimetres (the avatar scale is 0.01), +Y up, +Z the seat's forward and +X the kitten's left ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L107-L110)). Where the vehicle is in the world never enters the solve.
- `Skeleton.WorldTransforms[bone].Translation` is a bone's model-space position; `SetWorldTransform(bone, matrix)` writes a world matrix and updates the descendants ([current/decomp/RenderCore/PackedTransformExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/PackedTransformExtensions.cs#L135-L150)).
- The processor runs in `UpdateSkeleton`, after the seated idle was applied and before skinning, once per viewport.

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Solve | `DriverHands.UpdateSkeleton`, inside `Vehicle.UpdateSeatedCrewRenderData` -> `IVASeat.UpdateSeatedRenderable` -> `KittenRenderable.UpdateRenderData` -> `AnimatedRenderable.UpdateAnimation` | `frame:render` |
| Inputs | the car's steer angle, written by the mod's physics step earlier in the frame | main thread |

Because the solve places joints absolutely from the seated pose, the second call for a second viewport reproduces the first. Any exception switches the processor off for good; the kitten keeps its seated pose ([game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence)).

## Steps

1. **Express the target in the kitten's model space.** KSACars' grip points are in the car part's frame (nine and three o'clock on the rim, turned with the wheel and clamped to +-20 degrees of wheel so the rim slides through the hands past that); `ToKittenModel` subtracts the seat's eye point lowered by the seated drop and re-axes and scales to centimetres:

```csharp
// src/KSACars/Sim/SteeringGrip.cs L31-L44
    public static (double3 Left, double3 Right) GripsPart(BuggyProfile p, double steerAngle)
    {
        double wheel = Math.Clamp(steerAngle * p.SteeringRatio, -HandsFollowRad, HandsFollowRad);
        doubleQuat turn = doubleQuat.CreateFromAxisAngle(p.SteeringAxis, wheel);
        double3 across = Vec.Unit(Vec.RejectFrom(new double3(0, 0, 1), p.SteeringAxis)) * p.SteeringRimRadius;
        return (p.SteeringPivot + (turn * across), p.SteeringPivot - (turn * across));
    }

    /// <summary>A part-frame point in the driver kitten's model space, centimetres.</summary>
    public static double3 ToKittenModel(BuggyProfile p, double3 part)
    {
        double3 d = part - (p.DriverEye - new double3(SeatedDrop, 0, 0));
        return new double3(100.0 * d.Z, 100.0 * d.X, 100.0 * d.Y);
    }
```

   This mapping is specific to KSACars' car parts (a seat that faces the part's +Y with +X up). For another seat, derive the mapping from the seat's axes: `IVASeat.ForwardAxisAsmb`, `UpAxisAsmb`, `PositionAsmb` and `KittenLocomotionTuning.Current.SeatedOffset`.

2. **Solve the elbow.** The target is first clamped into the arm's reach (98 percent of the sum of the segments, 102 percent of their difference), the point along the line is found with the law of cosines, and the elbow is pushed out toward a bend direction by what is left of the upper arm:

```csharp
// src/KSACars/Sim/SteeringGrip.cs L51-L67
    public static (double3 Elbow, double3 Reached) SolveElbow(double3 shoulder, double3 target,
                                                             double upper, double fore, double3 bend)
    {
        double3 toTarget = target - shoulder;
        double reach = Math.Max((upper + fore) * 0.98, 1e-6);
        double shortest = Math.Abs(upper - fore) * 1.02;
        double length = Math.Clamp(Vec.Len(toTarget), shortest, reach);
        double3 dir = Vec.Len(toTarget) > 1e-9 ? Vec.Unit(toTarget) : Vec.Unit(bend);
        double3 reached = shoulder + (dir * length);

        // Law of cosines along the line, then out toward the bend by what is left of the upper arm.
        double along = ((upper * upper) - (fore * fore) + (length * length)) / (2.0 * length);
        double out_ = Math.Sqrt(Math.Max((upper * upper) - (along * along), 0.0));
        double3 side = Vec.RejectFrom(bend, dir);
        side = Vec.Len(side) > 1e-9 ? Vec.Unit(side) : Vec.AnyPerpendicular(dir);
        return (shoulder + (dir * along) + (side * out_), reached);
    }
```

3. **Read the arm, aim 4.5 cm short, and turn two bones.** Segment lengths come from the current skeleton; the wrist target is the grip moved 4.5 cm toward the shoulder (so the hand closes on the rim); the elbow bends outward and down (`outward` is +1 for the left arm, -1 for the right, since model +X is the kitten's left). The shoulder is turned about its own position so the upper arm points at the new elbow, the elbow is re-read, then turned so the forearm points at the wrist target:

```csharp
// src/KSACars/Ksa/DriverHands.cs L79-L111
    // outward is +1 for the left arm and -1 for the right: model +X is the kitten's left.
    // Returns how far the wrist ended from where it was sent.
    private static double Hold(Skeleton skeleton, int shoulder, int elbow, int wrist, double3 grip, double outward)
    {
        double3 s = Position(skeleton, shoulder);
        double3 e = Position(skeleton, elbow);
        double3 w = Position(skeleton, wrist);
        double upper = Vec.Len(e - s), fore = Vec.Len(w - e);
        if (!(upper > 0.0) || !(fore > 0.0)) return double.NaN;

        double3 target = grip + (Vec.Unit(s - grip) * (SteeringGrip.PalmMetres * 100.0));
        (double3 elbowAt, double3 wristAt) = SteeringGrip.SolveElbow(s, target, upper, fore, new double3(0.5 * outward, -1.0, 0.0));

        TurnAbout(skeleton, shoulder, s, e - s, elbowAt - s);
        double3 eNow = Position(skeleton, elbow);
        TurnAbout(skeleton, elbow, eNow, Position(skeleton, wrist) - eNow, wristAt - eNow);
        return Vec.Len(Position(skeleton, wrist) - target);
    }

    private static double3 Position(Skeleton skeleton, int bone) => double3.Unpack(skeleton.WorldTransforms[bone].Translation);

    // Row vectors: a point is carried by M, then turned about the pivot.
    private static void TurnAbout(Skeleton skeleton, int bone, double3 pivot, double3 from, double3 to)
    {
        doubleQuat q = Vec.RotationFromTo(from, to);
        float3 p = float3.Pack(pivot);
        floatQuat qf = new((float)q.X, (float)q.Y, (float)q.Z, (float)q.W);
        float4x4 turned = skeleton.WorldTransforms[bone]
                          * float4x4.CreateTranslation(-p)
                          * float4x4.CreateFromQuaternion(qf)
                          * float4x4.CreateTranslation(p);
        skeleton.SetWorldTransform(bone, turned);
    }
```

4. **Test the maths without the game.** `SteeringGripTests` places measured shoulders in model space and asserts every grip across the full steering lock is within an arm's length (98 percent of 14.7 cm), for each car profile, so a car whose rim is out of reach fails a unit test instead of looking wrong in game ([tests/KSACars.Tests/SteeringGripTests.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tests/KSACars.Tests/SteeringGripTests.cs#L7-L45)).

5. **Check the result.** The processor records `Solves` and `MissCm` (distance from the wrist to its target in cm). A `MissCm` near zero means the wrist reached; a non-zero value means the target was beyond the clamped reach.

## Complete example

- [src/KSACars/Ksa/DriverHands.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/DriverHands.cs#L18-L111) - processor, bone lookup, two-bone turn.
- [src/KSACars/Sim/SteeringGrip.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/SteeringGrip.cs#L13-L68) - grips, frame conversion and elbow solve (game-free).
- [tests/KSACars.Tests/SteeringGripTests.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tests/KSACars.Tests/SteeringGripTests.cs#L7-L45) - tests that each car's wheel is within reach.
- Feature page: [Driver hands and steering IK](../../../../mods/ksacars/features/driver-hands-steering-ik.md).

## Variations and gotchas

- **Reach is short.** A seated kitten's arm segments are about 7.4 cm and 7.3 cm in model units; the car profile places the seat so the rim is reachable, and the test enforces it.
- **Model space is centimetres and rotated.** Convert with the seat's axes and the seated offset; do not feed ecliptic metres.
- **Idle actions fight the solve.** KSACars nulls the private `_seatedIdleActionAnims` so the seated stretches do not pull the hands off the rim ([seated crew topic](../animate-and-draw-seated-kitten-crew/overview.md)).
- **The elbow bend is a choice.** `(0.5 * outward, -1, 0)` bends the elbow outward and down; pick the direction that suits your pose.
- **Skin stretching.** Large corrections between shoulder and wrist stretch the skin; aim only inside the clamped reach.
- **A standing kitten uses the same maths** with the model space of [approach 5](pose-kitten-bones-with-an-anim-processor-5.md) instead of a seat's.

## See also

- [Pose a kitten's bones and aim its limbs with an IAnimProcessor](overview.md), [approach 3: one-bone aim](pose-kitten-bones-with-an-anim-processor-3.md), [approach 5: model space and the world](pose-kitten-bones-with-an-anim-processor-5.md)
- [Animate seated kittens, draw them on unflown craft and fly another craft from a chair](../animate-and-draw-seated-kitten-crew/overview.md)
- [Add crew seats and EVA doors](../../../vehicles-and-parts/parts/add-crew-seats-and-eva-doors/overview.md)
