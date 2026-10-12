---
type: How-To
title: Overwrite world transforms in UpdateSkeleton once per view
description: KSACars and KSAStructures write bone world matrices from IAnimProcessor.UpdateSkeleton, which runs after the clip, the processors' local edits and the world rebuild but before the skinning matrices are made, once per viewport, so a view-dependent edit is possible
category: kittens
group: animation
topic: pose-kitten-bones-with-an-anim-processor
approach: 2
difficulty: advanced
prerequisites: [starmap, reflection]
demonstrated_by:
  - mods/ksacars/features/driver-hands-steering-ik
  - mods/ksastructures/features/first-person-body-hiding-and-shadow
techniques: [anim-processor]
hooks:
  - starmap StarMapAfterGui
  - starmap StarMapAfterOnFrame
  - engine-callback KSA.IAnimProcessor.UpdateSkeleton
  - reflection KSA.KittenRenderable._characterAvatar
lifecycle: [frame:draw-ui, frame:after-on-frame, frame:render]
game_types:
  - KSA.IAnimProcessor
  - KSA.AnimatedRenderable
  - KSA.KittenRenderable
  - RenderCore.Animation.Skeleton
  - KSA.KittenEva
tags: [kitten, animation, anim-processor, world-transforms, skinning, per-view]
---
`UpdateLocalPose` edits bone-local transforms once per frame. `UpdateSkeleton` runs later and sees every bone's **world (model-space) transform** after the engine rebuilt them; whatever it writes there goes straight into the skinning matrices. It is called on every `UpdateAnimation` call, that is once per viewport that draws the kitten, so it is also the only place where one view can see a different body from another. This article shows the shape of such a processor from two mods: KSACars' steering-wheel hands (an absolute solve, so being called twice changes nothing) and KSAStructures' first-person processor (which folds every bone to a point, but only for the main view). Prerequisites: StarMap, reflection to the avatar ([reach the avatar](../../characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-1.md)).

## Background: how the game does it

- The order inside `AnimatedRenderable.UpdateAnimation`: sample clip, run `UpdateLocalPose` (first call per frame only), `Skeleton.UpdateLocalTransforms` and `UpdateWorldTransforms(0)`, run `UpdateSkeleton` on each enabled processor, then `skin[i] = InverseBindPose[i] * WorldTransformList[i]` ([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L168-L185)).
- `Skeleton.WorldTransforms` is a `Span<float4x4>` of model-space matrices (row vectors; `Translation` is the bone position in model units). Writing a matrix into the span changes that bone's skin and anything computed from `GetBoneTransform(i) = WorldTransformList[i] * Transform`, such as the helmet, visor, MMU and cosmetic attachments ([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L301-L308)).
- The extension `SetWorldTransform(index, worldMatrix)` derives the bone's local matrix from its parent and recomputes the descendants, which the span write does not ([current/decomp/RenderCore/PackedTransformExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/PackedTransformExtensions.cs#L135-L150)). The game's own eye processor calls it from `UpdateSkeleton` ([current/decomp/KSA/CatEyeAnim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatEyeAnim.cs#L243-L244)).
- `UpdateAnimation` is called once per drawing view; `SkinningPoseIsViewportInvariant` would reuse one skin for all views, but only parachutes set it ([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L140-L144)).

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Add/remove the processor | a StarMap hook outside the render | `frame:draw-ui` or `frame:after-on-frame` |
| `UpdateSkeleton` | inside `KittenRenderable.UpdateRenderData` for each viewport, secondary views before the main one | `frame:render` |

Both mods keep every callback body in a try/catch, because the callback runs inside the render: an unposed kitten is better than a thrown frame. KSACars switches the processor off for good after the first exception and logs it once ([contain exceptions](../../../getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md)). The steer angle the processor reads is state written by the mod's physics prefix earlier in the frame; the processor only reads it.

## Steps

1. **Implement `IAnimProcessor` with a no-op `UpdateLocalPose`.** KSACars' `DriverHands` leaves the local pass empty and does all work in `UpdateSkeleton`. `Priority` 5 keeps it running to 225 m; `CanCacheAnimation` is false:

```csharp
// src/KSACars/Ksa/DriverHands.cs L31-L60
    public float Priority => 5f;

    public bool ShouldUpdate { get; set; } = true;

    public bool CanCacheAnimation => false;

    public void UpdateLocalPose(float4x4 transform, Skeleton skeleton, Span<TransformTRS> localPose, float dt)
    {
    }

    public void UpdateSkeleton(float4x4 transform, Skeleton skeleton, float dt)
    {
        if (_failed) return;
        try
        {
            if ((_bones ??= Resolve(skeleton)) is not { } bones) return;
            (double3 leftPart, double3 rightPart) = SteeringGrip.GripsPart(_drive.Profile, _drive.SteerAngle);
            double right = Hold(skeleton, bones[0], bones[1], bones[2], SteeringGrip.ToKittenModel(_drive.Profile, rightPart), -1.0);
            double left = Hold(skeleton, bones[3], bones[4], bones[5], SteeringGrip.ToKittenModel(_drive.Profile, leftPart), 1.0);
            MissCm = Math.Max(right, left);
            Solves++;
        }
        catch (Exception e)
        {
            _failed = true;
            Log.Warn($"driver's hands: {e.Message}; the kitten keeps its seated pose");
        }
    }
```

2. **Resolve bones by name once,** logging the first missing bone and failing the processor rather than throwing each frame:

```csharp
// src/KSACars/Ksa/DriverHands.cs L62-L77
    private int[]? Resolve(Skeleton skeleton)
    {
        List<string>? names = skeleton.BoneNames;
        int[] found = new int[BoneNames.Length];
        for (int i = 0; i < BoneNames.Length; i++)
        {
            found[i] = names?.IndexOf(BoneNames[i]) ?? -1;
            if (found[i] < 0)
            {
                _failed = true;
                Log.Warn($"driver's hands: the kitten has no bone '{BoneNames[i]}'");
                return null;
            }
        }
        return found;
    }
```

3. **Write transforms absolutely, so a second call changes nothing.** KSACars sets each joint with `SetWorldTransform` (details in [approach 4](pose-kitten-bones-with-an-anim-processor-4.md)). Because the solve reads positions from the skeleton and places joints toward fixed targets, the engine calling the processor once per viewport is harmless.

4. **Or collapse the skeleton for one view only.** KSAStructures' `EyesPose` reads the head bone on every call and, only when the main view of the first-person kitten is being drawn, saves the whole pose and overwrites every world transform with a zero-scale matrix at the head:

```csharp
// src/KSAStructures/Ksa/FirstPerson.cs L160-L187
        // Inside the engine's render: an unposed kitten is better than a thrown frame.
        public void UpdateSkeleton(float4x4 transform, Skeleton skeleton, float dt)
        {
            ViewmodelApplied = false;
            try
            {
                if (_headBone < 0) _headBone = skeleton.BoneNames?.IndexOf("Head_M") ?? -1;
                if (_headBone < 0) return;

                Span<float4x4> world = skeleton.WorldTransforms;
                float3 head = world[_headBone].Translation;
                Head = new double3(head.X, head.Y, head.Z);

                if (!FirstPersonHook.DrawingMainViewOf(kitten)) return;

                // Every bone folded to a point at the head, so nothing of the kitten is in front of
                // its eyes. The whole kitten is kept for the shadow, which the hook puts back.
                BodyWorld = BodyWorld is { } kept && kept.Length == world.Length ? kept : new float4x4[world.Length];
                world.CopyTo(BodyWorld);

                float4x4 gone = new(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, head.X, head.Y, head.Z, 1f);
                for (int i = 0; i < world.Length; i++) world[i] = gone;
                ViewmodelApplied = true;
            }
            catch
            {
            }
        }
```

   The view test is a prefix on `KittenEva.UpdateRenderData` that records whether the viewport being drawn is `Program.MainViewport` ([hide a kitten body from one viewport](../../appearance/hide-a-kitten-body-from-one-viewport/overview.md)). Because the helmet and visor take their matrix from `GetBoneTransform`, they collapse with the head.

5. **Attach and detach outside the render,** when the kitten's renderable changes. A seat builds a new `KittenRenderable` whenever its kitten changes, which discards processors added to the old one, so KSACars re-checks every frame ([seated crew topic](../animate-and-draw-seated-kitten-crew/overview.md)).

6. **Check the result.** KSACars records `Solves` and `MissCm` (wrist to target) for its bridge; in game the driver's wrists stay on the rim as the wheel turns. A `driver's hands: ...` warning appears once if a bone name is missing or the solve throws.

## Complete example

- [src/KSACars/Ksa/DriverHands.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/DriverHands.cs#L18-L150) - processor, two-bone turn and reflection attach.
- [src/KSAStructures/Ksa/FirstPerson.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPerson.cs#L142-L190) - the folding processor.
- Feature pages: [Driver hands and steering IK](../../../../mods/ksacars/features/driver-hands-steering-ik.md), [First-person body hiding and shadow](../../../../mods/ksastructures/features/first-person-body-hiding-and-shadow.md).

## Variations and gotchas

- **Directly writing the span does not move descendants.** Folding every bone is safe; posing one bone needs `SetWorldTransform` or an explicit rebuild of its children.
- **A world edit survives only this view.** The next view's `UpdateAnimation` rebuilds the world transforms from the stored local pose, then calls your processor again, so view-specific edits must be reapplied each call and must not accumulate.
- **Edits affect shadows.** `AnimatedRenderable.Draw` puts the skin it was drawn with into the shadow pool; a collapsed body casts a collapsed shadow unless you fix the pool ([hide a kitten body](../../appearance/hide-a-kitten-body-from-one-viewport/overview.md)).
- **Throwing is expensive.** An exception from the callback ends the frame; catch inside, switch off once, log once.
- **Reach and animation cull.** The processor stops running beyond 45 m times `Priority` from the drawing camera.

## See also

- [Pose a kitten's bones and aim its limbs with an IAnimProcessor](overview.md), [approach 1](pose-kitten-bones-with-an-anim-processor-1.md), [approach 4: two-bone IK](pose-kitten-bones-with-an-anim-processor-4.md)
- [Reach a kitten's avatar, skeleton, bones and animation catalog](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md) ([draw path](../../characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-4.md))
