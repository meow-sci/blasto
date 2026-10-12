---
type: How-To
title: Fold onto the wrist and keep the club-holding paw, or use KittenRenderable.HideHead per view
description: Collapse every bone onto the right wrist except the right paw subtree for the main view only, keep the full pose for the shadow, and compare the engine HideHead switch that IVA seats use
category: kittens
group: appearance
topic: hide-a-kitten-body-from-one-viewport
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony, hide-a-kitten-body-from-one-viewport-1]
demonstrated_by: [mods/ksagolf/features/first-person-kitten-view]
techniques: [anim-processor, harmony-prefix, harmony-postfix]
hooks:
  - harmony-prefix KSA.KittenEva.UpdateRenderData(IViewport, int)
  - harmony-postfix KSA.AnimatedRenderable.Draw(ViewHandle)
  - anim-processor KSA.IAnimProcessor.UpdateSkeleton
lifecycle: [startup:all-mods-loaded, frame:render, render:shadows]
game_types:
  - KSA.KittenEva
  - KSA.KittenRenderable
  - KSA.AnimatedRenderable
  - KSA.IAnimProcessor
  - KSA.IVASeat
  - RenderCore.Animation.Skeleton
  - KSA.SuperMeshRenderSystem
  - KSA.ShadowRenderable
tags: [kitten, first-person, viewmodel, shadow, ksagolf]
---
KSAGolf hides the walking kitten from its own eyes the same way as [approach 1](hide-a-kitten-body-from-one-viewport-1.md), with two differences that matter when you are not building a pure viewmodel-free first person. First, it folds every bone onto the **right wrist** instead of the head, and spares the right paw's subtree while the kitten carries the club, so the paw stays visible as a viewmodel. Second, the mod's notes record that the engine already has a simpler per-view switch, `KittenRenderable.HideHead`, which the mod does not use because it hides only the head. This article shows the wrist fold and the `HideHead` alternative. Prerequisites: everything in approach 1 (the view marker, the processor registration, the shadow swap). Demonstrated by KSAGolf: [First-person kitten view](../../../../mods/ksagolf/features/first-person-kitten-view.md).

## Background: how the game does it

The engine's own head-hiding switch is a public field set by an IVA seat once per viewport. When `HideHead` is true the kitten's `CharacterModel` skips the masked head meshes in both the colour pass and the shadow pass, and the fur draw is skipped; the helmet and visor are separate attachments and are still drawn:

```csharp
// current/decomp/KSA/IVASeat.cs L103
		_renderable.HideHead = IsCameraInThisSeat(viewport);
```

```csharp
// current/decomp/KSA/KittenRenderable.cs L357-L361
		_characterAvatar.Core.CharacterModel.HideMaskedMeshes = HideHead;
		_characterAvatar.Core.CharacterModel.Draw(view);
		if (!HideHead)
		{
			_characterAvatar.Fur.CatFurRenderable.Draw(view);
```

```csharp
// current/decomp/KSA/AnimatedRenderable.cs L245
			if (!ContainsIndex(ShadowIgnoreIndicies, j) && (!HideMaskedMeshes || !ContainsIndex(MaskedMeshIndices, j)))
```

A skeleton fold has a visual cost. Collapsing the whole kitten onto one point stretches skin: the vertices of the folded bones all land on that point, and the skin of the paw you keep is stretched toward it. Folding onto the chest stretched sheets of skin across the view; folding onto the wrist keeps the distance between the kept paw and the folded arm short.

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
| Patches installed | `FirstPersonHook.Install` from `[StarMapAllModsLoaded]` | `startup:all-mods-loaded` |
| View marker, strip, pool swap | `KittenEva.UpdateRenderData`, `GolfPose.UpdateSkeleton`, `AnimatedRenderable.Draw`, per view | `frame:render` |

The strip runs from the same `UpdateSkeleton` that places the held club, so for the main view the body is folded after the club attachment has been solved (the attachment is solved against the wrist before the fold, see [attach a prop to a kitten bone](../attach-a-prop-to-a-kitten-bone/overview.md), approach 2). See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Use the same view marker as approach 1.** KSAGolf's prefix and postfix on `KittenEva.UpdateRenderData` are identical in shape:

```csharp
// src/KSAGolf/Ksa/FirstPersonHook.cs L178-L190
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
```

2. **Fold onto the wrist, keep a subtree.** Copy the world transforms to `BodyWorld`, mark which bones lie below the kept bone, then overwrite every other bone with a zero-scale matrix at the wrist. `keepPaw` is true while carrying and false at address (where the mod draws a separate club):

```csharp
// src/KSAGolf/Ksa/GolfPose.cs L119-L135
    private void StripForTheEyes(Skeleton skeleton, bool keepPaw)
    {
        Span<float4x4> world = skeleton.WorldTransforms;
        BodyWorld = BodyWorld is { } kept && kept.Length == world.Length ? kept : new float4x4[world.Length];
        world.CopyTo(BodyWorld);

        if (_rightPaw.Length != world.Length) _rightPaw = Below(skeleton, _bones[2]);

        float3 wrist = world[_bones[2]].Translation;
        float4x4 gone = new(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, wrist.X, wrist.Y, wrist.Z, 1f);
        for (int i = 0; i < world.Length; i++)
        {
            if (!(keepPaw && _rightPaw[i])) world[i] = gone;
        }

        ViewmodelApplied = true;
    }
```

```csharp
// src/KSAGolf/Ksa/GolfPose.cs L137-L153
    // A bone and everything hanging from it.
    private static bool[] Below(Skeleton skeleton, int top)
    {
        Span<TransformNodeInfo> nodes = skeleton.NodeInfo;
        bool[] below = new bool[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            for (int b = i, hops = 0; b >= 0 && hops < nodes.Length; b = nodes[b].ParentIndex, hops++)
            {
                if (b != top) continue;
                below[i] = true;
                break;
            }
        }

        return below;
    }
```

3. **Call it from `UpdateSkeleton`, for the main view only.** The strip happens at the end of the processor's per-view callback, after the attachment has been solved against the unfolded wrist:

```csharp
// src/KSAGolf/Ksa/GolfPose.cs L106
            if (now.FirstPerson && FirstPersonHook.DrawingMainViewOf(kitten)) StripForTheEyes(skeleton, keepPaw: now.Theta is null);
```

4. **Keep the whole kitten for the shadow.** Reuse approach 1's `BeforeDraw`/`AfterDraw` pool swap unchanged; KSAGolf's copy takes `BodyWorld` from the same kind of interface:

```csharp
// src/KSAGolf/Ksa/FirstPersonHook.cs L212-L247
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
```

5. **Alternative: use the engine's `HideHead` per view.** If you only need the head and fur gone, set the public field around the kitten's own draw call instead of folding. The following is **adapted** from the shape of `IVASeat.UpdateSeatedRenderable` and the prefix in step 1; it assumes you track the kitten whose view you borrow in `MyKitten`:

```csharp
// adapted from KSA IVASeat.UpdateSeatedRenderable (HideHead per viewport) and KSAGolf/src/KSAGolf/Ksa/FirstPersonHook.cs
private static void HideHeadBefore(KittenEva __instance, IViewport viewport, out bool __state)
{
    __state = __instance.Renderable.HideHead;
    if (ReferenceEquals(__instance, MyKitten) && ReferenceEquals(viewport, Program.MainViewport))
        __instance.Renderable.HideHead = true;
}

private static void HideHeadAfter(KittenEva __instance, bool __state)
    => __instance.Renderable.HideHead = __state;
```

Patch both onto `KittenEva.UpdateRenderData(IViewport, int)`. The helmet and visor are still drawn (they are not masked head meshes); clear `Attachments.Helmet.Enabled` as in [tint and restyle kitten appearance, approach 5](../tint-and-restyle-kitten-appearance/tint-and-restyle-kitten-appearance-5.md) if they would be in the way. The head's shadow is lost with the head meshes, so if you want a full shadow you still need the pool swap.

6. **Check the result.** Pick up the club and turn first person on: the main view shows only the right paw gripping the club; camera windows show the whole kitten; the ground shadow shows the whole kitten.

## Complete example

- [src/KSAGolf/Ksa/GolfPose.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/GolfPose.cs) - `StripForTheEyes`, `Below`, `UpdateSkeleton`.
- [src/KSAGolf/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/FirstPersonHook.cs) - the four patches, shadow swap.
- Feature page: [First-person kitten view](../../../../mods/ksagolf/features/first-person-kitten-view.md).

## Variations and gotchas

- **Fold target matters.** Folding onto the chest stretched skin into sheets across the view (the mod's finding); the wrist keeps the skin between the kept paw and the folded arm short. Pick a fold target near the part you keep.
- **Helmet and fur fold with the head and body they hang from** (the mod's own comment); the helmet matrix derives from the folded bone, so no special handling is needed for the main view colour pass.
- **`HideHead` is the cheap option.** It costs one field write and hides head and fur in colour and shadow; the strip costs a skeleton copy and a second skinning but hides the whole body.
- **`ShadowRenderable.Center` (new in build 2026.10.10.5554).** The replacement shadow entries leave `Center` at its default; set `Center = ((min + max) / 2) * model` from `BodyWorld` to match the engine if cascade culling drops the shadow at cascade edges (unverified in game).
- **Helmet path is dormant while golfing.** The mod disables the helmet during golf, so its helmet and visor shadow fix-up only matters for a caller that keeps the helmet on.

## See also

- [Topic overview](overview.md), [approach 1: fold onto the head and swap the shadow](hide-a-kitten-body-from-one-viewport-1.md)
- Mod feature: [First-person kitten view](../../../../mods/ksagolf/features/first-person-kitten-view.md), [First-person body hiding and shadow](../../../../mods/ksastructures/features/first-person-body-hiding-and-shadow.md)
- [Attach a prop to a kitten bone](../attach-a-prop-to-a-kitten-bone/overview.md), [Tint and restyle kitten appearance](../tint-and-restyle-kitten-appearance/overview.md), [Animate and draw seated kitten crew](../../animation/animate-and-draw-seated-kitten-crew/overview.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
