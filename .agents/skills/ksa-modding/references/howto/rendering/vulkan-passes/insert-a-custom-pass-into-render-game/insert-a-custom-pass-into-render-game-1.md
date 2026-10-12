---
type: How-To
title: Postfix RenderTarget.ResolveAttachments for a post-resolve pass
description: Record a depth-aware pass into the resolved main colour image after the gizmo pass by postfixing the final RenderTarget.ResolveAttachments call
category: rendering
group: vulkan-passes
topic: insert-a-custom-pass-into-render-game
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/graffiti-projected-decal-pass, mods/gatos/features/sticker-projected-decals]
techniques: [harmony-postfix, vulkan-pipeline, glsl-shader]
hooks: [starmap StarMapBeforeGui, harmony-postfix KSA.Rendering.RenderTarget.ResolveAttachments]
lifecycle: [frame:draw-ui, frame:render, render:grid]
game_types: [KSA.Rendering.RenderTarget, KSA.Program, KSA.GridPass, KSA.IViewport]
tags: [harmony, render-pass, resolve, depth, post-resolve, grid-pass]
---

Outcome: your code records an extra dynamic-rendering pass into the main scene colour image at the
one moment in `RenderGame` where the resolved single-sample colour and depth are both current and not bound
as attachments: after gizmos, before the map grid, gauges and the final composite. Depth is sampleable,
so the pass can reconstruct world positions (decals, depth-aware rings). Prerequisites: StarMap,
Harmony, and a Vulkan pipeline for the pass (see [build a Vulkan pipeline for the scene target](../build-a-vulkan-pipeline-for-the-scene-target/overview.md)).
Demonstrated by unscience Graffiti ([Projected PNG decal pass recorded after the main-view resolve](../../../../mods/unscience/features/graffiti-projected-decal-pass.md)) and gatOS stickers
([Projected sticker decals](../../../../mods/gatos/features/sticker-projected-decals.md)).

## Background: how the game does it

`RenderTarget.ResolveAttachments(CommandBuffer, bool inResolveDepth = true)`
[current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L315-L342) resolves the MSAA colour (and depth when asked) by opening and
closing an empty rendering scope, and only does anything when the target is multisampled. A postfix on it
still fires at every MSAA setting. `Program.RenderGame` calls it twice for the main view: once after
translucency with `inResolveDepth: false` (colour only, before underwater composition and bloom), and once
after `GizmoPass.Run` with the default `true`, immediately before `GridPass.Run`
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4927-L4936).[^ksa-program-cs]

```csharp
// current/decomp/KSA/Program.cs L4927-L4936
if (DrawUI)
{
	OrbitLinePass.Run(commandBuffer2, RenderedViewport, resourceFrameIndex);
}
GizmoPass.Run(commandBuffer2, RenderedViewport, resourceFrameIndex);
RenderedViewport.OffscreenTarget.ResolveAttachments(commandBuffer2);
if (ShowMapGridFlag(RenderedViewport))
{
	GridPass.Run(commandBuffer2, RenderedViewport, resourceFrameIndex);
}
```

The game's own `GridPass` draws in exactly this window, so it is the model for barriers and pipeline state.

## Lifecycle and timing

| Phase | `RenderGame` step | Relevance |
|---|---|---|
| `render:gizmos` | 19 `GizmoPass.Run`, then this resolve | Gizmos are already in the colour you read and write |
| **your pass** | postfix of the final `ResolveAttachments` | Recorded into the frame's single command buffer on the main thread; nothing is submitted until `PostRender` |
| `render:grid` | 20 `GridPass.Run` (map camera, `DrawUI`) | Draws on top of your pass |
| `render:gauges`, `render:aa`, `render:post-process`, `render:imgui` | 21 to 25 | Your result is part of the scene colour by then |

Pass state: every phase before it has already happened, so there is no open dynamic-rendering scope.
Per-frame inputs (matrices, entry arrays) belong in `frame:draw-ui`, in `[StarMapBeforeGui]`: the main
camera is final after the viewport frame, and the published data needs no locking because recording
happens later on the same thread. The phase list is in
[the render sequence of the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. Install the postfix after the game is loaded, binding the explicit `(CommandBuffer, bool)` overload so
   the early colour-only resolve can be ignored. Gate on the bool, on the editor flag, and on the main
   viewport: secondary and crew-portrait viewports resolve their own targets inside `RenderViewport`.

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

   gatOS does the same with a postfix that is installed only while at least one sticker exists
   ([gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerRenderPatches.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerRenderPatches.cs#L37-L86)).

2. Build the pipeline lazily on the first live use. It mirrors `GridPass.BuildPipeline`: one colour
   attachment in `Program.Instance.ColorFormat`, no depth attachment, single-sample, `CullFront` so a box still
   covers its footprint when the camera is inside it, no depth test (occlusion comes from the sampled depth), alpha-over blending.

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

3. Record the pass: barrier depth to a sampled-read layout and colour to attachment read/write with
   KSA's `BarrierBatch` presets exactly as `GridPass.Run` does, `BeginRendering` with `LoadOp.Load`, bind the
   viewport's global descriptor set, your depth set and (if you use it) the bindless table, then one
   push-constant block and indexed draw per item.

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

4. In the fragment shader, fetch depth at `gl_FragCoord`, discard reverse-Z background (`z <= 0`),
   unproject with the camera's inverse projection and inverse view into ego space, and test against your
   volume.

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

5. Expect no log lines. If the first decal fails to build, the mod logs the shaderc error once and disables
   itself; any exception while recording latches a failure flag so it does not spam every frame.

## Complete example

| File | Role |
|---|---|
| [graffiti.lib/GraffitiPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/GraffitiPatches.cs#L51-L76) | Postfix and its gates |
| [graffiti.lib/DecalRenderer.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalRenderer.cs) | Pipeline, per-frame record, teardown |
| [graffiti.lib/DecalShaders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalShaders.cs) | Runtime GLSL |
| [gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs#L436-L470) | gatOS port: barriers and per-sticker draws |

The decal use case itself is in [Project decals onto vessels, terrain and clutter](../../textures-and-decals/project-decals-onto-scene-geometry/overview.md).

## Variations and gotchas

- **Two resolves per frame.** The early `ResolveAttachments(..., inResolveDepth: false)` runs with stale depth;
  filtering on the bool keeps one pass per frame.
- **Main viewport only.** Compare `__instance` with `Program.OffscreenTarget` and `Program.RenderedViewport`
  with `Program.MainViewport`; the editor resolves the same target through the main viewport, so also check
  `Program.EditorFlag`.
- **Header drift is a runtime break.** Runtime-compiled GLSL includes the game's `Common/*.glsl`; a renamed field
  fails at shaderc compile and should disable the feature, not crash the frame.
- **Descriptor migration.** In this build `GlobalShaderBindings.DescriptorSet` takes the viewport's `ShaderSlot` and is bound with no dynamic offsets.
- **CMAA2.** With CMAA2 selected the composite blits a tonemapped LDR target, so edits to the offscreen colour made
  after step 22 are not shown (see the notes for render mods in the lifecycle reference); your pass is
  earlier than that, so it is unaffected.
- **Teardown.** Destroy the pipeline only after `WaitIdle`, and do not touch the device at game exit
  (`[StarMapUnload]` runs after the device is destroyed; see [free mod-owned Vulkan resources safely](../free-vulkan-resources-safely/overview.md)).

## See also

- [Overview](overview.md)
- [Approach 2: RenderMainPass postfix](insert-a-custom-pass-into-render-game-2.md)
- [Approach 3: RenderTranslucencyPass postfix](insert-a-custom-pass-into-render-game-3.md)
- [Approach 4: SunbloomRenderer.Render prefix](insert-a-custom-pass-into-render-game-4.md)
- [Approach 5: RenderGame transpiler](insert-a-custom-pass-into-render-game-5.md)
- [Projected PNG decal pass recorded after the main-view resolve](../../../../mods/unscience/features/graffiti-projected-decal-pass.md), [Projected sticker decals](../../../../mods/gatos/features/sticker-projected-decals.md)
- [Project decals onto vessels, terrain and clutter](../../textures-and-decals/project-decals-onto-scene-geometry/overview.md), [Capture rendered frames from KSA](../capture-rendered-frames/overview.md)

[^ksa-program-cs]: KSA.Program (decompiled, build 2026.10.10.5554)
