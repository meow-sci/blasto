---
type: Game Feature
title: Take clean screenshots from code, with a composed camera shot held for the capture
description: Ask the game's own ScreenshotCapture for a UI-free frame, collect the file with a manifest of the instant, and hold the main view at a scripted pose until the warm-up frames settle
category: testing-and-tooling
group: in-game-testing
topic: take-screenshots-from-code
demonstrated_by:
  - mods/ksagolf/features/visual-testing-captures
  - mods/ksastructures/features/dev-file-bridge-and-mcp
  - mods/ksarmory/features/file-command-bridge-and-mcp
  - mods/ksacars/features/file-drop-command-bridge
lifecycle:
  - frame:after-on-frame
  - frame:post-render
  - frame:prepare
  - thread:background
  - frame:viewports
  - frame:draw-ui
game_types: [KSA.ScreenshotCapture, KSA.Program, KSA.Camera, KSA.Constants, KSA.FixedController, Core.Renderer]
tags: [screenshots, capture, manifest, camera, visual-testing, reflection]
---

Seeing what a mod draws without sitting at the game means asking KSA for a picture from code. The game already has a screenshot system with
hidden-UI capture, high-resolution scaling and a warm-up delay; it is just not reachable by a public static call. Four of the analyzed mods
reach it the same way (one reflected private field, one public method), then add what a tester needs around it: a manifest beside each PNG that
says what instant it shows, a held camera pose so the shot is composed, and a rule for where in the frame the request may be made.

## Engine background

| Piece | Where | Facts |
|---|---|---|
| `Program._screenshotCapture` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L175) | Private instance field, created at load ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1132)); `Program.Instance` is the instance |
| `ScreenshotCapture.Request(int scale, string flags)` | [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L92-L190) | Public. Refuses with a log warning if a capture is running (L94-L98) or in the editor (L99-L103). Flags are split on space or comma: `ui` (grab the swapchain including ImGui, ignores scale), `hud` (keep the HUD), `warm=N` (warm-up frames, clamped 0..600); anything else logs `unknown flag` |
| Warm-up default | [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L36), [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L106) | `DEFAULT_WARMUP_FRAMES = 30` applies only when `scale > 1`; at scale 1 the default is 0 |
| `OnPrepareFrame` | [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L210-L270), called from `PrepareFrame` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2149)) | Sets `Program.DrawUI = false` unless `hud`, and for `scale > 1` sets `Renderer.ExtentOverride` and calls `RebuildRenderer(skipSwapchain: true)`; counts the warm-up down |
| `OnRenderGameCapture` | [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L272-L332), called from `RenderGame` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4946)) | Records the composite into a private image and a copy to a staging buffer |
| `OnPostRender` | [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L393-L433), called from `PostRender` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2350)) | `Device.WaitIdle()`, reads the staging buffer, restores `DrawUI`, schedules a renderer rebuild when it scaled, then `Task.Run(SaveToPng)` |
| File name | [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L597) | `ksa_<yyyyMMdd_HHmmss>_<w>x<h>.png` under `Constants.DocumentsFolderPath/exports/screenshots` ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L80)); two captures in one second collide |
| Stock triggers | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1805-L1808), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1660-L1668) | The `Screenshot` key calls `Request(1, "")`; the `screenshot` terminal action takes `scale` and `flags` |
| Projection | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L277-L280), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L631-L634) | The matrix the frame was drawn with; projecting a world point through it gives the exact pixel |
| Camera pose | [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L5-L12) | `CameraOffset` (from the followed object) and `CameraRotation` (forward vector); a composed shot is this controller held every frame |

Frame order and the render sequence: [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).
A related but different job, grabbing frames continuously for streaming or thumbnails, is [Capture rendered frames from KSA](../../../rendering/vulkan-passes/capture-rendered-frames/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: reflect the private field, Request with warm=24](take-screenshots-from-code-1.md) | ksagolf ([src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs)), ksacars ([src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs)) | `reflection` | `frame:after-on-frame`, `frame:post-render` | Smallest working capture, a manifest per image and a series; depends on one private field |
| [2: composed shot with a 24-frame countdown and a manifest](take-screenshots-from-code-2.md) | ksagolf ([src/KSAGolf/Ksa/SceneCamera.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/SceneCamera.cs)) | `reflection` | `frame:after-on-frame`, `frame:viewports` | A shot relative to the flown craft, aimed at a target; needs the main-view borrowing helpers |
| [3: shot relative to the craft or the building, held by the dev bridge](take-screenshots-from-code-3.md) | ksastructures ([src/KSAStructures/Ksa/SceneCamera.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/SceneCamera.cs)) | `reflection` | `frame:after-on-frame`, `frame:viewports` | The lean variant: one pose source, an extra `office_view` at building coordinates |
| [4: bridge as a view-pose claimant that can orbit](take-screenshots-from-code-4.md) | ksarmory ([src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs)) | `view-claim-ladder` | `frame:after-on-frame`, `frame:viewports` | Cooperates with the mod's other camera owners; the most code |
| [5: capture button that only sets a flag, plus player bundles](take-screenshots-from-code-5.md) | ksarmory ([src/KSArmory/Ksa/Ui/UiSession.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiSession.cs)) | `reflection` | `frame:draw-ui`, `frame:after-on-frame` | The right way to offer capture to a player; note.json and log tail with each bundle |

## See also

- [Drive a running game through a file bridge](../drive-a-running-game-through-a-file-bridge/overview.md) (the transport every article uses), [Visual regression testing for a KSA mod](../visual-regression-for-mods/overview.md) (what to do with the pictures)
- [Capture rendered frames from KSA](../../../rendering/vulkan-passes/capture-rendered-frames/overview.md), [Handle renderer rebuilds](../../../rendering/vulkan-passes/handle-renderer-rebuilds/overview.md), [Project world positions to screen](../../../rendering/overlays/project-world-positions-to-screen/overview.md), [Borrow the main camera and hand it back](../../../camera/main-view/borrow-the-main-camera-and-hand-it-back/overview.md)
- Mod features: [KSAGolf visual testing captures](../../../../mods/ksagolf/features/visual-testing-captures.md), [KSACars file-drop command bridge](../../../../mods/ksacars/features/file-drop-command-bridge.md), [KSAStructures dev file bridge](../../../../mods/ksastructures/features/dev-file-bridge-and-mcp.md), [KSArmory file command bridge](../../../../mods/ksarmory/features/file-command-bridge-and-mcp.md)
