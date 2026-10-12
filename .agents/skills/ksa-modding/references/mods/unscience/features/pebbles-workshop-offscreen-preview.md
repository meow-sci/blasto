---
type: Mod Feature
title: Independent offscreen Vulkan preview rendered before the GUI and shown as an ImGui image
description: Pebbles renders the Workshop mesh into private color and depth images with a dynamic-rendering pipeline and runtime-compiled GLSL, submits it with a fence in StarMapBeforeGui, and shows the result through the ImGui Vulkan backend
mod: unscience
submod: pebbles
category: rendering
techniques: [vulkan-pipeline, vulkan-buffer, vulkan-texture, glsl-shader, imgui-drawlist, starmap-lifecycle]
hooks:
  - starmap StarMapBeforeGui (via unscience ISubmod.Update -> WorkshopEditor.Update; scene build, offscreen render, private queue submission)
  - harmony-prefix KSA.Program.OnDrawUiConsole (HiddenUiFrameHook replays ISubmod.Update while the HUD is hidden)
  - starmap StarMapAfterGui (via unscience ISubmod.RenderFloatingWindows; AddImage of the finished target)
lifecycle: [frame:draw-ui, render:imgui]
game_types:
  - KSA.Program
  - Core.Renderer
  - KSA.ImGuiBackend
  - KSA.ImGuiBackendVulkanImpl
  - KSA.Rendering.ImageBarrierInfo
  - KSA.Rendering.ImageTransition
  - KSA.CommandBufferEx
  - KSA.TextureReference
  - KSA.MeshReference
  - RenderCore.MeshAsset
  - RenderCore.ShaderModuleUtils
  - RenderCore.VkUtils
  - Brutal.VulkanApi.Abstractions.VertexInput
  - Brutal.VulkanApi.Abstractions.Presets
  - Brutal.ImGuiApi.ImTextureRef
tags:
  - vulkan
  - offscreen
  - render-target
  - dynamic-rendering
  - glsl
  - imgui-image
  - preview
  - synchronization
  - lifetime
---

The Workshop shows the chosen mesh lit, textured and depth-tested in a "studio" setting while the
player edits colliders, without leasing a game viewport, moving a camera or touching the scene: the
preview is a small private renderer with its own color and depth images, pipeline, shaders, buffers
and descriptors. Its output image is handed to ImGui as a texture and drawn inside the editor
window.[^scope-ground-clutter]

Mechanism in one line: on frames where the mesh, camera or panel size changed, `WorkshopPreview.Render`
(called from `ISubmod.Update`, which unscience runs in `[StarMapBeforeGui]`) waits for the device,
records a dynamic-rendering pass into a one-shot command buffer of its own, submits it to the
graphics queue and waits on a fence; only then does the editor's `Draw` (in `[StarMapAfterGui]`) add
the image to the window draw list, which the game samples in its ImGui pass at the end of
`RenderGame`.[^workshop-preview]

## How it works

**Classes** (all under `pebbles.lib/Preview/`):

| Class | Owns | Lifetime |
|---|---|---|
| `PreviewGeometry` | CPU-only draw list: transformed `PreviewVertex` arrays, `uint` indices, five `TextureReference`s and flag vectors per primitive | Built by `Refresh`, dropped after the next successful render |
| `PreviewPipeline` | Descriptor set layout (5 combined image samplers), pipeline layout with a 112-byte push range, one sampler, one graphics pipeline | Created lazily on the first render; lives until `Dispose` |
| `PreviewScene` | Device-local vertex and index buffers per draw, a descriptor pool sized for the draws, one descriptor set per draw, strong references to the borrowed textures | Replaced on each refresh; old one disposed only after the new one rendered |
| `PreviewTarget` | `R8G8B8A8UNorm` color image and `D32SFloat` depth image with views, and the ImGui texture registration | Replaced when the clamped size changes; disposed before its replacement is published |
| `WorkshopPreview` | Orchestration, status text, last matrix and eye for change detection | One per editor; recreated on release |

**Geometry preparation** ([pebbles.lib/Preview/PreviewGeometry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewGeometry.cs#L19-L89)).
`Refresh` resolves the LOD's meshes through `ClutterAssets` (CPU `MeshReference.HostPrimitives`),
requires float3 position and normal and float2 UV0 streams, applies the recipe transform
(`Scale * RotX * RotY * RotZ * Translation`, the same convention as Apply) and its inverse transpose
for normals, flips winding for mirrored transforms, widens 16-bit indices to 32-bit, caps the preview
at 2 million vertices and 12 million indices, and resolves each primitive's material slot to five
textures (diffuse, normal, PBR, opacity, thickness; `TextureReference.EmptyWhite` for unused ones).
Resolving a GLB texture is what triggers its first GPU upload, which is why `Refresh` also runs before
the GUI. Two `Vector4` flag sets record which maps are real and whether the material uses source
colors.[^preview-geometry]

**Pipeline** ([pebbles.lib/Preview/PreviewPipeline.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewPipeline.cs#L44-L135)).
The constructor first checks `Marshal.SizeOf`/`OffsetOf` of `PreviewVertex` (32 bytes: position 0,
normal 12, UV 24) and `PreviewPush` (112 bytes: matrix 0, camera 64, maps 80, options 96) so a layout
mismatch fails loudly instead of rendering garbage. The pipeline uses dynamic rendering
(`VkPipelineRenderingCreateInfo` chained in `Next`, `RenderPass = NullHandle`) with the two attachment
formats, the renderer's shared dynamic viewport/scissor state (`Renderer.DynamicStateInfo`,
`ViewportState`), triangle lists, no culling (imported meshes may be single-sided), depth test
`LessOrEqual` with writes, and no blending. The GLSL sources are embedded resources
(`<EmbeddedResource Include="Preview/**/*.glsl" />`) and are compiled at runtime with
`ShaderModuleUtils.FromString`, the game's shaderc wrapper, using the resource name plus a NUL as the
debug name; the modules are destroyed right after pipeline creation.[^preview-pipeline][^pebbles-csproj][^ksa-shader-module-utils][^core-renderer]

**Scene upload** ([pebbles.lib/Preview/PreviewScene.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewScene.cs#L31-L86)).
Per draw: two device-local buffers filled with `VkUtils.StageAndUploadToBuffer` into an
[asset upload submission](./pebbles-asset-upload-submission.md), a transfer-to-vertex-input buffer
barrier, a descriptor set allocated from the scene's own pool, and five combined-image-sampler
writes pointing at the textures' image views in `ShaderReadOnlyOptimal`. One `SubmitAndWait`
completes all uploads. The `Draw` keeps the `TextureReference` array alive for as long as its
descriptor set exists, because some of those textures belong to the shared GLB cache.[^preview-scene][^ksa-vk-utils]

**Recording** ([pebbles.lib/Preview/PreviewTarget.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewTarget.cs#L55-L102)).
`PreviewTarget.Render` borrows the same one-shot submission class for drawing rather than uploading:

1. `TransitionImages2`: color from `Undefined` (first use) or `SampledReadF` (previous result) to
   `ColorAttachmentWrite`; depth to `DepthStencilAttachmentWrite`.
2. `BeginRendering` with a cleared color (`0.055, 0.068, 0.086`) and cleared depth 1.0 (`DontCare` store).
3. Dynamic viewport and scissor, then `PreviewScene.Record`: bind pipeline, and per draw push the
   view-projection, eye and flags, bind the descriptor set, vertex and `UInt32` index buffers, and
   `DrawIndexed`.
4. `EndRendering`, transition color to `SampledReadF` (`ShaderReadOnlyOptimal`, fragment read).
5. `SubmitAndWait`, then on first use `ImGuiBackend.Vulkan.AddTexture(sampler, view)`, which
   allocates a combined-image-sampler set from the ImGui backend's pool with the default
   `ShaderReadOnlyOptimal` layout and returns it as an `ImTextureRef`.[^preview-target][^ksa-imgui-backend-vulkan][^ksa-image-barrier-info][^ksa-command-buffer-ex]

The game itself records dynamic rendering the same way (for example
`ParticleRenderer` calls `commandBuffer.BeginRendering(in pRenderingInfo)`), so no extension or
patch is needed.[^ksa-particle-renderer]

**Shaders** ([pebbles.lib/Preview/Workshop.frag.glsl](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/Workshop.frag.glsl)).
The vertex shader passes object-space position, normal and UV through and multiplies by the push
matrix ([pebbles.lib/Preview/Workshop.vert.glsl](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/Workshop.vert.glsl)).
The fragment shader discards below 0.5 opacity, uses the diffuse RGB directly for source-color
materials and otherwise follows the clutter convention where diffuse alpha blends between a 2.2
gamma decode and raw values, flips normals on back faces, applies the normal map with a cotangent
frame built from screen-space derivatives (no vertex tangents), lights with a fixed key and fill
light plus ambient, uses the packed map as AO (R), roughness (G, Blinn-Phong exponent 128 to 4) and
metalness (B), and adds a back-lit term from the thickness map. It is a neutral studio look; the
game's terrain tint and lighting only apply after Apply.[^workshop-frag][^workshop-vert]

**Change detection** ([pebbles.lib/Preview/WorkshopPreview.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/WorkshopPreview.cs#L61-L107)).
`Render` returns immediately when nothing is pending and the clamped size, matrix and eye equal the
last render. The matrix uses the panel's logical size, while the GPU size is clamped to 64..1536 px
per side, so a large panel keeps the right aspect at a capped resolution. Failures dispose the new
objects and the current target (its layout is unknown after a failed record), mark the preview
failed and stop retrying until the next explicit refresh.

## Lifecycle and timing

| Step | Hook and frame point (build 5554) |
|---|---|
| `Refresh` (CPU prep, possible GLB texture upload), `Render` (wait, build, record, submit, fence) | `WorkshopEditor.Update` <- `PebblesSubmod.Update` <- unscience `[StarMapBeforeGui]` = prefix of `Program.OnDrawUiFrame` (`Program.cs` L2267), or the `HiddenUiFrameHook` replay in the `OnDrawUiConsole` prefix (L2275); `frame:draw-ui` |
| `AddImage(preview.Texture, ...)` into the editor window | `WorkshopEditor.Draw` <- `RenderFloatingWindows` <- `[StarMapAfterGui]` = postfix of `Program.OnDrawUiViewports` (L2268) |
| Draw data finalized | `ImGui.Render()` (L2286) |
| Preview image sampled | `ImGuiBackend.Vulkan.RenderDrawData` after `RenderFinalComposite` in `RenderGame` (L4950) or `RenderEditor` (L5074); `render:imgui` |
| Frame submitted | `PostRender` -> `Renderer.TrySubmitFrame` on `Renderer.Graphics` (`Program.cs` L2343) |

Why `[StarMapBeforeGui]` and a full `Device.WaitIdle()`. The image may still be read by the previous
frame's ImGui pass, which `PostRender` already submitted and which can still be executing (the
renderer keeps `MaxFramesInFlight = 2`). Waiting on the preview's own fence would not cover that
reader; `WaitIdle` does. At `OnDrawUiFrame` the current frame has not recorded anything yet, so after
the wait there is no reader at all, and the preview may be re-rendered, resized or freed. The
preview's own submission goes to the same `Renderer.Graphics` queue the game submits frames to, and
is fenced before `Draw` ever references the image, so the ImGui pass later in the frame always sees
a complete image in `ShaderReadOnlyOptimal`.[^workshop-preview][^ksa-program][^core-renderer]

Doing the same work from `RenderFloatingWindows` (after this frame's ImGui list may already contain
the texture) or from a `RenderGame` hook (inside the frame's command buffer) would require either a
deferred destroy queue or inserting into the game's pass order; the pre-GUI blocking design avoids
both at the cost of a GPU drain on change frames.

**Resource lifetime.**

- Replace-then-dispose: a new scene or target is rendered first and only then swapped in, so a
  failed refresh never leaves the editor without its previous image.
- `PreviewTarget.Dispose` removes the ImGui descriptor set (`ImGuiBackend.Vulkan.RemoveTexture`)
  before destroying the views and images.
- `WorkshopEditor.Release()` only sets a flag; the next `Update` disposes the preview (with
  `WaitIdle`) at the safe phase. `PebblesSubmod.Update` uses this ordering to retire the preview before
  releasing the shared GLB cache whose textures it borrows
  ([pebbles.lib/PebblesSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/PebblesSubmod.cs#L20-L29)).[^pebbles-submod]
- `WorkshopPreview.Dispose` waits for the device, then frees target, scene and pipeline; it is a
  no-op if nothing was ever rendered.

## Key code

| Path | Role |
|---|---|
| [pebbles.lib/Preview/WorkshopPreview.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/WorkshopPreview.cs) | Refresh, change-detected render, failure handling, disposal |
| [pebbles.lib/Preview/PreviewTarget.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewTarget.cs) | Color/depth images, layout transitions, dynamic-rendering pass, ImGui registration |
| [pebbles.lib/Preview/PreviewPipeline.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewPipeline.cs) | Layout checks, descriptor and pipeline layouts, runtime shader compile |
| [pebbles.lib/Preview/PreviewScene.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewScene.cs) | GPU buffers, descriptor sets, draw recording |
| [pebbles.lib/Preview/PreviewGeometry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewGeometry.cs) | CPU vertex/index preparation and material binding |
| [pebbles.lib/Preview/Workshop.vert.glsl](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/Workshop.vert.glsl), [pebbles.lib/Preview/Workshop.frag.glsl](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/Workshop.frag.glsl) | Studio shading |
| [pebbles.lib/pebbles.lib.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/pebbles.lib.csproj#L102) | Embeds the GLSL as resources |
| [pebbles.lib/Assets/AssetUploadSubmission.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/AssetUploadSubmission.cs) | One-shot command buffer and fence used for uploads and the draw |
| [pebbles.lib/Workshop/WorkshopEditor.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopEditor.cs#L55-L72) | Calls `Refresh`/`Render` from `Update`; deferred release |
| [pebbles.lib/Workshop/WorkshopCanvas.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopCanvas.cs#L27-L54) | Draws the image and uses its matrix for the overlay |
| [scope/ground-clutter.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/ground-clutter.md#L87-L100) | Authors' summary of the preview contract |

## Snippets

The pre-GUI render with change detection, device wait and replace-then-dispose:

```csharp
// pebbles.lib/Preview/WorkshopPreview.cs L56-L94
    /// <summary>
    /// Call in BeforeGui, before this frame can record an ImGui reference to Texture.
    /// Changed previews conservatively wait for prior GPU/UI readers. The preview submission
    /// finishes before publishing its image. Unchanged previews do no GPU work and never wait.
    /// </summary>
    public void Render(WorkshopView view, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failedRefresh || (_pending == null && _scene == null)) return;
        var matrix = WorkshopMath.ViewProjection(view, width, height);
        // Keep the logical panel aspect for overlay/picking, even if GPU resolution is capped.
        width = Math.Clamp(width, 64, 1536); height = Math.Clamp(height, 64, 1536);
        var eye = view.Eye;
        if (!Finite(eye) || !Finite(matrix)) { Status = "Preview camera contains invalid values."; _ready = false; return; }
        if (_ready && _pending == null && _target?.Width == width && _target.Height == height &&
            matrix == _lastMatrix && eye == _lastEye) return;

        PreviewScene? nextScene = null;
        PreviewTarget? nextTarget = null;
        try
        {
            _renderer ??= Program.GetRenderer();
            // Retires submitted ImGui consumers too, unlike a fence for the preview producer alone.
            // BeforeGui scheduling is essential: there must be no unsubmitted current-frame UI use.
            _renderer.Device.WaitIdle();
            _pipeline ??= new PreviewPipeline(_renderer);
            if (_pending != null) nextScene = new PreviewScene(_renderer, _pipeline, _pending);
            var drawScene = nextScene ?? _scene!;
            bool resize = _target == null || _target.Width != width || _target.Height != height;
            if (resize) nextTarget = new PreviewTarget(_renderer, width, height);
            var drawTarget = nextTarget ?? _target!;
            drawTarget.Render(_pipeline, drawScene, matrix, eye);
            if (nextScene != null) { _scene?.Dispose(); _scene = nextScene; nextScene = null; }
            if (nextTarget != null) { _target?.Dispose(); _target = nextTarget; nextTarget = null; }
            _pending = null;
            _lastMatrix = matrix; _lastEye = eye;
            _ready = true;
            Status = "Studio preview · terrain lighting and placement are evaluated on Apply.";
        }
```

The offscreen pass on its own command buffer, then registration with the ImGui backend:

```csharp
// pebbles.lib/Preview/PreviewTarget.cs L55-L102
    internal void Render(PreviewPipeline pipeline, PreviewScene scene, Matrix4x4 viewProjection, Vector3 eye)
    {
        using var submission = new AssetUploadSubmission(_renderer);
        var command = submission.Command;
        ImageTransition[] transitions =
        [
            new(_colorImage!.Value.VkImage, _initialized ? ImageBarrierInfo.Presets.SampledReadF : ImageBarrierInfo.Presets.Undefined,
                ImageBarrierInfo.Presets.ColorAttachmentWrite),
            new(_depth!.Value.VkImage, _initialized ? ImageBarrierInfo.Presets.DepthStencilAttachmentWrite : ImageBarrierInfo.Presets.Undefined,
                ImageBarrierInfo.Presets.DepthStencilAttachmentWrite, ImageTransition.Subresource(VkImageAspectFlags.DepthBit))
        ];
        command.TransitionImages2(transitions);
// ...
        command.BeginRendering(in rendering);
        try
        {
            var viewport = new VkViewport { Width = Width, Height = Height, MinDepth = 0, MaxDepth = 1 };
            var scissor = new VkRect2D(extent);
            command.SetViewport(0, new ReadOnlySpan<VkViewport>(ref viewport));
            command.SetScissor(0, new ReadOnlySpan<VkRect2D>(ref scissor));
            scene.Record(command, pipeline, viewProjection, eye);
        }
        finally { command.EndRendering(); }
        var sampled = new ImageTransition(_colorImage!.Value.VkImage,
            ImageBarrierInfo.Presets.ColorAttachmentWrite, ImageBarrierInfo.Presets.SampledReadF);
        command.TransitionImages2(new ReadOnlySpan<ImageTransition>(ref sampled));
        submission.SubmitAndWait();
        _initialized = true;
        if (Texture._TexID.Value == IntPtr.Zero)
            Texture = ImGuiBackend.Vulkan.AddTexture(pipeline.Sampler, _colorView!.Value.VkImageView);
    }
```

A render-pass-less pipeline and runtime GLSL compilation from embedded resources:

```csharp
// pebbles.lib/Preview/PreviewPipeline.cs L104-L135
            VkFormat color = ColorFormat;
            var rendering = new VkPipelineRenderingCreateInfo
            {
                ColorAttachmentCount = 1, ColorAttachmentFormats = &color, DepthAttachmentFormat = DepthFormat
            };
            var info = new VkGraphicsPipelineCreateInfo
            {
                Layout = Layout, Next = &rendering, RenderPass = VkRenderPass.NullHandle,
                StageCount = stages.Length, Stages = stages.AsPointer(),
                DynamicState = _renderer.DynamicStateInfo, ViewportState = _renderer.ViewportState,
                VertexInputState = input, InputAssemblyState = Presets.InputAssembly.TriangleList,
                RasterizationState = Presets.Rasterization.Fill.CullNone, DepthStencilState = &depth,
                ColorBlendState = Presets.BlendState.BlendNone, MultisampleState = &samples
            };
            Pipeline = device.CreateGraphicsPipeline(default(VkPipelineCache), info, null);
        }
        finally
        {
            if (!vertex.IsNull()) device.DestroyShaderModule(vertex, null);
            if (!fragment.IsNull()) device.DestroyShaderModule(fragment, null);
        }
    }

    private VkShaderModule Compile(string suffix, VkShaderStageFlags stage)
    {
        var assembly = typeof(PreviewPipeline).Assembly;
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing preview shader {name}.");
        using var reader = new StreamReader(stream);
        return ShaderModuleUtils.FromString(_renderer.Device, Encoding.UTF8.GetBytes(reader.ReadToEnd()), stage,
            null, Encoding.UTF8.GetBytes(name + "\0"));
    }
```

Studio fragment shading with derivative-based normal mapping:

```glsl
// pebbles.lib/Preview/Workshop.frag.glsl L17-L41
void main() {
    if (pc.maps.w > .5 && texture(opacityMap, textureUv).r < .5) discard;
    vec4 sampleColor = pc.maps.x > .5 ? texture(diffuseMap, textureUv) : vec4(.62,.67,.73,0);
    // A neutral studio preview: source colors bypass clutter's terrain-color alpha convention.
    vec3 base = pc.options.y > .5 ? sampleColor.rgb : mix(pow(max(sampleColor.rgb,vec3(0)),vec3(2.2)),sampleColor.rgb,sampleColor.a);
    vec3 n = normalize(worldNormal);
    if (!gl_FrontFacing) n = -n;
    if (pc.maps.y > .5) {
        vec3 dp1=dFdx(worldPosition), dp2=dFdy(worldPosition);
        vec2 duv1=dFdx(textureUv), duv2=dFdy(textureUv);
        vec3 t=cross(dp2,n)*duv1.x+cross(n,dp1)*duv2.x;
        vec3 b=cross(dp2,n)*duv1.y+cross(n,dp1)*duv2.y;
        float norm=max(dot(t,t),dot(b,b));
        if (norm > 1e-12) n=normalize(mat3(t*inversesqrt(norm),b*inversesqrt(norm),n)*(texture(normalMap,textureUv).xyz*2-1));
    }
    vec3 pbr=pc.maps.z>.5?texture(pbrMap,textureUv).rgb:vec3(1,.65,0);
    vec3 v=normalize(pc.camera.xyz-worldPosition);
    vec3 light=normalize(vec3(.5,.85,.7));
    vec3 fill=normalize(vec3(-.8,.3,-.4));
    float diffuse=max(dot(n,light),0)*.75+max(dot(n,fill),0)*.2+.22;
    float spec=pow(max(dot(n,normalize(light+v)),0),mix(128,4,clamp(pbr.g,0,1)));
    vec3 result=base*diffuse*pbr.r+mix(vec3(.04),base,pbr.b)*spec*.7;
    if(pc.options.x>.5) result+=base*(1-texture(thicknessMap,textureUv).r)*max(dot(-n,light),0)*.25;
    color=vec4(clamp(result,0,1),1);
}
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Program.GetRenderer()`; `OnFrame` order; `RenderGame` ImGui pass; `PostRender` submit | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2343) | Renderer access and the timing argument (ImGui pass at L4950) |
| `Renderer.Device`, `Allocator`, `Graphics`, `DynamicStateInfo`, `ViewportState`, `MaxFramesInFlight`, `TrySubmitFrame` | [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L16-L30) | Device, images, shared dynamic state; frames go to the same `Graphics` queue (L500-L516) |
| `ImGuiBackend.Vulkan` | [current/decomp/KSA/ImGuiBackend.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackend.cs#L23-L46) | Backend singleton (descriptor pool of 1000 sets) |
| `ImGuiBackendVulkanImpl.AddTexture(VkSampler, VkImageView, VkImageLayout = ShaderReadOnlyOptimal)`, `RemoveTexture` | [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L1119-L1164) | Turn the color view into an `ImTextureRef` and free it |
| `ImageBarrierInfo.Presets.Undefined`, `ColorAttachmentWrite`, `DepthStencilAttachmentWrite`, `SampledReadF` | [current/decomp/KSA.Rendering/ImageBarrierInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ImageBarrierInfo.cs#L9-L33) | Layout and stage presets |
| `CommandBufferEx.TransitionImages2` | [current/decomp/KSA/CommandBufferEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CommandBufferEx.cs#L10-L25) | Synchronization2 image barriers |
| `ShaderModuleUtils.FromString(Device, ReadOnlySpan<byte>, VkShaderStageFlags, CompileOptions?, ReadOnlySpan<byte>)` | [current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L79-L100) | Runtime GLSL to SPIR-V |
| `VkUtils.StageAndUploadToBuffer<T>` | [current/decomp/RenderCore/VkUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/VkUtils.cs#L101) | Vertex and index uploads |
| `CommandBuffer.BeginRendering` used natively | [current/decomp/KSA.Rendering.Particles/ParticleRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ParticleRenderer.cs#L238) | Confirms dynamic rendering is available |

## Pitfalls and version notes

- **GPU drain on every change.** Orbiting, zooming or resizing changes the matrix or size every frame,
  so each such frame does a `Device.WaitIdle()` plus a blocking submit. The README calls this
  "conservative synchronization" and warns of hitches with large meshes or while resizing.[^pebbles-readme]
- **Only safe before GUI emission.** `Render` must not be called after anything in the current frame
  has referenced `Texture`; the class documents this and the editor enforces it by calling it only
  from `Update`.
- **Shader compile cost.** The first render compiles both shaders with shaderc on the main thread.
- **Format assumptions.** Color is `R8G8B8A8UNorm`, not sRGB; the shader output is written as-is
  and ImGui displays it directly, so the preview's brightness is not the game's tonemapped output.
- **Borrowed textures.** Descriptor sets point at image views owned by game textures or the GLB cache;
  the cache must outlive the scene, which is why release is ordered through `Update`.
- **Index enum.** KSA 5438 renamed the index-type member; the preview uses `VkIndexType.UInt32`
  (authors' note).[^scope-ground-clutter]
- **No game state.** The preview has no terrain, sun, atmosphere or shadows; Pebbles labels it a
  "studio preview" and evaluates real lighting only after Apply.
- Managed tests cannot exercise any of this; it is covered only by in-game acceptance.

## See also

- [Collider workshop editor](./pebbles-workshop-editor.md) (the window that hosts the image)
- [Asset upload submission](./pebbles-asset-upload-submission.md) (the one-shot submit-and-wait primitive)
- [GLB material conversion](./pebbles-glb-material-conversion.md) (where the preview's GLB textures come from)
- [Live part-mounted cameras](./hot-pursuit-part-mounted-cameras.md) (the opposite approach: leasing stock secondary viewports)
- [Textured quad render pass](./thug-life-textured-quad-render-pass.md) (drawing inside the game's own frame instead)
- [Pebbles sub-mod](../submods/pebbles.md) and [mod overview](../overview.md)
- [Private colour and depth targets with a standalone dynamic-rendering pipeline](../../../howto/rendering/vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/build-a-vulkan-pipeline-for-the-scene-target-3.md) - how-to article demonstrating this feature
- [Publish new GPU resources after a fence, retire old ones after WaitIdle](../../../howto/rendering/vulkan-passes/free-vulkan-resources-safely/free-vulkan-resources-safely-3.md) - how-to article demonstrating this feature
- [Private images, runtime GLSL and a fenced submit from StarMapBeforeGui](../../../howto/rendering/vulkan-passes/submit-your-own-vulkan-commands/submit-your-own-vulkan-commands-4.md) - how-to article demonstrating this feature
- [Compile GLSL at runtime: a premultiplied-alpha fragment shader paired with the stock unlit vertex shader](../../../howto/shaders/glsl-workflow/compile-glsl-at-runtime/compile-glsl-at-runtime-1.md) - how-to article demonstrating this feature
- [Compile GLSL at runtime: derivative normal mapping in an embedded-resource preview shader](../../../howto/shaders/glsl-workflow/compile-glsl-at-runtime/compile-glsl-at-runtime-3.md) - how-to article demonstrating this feature

[^scope-ground-clutter]: scope/ground-clutter.md (authors' integration notes)
[^workshop-preview]: pebbles.lib/Preview/WorkshopPreview.cs
[^preview-geometry]: pebbles.lib/Preview/PreviewGeometry.cs
[^preview-pipeline]: pebbles.lib/Preview/PreviewPipeline.cs
[^pebbles-csproj]: pebbles.lib/pebbles.lib.csproj
[^ksa-shader-module-utils]: RenderCore.ShaderModuleUtils (decompiled, build 2026.10.10.5554)
[^core-renderer]: Core.Renderer (decompiled, build 2026.10.10.5554)
[^preview-scene]: pebbles.lib/Preview/PreviewScene.cs
[^ksa-vk-utils]: RenderCore.VkUtils (decompiled, build 2026.10.10.5554)
[^preview-target]: pebbles.lib/Preview/PreviewTarget.cs
[^ksa-imgui-backend-vulkan]: KSA.ImGuiBackendVulkanImpl (decompiled, build 2026.10.10.5554)
[^ksa-image-barrier-info]: KSA.Rendering.ImageBarrierInfo (decompiled, build 2026.10.10.5554)
[^ksa-command-buffer-ex]: KSA.CommandBufferEx (decompiled, build 2026.10.10.5554)
[^ksa-particle-renderer]: KSA.Rendering.Particles.ParticleRenderer (decompiled, build 2026.10.10.5554)
[^workshop-frag]: pebbles.lib/Preview/Workshop.frag.glsl
[^workshop-vert]: pebbles.lib/Preview/Workshop.vert.glsl
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^pebbles-submod]: pebbles.lib/PebblesSubmod.cs
[^pebbles-readme]: pebbles.lib/README.md
