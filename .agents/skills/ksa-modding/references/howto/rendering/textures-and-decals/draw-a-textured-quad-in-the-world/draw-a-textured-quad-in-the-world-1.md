---
type: How-To
title: Opaque pixel quads with stock UnlitMesh shaders inside the offscreen scene pass
description: Draw a sprite as one small quad per opaque texel with the stock unlit shaders from a RenderMainPass postfix, with the ego matrix computed per frame and the pipeline built lazily
category: rendering
group: textures-and-decals
topic: draw-a-textured-quad-in-the-world
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/unscience/features/thug-life-textured-quad-render-pass
  - mods/unscience/features/thug-life-procedural-texture
techniques: [vulkan-pipeline, harmony-postfix]
hooks:
  - harmony-postfix KSA.SuperMeshRenderSystem.RenderMainPass
  - starmap StarMapBeforeGui
  - starmap StarMapAfterGui
  - starmap StarMapAllModsLoaded
lifecycle: [frame:render, render:scene-opaque, frame:draw-ui]
game_types: [KSA.SuperMeshRenderSystem, KSA.Program, KSA.Part, RenderCore.SimpleVkTexture]
tags: [quad, sprite, unlit, part-anchor, render-main-pass, pixel-art]
---

Outcome: a flat pixel-art sprite attached to any part or sub-part of any vessel (or an EVA kitten's face), drawn inside the game's opaque scene pass so it is depth-tested, anti-aliased and lit by nothing. Because the stock unlit fragment shader always writes alpha 1, transparency is made from geometry: the sprite is emitted as one small quad per opaque texel.
Demonstrated by unscience Thug Life ([Draw a part-anchored textured quad inside the scene's opaque pass](../../../../mods/unscience/features/thug-life-textured-quad-render-pass.md), [Upload a procedural pixel-art texture and cut it out with geometry](../../../../mods/unscience/features/thug-life-procedural-texture.md)). Prerequisites: StarMap, Harmony. The pass hook is [insert a custom Vulkan pass](../../vulkan-passes/insert-a-custom-pass-into-render-game/overview.md) approach 2; the pipeline is [build a Vulkan pipeline for the scene target](../../vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/overview.md) approach 1; the texture is [upload an image](../upload-an-image-as-a-vulkan-texture/overview.md) approach 3.

## Background: how the game does it

`UnlitMesh.vert` takes `inPos` and `inUV` and a push-constant `worldViewProjMatrix` and nothing else, so any mod can reuse it for a textured mesh; `UnlitMesh.frag` writes `vec4(sampledColor, 1.0)` after a `gammaToLinear()` decode. The scene scope is opened by `RenderGame` and `SuperMeshRenderSystem.RenderMainPass(IViewport, CommandBuffer)` [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L388) is called inside it
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4837-L4848). A part's ego transform comes from `Vehicle.GetMatrixAsmb2Ego(camera)` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270) with `Part.PositionEgo(in vehMat)` and `Part.Asmb2Ego(vehicle.Asmb2Ego)`. Row-vector convention: `mvp = model * viewProjection`.

## Lifecycle and timing

| Step | Where |
|---|---|
| Create the manager (no GPU) | `StarMapAllModsLoaded` (`startup:all-mods-loaded`) |
| First `Add` builds texture, geometry and pipeline | `frame:draw-ui` (`StarMapAfterGui`), because `Program.OffscreenTarget` does not exist at load |
| Slide animation | `[StarMapBeforeGui]` |
| Record the draw | `frame:render`, `render:scene-opaque`: postfix on `RenderMainPass`, once per rendered viewport |

Entries are edited in ImGui on the same thread as the recording, so no locking is needed. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Texture and geometry.** Build the RGBA texture from the character pattern, a nearest/clamp sampler, and a vertex/index buffer with four vertices and six `ushort` indices per non-transparent texel (rows flipped so row 0 is the top; UVs inset by 0.001 of a texel):

   ```csharp
   // thug-life.lib/ThugLifeQuadRenderer.cs L175-L206
   for (int row = 0; row < h; row++)
   {
       string rowStr = ThugLifeTexturePattern.Rows[row];
       for (int col = 0; col < w; col++)
       {
           if (rowStr[col] == '.') continue; // transparent — skip

           float x0 = -0.5f + (float)col / w;
           float x1 = -0.5f + (float)(col + 1) / w;
           // Flip Y so pattern row 0 maps to the top of the quad (+Y) and row N-1 to the bottom.
           float y1 = 0.5f - (float)row / h;
           float y0 = 0.5f - (float)(row + 1) / h;

           float u0 = ((float)col + uvInset) / w;
           float u1 = ((float)(col + 1) - uvInset) / w;
           float v0 = ((float)row + uvInset) / h;
           float v1 = ((float)(row + 1) - uvInset) / h;

           ushort baseIdx = (ushort)verts.Count;
           verts.Add(new QuadVertex { Pos = new float3(x0, y0, 0f), Uv = new float2(u0, v1) });
           verts.Add(new QuadVertex { Pos = new float3(x1, y0, 0f), Uv = new float2(u1, v1) });
           verts.Add(new QuadVertex { Pos = new float3(x1, y1, 0f), Uv = new float2(u1, v0) });
           verts.Add(new QuadVertex { Pos = new float3(x0, y1, 0f), Uv = new float2(u0, v0) });

           indices.Add((ushort)(baseIdx + 0));
           indices.Add((ushort)(baseIdx + 1));
           indices.Add((ushort)(baseIdx + 2));
           indices.Add((ushort)(baseIdx + 0));
           indices.Add((ushort)(baseIdx + 2));
           indices.Add((ushort)(baseIdx + 3));
       }
   }
   ```

2. **Pipeline** from the stock `UnlitMeshVert`/`UnlitMeshFrag`, `CullNone`, reverse-Z depth test and write, then `Program.OffscreenTarget.SetupGraphicsPipeline(ref info)` immediately before creation:

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

3. **Postfix `RenderMainPass`** (binding only `commandBuffer`), gated on an `Active` flag and wrapped in `try`/`catch` that clears `Active` so a failing draw cannot spam:

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

4. **Per entry**, compute the model in ego space every draw from live state (`scale(width, height, 1) * rotX * rotY * rotZ * translate(position) * partEgo`), multiply by `camera.MVP.viewProjection` of `Program.GetRenderCamera()` (the viewport being recorded), push it and `DrawIndexed`:

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

5. **Create lazily on the first `Add`:**

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

## Complete example

| File | Role |
|---|---|
| [thug-life.lib/ThugLifeRenderPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeRenderPatches.cs) | Postfix |
| [thug-life.lib/ThugLifeQuadRenderer.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeQuadRenderer.cs) | Pipeline, geometry, draw |
| [thug-life.lib/ThugLifeTextureFactory.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeTextureFactory.cs) | Texture and sampler |
| [thug-life.lib/ThugLifeRenderManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeRenderManager.cs) | Entries, lazy bring-up |

## Variations and gotchas

- **Use the render camera, not the main camera:** crew-portrait viewports are always visible and run `RenderMainPass` with their own cameras.
- **Cap:** `ushort` indices limit the sprite to 16,384 opaque texels.
- **A renderer rebuild with a different MSAA setting** is not handled; the pipeline would keep the old sample count.
- **Teardown without a wait** acts on a dead device at game exit; destroy after `WaitIdle` and never from `[StarMapUnload]`.
- The hook also fires in the vehicle editor's render path; entries are not filtered by scene.

## See also

- [Overview](overview.md)
- [Approach 2: single textured quad with its own pipeline setup](draw-a-textured-quad-in-the-world-2.md)
- [Approach 3: premultiplied-alpha translucent quad](draw-a-textured-quad-in-the-world-3.md)
- [Draw a part-anchored textured quad inside the scene's opaque pass](../../../../mods/unscience/features/thug-life-textured-quad-render-pass.md), [Upload a procedural pixel-art texture and cut it out with geometry](../../../../mods/unscience/features/thug-life-procedural-texture.md)
- [Insert a custom Vulkan pass into KSA's frame](../../vulkan-passes/insert-a-custom-pass-into-render-game/overview.md), [Build a Vulkan pipeline compatible with KSA's scene render target](../../vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/overview.md)
