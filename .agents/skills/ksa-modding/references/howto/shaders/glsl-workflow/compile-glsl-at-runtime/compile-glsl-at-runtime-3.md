---
type: How-To
title: "Compile GLSL at runtime: derivative normal mapping in an embedded-resource preview shader"
description: Ship GLSL as embedded resources, compile them with FromString for a private offscreen pipeline, and normal-map meshes that have no tangents using screen-space derivatives
category: shaders
group: glsl-workflow
topic: compile-glsl-at-runtime
approach: 3
difficulty: advanced
prerequisites: [starmap, vulkan]
demonstrated_by: [mods/unscience/features/pebbles-workshop-offscreen-preview]
techniques: [glsl-shader, vulkan-pipeline]
hooks: [starmap StarMapBeforeGui, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, render:imgui]
game_types: [RenderCore.ShaderModuleUtils, RenderCore.SimpleVkMesh]
tags:
  - glsl
  - shaderc
  - fromstring
  - embedded-resource
  - normal-mapping
  - derivatives
  - offscreen-preview
  - unscience
---
The Pebbles Workshop in unscience shows the chosen mesh lit, textured and depth-tested in a small private renderer with its own colour and depth images, then hands that image to ImGui. Its shaders are plain `.glsl` files embedded in the assembly and compiled at runtime. The fragment shader normal-maps imported meshes that carry no tangents by building a
cotangent frame from screen-space derivatives. This article covers the embedded-resource workflow and the derivative trick; the offscreen-target and ImGui-registration side is in [Render ImGui UI into a Vulkan texture for use in the 3D world](../../../rendering/textures-and-decals/render-imgui-into-a-texture/overview.md) and [Build a Vulkan pipeline compatible with KSA's scene render target](../../../rendering/vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/overview.md).

## Background: how the game does it

`ShaderModuleUtils.FromString` accepts any UTF-8 GLSL source, so the source does not have to live on disk beside the mod. Embedded resources keep the shader inside the assembly (no file-path assumptions, nothing to ship separately) at the cost of no hot reload. Because the preview shaders include nothing from the game, the debug name is only for diagnostics; the mod uses the
resource name plus a NUL anyway. Pipelines drawn into a private target use dynamic rendering with the target's own formats ([current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L79-L115)).

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Geometry prepared, preview rendered (device wait, one-shot command buffer, fence) | `WorkshopEditor.Update`, called from unscience's `[StarMapBeforeGui]` | `frame:draw-ui` |
| The image is added to the editor window | `WorkshopEditor.Draw`, from `[StarMapAfterGui]` | `frame:draw-ui` |
| The preview image is sampled | `ImGuiBackend.Vulkan.RenderDrawData`, at the end of `RenderGame` | `render:imgui` |
| Shaders compile | The first render builds `PreviewPipeline` lazily | `frame:draw-ui` |

The preview waits for the device before changing its image, because the previous frame's ImGui pass may still be reading it (`MaxFramesInFlight = 2`). See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Embed the GLSL in the assembly:**

   ```xml
   <!-- pebbles.lib/pebbles.lib.csproj L102 -->
     <ItemGroup><EmbeddedResource Include="Preview/**/*.glsl" /></ItemGroup>
   ```

   Source: [pebbles.lib/pebbles.lib.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/pebbles.lib.csproj#L102). Name the files by stage (`Workshop.vert.glsl`, `Workshop.frag.glsl`) so one lookup by suffix finds each.

2. **Compile each resource with the resource name plus a NUL as debug name, and destroy the modules after the pipeline is created:**

   ```csharp
   // pebbles.lib/Preview/PreviewPipeline.cs L127-L135
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

   Source: [pebbles.lib/Preview/PreviewPipeline.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewPipeline.cs#L127-L135). `Build()` calls it for both stages, creates the graphics pipeline and disposes both modules in a `finally` ([pebbles.lib/Preview/PreviewPipeline.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewPipeline.cs#L83-L125)).

3. **Guard the memory layout the shaders assume.** The constructor checks `Marshal.SizeOf` and `OffsetOf` of the vertex (32 bytes) and push struct (112 bytes: matrix 0, camera 64, maps 80, options 96) so a layout mismatch fails loudly instead of rendering garbage ([pebbles.lib/Preview/PreviewPipeline.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewPipeline.cs#L44-L53)). The vertex shader receives those exact fields:

   ```glsl
   // pebbles.lib/Preview/Workshop.vert.glsl L1-L19
   #version 450
   layout(location=0) in vec3 position;
   layout(location=1) in vec3 normal;
   layout(location=2) in vec2 uv;
   layout(location=0) out vec3 worldPosition;
   layout(location=1) out vec3 worldNormal;
   layout(location=2) out vec2 textureUv;
   layout(push_constant) uniform PreviewPush {
       mat4 viewProjection;
       vec4 camera;
       vec4 maps;
       vec4 options;
   } pc;
   void main() {
       worldPosition = position;
       worldNormal = normal;
       textureUv = uv;
       gl_Position = pc.viewProjection * vec4(position, 1);
   }
   ```

   Source: [pebbles.lib/Preview/Workshop.vert.glsl](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/Workshop.vert.glsl).

4. **Normal-map without tangents.** Imported meshes carry position, normal and one UV only. Build the tangent frame in the fragment shader from the derivatives of position and UV (the cotangent-frame technique), skip it when the UV is degenerate, and flip the normal on back faces:

   ```glsl
   // pebbles.lib/Preview/Workshop.frag.glsl L17-L31
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
   ```

   Source: [pebbles.lib/Preview/Workshop.frag.glsl](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/Workshop.frag.glsl#L17-L31). The rest of `main` uses the packed map as AO (R), roughness (G, Blinn-Phong exponent 128 to 4) and metalness (B), a fixed key and fill light plus ambient, and a thickness-map back-light term ([pebbles.lib/Preview/Workshop.frag.glsl](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/Workshop.frag.glsl#L32-L41)).

5. **Flags in the push constant select map usage.** `maps` records which maps are real (x diffuse, y normal, z PBR, w opacity) and `options` whether the material uses source colours; unused slots are bound to an empty white texture, so one pipeline serves every material.

## Complete example

| File | Role |
|---|---|
| [pebbles.lib/Preview/PreviewPipeline.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewPipeline.cs) | Layout checks, descriptor and pipeline layouts, runtime shader compile |
| [pebbles.lib/Preview/Workshop.vert.glsl](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/Workshop.vert.glsl), [pebbles.lib/Preview/Workshop.frag.glsl](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/Workshop.frag.glsl) | Studio shading |
| [pebbles.lib/pebbles.lib.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/pebbles.lib.csproj) | Embeds the GLSL as resources |

## Variations and gotchas

- **Derivatives need uniform control flow;** the `discard` on opacity precedes them here, which can make derivatives undefined at alpha-test edges.
- **The tangent-free frame is an approximation** and breaks on degenerate UVs; the shader keeps the vertex normal when `norm` is tiny.
- **No hot reload.** An embedded shader change needs a rebuild of the mod; the preview rebuilds its pipeline only on dispose.
- **Failures stop the preview** until the next explicit refresh: the code disposes the new objects, marks the preview failed and stops retrying.
- **Studio look only.** The game's terrain tint and lighting apply only after the mod applies the recipe.

## See also

- Siblings: [stock vertex quad](compile-glsl-at-runtime-1.md), [decal pass with includes](compile-glsl-at-runtime-2.md), [overview](overview.md)
- [Independent offscreen Vulkan preview rendered before the GUI and shown as an ImGui image](../../../../mods/unscience/features/pebbles-workshop-offscreen-preview.md)
- [Render ImGui UI into a Vulkan texture for use in the 3D world](../../../rendering/textures-and-decals/render-imgui-into-a-texture/overview.md), [Build a Vulkan pipeline compatible with KSA's scene render target](../../../rendering/vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/overview.md)
