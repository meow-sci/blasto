---
type: Game Feature
title: Bend the picture with CRT curvature and chromatic aberration
description: Remap UVs for barrel distortion and offset colour channels by a luminance-masked amount, applied to one tagged ImGui window or to a whole canopy-glass composite, plus how the host replays a tagged window offscreen
category: shaders
group: screen-effects
topic: bend-the-picture-with-crt-curvature-and-chromatic-aberration
demonstrated_by:
  - mods/ksa-aircrafthud/features/imgui-window-hud-shader
  - mods/ksa-aircrafthud/features/glass-post-process-chain
  - mods/shaderextensions/features/imgui-window-post-shader
  - mods/shaderextensions/features/imgui-draw-list-marker-callback
lifecycle: [render:imgui, render:post-process, frame:draw-ui, frame:render]
game_types:
  - KSA.Program
  - KSA.ImGuiBackendVulkanImpl
  - KSA.ShaderReference
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImDrawDataPtr
  - KSA.KeyHash
  - Brutal.ImGuiApi.ImDrawCmd
  - Brutal.ImGuiApi.ImGuiPlatformIOPtr
tags:
  - crt
  - barrel-distortion
  - chromatic-aberration
  - imgui
  - imguishader
  - draw-list
  - marker-callback
  - post-processing
---
A curved-glass look needs two operations: remap the sample position so straight lines bow outward toward the edges (barrel distortion), and sample red and blue at slightly different positions along the radius so bright edges get colour fringes. In KSA you can apply it to the whole scene (a post-processing pass) or to a single ImGui window such as a HUD, which needs a
shader that runs over only that window's pixels. This topic gives the maths and the full recipe for running a custom fragment shader over one ImGui window with ShaderExtensions, including how the host replays the window offscreen, which is useful when a window does not show.

## Engine background

- **ImGui draws once at the end of the frame.** `ImGui.Render` finalises the draw data (`frame:imgui-render`), and `ImGuiBackend.Vulkan.RenderDrawData` draws it inside the swapchain render pass after the tonemap composite ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4948-L4951)). Draw data has no notion of windows, only a list of `ImDrawList`s.
- **Draw callbacks.** A draw-list callback command carries a pointer and up to a few bytes of user data that ImGui copies into the list's callback buffer; the game's backend invokes it when it meets the command ([current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L774-L791)). ShaderExtensions uses an empty callback whose payload is a marker plus a shader key.
- **A slot for work.** ShaderExtensions replaces the swapchain `BeginRenderPass` call with a method that first replays tagged windows offscreen, applies the fragment shader, rewrites the draw list into one textured quad, then runs the pre-ImGui chain ([Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing](../../post-processing/choose-pre-or-post-imgui-post-processing/overview.md)).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Barrel curve within UvRect and channel offsets in an ImGuiShader](bend-the-picture-with-crt-curvature-and-chromatic-aberration-1.md) | KSA_AircraftHUD | `glsl-shader` | `frame:viewports`, `render:imgui` | Only the tagged window is bent; needs the marker callback, the asset, and a full-window rectangle to pin the bounds |
| [Chromatic aberration and radial edge blur in a glass composite pass](bend-the-picture-with-crt-curvature-and-chromatic-aberration-2.md) | KSA_AircraftHUD | `glsl-shader` | `render:post-process` | Bends the scene, not the UI; part of a three-pass chain that the pinned ShaderExtensions skips until ported (removed subpass attributes); the curvature helper ignores its arguments |
| [How the host replays the draw list offscreen and rewrites it to one image](bend-the-picture-with-crt-curvature-and-chromatic-aberration-3.md) | ShaderExtensions | `vulkan-pipeline` | `frame:draw-ui`, `render:imgui` | Background and debugging guide: private-member reflection, the vertex shader you must supply, and why a window might not show |

## See also

- [Curved, chromatic HUD window via an ImGuiShader](../../../../mods/ksa-aircrafthud/features/imgui-window-hud-shader.md), [Canopy-glass post-process chain](../../../../mods/ksa-aircrafthud/features/glass-post-process-chain.md), [Per-window ImGui post shader](../../../../mods/shaderextensions/features/imgui-window-post-shader.md), [Tagging an ImGui window with a draw-list marker callback](../../../../mods/shaderextensions/features/imgui-draw-list-marker-callback.md)
- [Refract and distort the rendered scene, and flash or shake it](../refract-and-distort-the-scene/overview.md), [Render ImGui UI into a Vulkan texture for use in the 3D world](../../../rendering/textures-and-decals/render-imgui-into-a-texture/overview.md), [Show a PNG or runtime-generated image in an ImGui window](../../../rendering/textures-and-decals/show-an-image-in-imgui/overview.md), [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../../post-processing/declare-post-processing-shader-assets-in-xml/overview.md)
