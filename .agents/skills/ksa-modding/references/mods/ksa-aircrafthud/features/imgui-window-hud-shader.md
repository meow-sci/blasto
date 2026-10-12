---
type: Mod Feature
title: Curved, chromatic HUD window via an ImGuiShader
description: Tags the HUD's ImGui draw list with a ShaderExtensions marker callback so the window is rendered offscreen and re-composited through UI_PP_HUD.frag, which barrel-curves it and adds luminance-masked chromatic aberration in IVA view
mod: ksa-aircrafthud
category: shaders
techniques: [glsl-shader, xml-asset, imgui-drawlist, post-processing]
hooks: [harmony-postfix KSA.Program.OnFrameEditor]
lifecycle: [frame:viewports, render:imgui]
game_types: [Brutal.ImGuiApi.ImDrawListExtensions, KSA.KeyHash, KSA.Program]
tags:
  - shaders
  - imgui
  - shaderextensions
  - imguishader
  - draw-callback
  - chromatic-aberration
  - barrel-distortion
  - glsl
---

In IVA view the HUD symbols look like they are projected onto curved glass: lines bow outward
toward the edges and bright symbols get red/blue fringes. None of that is in the C# drawing code.
The mod declares an `ImGuiShader` asset in XML and, while building the HUD window, drops a marker
callback into the window's draw list. ShaderExtensions finds the marker at render time, renders that
one draw list to an offscreen image, runs the mod's fragment shader over it, and replaces the draw
list with a single textured quad.[^ah-assets] [^ah-uipphud] [^sx-readme]

## How it works

1. **Asset.** [HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml#L21-L26) declares
   `<ImGuiShader Id="HudShader">` with `<Vertex Id="ImGuiVertexPost" />` (a vertex shader shipped in
   ShaderExtensions' release content, described in its README as one quad over the bounding box of
   the window's draw commands) and `<Fragment Path="Shaders/UI_PP_HUD.frag">`. The fragment is a
   `ShaderEx`, so it can carry extra bindings
   ([ShaderExtensions/ImGuiShader/ImGuiShaderReference.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiShaderReference.cs#L8-L29)).[^sx-imguiref]
2. **Marker.** `SxImGui.CustomShader(KeyHash)` adds an ImGui draw callback whose user data is
   `uint2(KeyHash.Make("SxImGuiShader").Code, shaderKey.Code)`. The callback body is empty; only the
   data matters ([ShaderExtensionsHelpers.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ShaderExtensionsHelpers.cs#L37-L46)).[^ah-sxhelpers]
3. **Selection per frame.** In `DrawHud`, right after `ImGui.Begin`, the mod calls
   `SxImGui.CustomShader(KeyHash.Make("HudShader"))` only when the camera is `IVA` or the
   "Show Effects" setting is on ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1015-L1027)). In
   other camera modes the window is drawn by plain ImGui.[^ah-main]
4. **Detection.** `ImGuiRenderers.Render` walks every viewport's draw lists, and
   `FindCustomRenderer` returns the shader key from the first command whose callback data starts with
   the marker hash
   ([ShaderExtensions/ImGuiShader/ImGuiRenderers.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiRenderers.cs#L68-L117)).[^sx-imguirenderers]
5. **Offscreen pass and swap.** `ImGuiPostRenderer.Render` draws the list offscreen, computes the
   pixel and UV bounds from the vertices, draws the fragment shader over those bounds into a second
   target, then calls `drawList.ResetForNewFrame()` and `AddImage` so ImGui composites the result
   ([ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs#L146-L198)).[^sx-imguipost]
   This is why the HUD fills its window with an almost-transparent rectangle: it pins the bounds, and
   with them `UvRect`, to the full screen (see
   [Fullscreen draw-list HUD](fullscreen-drawlist-hud.md)).
6. **Shader.** [Shaders/UI_PP_HUD.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/UI_PP_HUD.frag) samples the rendered window
   (`set 0, binding 0`), remaps `In.Uv` within `UvRect` with a barrel distortion
   (`CurvedUV_UI(..., -0.15, 1.15)`), and applies `ChromaticAberrationUI` with amount `-0.003`. The
   helper shifts red and blue samples along the radius, scaled by `r^2`, and blends them in only where
   luminance is high (`smoothstep(0.15, 0.75, lum)`), so dim pixels and the transparent background
   keep their color and alpha
   ([Shaders/UI_Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/UI_Functions.glsl#L11-L66)).[^ah-uifunctions] A bloom
   (`GaussianBlurUI`, `SpiralGlowUI`) is written but commented out in `main()`.
7. **Includes.** The `.frag` uses `#include "UI_Functions.glsl"`. KSA compiles GLSL through shaderc
   with an include callback ([current/decomp/RenderCore.Shaders/ShaderCompilerResolve.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Shaders/ShaderCompilerResolve.cs#L21-L35)),
   and the mod keeps its includes next to the shaders, with include guards.[^ksa-shadercompiler]

## Lifecycle and timing

- **`frame:viewports` (marker).** The callback is recorded into the HUD's draw list inside the
  `Program.OnFrameEditor` postfix, between `ImGui.NewFrame` and `ImGui.Render`.
- **`render:imgui` (shader).** ShaderExtensions transpiles `Program.RenderGame`: the
  `BeginRenderPass` before the game's final composite and `RenderDrawData` is replaced by
  `ImGuiPreRender`, which runs `ImGuiRenderers.Render` (all window shaders) and then the pre-ImGui
  post-process chain before re-opening the pass
  ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L150-L225)).[^sx-patches] The HUD
  is therefore never blurred by the canopy-glass passes: those work on the scene image, and the
  shaded HUD quad is drawn on top by ImGui.
- Only the window that called `CustomShader` is affected; child windows are not included (README
  limitation).
- The transpiler patches `RenderGame` only, not `RenderEditor`, and `ImGuiPreRender` runs whether or
  not `DrawUI` is set. With the UI hidden the HUD window is still submitted (see
  [Always-on ImGui hook from a Program.OnFrameEditor postfix](always-on-onframeeditor-hook.md)), so
  the curved shader keeps applying in IVA.

## Key code

| Path | Role |
|---|---|
| [HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml#L21-L26) | `ImGuiShader` asset |
| [ShaderExtensionsHelpers.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ShaderExtensionsHelpers.cs#L37-L46) | `SxImGui.CustomShader` marker |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1015-L1039) | Per-frame selection and full-size rectangle |
| [Shaders/UI_PP_HUD.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/UI_PP_HUD.frag) | Fragment shader |
| [Shaders/UI_Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/UI_Functions.glsl) | Curvature, aberration, unused glow helpers |

## Snippets

```xml
<!-- HudAssests.xml L21-L26 -->
	<ImGuiShader Id="HudShader">
	    <Vertex Id="ImGuiVertexPost" />
		<Fragment Path="Shaders/UI_PP_HUD.frag" >
			<AircraftHUDBufferAsset Id="HudTestBuffer" Size="1" />
		</Fragment>
	</ImGuiShader>
```

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

```glsl
// Shaders/UI_PP_HUD.frag L7-L28
layout(set=0, binding=0) uniform sampler2D imguiTex; // rendered ImGui Window
layout(location = 0) in struct {
  vec2 Px; // screen pixel coord
  vec2 Uv; // screen uv coord
} In;
layout(location = 4) flat in vec4 PxRect; // bounding pixel rect for window
layout(location = 8) flat in vec4 UvRect; // bounding uv rect for window

layout(set = 1, binding = 0) uniform HudTestBuffer {
  float BufferValue1;
};

// TO DO: expose params to C#
const int blurRadius = 16;
const int blurSpread = 8;
const float threshold = 0.1;
const float intensity = 3.0;

void main()
{
    vec2 curvedUv = CurvedUV_UI(In.Uv, UvRect, -0.15, 1.15);
    vec4 color = ChromaticAberrationUI(imguiTex, curvedUv, UvRect, -0.003);
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `ImDrawListExtensions.AddCallback(ImDrawListPtr, ImDrawCallback, Ptr, nuint)` | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L416-L419) | Store the marker; ImGui copies `userdataSize` bytes into the draw list's callback buffer[^imgui-drawlistext] |
| `KeyHash.Make(ReadOnlySpan<char>)` | [current/decomp/KSA/KeyHash.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KeyHash.cs#L15) | Marker and shader id hashes[^ksa-keyhash] |
| `Program.RenderGame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4948-L4950) | `BeginRenderPass`, `RenderFinalComposite`, `RenderDrawData`: the call sequence ShaderExtensions rewrites |

## Pitfalls and version notes

- The marker's `uint2` lives on the stack; this works because ImGui copies the user data when
  `userdataSize` is non-zero (ShaderExtensions reads it back from `_CallbacksDataBuf`).
- `HudTestBuffer` is declared at `set = 1, binding = 0`, but ShaderExtensions binds ImGui shader
  extras on set 0 (from binding 1 in v0.4.0, from binding 2 on the pinned commit). The block is not
  read by `main()` and the C# writer is commented out
  ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1029-L1036)), so it is dead weight; copy the
  ShaderExtensions README layout instead.
- On the pinned ShaderExtensions the ImGui shader descriptor set also carries a shared time buffer at
  binding 1, which a newer version of this effect could use instead of the C# frame counter.
- The quad bounds are the extents of the draw list's vertices after each vertex is clamped to its
  command's clip rectangle, and the clip rectangle to the framebuffer
  ([ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs#L203-L248)).
  They never exceed the screen, but without the full-window rectangle they shrink to the drawn
  symbols, which moves `UvRect` and with it the center of the barrel curvature.[^sx-imguibase]
- `KeyHash.Make("HudShader")` is recomputed every frame inside `DrawHud`; the ShaderExtensions README
  recommends caching it.

## See also

- [Fullscreen draw-list HUD](fullscreen-drawlist-hud.md)
- [Canopy-glass post-process chain](glass-post-process-chain.md)
- [ShaderExtensions push constants and uniform buffers from C#](sx-push-constants-and-buffers.md)
- [Declare post-processing assets: a small file with a chain, an input reference and an ImGuiShader](../../../howto/shaders/post-processing/declare-post-processing-shader-assets-in-xml/declare-post-processing-shader-assets-in-xml-2.md) - how-to article demonstrating this feature
- [Bend the picture: barrel curve within UvRect and channel offsets in an ImGuiShader](../../../howto/shaders/screen-effects/bend-the-picture-with-crt-curvature-and-chromatic-aberration/bend-the-picture-with-crt-curvature-and-chromatic-aberration-1.md) - how-to article demonstrating this feature
- [Bend the picture: how the host replays a tagged ImGui window offscreen and rewrites it to one image](../../../howto/shaders/screen-effects/bend-the-picture-with-crt-curvature-and-chromatic-aberration/bend-the-picture-with-crt-curvature-and-chromatic-aberration-3.md) - how-to article demonstrating this feature

[^ah-main]: KSA_AircraftHUD/AircraftHUDMain.cs
[^ah-sxhelpers]: KSA_AircraftHUD/ShaderExtensionsHelpers.cs
[^ah-assets]: KSA_AircraftHUD/HudAssests.xml
[^ah-uipphud]: KSA_AircraftHUD/Shaders/UI_PP_HUD.frag
[^ah-uifunctions]: KSA_AircraftHUD/Shaders/UI_Functions.glsl
[^sx-readme]: ShaderExtensions/README.md
[^sx-imguirenderers]: ShaderExtensions ImGuiRenderers.cs
[^sx-imguipost]: ShaderExtensions ImGuiPostRenderer.cs
[^sx-imguiref]: ShaderExtensions ImGuiShaderReference.cs
[^sx-imguibase]: ShaderExtensions ImGuiBaseRenderer.cs
[^sx-patches]: ShaderExtensions Patches.cs
[^imgui-drawlistext]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
[^ksa-keyhash]: KSA.KeyHash (decompiled, build 2026.10.10.5554)
[^ksa-shadercompiler]: RenderCore.Shaders.ShaderCompilerResolve (decompiled, build 2026.10.10.5554)
