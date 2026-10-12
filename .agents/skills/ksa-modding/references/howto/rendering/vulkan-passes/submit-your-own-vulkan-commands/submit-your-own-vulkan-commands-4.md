---
type: How-To
title: Private images, runtime GLSL and a fenced submit from StarMapBeforeGui
description: Render a mesh into private colour and depth images with a free-standing pipeline and show the finished image in an ImGui window as an editor preview
category: rendering
group: vulkan-passes
topic: submit-your-own-vulkan-commands
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/pebbles-workshop-offscreen-preview]
techniques: [vulkan-pipeline, glsl-shader, imgui-window, starmap-lifecycle]
hooks: [starmap StarMapBeforeGui, starmap StarMapAfterGui, harmony-prefix KSA.Program.OnDrawUiConsole]
lifecycle: [frame:draw-ui, render:imgui]
game_types: [KSA.ImGuiBackendVulkanImpl, RenderCore.ShaderModuleUtils, KSA.Program, Core.Renderer]
tags: [vulkan, offscreen, preview, imgui-image, runtime-glsl, wait-idle]
---

Outcome: a lit, textured, depth-tested 3D preview of your own mesh inside an ImGui window, drawn by a
private renderer that never touches the game's scene, viewports or cameras. The preview owns its
colour and depth images, its pipeline, shaders, buffers and descriptors; the finished colour image is
registered with the ImGui Vulkan backend and drawn with `ImGui.Image` or a draw-list `AddImage`.
Demonstrated by unscience's Pebbles Workshop editor ([Independent offscreen Vulkan preview rendered before the GUI and shown as an ImGui image](../../../../mods/unscience/features/pebbles-workshop-offscreen-preview.md)).
For the free-standing pipeline in isolation see [build a Vulkan pipeline for the scene target](../build-a-vulkan-pipeline-for-the-scene-target/overview.md) (approach 3).

## Background: how the game does it

The game records dynamic rendering itself (for example `ParticleRenderer` calls
`commandBuffer.BeginRendering`), so no extension or patch is needed to open a rendering scope in your own
command buffer. KSA's ImGui backend turns an image view into a texture with
`ImGuiBackendVulkanImpl.AddTexture(VkSampler, VkImageView, VkImageLayout = ShaderReadOnlyOptimal)`, which
allocates a combined-image-sampler descriptor set from the backend's pool and returns it as an `ImTextureRef`
[current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L1119-L1126); `RemoveTexture` returns the set to the pool
[current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L1128-L1131). The ImGui pass reads the image at the end of
`RenderGame` (`render:imgui`), after the composite.

## Lifecycle and timing

| Step | Hook | Frame point |
|---|---|---|
| Prepare geometry, then wait for the device, build, record, submit, fence | `[StarMapBeforeGui]` (and the `OnDrawUiConsole` replay when the HUD is hidden) | `frame:draw-ui`, before any GUI code this frame |
| `AddImage(texture, ...)` into the editor window | `[StarMapAfterGui]` | `frame:draw-ui`, later the same frame |
| ImGui samples the image | `ImGuiBackend.Vulkan.RenderDrawData` | `render:imgui` (step 25 of the `RenderGame` table in [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence)) |

Why before the GUI and with `Device.WaitIdle()`: the previous frame's ImGui pass may still be reading the
old image (frames in flight), and waiting on the preview's own fence would not cover that reader. At the
start of `OnDrawUiFrame` the current frame has recorded nothing yet, so after the wait no reader exists and the
preview can be re-rendered, resized or freed.

## Steps

1. Detect change. Return immediately when the clamped size, view-projection matrix and eye equal the last
   render. Wait for the device only when something changed, build the next scene and target, render, and
   only then replace and dispose the old ones. A failure disposes the new objects and stops retrying until the
   next explicit refresh.

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

2. Record the offscreen pass on a private one-shot command buffer (the approach 1 class): transition
   colour to attachment, `BeginRendering` with cleared colour and depth, set dynamic viewport and scissor,
   draw, `EndRendering`, transition colour to `SampledReadF`, `SubmitAndWait`, and on first use register the
   view with the ImGui backend.

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
       var color = new VkRenderingAttachmentInfo
       {
           ImageView = _colorView!.Value.VkImageView, ImageLayout = VkImageLayout.ColorAttachmentOptimal,
           LoadOp = VkAttachmentLoadOp.Clear, StoreOp = VkAttachmentStoreOp.Store,
           ClearValue = new VkClearValue { Color = new VkClearColorValue { Float32 = new float4(.055f, .068f, .086f, 1) } }
       };
       var depth = new VkRenderingAttachmentInfo
       {
           ImageView = _depthView!.Value.VkImageView, ImageLayout = VkImageLayout.DepthStencilAttachmentOptimal,
           LoadOp = VkAttachmentLoadOp.Clear, StoreOp = VkAttachmentStoreOp.DontCare,
           ClearValue = new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 1, Stencil = 0 } }
       };
       var extent = new VkExtent2D(Width, Height);
       var rendering = new VkRenderingInfo
       {
           RenderArea = new VkRect2D(extent), LayerCount = 1, ColorAttachmentCount = 1,
           ColorAttachments = &color, DepthAttachment = &depth
       };
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

3. Build a render-pass-less pipeline: chain `VkPipelineRenderingCreateInfo` into `Next`, set
   `RenderPass = NullHandle`, and compile GLSL at runtime with `ShaderModuleUtils.FromString` using the
   embedded resource name plus a NUL as the debug name. Destroy the shader modules right after pipeline
   creation.

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

4. Draw the image in the editor window from `[StarMapAfterGui]` with the registered `ImTextureRef`.
   Before freeing the image, call `RemoveTexture` first.

## Offscreen scene sub-step: lighting without vertex tangents

The fragment shader needs normal mapping but the preview mesh has no tangents, so it builds a cotangent
frame from screen-space derivatives:

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

The preview is a neutral studio look (fixed key and fill light plus ambient) and writes `R8G8B8A8UNorm`
as-is, so its brightness is not the game's tonemapped output.

## Complete example

| File | Role |
|---|---|
| [pebbles.lib/Preview/WorkshopPreview.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/WorkshopPreview.cs) | Orchestration, change detection, status |
| [pebbles.lib/Preview/PreviewTarget.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewTarget.cs) | Private colour and depth images, the offscreen pass, ImGui registration |
| [pebbles.lib/Preview/PreviewPipeline.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewPipeline.cs) | Pipeline layout, descriptor layout, runtime GLSL |
| [pebbles.lib/Preview/PreviewScene.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewScene.cs#L31-L86) | Device-local buffers and descriptor sets per draw |
| [pebbles.lib/Preview/Workshop.frag.glsl](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/Workshop.frag.glsl) | Studio fragment shader |

## Variations and gotchas

- **GPU drain on every change.** Orbiting or resizing changes the matrix each frame, so each such frame
  does a `WaitIdle` plus a blocking submit; large meshes hitch.
- **Only safe before GUI emission.** Never call `Render` after anything in the current frame has referenced the texture.
- **Shader compile cost.** The first render compiles both shaders with shaderc on the main thread.
- **Borrowed textures.** Descriptor sets point at image views owned by game textures; keep them alive for as
  long as the scene's descriptor sets exist.
- **Index type.** The preview uses `VkIndexType.UInt32` (the member was renamed in an earlier KSA build).
- Release is deferred: `WorkshopEditor.Release()` only sets a flag, and the next `Update` disposes the preview
  with `WaitIdle` at the safe phase.

## See also

- [Overview](overview.md)
- [Approach 1: one-shot command buffer and fence](submit-your-own-vulkan-commands-1.md)
- [Approach 2: fence ring](submit-your-own-vulkan-commands-2.md)
- [Approach 3: transient pool for readback](submit-your-own-vulkan-commands-3.md)
- [Independent offscreen Vulkan preview rendered before the GUI and shown as an ImGui image](../../../../mods/unscience/features/pebbles-workshop-offscreen-preview.md)
- [Free mod-owned Vulkan resources without device loss](../free-vulkan-resources-safely/overview.md), [Show a PNG or runtime-generated image in an ImGui window](../../textures-and-decals/show-an-image-in-imgui/overview.md)
