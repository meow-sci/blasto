---
type: How-To
title: "Time in a shader: the ShaderTime block at binding 1 of every ShaderEx set"
description: Read frame number, frame delta, real time, simulation time and warp rate in any ShaderExtensions fragment shader without declaring anything in XML, and know which fields freeze on pause
category: shaders
group: post-processing
topic: send-push-constants-to-a-shaderextensions-shader
approach: 3
difficulty: beginner
prerequisites: [starmap, shaderextensions, kittenextensions]
demonstrated_by: [mods/shaderextensions/features/shared-time-uniform-buffer]
techniques: [vulkan-buffer, glsl-shader, harmony-prefix]
hooks: [harmony-prefix KSA.Program.RenderGame (ShaderExtensions)]
lifecycle: [frame:render, render:post-process]
game_types:
  - KSA.Program
  - KSA.Universe
  - Core.Renderer
  - Brutal.VulkanApi.Abstractions.BufferEx
  - Brutal.VulkanApi.Abstractions.MappedMemory
tags: [shader-time, animation, time-warp, pause, uniform-buffer, shaderextensions, frame-number]
---
Every `ShaderEx` fragment shader (post-processing passes and ImGui window shaders) gets the same five time values for free, at binding 1 of its
descriptor set. You declare the block in GLSL and read it; no XML, no C#. This article lists the fields, which of them stop when the game is
paused and which scale with time warp, and how ShaderExtensions fills the buffer once per frame.

## Background: how the game does it

ShaderExtensions prefixes `Program.RenderGame` and writes a `ShaderTimeData` struct into a persistently mapped, host-coherent uniform buffer
before the passes record:

```csharp
// ShaderExtensions/Patches.cs L27-L43
        internal static Stopwatch timeSinceStart;

        [HarmonyPatch(typeof(Program), "RenderGame"), HarmonyPrefix]
        internal static void Program_RenderGame_Prefix(double dt)
        {
            timeSinceStart ??= Stopwatch.StartNew();
            if (!SharedTimeBuffer.IsInitialized) SharedTimeBuffer.Initialize(Program.GetRenderer());

            SharedTimeBuffer.UpdateInstance(new ShaderTimeData
            {
                FrameNumber = unchecked((uint)Program.FrameNumber),
                DeltaTime = (float)dt,
                RealTimeSinceStart = (float)timeSinceStart?.Elapsed.TotalSeconds,
                TimeSinceStart = (float)Universe.GetElapsedSeconds(),
                TimeWarpSpeed = (float)Universe.SimulationSpeed,
            });
        }
```

Source: [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L27-L43). `Program.FrameNumber` is incremented at the end of `OnFrame`
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2332)); `Universe.GetElapsedSeconds()` returns the next time of the last applied simulation step and
`Universe.SimulationSpeed` is the warp rate ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2393-L2402)).

## Lifecycle and timing

`frame:render`: the prefix runs at the start of `RenderGame`, on the main thread, right before the command buffer that samples the buffer is recorded.
`DeltaTime` is the player frame delta (`dtPlayer`) given to `Render`. `RenderGame` is skipped in the vehicle editor (`RenderEditor` is not patched),
so the values stop updating while the editor is open. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Fields

| Field | Value | Pause | Warp | Notes |
|---|---|---|---|---|
| `FrameNumber` (`uint`) | `Program.FrameNumber` | counts on | no | Wraps after 4,294,967,295 frames |
| `DeltaTime` (`float`) | player frame delta | n/a | no | |
| `RealTimeSinceStart` (`float`) | stopwatch started at the first flight frame | runs | no | Not process start |
| `TimeSinceStart` (`float`) | `Universe.GetElapsedSeconds()` | freezes | scales | A 32-bit float: step is already about one second near 10^7 s of game time |
| `TimeWarpSpeed` (`float`) | `Universe.SimulationSpeed` | 0 when paused | is the warp | |

## Steps

1. **Declare the block in your post-processing fragment shader**, set 1 binding 1, std140:

   ```glsl
   // ShaderExtensions/README.md L76-L82
   layout(std140, set = 1, binding = 1) uniform ShaderTime {
       uint FrameNumber;
       float DeltaTime;
       float RealTimeSinceStart;
       float TimeSinceStart;
       float TimeWarpSpeed;
   } Time;
   ```

   Source: [README.md](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/README.md#L76-L82). Use `set = 0` instead in an `ImGuiShader` fragment shader. Declaring it is optional; the binding exists either way.

2. **Pick the field for the job.** Animation that should run on pause: `RealTimeSinceStart` or `FrameNumber`. Animation tied to the simulation:
   `TimeSinceStart`. Frame-rate-independent integration: `DeltaTime`. A noise slice index: `FrameNumber & 63`.

3. **How binding 1 gets there.** `ShaderEx.CreateDescriptorSetLayout` always adds binding 1 as a fragment-stage `UniformBuffer`, and `UpdateDescriptorSets`
   always writes the shared buffer into it ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L156-L224)). The buffer is a singleton created lazily by whichever of the
   `RenderGame` prefix or the `RebuildRenderer` postfix runs first:

   ```csharp
   // ShaderExtensions/SharedTimeBuffer.cs L60-L78
           public void Bind(Renderer renderer)
           {
               buffer = renderer.Device.CreateBuffer(new BufferEx.CreateInfo
               {
                   Name = nameof(SharedTimeBuffer),
                   BufferUsage = VkBufferUsageFlags.UniformBufferBit,
                   BufferSize = ByteSize.Of<ShaderTimeData>(),
                   AllocRequiredProperties =
                       VkMemoryPropertyFlags.HostVisibleBit |
                       VkMemoryPropertyFlags.HostCoherentBit,
               });

               mappedMemory = buffer.Map();
           }

           public void Update(in ShaderTimeData value)
           {
               mappedMemory.AsSpan<ShaderTimeData>()[0] = value;
           }
   ```

   Source: [ShaderExtensions/SharedTimeBuffer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/SharedTimeBuffer.cs#L60-L78). It is created once and kept for the process; the `RebuildRenderer` postfix initialises it only if it
   does not yet exist, and the descriptor sets that reference it are rebuilt with the passes.

## Complete example

[ShaderExtensions/SharedTimeBuffer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/SharedTimeBuffer.cs) (struct, buffer), [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs) (the prefix), [ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs) (binding 1).
MPFX declares the block in all its shaders ([MPFX/Shaders/BrightnessPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/BrightnessPre.frag#L7-L13)) but reads it in none; AircraftHUD's pinned-API port could take the
noise slice from `Time.FrameNumber & 63`.

## Variations and gotchas

- **One copy, two frames in flight.** The prefix overwrites the only copy while the GPU may still be executing the previous frame, which can then read newer
  values. Harmless for animation; do not copy the pattern for data that must match one frame. Push constants (articles 1 and 2) do not have this issue.
- **Float precision.** Use `RealTimeSinceStart` or `FrameNumber` for smooth animation in long saves.
- **Set number.** `set = 1` for post-processing passes, `set = 0` for ImGui shaders.
- **Never disposed.** `DisposeInstance` exists but nothing calls it.
- **Pause.** `TimeSinceStart` and `TimeWarpSpeed` freeze at pause (speed 0), `RealTimeSinceStart` and `FrameNumber` do not.

## See also

- Siblings: [enabled flag per pass](send-push-constants-to-a-shaderextensions-shader-1.md), [per-side clock in a settings mod](send-push-constants-to-a-shaderextensions-shader-2.md), [sim seconds through a stock field](send-push-constants-to-a-shaderextensions-shader-4.md), [overview](overview.md)
- [Shared shader time uniform buffer](../../../../mods/shaderextensions/features/shared-time-uniform-buffer.md), [Rebuilding mod GPU resources with the game renderer](../../../../mods/shaderextensions/features/renderer-rebuild-hook.md)
- [Add film grain and dither away banding](../../screen-effects/add-film-grain-and-blue-noise-dithering/overview.md), [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../feed-uniform-buffers-to-a-shaderextensions-shader/overview.md)
