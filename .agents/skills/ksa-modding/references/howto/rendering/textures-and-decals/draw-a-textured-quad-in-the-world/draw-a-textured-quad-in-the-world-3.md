---
type: How-To
title: Premultiplied-alpha translucent quad after the translucency pass
description: Draw a translucent texture on a part-anchored or camera-pinned quad with a custom premultiplied-alpha shader after atmosphere and clouds, with click focus by ray picking
category: rendering
group: textures-and-decals
topic: draw-a-textured-quad-in-the-world
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/purrtty/features/world-space-quad-pipeline-custom-shader
  - mods/purrtty/features/draw-after-translucency-pass-postfix
  - mods/purrtty/features/anchor-quad-to-part-or-camera
techniques: [vulkan-pipeline, harmony-postfix]
hooks: [harmony-postfix KSA.SuperMeshRenderSystem.RenderTranslucencyPass, starmap StarMapAfterGui]
lifecycle: [frame:render, render:translucency, frame:draw-ui]
game_types: [KSA.SuperMeshRenderSystem, KSA.Program, KSA.Part, RenderCore.SimpleVkTexture]
tags: [quad, premultiplied-alpha, translucency, billboard, part-anchor, ray-pick]
---

Outcome: a translucent image (an in-world terminal) blended over the scene, hidden behind opaque parts and terrain, surviving the atmosphere, cloud and ocean passes, anchored either to a part of the rocket or pinned to the camera like a HUD billboard. The pieces: a custom fragment shader that keeps premultiplied alpha, an inline premultiplied blend state, a postfix on `RenderTranslucencyPass` that reopens a rendering scope, and a part or view-space
model matrix. Demonstrated by purrTTY ([World-space textured quad pipeline with a runtime-compiled shader](../../../../mods/purrtty/features/world-space-quad-pipeline-custom-shader.md), [Injecting world-space draws after KSA's translucency pass](../../../../mods/purrtty/features/draw-after-translucency-pass-postfix.md), [Anchoring a world-space quad to a part or to the camera](../../../../mods/purrtty/features/anchor-quad-to-part-or-camera.md)). Prerequisites: StarMap, Harmony. The texture is produced as in [render ImGui into a texture](../render-imgui-into-a-texture/overview.md).

## Background: how the game does it

An earlier purrTTY postfixed `RenderMainPass`; atmosphere, clouds and ocean then overwrote the quad wherever it overlapped the planet. The translucency stage ([current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L459) with the call in `RenderGame` at [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4893-L4904)) runs after them, so a postfix there composites last. A postfix cannot append to the scope the original closed, so it
reopens a scope over the same attachments (see [insert a custom Vulkan pass](../../vulkan-passes/insert-a-custom-pass-into-render-game/overview.md) approach 3). The pipeline is [build a Vulkan pipeline for the scene target](../../vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/overview.md) approach 2.

## Lifecycle and timing

- Pipelines and buffers are created on the main thread in `frame:draw-ui` when the first instance is created, because `Program.OffscreenTarget` is not available at `StarMapAllModsLoaded`.
- The model is computed when the quad is recorded, inside the `RenderTranslucencyPass` postfix (`frame:render`, `render:translucency`), from vehicle and part state after this frame's `Apply*Solvers` and the camera updated in `frame:viewports`, so the panel moves in lockstep with the part. Only reads happen, so it is safe while the next physics step runs on workers.
- The texture it samples was last re-rendered in a `StarMapAfterGui` call; picking computes the same transform during `frame:draw-ui`.

See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Custom fragment shader** (the stock one discards texture alpha and gamma-decodes): un-premultiply, apply the same `pow(2.2)` decode, re-premultiply. A premultiplied blend state (`One` / `OneMinusSrcAlpha`) avoids dark fringes.

   ```csharp
   // purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs L68-L78
   private const string QuadFragGlsl =
       "#version 450\n" +
       "layout(location = 0) in vec2 inUv;\n" +
       "layout(location = 0) out vec4 outColor;\n" +
       "layout(set = 0, binding = 0) uniform sampler2D samplerColorMap;\n" +
       "void main()\n" +
       "{\n" +
       "    vec4 t = texture(samplerColorMap, inUv);\n" +
       "    vec3 straight = (t.a > 0.0) ? (t.rgb / t.a) : vec3(0.0);\n" +
       "    vec3 lin = pow(straight, vec3(2.2));\n" +
       "    outColor = vec4(lin * t.a, t.a);\n" +
   ```

2. **Postfix `RenderTranslucencyPass`:** barriers through the tracked-state helper, reopen a scope with `LoadOp.Load` over the viewport's colour and depth attachments, `RecordDrawAll`, `EndRendering` in `finally`.

   ```csharp
   // purrTTY.GameMod/InWorld/Patches/RenderTranslucencyPassPatch.cs L110-L146
   commandBuffer.PipelineBarrier2(colorImage, ImageBarrierInfo.Presets.ColorAttachmentReadWrite);
   commandBuffer.PipelineBarrier2(depthImage, ImageBarrierInfo.Presets.DepthStencilAttachmentReadWrite);

   var colorAttachment = new VkRenderingAttachmentInfo
   {
       ImageLayout = VkImageLayout.ColorAttachmentOptimal,
       ImageView   = colorImage.ImageView,
       ResolveMode = VkResolveModeFlags.None,
       LoadOp      = VkAttachmentLoadOp.Load,
       StoreOp     = VkAttachmentStoreOp.Store,
   };
   var depthAttachment = new VkRenderingAttachmentInfo
   {
       ImageLayout = VkImageLayout.DepthStencilAttachmentOptimal,
       ImageView   = depthImage.ImageView,
       ResolveMode = VkResolveModeFlags.None,
       LoadOp      = VkAttachmentLoadOp.Load,
       StoreOp     = VkAttachmentStoreOp.Store,
   };
   var renderingInfo = new VkRenderingInfo
   {
       RenderArea          = new VkRect2D(viewport.Size.X, viewport.Size.Y),
       LayerCount          = 1,
       ColorAttachmentCount = 1,
       ColorAttachments    = &colorAttachment,
       DepthAttachment     = &depthAttachment,
   };

   commandBuffer.BeginRendering(in renderingInfo);
   try
   {
       InWorldTerminalManager.Instance?.RecordDrawAll(commandBuffer);
   }
   finally
   {
       commandBuffer.EndRendering();
   }
   ```

3. **Part anchor.** Take the part's ego rotation and position separately (so the part's own scale does not leak into the panel size) and compose `model = Scale(width, height, 1) * userRot * T(userOffset) * partEgo`:

   ```csharp
   // purrTTY.GameMod/InWorld/Display/InWorldQuad.cs L337-L364
   private bool ComputePartTransform(Camera camera, Vehicle vehicle, Part part, out float4x4 model)
   {
       // Pull the part's ego-space rotation + position separately rather than its
       // combined MatrixAsmb2Ego: the combined matrix bakes in the part's own scale,
       // which we exclude so Width/Height are the sole size source. The transform flows
       // through whichever vehicle currently owns the part.
       double4x4 vehMat    = vehicle.GetMatrixAsmb2Ego(camera);
       double3   partPos   = part.PositionEgo(in vehMat);
       doubleQuat partRotD = part.Asmb2Ego(vehicle.Asmb2Ego);

       float4x4 partRotMat   = float4x4.CreateFromQuaternion(floatQuat.Pack(in partRotD));
       float4x4 partTransMat = float4x4.CreateTranslation(float3.Pack(in partPos));
       // Row-vector convention (mvp = model * viewProjection): rotate first, then
       // translate — places verts at the part's origin in ego space.
       float4x4 partEgo = partRotMat * partTransMat;

       const float deg2rad = MathF.PI / 180f;
       float4x4 userRot = float4x4.CreateRotationX(_settings.PartRotationX * deg2rad)
                        * float4x4.CreateRotationY(_settings.PartRotationY * deg2rad)
                        * float4x4.CreateRotationZ(_settings.PartRotationZ * deg2rad);
       float4x4 userTrans = float4x4.CreateTranslation(
           new float3(_settings.PartOffsetX, _settings.PartOffsetY, _settings.PartOffsetZ));
       float4x4 scaleMat = float4x4.CreateScale(_settings.PartWidthMeters, _settings.PartHeightMeters, 1f);

       // v_local → scale → userRot → userTrans → partEgo → ego
       model = scaleMat * userRot * userTrans * partEgo;
       return true;
   }
   ```

4. **Follow the part through staging.** Cache the `Part` object and look up the vehicle that owns it each frame by reference identity (decoupling and docking move the same instance into the new vehicle). An unknown vehicle id falls back to the controlled vehicle.

5. **Camera anchor (billboard).** `model = Scale * userRot * T(offsetX, offsetY, -distance)` in view space, drawn with `model * camera.MVP.projection` (no view matrix); an option selects the no-depth-test pipeline so it is always on top.

   ```csharp
   // purrTTY.GameMod/InWorld/Display/InWorldQuad.cs L366-L392
   private bool TryComputeBillboardModel(out float4x4 model, out bool useNoDepth)
   {
       // View space: the camera sits at the origin looking down its forward axis.
       // Place the quad `distance` in front (offset in screen X/Y), so mvp =
       // model * projection (skip view) keeps it pinned in the camera's frame.
       //
       // NOTE: the forward-axis sign (-distance) is the single most likely first-run
       // surprise — if the panel renders behind the camera, flip the sign.
       float4x4 scaleMat = float4x4.CreateScale(_settings.BillboardWidthMeters, _settings.BillboardHeightMeters, 1f);

       // User rotation about the panel's own centre (degrees), applied before it is
       // pushed to `distance` so the panel pivots in place — projection then renders
       // the tilt as perspective distortion. Mirrors the part-mode user rotation.
       const float deg2rad = MathF.PI / 180f;
       float4x4 userRot = float4x4.CreateRotationX(_settings.BillboardRotationX * deg2rad)
                        * float4x4.CreateRotationY(_settings.BillboardRotationY * deg2rad)
                        * float4x4.CreateRotationZ(_settings.BillboardRotationZ * deg2rad);

       float4x4 placeMat = float4x4.CreateTranslation(
           new float3(_settings.BillboardOffsetX, _settings.BillboardOffsetY, -_settings.BillboardDistance));

       // v_local → scale → userRot (about centre) → place (offset + forward distance).
       model = scaleMat * userRot * placeMat;
       useNoDepth = _settings.BillboardAlwaysOnTop;
       return true;
   }

   ```

6. **Click focus** by ray picking against the same model (see [render ImGui into a texture](../render-imgui-into-a-texture/overview.md) approach 3 and [Turn the mouse cursor into a world ray and pick points on a construction plane](../../../ui/input/cursor-to-world-ray/overview.md)).

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs) | Shaders, pipelines, buffers |
| [purrTTY.GameMod/InWorld/Display/InWorldQuad.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/InWorldQuad.cs) | Descriptor set, anchors, draw, raycast |
| [purrTTY.GameMod/InWorld/Patches/RenderTranslucencyPassPatch.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Patches/RenderTranslucencyPassPatch.cs) | The postfix |

## Variations and gotchas

- **Ego space is camera-relative:** compute transforms every frame from the current camera; never cache ego positions.
- **Matrices use row vectors** (`model * viewProjection`); composing in column-vector order mirrors or misplaces the quad.
- **Billboard forward is `-distance` along view Z;** the code comment flags the sign as the likely first-run surprise.
- **Secondary viewports and the editor:** the quad draws there too, always with the main camera's matrices unless you filter by viewport.
- **Part identity is per session:** only `Vehicle.Id` is reliably stable across restarts.
- **Shared pipeline never rebuilt:** changing the anti-aliasing sample count while a quad exists leaves a stale `RasterizationSamples` (inferred, not tested).

## See also

- [Overview](overview.md)
- [Approach 1: opaque pixel quads](draw-a-textured-quad-in-the-world-1.md)
- [Approach 2: single textured quad via the same hook](draw-a-textured-quad-in-the-world-2.md)
- [World-space textured quad pipeline with a runtime-compiled shader](../../../../mods/purrtty/features/world-space-quad-pipeline-custom-shader.md), [Injecting world-space draws after KSA's translucency pass](../../../../mods/purrtty/features/draw-after-translucency-pass-postfix.md), [Anchoring a world-space quad to a part or to the camera](../../../../mods/purrtty/features/anchor-quad-to-part-or-camera.md)
- [Render ImGui UI into a Vulkan texture for use in the 3D world](../render-imgui-into-a-texture/overview.md), [Insert a custom Vulkan pass into KSA's frame](../../vulkan-passes/insert-a-custom-pass-into-render-game/overview.md)
