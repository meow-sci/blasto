---
type: How-To
title: Sticker pass after resolve with runtime GLSL including the game library
description: Port the grid-pass window to a sticker renderer whose patch is installed only while stickers exist, with anchors from parts or latitude and longitude
category: rendering
group: textures-and-decals
topic: project-decals-onto-scene-geometry
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/sticker-projected-decals]
techniques: [vulkan-pipeline, harmony-postfix]
hooks:
  - harmony-postfix KSA.Rendering.RenderTarget.ResolveAttachments
  - harmony-prefix KSA.GameViewport.OnFrame
  - starmap StarMapBeforeGui
  - starmap StarMapAfterOnFrame
lifecycle: [frame:render, render:grid, frame:viewports, frame:draw-ui, frame:after-on-frame]
game_types: [KSA.Rendering.RenderTarget, KSA.Program, KSA.GridPass, KSA.Cursor]
tags: [decal, sticker, projected, depth, lazy-patch, runtime-glsl]
---

Outcome: uploaded images placed on the world (spray at the crosshair or place by latitude and longitude) as projected decals that wrap hull curvature, tessellated terrain and even ground clutter (which has no CPU-side transform at all), ride parts through flight and survive vessel switches, warp and planet rotation. The renderer is a close port of KSA's own map-grid pass, so it is also a reference for adding any post-resolve, depth-aware pass.
Same hook as approach 1; the differences are that the Harmony patch is installed only while stickers exist and that anchors can be geodetic. Demonstrated by gatOS ([Projected sticker decals](../../../../mods/gatos/features/sticker-projected-decals.md)). Prerequisites: StarMap, Harmony.

## Background: how the game does it

In this build `Program.RenderGame` resolves colour only after translucency (`inResolveDepth: false`), draws bloom, orbit lines and gizmos, then resolves colour and depth and runs `GridPass` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4904-L4936). After the full resolve the single-sample depth and colour are both current and not bound as attachments: the window `GridPass` draws in. `RenderTarget.ResolveAttachments`
[current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L315) is the seam; `Cursor.GetEgoRay(IViewport)` [current/decomp/KSA/Cursor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Cursor.cs#L27) unprojects the cursor.

## Lifecycle and timing

- `render:grid`: the postfix records into the frame's command buffer on the main thread during `RenderGame`, after the post-gizmo resolve and before the map grid and ImGui.
- `place` / `spray` commands execute in the Frame-phase drain (the main-viewport `GameViewport.OnFrame` prefix, `frame:viewports`). `StickerManager.Tick` runs later from `StarMapBeforeGui` (`frame:draw-ui`): re-resolve anchors and textures, recompose ego-space decal matrices against the main camera, publish an immutable entry array for the postfix, and install or remove the patch on the 0-to-1 and 1-to-0 live transitions.
- On hidden-HUD frames the tick runs from `StarMapAfterOnFrame`, after the frame rendered, so stickers can trail by one frame while the HUD is hidden (inferred).

See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Install the postfix on its own Harmony instance only while a sticker is live,** and bail unless the depth flag is true, the scene is not the editor, `__instance` is `Program.OffscreenTarget` and `Program.RenderedViewport` is `Program.MainViewport`:

   ```csharp
   // gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerRenderPatches.cs L59-L77
   private static void Postfix(RenderTarget __instance, CommandBuffer inCmdBuffer, bool __1)
   {
       if (!__1 || !StickerManager.Active)
           return;
       try
       {
           // The editor renders the SAME offscreen target through the main viewport index, so both
           // identity checks below pass in the VAB. Stickers are a flight-scene feature: a body
           // anchor still resolves in the editor and would otherwise draw over the hangar.
           if (Program.EditorFlag)
               return;
           // Main viewport only: every other viewport resolves its own target with its own camera,
           // and the decal matrices were composed against the main camera this frame.
           if (!ReferenceEquals(__instance, Program.OffscreenTarget)
               || !ReferenceEquals(Program.RenderedViewport, Program.MainViewport))
               return;
           StickerManager.Instance?.RecordPass(inCmdBuffer);
       }
       catch (Exception ex)
   ```

2. **Record the pass:** barrier depth to sampled-read and colour to attachment read/write with the game's own `BarrierBatch` presets, begin dynamic rendering on the colour image with `LoadOp.Load`, draw one unit cube per sticker with `CullFront`, no depth test and alpha-over blending.

   ```csharp
   // gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs L436-L470
   internal void RecordPass(CommandBuffer commandBuffer, ReadOnlySpan<StickerEntry> entries, bool debug)
   {
       if (_disposed || entries.Length == 0)
           return;
       if (Program.OffscreenTarget is not { } target)
           return;
       if (target.DepthImage is not { } depthImage || target.ColorImage is not { } colorImage)
           return;

       var drawable = 0;
       foreach (var entry in entries)
           if (IsDrawable(entry))
               drawable++;
       if (drawable == 0)
           return;

       UpdateDepthDescriptor(depthImage, out var depthSet);

       Span<VkImageMemoryBarrier2> barrierImages = stackalloc VkImageMemoryBarrier2[2];
       var barriers = new BarrierBatch(barrierImages);
       barriers.Add(depthImage, ImageBarrierInfo.Presets.DepthSampledReadF);
       barriers.Add(colorImage, ImageBarrierInfo.Presets.ColorAttachmentReadWrite, 0, inForceBarrier: true);
       barriers.SubmitAndFlush(commandBuffer);

       var attachment = new VkRenderingAttachmentInfo
       {
           ImageView = colorImage.ImageView,
           ImageLayout = VkImageLayout.ColorAttachmentOptimal,
           ResolveMode = VkResolveModeFlags.None,
           LoadOp = VkAttachmentLoadOp.Load,
           StoreOp = VkAttachmentStoreOp.Store,
       };
       var renderingInfo = new VkRenderingInfo
       {
           RenderArea = new VkRect2D { Extent = target.Extent },
   ```

3. **Fragment shader (GLSL in a C# string):** depth at the fragment, NDC, `inverseProjection` then `inverseView` to ego space, surface normal from screen derivatives before any discard, discard far-plane pixels and anything outside the unit box or facing away, then sample from the global bindless array and shade with a sun term:

   ```csharp
   // gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs L668-L696
   void main()
   {
       // Screen-sized and single-sample after ResolveAttachments: exactly one texel per fragment.
       ivec2 size = textureSize(sceneDepth, 0);
       float z = texelFetch(sceneDepth, ivec2(gl_FragCoord.xy), 0).r;

       // Same convention as Camera.ScreenToEgoNearPlane (KSA/Camera.cs:658-671): ndc = 2*p/size - 1
       // on BOTH axes, no Y flip -- the projection already carries it (M22 is negated in
       // ReverseDepthBufferUtils.CreatePerspectiveFieldOfViewReverseZ).
       vec2 ndc = (gl_FragCoord.xy / vec2(size)) * 2.0 - 1.0;
       vec4 v = global.camera.inverseProjection * vec4(ndc, z, 1.0);
       v /= v.w;

       // The view matrix is rotation-only (KSA/Camera.cs:482-492), so undoing it lands in ego.
       vec3 pEgo = (global.camera.inverseView * vec4(v.xyz, 1.0)).xyz;

       // The receiving surface's normal, from the reconstructed position's screen derivatives.
       // Taken BEFORE any discard: derivatives are only defined in uniform control flow, and a
       // neighbour that has already been discarded would otherwise make this undefined rather
       // than merely noisy. (Noisy it still is at a depth discontinuity -- that is the known
       // one-pixel edge artifact of a projected decal, and the NaN-safe tests below eat it.)
       vec3 n = normalize(cross(dFdx(pEgo), dFdy(pEgo)));

       // Reverse-Z: 0 is the far plane AND what untouched background reads as. A decal has
       // nothing to stick to there.
       if (z <= 0.0) discard;

       vec4 p4 = vec4(pEgo, 1.0);
       vec3 pDec = vec3(dot(st.e2d0, p4), dot(st.e2d1, p4), dot(st.e2d2, p4));
   ```

4. **Runtime-compile** with `ShaderModuleUtils.FromString(device, utf8, stage, null, debugName)`, the debug name a NUL-terminated path next to the stock `Grid.frag` (found through the `GridFrag` shader asset) so `#include` resolves against the game's own shader library. Omit the NUL and includes fail.

5. **Anchors:** compose decal space every frame in `double` from a part's local frame or from geodetic lat/lon, never storing ecliptic coordinates (see [choose a render frame and anchor effects](../../overlays/choose-a-render-frame-and-anchor-effects/overview.md) approach 6). Picking uses `Cursor.GetEgoRay(Program.MainViewport)` with `Ray.RaycastWatertight` against part view meshes, falling back to a 64-step march plus 24 bisections against the terrain height field.

6. **Textures** come from a store with their own bindless slots (`AddTexture`); `FreeTexture` rewrites a freed slot to the engine's empty texture so an already-recorded draw samples white, and the image goes on the shared retire queue. Teardown clears `Active`, unpatches, then waits for idle before freeing.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerRenderPatches.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerRenderPatches.cs) | Postfix |
| [gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs) | Pipeline, record, shaders |

## Variations and gotchas

- **A decal pass needs single-sample depth;** draw only after the depth resolve (the colour-only call passes `false`).
- **Derivatives are undefined after a discard** in non-uniform control flow: compute the normal first.
- **Reverse-Z:** the far plane is depth 0, which is also what empty background reads as.
- **`Cursor.InputRay` was removed** in favour of `Cursor.GetEgoRay(IViewport)`.
- **Main viewport only** in the first version; the pipeline, unit cube and depth descriptor ring exist only while the registry is non-empty.
- A dormant sticker (vessel despawned, part staged, image evicted) keeps its registry entry but does not keep the patch installed.

## See also

- [Overview](overview.md)
- [Approach 1: unit-cube decal volumes](project-decals-onto-scene-geometry-1.md)
- [Projected sticker decals](../../../../mods/gatos/features/sticker-projected-decals.md)
- [Insert a custom Vulkan pass into KSA's frame](../../vulkan-passes/insert-a-custom-pass-into-render-game/overview.md), [Ray cast against part meshes and terrain, from the cursor or any origin](../../../physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md)
