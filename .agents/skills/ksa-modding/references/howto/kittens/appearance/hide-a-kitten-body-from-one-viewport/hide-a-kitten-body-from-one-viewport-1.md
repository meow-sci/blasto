---
type: How-To
title: Fold all bones onto the head with zero-scale matrices, then give the shadow pass the full-body pose
description: Mark the main viewport in a KittenEva.UpdateRenderData prefix, collapse every bone to the head in an IAnimProcessor for that view only, and replace the shadow entries with a second skinning of the saved full pose
category: kittens
group: appearance
topic: hide-a-kitten-body-from-one-viewport
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksastructures/features/first-person-body-hiding-and-shadow
  - mods/ksagolf/features/first-person-kitten-view
techniques: [anim-processor, harmony-prefix, harmony-postfix, reflection]
hooks:
  - harmony-prefix KSA.KittenEva.UpdateRenderData(IViewport, int)
  - harmony-postfix KSA.KittenEva.UpdateRenderData(IViewport, int)
  - harmony-prefix KSA.AnimatedRenderable.Draw(ViewHandle)
  - harmony-postfix KSA.AnimatedRenderable.Draw(ViewHandle)
  - harmony-prefix KSA.StaticMeshRenderable.Draw(ViewHandle)
  - harmony-postfix KSA.StaticMeshRenderable.Draw(ViewHandle)
  - anim-processor KSA.IAnimProcessor.UpdateSkeleton
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:render, render:prepass, render:shadows]
game_types:
  - KSA.KittenEva
  - KSA.KittenRenderable
  - KSA.AnimatedRenderable
  - KSA.IAnimProcessor
  - KSA.Program
  - RenderCore.Animation.Skeleton
  - KSA.SuperMeshRenderSystem
  - KSA.ShadowRenderable
  - KSA.InstanceData
  - KSA.StaticMeshRenderable
tags: [kitten, first-person, shadow, skinning, anim-processor, ksastructures]
---
In first person the camera sits at the walking kitten's eyes, and its own arms, muzzle and helmet would fill the screen. This article makes the body vanish from the **main view only**: camera windows and crew portraits still show the whole kitten, and in the main view the kitten still casts its complete shadow, helmet included. The mechanism: a Harmony prefix on `KittenEva.UpdateRenderData` marks when the main viewport is being drawn; a custom `IAnimProcessor` collapses every bone's world matrix to a zero-scale matrix at the head, but only for that view; a postfix on `AnimatedRenderable.Draw` then removes the collapsed kitten's `ShadowRenderable` entries and adds new ones skinned from a saved copy of the full pose. Prerequisites: StarMap, Harmony and an anim processor ([pose kitten bones with an anim processor](../../animation/pose-kitten-bones-with-an-anim-processor/overview.md)), and reflection into the avatar ([reach a kitten's avatar and skeleton](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md)). Demonstrated by KSAStructures (the technique KSAGolf also uses): [First-person body hiding and shadow](../../../../mods/ksastructures/features/first-person-body-hiding-and-shadow.md). Placing the camera, the look and the walk direction are other topics ([first-person camera for a kitten](../../../camera/special-views/first-person-camera-for-a-kitten/overview.md), [steer a walking kitten](../../eva/steer-a-walking-kitten/overview.md)). For the simpler engine switch that hides only the head, see the last section and [approach 2](hide-a-kitten-body-from-one-viewport-2.md).

## Background: how the game does it

**Skeleton rebuilt per view.** The local pose advances once per frame, but `AnimatedRenderable.UpdateAnimation` rebuilds the skeleton's world transforms and reruns every processor's `UpdateSkeleton` on each call, that is, once per viewport. The skinning matrices are `InverseBindPose[i] * WorldTransform[i]` computed straight after the processors run, so a processor that rewrites `WorldTransforms` for one view changes only that view's skin:

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

**Kitten draw order.** `KittenRenderable.UpdateRenderData` sets the model transform, calls `UpdateAnimation`, draws the body, the fur (unless `HideHead`), then the helmet and visor. The helmet and visor take their matrix from `GetBoneTransform(SocketIndex)`, which is `WorldTransformList[i] * Transform`, so they collapse with the skeleton:

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

```csharp
// current/decomp/KSA/AnimatedRenderable.cs L301-L307
	public float4x4 GetBoneTransform(int boneIdx)
	{
		if (Skeleton.BoneCount <= boneIdx)
		{
			return float4x4.Identity;
		}
		return Skeleton.WorldTransformList[boneIdx] * Transform;
```

**Shadow entries come from `Draw`.** `AnimatedRenderable.Draw` appends one `ShadowRenderable` per shadow-casting mesh to `SuperMeshRenderSystem.ShadowRenderablePool`, using the same (collapsed) skin through `AnimationIndex` in `InstanceData.data.Y`:

```csharp
// current/decomp/KSA/AnimatedRenderable.cs L235-L258
		if (!CastShadows)
		{
			return;
		}
		List<ShadowRenderable> shadowRenderablePool = Program.Instance.SuperMeshRenderSystem.ShadowRenderablePool;
		float num = float.Max(Transform.X.XYZ.Length(), float.Max(Transform.Y.XYZ.Length(), Transform.Z.XYZ.Length()));
		float3 center = ((_poseBonesMin + _poseBonesMax) * 0.5f).Transform(Transform);
		float num2 = (_poseBonesMax - _poseBonesMin).Length() * 0.5f;
		for (int j = 0; j < ColourMeshBucketHandles.Length; j++)
		{
			if (!ContainsIndex(ShadowIgnoreIndicies, j) && (!HideMaskedMeshes || !ContainsIndex(MaskedMeshIndices, j)))
			{
				instanceData.data.X = MaterialIndices[j];
				float num3 = 2f * (GltfAssetRef.Meshes[j]?.BoundingRadius ?? 2f) * _poseMaxBoneScale;
				shadowRenderablePool.Add(new ShadowRenderable
				{
					Center = center,
					Radius = (num2 + num3) * num,
					BucketHandle = DepthMeshBucketHandles[j],
					InstanceData = instanceData
				});
			}
		}
	}
```

Per-view order inside `Program.RenderGame` (all main thread): every visible secondary viewport goes first through `RenderViewport`, each with its own `ClearBuckets`; then the main view clears its buckets and draws its kittens. `ClearBuckets` also empties `ShadowRenderablePool`, so what the main view leaves in the pool is what the shadow cascades read:

```csharp
// current/decomp/KSA/Program.cs L4672-L4683
		using (commandBuffer2.TagRegion(Profiler.GpuTag.SecondaryViewport))
		{
			ReadOnlySpan<IViewport> views = ViewportRegistry.Views;
			for (int i = 0; i < views.Length; i++)
			{
				IViewport viewport = views[i];
				if (!viewport.IsMain() && viewport.Visible)
				{
					RenderViewport(commandBuffer2, viewport, resourceFrameIndex);
				}
			}
		}
```

```csharp
// current/decomp/KSA/SuperMeshRenderSystem.cs L546-L554
	public void ClearBuckets(IViewport viewport)
	{
		ViewHandle view = ViewForViewport(viewport);
		PassBucketSystem.Clear(view, MeshPass.OpaquePrePass);
		PassBucketSystem.Clear(view, MeshPass.TranslucentPrePass);
		PassBucketSystem.Clear(view, MeshPass.MainOpaque);
		PassBucketSystem.Clear(view, MeshPass.Translucency);
		ShadowRenderablePool.Clear();
	}
```

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Install the patches | `FirstPersonHook.Install` from `[StarMapAllModsLoaded]` | `startup:all-mods-loaded` |
| Add and remove the processor, publish it as the first-person body | the mod's per-frame step, outside the render | `frame:draw-ui` |
| Mark the main view; fold the skeleton; swap the shadow entries | `KittenEva.UpdateRenderData`, `UpdateSkeleton`, `AnimatedRenderable.Draw`, once per view | `frame:render` |

All callbacks run on the main thread inside the kitten's own draw for one view: `UpdateSkeleton` runs before the skinning matrices are computed, and the `Draw` postfix runs before the next view clears the pool. Every hook body is wrapped in `try`/`catch`: a failure leaves an unposed or shadowless kitten, never a thrown frame. See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Mark the main view.** A prefix on `KittenEva.UpdateRenderData(IViewport, int)` records the kitten when it is the first-person kitten and the viewport is `Program.MainViewport`; a postfix clears it. The processor asks `DrawingMainViewOf(kitten)` to learn which view it is posing:

```csharp
// src/KSAStructures/Ksa/FirstPersonHook.cs L177-L191
    private static void BeforeUpdateRenderData(KittenEva __instance, IViewport viewport)
    {
        try
        {
            _drawingMain = Body is { } b && ReferenceEquals(__instance, b.Kitten) && ReferenceEquals(viewport, Program.MainViewport)
                ? __instance
                : null;
        }
        catch
        {
            _drawingMain = null;
        }
    }

    private static void AfterUpdateRenderData() => _drawingMain = null;
```

2. **Add a processor to the model.** `KittenEva.Renderable` is public but the avatar inside it is the private `_characterAvatar` (reflection, see the sibling topic). Add the processor to `avatar.Core.CharacterModel.AnimProcessors` while first person is on; remove it when it ends:

```csharp
// src/KSAStructures/Ksa/FirstPerson.cs L50-L57
            if (_pose is null)
            {
                if (KittenFrame.AvatarOf(flown) is not { } avatar) return;

                _kitten = flown;
                _pose = new EyesPose(flown, avatar);
                avatar.Core.CharacterModel.AnimProcessors.Add(_pose);
            }
```

```csharp
// src/KSAStructures/Ksa/FirstPerson.cs L90
                avatar.Core.CharacterModel.AnimProcessors.Remove(_pose);
```

3. **Fold the skeleton in `UpdateSkeleton`, for the main view only.** Always record the head bone's position (the camera needs it). Only when the main view is being drawn, copy `skeleton.WorldTransforms` into `BodyWorld`, then overwrite every bone with a matrix whose rotation and scale rows are zero and whose translation is the head:

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

4. **Remember where the shadow pool was before `Draw`.** A prefix on `AnimatedRenderable.Draw` notes the pool's count, but only for the first-person model while a fold was applied:

```csharp
// src/KSAStructures/Ksa/FirstPersonHook.cs L193-L207
    private static void BeforeDraw(AnimatedRenderable __instance)
    {
        _poolBefore = -1;
        try
        {
            if (Body is { ViewmodelApplied: true } b && ReferenceEquals(__instance, b.Avatar.Core.CharacterModel))
            {
                _poolBefore = Program.Instance.SuperMeshRenderSystem.ShadowRenderablePool.Count;
            }
        }
        catch
        {
            _poolBefore = -1;
        }
    }
```

5. **Swap the shadow entries.** The postfix removes everything `Draw` appended, allocates a block of skinning matrices from the same per-frame bone buffer the engine's renderer uses (`SuperMeshRenderSystem.AllocateSkinningMatrices`), fills it with `inverseBind[i] * BodyWorld[i]`, and appends one `ShadowRenderable` per shadow-casting mesh, pointing at the block through `InstanceData.data.Y` (the slot `Draw` uses for its own animation index):

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

6. **Read the bind pose and the shadow-casting meshes by reflection.** `Skeleton.InverseBindPose` and `AnimatedRenderable.ShadowIgnoreIndicies` are `NativeList`s, a type from an assembly the mod does not reference, so the mod reads them by field name once per model and caches them. The protected `MaterialIndices` is reached with `AccessTools.FieldRefAccess`:

```csharp
// src/KSAStructures/Ksa/FirstPersonHook.cs L302-L311
    private static void Describe(AnimatedRenderable model)
    {
        if (ReferenceEquals(model, _described)) return;
        _described = model;

        IEnumerable<T> Read<T>(object? owner, Type type, string field) => AccessTools.Field(type, field)?.GetValue(owner) as IEnumerable<T> ?? [];
        _inverseBind = [.. Read<float4x4>(model.Skeleton, typeof(Skeleton), "InverseBindPose")];
        HashSet<int> ignored = [.. Read<int>(model, typeof(AnimatedRenderable), "ShadowIgnoreIndicies")];
        _shadowMeshes = [.. Enumerable.Range(0, model.DepthMeshBucketHandles.Length).Where(i => !ignored.Contains(i))];
    }
```

7. **Move the helmet and visor shadow back onto the whole head.** Their shadow entries come from `StaticMeshRenderable.Draw` using the collapsed head. A prefix and postfix on that method rewrite the model matrix of the entries it appended to `HelmetTransform * (SocketTurn * (BodyWorld[SocketIndex] * CharacterModel.Transform))`, the engine's attachment formula evaluated on the full-body head:

```csharp
// src/KSAStructures/Ksa/FirstPersonHook.cs L266-L290
    private static void AfterDrawMesh(StaticMeshRenderable __instance)
    {
        int before = _meshPoolBefore;
        _meshPoolBefore = -1;
        try
        {
            if (before < 0 || Body is not { BodyWorld: { } body } b || HelmetTransform(b, __instance) is not { } own) return;

            ref CharacterAvatar.Helmet helmet = ref b.Avatar.Attachments.Helmet;
            if (helmet.SocketIndex >= body.Length) return;

            float4x4 drawn = own * (SocketTurn * (body[helmet.SocketIndex] * b.Avatar.Core.CharacterModel.Transform));
            List<ShadowRenderable> pool = Program.Instance.SuperMeshRenderSystem.ShadowRenderablePool;
            for (int i = before; i < pool.Count; i++)
            {
                ShadowRenderable s = pool[i];
                s.InstanceData.model = drawn;
                pool[i] = s;
            }
        }
        catch
        {
            // As above.
        }
    }
```

8. **Install the four patches in one try.** Resolve all targets first, log and return if any is missing, so a game update disables first person instead of crashing:

```csharp
// src/KSAStructures/Ksa/FirstPersonHook.cs L81-L118
    public static void Install()
    {
        if (Installed) return;

        try
        {
            MethodInfo? render = AccessTools.Method(typeof(KittenEva), nameof(KittenEva.UpdateRenderData));
            MethodInfo? draw = AccessTools.Method(typeof(AnimatedRenderable), nameof(AnimatedRenderable.Draw));
            MethodInfo? drawMesh = AccessTools.Method(typeof(StaticMeshRenderable), nameof(StaticMeshRenderable.Draw));
            _materialIndices = AccessTools.FieldRefAccess<AnimatedRenderable, int[]>("MaterialIndices");
            if (render is null || draw is null || drawMesh is null)
            {
                Log.Warn("first person cannot hide the kitten: KSA has moved KittenEva.UpdateRenderData or a Draw");
                return;
            }

            _harmony = new Harmony(HarmonyId);

            if (AccessTools.Method(typeof(KittenLocomotion), nameof(KittenLocomotion.StepGrounded)) is { } step)
            {
                _harmony.Patch(step, prefix: Hook(nameof(BeforeStepGrounded)), postfix: Hook(nameof(AfterStepGrounded)));
            }
            else
            {
                Log.Warn("a first-person kitten cannot face where it looks: KSA has no KittenLocomotion.StepGrounded");
            }

            _harmony.Patch(render, prefix: Hook(nameof(BeforeUpdateRenderData)), postfix: Hook(nameof(AfterUpdateRenderData)));
            _harmony.Patch(draw, prefix: Hook(nameof(BeforeDraw)), postfix: Hook(nameof(AfterDraw)));
            _harmony.Patch(drawMesh, prefix: Hook(nameof(BeforeDrawMesh)), postfix: Hook(nameof(AfterDrawMesh)));
            Installed = true;
            Log.Info("first person hides the kitten from its own view and keeps its shadow, via KittenEva.UpdateRenderData and the two Draws");
        }
        catch (Exception e)
        {
            Log.Warn($"first person cannot hide the kitten: {e.GetBaseException().Message}");
        }
    }
```

9. **Check the result.** Turn first person on: the main view shows no kitten body, a camera window shows the whole kitten, and the ground shadow keeps the complete kitten silhouette including the helmet. Turn it off: the processor is removed and everything draws normally.

## Complete example

- [src/KSAStructures/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPersonHook.cs) - the patches (mark view, pool swap, helmet and visor shadow).
- [src/KSAStructures/Ksa/FirstPerson.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPerson.cs) - `EyesPose` and the attach/detach.
- Feature pages: [First-person body hiding and shadow](../../../../mods/ksastructures/features/first-person-body-hiding-and-shadow.md), [First-person kitten view](../../../../mods/ksagolf/features/first-person-kitten-view.md).

## Variations and gotchas

- **KSA's own head hiding is narrower.** An IVA seat sets `KittenRenderable.HideHead` per viewport, which hides only the masked head meshes and the fur; a kitten on foot needs the whole body gone (see [approach 2](hide-a-kitten-body-from-one-viewport-2.md) for using the engine switch).

- **Replacement shadow entries leave `Center` at its default.** The engine's entries are centred on the pose's bone bounds with a pose-based radius. The mod's entries keep `Center` at (0, 0, 0), which in Ego space is the camera; in first person the camera is at the head, so the culling sphere sits about the head. The light and cascade culls use that sphere; in a different camera setup copy the engine's calculation.
- **Fur is not handled.** `CatFurRenderable.Draw` runs after the body draw unless `HideHead` is set; what the fur does with the collapsed skeleton was not traced here (unverified).
- **Per-viewport skinning is load-bearing.** If KSA starts caching kitten skinning across viewports (as it does for parachutes through `SkinningPoseIsViewportInvariant`), the fold would reach every view.
- **Reflection surface.** `_characterAvatar`, `MaterialIndices`, `InverseBindPose` and `ShadowIgnoreIndicies` are read by name; a missing field disables hiding with a log line.
- **Version.** The mod is built against KSA 2026.10.7.5541; the decompiled diff to build 2026.10.10.5554 does not touch the kitten, animation or SuperMesh files used here.

## See also

- [Topic overview](overview.md), [approach 2: fold onto the wrist and keep the paw](hide-a-kitten-body-from-one-viewport-2.md)
- Mod features: [First-person body hiding and shadow](../../../../mods/ksastructures/features/first-person-body-hiding-and-shadow.md), [First-person kitten view](../../../../mods/ksagolf/features/first-person-kitten-view.md)
- [Pose kitten bones with an anim processor](../../animation/pose-kitten-bones-with-an-anim-processor/overview.md), [Reach a kitten's avatar and skeleton](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md), [First-person camera for a kitten](../../../camera/special-views/first-person-camera-for-a-kitten/overview.md), [Steer a walking kitten](../../eva/steer-a-walking-kitten/overview.md)
- [Add invisible shadow casters](../../../rendering/meshes/add-invisible-shadow-casters/overview.md), [Hide parts or subparts from rendering](../../../rendering/meshes/hide-parts-or-subparts-from-rendering/overview.md), [Unlock the IVA camera](../../../camera/special-views/unlock-the-iva-camera/overview.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
