---
type: How-To
title: Unit-cube decal volumes with reverse-Z depth reconstruction
description: Draw each decal as a unit cube in the post-resolve window, reconstruct the scene position from the reverse-Z depth in the fragment shader and sample the texture through the bindless table
category: rendering
group: textures-and-decals
topic: project-decals-onto-scene-geometry
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/unscience/features/graffiti-projected-decal-pass
  - mods/unscience/features/graffiti-bindless-decal-textures
techniques: [vulkan-pipeline, harmony-postfix, glsl-shader]
hooks:
  - harmony-postfix KSA.Rendering.RenderTarget.ResolveAttachments
  - starmap StarMapBeforeGui
  - starmap StarMapAllModsLoaded
  - harmony-prefix KSA.Program.OnDrawUiConsole
lifecycle: [frame:render, render:grid]
game_types: [KSA.Rendering.RenderTarget, KSA.Program, KSA.GridPass]
tags: [decal, projected, depth, reverse-z, unit-cube, bindless]
---

Outcome: images painted onto whatever the player clicked (a hull, a deployed canopy, a kitten, the ground) that wrap curvature and relief because they are not meshes: a screen-space projected decal drawn after the main view has been resolved. Each decal rasterises a unit cube; the fragment shader rebuilds the scene position under each covered pixel from the resolved reverse-Z depth, tests it against the cube, and samples
the PNG through KSA's bindless table. Demonstrated by unscience Graffiti ([Projected PNG decal pass recorded after the main-view resolve](../../../../mods/unscience/features/graffiti-projected-decal-pass.md), [PNG decals as mod-owned slots in KSA's bindless texture table](../../../../mods/unscience/features/graffiti-bindless-decal-textures.md)). Prerequisites: StarMap, Harmony. The hook is [insert a custom Vulkan pass](../../vulkan-passes/insert-a-custom-pass-into-render-game/overview.md) approach 1; textures are [upload an image](../upload-an-image-as-a-vulkan-texture/overview.md) approach 2; anchors are
[choose a render frame and anchor effects](../../overlays/choose-a-render-frame-and-anchor-effects/overview.md) approach 6.

## Background: how the game does it

`RenderTarget.ResolveAttachments(CommandBuffer, bool inResolveDepth = true)` [current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L315) resolves MSAA colour (and depth) and is called after `GizmoPass.Run`, immediately before `GridPass.Run` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4927-L4936). After the full resolve the single-sample depth and colour are both current and not bound as attachments: the window the game's `GridPass` draws in.
The scene depth is reverse-Z (zero is the far plane and what untouched background reads as). Shaders reconstruct a position with `global.camera.inverseProjection` and `inverseView` from `Common/Camera.glsl`, included from the game's shader directory.

## Lifecycle and timing

| Step | Where |
|---|---|
| Install the postfix | `startup:all-mods-loaded` (`StarMapAllModsLoaded` then `Patcher.Patch()`) |
| Re-resolve anchors, compose matrices against the main camera, build the pipeline on first use, publish the entry array | `[StarMapBeforeGui]` (`frame:draw-ui`), or the hidden-HUD replay in `OnDrawUiConsole` |
| Record the pass | `render:grid` window: postfix of the final `ResolveAttachments`, after `GizmoPass.Run`, before `GridPass.Run` |

The main camera is final after `OnFrameViewports`, so matrices composed in `frame:draw-ui` match the camera `RenderGame` records with; command recording happens later on the same main thread, so the published `DecalEntry[]` needs no locking. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Postfix the explicit `(CommandBuffer, bool)` overload** and return unless `inResolveDepth`, no decal is live, not the editor, `__instance` is `Program.OffscreenTarget` and `Program.RenderedViewport` is `Program.MainViewport`.

   ```csharp
   // graffiti.lib/GraffitiPatches.cs L25-L40, L51-L76
   private static bool _loggedFault;

   private static MethodBase? Target() =>
       AccessTools.Method(typeof(RenderTarget), nameof(RenderTarget.ResolveAttachments), new[]
       {
           typeof(CommandBuffer), typeof(bool)
       });

   private static MethodInfo Postfix() =>
       AccessTools.Method(typeof(GraffitiPatches), nameof(AfterResolveAttachments))!;

   public static void Apply(Harmony harmony)
   {
       var original = Target();
       if (original == null)
           throw new MissingMethodException(typeof(RenderTarget).FullName,
   // ...
   private static void AfterResolveAttachments(RenderTarget __instance, CommandBuffer inCmdBuffer,
       bool inResolveDepth)
   {
       // KSA 5438 resolves color before underwater composition with depth resolution disabled.
       // The decal pass needs the final depth and therefore belongs only after the normal resolve,
       // immediately before the game's GridPass.
       if (!inResolveDepth)
           return;
       RecordPass(__instance, inCmdBuffer);
   }

   private static void RecordPass(RenderTarget __instance, CommandBuffer inCmdBuffer)
   {
       if (!GraffitiSubmod.RenderActive)
           return;
       try
       {
           if (Program.EditorFlag)
               return;
           // Main viewport only: every other viewport (crew portraits, secondary views) resolves
           // its own target with its own camera, and the decal matrices were composed against
           // the main camera this frame.
           if (!ReferenceEquals(__instance, Program.OffscreenTarget)
               || !ReferenceEquals(Program.RenderedViewport, Program.MainViewport))
               return;
           GraffitiSubmod.Instance?.RecordPass(inCmdBuffer);
   ```

2. **Pipeline (once, lazily):** dynamic rendering, one colour attachment in `Program.Instance.ColorFormat`, no depth attachment, single-sample, `CullFront` (the box still covers its footprint when the camera is inside it), no depth test (occlusion comes from the sampled depth), alpha-over blending. Layout: set 0 `GlobalShaderBindings.DescriptorSetLayout`, set 1 your depth sampler (one per frame in flight),
   set 2 the bindless library layout, plus a 112-byte push block.

   ```csharp
   // graffiti.lib/DecalRenderer.cs L242-L258
   var info = new VkGraphicsPipelineCreateInfo
   {
       Layout = layout,
       Next = &rendering,
       RenderPass = VkRenderPass.NullHandle,
       StageCount = stages.Length,
       Stages = stages.AsPointer(),
       DynamicState = renderer.DynamicStateInfo,
       ViewportState = renderer.ViewportState,
       VertexInputState = vertexInput,
       InputAssemblyState = Presets.InputAssembly.TriangleList,
       RasterizationState = Presets.Rasterization.Fill.CullFront,
       DepthStencilState = RenderingPresets.ReverseZDepthStencil.NoDepthTest,
       ColorBlendState = RenderingPresets.BlendState.BlendColorAlphaOver,
       MultisampleState = &multisample,
   };
   return device.CreateGraphicsPipeline(default(VkPipelineCache), info, null);
   ```

3. **Compile GLSL at runtime** with `ShaderModuleUtils.FromString`, passing a NUL-terminated fake path inside the directory of the shipped `GridFrag` asset (`ModLibrary.Get<ShaderReference>("GridFrag").ModPath`) as debug name, so `#include "Common/Camera.glsl"` resolves against the installed build's own headers.

4. **Record:** rewrite this frame's depth descriptor to `OffscreenTarget.DepthImage` with `Program.PointClampedSampler`; barrier depth to sampled-read and colour to attachment read/write like `GridPass.Run`; `BeginRendering` with `LoadOp.Load`; bind the viewport's global set with no dynamic offsets, your depth set and the bindless set; one `PushConstants` + `DrawIndexed(36)` per drawable decal.

   ```csharp
   // graffiti.lib/DecalRenderer.cs L396-L414
   commandBuffer.BeginRendering(in renderingInfo);
   try
   {
       commandBuffer.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
       Program.SetViewport(commandBuffer);

       // KSA 5541 allocates a global set per viewport/frame with fixed buffer offsets.
       var globalSet = GlobalShaderBindings.DescriptorSet(Program.MainViewport.ShaderSlot);
       commandBuffer.BindDescriptorSets(VkPipelineBindPoint.Graphics, _pipelineLayout, 0,
           new ReadOnlySpan<VkDescriptorSet>(ref globalSet),
           default(Span<ByteSize32>));
       commandBuffer.BindDescriptorSets(VkPipelineBindPoint.Graphics, _pipelineLayout, 1,
           new ReadOnlySpan<VkDescriptorSet>(ref depthSet), default(Span<ByteSize32>));
       if (Program.Instance?.BindlessTextures is { } bindless)
       {
           var bindlessSet = bindless.DescriptorSet;
           commandBuffer.BindDescriptorSets(VkPipelineBindPoint.Graphics, _pipelineLayout, 2,
               new ReadOnlySpan<VkDescriptorSet>(ref bindlessSet), default(Span<ByteSize32>));
       }
   ```

5. **Fragment shader:** fetch depth at `gl_FragCoord`, discard reverse-Z background, unproject into ego space, transform by the packed ego-to-decal matrix, discard outside the unit box, derive the surface normal from `dFdx`/`dFdy` of the position (before any discard), fade at grazing angles, sample `SAMPLE_TEXTURE(texId, 0, uv)` with v flipped, shade with a sun Lambert term plus ambient.

   ```csharp
   // graffiti.lib/DecalShaders.cs L76-L104
   void main()
   {
       // Screen-sized and single-sample after ResolveAttachments: exactly one texel per fragment.
       ivec2 size = textureSize(sceneDepth, 0);
       float z = texelFetch(sceneDepth, ivec2(gl_FragCoord.xy), 0).r;

       // Same convention as Camera.ScreenToEgoNearPlane: ndc = 2*p/size - 1 on BOTH axes,
       // no Y flip -- the reverse-Z projection already carries it.
       vec2 ndc = (gl_FragCoord.xy / vec2(size)) * 2.0 - 1.0;
       vec4 v = global.camera.inverseProjection * vec4(ndc, z, 1.0);
       v /= v.w;

       // The view matrix is rotation-only, so undoing it lands in ego.
       vec3 pEgo = (global.camera.inverseView * vec4(v.xyz, 1.0)).xyz;

       // The receiving surface's normal, from the reconstructed position's screen
       // derivatives. Taken BEFORE any discard: derivatives are only defined in uniform
       // control flow. (Noisy at depth discontinuities -- that is the known one-pixel edge
       // artifact of a projected decal, and the NaN-safe tests below eat it.)
       vec3 n = normalize(cross(dFdx(pEgo), dFdy(pEgo)));

       // Reverse-Z: 0 is the far plane AND what untouched background reads as. A decal has
       // nothing to stick to there.
       if (z <= 0.0) discard;

       vec4 p4 = vec4(pEgo, 1.0);
       vec3 pDec = vec3(dot(dc.e2d0, p4), dot(dc.e2d1, p4), dot(dc.e2d2, p4));
       // Negated form so a NaN coordinate (a degenerate reconstruction) discards too.
       if (!all(lessThanEqual(abs(pDec), vec3(0.5)))) discard;
   ```

6. **Fail soft:** a shaderc failure throws with the full log and disables the feature; any exception while recording latches a failure flag, clears the active flag and logs once. Tear the pipeline down after `WaitIdle` when the list empties.

## Complete example

| File | Role |
|---|---|
| [graffiti.lib/GraffitiPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/GraffitiPatches.cs) | Postfix and gates |
| [graffiti.lib/DecalRenderer.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalRenderer.cs) | Pipeline, record, teardown |
| [graffiti.lib/DecalShaders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalShaders.cs) | Runtime GLSL |
| [graffiti.lib/DecalAnchors.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalAnchors.cs) | Anchor composition |

## Variations and gotchas

- **The visible decal is surface intersect box:** too shallow crops a wide decal on a curved hull, too deep paints through thin parts.
- **Header drift is a runtime break:** the GLSL is compiled against whatever `Common/*.glsl` ships; a rename fails at shaderc compile and disables the feature.
- **Two resolves per frame** in this build; the bool filter keeps one pass.
- **Lighting:** a single Lambert sun term plus ambient, flatter than PBR hulls; main viewport and flight scene only.
- **Teardown at game exit** acts on a disposed device (`[StarMapUnload]` runs after `Program.Dispose`); see [free mod-owned Vulkan resources safely](../../vulkan-passes/free-vulkan-resources-safely/overview.md).

## See also

- [Overview](overview.md)
- [Approach 2: sticker pass after resolve](project-decals-onto-scene-geometry-2.md)
- [Approach 3: procedural bullet holes by compute](project-decals-onto-scene-geometry-3.md)
- [Approach 4: engine terrain decal modifiers](project-decals-onto-scene-geometry-4.md)
- [Projected PNG decal pass recorded after the main-view resolve](../../../../mods/unscience/features/graffiti-projected-decal-pass.md)
- [Insert a custom Vulkan pass into KSA's frame](../../vulkan-passes/insert-a-custom-pass-into-render-game/overview.md)
