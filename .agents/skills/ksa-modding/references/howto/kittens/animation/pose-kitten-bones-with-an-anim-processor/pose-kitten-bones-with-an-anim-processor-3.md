---
type: How-To
title: Aim an arm at a point with a one-bone shoulder turn
description: KSAGolf's GolfPose turns each shoulder by the rotation that carries its shoulder-to-wrist vector onto shoulder-to-target, working in model space on the processed local pose, with no elbow solve
category: kittens
group: animation
topic: pose-kitten-bones-with-an-anim-processor
approach: 3
difficulty: intermediate
prerequisites: [starmap, reflection]
demonstrated_by: [mods/ksagolf/features/putting-address-pose, mods/ksagolf/features/kitten-pose-processor]
techniques: [anim-processor]
hooks: [engine-callback KSA.IAnimProcessor.UpdateLocalPose]
lifecycle: [frame:render]
game_types:
  - KSA.IAnimProcessor
  - KSA.AnimatedRenderable
  - RenderCore.Animation.Skeleton
  - RenderCore.TransformTRS
  - RenderCore.TransformNodeInfo
  - KSA.KittenEva
tags: [kitten, animation, arm, aim, shoulder, model-space]
---
To make a kitten's paws reach a grip, a ball or a button you need to aim the arm. KSAGolf does it with the smallest tool that works: for each arm it computes the rotation that carries the current shoulder-to-wrist vector onto the shoulder-to-target vector and applies it to the **shoulder** bone alone. There is no elbow solve (the repository's own notes record two-bone IK as possible but not implemented), so the elbow keeps whatever bend the clip gave it and the paw lands on the line to the target rather than exactly on it. That is good enough when the target is near the arm's natural reach, and it needs only the local pose, so it runs once per frame in `UpdateLocalPose`. For a wrist that must land on the target, use the [two-bone solve](pose-kitten-bones-with-an-anim-processor-4.md). Prerequisites: [the pose processor basics](pose-kitten-bones-with-an-anim-processor-1.md).

## Background: how the game does it

- In `UpdateLocalPose` the bone-local TRS span is writable but the world transforms are not yet rebuilt for this frame; an aim needs world positions, so the mod rebuilds model-space matrices itself from the local pose, parents first, and recomputes after each edit.
- `Skeleton.NodeInfo` gives `TransformNodeInfo.ParentIndex` (-1 for roots) and the child range; the engine's own world build is `local * parentWorld` in row-vector order ([current/decomp/RenderCore.Animation/Skeleton.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Animation/Skeleton.cs#L29-L31), [current/decomp/RenderCore/TransformNodeInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/TransformNodeInfo.cs)).
- A model-space turn is applied to a bone by composing the bone's current model-space rotation with the turn and then removing the parent's model-space rotation again, which gives the new local rotation; the mod writes that into `localPose[bone].Rotation` ([current/decomp/RenderCore/TransformTRS.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/TransformTRS.cs)).

## Lifecycle and timing

`UpdateLocalPose` runs once per frame for the first view to draw the kitten (`frame:render`), on the main thread, after the clip was copied into the span and before the skeleton is rebuilt. The posture it reads (where the target is) is written by a main-thread step in the UI pass, so all views in a frame see the same posture and nothing needs locking ([game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)).

## Steps

1. **Resolve the bones once by name** (`Spine1_M`, `Shoulder_R`, `Wrist_R`, `Shoulder_L`, `Wrist_L`, `Head_M`, finger joints) and give up quietly if any is missing:

```csharp
// src/KSAGolf/Ksa/GolfPose.cs L189-L199
    private bool Resolve(Skeleton skeleton)
    {
        if (_bones.Length == Names.Length) return true;
        if (skeleton.BoneNames is not { } names) return false;

        int[] found = Names.Select(n => names.IndexOf(n)).ToArray();
        if (found.Any(i => i < 0)) return false;

        _bones = found;
        return true;
    }
```

2. **Build model-space world transforms from the processed local pose,** parents first (the engine's world rebuild comes later, so you have to do your own):

```csharp
// src/KSAGolf/Ksa/GolfPose.cs L201-L226
    // The model-space transform of every bone, from the pose as it stands now.
    private void World(Skeleton skeleton, Span<TransformTRS> localPose)
    {
        int count = Math.Min(skeleton.BoneCount, localPose.Length);
        if (_world.Length != count) _world = new float4x4[count];

        Span<TransformNodeInfo> nodes = skeleton.NodeInfo;
        bool[] done = new bool[count];
        for (int i = 0; i < count; i++) Build(i, nodes, localPose, done);
    }

    private void Build(int i, Span<TransformNodeInfo> nodes, Span<TransformTRS> localPose, bool[] done)
    {
        if (done[i]) return;

        int parent = nodes[i].ParentIndex;
        float4x4 local = localPose[i].CreateMatrix();
        if (parent >= 0 && parent < done.Length)
        {
            Build(parent, nodes, localPose, done);
            local *= _world[parent];
        }

        _world[i] = local;
        done[i] = true;
    }
```

3. **Turn one bone about its own position by a model-space rotation,** convert it into the parent's frame, write the local rotation and rebuild everything below:

```csharp
// src/KSAGolf/Ksa/GolfPose.cs L228-L237
    // Turns one bone about its own position by a model-space rotation, and rebuilds everything below it.
    private void TurnAbout(Skeleton skeleton, Span<TransformTRS> localPose, int bone, floatQuat turn)
    {
        int parent = skeleton.NodeInfo[bone].ParentIndex;
        floatQuat above = parent >= 0 ? Rotation(_world[parent]) : floatQuat.Identity;
        floatQuat wanted = floatQuat.Concatenate(floatQuat.Concatenate(localPose[bone].Rotation, above), turn);

        localPose[bone].Rotation = floatQuat.Normalize(floatQuat.Concatenate(wanted, floatQuat.Inverse(above)));
        World(skeleton, localPose);
    }
```

4. **Aim the arm.** The turn that carries the shoulder-to-wrist vector onto the shoulder-to-target vector is applied to the shoulder:

```csharp
// src/KSAGolf/Ksa/GolfPose.cs L239-L252
    // Turns an arm at the shoulder so the wrist points at a model-space target.
    private void Reach(Skeleton skeleton, Span<TransformTRS> localPose, int shoulder, int wrist, double3 target)
    {
        double3 from = Position(_world[shoulder]);
        double3 hand = Position(_world[wrist]) - from;
        double3 want = target - from;
        if (Vec.Len2(hand) < 1e-6 || Vec.Len2(want) < 1e-6) return;

        TurnAbout(skeleton, localPose, shoulder, floatQuat.Pack(Vec.RotationFromTo(hand, want)));
    }

    private static floatQuat Rotation(float4x4 m) => TransformTRS.FromMatrix(m).Rotation;

    private static double3 Position(float4x4 m) => new(m.Translation.X, m.Translation.Y, m.Translation.Z);
```

   `Vec.RotationFromTo` is the mod's own shortest-arc quaternion helper (not a game type).

5. **Call it from `UpdateLocalPose`** after laying the stance on the roots and leaning the spine, with the right paw at the grip and the left 4 cm above it:

```csharp
// src/KSAGolf/Ksa/GolfPose.cs L53-L81
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
        catch
        {
            // Inside the engine's render: an unposed kitten is better than a thrown frame.
        }
    }
```

6. **Check the result.** In game the paws turn toward the grip as the club swings; they will not close exactly on it unless the elbow already points the right way.

## Complete example

- [src/KSAGolf/Ksa/GolfPose.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/GolfPose.cs#L23-L253) - the processor with stance, lean and reach.
- [src/KSAGolf/Sim/PutterRig.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/PutterRig.cs#L1-L105) - the pure-maths rig that supplies the grip position.
- Feature pages: [Putting address pose](../../../../mods/ksagolf/features/putting-address-pose.md), [Posing kitten bones](../../../../mods/ksagolf/features/kitten-pose-processor.md).

## Variations and gotchas

- **Only the shoulder turns.** The elbow keeps the clip's bend, so the length of the shoulder-to-wrist line does not change; the paw lands on the line to the target, not necessarily on the target. If the target is nearer or farther than the arm reaches, use a two-bone solve.
- **The idle shows through.** The pose is laid over the live clip, so breathing sway reaches the arms. Capturing the idle and holding it is recorded in KITTEN-ARMS.md as a fix but is not implemented.
- **Recompute after every edit.** `TurnAbout` rebuilds the world matrices because a later edit reads positions from them.
- **Not for seated crew.** This runs in the local pass; for a view-final world solve (IK on a seat) see [approach 2](pose-kitten-bones-with-an-anim-processor-2.md).

## See also

- [Pose a kitten's bones and aim its limbs with an IAnimProcessor](overview.md), [approach 4: two-bone IK](pose-kitten-bones-with-an-anim-processor-4.md), [approach 6: stance and lean](pose-kitten-bones-with-an-anim-processor-6.md)
- [Attach a prop to a kitten's hand or another bone](../../appearance/attach-a-prop-to-a-kitten-bone/overview.md)
