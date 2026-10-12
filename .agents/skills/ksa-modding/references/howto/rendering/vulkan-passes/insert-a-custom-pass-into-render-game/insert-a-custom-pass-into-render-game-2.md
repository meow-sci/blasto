---
type: How-To
title: Postfix SuperMeshRenderSystem.RenderMainPass to draw inside the opaque scene pass
description: Record depth-tested draws into the already-open MSAA opaque scene pass with a postfix on SuperMeshRenderSystem.RenderMainPass
category: rendering
group: vulkan-passes
topic: insert-a-custom-pass-into-render-game
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/unscience/features/thug-life-textured-quad-render-pass
  - mods/gatos/features/thug-life-world-space-quad
techniques: [harmony-postfix, vulkan-pipeline]
hooks: [harmony-postfix KSA.SuperMeshRenderSystem.RenderMainPass]
lifecycle: [frame:render, render:scene-opaque]
game_types: [KSA.SuperMeshRenderSystem, KSA.Program, KSA.Rendering.RenderTarget, KSA.IViewport]
tags: [harmony, opaque-pass, depth-test, msaa, scene-pass]
---

Outcome: your geometry is drawn inside the offscreen scene pass the game has already opened, so it is
depth-tested against (and writes into) the scene depth, is anti-aliased with the scene, and later translucent
passes, atmosphere and plumes composite correctly over it. The hook is a postfix on
`SuperMeshRenderSystem.RenderMainPass(IViewport, CommandBuffer)`; the draws themselves need a pipeline that
matches the scene target (see [build a Vulkan pipeline for the scene target](../build-a-vulkan-pipeline-for-the-scene-target/overview.md)).
Demonstrated by unscience Thug Life ([Draw a part-anchored textured quad inside the scene's opaque pass](../../../../mods/unscience/features/thug-life-textured-quad-render-pass.md)) and its gatOS port
([World-space textured quad (thug_life)](../../../../mods/gatos/features/thug-life-world-space-quad.md)). Requires StarMap and Harmony.

## Background: how the game does it

`RenderGame` opens a dynamic-rendering scope on `Program.OffscreenTarget` (step 9 of the pass table in
[the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence)), draws the sky, planets and part models, calls
`SuperMeshRenderSystem.RenderMainPass(RenderedViewport, commandBuffer2)`, then `GizmosRenderer.Render`, and
closes the scope. `RenderMainPass(IViewport viewport, CommandBuffer commandBuffer)` is public
[current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L388) and is called from three places: `RenderViewport` for every visible
secondary viewport (camera windows and the two crew portraits), `RenderGame` for the main view, and `RenderEditor`.

```csharp
// current/decomp/KSA/Program.cs L4835-L4843
	_planetRenderer.Render(commandBuffer2, RenderedViewport, resourceFrameIndex);
}
SuperMeshRenderSystem.SetCamMatrices(_cameraData[shaderSlot].View, _cameraData[shaderSlot].Projection);
Profiler.MainThread.Begin(Profiler.Tag.RecordSuperMeshMainPass);
SuperMeshRenderSystem.RenderMainPass(RenderedViewport, commandBuffer2);
Profiler.MainThread.End();
_volumetricExhaustRenderer.RenderDebugWireFrame(commandBuffer2, RenderedViewport, resourceFrameIndex);
GizmosRenderer.Render(commandBuffer2, RenderedViewport, resourceFrameIndex);
if (IsRaytracingActive && MainViewport.Mode == CameraMode.IVA)
```

## Lifecycle and timing

| What | Where | Notes |
|---|---|---|
| Postfix records draws | `render:scene-opaque` inside `frame:render`, step 9 | The scope is open; appending draws is legal, nothing is submitted until `PostRender` |
| Entry list edits, animation | `frame:draw-ui` (`[StarMapBeforeGui]`, ImGui) | Same thread as recording, no locking |
| GPU bring-up | first user action, not at load | `Program.OffscreenTarget` is still null at `StarMapAllModsLoaded` |

The postfix runs once per call, not once per frame: secondary viewports record first with their own cameras,
then the main view, and the editor has its own call. Use `Program.GetRenderCamera()` (the camera of the viewport
being recorded) and decide per viewport whether to draw.

## Steps

1. Patch `RenderMainPass` by name with a postfix that binds only `commandBuffer`; Harmony injects by
   parameter name, so the leading `IViewport` parameter added in a later game build did not break it.
   Check an `Active` flag first and catch exceptions so a failing draw cannot spam the render loop.

   ```csharp
   // thug-life.lib/ThugLifeRenderPatches.cs L17-L55
   public static void Apply(Harmony harmony)
   {
       var original = AccessTools.Method(
           typeof(SuperMeshRenderSystem),
           nameof(SuperMeshRenderSystem.RenderMainPass));
       if (original == null)
           throw new MissingMethodException(
               typeof(SuperMeshRenderSystem).FullName,
               nameof(SuperMeshRenderSystem.RenderMainPass));

       var postfixMethod = AccessTools.Method(typeof(ThugLifeRenderPatches), nameof(RenderMainPassPostfix));
       if (postfixMethod == null)
           throw new MissingMethodException(typeof(ThugLifeRenderPatches).FullName, nameof(RenderMainPassPostfix));

       harmony.Patch(original, postfix: new HarmonyMethod(postfixMethod));
   }

   public static void Remove(Harmony harmony)
   {
       var original = AccessTools.Method(
           typeof(SuperMeshRenderSystem),
           nameof(SuperMeshRenderSystem.RenderMainPass));
       var postfix = AccessTools.Method(typeof(ThugLifeRenderPatches), nameof(RenderMainPassPostfix));
       if (original != null && postfix != null)
           harmony.Unpatch(original, postfix);
   }

   private static void RenderMainPassPostfix(CommandBuffer commandBuffer)
   {
       if (!ThugLifeRenderManager.Active) return;
       try
       {
           ThugLifeRenderManager.Instance?.RecordDraws(commandBuffer);
       }
       catch (Exception ex)
       {
           Console.WriteLine($"thug-life: render postfix error: {ex.Message}");
       }
   }
   ```

2. Create the pipeline lazily, once the render targets exist. Stock shaders (`UnlitMeshVert` and
   `UnlitMeshFrag` from `ModLibrary.Get<ShaderReference>`) give a free textured pipeline, and
   `Program.OffscreenTarget.SetupGraphicsPipeline(ref info)` stamps the dynamic-rendering formats and sample
   count immediately before `CreateGraphicsPipeline`.

   ```csharp
   // thug-life.lib/ThugLifeQuadRenderer.cs L112-L154
   var shaderRefs = new ShaderReference[]
   {
       ModLibrary.Get<ShaderReference>("UnlitMeshVert"),
       ModLibrary.Get<ShaderReference>("UnlitMeshFrag"),
   };
   var stages = RenderTechnique.CreateShaderStages(device, shaderRefs.AsSpan());

   var vertexInput = new VertexInput(1, 2)
       .AddBinding(0, ByteSize.Of<QuadVertex>(), VkVertexInputRate.Vertex)
       .AddAttribute(0, 0, VkFormat.R32G32B32SFloat, ByteSize.Zero)
       .AddAttribute(1, 0, VkFormat.R32G32SFloat, ByteSize.Of<float3>())
       .Check();

   var info = new VkGraphicsPipelineCreateInfo
   {
       Layout = layout,
       StageCount = stages.Count,
       Stages = stages,
       DynamicState = renderer.DynamicStateInfo,
       ViewportState = renderer.ViewportState,
       VertexInputState = vertexInput,
       InputAssemblyState = Presets.InputAssembly.TriangleList,
       RasterizationState = Presets.Rasterization.Fill.CullNone,
       DepthStencilState = RenderingPresets.ReverseZDepthStencil.DepthTestWrite,
       ColorBlendState = Presets.BlendState.BlendColorAlpha,
   };

   // KSA 2026.8.19.5261 migrated the main scene pass from classic Vulkan render
   // passes to dynamic rendering. Program.OffScreenPass (RenderPassState, with
   // .Pass/.SampleCount) no longer exists; the offscreen target is now
   // Program.OffscreenTarget (RenderTarget : IRenderPassInfo), and pass
   // compatibility is established by SetupGraphicsPipeline, which chains a
   // VkPipelineRenderingCreateInfo describing the colour/depth formats onto
   // pNext, sets RenderPass to VK_NULL_HANDLE, and fills in MultisampleState
   // with the target's sample count. So RenderPass/Subpass/MultisampleState must
   // NOT be set by hand here — this call supplies all three. Must stay
   // immediately before CreateGraphicsPipeline: the structures it points pNext
   // at are owned and overwritten by the RenderTarget on each call.
   // This mirrors the game's own main-pass pipelines (GenericMeshRenderer,
   // PartModelRenderer, PartModelGlass).
   Program.OffscreenTarget.SetupGraphicsPipeline(ref info);

   return device.CreateGraphicsPipeline(default(VkPipelineCache), info, null);
   ```

3. Record each draw in ego space. Take the camera from `Program.GetRenderCamera()`, rebuild the anchor's
   ego matrix every draw (`Vehicle.GetMatrixAsmb2Ego(camera)`, `Part.PositionEgo`, `Part.Asmb2Ego`), push
   the MVP, bind buffers and `DrawIndexed`.

   ```csharp
   // thug-life.lib/ThugLifeQuadRenderer.cs L252-L297
       var camera = Program.GetRenderCamera();
       if (camera == null) return;
       if (!TryComputeModelEgo(entry, camera, out float4x4 modelEgo)) return;

       float4x4 mvp = modelEgo * camera.MVP.viewProjection;

       cmd.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
       VkDescriptorSet setCopy = _descriptorSet;
       cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _pipelineLayout, 0,
           new ReadOnlySpan<VkDescriptorSet>(ref setCopy),
           default(Span<ByteSize32>));

       Program.SetViewport(cmd);
       cmd.PushConstants(_pipelineLayout, VkShaderStageFlags.VertexBit, ByteSize.Zero, mvp);

       VkBuffer vbHandle = _vb.VkBuffer;
       ByteSize64 vbOff = (ByteSize64)_vb.BindOffset;
       cmd.BindVertexBuffers(0,
           new ReadOnlySpan<VkBuffer>(ref vbHandle),
           new ReadOnlySpan<ByteSize64>(ref vbOff));
       cmd.BindIndexBuffer(_ib.VkBuffer, (ByteSize64)_ib.BindOffset, VkIndexType.UInt16);
       cmd.DrawIndexed(_indexCount, 1, 0, 0, 0);
   }

   private static bool TryComputeModelEgo(ThugLifeEntry entry, Camera camera, out float4x4 model)
   {
       model = float4x4.Identity;
       if (entry.Vehicle == null || entry.Part == null) return false;

       double4x4 vehMat = entry.Vehicle.GetMatrixAsmb2Ego(camera);
       double3 partPos = entry.Part.PositionEgo(in vehMat);
       doubleQuat partRot = entry.Part.Asmb2Ego(entry.Vehicle.Asmb2Ego);

       float4x4 partRotMat = float4x4.CreateFromQuaternion(floatQuat.Pack(in partRot));
       float4x4 partTransMat = float4x4.CreateTranslation(float3.Pack(in partPos));
       float4x4 partEgo = partRotMat * partTransMat;

       const float deg2rad = MathF.PI / 180f;
       float4x4 userRot = float4x4.CreateRotationX(entry.Rotation.X * deg2rad)
                        * float4x4.CreateRotationY(entry.Rotation.Y * deg2rad)
                        * float4x4.CreateRotationZ(entry.Rotation.Z * deg2rad);
       float4x4 userTrans = float4x4.CreateTranslation(entry.Position);
       float4x4 scaleMat = float4x4.CreateScale(entry.Width, entry.Height, 1f);

       // v_local → scale → userRot → userTrans → partEgo → ego
       model = scaleMat * userRot * userTrans * partEgo;
   ```

4. Bring the GPU objects up on the first user action (the unscience manager does it inside the first `Add`),
   because at `StarMapAllModsLoaded` the offscreen target does not exist yet.

   ```csharp
   // thug-life.lib/ThugLifeRenderManager.cs L91-L115
   private bool EnsureGpuResources()
   {
       if (!IsReady) return false;
       if (IsGpuReady) return true;

       try
       {
           var renderer = Program.GetRenderer();
           _texture = new ThugLifeTextureFactory(renderer);
           _quad = new ThugLifeQuadRenderer(renderer, _texture);
           Active = true;
           _lastError = null;
           Console.WriteLine("thug-life: render manager initialized");
           return true;
       }
       catch (Exception ex)
       {
           _lastError = $"init failed: {ex.Message}";
           Console.WriteLine($"thug-life: {_lastError}");
           _disabledDueToError = true;
           Active = false;
           DisposeGpuResources();
           return false;
       }
   }
   ```

5. On success the quad appears on the chosen part; on failure the manager records the error and disables
   itself (look for `thug-life: render postfix error` on the console).

## Complete example

| File | Role |
|---|---|
| [thug-life.lib/ThugLifeRenderPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeRenderPatches.cs) | Postfix and install/remove |
| [thug-life.lib/ThugLifeQuadRenderer.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeQuadRenderer.cs) | Pipeline, geometry, per-entry draw |
| [thug-life.lib/ThugLifeRenderManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeRenderManager.cs) | Lazy bring-up and the `Active` gate |
| [gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeQuadRenderer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeQuadRenderer.cs#L131-L168) | gatOS variant of the pipeline with a per-entry camera mask |

The quad itself is explained in [Draw a textured quad attached to a part or the camera](../../textures-and-decals/draw-a-textured-quad-in-the-world/overview.md).

## Variations and gotchas

- **Pipeline must match the target.** `SetupGraphicsPipeline` must be the last call before
  `CreateGraphicsPipeline`; the structures it chains belong to the target and are overwritten each call.
- **Rebuilds.** Build once but rebuild when the renderer rebuilds with a different MSAA sample count (see [Recreate mod render targets when KSA rebuilds its renderer](../handle-renderer-rebuilds/overview.md)); the unscience mod does not handle this.
- **Which viewport.** The two crew portraits (shown while the crew-portrait cameras setting is on, `GameSettings.ShowCrewPortraitCameras()`) and any open camera windows run `RenderMainPass` with their own cameras. Use the
  render camera, and mask by `Program.RenderedViewport` when only the main view should draw.
- **Editor.** The hook also fires from `RenderEditor`; entries are not filtered by scene unless you do it.
- **Stock unlit fragment writes alpha 1,** so transparency has to come from geometry (one quad per opaque texel).
- **Teardown without a wait is unsafe.** Destroy GPU objects only after `WaitIdle`; the gatOS sticker renderer
  later added a retire queue for exactly this (see [free mod-owned Vulkan resources safely](../free-vulkan-resources-safely/overview.md)).

## See also

- [Overview](overview.md)
- [Approach 1: ResolveAttachments postfix](insert-a-custom-pass-into-render-game-1.md)
- [Approach 3: RenderTranslucencyPass postfix](insert-a-custom-pass-into-render-game-3.md)
- [Draw a part-anchored textured quad inside the scene's opaque pass](../../../../mods/unscience/features/thug-life-textured-quad-render-pass.md), [World-space textured quad (thug_life)](../../../../mods/gatos/features/thug-life-world-space-quad.md)
- [Draw a textured quad attached to a part or the camera](../../textures-and-decals/draw-a-textured-quad-in-the-world/overview.md), [Build a Vulkan pipeline compatible with KSA's scene render target](../build-a-vulkan-pipeline-for-the-scene-target/overview.md)
