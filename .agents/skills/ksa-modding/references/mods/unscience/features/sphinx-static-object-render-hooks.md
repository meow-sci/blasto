---
type: Mod Feature
title: Drawing private meshes through the native static-object pipelines with three postfixes
description: Sphinx postfixes StaticObjectRenderer.UpdateRenderData, the private WriteCommandsColor and WriteCommandsPrePass so its own vertex, index, material and matrix buffers are drawn while the game's static-object pipeline, lighting and shadow descriptor sets are still bound
mod: unscience
submod: sphinx
category: rendering
techniques: [harmony-postfix, vulkan-buffer, vulkan-pipeline, reflection]
hooks:
  - harmony-postfix KSA.StaticObjectRenderer.UpdateRenderData
  - harmony-postfix KSA.StaticObjectRenderer.WriteCommandsColor (private, looked up by name and parameter types)
  - harmony-postfix KSA.StaticObjectRenderer.WriteCommandsPrePass
lifecycle: [frame:render, render:prepass, render:scene-opaque]
game_types:
  - KSA.StaticObjectRenderer
  - KSA.StaticObjectModel
  - KSA.PrePassRenderer
  - KSA.Program
  - KSA.IViewport
  - KSA.ViewportRegistry
  - KSA.Camera
  - KSA.Rendering.Utils
  - Core.Renderer
tags: [vulkan, static-objects, descriptor-sets, render-pass, harmony, glb, statics, lighting]
---

Sphinx's imported statics look like they belong in the scene: they receive the sun, cascaded and
terrain shadows, viewport lights, ambient occlusion, cloud shadows and atmosphere lighting, and they
write depth and normals for later passes. It gets all of that without a shader, pipeline or registry
entry of its own. It records its draws immediately after the game's static-object renderer has bound
its complete state, swapping in only the per-draw material set, the per-instance matrix set and its own
vertex and index buffers.[^sphinx-lib-readme]

Mechanism in one line: three Harmony postfixes on `StaticObjectRenderer` (`UpdateRenderData` for the
per-viewport matrices, the private `WriteCommandsColor` for the opaque and blended colour passes, and
`WriteCommandsPrePass` for the depth-normal prepass) call into `SphinxSubmod`, which records direct
`DrawIndexed` calls against `StaticObjectRenderer.PipelineLayout` / `PrePassPipelineLayout` with private
descriptor sets 2 and 3.[^sphinx-patches]

## How it works

**Patch set** ([sphinx.lib/SphinxPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxPatches.cs)).
All three targets are resolved with exact parameter types (`WriteCommandsColor` is private, so it is a
by-name lookup), patched together with the collision hooks, and on any failure everything is removed and
`Ready` stays false; placement and save replay refuse to run without it. `Remove` waits for the solvers,
clears per-bubble physics handles, then unpatches each method by its exact patch method. The supermod
applies it at [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L97)
and removes it at L151.[^sphinx-patches][^unscience-patcher]

| Postfix | Calls | Filter |
|---|---|---|
| `UpdateRenderData(IViewport, int)` | `SphinxSubmod.Prepare` -> `entry.Model.Prepare(viewport, frame, entry.Matrix(camera))` | Not in editor; visible entries on the viewport camera's `NearbyCelestial` |
| `WriteCommandsColor(CommandBuffer, IViewport, int, VkPipeline, DrawBucket)` | `Record(..., prepass: false, alpha: bucket == Blended)` | Skips `OpaqueTerrain` and viewports with no `NearbyCelestial` |
| `WriteCommandsPrePass(CommandBuffer, IViewport, int)` | `Record(..., prepass: true, alpha: false)` | Opaque primitives only |

**Private resources** ([sphinx.lib/StaticModelResources.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticModelResources.cs#L44-L126)),
one per placed static:

- A descriptor pool sized for one per-draw set per primitive plus one per-instance set per
  `MaxFramesInFlight × ViewportRegistry.MAX_VIEWPORTS` slot.
- Per primitive: device-local vertex, index (`uint`) and 24-byte `PerDrawData` storage buffers, and a
  set allocated from `StaticObjectRenderer.PerDrawDataDescriptorSetLayout` (binding 0 the material
  record, binding 1 a private Repeat sampler).
- One host-visible, host-coherent mapped buffer of 64-byte matrices, one slice per slot, with the
  stride aligned to `KSA.Rendering.Utils.MinStorageBufferOffsetAlignment`, each slice bound in its own
  `PerInstanceDataDescriptorSetLayout` set.
- Before allocating, the constructor asserts the managed layouts the shaders expect (32-byte vertex
  with normal at 12 and UV at 24, every `PerDrawData` field offset, 64-byte `float4x4`).
- Uploads go through Pebbles' `AssetUploadSubmission` (private command buffer, explicit barriers,
  submit and wait); `Dispose` waits for `Device.WaitIdle()` before freeing anything.[^resources]

**Prepare and record** ([sphinx.lib/StaticModelResources.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticModelResources.cs#L138-L169)).
`Prepare` writes the camera-relative matrix into slot `viewport.ShaderSlot × frames + frame` and
stamps it with `Renderer.FrameCount`. `Record` draws only if the slot was prepared in the same
`FrameCount`, binds sets 2 and 3 at `firstSet = 2`, binds the vertex and index buffers and issues
`DrawIndexed(count, 1, 0, 0, 0)`; with a direct draw `gl_DrawID` is 0, so each primitive's set points at
exactly one material record. Blended primitives are skipped in the prepass because the stock normal
prepass ignores alpha. For the blended bucket `SphinxSubmod.Record` sorts entries back to front by anchor
distance from the camera.[^resources][^sphinx-submod]

**No global allocations.** Nothing is added to `StaticObjectModel.Instances`, the shared mesh pools or
SuperMesh, so stock statics and their indirect draw tables are untouched, and removing a Sphinx static
frees only its own memory.[^sphinx-lib-readme][^scope-statics]

## Lifecycle and timing

Where the three targets run in build 5554 (all on the main thread, inside `frame:render`):[^ksa-program]

| Call site | Phase | Sphinx work |
|---|---|---|
| `Program.Render` L2468 -> `UpdateRenderingResources` -> per visible viewport `StaticObjectRenderer.UpdateRenderData` (L4377) | Resource update, before any command recording | Write this frame's matrix slice per viewport |
| `RenderGame` L4713 `PrePassRenderer.Render` -> `WriteCommandsPrePass` (PrePassRenderer.cs L278), "VesselPrePassOpaque" region | `render:prepass` (main viewport only) | Opaque depth and normals |
| `RenderGame` L4830 `StaticObjectRenderer.WriteCommands` (Opaque, then OpaqueTerrain) in the offscreen scene-opaque pass, after `PartModelRenderer.ColorData`, before ring meshes, the billboarded planet and SuperMesh | `render:scene-opaque` | Opaque draws in the Opaque call |
| `RenderGame` L4847 `WriteCommandsBlended`, last draw before the offscreen pass ends | `render:scene-opaque` | Cutout (alpha) draws, sorted |
| `RenderViewport` L4562 and L4569 | Secondary viewports | Same colour draws; no prepass there |
| `RenderEditor` L5045 and L5048 | Editor | Returns immediately (`Program.EditorFlag`) |

The colour hook is a postfix on the per-bucket method rather than on `WriteCommands`, because
`WriteCommandsColor` binds the pipeline, viewport, global set 0, the bindless texture set 1, CSM and
terrain-shadow sets 4 and 5 (shadowed for the main viewport, unshadowed otherwise), the viewport light
set 6, AO set 7, cloud-shadow set 8, the planet set 9 when the body has a render slot, and the PBR push
constants, and then returns. A postfix on the Opaque call therefore runs with all of that still bound,
even when there are no stock statics to draw. The OpaqueTerrain call binds a different terrain pipeline,
which is why that bucket is skipped.[^ksa-static-object-renderer]

The cascaded shadow loop (L4745-L4769) renders planet, SuperMesh, part-model and spline depth only, so
these meshes never become shadow casters; they only receive shadows. The render hooks are independent of
`DrawUI`, so statics keep rendering with the HUD hidden.

## Key code

| Path | Role |
|---|---|
| [sphinx.lib/SphinxPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxPatches.cs) | The three postfixes, `Ready` flag, apply and remove |
| [sphinx.lib/StaticModelResources.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticModelResources.cs) | Private buffers, descriptor sets, matrix slices, `Prepare`, `Record`, `Dispose` |
| [sphinx.lib/StaticGeometry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticGeometry.cs) | Pebbles host primitives to native static vertices and `PerDrawData` |
| [sphinx.lib/SphinxSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxSubmod.cs#L85-L106) | Per-viewport filtering, blended sort, error-to-hidden handling |
| [sphinx.lib/SphinxEntry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxEntry.cs#L21-L27) | Model matrix for one viewport camera |
| [scope/statics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/statics.md) | Authors' GPU and shader contract (sets, bindings, matrix convention) |

## Snippets

The three postfixes:

```csharp
// sphinx.lib/SphinxPatches.cs L10-L52
public static class SphinxPatches
{
    public static bool Ready { get; private set; }
    private static MethodInfo Method(string name, params Type[] parameters) =>
        AccessTools.Method(typeof(StaticObjectRenderer), name, parameters)
        ?? throw new MissingMethodException(typeof(StaticObjectRenderer).FullName, name);
    private static MethodInfo Color() => Method("WriteCommandsColor", typeof(CommandBuffer), typeof(IViewport), typeof(int), typeof(VkPipeline), typeof(StaticObjectModel.DrawBucket));
    private static MethodInfo Prepare() => Method(nameof(StaticObjectRenderer.UpdateRenderData), typeof(IViewport), typeof(int));
    private static MethodInfo Prepass() => Method(nameof(StaticObjectRenderer.WriteCommandsPrePass), typeof(CommandBuffer), typeof(IViewport), typeof(int));
    public static void Apply(Harmony harmony)
    {
        var prepare = Prepare(); var color = Color(); var prepass = Prepass();
        try
        {
            harmony.Patch(prepare, postfix: new HarmonyMethod(typeof(SphinxPatches), nameof(AfterPrepare)));
            harmony.Patch(color, postfix: new HarmonyMethod(typeof(SphinxPatches), nameof(AfterColor)));
            harmony.Patch(prepass, postfix: new HarmonyMethod(typeof(SphinxPatches), nameof(AfterPrepass)));
            SphinxPhysicsPatches.Apply(harmony);
            Ready = true;
        }
        catch { Remove(harmony); throw; }
    }
    // ...
    private static void AfterPrepare(IViewport viewport, int frameIndex) => SphinxSubmod.Instance?.Prepare(viewport, frameIndex);
    private static void AfterColor(CommandBuffer commandBuffer, IViewport viewport, int frameIndex, StaticObjectModel.DrawBucket bucket)
    {
        // Opaque and blended calls always bind complete game lighting state when a nearby body
        // exists. The terrain variant has a different shader and must never submit these meshes.
        if (viewport.GetCamera().NearbyCelestial == null || bucket == StaticObjectModel.DrawBucket.OpaqueTerrain) return;
        SphinxSubmod.Instance?.Record(commandBuffer, viewport, frameIndex, false, bucket == StaticObjectModel.DrawBucket.Blended);
    }
    private static void AfterPrepass(CommandBuffer commandBuffer, IViewport viewport, int frameIndex) =>
        SphinxSubmod.Instance?.Record(commandBuffer, viewport, frameIndex, true, false);
}
```

Per-viewport, per-frame slices, frame-stamped, drawn with only sets 2 and 3 replaced:

```csharp
// sphinx.lib/StaticModelResources.cs L138-L169
    private int Slot(IViewport viewport, int frame)
    {
        if (frame < 0 || frame >= _frames || viewport.ShaderSlot < 0 || viewport.ShaderSlot >= ViewportRegistry.MAX_VIEWPORTS)
            throw new InvalidOperationException("Sphinx viewport/frame allocation no longer matches the game.");
        return viewport.ShaderSlot * _frames + frame;
    }
    public void Prepare(IViewport viewport, int frame, float4x4 model)
    {
        if (_disposed || !ReferenceEquals(Owner, Program.GetRenderer()))
            throw new InvalidOperationException("The renderer changed; remove and re-place this static.");
        int slot = Slot(viewport, frame);
        MemoryMarshal.Write(_mapping!.Value.AsSpan().Slice(slot * _stride, 64), in model);
        _preparedFrames[slot] = Owner.FrameCount;
    }
    public void Record(CommandBuffer command, IViewport viewport, int frame, bool prepass, bool alpha)
    {
        if (_disposed || !ReferenceEquals(Owner, Program.GetRenderer())) return;
        int slot = Slot(viewport, frame);
        if (_preparedFrames[slot] != Owner.FrameCount) return;
        var layout = prepass ? StaticObjectRenderer.PrePassPipelineLayout : StaticObjectRenderer.PipelineLayout;
        foreach (var draw in _draws)
        {
            // Blended cutouts have no prepass: stock's normal prepass does not sample alpha.
            if (draw.Alpha != alpha || (prepass && draw.Alpha)) continue;
            VkDescriptorSet[] sets = [draw.Set, _instanceSets[slot]];
            command.BindDescriptorSets(VkPipelineBindPoint.Graphics, layout, 2, sets, default(Span<ByteSize32>));
            command.BindVertexBuffer(0, draw.Vertices!.Value.VkBuffer, draw.Vertices.Value.BindOffset);
            VkDeviceExtensions.BindIndexBuffer(command, draw.Indices!.Value.VkBuffer, draw.Indices.Value.BindOffset, VkIndexType.UInt32);
            // Direct draw => gl_DrawID == 0; each descriptor points at exactly one PerDrawData.
            command.DrawIndexed(draw.Count, 1, 0, 0, 0);
        }
    }
```

The game method whose bound state the colour postfix borrows:

```csharp
// current/decomp/KSA/StaticObjectRenderer.cs L306-L360
	private static void WriteCommandsColor(CommandBuffer commandBuffer, IViewport viewport, int frameIndex, VkPipeline pipeline, StaticObjectModel.DrawBucket bucket)
	{
		Celestial nearbyCelestial = viewport.GetCamera().NearbyCelestial;
		if (nearbyCelestial == null)
		{
			return;
		}
		PlanetRenderer planetRenderer = Program.GetPlanetRenderer();
		int num = planetRenderer.RenderUboSlot(nearbyCelestial);
		if (bucket != StaticObjectModel.DrawBucket.OpaqueTerrain || (num >= 0 && nearbyCelestial.IsBillboarded()))
		{
			commandBuffer.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
			Program.SetViewport(commandBuffer);
			// ...
			if (flag)
			{
				Program.LightSystem.BindDescriptorSet(commandBuffer, PipelineLayout, 6);
			}
			// ...
			commandBuffer.PushConstants(PipelineLayout, VkShaderStageFlags.FragmentBit, ByteSize.Zero, new PBRPushConstants.Data(flag2 && GameSettings.ShowStars(), flag2 && GameSettings.ShowAtmosphere(), thinFilmInterference: false, Program.Editor == null, atmosphereLighting));
			StaticObjectModel.Shared.WriteCommands(commandBuffer, PipelineLayout, viewport, frameIndex, bucket);
		}
	}
```

Where it sits in the main view's scene-opaque pass:

```csharp
// current/decomp/KSA/Program.cs L4827-L4848
		GenericMeshRenderer.WriteCommands(commandBuffer2, RenderedViewport, resourceFrameIndex);
		SplineRenderer.WriteCommands(commandBuffer2, RenderedViewport, resourceFrameIndex);
		PartModelRenderer.ColorData.WriteCommands(commandBuffer2, RenderedViewport, resourceFrameIndex);
		StaticObjectRenderer.WriteCommands(commandBuffer2, RenderedViewport, resourceFrameIndex);
		_planetTransparenciesRenderer.RenderRingMeshes(commandBuffer2, RenderedViewport, resourceFrameIndex);
		// ...
		SuperMeshRenderSystem.RenderMainPass(RenderedViewport, commandBuffer2);
		// ...
		GizmosRenderer.Render(commandBuffer2, RenderedViewport, resourceFrameIndex);
		// ...
		StaticObjectRenderer.WriteCommandsBlended(commandBuffer2, RenderedViewport, resourceFrameIndex);
		_offscreenTarget.EndRendering(commandBuffer2, ImageBarrierInfo.Presets.ColorAttachmentWrite, ImageBarrierInfo.Presets.DepthStencilAttachmentWrite);
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `StaticObjectRenderer.UpdateRenderData(IViewport, int)` | [current/decomp/KSA/StaticObjectRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectRenderer.cs#L275-L283) | Postfix target: matrix upload per viewport |
| `StaticObjectRenderer.WriteCommandsColor` (private), `WriteCommands`, `WriteCommandsBlended` | [current/decomp/KSA/StaticObjectRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectRenderer.cs#L295-L360) | Postfix target and its two callers per bucket |
| `StaticObjectRenderer.WriteCommandsPrePass` | [current/decomp/KSA/StaticObjectRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectRenderer.cs#L362-L370) | Postfix target: prepass pipeline and sets 0-1 bound |
| `StaticObjectRenderer.PipelineLayout`, `PrePassPipelineLayout`, `PerDrawDataDescriptorSetLayout`, `PerInstanceDataDescriptorSetLayout` | [current/decomp/KSA/StaticObjectRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectRenderer.cs#L22-L115) | Layouts the private sets are allocated from and bound against |
| `StaticObjectModel.DrawBucket`, `PerDrawData` | [current/decomp/KSA/StaticObjectModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectModel.cs#L16-L41) | Bucket filter and material record |
| `PrePassRenderer.Render` | [current/decomp/KSA/PrePassRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PrePassRenderer.cs#L268-L285) | Calls `WriteCommandsPrePass` in the opaque depth-normal pass |
| `Program.Render`, `UpdateRenderingResources`, `RenderGame`, `RenderViewport`, `RenderEditor` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2443-L2477) | Call order of the hooked methods |
| `Renderer.FrameCount`, `MaxFramesInFlight` | [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L28-L66) | Slot count and frame stamp (incremented after submit, L517) |
| `ViewportRegistry.MAX_VIEWPORTS`, `IViewport.ShaderSlot` | [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L18) | Matrix slice indexing (8 viewports) |

## Pitfalls and version notes

- `WriteCommandsColor` is private and found by string with exact parameter types; parameter names
  (`commandBuffer`, `viewport`, `frameIndex`, `bucket`) are also part of the Harmony contract. The
  authors recheck the method bodies, not just signatures, on every game update, because the postfix
  relies on what the body binds.[^scope-statics]
- If a future build stops binding full lighting state when there are no stock statics, Sphinx draws
  with stale or missing sets; the comment in `AfterColor` records that assumption.[^sphinx-patches]
- Only the main viewport has a Sphinx prepass, so statics in secondary viewports do not contribute to
  that view's AO or depth-normal consumers (the stock path behaves the same for static objects there).
- A renderer rebuild changes `Program.GetRenderer()`. `Record` then draws nothing and `Prepare` throws,
  which hides the entry and detaches its collider; the static must be re-placed or reloaded.[^resources][^sphinx-submod]
- The frame stamp (`_preparedFrames[slot] == FrameCount`) prevents a newly created resource from
  drawing an unwritten matrix slice in its first frame, and a viewport that was not prepared this frame
  from drawing last frame's matrix.[^ksa-renderer]
- Cutouts use the blended pipeline and a 0.5 alpha map; GLB blend modes are approximated, and imported
  emissive textures are not used (`EmissiveTextureIndex = -1`).[^static-geometry]
- The ego-space matrix is computed in double precision and packed to float per viewport, which keeps
  statics stable far from the world origin.[^sphinx-entry]

## See also

- [Body-fixed GLB statics placement](./sphinx-glb-statics-placement.md)
- [UV mapping and PNG texture overrides](./sphinx-uv-texture-overrides.md)
- [Static colliders](./sphinx-static-colliders.md) (applied by the same `SphinxPatches.Apply`)
- [Draw a part-anchored textured quad inside the scene's opaque pass](./thug-life-textured-quad-render-pass.md) (own pipeline, postfix on SuperMesh)
- [Private clutter materials](./pebbles-private-clutter-materials.md) and [GLB material conversion](./pebbles-glb-material-conversion.md)
- [Sphinx sub-mod](../submods/sphinx.md) and [mod overview](../overview.md)
- [GroundAnchor composed each frame into a camera-relative matrix](../../../howto/celestials/terrain/place-static-objects-on-a-planet-surface/place-static-objects-on-a-planet-surface-2.md) - how-to article demonstrating this feature
- [Draw private vertex, index and material buffers inside the static-object pipeline](../../../howto/rendering/meshes/draw-mod-meshes-with-the-engine-renderers/draw-mod-meshes-with-the-engine-renderers-2.md) - how-to article demonstrating this feature

[^sphinx-lib-readme]: sphinx.lib/README.md
[^sphinx-patches]: sphinx.lib/SphinxPatches.cs
[^unscience-patcher]: unscience/Patcher.cs
[^resources]: sphinx.lib/StaticModelResources.cs
[^sphinx-submod]: sphinx.lib/SphinxSubmod.cs
[^scope-statics]: scope/statics.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-static-object-renderer]: KSA.StaticObjectRenderer (decompiled, build 2026.10.10.5554)
[^ksa-renderer]: Core.Renderer (decompiled, build 2026.10.10.5554)
[^static-geometry]: sphinx.lib/StaticGeometry.cs
[^sphinx-entry]: sphinx.lib/SphinxEntry.cs
