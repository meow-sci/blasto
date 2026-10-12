---
type: How-To
title: Solve the attachment transform in UpdateSkeleton against the view's wrist
description: Write placed times the inverse of SocketTurn times the wrist's world transform into the CosmeticAttachment from an IAnimProcessor's UpdateSkeleton so the prop has no frame of lag in any view
category: kittens
group: appearance
topic: attach-a-prop-to-a-kitten-bone
approach: 2
difficulty: advanced
prerequisites: [starmap, attach-a-prop-to-a-kitten-bone-1]
demonstrated_by: [mods/ksagolf/features/putting-address-pose]
techniques: [anim-processor]
hooks: [starmap StarMapAfterGui, anim-processor KSA.IAnimProcessor.UpdateSkeleton]
lifecycle: [frame:draw-ui, frame:render]
game_types:
  - KSA.CharacterAvatar
  - KSA.KittenRenderable
  - KSA.AnimatedRenderable
  - KSA.IAnimProcessor
  - RenderCore.Animation.Skeleton
  - KSA.StaticMeshRenderable
tags: [kitten, prop, cosmetic-attachment, anim-processor, ksagolf]
---
A static attachment `Transform` ([approach 1](attach-a-prop-to-a-kitten-bone-1.md)) is one frame stale against a moving bone and is the same in every view. When a prop must sit exactly on the drawn hand, solve the attachment transform **per view**, from inside an `IAnimProcessor.UpdateSkeleton`, against the wrist the engine has just built for that view: `attachment = placed * inverse(SocketTurn * wristWorld)`. The engine calls `UpdateSkeleton` after the view's skeleton is built and before the attachment loop reads the transform, so the prop has no frame of lag in any view. Prerequisites: StarMap, the prop from approach 1, and a pose processor ([pose kitten bones with an anim processor](../../animation/pose-kitten-bones-with-an-anim-processor/overview.md)). Demonstrated by KSAGolf's putting pose, which also turns the whole kitten to address the ball: [Putting address pose drawn on the kitten skeleton](../../../../mods/ksagolf/features/putting-address-pose.md).

## Background: how the game does it

`AnimatedRenderable.UpdateAnimation` rebuilds the skeleton's world transforms for every view and runs each processor's `UpdateSkeleton` before it computes skinning matrices; afterwards `KittenRenderable.UpdateRenderData` draws the body and then the attachments:

```csharp
// current/decomp/KSA/AnimatedRenderable.cs L173-L185
		foreach (IAnimProcessor animProcessor2 in AnimProcessors)
		{
			if (animProcessor2.ShouldUpdate)
			{
				animProcessor2.UpdateSkeleton(Transform, Skeleton, (float)dt);
			}
		}
		Span<float4x4> span3 = (Skeleton.InverseBindPose ?? throw new Exception("No inv bind pose")).AsSpan();
		Span<float4x4> span4 = Skeleton.WorldTransformList.AsSpan();
		for (int i = 0; i < boneCount; i++)
		{
			span[i] = span3[i] * span4[i];
		}
```

```csharp
// current/decomp/KSA/KittenRenderable.cs L399-L406
		foreach (CharacterAvatar.CosmeticAttachment cosmeticAttachment in _characterAvatar.Attachments.CosmeticAttachments)
		{
			float4x4 boneTransform3 = _characterAvatar.Core.CharacterModel.GetBoneTransform(cosmeticAttachment.SocketIndex);
			float4x4 float4x8 = float4x4.CreateRotationZ(-(float)Math.PI / 2f) * float4x4.CreateRotationX(-(float)Math.PI / 2f);
			cosmeticAttachment.Mesh.Transform = cosmeticAttachment.Transform * (float4x8 * boneTransform3);
			cosmeticAttachment.Mesh.Draw(view);
		}
	}
```

Because the engine composes `attachment.Transform * (SocketTurn * bone)` with `bone = WorldTransformList[i] * Transform` (the model transform), the attachment that cancels the socket and bone parts leaves your `placed` matrix, **in model space**, to be carried by the model transform. That is why `placed` can be written in the skeleton's own centimetre space regardless of where the kitten is in the world.

## Lifecycle and timing

| Step | Where it runs | Lifecycle |
|---|---|---|
| The processor is added to `AnimProcessors` and the prop to `CosmeticAttachments` | `Golf.Hold` from the mod's per-step update | `frame:draw-ui` |
| `UpdateSkeleton` rewrites the attachment `Transform`, once per view | `KittenRenderable.UpdateRenderData` to `AnimatedRenderable.UpdateAnimation`, just before the attachment loop | `frame:render` |

Writing the attachment from a StarMap hook would use the previous frame's wrist; `UpdateSkeleton` is the one point where a view's skeleton is final and the attachment loop has not run yet. The callback runs inside the engine's render loop, so wrap the body in `try`/`catch`: an unplaced prop is better than a frame that throws. See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare the engine's turn.** `SocketTurn` is the constant the engine applies before the socket bone, `RotZ(-90 deg) * RotX(-90 deg)`:

```csharp
// src/KSAGolf/Ksa/GolfPose.cs L31-L32
    // The attachment's own turn, applied before the socket's bone by KittenRenderable.
    private static readonly float4x4 SocketTurn = float4x4.CreateRotationZ(-MathF.PI / 2f) * float4x4.CreateRotationX(-MathF.PI / 2f);
```

2. **Implement `IAnimProcessor` and register it** on the model's `AnimProcessors` when you pick the prop up (remove it when you let go):

```csharp
// src/KSAGolf/Ksa/GolfPose.cs L23-L24
internal sealed class GolfPose(KittenEva kitten, CharacterAvatar avatar, StaticMeshRenderable club, PutterRig rig, Func<Posture> posture)
    : IAnimProcessor, IFirstPersonBody
```

```csharp
// src/KSAGolf/Ksa/Golf.cs L495-L496
        _pose = new GolfPose(kitten, avatar, club!, _rig, Posture);
        avatar.Core.CharacterModel.AnimProcessors.Add(_pose);
```

```csharp
// src/KSAGolf/Ksa/Golf.cs L526-L528
            avatar.Attachments.CosmeticAttachments.RemoveAll(a => ReferenceEquals(a.Mesh, _club));
            if (_pose is not null) avatar.Core.CharacterModel.AnimProcessors.Remove(_pose);
            avatar.Attachments.Mmu.Enabled = _mmuWas;
```

3. **Decide where the prop goes, in model space.** At address KSAGolf takes it from a pure-maths rig. While carrying, it puts the grip at the centroid of the index, pinky and middle knuckles with the shaft along index-to-pinky; your prop can use any model-space matrix, for example a bone's world transform times a fixed offset:

```csharp
// src/KSAGolf/Ksa/GolfPose.cs L157-L173
    private float4x4 InTheFist(Span<float4x4> world)
    {
        double3 index = Position(world[_bones[6]]), pinky = Position(world[_bones[7]]), middle = Position(world[_bones[8]]);
        double3 fist = (index + pinky + middle) / 3.0;
        double3 down = Vec.Unit(pinky - index);

        double3 face = Vec.Unit(Vec.RejectFrom(new double3(0, 0, 1), down));
        if (!Vec.IsFinite(face) || Vec.Len2(face) < 0.5) face = Vec.Unit(Vec.AnyPerpendicular(down));
        double3 shaftUp = -down;
        double3 toe = Vec.Cross(face, shaftUp);

        float4x4 turn = new((float)toe.X, (float)toe.Y, (float)toe.Z, 0f,
                            (float)face.X, (float)face.Y, (float)face.Z, 0f,
                            (float)shaftUp.X, (float)shaftUp.Y, (float)shaftUp.Z, 0f,
                            0f, 0f, 0f, 1f);
        return float4x4.CreateScale((float)rig.Scale) * turn * float4x4.CreateTranslation(float3.Pack(fist));
    }
```

4. **Solve the attachment against this view's wrist and write it back.** Invert `SocketTurn * wristWorld` for the wrist bone this view just built, multiply, then read, change and write back the struct in the list:

```csharp
// src/KSAGolf/Ksa/GolfPose.cs L83-L112
    public void UpdateSkeleton(float4x4 transform, Skeleton skeleton, float dt)
    {
        ViewmodelApplied = false;
        try
        {
            if (!Resolve(skeleton)) return;

            float3 head = skeleton.WorldTransforms[_bones[5]].Translation;
            Head = new double3(head.X, head.Y, head.Z);

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
        }
        catch
        {
            // As above.
        }
    }
```

5. **Resolve bone indices once.** Look the bone names up in `Skeleton.BoneNames` the first time and cache the indices; bail out if any is missing so a different rig draws an unposed kitten rather than throwing:

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

6. **Check the result.** Walk the kitten with the prop and open a second camera window: in the main view and in the window the prop sits on the drawn hand with no lag or sliding, even while the arm swings.

## Complete example

- [src/KSAGolf/Ksa/GolfPose.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/GolfPose.cs) - the processor (`UpdateSkeleton`, `InTheFist`, `Resolve`).
- [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L471) - adding the processor and the prop together.
- Feature pages: [Putting address pose drawn on the kitten skeleton](../../../../mods/ksagolf/features/putting-address-pose.md), [Posing kitten bones through the pose-processor list](../../../../mods/ksagolf/features/kitten-pose-processor.md).

## Variations and gotchas

- **Drawn, not moved.** The whole pose lives in the renderer; the physics body stays where the player walked it.
- **Order matters.** `UpdateSkeleton` must run before the attachment loop; the processor list is the only seam between them, so register your processor rather than patching.
- **`Priority` and ordering with other processors.** Other mods' processors may run on the same skeleton; see [pose kitten bones with an anim processor](../../animation/pose-kitten-bones-with-an-anim-processor/overview.md).
- **One mesh, one attachment.** Find your entry by `ReferenceEquals(a.Mesh, club)`; entries added by other mods share the list.
- **Version notes.** `KittenRenderable`, `CharacterAvatar` and `Skeleton` are unchanged in build 2026.10.10.5554 versus the 2026.9.22.5482 build the mod was written against, and the attachment composition (`RotZ(-90) * RotX(-90) * bone`) is the same.

## See also

- [Topic overview](overview.md), [approach 1: static offset and building the mesh](attach-a-prop-to-a-kitten-bone-1.md)
- Mod features: [Putting address pose](../../../../mods/ksagolf/features/putting-address-pose.md), [Kitten pose processor](../../../../mods/ksagolf/features/kitten-pose-processor.md)
- [Pose kitten bones with an anim processor](../../animation/pose-kitten-bones-with-an-anim-processor/overview.md), [Hide a kitten's body from one viewport](../hide-a-kitten-body-from-one-viewport/overview.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
