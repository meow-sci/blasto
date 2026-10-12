---
type: How-To
title: "Bend the picture: barrel curve within UvRect and channel offsets in an ImGuiShader"
description: Run a custom fragment shader over one ImGui window by tagging its draw list with a no-op marker callback and declaring an ImGuiShader asset, with barrel distortion and luminance-masked chromatic aberration inside the window's UV rectangle
category: shaders
group: screen-effects
topic: bend-the-picture-with-crt-curvature-and-chromatic-aberration
approach: 1
difficulty: intermediate
prerequisites: [starmap, shaderextensions, kittenextensions, harmony]
demonstrated_by: [mods/ksa-aircrafthud/features/imgui-window-hud-shader]
techniques: [glsl-shader, imgui-drawlist, xml-asset]
hooks: [harmony-postfix KSA.Program.OnFrameEditor, xml-asset ImGuiShader]
lifecycle: [frame:viewports, render:imgui]
game_types: [KSA.Program, KSA.KeyHash, Brutal.ImGuiApi.ImDrawListPtr]
tags: [crt, barrel-distortion, chromatic-aberration, imguishader, marker-callback, uvrect, aircrafthud]
---
KSA_AircraftHUD draws its flight symbology with ImGui and makes it look projected onto curved glass without any of that in the C# drawing code. The mod declares an `ImGuiShader` asset, drops a marker callback into the HUD window's draw list, and ShaderExtensions renders that one draw list offscreen, runs the mod's fragment shader over it and replaces the list with one textured quad. This is the complete recipe
for the shader and the tagging. Prerequisites: ShaderExtensions with KittenExtensions Continued; the mod needs no assembly reference to ShaderExtensions.

## Background: how the game does it

ImGui hands the backend one `ImDrawList` per window and nothing says which window it is, so ShaderExtensions uses the only channel that survives into draw data, a draw-list callback command whose user data holds a marker hash and the key of an `<ImGuiShader>` asset. The callback body is empty; the payload is read at render time. The mechanics of the host side are in the [third article](bend-the-picture-with-crt-curvature-and-chromatic-aberration-3.md).
The fragment shader receives the window rendered alone at `set = 0, binding = 0`, the shared time block at binding 1, extra bindings from binding 2, and per-window varyings: `In.Px`, `In.Uv`, `PxRect` and `UvRect`.

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Marker callback recorded | In the HUD's draw code, inside the `Program.OnFrameEditor` postfix, between `ImGui.NewFrame` and `ImGui.Render`; runs even when `DrawUI` is false | `frame:viewports` |
| Offscreen replay, shader pass, draw-list rewrite | ShaderExtensions' replacement for the swapchain `BeginRenderPass`, after `ImGui.Render` | `frame:render` |
| The shaded quad is drawn | `ImGuiBackend.Vulkan.RenderDrawData` | `render:imgui` |

The HUD is therefore never blurred by scene post-processing: those passes work on the scene image and the shaded quad is drawn on top by ImGui. Only the window that called `CustomShader` is affected; child windows are not included. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Declare the asset.** The vertex stage is `ImGuiVertexPost` (which the README describes as one quad over the bounding box of the window's draw commands; see the third article for the caveat that it is not shipped at the pinned ref); the fragment is a `ShaderEx` and can carry extra bindings:

   ```xml
   <!-- HudAssests.xml L21-L26 -->
   	<ImGuiShader Id="HudShader">
   	    <Vertex Id="ImGuiVertexPost" />
   		<Fragment Path="Shaders/UI_PP_HUD.frag" >
   			<AircraftHUDBufferAsset Id="HudTestBuffer" Size="1" />
   		</Fragment>
   	</ImGuiShader>
   ```

   Source: [HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml#L21-L26).

2. **Copy the marker helper into your assembly** (it can live in your own namespace). It adds a callback whose user data is `uint2(markerHash, shaderKey)`; ImGui copies the 8 bytes, and the empty delegate must stay alive for the frame:

   ```csharp
   // ShaderExtensionsHelpers.cs L37-L46
       internal static class SxImGui
       {
           internal static readonly KeyHash MarkerKey = KeyHash.Make("SxImGuiShader");
           internal static unsafe void CustomShader(KeyHash key)
           {
               var data = new uint2(MarkerKey.Code, key.Code);
               ImGui.GetWindowDrawList().AddCallback(DummyCallback, (Brutal.Pointers.Ptr)(&data), ByteSize.Of<uint2>().Bytes);
           }
           private static unsafe void DummyCallback(ImDrawList* parent_list, ImDrawCmd* cmd) { }
       }
   ```

   Source: [ShaderExtensionsHelpers.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ShaderExtensionsHelpers.cs#L37-L46).

3. **Call it inside the window, right after `ImGui.Begin`, only when you want the effect** (the HUD does this in IVA or when "Show Effects" is on; in other camera modes the window is drawn by plain ImGui). Cache the `KeyHash`; the mod recomputes it every frame, which the README advises against:

   ```csharp
   // AircraftHUDMain.cs L1019-L1027
               if (CamMode.Equals(CameraMode.IVA) || effectsOutsideIVA)
               {
                   SxImGui.CustomShader(HudShader);
                   SetPostProcess(1, FrameTime);
               }
               else
               {
                   SetPostProcess(0, FrameTime);
               }
   ```

   Source: [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1019-L1027).

4. **Pin the bounds with a nearly invisible full-window rectangle.** The quad covers the bounding box of the drawn vertices, so a window that only draws a few symbols would shrink `UvRect` and move the centre of the curvature. The HUD draws an almost transparent rectangle over the whole window:

   ```csharp
   // AircraftHUDMain.cs L1038-L1039
               // Workaround: It seems that ImGui window gets clipped, draw almost transparent rect to force full size
               ImDrawListExtensions.AddRectFilled(draw_list, new float2(0f, 0f), window_size, new ImColor8(0, 0, 0, 1), 0);
   ```

   Source: [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1038-L1039). The extents are taken from the draw list's vertices after each vertex is clamped to its command's clip rectangle and the clip rectangle to the framebuffer, so they never exceed the screen.

5. **Write the shader.** Remap `In.Uv` within `UvRect` with a barrel distortion, then sample with the aberration. Alpha is kept so the transparent background stays transparent:

   ```glsl
   // Shaders/UI_Functions.glsl L11-L24
   vec2 CurvedUV_UI(vec2 uv, vec4 uvRect, float strength, float scale)
   {
       vec2 local = (uv - uvRect.xy) / (uvRect.zw - uvRect.xy);

       vec2 p = local * 2.0 - 1.0;
       p *= scale;

       float r2 = dot(p, p);
       p *= 1.0 + strength * r2;

       local = p * 0.5 + 0.5;

       return uvRect.xy + local * (uvRect.zw - uvRect.xy);
   }
   ```

   ```glsl
   // Shaders/UI_Functions.glsl L37-L66
   vec4 ChromaticAberrationUI(sampler2D tex, vec2 uv, vec4 uvRect, float amount)
   {
       vec2 local = (uv - uvRect.xy) / (uvRect.zw - uvRect.xy);
       vec2 dir = local * 2.0 - 1.0;

       float r2 = dot(dir, dir);
       vec2 offset = dir * r2 * amount;

       vec4 center = textureLod(tex, uv, 0);
       vec4 red    = textureLod(tex, uv + offset, 0);
       vec4 blue   = textureLod(tex, uv - offset, 0);

       vec3 baseColor = center.rgb / max(center.a, 1e-5);

       float lumCenter = Luminance(center.rgb / max(center.a, 1e-5));
       float lumRed    = Luminance(red.rgb    / max(red.a,    1e-5));
       float lumBlue   = Luminance(blue.rgb   / max(blue.a,   1e-5));

       float lum = max(lumCenter, max(lumRed, lumBlue));

       float mask = smoothstep(0.15, 0.75, lum);

       vec3 color = center.rgb;
       color.r = mix(center.r, red.r, mask);
       color.b = mix(center.b, blue.b, mask);

       float alpha = mix(center.a, max(center.a, max(red.a, blue.a)), mask);

       return vec4(color, alpha);
   }
   ```

   ```glsl
   // Shaders/UI_PP_HUD.frag L25-L41
   void main()
   {
       vec2 curvedUv = CurvedUV_UI(In.Uv, UvRect, -0.15, 1.15);
       vec4 color = ChromaticAberrationUI(imguiTex, curvedUv, UvRect, -0.003);
       
       vec2 texelSize = 1.0 / vec2(textureSize(imguiTex, 0));

   //    vec4 glow = GaussianBlurUI(imguiTex, curvedUv, texelSize, blurRadius, blurSpread, threshold);

   //    vec4 glow = SpiralGlowUI(imguiTex, curvedUv, texelSize,
   //        20.0,   // radius in pixels
   //        128,     // samples
   //        threshold
   //    );
   //    color += glow * intensity;

       outColor = vec4(color.rgb, color.a);
   ```

   Sources: [Shaders/UI_Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/UI_Functions.glsl#L11-L24), [Shaders/UI_Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/UI_Functions.glsl#L37-L66), [Shaders/UI_PP_HUD.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/UI_PP_HUD.frag#L25-L41). The aberration shifts red and blue along the radius scaled by `r^2` and blends them in only where luminance is high, so dim pixels and the transparent background keep their colour and alpha. A bloom (`GaussianBlurUI`, `SpiralGlowUI`) is written but commented out. The shader file starts with `#include "UI_Functions.glsl"`, an include-guarded file beside it.

## Complete example

| File | Role |
|---|---|
| [HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml) | `ImGuiShader` asset |
| [ShaderExtensionsHelpers.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ShaderExtensionsHelpers.cs) | `SxImGui.CustomShader` marker |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs) | Per-frame selection and the full-size rectangle |
| [Shaders/UI_PP_HUD.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/UI_PP_HUD.frag), [Shaders/UI_Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/UI_Functions.glsl) | The shader and its helpers |

## Variations and gotchas

- **The vertex shader is not shipped.** `ImGuiVertexPost` is referenced here and in the README but defined by none of the reference repositories at their pinned refs (see the third article).
- **Descriptor layout.** The mod declares `HudTestBuffer` at `set = 1, binding = 0`, but ImGui shader extras live on set 0, from binding 2 on the pinned commit. The block is not read, so it does nothing; copy the README's layout instead.
- **Not in the editor.** `RenderEditor` is not patched, so tagged windows draw unshaded there.
- **Hidden HUD.** `ImGuiPreRender` runs whether or not `DrawUI` is set; the HUD window is still submitted from the `OnFrameEditor` postfix, so the curved shader keeps applying in IVA.
- **Cost.** Two full-viewport RGBA8 targets are used by each tagged window's renderer pair.

## See also

- Siblings: [glass composite pass](bend-the-picture-with-crt-curvature-and-chromatic-aberration-2.md), [host mechanics](bend-the-picture-with-crt-curvature-and-chromatic-aberration-3.md), [overview](overview.md)
- [Curved, chromatic HUD window via an ImGuiShader](../../../../mods/ksa-aircrafthud/features/imgui-window-hud-shader.md)
- [Render ImGui UI into a Vulkan texture for use in the 3D world](../../../rendering/textures-and-decals/render-imgui-into-a-texture/overview.md), [Show a PNG or runtime-generated image in an ImGui window](../../../rendering/textures-and-decals/show-an-image-in-imgui/overview.md)
