---
type: Mod Feature
title: Putting address pose drawn on the kitten skeleton
description: Draw a kitten stepped up to the ball, bent at the waist with both arms aimed at the grip, by carrying the skeleton's root bones in a pose processor while the physics body stays where the player walked it, and place the club against each view's own wrist
mod: ksagolf
category: kittens
techniques: [anim-processor, procedural-animation, runtime-attachment, reflection, headless-tests]
hooks:
  - starmap StarMapAfterGui
  - engine-callback KSA.IAnimProcessor.UpdateLocalPose
  - engine-callback KSA.IAnimProcessor.UpdateSkeleton
  - reflection KSA.KittenRenderable._characterAvatar
lifecycle: [frame:draw-ui, frame:render]
game_types:
  - KSA.KittenEva
  - KSA.KittenRenderable
  - KSA.CharacterAvatar
  - KSA.AnimatedRenderable
  - KSA.IAnimProcessor
  - KSA.StaticMeshRenderable
  - KSA.Vehicle
  - RenderCore.Animation.Skeleton
  - RenderCore.TransformTRS
  - RenderCore.TransformNodeInfo
tags: [kitten, pose, skeleton, stance, putter, golf, attachment, model-space, anim-processor]
---

When the player presses G near the ball, the flown kitten appears to step up to it, turn side-on to
the target, bend 24° at the waist and close both paws on the putter's grip. The club then swings
with the mouse. None of that moves the kitten. KSAGolf draws the whole skeleton displaced and
turned from inside a pose processor, `GolfPose`. The physics body stays exactly where the player
walked it, because teleporting a landed kitten makes the engine rebuild it from an orbit and throws
it about.[^golf-kittenarms][^ksa-vehicle]

The same processor carries the putter while walking: its grip runs through the closed right paw,
along the knuckles. Each frame it re-solves the putter's attachment transform against the wrist of
the view being drawn, so the club sits on the drawn hand with no frame of lag.

## How it works

**Model space.** Everything is aimed in the skeleton's own space, which `KittenFrame` documents:
centimetres, +Y up, +Z the way the kitten faces, +X its left.[^golf-kittenframe] `KittenFrame`
rebuilds the engine's model-to-world chain itself. That chain is avatar `Scale` (0.01), a quarter
turn about X then about Z (`KittenRenderable.ModelToBodyMatrix`), less the centre of mass (which
`KittenEva.UpdateRenderData` subtracts), then `Body2Cce` and the vehicle's position. Rebuilding it
means any model-space point converts to the ecliptic or to a given camera's ego frame without
reading a drawn matrix that belongs to whichever camera drew last.[^ksa-kittenrenderable][^ksa-kitteneva]

**The rig (`Sim/PutterRig.cs`).** This is pure maths, so it has headless tests.

- It holds the putter's measured geometry: grip at the file origin, shaft down −Z, 18° lie, head
  11 × 3.2 cm, faces 1.8 cm either side of the shaft plane.
- The head at address sits on the ground 22 cm ahead of the kitten.
- The ball sits just past the face.
- The swing is a pendulum about a chest pivot `(0, 44, 8)`, turning about +Z. Positive `theta` moves
  the head towards the target.
- `Grip(theta)`, `LeadingFaceX(theta)` and `SoleY(theta)` feed both the pose and the strike test in
  [Putting session](../features/putting-session.md).[^golf-putterrig][^golf-test-putterrig]

**The stance (`Sim/Stance.cs`).** `Stance.TryFor(ball, target, rig, reach)` takes the ball and the
target direction in model space. It returns a yaw about +Y that turns the rig's +X onto the
levelled target, and an offset that lands the rig's ball spot on the ball. If that offset is beyond
`StepReachCm` (90 cm) it refuses, and the player is told to walk closer. `Rotation(blend)` and
`Place(p, blend)` give the stance partway in, which is how the 0.35 s step is
animated.[^golf-stance][^golf-test-stance]

**`GolfPose.UpdateLocalPose`, once per frame.**

1. Ask `Golf` for the current `Posture` (stance, blend, swing angle or null, first person).
2. Multiply every root bone's local transform (`TransformNodeInfo.ParentIndex < 0`) by the stance
   matrix, so the whole skeleton is drawn turned and moved.
3. At address, build model-space world transforms from the edited pose. Then turn `Spine1_M` by
   24° × blend about the stance-turned +X axis, and rebuild everything below it.
4. `Reach` turns each shoulder by the rotation that carries its shoulder-to-wrist vector onto
   shoulder-to-target. The right paw aims at `Grip(theta)` and the left at 4 cm above it. This is a
   one-bone aim at the shoulder. There is no elbow solve.[^golf-golfpose]

**`GolfPose.UpdateSkeleton`, once per view.**

1. Record the head bone's model-space position. The first-person eye is built from it.
2. Choose where the club goes in model space. At address that is `ClubInModel`: the rig pose for
   `theta`, then the stance. While carrying it is `InTheFist`: the grip at the centroid of the
   index, pinky and middle knuckles, with the shaft along index-to-pinky and the face turned to +Z.
3. Solve the attachment transform so that the engine's own composition lands it there:
   `attachment = placed * inverse(SocketTurn * wristWorld)`. Here `SocketTurn` is the engine's
   `RotZ(-90°) * RotX(-90°)` and `wristWorld` is the wrist bone this view just
   built.[^ksa-kittenrenderable][^ksa-animatedrenderable]
4. Write the result back into the putter's `CosmeticAttachment` (read, modify, write back, since it
   is a struct in a list).
5. In first person, on the main view only, collapse the body for the eyes (see
   [First-person kitten view](../features/first-person-kitten-view.md)).

**`Sim/Orient.cs`** provides `CarryAxes`: the rotation that carries one facing-and-up pair onto
another, with the facing exact and the up as near as the facing allows. At this ref only
`OrientTests` calls it. `Bridge.cs` has its own private copy (`Turning`), which `orbit_place` uses to
face a kitten it teleports into orbit.[^golf-orient][^golf-bridge]

## Lifecycle and timing

| Step | Where it runs | Lifecycle |
|---|---|---|
| `Golf.Hold` adds `GolfPose` to `AnimProcessors` and the putter to `CosmeticAttachments` | `Golf.Step`, from `KSAGolfMod.StepOnce` | `frame:draw-ui` (or the hidden-HUD fallbacks) |
| G pressed: `Golf.TryAddress` computes the `Stance` from `KittenFrame.FromEcl` | `Golf.Keys`, from `[StarMapAfterGui]` | `frame:draw-ui` |
| The blend runs 0 to 1 over 0.35 s on player time; the swing angle advances | `Golf.Step` | `frame:draw-ui` |
| `UpdateLocalPose`: stance on the roots, lean, reach | `KittenRenderable.UpdateRenderData` → `AnimatedRenderable.UpdateAnimation`, first view to draw the kitten | `frame:render` |
| `UpdateSkeleton`: head, club attachment, first-person strip | Same call, once per view, before the attachment loop | `frame:render` |

`Golf.Step` writes the posture state on the main thread before the render, and the processor reads
it during the render. So every view in a frame draws the same posture and nothing needs locking.
The club is placed from `UpdateSkeleton` because that is the one point where a view's skeleton is
final and the attachment loop has not run yet. Writing the attachment from a StarMap hook would
use the previous frame's wrist.[^ksa-kittenrenderable] Both callbacks run inside the engine's
render loop, so each body is wrapped in `try`/`catch`: an unposed kitten is better than a frame
that throws.

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Ksa/GolfPose.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/GolfPose.cs#L53-L112) | The two processor callbacks |
| [src/KSAGolf/Ksa/GolfPose.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/GolfPose.cs#L155-L248) | `InTheFist`, `ClubInModel`, `StanceMatrix`, `TurnAbout`, `Reach` |
| [src/KSAGolf/Ksa/KittenFrame.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KittenFrame.cs) | Model space to and from the ecliptic and a camera's ego frame |
| [src/KSAGolf/Sim/Stance.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/Stance.cs) | Yaw and offset to address a ball, partway in by blend |
| [src/KSAGolf/Sim/PutterRig.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/PutterRig.cs) | Putter geometry, pendulum swing, grip and face positions |
| [src/KSAGolf/Sim/Orient.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/Orient.cs) | Facing-and-up alignment helper (tested; unused in game code at this ref) |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L542-L606) | `TryAddress`: target to the kitten's left, ball and target into model space, `Stance.TryFor` |
| [tests/KSAGolf.Tests/PutterRigTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/PutterRigTests.cs) | Sole flat on the ground, face square to the target, the club swings as one |
| [tests/KSAGolf.Tests/StanceTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/StanceTests.cs) | Ball spot lands on the ball, target levelled, half-way in is half the move, out of reach refused |
| [tests/KSAGolf.Tests/OrientTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/OrientTests.cs) | Both axes arrive; an up not square to the facing is met as nearly as it can be |

## Snippets

```csharp
// src/KSAGolf/Ksa/GolfPose.cs L53-L76
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

            World(skeleton, localPose);
            double3 across = double3.Transform(new double3(1, 0, 0), now.Stance.Rotation(now.Blend));
            TurnAbout(skeleton, localPose, _bones[0], floatQuat.CreateFromAxisAngle(float3.Pack(across), float.DegreesToRadians(LeanDeg) * (float)now.Blend));

            double3 grip = now.Stance.Place(rig.Grip(theta), now.Blend);
            Reach(skeleton, localPose, _bones[1], _bones[2], grip);
            Reach(skeleton, localPose, _bones[3], _bones[4], grip + LeftHandAbove);
        }
```

```csharp
// src/KSAGolf/Ksa/GolfPose.cs L93-L106
            Posture now = posture();
            float4x4 placed = now.Theta is { } theta && now.Blend > 0.0 ? ClubInModel(rig, now, theta) : InTheFist(skeleton.WorldTransforms);
            if (!float4x4.Invert(SocketTurn * skeleton.WorldTransforms[_bones[2]], out float4x4 fromSocket)) return;
            float4x4 attachment = placed * fromSocket;

            List<CharacterAvatar.CosmeticAttachment> list = avatar.Attachments.CosmeticAttachments;
            int i = list.FindIndex(a => ReferenceEquals(a.Mesh, club));
            if (i < 0) return;

            CharacterAvatar.CosmeticAttachment held = list[i];
            held.Transform = attachment;
            list[i] = held;

            if (now.FirstPerson && FirstPersonHook.DrawingMainViewOf(kitten)) StripForTheEyes(skeleton, keepPaw: now.Theta is null);
```

```csharp
// src/KSAGolf/Sim/Stance.cs L21-L36
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
```

```csharp
// src/KSAGolf/Ksa/KittenFrame.cs L44-L52
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

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `IAnimProcessor.UpdateLocalPose` / `UpdateSkeleton` | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L134-L207) | Local pose once per frame, then per-view skeleton callback |
| `AnimatedRenderable.GetBoneTransform` | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L301-L308) | `WorldTransformList[i] * Transform`, the bone matrix the attachment loop composes with |
| `KittenRenderable.ModelToBodyMatrix` / `UpdateRenderData` | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L107-L110) | The scale and quarter turns `KittenFrame` replicates; the attachment loop at L399-L404 |
| `KittenEva.UpdateRenderData` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L1062-L1078) | Draws the model at the vehicle origin less the turned centre of mass |
| `Vehicle.Body2Cce` / `CenterOfMassAsmbF` / `GetPositionEcl` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L476) | Inputs to the model-to-world chain |
| `Vehicle.Teleport` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2241-L2292) | Why the stance is drawn: it calls `OverwriteFromLeaderAnalytic` from the orbit |
| `Skeleton.NodeInfo` / `WorldTransforms` / `BoneNames` / `BoneCount` | [current/decomp/RenderCore.Animation/Skeleton.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Animation/Skeleton.cs) | Parent indices and per-view world transforms |
| `TransformNodeInfo.ParentIndex` | [current/decomp/RenderCore/TransformNodeInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/TransformNodeInfo.cs) | Finds the root bones the stance is applied to |
| `TransformTRS.FromMatrix` / `CreateMatrix` | [current/decomp/RenderCore/TransformTRS.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/TransformTRS.cs) | Matrix to TRS and back for the root bones |
| `CharacterAvatar.CosmeticAttachment.Transform` / `Core.Scale` | [current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L30-L101) | The putter's per-view transform; the 0.01 model scale |

## Pitfalls and version notes

- **Drawn, not moved.** The physics body stays where the player walked it. `Golf` gives up the
  stance if the real body drifts more than 15 cm from where it stood at address. If something else
  moves the kitten (a push, walking), the drawn pose leaves its collider behind.
- **Root bones only.** The stance is applied to every bone with no parent. If a future rig adds a
  second root that is not part of the body, it will be carried too.
- **The idle shows through.** The pose is laid over KSA's live clip, so breathing sway reaches the
  arms. KITTEN-ARMS.md records capturing the idle and holding it as the fix; that is not
  implemented here.[^golf-kittenarms]
- **One-bone reach.** `Reach` turns only the shoulder, so it points the shoulder-to-wrist line at
  the grip. The elbow keeps whatever bend the clip gave it, so the length of that line does not
  change. The paw lands on the line to the grip, not necessarily on the grip, and the club is
  placed from the rig rather than from the paw.
- **`Head` is a frame stale for the eye.** The first-person eye position comes from the last
  `UpdateSkeleton`, which ran in the previous frame's render. The camera is positioned before this
  frame's render.
- **Version notes.** Built against 2026.9.22.5482. `KittenRenderable`, `KittenEva`,
  `CharacterAvatar` and `Skeleton` are unchanged in 2026.10.10.5554, and the attachment composition
  (`RotZ(-90°) * RotX(-90°) * bone`) is the same.

## See also

- [Posing kitten bones through the pose-processor list](../features/kitten-pose-processor.md)
- [Mod meshes held by a kitten as cosmetic attachments](../features/kitten-held-props.md)
- [Putting session with a mouse-swung club](../features/putting-session.md)
- [First-person kitten view that keeps the shadow](../features/first-person-kitten-view.md)
- [Headless simulation tests behind a Sim/Ksa boundary](../features/headless-sim-tests.md)
- [Aim an arm at a point with a one-bone shoulder turn](../../../howto/kittens/animation/pose-kitten-bones-with-an-anim-processor/pose-kitten-bones-with-an-anim-processor-3.md) - how-to article demonstrating this feature
- [Convert a standing kitten's model space to the ecliptic or a camera's ego frame](../../../howto/kittens/animation/pose-kitten-bones-with-an-anim-processor/pose-kitten-bones-with-an-anim-processor-5.md) - how-to article demonstrating this feature
- [Draw a kitten stepped, turned and leaning with a stance matrix on the root bones, without moving its body](../../../howto/kittens/animation/pose-kitten-bones-with-an-anim-processor/pose-kitten-bones-with-an-anim-processor-6.md) - how-to article demonstrating this feature
- [Solve the attachment transform in UpdateSkeleton against the view's wrist](../../../howto/kittens/appearance/attach-a-prop-to-a-kitten-bone/attach-a-prop-to-a-kitten-bone-2.md) - how-to article demonstrating this feature

[^golf-golfpose]: src/KSAGolf/Ksa/GolfPose.cs
[^golf-kittenframe]: src/KSAGolf/Ksa/KittenFrame.cs
[^golf-stance]: src/KSAGolf/Sim/Stance.cs
[^golf-putterrig]: src/KSAGolf/Sim/PutterRig.cs
[^golf-orient]: src/KSAGolf/Sim/Orient.cs
[^golf-bridge]: src/KSAGolf/Ksa/Bridge.cs
[^golf-kittenarms]: docs/KITTEN-ARMS.md
[^golf-test-putterrig]: tests/KSAGolf.Tests/PutterRigTests.cs
[^golf-test-stance]: tests/KSAGolf.Tests/StanceTests.cs
[^ksa-kittenrenderable]: KSA.KittenRenderable (decompiled, build 2026.10.10.5554)
[^ksa-kitteneva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
[^ksa-animatedrenderable]: KSA.AnimatedRenderable (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
