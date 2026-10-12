---
type: Game Feature
title: Send per-frame values and time to a shader with push constants, written at the right point in the frame
description: How a ShaderExtensions push constant becomes a Vulkan push-constant range, which clock a shader can read, how to switch an always-on pass off, and when in the frame to write
category: shaders
group: post-processing
topic: send-push-constants-to-a-shaderextensions-shader
demonstrated_by:
  - mods/ksa-aircrafthud/features/sx-push-constants-and-buffers
  - mods/shaderextensions/features/push-constants
  - mods/ksa-aircrafthud/features/glass-post-process-chain
  - mods/mpfx/features/film-grain-and-experimental-effects
  - mods/mpfx/features/duck-typed-buffer-structs
  - mods/mpfx/features/per-frame-parameter-upload
  - mods/mpfx/features/effect-shader-contract
  - mods/shaderextensions/features/shared-time-uniform-buffer
  - mods/realatmospheres/features/atmosphere-uniform-side-channel
lifecycle:
  - startup:starmap-before-main
  - startup:asset-load
  - frame:draw-ui
  - frame:render
  - render:post-process
  - frame:viewports
  - render:atmosphere
  - frame:prepare
game_types:
  - KSA.LookupCollection
  - KSA.KeyHash
  - Brutal.VulkanApi.VkDeviceExtensions
  - KSA.Program
  - KSA.Universe
  - Core.Renderer
  - Brutal.VulkanApi.Abstractions.BufferEx
  - Brutal.VulkanApi.Abstractions.MappedMemory
tags: [push-constants, shader-time, animation, time-warp, shaderextensions, timing]
---
Small values that change every frame (an enabled flag, a strength, a clock) are best sent as a **push constant**: the bytes are
copied into the command buffer when the pass is recorded, so every frame gets exactly the value that was current at record time and
passes never tear across frames in flight. This topic covers how a push constant is declared and turned into a Vulkan range, how to
switch an always-on pass off, which clock a shader should read, and where in the frame the write must happen.

## Engine background

- **Recording.** `ShaderEx.PushConstants` issues one `CommandBuffer.PushConstants(layout, stageFlags, offset, bytes)` per range after the
  pipeline is bound and before the draw ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L95-L103)); the extension method is
  `VkDeviceExtensions.PushConstants<T>` ([current/decomp/Brutal.VulkanApi/VkDeviceExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi/VkDeviceExtensions.cs#L1189)). The bytes are captured then.
- **Limits.** `CreatePushConstantRanges` rejects zero or non-multiple-of-4 sizes and totals above 128 bytes, the minimum
  `maxPushConstantsSize` every Vulkan device guarantees; `ShaderEx.Bind` allows one push-constant element per shader
  ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L59-L93)). The element's `Stage` attribute (default `FragmentBit`) becomes the range's stage flags
  ([ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L126-L162)).
- **Clocks.** `Universe.GetElapsedSeconds()` returns the next time of the last applied simulation step, so it freezes on pause and
  scales with warp; `Universe.SimulationSpeed` is the warp rate ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2393-L2402)). `Program.FrameNumber`
  is a `ulong` incremented at the end of `OnFrame` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2332)).
- **Order within a frame.** `OnFrame` runs the UI hooks, then `Render` calls `RenderGame`; a value written in any main-thread hook before
  that call lands in the same frame's commands. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Which clock?

| Clock | Source | Pause | Warp | Use |
|---|---|---|---|---|
| `ShaderTime.RealTimeSinceStart` | ShaderExtensions stopwatch from the first flight frame | keeps running | no | Smooth wall-clock animation |
| `ShaderTime.TimeSinceStart` | `Universe.GetElapsedSeconds()` as float | freezes | scales | Effects tied to simulation time (precision loss in long saves) |
| `ShaderTime.DeltaTime` | the player frame delta passed to `RenderGame` | n/a | no | Frame-rate independent integration |
| `ShaderTime.FrameNumber` | `Program.FrameNumber` truncated to `uint` | keeps counting | no | Noise seeds, slice indices |
| A mod accumulator in a push constant | `dt` summed by the mod | your choice | optional | Per-effect speed, freeze, or warp scaling |
| Sim seconds smuggled into a stock field | `Universe.GetElapsedTime()` | freezes | scales | Stock shaders with no push constant of yours |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [One push-constant element per pass, enabled flag and time](send-push-constants-to-a-shaderextensions-shader-1.md) | KSA_AircraftHUD | `vulkan-pipeline` | `frame:viewports`, `render:post-process` | Simple toggle with an early pass-through branch; works when the HUD is hidden |
| [Per-side push-constant clock in a settings-driven mod](send-push-constants-to-a-shaderextensions-shader-2.md) | MPFX | `vulkan-pipeline` | `frame:draw-ui`, `render:post-process` | Own clock (pause-proof, optionally warp-scaled); buffers rewritten only on change; stops with the HUD |
| [ShaderTime block at binding 1 of every set](send-push-constants-to-a-shaderextensions-shader-3.md) | ShaderExtensions | `vulkan-buffer` | `frame:render` | Free, no declaration; single buffer for two frames in flight |
| [Signed simulation seconds through a stock atmosphere field](send-push-constants-to-a-shaderextensions-shader-4.md) | RealAtmospheres | `harmony-prefix` | `render:atmosphere` | Only for stock shaders you patch; the sim clock freezes on pause |

## See also

- [Mod-defined push constants](../../../../mods/shaderextensions/features/push-constants.md), [Shared shader time uniform buffer](../../../../mods/shaderextensions/features/shared-time-uniform-buffer.md), [Per-frame effect parameter upload](../../../../mods/mpfx/features/per-frame-parameter-upload.md)
- [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../feed-uniform-buffers-to-a-shaderextensions-shader/overview.md), [Pack parameters and camera data for a compute effect](../../compute-effects/pack-parameters-for-a-compute-effect/overview.md), [Write a fullscreen post-processing fragment shader for KSA](../write-a-post-processing-fragment-shader/overview.md), [Smuggle data into stock shaders through unused uniforms, state bits and sentinel values](../../stock-shaders/send-extra-data-to-stock-shaders-without-new-uniforms/overview.md), [Add film grain and dither away banding](../../screen-effects/add-film-grain-and-blue-noise-dithering/overview.md), [Free mod-owned Vulkan resources without device loss](../../../rendering/vulkan-passes/free-vulkan-resources-safely/overview.md), [Draw full-screen click-through overlays and choose the right ImGui draw list](../../../ui/overlays/draw-a-fullscreen-click-through-overlay/overview.md)
