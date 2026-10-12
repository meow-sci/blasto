---
type: Game Feature
title: Stream the game screen to a terminal with the Kitty graphics protocol
description: Deliver captured game frames to a terminal session as Kitty graphics images that replace one image id, encoded on a worker task and paced to the terminal
category: external-io
group: terminals
topic: stream-the-game-screen-to-a-terminal
demonstrated_by: [mods/gatos/features/screen-stream-frame-capture]
lifecycle: [frame:render, thread:background]
game_types: [KSA.Program, Core.Renderer]
tags: [kitty-graphics, screen-stream, terminal, encoder]
---

Streaming the game's picture into a terminal turns one frame into a few megabytes of escape sequences, up to sixty times a second, through an SSH session and a terminal parser that may be running inside the game. Two halves make that work: a capture that never stalls the GPU, and an encoder and delivery path that never stalls the frame and that slows itself down when the terminal falls behind. This topic is the second half; the capture half lives in [Capture rendered frames from KSA](../../../rendering/vulkan-passes/capture-rendered-frames/overview.md).

## Engine background

| Type or member | File | Why it matters |
|---|---|---|
| `Program.RenderGame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4646-L4955) | The hook sits before its final `commandBuffer2.End()`; the offscreen colour image is sampled-readable and outside any render pass there |
| `Program.ResourceFrameIndex` and `PostRender` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2336-L2370) | `PostRender` submits the frame and advances the slot index modulo `MaxFramesInFlight` |
| `Renderer.MaxFramesInFlight` | [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L28) | Two slots: the readback of slot N happens when N comes round again |
| `RenderTarget.ColorImage`, `Extent` | [current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L36) | The capture source; the colour image is nullable |

Frame structure and phase order are in the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Staging-buffer readback on slot reuse, background Kitty encoder, fixed image id](stream-the-game-screen-to-a-terminal.md) | gatos | `harmony-transpiler` | `frame:render`, `thread:background` | Never blocks the render thread and self-paces; needs a Kitty-capable terminal and a few frames of latency, and depends on the shape of `RenderGame` |

## See also

- [Capture rendered frames from KSA](../../../rendering/vulkan-passes/capture-rendered-frames/overview.md)
- [Host a terminal with pty sessions and frame handoff](../host-a-terminal-with-pty-sessions-and-frame-handoff/overview.md)
- [Show an image in ImGui](../../../rendering/textures-and-decals/show-an-image-in-imgui/overview.md)
- [Take clean screenshots from code, with a composed camera shot held for the capture](../../../testing-and-tooling/in-game-testing/take-screenshots-from-code/overview.md) - related topic

