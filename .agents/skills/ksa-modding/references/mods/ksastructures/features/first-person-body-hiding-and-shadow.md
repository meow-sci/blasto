---
type: Mod Feature
title: First-person body hiding with a full-body shadow
description: An IAnimProcessor folds every bone of the walking kitten to its head for the main view only, and Draw postfixes swap the folded kitten's shadow entries for a second skinning of the whole body, so the kitten is invisible to its own eyes but still casts its full shadow
mod: ksastructures
category: kittens
techniques: [harmony-prefix, harmony-postfix, reflection, anim-processor]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-prefix KSA.KittenEva.UpdateRenderData
  - harmony-postfix KSA.KittenEva.UpdateRenderData
  - harmony-prefix KSA.AnimatedRenderable.Draw
  - harmony-postfix KSA.AnimatedRenderable.Draw
  - harmony-prefix KSA.StaticMeshRenderable.Draw
  - harmony-postfix KSA.StaticMeshRenderable.Draw
  - reflection KSA.KittenRenderable._characterAvatar
  - reflection RenderCore.Animation.Skeleton.InverseBindPose
  - reflection KSA.AnimatedRenderable.ShadowIgnoreIndicies
  - reflection KSA.AnimatedRenderable.MaterialIndices
lifecycle: [startup:all-mods-loaded, frame:draw-ui, render:prepass, render:shadows]
game_types:
  - KSA.KittenEva
  - KSA.KittenRenderable
  - KSA.AnimatedRenderable
  - KSA.IAnimProcessor
  - KSA.CharacterAvatar
  - KSA.StaticMeshRenderable
  - KSA.SuperMeshRenderSystem
  - KSA.ShadowRenderable
  - KSA.InstanceData
  - RenderCore.Animation.Skeleton
  - KSA.Program
tags: [first-person, kitten, skinning, animation, shadows, viewports, harmony]
---

In first person the camera sits at the walking kitten's eyes. Drawn normally, its own arms, muzzle
and helmet would fill the screen. The mod makes the body vanish from the main view only. Camera
windows still show the whole kitten, and in the main view the kitten still casts its complete
shadow, helmet included.

Mechanism in one line: a custom `IAnimProcessor` collapses every bone world matrix to a zero-scale
matrix at the head, but only while `KittenEva.UpdateRenderData` is drawing the main viewport. A
postfix on `AnimatedRenderable.Draw` then removes the collapsed kitten's `ShadowRenderable` entries
and adds new ones skinned from a saved copy of the full pose. Placing the camera, the look and the
walk direction are covered by the separate feature [First-person kitten view](./first-person-kitten-view.md).

## How it works

**Getting at the skeleton.** `KittenEva.Renderable` is public, but the `CharacterAvatar` inside
`KittenRenderable` is the private field `_characterAvatar`. `KittenFrame.AvatarOf` reads it by
reflection ([src/KSAStructures/Ksa/KittenFrame.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KittenFrame.cs#L18-L23),
[current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L13)). `FirstPerson.Drive` adds an `EyesPose` to
`avatar.Core.CharacterModel.AnimProcessors` and publishes it as `FirstPersonHook.Body`; `Leave`
removes it ([src/KSAStructures/Ksa/FirstPerson.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPerson.cs#L50-L71),
[src/KSAStructures/Ksa/FirstPerson.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPerson.cs#L82-L101)).

**Which view is being drawn.** A prefix on `KittenEva.UpdateRenderData(IViewport, int)` sets
`_drawingMain` to the kitten when it is the first-person kitten and the viewport is
`Program.MainViewport`; the postfix clears it
([src/KSAStructures/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPersonHook.cs#L177-L191)).

**Folding the body** (`EyesPose.UpdateSkeleton`,
[src/KSAStructures/Ksa/FirstPerson.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPerson.cs#L160-L187)). On every call it records the `Head_M` bone's
position for the camera. Only for the main view it also copies `skeleton.WorldTransforms` into
`BodyWorld`, overwrites every bone with a matrix whose rotation and scale rows are zero and whose
translation is the head, and sets `ViewmodelApplied`. The engine builds the skinning matrices as
`InverseBindPose[i] * WorldTransform[i]` straight after the processors run, so every vertex lands
on the head point ([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L173-L185)). The helmet and visor
take their matrix from `GetBoneTransform(SocketIndex)` = `WorldTransformList[i] * Transform`, so
they collapse with it ([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L301-L308)).

**Putting the shadow back** (`AfterDraw`, [src/KSAStructures/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPersonHook.cs#L193-L247)).
`AnimatedRenderable.Draw` appends one `ShadowRenderable` per shadow-casting mesh to
`SuperMeshRenderSystem.ShadowRenderablePool`, using the collapsed skin
([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L235-L257)).[^ksa-animatedrenderable] When
`ViewmodelApplied` is set for the avatar's `CharacterModel`, the prefix notes the pool's count, and
the postfix:

1. removes everything that `Draw` appended;
2. once per model, reads `Skeleton.InverseBindPose` and `AnimatedRenderable.ShadowIgnoreIndicies`
   by reflection (both are `NativeList`s, a type from an assembly the mod does not reference) and
   picks the shadow-casting meshes ([src/KSAStructures/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPersonHook.cs#L300-L311));
3. allocates `bones` matrices with `SuperMeshRenderSystem.AllocateSkinningMatrices`, the same
   per-frame bone buffer the engine's renderer allocates from (`MeshRenderTechnique.AllocateSkinningMatrices`
   forwards to its render context), and writes `inverseBind[i] * BodyWorld[i]`
   ([current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L1007-L1013),
   [current/decomp/KSA/MeshRenderTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshRenderTechnique.cs#L169-L172));
4. appends a `ShadowRenderable` per mesh with `InstanceData { model = Transform, data = (material, boneOffset, 1, 0) }`.
   That is the engine's own layout, where `data.y` is the skinning offset
   ([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L215-L219)).

**Helmet and visor** ([src/KSAStructures/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPersonHook.cs#L249-L298)). Around
`StaticMeshRenderable.Draw` for the avatar's `HelmetMesh` or `VisorMesh` in the main view, the
postfix rewrites the model matrix of the shadow entries that call appended. It uses
`HelmetTransform * (SocketTurn * (BodyWorld[SocketIndex] * CharacterModel.Transform))`, the
engine's own attachment formula evaluated on the full-body head
([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L363-L370)).

## Lifecycle and timing

The per-viewport order, all on the main thread inside `Program.RenderGame`:

| Step | Where |
|---|---|
| Kittens are skipped by the vehicle loop of `UpdateRenderingResources` and drawn only inside the render | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4349-L4357) |
| Every visible camera window first: `ClearBuckets`, then `KittenEva.UpdateRenderData(viewport)` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4481-L4495), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4672-L4683) |
| Then the main view: `ClearBuckets`, then `KittenEva.UpdateRenderData(MainViewport)` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4687-L4703) |
| Inside it: `KittenRenderable.UpdateRenderData` sets `CharacterModel.Transform`, calls `UpdateAnimation` (pose, skeleton, processors' `UpdateSkeleton`, skin matrices), `Draw(view)` (colour and shadow-pool entries), fur, then the helmet and visor `Draw`s | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L1062-L1078), [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L353-L370) |
| Pool read by the lamp shadow atlas, then the sun cascades | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4721-L4767) |

- The local pose is advanced once per frame, but `UpdateAnimation` rebuilds the skeleton's world
  transforms and reruns every processor's `UpdateSkeleton` on each call, that is, once per viewport
  ([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L134-L207)). That is what lets one processor fold
  the body for one view and leave it whole for the others. Only parachutes set
  `SkinningPoseIsViewportInvariant`, which would reuse one skin for every view
  ([current/decomp/KSA/ChuteRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChuteRenderable.cs#L30)).
- Camera windows are recorded first and empty the pool for themselves, so the entries the main view
  leaves in the pool are the ones the shadow passes read, and those carry the full body.
- **Setup** (`FirstPersonHook.Install`) runs at `[StarMapAllModsLoaded]`
  ([src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L57)). The processor is attached and detached in
  the mod's per-frame step (`frame:draw-ui` normally), outside the render.
- Every hook body is wrapped in `try`/`catch`. A failure leaves an unposed or shadowless kitten,
  never a thrown frame.

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/FirstPerson.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPerson.cs#L141-L188) | `EyesPose`: the `IAnimProcessor` that folds the body for the main view |
| [src/KSAStructures/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPersonHook.cs#L177-L191) | Which viewport is being drawn |
| [src/KSAStructures/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPersonHook.cs#L193-L247) | Shadow entries swapped for a full-body skinning |
| [src/KSAStructures/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPersonHook.cs#L249-L298) | Helmet and visor shadow matrices moved to the full-body head |
| [src/KSAStructures/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPersonHook.cs#L81-L118) | Patch installation |
| [src/KSAStructures/Ksa/KittenFrame.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KittenFrame.cs#L18-L23) | `AvatarOf`: reflection into `KittenRenderable._characterAvatar` |

## Snippets

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

```csharp
// src/KSAStructures/Ksa/FirstPersonHook.cs L211-L247
    private static void AfterDraw(AnimatedRenderable __instance)
    {
        int before = _poolBefore;
        _poolBefore = -1;
        try
        {
            if (before < 0 || Body is not { BodyWorld: { } body } || _materialIndices is null) return;

            List<ShadowRenderable> pool = Program.Instance.SuperMeshRenderSystem.ShadowRenderablePool;
            if (pool.Count > before) pool.RemoveRange(before, pool.Count - before);

            Describe(__instance);
            int bones = __instance.Skeleton.BoneCount;
            if (_inverseBind.Length < bones || body.Length < bones) return;

            Span<float4x4> skin = Program.Instance.SuperMeshRenderSystem.AllocateSkinningMatrices(bones, out int index);
            for (int i = 0; i < bones; i++) skin[i] = _inverseBind[i] * body[i];

            int[] materials = _materialIndices(__instance);
            float4x4 model = __instance.Transform;
            float scale = float.Max(model.X.XYZ.Length(), float.Max(model.Y.XYZ.Length(), model.Z.XYZ.Length()));
            foreach (int j in _shadowMeshes)
            {
                if (j >= __instance.DepthMeshBucketHandles.Length || j >= materials.Length) continue;
                pool.Add(new ShadowRenderable
                {
                    Radius = (__instance.GltfAssetRef.Meshes[j]?.BoundingRadius ?? 2f) * scale,
                    BucketHandle = __instance.DepthMeshBucketHandles[j],
                    InstanceData = new InstanceData { model = model, data = new float4(materials[j], index, 1f, 0f) },
                });
            }
        }
        catch
        {
            // A missing shadow is the only failure allowed.
        }
    }
```

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
// current/decomp/KSA/KittenRenderable.cs L353-L370
		_characterAvatar.Core.CharacterModel.Transform = float4x5 * transform;
		double dt2 = dt * Universe.GetSimulationSpeed() * Universe.GetAchivedSpeedFraction() * (double)_groundAnimPlaybackRate;
		_characterAvatar.Core.CharacterModel.UpdateAnimation(dt2);
		ViewHandle view = Program.Instance.SuperMeshRenderSystem.ViewForViewport(viewport);
		_characterAvatar.Core.CharacterModel.HideMaskedMeshes = HideHead;
		_characterAvatar.Core.CharacterModel.Draw(view);
		if (!HideHead)
		{
			_characterAvatar.Fur.CatFurRenderable.Draw(view);
		}
		if (_characterAvatar.Attachments.Helmet.HasAttachment)
		{
			float4x4 boneTransform = _characterAvatar.Core.CharacterModel.GetBoneTransform(_characterAvatar.Attachments.Helmet.SocketIndex);
			float4x4 float4x6 = float4x4.CreateRotationZ(-(float)Math.PI / 2f) * float4x4.CreateRotationX(-(float)Math.PI / 2f);
			_characterAvatar.Attachments.Helmet.HelmetMesh.Transform = _characterAvatar.Attachments.Helmet.HelmetTransform * (float4x6 * boneTransform);
			_characterAvatar.Attachments.Helmet.VisorMesh.Transform = _characterAvatar.Attachments.Helmet.VisorTransform * (float4x6 * boneTransform);
			_characterAvatar.Attachments.Helmet.HelmetMesh.Draw(view);
			_characterAvatar.Attachments.Helmet.VisorMesh.Draw(view);
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `KittenEva.UpdateRenderData(IViewport, int)`, `KittenEva.Renderable` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L1062-L1078) | Patched; marks the viewport being drawn |
| `KittenRenderable._characterAvatar` (private) | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L13) | The avatar, by reflection |
| `IAnimProcessor` (`UpdateSkeleton`, `UpdateLocalPose`, `Priority`, `ShouldUpdate`, `CanCacheAnimation`) | [current/decomp/KSA/IAnimProcessor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IAnimProcessor.cs#L8-L19) | Implemented by `EyesPose` |
| `AnimatedRenderable.AnimProcessors`, `.Skeleton`, `.Transform`, `.UpdateAnimation`, `.Draw` | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L54-L258) | Processor list; patched `Draw` |
| `AnimatedRenderable.DepthMeshBucketHandles`, `.MaterialIndices` (protected), `.ShadowIgnoreIndicies` | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L24-L32) | Rebuilding the shadow entries |
| `Skeleton.WorldTransforms`, `.InverseBindPose`, `.BoneNames` | [current/decomp/RenderCore.Animation/Skeleton.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Animation/Skeleton.cs#L15-L27) | The pose and its bind inverse |
| `SuperMeshRenderSystem.AllocateSkinningMatrices`, `.ShadowRenderablePool` | [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L1007-L1013) | Second skinning and pool edit |
| `CharacterAvatar.Core.CharacterModel`, `.Attachments.Helmet` (`HelmetMesh`, `VisorMesh`, `SocketIndex`, `HelmetTransform`, `VisorTransform`) | [current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L103-L117) | The meshes and socket to re-target |
| `StaticMeshRenderable.Draw` | [current/decomp/KSA/StaticMeshRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticMeshRenderable.cs#L56-L90) | Patched for helmet and visor |

## Pitfalls and version notes

- **KSA's own head hiding is narrower.** An IVA seat sets `KittenRenderable.HideHead` per viewport,
  which hides the masked head meshes only ([current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L103),
  [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L357-L362)); a kitten on foot needs the whole body gone.
- **The replacement entries leave `Center` at its default.** The engine's entries are centred on the
  pose's bone bounds with a pose-based radius. The mod's entries keep `Center` at (0, 0, 0), which in
  Ego space is the camera, with radius = mesh bounding radius x model scale. In first person the
  camera is at the kitten's eyes, so the culling sphere sits about the head; the light and cascade
  culls use that sphere.
- **Fur is not handled.** `CatFurRenderable.Draw` runs after the body draw unless `HideHead` is set;
  what the fur does with the collapsed skeleton was not traced here (unverified).
- **Per-viewport skinning is load-bearing.** If KSA starts caching kitten skinning across viewports
  (as it does for parachutes), the fold would reach every view.
- **Reflection surface.** `_characterAvatar`, `MaterialIndices`, `InverseBindPose` and
  `ShadowIgnoreIndicies` are read by name. A missing field disables the hiding with a log line, and
  `AvatarOf` returns null.
- **Version:** the mod is built against KSA 2026.10.7.5541; this page was checked against
  2026.10.10.5554, and the decompiled diff between the two does not touch the kitten, animation or
  SuperMesh files used here.

## See also

- [First-person kitten view](./first-person-kitten-view.md): the camera, the head look and the walk
- [Invisible shadow casters](./invisible-shadow-casters.md): the same shadow pool, used for static geometry
- [Level horizon camera controller](./level-horizon-camera-controller.md): how the main view is posed
- [Settings menu](./settings-menu.md): the first-person toggle
- [Overwrite world transforms in UpdateSkeleton once per view](../../../howto/kittens/animation/pose-kitten-bones-with-an-anim-processor/pose-kitten-bones-with-an-anim-processor-2.md) - how-to article demonstrating this feature
- [Fold all bones onto the head with zero-scale matrices, then give the shadow pass the full-body pose](../../../howto/kittens/appearance/hide-a-kitten-body-from-one-viewport/hide-a-kitten-body-from-one-viewport-1.md) - how-to article demonstrating this feature
- [Reach the avatar with one reflection hop and resolve bones by name](../../../howto/kittens/characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-1.md) - how-to article demonstrating this feature
- [Know where and when a kitten is animated and drawn, per viewport](../../../howto/kittens/characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-4.md) - how-to article demonstrating this feature

[^ksa-animatedrenderable]: KSA.AnimatedRenderable (decompiled)
