---
type: How-To
title: Draw a kitten stepped, turned and leaning with a stance matrix on the root bones, without moving its body
description: KSAGolf multiplies a yaw-and-offset stance onto every root bone of the processed pose, leans the spine, eases the stance in on player time, refuses a step beyond 90 cm and gives the stance up when the real body drifts 15 cm, because teleporting a landed kitten makes the engine throw it about
category: kittens
group: animation
topic: pose-kitten-bones-with-an-anim-processor
approach: 6
difficulty: advanced
prerequisites: [starmap, reflection]
demonstrated_by: [mods/ksagolf/features/putting-address-pose]
techniques: [anim-processor]
hooks: [starmap StarMapAfterGui, engine-callback KSA.IAnimProcessor.UpdateLocalPose]
lifecycle: [frame:draw-ui, frame:render]
game_types:
  - KSA.IAnimProcessor
  - KSA.AnimatedRenderable
  - RenderCore.Animation.Skeleton
  - RenderCore.TransformTRS
  - RenderCore.TransformNodeInfo
  - KSA.KittenEva
  - KSA.Vehicle
tags: [kitten, animation, stance, root-bones, lean, displaced-draw]
---
When the player presses G near the ball, KSAGolf's flown kitten steps up to it, turns side-on to the target and bends at the waist. None of that moves the kitten: its physics body stays exactly where the player walked it, and the whole skeleton is drawn displaced and turned from inside a pose processor. This article shows the stance matrix on the roots, the spine lean, the easing on player time, the safeguards, and why the obvious alternative (teleporting the kitten) does not work. Prerequisites: [the local-pose processor](pose-kitten-bones-with-an-anim-processor-1.md) and [model-space conversion](pose-kitten-bones-with-an-anim-processor-5.md).

## Background: how the game does it

- A landed kitten is simulated as a character on the ground by the vehicle worker. `Vehicle.Teleport(orbit, body2Cce, bodyRates)` sets `Body2Cce` and then calls `GetPhysicsStatesMutable().OverwriteFromLeaderAnalytic(Orbit, StateVectors, Body2Cce, BodyRates)`, which overwrites the physics state from the vehicle's **analytic orbit**, and for a kitten standing on the ground that orbit is not where it stands (KITTEN-ARMS.md records it thrown 1.7 m and tilted); `TeleportToLocation` resets the facing ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2241-L2295)).
- The sampled pose is a root-relative hierarchy: bones with `TransformNodeInfo.ParentIndex < 0` are roots; multiplying a transform onto every root moves and turns the whole skeleton ([current/decomp/RenderCore/TransformNodeInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/TransformNodeInfo.cs)).
- The kitten is drawn at its origin less the centre of mass, from the body; a displaced skeleton is drawn where the root transforms say, and the collider stays at the body.
- The stance is applied in `UpdateLocalPose`, so every viewport draws the same posture.

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| G pressed near the ball: compute the stance from the ball and target in model space | `Golf.Keys` from `[StarMapAfterGui]` | `frame:draw-ui` |
| The blend runs 0 to 1 over 0.35 s on **player** time, and the swing advances | `Golf.Step` | `frame:draw-ui` |
| Stance on the roots, lean and reach | `GolfPose.UpdateLocalPose`, first view to draw the kitten | `frame:render` |

`Golf.Step` writes the posture on the main thread before the render and the processor only reads it during the render, so nothing needs locking and every view draws the same posture. The blend uses player time (`dtPlayer`) so the step to the ball completes when the game is paused; a processor that used simulated `dt` would freeze mid-step ([game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)).

## Steps

1. **Compute a stance, or refuse.** The stance is a yaw about +Y and an offset in model space that lands the rig's ball spot on the ball with the rig's +X pointing at the target. If the offset exceeds `StepReachCm` (90 cm) it refuses and the player is told to walk closer:

```csharp
// src/KSAGolf/Sim/Stance.cs L14-L43
internal readonly record struct Stance(double Yaw, double3 Offset)
{
    /// <summary>
    /// The stance addressing a ball at <paramref name="ball"/> towards <paramref name="target"/>, both
    /// in model space, or false when the ball is further than <paramref name="reach"/> from where the
    /// kitten would have to stand.
    /// </summary>
    public static bool TryFor(double3 ball, double3 target, PutterRig rig, double reach, out Stance stance)
    {
        stance = default;
        double3 flat = new(target.X, 0.0, target.Z);
        if (Vec.Len2(flat) < 1e-12) return false;

        flat = Vec.Unit(flat);
        double yaw = Math.Atan2(-flat.Z, flat.X);
        var turned = new Stance(yaw, Vec.Zero);
        double3 offset = ball - turned.Place(rig.Ball, 1.0);

        if (Math.Sqrt((offset.X * offset.X) + (offset.Z * offset.Z)) > reach) return false;

        stance = new Stance(yaw, offset);
        return true;
    }

    /// <summary>The turn, part of the way in.</summary>
    public doubleQuat Rotation(double blend) => doubleQuat.CreateFromAxisAngle(new double3(0, 1, 0), Yaw * blend);

    /// <summary>A rig point in model space, part of the way in.</summary>
    public double3 Place(double3 rig, double blend) => double3.Transform(rig, Rotation(blend)) + (Offset * blend);
}
```

2. **Ease the stance in on player time:**

```csharp
// src/KSAGolf/Ksa/Golf.cs L141-L142
        double towards = _phase == Phase.Addressing ? 1.0 : 0.0;
        _blend = Math.Clamp(_blend + (Math.Sign(towards - _blend) * dtPlayer / StepSeconds), 0.0, 1.0);
```

3. **Lay the stance on every root bone.** Roots are the bones with no parent; multiplying the stance onto their local matrices turns and moves the entire skeleton, at partial blend during the step:

```csharp
// src/KSAGolf/Ksa/GolfPose.cs L53-L67
    public void UpdateLocalPose(float4x4 transform, Skeleton skeleton, Span<TransformTRS> localPose, float dt)
    {
        try
        {
            Posture now = posture();
            if (!Resolve(skeleton) || !(now.Blend > 0.0)) return;

            float4x4 stance = StanceMatrix(now);
            Span<TransformNodeInfo> nodes = skeleton.NodeInfo;
            for (int i = 0; i < Math.Min(nodes.Length, localPose.Length); i++)
            {
                if (nodes[i].ParentIndex < 0) localPose[i] = TransformTRS.FromMatrix(localPose[i].CreateMatrix() * stance);
            }

            if (now.Theta is not { } theta) return;
```

```csharp
// src/KSAGolf/Ksa/GolfPose.cs L185-L187
    private static float4x4 StanceMatrix(Posture now)
        => float4x4.CreateFromQuaternion(floatQuat.Pack(now.Stance.Rotation(now.Blend)))
           * float4x4.CreateTranslation(float3.Pack(now.Stance.Offset * now.Blend));
```

4. **Lean the spine, then reach.** After the stance, build model-space transforms from the edited pose, turn `Spine1_M` by 24 degrees times the blend about the stance-turned +X axis (so the lean is across the target line), and aim the arms ([one-bone aim](pose-kitten-bones-with-an-anim-processor-3.md)):

```csharp
// src/KSAGolf/Ksa/GolfPose.cs L69-L75
            World(skeleton, localPose);
            double3 across = double3.Transform(new double3(1, 0, 0), now.Stance.Rotation(now.Blend));
            TurnAbout(skeleton, localPose, _bones[0], floatQuat.CreateFromAxisAngle(float3.Pack(across), float.DegreesToRadians(LeanDeg) * (float)now.Blend));

            double3 grip = now.Stance.Place(rig.Grip(theta), now.Blend);
            Reach(skeleton, localPose, _bones[1], _bones[2], grip);
            Reach(skeleton, localPose, _bones[3], _bones[4], grip + LeftHandAbove);
```

5. **Give the stance up when the real body leaves it.** The drawn kitten stands where the stance puts it but the body is still where the player walked it; if the body drifts 15 cm or the ball moves, the mod leaves the stance (otherwise the drawn pose leaves its collider behind):

```csharp
// src/KSAGolf/Ksa/Golf.cs L628-L633
        // Walked or knocked off: the kitten drawn at the ball has left its body behind.
        if (Vec.Len(ToCcf(_ballBody, KsaWorld.PositionEcl(kitten)) - _standingCcf) > DriftM || !ball.Resting)
        {
            Leave("You moved away from the ball. Press G to step up again.");
            return;
        }
```

6. **Check the result.** The status line reports `addressing the ball: stepping N cm, turning M deg` in the log; walking more than 15 cm away prints `You moved away from the ball`.

## Complete example

- [src/KSAGolf/Ksa/GolfPose.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/GolfPose.cs#L23-L112) - the processor.
- [src/KSAGolf/Sim/Stance.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/Stance.cs#L14-L43) - the stance maths (headless tests in `StanceTests`).
- [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L121-L198) - the per-frame step, blend and posture.
- Feature page: [Putting address pose drawn on the kitten skeleton](../../../../mods/ksagolf/features/putting-address-pose.md).

## Variations and gotchas

- **Drawn, not moved.** Anything that moves the real body (a push, walking) leaves the drawn pose behind its collider. The mod's drift guard is the mitigation.
- **Root bones only.** If a future rig adds a second root that is not part of the body, it will be carried too.
- **The idle shows through.** The pose is laid over the live clip, so breathing sway reaches the arms.
- **Why not `Teleport`?** `Vehicle.Teleport(null, attitude, null)` overwrites the physics state from the analytic orbit; for a kitten on the ground that is not where it stands. `TeleportToLocation` resets the facing.
- **Head position lags a frame.** The first-person eye is built from the head recorded by the previous render's `UpdateSkeleton`, because the camera is positioned before this frame's render.
- **A kitten standing still is asleep on the terrain.** KSA's servo does not turn it, so a turn to face a look direction must also be drawn until it walks ([first-person topic](../../../camera/special-views/first-person-camera-for-a-kitten/overview.md)).

## See also

- [Pose a kitten's bones and aim its limbs with an IAnimProcessor](overview.md), [approach 3: shoulder aim](pose-kitten-bones-with-an-anim-processor-3.md), [approach 5: model space and the world](pose-kitten-bones-with-an-anim-processor-5.md)
- [Steer a walking kitten](../../eva/steer-a-walking-kitten/overview.md), [Kitten locomotion, modes and the servo](../../eva/kitten-locomotion-and-servo-reference/overview.md)
- [Compute mission elapsed time and detect liftoff](../../../flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/overview.md)
