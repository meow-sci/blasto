---
type: How-To
title: Draw private vertex, index and material buffers inside the static-object pipeline
description: Record DrawIndexed calls for your own GPU buffers from postfixes on StaticObjectRenderer so the mesh uses the game's PBR, shadow-receiving, light-aware static-object shader state
category: rendering
group: meshes
topic: draw-mod-meshes-with-the-engine-renderers
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony, vulkan]
demonstrated_by: [mods/unscience/features/sphinx-static-object-render-hooks]
techniques: [harmony-postfix, vulkan-buffer, reflection]
hooks:
  - harmony-postfix KSA.StaticObjectRenderer.UpdateRenderData
  - harmony-postfix KSA.StaticObjectRenderer.WriteCommandsColor
  - harmony-postfix KSA.StaticObjectRenderer.WriteCommandsPrePass
lifecycle: [frame:render, render:prepass, render:scene-opaque]
game_types:
  - KSA.StaticObjectRenderer
  - KSA.StaticObjectModel
  - KSA.IViewport
  - KSA.ViewportRegistry
  - KSA.PrePassRenderer
tags: [static-object, vulkan, descriptor-sets, per-viewport, prepass, shadow-receiver]
---
Outcome: an arbitrary mesh (here, GLB statics placed on terrain) that receives sun and terrain shadows, viewport lights, ambient occlusion, cloud shadows and atmosphere lighting, writes depth and normals for the prepass, and needs no shader, pipeline or registry entry of its own. You own the vertex, index and material buffers; the game owns everything else.

Demonstrated by unscience's Sphinx sub-mod ([Static-object render hooks](../../../../mods/unscience/features/sphinx-static-object-render-hooks.md)). Prerequisites: StarMap, Harmony, comfortable Vulkan buffer and descriptor-set code (this is the most involved approach in this topic).

## Background: how the game does it

`StaticObjectRenderer` [current/decomp/KSA/StaticObjectRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectRenderer.cs#L295-L370) draws every placed static object from one shared `StaticObjectModel`. Its draw methods bind a complete state before issuing draws: pipeline, global set 0, bindless textures set 1, cascaded and terrain shadow sets 4 and 5, light set 6, ambient occlusion set 7, cloud shadows set 8, the planet set 9 and the PBR push constants. The per-draw material set (2) and per-instance matrix set (3) are the only layouts a model owns (`PerDrawDataDescriptorSetLayout`, `PerInstanceDataDescriptorSetLayout`).

The colour method is private; the opening and closing of it matter for this approach:

```csharp
// current/decomp/KSA/StaticObjectRenderer.cs L306-L318, L355-L359
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
			bool atmosphereLighting = GameSettings.Current.Graphics.Atmosphere && nearbyCelestial.BodyTemplate.AtmosphereReference != null;
			bool flag2 = GameSettings.ShowReflections();
			commandBuffer.PushConstants(PipelineLayout, VkShaderStageFlags.FragmentBit, ByteSize.Zero, new PBRPushConstants.Data(flag2 && GameSettings.ShowStars(), flag2 && GameSettings.ShowAtmosphere(), thinFilmInterference: false, Program.Editor == null, atmosphereLighting));
			StaticObjectModel.Shared.WriteCommands(commandBuffer, PipelineLayout, viewport, frameIndex, bucket);
		}
```

A postfix on it therefore runs with all of that state still bound. You only rebind sets 2 and 3 (`firstSet = 2`), bind your vertex and index buffers and call `DrawIndexed`.

## Lifecycle and timing

| Hook | Called from | Phase |
|---|---|---|
| `StaticObjectRenderer.UpdateRenderData(IViewport, int)` | `Program.UpdateRenderingResources`, once per visible viewport, after `PartModelRenderer.UpdateRenderData` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4374-L4377)) | `frame:render`, before any command recording |
| `StaticObjectRenderer.WriteCommandsPrePass` | `PrePassRenderer.Render` ([current/decomp/KSA/PrePassRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PrePassRenderer.cs#L278)) | `render:prepass`, main viewport |
| `StaticObjectRenderer.WriteCommandsColor` (Opaque bucket) | `StaticObjectRenderer.WriteCommands` in the offscreen scope, after `PartModelRenderer.ColorData` and before ring meshes, `PlanetRenderer.Render` and `RenderMainPass` | `render:scene-opaque` |
| `WriteCommandsColor` (Blended bucket) | `WriteCommandsBlended`, the last draw before the offscreen scope ends | `render:scene-opaque` |

The same colour path also runs for each secondary viewport in `RenderViewport`. See the RenderGame table in [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence), rows 2 and 9.

```csharp
// current/decomp/KSA/Program.cs L4830, L4839, L4847
		StaticObjectRenderer.WriteCommands(commandBuffer2, RenderedViewport, resourceFrameIndex);
// ...
		SuperMeshRenderSystem.RenderMainPass(RenderedViewport, commandBuffer2);
// ...
		StaticObjectRenderer.WriteCommandsBlended(commandBuffer2, RenderedViewport, resourceFrameIndex);
```

The cascaded shadow loop renders planet, SuperMesh, part-model and spline depth only, so meshes drawn this way **receive** shadows but never **cast** them (use [invisible shadow casters](../add-invisible-shadow-casters/overview.md) for that). The hooks do not depend on `DrawUI`.

## Steps

1. **Patch three methods together, and roll back on failure.** `WriteCommandsColor` is private, so look it up by name and exact parameter types. Parameter names (`commandBuffer`, `viewport`, `frameIndex`, `bucket`) are part of the Harmony contract for the postfix.

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
    public static void Remove(Harmony harmony)
    {
        Ready = false;
        SphinxSubmod.WaitForPhysics();
        SphinxPhysics.ClearAll();
        SphinxPhysicsPatches.Remove(harmony);
        harmony.Unpatch(Prepare(), AccessTools.Method(typeof(SphinxPatches), nameof(AfterPrepare)));
        harmony.Unpatch(Color(), AccessTools.Method(typeof(SphinxPatches), nameof(AfterColor)));
        harmony.Unpatch(Prepass(), AccessTools.Method(typeof(SphinxPatches), nameof(AfterPrepass)));
    }
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

2. **Verify layouts before allocating.** The shaders read a 32-byte vertex (position at 0, normal at 12, uv at 24), a 24-byte `PerDrawData` of six `int` texture indices, and a 64-byte matrix. Sphinx asserts every offset in the constructor and throws if the game changed it:

   ```csharp
// sphinx.lib/StaticModelResources.cs L51-L61
if (Marshal.SizeOf<StaticVertex>() != 32 || Marshal.SizeOf<StaticObjectModel.PerDrawData>() != 24 || Marshal.SizeOf<float4x4>() != 64 ||
    Marshal.OffsetOf<StaticVertex>(nameof(StaticVertex.Position)).ToInt32() != 0 ||
    Marshal.OffsetOf<StaticVertex>(nameof(StaticVertex.Normal)).ToInt32() != 12 ||
    Marshal.OffsetOf<StaticVertex>(nameof(StaticVertex.Uv)).ToInt32() != 24 ||
    Marshal.OffsetOf<StaticObjectModel.PerDrawData>(nameof(StaticObjectModel.PerDrawData.DiffuseTextureIndex)).ToInt32() != 0 ||
    Marshal.OffsetOf<StaticObjectModel.PerDrawData>(nameof(StaticObjectModel.PerDrawData.NormalTextureIndex)).ToInt32() != 4 ||
    Marshal.OffsetOf<StaticObjectModel.PerDrawData>(nameof(StaticObjectModel.PerDrawData.PbrTextureIndex)).ToInt32() != 8 ||
    Marshal.OffsetOf<StaticObjectModel.PerDrawData>(nameof(StaticObjectModel.PerDrawData.EmissiveTextureIndex)).ToInt32() != 12 ||
    Marshal.OffsetOf<StaticObjectModel.PerDrawData>(nameof(StaticObjectModel.PerDrawData.TfiTextureIndex)).ToInt32() != 16 ||
    Marshal.OffsetOf<StaticObjectModel.PerDrawData>(nameof(StaticObjectModel.PerDrawData.AlphaTextureIndex)).ToInt32() != 20)
    throw new InvalidOperationException("The game's static-object shader layout changed.");
```

3. **Allocate private resources.** Per primitive: device-local vertex, index and 24-byte material buffers and a set allocated from `StaticObjectRenderer.PerDrawDataDescriptorSetLayout` (binding 0 the material record, binding 1 a sampler). One host-visible, host-coherent buffer of matrices, one slice per `MaxFramesInFlight * ViewportRegistry.MAX_VIEWPORTS` slot, with the stride aligned to `KSA.Rendering.Utils.MinStorageBufferOffsetAlignment`, each slice bound in a `PerInstanceDataDescriptorSetLayout` set:

   ```csharp
// sphinx.lib/StaticModelResources.cs L46-L49, L70-L94
        Owner = Program.GetRenderer();
        _frames = Owner.MaxFramesInFlight;
        _stride = (int)ByteSize.Of<float4x4>().AlignTo(KSA.Rendering.Utils.MinStorageBufferOffsetAlignment);
        _instanceSets = new VkDescriptorSet[_frames * ViewportRegistry.MAX_VIEWPORTS];
// ...
            VkDescriptorPoolSize[] sizes =
            [
                new() { Type = VkDescriptorType.StorageBuffer, DescriptorCount = count + _instanceSets.Length },
                new() { Type = VkDescriptorType.Sampler, DescriptorCount = count }
            ];
            _pool = Owner.Device.CreateDescriptorPool(new DescriptorPoolEx.CreateInfo { MaxSets = count + _instanceSets.Length, PoolSizes = sizes }, null);
            var sampler = new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Linear, MinFilter = VkFilter.Linear, MipmapMode = VkSamplerMipmapMode.Linear,
                AddressModeU = VkSamplerAddressMode.Repeat, AddressModeV = VkSamplerAddressMode.Repeat,
                AddressModeW = VkSamplerAddressMode.Repeat, MinLod = 0, MaxLod = 16, MaxAnisotropy = 1
            };
            _sampler = Owner.Device.CreateSampler(in sampler, null);
            _instances = Owner.Allocator.CreateBuffer(new BufferEx.CreateInfo
            {
                Name = "Sphinx instance matrices", BufferSize = new ByteSize((uint)(_stride * _instanceSets.Length)),
                BufferUsage = VkBufferUsageFlags.StorageBufferBit,
                AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit
            });
            _mapping = _instances.Value.Map();
            for (int i = 0; i < _instanceSets.Length; i++)
            {
                _instanceSets[i] = Owner.Device.AllocateDescriptorSet(_pool, StaticObjectRenderer.PerInstanceDataDescriptorSetLayout);
                WriteBuffer(_instanceSets[i], _instances.Value, new ByteSize((uint)(i * _stride)), ByteSize.Of<float4x4>());
            }
```

   Uploads go through a private command buffer with explicit barriers and a submit-and-wait (`AssetUploadSubmission`), so they are finished before the first draw.

4. **Write this frame's matrix per viewport** from the `UpdateRenderData` postfix. The matrix is camera-relative (ego) for `viewport.GetCamera()`, computed in double precision and packed to `float4x4`:

   ```csharp
// sphinx.lib/SphinxEntry.cs L21-L27
public float4x4 Matrix(Camera camera)
{
    var local = PlacementMath.GroundedLocal(Model.Min, Model.Max, Vector(Scale), Vector(Rotation), Vector(Offset));
    var matrix = new double4x4(local.M11,local.M12,local.M13,local.M14,local.M21,local.M22,local.M23,local.M24,
        local.M31,local.M32,local.M33,local.M34,local.M41,local.M42,local.M43,local.M44);
    return float4x4.Pack(matrix * GroundPlacement.Frame(Anchor, Align, camera));
}
```

   `Prepare` writes it into slice `viewport.ShaderSlot * frames + frame` and stamps the slice with `Renderer.FrameCount`; `Record` draws only slices stamped this frame, so a viewport that was not prepared never draws last frame's matrix:

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

5. **Filter in the callers.** Draw only for the celestial the viewport's camera is near, and skip the editor. For the blended bucket sort entries back to front:

   ```csharp
// sphinx.lib/SphinxSubmod.cs L85-L106
internal void Prepare(IViewport viewport, int frame)
{
    if (_disposed || Program.EditorFlag) return;
    var camera = viewport.GetCamera();
    foreach (var entry in _entries)
    {
        if (!entry.Visible || !ReferenceEquals(entry.Anchor.Body, camera.NearbyCelestial)) continue;
        try { entry.Model.Prepare(viewport, frame, entry.Matrix(camera)); }
        catch (Exception ex) { entry.Visible = false; _pending.Enqueue(() => SphinxPhysics.Detach(entry)); _status = $"#{entry.Id} hidden: {ex.Message}"; Console.WriteLine($"sphinx: {ex}"); }
    }
}
internal void Record(CommandBuffer command, IViewport viewport, int frame, bool prepass, bool alpha)
{
    if (_disposed || Program.EditorFlag) return;
    var camera = viewport.GetCamera();
    var entries = _entries.Where(e => e.Visible && ReferenceEquals(e.Anchor.Body, camera.NearbyCelestial));
    if (alpha) entries = entries.OrderByDescending(e =>
        (camera.GetPositionEgo(e.Anchor.Body) + e.Anchor.PositionCcf.Transform(e.Anchor.Body.GetCcf2Cce())).LengthSquared());
    foreach (var entry in entries)
        try { entry.Model.Record(command, viewport, frame, prepass, alpha); }
        catch (Exception ex) { entry.Visible = false; _pending.Enqueue(() => SphinxPhysics.Detach(entry)); _status = $"#{entry.Id} hidden: {ex.Message}"; Console.WriteLine($"sphinx: {ex}"); }
}
```

6. **Tear down after a wait.** `Dispose` must call `Device.WaitIdle()` before freeing buffers; see [free Vulkan resources safely](../../vulkan-passes/free-vulkan-resources-safely/overview.md).

## Complete example

| File | Role |
|---|---|
| [sphinx.lib/SphinxPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxPatches.cs) | The three postfixes, `Ready` flag, apply and remove |
| [sphinx.lib/StaticModelResources.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticModelResources.cs) | Buffers, descriptor sets, matrix slices, `Prepare`, `Record`, `Dispose` |
| [sphinx.lib/SphinxSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxSubmod.cs) | Per-viewport filtering, blended sort, error-to-hidden handling |
| [sphinx.lib/SphinxEntry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxEntry.cs) | Model matrix per camera |
| [scope/statics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/statics.md) | Authors' contract for sets, bindings and matrix convention |

## Variations and gotchas

- **Fragile contract.** The postfix relies on what the colour method binds. The authors re-read the method body, not just the signature, on every game update, because if a future build stopped binding full lighting state when there are no stock statics, your draw would use stale or missing sets.
- **Terrain bucket.** `OpaqueTerrain` binds a different pipeline; skip it (the filter above does).
- **Prepass is main viewport only.** Secondary viewports get no prepass for statics, which matches the stock behaviour; the blended bucket has no prepass at all because the stock normal prepass ignores alpha.
- **Renderer rebuilds.** After a resize or setting change `Program.GetRenderer()` is a new object; `Record` draws nothing and `Prepare` throws, which hides the entry. Re-create the resources, as covered in [handle renderer rebuilds](../../vulkan-passes/handle-renderer-rebuilds/overview.md).
- **Direct draws have `gl_DrawID == 0`.** Give each primitive its own set pointing at exactly one material record, as above.
- **Not in SuperMesh or `StaticObjectModel.Instances`.** Nothing is added to the shared pools, so stock statics are untouched and removing your mesh frees only your memory.
- **Compare with approach 1** if you do not need custom buffers: [`StaticMeshRenderable`](draw-mod-meshes-with-the-engine-renderers-1.md) gives lit, shadow-casting meshes with no Vulkan code.

## See also

- [Overview](overview.md)
- [Approach 1](draw-mod-meshes-with-the-engine-renderers-1.md), [approach 3](draw-mod-meshes-with-the-engine-renderers-3.md), [approach 4](draw-mod-meshes-with-the-engine-renderers-4.md)
- [Static-object render hooks](../../../../mods/unscience/features/sphinx-static-object-render-hooks.md)
- [Insert a custom Vulkan pass into KSA's frame](../../vulkan-passes/insert-a-custom-pass-into-render-game/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
