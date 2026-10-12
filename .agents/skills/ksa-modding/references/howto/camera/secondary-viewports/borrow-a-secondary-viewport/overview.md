---
type: Game Feature
title: Lease a secondary viewport for a mod camera and show it in a window
description: KSA seals its viewport registry at boot and offers four spare secondary camera windows; how mods claim, configure, track and release one and show its picture in KSA's window or their own
category: camera
group: secondary-viewports
topic: borrow-a-secondary-viewport
demonstrated_by:
  - mods/ksarmory/features/director-camera-window
  - mods/ksastructures/features/computer-flies-nearest-craft
  - mods/unscience/features/hot-pursuit-part-mounted-cameras
lifecycle: [frame:draw-ui, frame:celestials, frame:viewports, event:save, event:load, frame:render, render:imgui]
game_types:
  - KSA.ViewportRegistry
  - KSA.IGameViewport
  - KSA.IViewportOwner
  - KSA.GameViewport
  - KSA.ViewportBase
  - KSA.ViewportPresets
  - KSA.ViewportType
  - KSA.DockingPort
  - KSA.ViewportOptionFlags
  - Brutal.ImGuiApi.ImGui
tags: [camera, secondary-viewport, lease, viewport-registry, imgui]
---
A mod cannot create a camera, but it can borrow one. At boot the game builds eight viewports and seals the registry; four of them are spare `Secondary` viewports that the View menu's Add Camera, docking ports and mods all draw from. A lease resets the viewport, hands the mod a `Camera`
and a render target, and is lost when the player closes the stock window. The three approaches differ in how the lease is tracked and who draws the window.

## Engine background

| Type | Role | Source |
|---|---|---|
| `ViewportRegistry` | `TryOpenSecondaryViewport`, `TryClaimSecondaryViewport`, `TryGetOwned`, `ReleaseSecondaryViewport`, `AvailableSecondaryCount`, `GameViews` | [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L52-L66), [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L180-L262) |
| `GameViewport.ResetToDefaults`, `DrawImGui` | A lease resets the viewport; the stock window releases it on close | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L66-L85), [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L193-L260) |
| `ViewportBase` | `OptionFlags` (protected set), `RequestResize`, `SetName`, `ImGuiTexture` | [current/decomp/KSA/ViewportBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportBase.cs#L28), [current/decomp/KSA/ViewportBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportBase.cs#L67-L75) |
| `Program.OnDrawUiViewports`, `RenderGame` | Draws stock windows for `HasUi` viewports; renders visible secondaries before the main view | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3162-L3176), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4672-L4684) |
| `DockingPort` | Shares the same pool | [current/decomp/KSA/DockingPort.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DockingPort.cs#L188) |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. `TryOpenSecondaryViewport`, index re-resolved by reference](borrow-a-secondary-viewport-1.md) | KSArmory, KSAStructures | `viewport-lease` | `frame:draw-ui`, `frame:render` | Simple; an index is not an identity |
| [2. `TryClaimSecondaryViewport` with an `IViewportOwner` per camera, KSA's window kept](borrow-a-secondary-viewport-2.md) | unscience (Hot Pursuit) | `viewport-lease` | `frame:draw-ui`, `event:load` | Detects a closed window; re-apply everything after each claim |
| [3. Clear `HasUi` by reflection and draw the texture yourself](borrow-a-secondary-viewport-3.md) | KSArmory | `reflection`, `imgui-window` | `frame:draw-ui`, `render:imgui` | Controls over the picture; reflection and no window with the HUD hidden |

## See also

- [Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](../aim-a-secondary-viewport-camera/overview.md), [Mount a camera on a vessel part and look through it](../mount-a-camera-on-a-part/overview.md), [Draw full-screen click-through overlays and choose the right ImGui draw list](../../../ui/overlays/draw-a-fullscreen-click-through-overlay/overview.md), [Render ImGui UI into a Vulkan texture for use in the 3D world](../../../rendering/textures-and-decals/render-imgui-into-a-texture/overview.md), [Taking control of another vehicle](../../../vehicles-and-parts/vehicles/take-control-of-another-vehicle/overview.md)
- [Record and submit your own Vulkan work: offscreen renders and one-shot uploads](../../../rendering/vulkan-passes/submit-your-own-vulkan-commands/overview.md), [Colour-grade the frame and add an aspect-correct vignette](../../../shaders/screen-effects/colour-grade-the-frame-and-add-a-vignette/overview.md)
- [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
