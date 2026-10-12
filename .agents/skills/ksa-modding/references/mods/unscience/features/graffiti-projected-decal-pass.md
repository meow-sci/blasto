---
type: Mod Feature
title: Projected PNG decal pass recorded after the main-view resolve
description: A Harmony postfix on RenderTarget.ResolveAttachments records a custom Vulkan pass in the post-resolve window KSA's GridPass uses, drawing one unit cube per decal whose fragment shader rebuilds the scene position from reverse-Z depth and projects a PNG onto it
mod: unscience
submod: graffiti
category: rendering
techniques: [harmony-postfix, vulkan-pipeline, vulkan-buffer, glsl-shader, starmap-lifecycle]
hooks:
  - harmony-postfix KSA.Rendering.RenderTarget.ResolveAttachments
  - starmap StarMapBeforeGui (via unscience ISubmod.Update; matrices and GPU bring-up)
  - harmony-prefix KSA.Program.OnDrawUiConsole (HiddenUiFrameHook replays ISubmod.Update while the HUD is hidden)
lifecycle: [startup:all-mods-loaded, frame:draw-ui, render:grid]
game_types:
  - KSA.Rendering.RenderTarget
  - KSA.Program
  - KSA.GridPass
  - KSA.GlobalShaderBindings
  - RenderCore.Systems.BindlessTextureLibrary
  - RenderCore.ShaderModuleUtils
  - KSA.ShaderReference
  - KSA.RenderingPresets
  - KSA.Rendering.BarrierBatch
  - KSA.Rendering.ImageBarrierInfo
  - Core.Renderer
tags: [decals, vulkan, harmony, deferred-decals, reverse-z, bindless, runtime-glsl]
---

Graffiti paints PNG images onto whatever the player clicked: a hull, a deployed canopy, a kitten or
the ground. The image wraps hull curvature and terrain relief because it is not a mesh: it is a
screen-space projected (deferred-style) decal drawn after the main view has been rendered and
resolved.[^graffiti-patches]

Mechanism in one line: a Harmony postfix on `RenderTarget.ResolveAttachments` records an extra
dynamic-rendering pass into the main offscreen colour image; for each decal it rasterizes a unit
cube, reconstructs the scene position under each covered pixel from the resolved reverse-Z depth,
and samples the PNG from KSA's bindless texture table when that position lies inside the cube.

## How it works

1. **Seam.** The final `ResolveAttachments(commandBuffer)` call in `Program.RenderGame` is the one
   moment where the resolved single-sample depth and colour images are both current and not bound as
   attachments; the game's own `GridPass` draws in the same window. The method body only resolves
   when MSAA is on, but a postfix fires either way, so the seam works at every MSAA
   setting.[^ksa-rendertarget][^ksa-program]
2. **Gates.** The postfix returns on `inResolveDepth == false` (the early colour-only resolve before
   underwater composition), when no decal is live (`GraffitiSubmod.RenderActive`, a `volatile` bool
   published by `Update`), in the editor (`Program.EditorFlag`; the VAB resolves the same target), and
   unless `__instance` is `Program.OffscreenTarget` and `Program.RenderedViewport` is
   `Program.MainViewport`. Secondary and crew-portrait viewports resolve their own targets inside
   `RenderViewport` and are skipped.[^graffiti-patches]
3. **Pipeline** (built once, lazily, on the first live decal; `DecalRenderer`): a hand-built
   dynamic-rendering pipeline mirroring `GridPass.BuildPipeline`: one colour attachment in
   `Program.Instance.ColorFormat`, no depth attachment, single-sample, `CullFront` (the box still
   covers its footprint when the camera is inside it), `ReverseZDepthStencil.NoDepthTest` (occlusion
   comes from the sampled depth), `BlendColorAlphaOver`. The layout is set 0 =
   `GlobalShaderBindings.DescriptorSetLayout`, set 1 = the mod's own combined-image-sampler set for
   scene depth (one set per frame in flight), set 2 = the bindless library layout, plus one 112-byte
   push-constant block. The bindless set is declared update-after-bind and partially bound, so
   binding it while other slots change is legal.[^decal-renderer][^ksa-gridpass][^ksa-bindless]
4. **Runtime GLSL.** Both stages are C# raw strings compiled with `ShaderModuleUtils.FromString`.
   The `debugName` argument is a NUL-terminated fake path inside the directory of the shipped
   `GridFrag` asset (`ModLibrary.Get<ShaderReference>("GridFrag").ModPath`), so `#include
   "Common/Camera.glsl"` and `"Common/TextureSet.glsl"` resolve to the game's own headers for
   whatever build is installed.[^decal-shaders][^ksa-shadermoduleutils]
5. **Per-frame record** (`DecalRenderer.RecordPass`): rewrite this frame's depth descriptor to
   `OffscreenTarget.DepthImage` with `Program.PointClampedSampler`; barrier depth to
   `DepthSampledReadF` and colour to `ColorAttachmentReadWrite` exactly like `GridPass.Run`;
   `BeginRendering` with `LoadOp.Load`; bind set 0 with
   `GlobalShaderBindings.DescriptorSet(Program.MainViewport.ShaderSlot)` and no dynamic offsets, set 1,
   and `Program.Instance.BindlessTextures.DescriptorSet`; then one `PushConstants` +
   `DrawIndexed(36)` per drawable decal (visible, live, textured, closer than
   `MaxViewDistanceMetres`, default 50 km). Depth is left in the sampled-read layout, as `GridPass`
   leaves it.[^decal-renderer]
6. **Fragment shader.** `texelFetch` the depth at `gl_FragCoord`, discard reverse-Z background
   (`z <= 0`), unproject with `global.camera.inverseProjection` and `inverseView` into ego space,
   transform by the packed ego-to-decal matrix, discard outside `[-0.5, 0.5]^3`, derive the receiving
   surface normal from `dFdx`/`dFdy` of the reconstructed position (taken before any discard), fade
   and cut off at grazing angles, sample `SAMPLE_TEXTURE(texId, 0, uv)` with v flipped, and shade
   with one sun Lambert term plus `planetColor` ambient. `texId == 0xFFFFFFFF` draws a magenta checker
   instead (the "Debug box" option).[^decal-shaders][^ksa-global-glsl][^ksa-textureset-glsl]
7. **Push block.** Six `vec4`s carry the columns of the row-vector decal-to-ego and ego-to-decal
   matrices (KSA multiplies `v * M`, so dotting `(p, 1)` with column i reproduces
   `float3.Transform`), then texture slot, alpha, brightness and the normal cut-off. 112 bytes is
   checked against `sizeof(DecalPush)` at pipeline build.[^decal-renderer]
8. **Failure policy.** A shaderc failure throws with the full log and disables the feature; any
   exception while recording latches `_gpuFailed`, clears `RenderActive` and logs once. The pipeline
   lives while the decal list is non-empty (a dormant decal keeps it) and is torn down after
   `GraphicsAndCompute.WaitIdle()` when the list empties or on unload.[^graffiti-submod]

## Lifecycle and timing

| Step | When (build 5554) | What it touches |
|---|---|---|
| `GraffitiPatches.Apply` | `startup:all-mods-loaded`: unscience `[StarMapAllModsLoaded]` then `Patcher.Patch()` | Installs the postfix only |
| `GraffitiSubmod.Update` | `frame:draw-ui`: `[StarMapBeforeGui]` (prefix of `Program.OnDrawUiFrame`, `Program.cs` L3131) or the hidden-HUD replay from `OnDrawUiConsole` (L3119) | Re-resolves anchors, composes matrices against `Program.GetMainCamera()`, builds the pipeline on first use, publishes the entry array, sets `RenderActive` |
| Early colour resolve (skipped) | `RenderGame`, `Program.cs` L4904, `inResolveDepth: false` | Postfix returns |
| `OrbitLinePass.Run` / `GizmoPass.Run` | L4929 / L4931 | Game overlays drawn before the decals |
| **Decal pass** | postfix of the final `ResolveAttachments` at L4932 | Records into the main colour image |
| `GridPass.Run` | L4935, only when `ShowMapGridFlag` | Grid draws on top of the decals |
| Gauges, ImGui, final composite | L4937 onward | Decals are part of the scene colour by then |

So the pass belongs to `render:grid`: after `render:gizmos`, before the map grid and
`render:gauges`.[^ksa-program] Matrix composition in `frame:draw-ui` is same-frame safe because the
main camera is final after `OnFrameViewports` (L2263) and `OnFrameController` only changes the cursor
mode; command recording happens later on the same main thread (`Program.Render` L2443 ->
`RenderGame` L2473), so the published `DecalEntry[]` needs no locking.[^ksa-program][^unscience-mod]
While the HUD is hidden, unscience still runs `Update` from its `OnDrawUiConsole` replay, so placed
decals keep rendering.[^unscience-mod] The depth descriptor ring is indexed by
`Program.Instance.ResourceFrameIndex % MaxFramesInFlight`: a slot is only rewritten after the engine
waited on that frame's fence.[^decal-renderer]

## Key code

| Path | Role |
|---|---|
| [graffiti.lib/GraffitiPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/GraffitiPatches.cs#L27-L87) | Exact `(CommandBuffer, bool)` lookup, postfix, viewport/editor gates |
| [graffiti.lib/DecalRenderer.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalRenderer.cs#L97-L145) | Depth set layout, pool and per-frame ring; pipeline layout and pipeline |
| [graffiti.lib/DecalRenderer.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalRenderer.cs#L356-L438) | `RecordPass`: barriers, dynamic rendering, binds, per-decal push + draw |
| [graffiti.lib/DecalShaders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalShaders.cs) | Vertex and fragment GLSL as raw strings |
| [graffiti.lib/GraffitiSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/GraffitiSubmod.cs#L53-L124) | `Update` (publish + `RenderActive`) and the fault-latching `RecordPass` wrapper |

## Snippets

The postfix and its gates:

```csharp
// graffiti.lib/GraffitiPatches.cs L51-L76
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

Pipeline state that makes a box-projected decal work without a depth attachment:

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

Binding the game's per-viewport global set (KSA 5541+ form), the depth ring and the bindless table:

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

Reverse-Z position reconstruction and the box test in the fragment shader:

```glsl
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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `RenderTarget.ResolveAttachments(CommandBuffer, bool inResolveDepth = true)` | [current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L315-L342) | Patched; MSAA-gated body, postfix always runs |
| `RenderTarget.DepthImage` / `ColorImage` / `Extent` | [current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L36-L56) | Resolved outputs sampled and drawn into |
| `Program.RenderGame` resolve and pass order | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4901-L4936) | Early resolve L4904, gizmos L4931, final resolve L4932, grid L4935 |
| `Program.RenderViewport` / `RenderEditor` resolves | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4468-L4610) | Secondary viewports (L4606) and the editor (L5055) also fire the postfix; filtered |
| `Program.EditorFlag`, `OffscreenTarget`, `RenderedViewport`, `MainViewport`, `PointClampedSampler`, `SetViewport`, `ColorFormat`, `BindlessTextures` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L456-L490) | Gates, targets, sampler, formats (`EditorFlag` L225, `ColorFormat` L223, `BindlessTextures` L111, `SetViewport` L4448) |
| `GridPass.Run` / `BuildPipeline` / `UpdateDescriptorSet` | [current/decomp/KSA/GridPass.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GridPass.cs#L445-L491) | The template the pass copies (barriers, attachment, binds) |
| `GlobalShaderBindings.DescriptorSetLayout`, `DescriptorSet(int viewportIndex)` | [current/decomp/KSA/GlobalShaderBindings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GlobalShaderBindings.cs#L47-L54) | Set 0: camera and lighting UBOs for the main viewport's current frame |
| `BindlessTextureLibrary.DescriptorSetLayout` / `DescriptorSet` | [current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L14-L28) | Set 2: update-after-bind, partially bound texture table |
| `ShaderModuleUtils.FromString(Device, ReadOnlySpan<byte>, VkShaderStageFlags, CompileOptions?, ReadOnlySpan<byte> debugName)` | [current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L79-L115) | Runtime compile; `debugName` doubles as the include root |
| `global.camera.{viewProjection, inverseView, inverseProjection}`, `global.lighting.{sunPosition, sunColor, planetColor}` | [current/Content/Core/Shaders/Common/Global.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/Global.glsl#L7-L42) | Read by the decal shaders (`SET_GLOBAL` defaults to 0) |
| `SAMPLE_TEXTURE`, `globalTextures[]`, `samplers[]` | [current/Content/Core/Shaders/Common/TextureSet.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/TextureSet.glsl#L26-L46) | Bindless sampling with `#define SET_TEXTURE 2` |

## Pitfalls and version notes

- **Two resolves per frame since 5438.** The early `ResolveAttachments(..., inResolveDepth: false)`
  runs before underwater composition with stale depth; binding the explicit `(CommandBuffer, bool)`
  overload and returning on `false` keeps one decal pass per frame.[^scope-decals]
- **5541 descriptor migration.** `GlobalShaderBindings.DynamicOffset` was removed and `DescriptorSet`
  became a method taking the viewport's `ShaderSlot`; bind it with an empty dynamic-offset span, as
  `GridPass.Run` does in 5554.[^ksa-gridpass][^ksa-globalshaderbindings]
- **Header drift is a runtime break.** The GLSL is compiled against whatever `Common/*.glsl` ships;
  5541 rewrote `TextureSet.glsl` (a `SET_TEXTURE` define is now mandatory: missing it is a compile
  error) and `Global.glsl`. The fields graffiti reads still exist in 5554, but a rename fails at
  shaderc compile with one console line and disables the feature.[^ksa-textureset-glsl]
- **Viewport identity checks are load-bearing.** The editor resolves the same `_offscreenTarget`
  through the main viewport, so only `EditorFlag` separates it; secondary viewports would draw decals
  composed against the wrong camera.
- **Projection depth.** The visible decal is surface ∩ box: too shallow crops a wide decal on a curved
  hull, too deep paints through thin parts; the depth heuristics live in
  [decal anchoring](./graffiti-decal-anchoring.md).
- Decals are lit with a single Lambert sun term plus ambient (no `Lighting.glsl` BRDF), so they read
  slightly flatter than PBR hulls. Main viewport and flight scene only; no MSAA on the decal itself.
- The repo README states that native rendered acceptance of the 5541 migration was still pending at
  the pinned ref.[^scope-decals]
- Teardown is safe while the game runs (`GraphicsAndCompute.WaitIdle()` before destroying the pipeline,
  descriptor pool and textures), but the unload path is reached from `[StarMapUnload]`, which runs
  after `Program.Dispose` has already destroyed the renderer and Vulkan device (`Program.cs` L1483); at
  game exit the wait and the destroys act on disposed objects.[^graffiti-submod][^ksa-program]

## See also

- [Click placement and picking](./graffiti-decal-placement-picking.md)
- [Per-frame decal anchoring](./graffiti-decal-anchoring.md)
- [Bindless decal textures](./graffiti-bindless-decal-textures.md)
- [Hidden-HUD frame hook](./lib-hidden-ui-frame-hook.md)
- [Thug Life textured-quad pass](./thug-life-textured-quad-render-pass.md) (another custom pass)
- [Graffiti sub-mod](../submods/graffiti.md) and the [mod overview](../overview.md)
- [Unit-cube decal volumes with reverse-Z depth reconstruction](../../../howto/rendering/textures-and-decals/project-decals-onto-scene-geometry/project-decals-onto-scene-geometry-1.md) - how-to article demonstrating this feature
- [Postfix RenderTarget.ResolveAttachments for a post-resolve pass](../../../howto/rendering/vulkan-passes/insert-a-custom-pass-into-render-game/insert-a-custom-pass-into-render-game-1.md) - how-to article demonstrating this feature

[^graffiti-patches]: graffiti.lib/GraffitiPatches.cs
[^decal-renderer]: graffiti.lib/DecalRenderer.cs
[^decal-shaders]: graffiti.lib/DecalShaders.cs
[^graffiti-submod]: graffiti.lib/GraffitiSubmod.cs
[^unscience-mod]: unscience/Mod.cs
[^scope-decals]: scope/decals.md (authors' graffiti integration analysis)
[^ksa-rendertarget]: KSA.Rendering.RenderTarget (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gridpass]: KSA.GridPass (decompiled, build 2026.10.10.5554)
[^ksa-globalshaderbindings]: KSA.GlobalShaderBindings (decompiled, build 2026.10.10.5554)
[^ksa-shadermoduleutils]: RenderCore.ShaderModuleUtils (decompiled, build 2026.10.10.5554)
[^ksa-bindless]: RenderCore.Systems.BindlessTextureLibrary (decompiled, build 2026.10.10.5554)
[^ksa-global-glsl]: Common/Global.glsl (game shader header, build 2026.10.10.5554)
[^ksa-textureset-glsl]: Common/TextureSet.glsl (game shader header, build 2026.10.10.5554)
