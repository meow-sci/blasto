---
type: Mod Feature
title: Draw a part-anchored textured quad inside the scene's opaque pass
description: Thug Life postfixes SuperMeshRenderSystem.RenderMainPass to record its own Vulkan draws with the stock UnlitMesh shaders into the offscreen MSAA pass, builds its pipeline lazily against Program.OffscreenTarget, and computes an ego-space MVP per entry from the camera of the viewport being rendered
mod: unscience
submod: thug-life
category: rendering
techniques: [harmony-postfix, vulkan-pipeline, vulkan-buffer, glsl-shader]
hooks:
  - harmony-postfix KSA.SuperMeshRenderSystem.RenderMainPass
  - "starmap StarMapAllModsLoaded (via unscience ISubmod.Initialize: manager created, no GPU work)"
  - "starmap StarMapBeforeGui (via unscience ISubmod.Update: slide animation)"
  - "starmap StarMapAfterGui (via unscience ISubmod.RenderContent: add, tune, remove entries)"
lifecycle: [render:scene-opaque, frame:draw-ui, startup:all-mods-loaded]
game_types:
  - KSA.SuperMeshRenderSystem
  - KSA.Program
  - KSA.Rendering.RenderTarget
  - KSA.Camera
  - KSA.Vehicle
  - KSA.Part
  - KSA.KittenEva
  - KSA.RenderingPresets
  - KSA.ShaderReference
  - RenderCore.RenderTechnique
tags: [vulkan, pipeline, quad, harmony, render-pass, viewports, ego-space, kittens]
---

Thug Life puts the "deal with it" sunglasses meme into the 3D scene: a flat pixel-art quad
anchored to any part or sub-part of any vessel (or the face of an EVA kitten), with its own offset,
rotation, width and height in the anchor part's frame. Several quads can exist at once, each can be
hidden or removed, and the kitten preset slides the glasses into place.[^tl-readme] It is the
smallest complete example in the repo of a mod drawing its own geometry with its own Vulkan
pipeline inside the game's scene pass.

Mechanism in one line: a Harmony postfix on `SuperMeshRenderSystem.RenderMainPass(IViewport,
CommandBuffer)` receives the command buffer while the offscreen target's dynamic-rendering pass is
open, binds a pipeline built from the stock `UnlitMeshVert` / `UnlitMeshFrag` shaders, pushes an MVP
and issues one indexed draw per entry.

## How it works

**The hook.** `ThugLifeRenderPatches.Apply` resolves `RenderMainPass` by name and adds a postfix that
binds only `commandBuffer`.[^ksa-supermesh] The postfix checks the static `ThugLifeRenderManager.Active`, then calls
`Instance.RecordDraws(commandBuffer)` inside a try/catch. Each host (standalone or Unscience) applies
it with its own Harmony instance and dispatches to its own manager instance.[^tl-patches]

**GPU resources.** `ThugLifeQuadRenderer` creates (once):[^tl-quad][^rc-rendertechnique][^ksa-renderingpresets]

| Resource | Details |
|---|---|
| Descriptor set layout, pool, set | One `CombinedImageSampler` at binding 0, fragment stage, pointing at the generated texture |
| Pipeline layout | That set plus one vertex-stage push constant of `float4x4` (the MVP) |
| Shader stages | `RenderTechnique.CreateShaderStages(device, [ModLibrary.Get<ShaderReference>("UnlitMeshVert"), ...("UnlitMeshFrag")])` |
| Vertex input | One binding: `float3` position at location 0, `float2` UV at location 1 |
| Fixed state | Triangle list, `Presets.Rasterization.Fill.CullNone` (visible from both sides), `RenderingPresets.ReverseZDepthStencil.DepthTestWrite`, `Presets.BlendState.BlendColorAlpha`, the renderer's dynamic viewport/scissor state |
| Render-pass compatibility | `Program.OffscreenTarget.SetupGraphicsPipeline(ref info)` immediately before `CreateGraphicsPipeline` |
| Geometry | Device-local vertex and `UInt16` index buffers, uploaded once through a staging pool (see [procedural texture](./thug-life-procedural-texture.md)) |

`UnlitMesh.vert` takes exactly that layout: `inPos`, `inUV` and a push-constant
`worldViewProjMatrix`, with no other uniforms, which is why any mod can reuse it for a textured
mesh.[^ksa-unlit-vert][^ksa-default-assets] KSA's scene pass uses Vulkan dynamic rendering, so a
pipeline has no `VkRenderPass`; `RenderTarget.SetupGraphicsPipeline` chains a
`VkPipelineRenderingCreateInfo` with the target's color, depth and stencil formats, and fills the
multisample state from the target's sample count.[^ksa-rendertarget] The source comment notes this
mirrors the game's own main-pass pipelines.

**Per-draw transform.** `RecordDraw` takes the camera from `Program.GetRenderCamera()`, which is
`RenderedViewport.GetCamera()`, the viewport currently being recorded. The anchor's ego-space matrix
is rebuilt every draw from live vessel state: `Vehicle.GetMatrixAsmb2Ego(camera)`, then
`Part.PositionEgo(in vehMat)` and `Part.Asmb2Ego(vehicle.Asmb2Ego)`. The quad's own matrix is
`scale(width, height, 1) * rotX * rotY * rotZ * translate(position) * partEgo`, and the final
`modelEgo * camera.MVP.viewProjection` is pushed as the constant. Ego space is camera-relative, so
the precision problem of world-space coordinates never appears.[^ksa-vehicle][^ksa-part][^ksa-camera]
Because sub-parts expose the same `PositionEgo` / `Asmb2Ego` chain, anchoring to a sub-part needs no
extra code.

**Entries and the kitten preset.** A `ThugLifeEntry` holds the vessel, the anchor part, position,
rotation (degrees), width, height, `Visible` and an optional `ThugLifeSlide`. The "animate thug"
button is shown only when the selected vessel `is KittenEva`; it anchors to the selected part or the
kitten's first top-level part and adds an entry whose position is driven by a 1.2 s ease-out slide
(`1 - (1 - t)^2`) from `KittenGlassesPreset.StartPosition` to `EndPosition`, advanced by
`ThugLifeRenderManager.Update(dt)`.[^tl-entry][^tl-slide][^tl-kitten-preset][^tl-submod]

**Fault handling.** If building the GPU resources throws, the manager records the error, disables
itself and shows the message where the player pressed Add. If a draw throws, `Active` is cleared so
the render loop is never spammed. `Dispose` clears `Active` before destroying Vulkan handles.[^tl-manager]

**Saves.** The `thug-life` save participant stores every entry's anchor path, offset, rotation, size
and visibility. Load clears old entries but keeps the GPU resources; a running slide is saved at its
current position and not replayed.[^tl-persistence]

## Lifecycle and timing

Where `RenderMainPass` is called in build 5554 (all inside an open `BeginRendering` /
`EndRendering` on an offscreen target, on the main thread during command recording):[^ksa-program]

| Call site | Lines | Target |
|---|---|---|
| `RenderGame` -> `RenderViewport` for every visible non-main viewport (secondary views, the two always-on 128 px crew portraits) | `RenderViewport` L4468; `BeginRendering` L4505, `RenderMainPass` L4568, `EndRendering` L4570; loop L4672-L4682 | `viewport.OffscreenTarget` |
| `RenderGame`, main flight view, after stars/planet/part models | `BeginRendering` L4792, `RenderMainPass` L4839, `EndRendering` L4848 | `Program.OffscreenTarget` |
| `RenderEditor` (vehicle editor scene) | L5047 | `Program.OffscreenTarget` |

That is `render:scene-opaque`: after the depth prepass, AO and shadows, before translucency,
atmosphere, volumetric exhausts (L4891), bloom and ImGui (L4950). The quad depth-tests against and
writes into the scene depth, so later translucent passes and plumes composite correctly over it.

Entries are created, tuned and removed from ImGui (`frame:draw-ui`), and the slide advances in
`Update(dt)` from `[StarMapBeforeGui]`; both run earlier in the same frame on the same thread as the
recording, so the entry list needs no locking.

**Why the GPU work is lazy.** The manager is constructed in `Initialize()`, which Unscience calls
from `[StarMapAllModsLoaded]`. StarMap fires that attribute from a Harmony postfix on
`ModLibrary.LoadAll`.[^starmap-modlibrary-patches] In the `Program` constructor `ModLibrary.LoadAll()`
runs at L942, while `BuildRenderTargets()`, which creates `_offscreenTarget`, runs at L970, so at
load time `Program.OffscreenTarget` is still null. The pipeline is therefore built inside the first
`Add`, from a UI click, long after the render targets exist. The README records the "init failed:
Object reference not set" error this fixed.[^tl-readme]

## Key code

| Path | Role |
|---|---|
| [thug-life.lib/ThugLifeRenderPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeRenderPatches.cs) | Postfix install/remove and dispatch |
| [thug-life.lib/ThugLifeRenderManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeRenderManager.cs) | Entries, lazy GPU init, slide update, fault latch |
| [thug-life.lib/ThugLifeQuadRenderer.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeQuadRenderer.cs) | Descriptor set, pipeline, buffers, per-entry MVP and draw |
| [thug-life.lib/ThugLifeEntry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeEntry.cs) | Anchor and transform state |
| [thug-life.lib/ThugLifeSlide.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeSlide.cs) | Ease-out position slide |
| [thug-life.lib/KittenGlassesPreset.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/KittenGlassesPreset.cs) | Tuned kitten pose, `is KittenEva` test |
| [thug-life.lib/ThugLifeSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeSubmod.cs#L218-L311) | Add entry, animate thug, anchor resolution |
| [thug-life.lib/ThugLifeSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeSubmod.Persistence.cs) | Save participant |
| [.claude/skills/ksa/quad.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/.claude/skills/ksa/quad.md) | The authors' general recipe for textured quads[^quad-skill] |

## Snippets

Patch install and the postfix:

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
// ...
        harmony.Patch(original, postfix: new HarmonyMethod(postfixMethod));
    }
// ...
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

Pipeline built from stock shaders against the offscreen target:

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
// ...
        Program.OffscreenTarget.SetupGraphicsPipeline(ref info);

        return device.CreateGraphicsPipeline(default(VkPipelineCache), info, null);
```

Per-entry draw with the render camera's ego-space MVP:

```csharp
// thug-life.lib/ThugLifeQuadRenderer.cs L252-L297
        var camera = Program.GetRenderCamera();
        if (camera == null) return;
        if (!TryComputeModelEgo(entry, camera, out float4x4 modelEgo)) return;

        float4x4 mvp = modelEgo * camera.MVP.viewProjection;

        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
// ...
        Program.SetViewport(cmd);
        cmd.PushConstants(_pipelineLayout, VkShaderStageFlags.VertexBit, ByteSize.Zero, mvp);
// ...
        cmd.DrawIndexed(_indexCount, 1, 0, 0, 0);
    }
// ...
        double4x4 vehMat = entry.Vehicle.GetMatrixAsmb2Ego(camera);
        double3 partPos = entry.Part.PositionEgo(in vehMat);
        doubleQuat partRot = entry.Part.Asmb2Ego(entry.Vehicle.Asmb2Ego);

        float4x4 partRotMat = float4x4.CreateFromQuaternion(floatQuat.Pack(in partRot));
        float4x4 partTransMat = float4x4.CreateTranslation(float3.Pack(in partPos));
        float4x4 partEgo = partRotMat * partTransMat;
// ...
        // v_local → scale → userRot → userTrans → partEgo → ego
        model = scaleMat * userRot * userTrans * partEgo;
```

Lazy GPU bring-up on the first entry:

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `SuperMeshRenderSystem.RenderMainPass(IViewport, CommandBuffer)` | [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L388-L395) | Postfix target; sets the viewport and renders the opaque mesh pass |
| `Program.RenderViewport` / `RenderGame` / `RenderEditor` call sites | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4468-L4570) | Where the pass is open and the hook fires |
| `Program.OffscreenTarget`, `RenderedViewport`, `GetRenderCamera()`, `GetRenderer()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L456-L644) | Pipeline target, render camera, renderer |
| `Program.SetViewport(CommandBuffer)` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4448-L4466) | Viewport/scissor sized to the rendered viewport |
| `Program` constructor: `ModLibrary.LoadAll()` then `BuildRenderTargets()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L942-L971) | Load-order reason for lazy GPU init |
| `RenderTarget.SetupGraphicsPipeline(ref VkGraphicsPipelineCreateInfo)` | [current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L356) | Dynamic-rendering formats and sample count |
| `ViewportRenderSurface` owned targets / `ViewportBase` sample count | [current/decomp/KSA.Rendering/ViewportRenderSurface.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ViewportRenderSurface.cs#L105-L107) | Secondary targets use the same color format and `GameSettings.GetSampleCount()` (see also [current/decomp/KSA/ViewportBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportBase.cs#L128)), so one pipeline fits every call site |
| `RenderTechnique.CreateShaderStages(Device, Span<ShaderReference>, ...)` | [current/decomp/RenderCore/RenderTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/RenderTechnique.cs#L35) | Shader modules from asset ids |
| `RenderingPresets.ReverseZDepthStencil.DepthTestWrite` | [current/decomp/KSA/RenderingPresets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RenderingPresets.cs#L12-L14) | Reverse-Z depth state (the pass clears depth to 0) |
| `Vehicle.GetMatrixAsmb2Ego(Camera)`, `Vehicle.Asmb2Ego` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270-L1281) | Vessel assembly-to-ego transform |
| `Part.PositionEgo(ref readonly double4x4)`, `Part.Asmb2Ego(doubleQuat)` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1203-L1211) | Anchor position and orientation |
| `Camera.MVP.viewProjection` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L63) | Per-camera view-projection |
| `UnlitMeshVert` / `UnlitMeshFrag` shader ids | [current/Content/Core/DefaultAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/DefaultAssets.xml#L53-L54) | Stock shaders reused |

## Pitfalls and version notes

- `RenderMainPass` gained the leading `IViewport` parameter in 5482; the postfix binds only
  `commandBuffer` by name, so it attached unchanged.[^scope-pixel-grids] In 5554 it is still the only
  overload; resolving it by name would become ambiguous if an overload were added.
- Before 5261 the target was `Program.OffScreenPass` (`RenderPassState` with `.Pass` and
  `.SampleCount`); it no longer exists. `SetupGraphicsPipeline` must be the last call before
  `CreateGraphicsPipeline`, because the structures it chains are owned by the target and overwritten
  on each call.
- The camera must be the render camera, not the main camera: since 5261 the crew-portrait
  viewports are always visible and run through `RenderMainPass` with their own cameras.
- One pipeline serves every call site because viewport-owned offscreen targets are created with
  the main target's color format and `GameSettings.GetSampleCount()`.[^ksa-viewport-surface][^ksa-viewportbase]
- The pipeline is built once. A renderer rebuild with a different MSAA setting
  (`Program.RebuildRenderer` -> `BuildRenderTargets` -> `Rebuild(..., GetSampleCount())`) is not
  handled by the mod; the pipeline would keep the old sample count (not tested in game).
- The hook also fires in the vehicle editor's render path (L5047); entries are not filtered by
  scene.
- GPU teardown happens only in `Dispose` (and on a failed first build). `ThugLifeQuadRenderer.Dispose`
  destroys the pipeline, buffers and descriptor pool without waiting for the device, which is safe only
  if no recorded frame still uses them. In the supermod `Dispose` runs from `[StarMapUnload]`, which
  StarMap dispatches after `Program.Main` returned, when `Program.Dispose` has already called
  `_renderer.Dispose()` and destroyed the Vulkan device (`Program.cs` L1483), so these destroys act on
  a dead device at game exit.[^ksa-program]
- `UnlitMesh.frag` writes alpha 1, so the blend state has no visible effect; transparency comes from
  geometry (see [procedural texture](./thug-life-procedural-texture.md)).
- The authors list "quad in main and portrait viewports" as a pending in-game check for
  5482.[^scope-pixel-grids]

## See also

- [Procedural texture and cut-out geometry](./thug-life-procedural-texture.md)
- [Pyro volumetric exhaust plumes](./pyro-volumetric-exhaust-plumes.md) (another render-time hook)
- [Graffiti projected decal pass](./graffiti-projected-decal-pass.md) (another custom Vulkan pass)
- [Thug Life sub-mod](../submods/thug-life.md)
- [Mod overview](../overview.md)
- [Opaque pixel quads with stock UnlitMesh shaders inside the offscreen scene pass](../../../howto/rendering/textures-and-decals/draw-a-textured-quad-in-the-world/draw-a-textured-quad-in-the-world-1.md) - how-to article demonstrating this feature
- [Program.OffscreenTarget.SetupGraphicsPipeline, created lazily](../../../howto/rendering/vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/build-a-vulkan-pipeline-for-the-scene-target-1.md) - how-to article demonstrating this feature
- [Postfix SuperMeshRenderSystem.RenderMainPass to draw inside the opaque scene pass](../../../howto/rendering/vulkan-passes/insert-a-custom-pass-into-render-game/insert-a-custom-pass-into-render-game-2.md) - how-to article demonstrating this feature

[^tl-patches]: thug-life.lib/ThugLifeRenderPatches.cs
[^tl-manager]: thug-life.lib/ThugLifeRenderManager.cs
[^tl-quad]: thug-life.lib/ThugLifeQuadRenderer.cs
[^tl-entry]: thug-life.lib/ThugLifeEntry.cs
[^tl-slide]: thug-life.lib/ThugLifeSlide.cs
[^tl-kitten-preset]: thug-life.lib/KittenGlassesPreset.cs
[^tl-submod]: thug-life.lib/ThugLifeSubmod.cs
[^tl-persistence]: thug-life.lib/ThugLifeSubmod.Persistence.cs
[^tl-readme]: thug-life/README.md
[^quad-skill]: .claude/skills/ksa/quad.md (authors' textured-quad recipe)
[^scope-pixel-grids]: scope/pixel-grids-and-render.md (authors' integration analysis)
[^starmap-modlibrary-patches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^ksa-supermesh]: KSA.SuperMeshRenderSystem (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-rendertarget]: KSA.Rendering.RenderTarget (decompiled, build 2026.10.10.5554)
[^ksa-viewport-surface]: KSA.Rendering.ViewportRenderSurface (decompiled, build 2026.10.10.5554)
[^ksa-viewportbase]: KSA.ViewportBase (decompiled, build 2026.10.10.5554)
[^rc-rendertechnique]: RenderCore.RenderTechnique (decompiled, build 2026.10.10.5554)
[^ksa-renderingpresets]: KSA.RenderingPresets (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-default-assets]: Core/DefaultAssets.xml (game content, build 2026.10.10.5554)
[^ksa-unlit-vert]: Core/Shaders/Mesh/UnlitMesh.vert (game shader, build 2026.10.10.5554)
