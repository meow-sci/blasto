---
type: Game Feature
title: Render ImGui UI into a Vulkan texture for use in the 3D world
description: Run a second ImGui context and a second Vulkan ImGui backend on a mod-owned render pass so a whole UI becomes an image shown in the scene or in another window, or put a live camera feed on a part
category: rendering
group: textures-and-decals
topic: render-imgui-into-a-texture
demonstrated_by:
  - mods/purrtty/features/offscreen-imgui-context-render-to-texture
  - mods/ksastructures/features/viewport-image-on-monitor
  - mods/purrtty/features/world-space-quad-pipeline-custom-shader
  - mods/purrtty/features/ray-pick-focus-for-world-quads
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:celestials, frame:after-on-frame, frame:render]
game_types:
  - KSA.ImGuiBackendVulkanImpl
  - Brutal.ImGuiApi.ImGui
  - RenderCore.SimpleVkTexture
  - KSA.Part
  - KSA.IViewport
  - RenderCore.Systems.BindlessTextureLibrary
  - KSA.Rendering.RenderTarget
tags: [imgui, render-to-texture, secondary-context, live-screen, monitor, world-ui]
---

A screen on a part, a dashboard on a wall, a terminal in a cockpit: the UI has to exist as a texture. Two routes work. Draw your own UI into a mod-owned image with a second ImGui context and backend, or reuse an image the game already renders (a secondary viewport's colour image) and only re-point a texture slot. The image is then shown by re-pointing a bindless slot on an existing
material, by a mod-owned quad pipeline, or by a projected quad on an ImGui draw list.

## Engine background

- `ImGuiBackendVulkanImpl` [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs) (render-pass-based, installs itself into the current context's IO; `AddTexture` / `RemoveTexture` for descriptor sets) and `ImGuiBackend.Vulkan` [current/decomp/KSA/ImGuiBackend.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackend.cs#L23).
- Secondary viewports: `ViewportRegistry.TryOpenSecondaryViewport` [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L180); their colour image is recorded before the main view in `RenderGame` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4672-L4683).
- Bindless re-point: `BindlessTextureLibrary.SetTexture` [current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L201).
- Picking: `Cursor.GetEgoRay(IViewport)` [current/decomp/KSA/Cursor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Cursor.cs).
- Phases: the off-screen UI renders in `frame:draw-ui`; quads sample in `frame:render`. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: secondary ImGui context, second backend, fence ring](render-imgui-into-a-texture-1.md) | purrtty | `imgui-window`, own render pass and submit | `frame:draw-ui` | Any UI, fully yours; extra submit per changed frame, context-switching care |
| [2: bindless re-point to a viewport's colour image](render-imgui-into-a-texture-2.md) | ksastructures | `SetTexture` plus projected `AddImageQuad` overlay | `frame:draw-ui`, `frame:celestials` | No pipeline, occluded correctly on the mesh; image is a camera, not a UI |
| [3: world-space quad with ray-pick focus](render-imgui-into-a-texture-3.md) | purrtty | Premultiplied quad pipeline, ray picking | `frame:draw-ui`, `frame:input`, `frame:render` | Interactive in the world; own pipeline and input gating |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Record and submit your own Vulkan work: offscreen renders and one-shot uploads](../../vulkan-passes/submit-your-own-vulkan-commands/overview.md), [Draw a textured quad attached to a part or the camera](../draw-a-textured-quad-in-the-world/overview.md), [Choose a render frame and anchor effects to bodies, parts and terrain](../../overlays/choose-a-render-frame-and-anchor-effects/overview.md)
- [Share the keyboard and mouse between ImGui, mod tools and the game](../../../ui/input/coordinate-imgui-focus-with-game-input/overview.md), [Turn the mouse cursor into a world ray and pick points on a construction plane](../../../ui/input/cursor-to-world-ray/overview.md)
