---
type: Mod Feature
title: Shared shader time uniform buffer
description: One persistently mapped uniform buffer holding frame number, frame delta, real time, simulation time and warp rate, rewritten from a Program.RenderGame prefix every flight frame and bound at binding 1 of every ShaderEx descriptor set
mod: shaderextensions
category: shaders
techniques: [harmony-prefix, vulkan-buffer, glsl-shader]
hooks: [harmony-prefix KSA.Program.RenderGame, harmony-postfix KSA.Program.RebuildRenderer]
lifecycle: [frame:render]
game_types:
  - KSA.Program
  - KSA.Universe
  - Brutal.VulkanApi.Abstractions.BufferEx
  - Brutal.VulkanApi.Abstractions.MappedMemory
  - Core.Renderer
tags: [shaders, uniform-buffer, time, vulkan, harmony]
---

Every `ShaderEx` fragment shader (post-processing passes and ImGui window shaders) can read the
same block of time values without declaring anything in XML:

```glsl
// README.md L76-L82
layout(std140, set = 1, binding = 1) uniform ShaderTime {
    uint FrameNumber;
    float DeltaTime;
    float RealTimeSinceStart;
    float TimeSinceStart;
    float TimeWarpSpeed;
} Time;
```

ImGui window shaders use `set = 0` with the same binding.[^sx-readme] The project instructions name
this as a deliberate design rule: one shared default time buffer at binding 1 for pre-ImGui,
post-ImGui and ImGui shaders.[^sx-copilot]

## How it works

1. **Data layout.** `ShaderTimeData` is a sequential struct of one `uint` and four `float`s (20
   bytes). All members are 4-byte scalars, so the C# offsets 0, 4, 8, 12, 16 equal the std140
   offsets of the GLSL block
   ([ShaderExtensions/SharedTimeBuffer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/SharedTimeBuffer.cs#L13-L21)).[^sx-sharedtimebuffer]
2. **Buffer.** A static singleton creates one `BufferEx` with `UniformBufferBit` and
   `HostVisibleBit | HostCoherentBit`, maps it once and keeps the `MappedMemory`
   ([ShaderExtensions/SharedTimeBuffer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/SharedTimeBuffer.cs#L60-L78)).
3. **Initialisation.** Lazily, either in the `RenderGame` prefix or in the `RebuildRenderer`
   postfix, whichever runs first, from `Program.GetRenderer()`
   ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L29-L43),
   [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L227-L236)).[^sx-patches]
4. **Per-frame update.** The `RenderGame` prefix writes a fresh struct straight into the mapped
   memory: `Program.FrameNumber` truncated to `uint`, the `dt` argument of `RenderGame`, seconds
   since the first rendered flight frame (a `Stopwatch` started on first use),
   `Universe.GetElapsedSeconds()` (the simulation clock) and `Universe.SimulationSpeed`
   ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L301),
   [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2393)).[^ksa-program] [^ksa-universe]
5. **Binding.** `ShaderEx.CreateDescriptorSetLayout` always adds binding 1 as a `UniformBuffer` for
   the fragment stage, and `ShaderEx.UpdateDescriptorSets` always writes
   `SharedTimeBuffer.GetDescriptorInfoInstance()` into it, which throws if the buffer was never
   initialised
   ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L165-L171),
   [ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L203-L224)).[^sx-shaderex]

## Lifecycle and timing

- **Hook: `frame:render`, Harmony prefix on `Program.RenderGame`.** `Program.Render` calls
  `RenderGame(acquiredFrame, dtPlayer)` after `UpdateShaderData` and `UpdateRenderingResources`
  and only when no editor is open; the editor path calls `RenderEditor`, which is not patched
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2443-L2481)). So `DeltaTime` is
  the player frame delta (`dtPlayer`), and the values stop updating while the vehicle editor is
  open.
- **Why this point.** The prefix runs right before the command buffer that samples the buffer is
  recorded, and on the main thread, so `Universe` time is consistent with the frame being drawn.
  The descriptor sets that reference the buffer are created later in the same call
  (`ImGuiPreRender`) or in the `RebuildRenderer` postfix, both after initialisation.

## Key code

| Path | Role |
|---|---|
| [ShaderExtensions/SharedTimeBuffer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/SharedTimeBuffer.cs#L23-L92) | Singleton buffer, map, update, descriptor info |
| [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L27-L43) | `RenderGame` prefix that writes the values |
| [ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L196-L224) | Binding 1 write in every `ShaderEx` set |

## Snippets

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

```csharp
// ShaderExtensions/SharedTimeBuffer.cs L60-L85
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

        public VkDescriptorBufferInfo GetDescriptorInfo() => new()
        {
            Buffer = buffer.VkBuffer,
            Offset = ByteSize.Zero,
            Range = ByteSize.Of<ShaderTimeData>(),
        };
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Program.RenderGame(AcquiredFrame, double)` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4646-L4955) | Prefix target; `dt` parameter bound by name |
| `Program.FrameNumber` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L301) | `ulong` frame counter |
| `Program.GetRenderer()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L557-L560) | Device for buffer creation |
| `Universe.GetElapsedSeconds()`, `Universe.SimulationSpeed` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2393-L2396), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L122-L131) | Simulation clock and warp rate |
| `BufferEx.CreateInfo`, `CreateBuffer`, `Map` | [current/decomp/Brutal.VulkanApi.Abstractions/BufferExExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/BufferExExtensions.cs#L5) | Host-visible coherent uniform buffer |
| `Renderer.MaxFramesInFlight` | [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L28) | Two frames in flight, relevant to the single-buffer caveat |

## Pitfalls and version notes

- **One copy for all frames in flight.** The game runs two frames in flight
  (`Renderer.MaxFramesInFlight = 2`).[^core-renderer] The prefix overwrites the only copy while the
  GPU may still be executing the previous frame, so that frame can read the newer values. Harmless
  for animation time; do not copy the pattern for data that must match a specific frame.
- **Float precision.** `TimeSinceStart` is simulation seconds stored as a 32-bit float; a float has
  a 24-bit mantissa, so at around 10^7 s of game time the step is already about one second. Use
  `RealTimeSinceStart` or `FrameNumber` for smooth animation.
- **Real time starts at the first flight frame,** not at process start.
- **Set number differs** between post-processing shaders (`set = 1`) and ImGui shaders (`set = 0`).
- **Never disposed.** `DisposeInstance` exists but nothing calls it; the compatibility plan lists a
  missing shutdown path as an open item.[^sx-compatplan]

## See also

- [ShaderEx asset with extra descriptor bindings](shaderex-extra-bindings.md)
- [RenderGame transpiler around the swapchain render pass](rendergame-render-pass-transpiler.md)
- [Rebuilding mod GPU resources with the game renderer](renderer-rebuild-hook.md)
- [Time in a shader: the ShaderTime block at binding 1 of every ShaderEx set](../../../howto/shaders/post-processing/send-push-constants-to-a-shaderextensions-shader/send-push-constants-to-a-shaderextensions-shader-3.md) - how-to article demonstrating this feature

[^sx-readme]: ShaderExtensions/README.md
[^sx-copilot]: ShaderExtensions/.github/copilot-instructions.md
[^sx-sharedtimebuffer]: ShaderExtensions/SharedTimeBuffer.cs
[^sx-patches]: ShaderExtensions/Patches.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^sx-shaderex]: ShaderExtensions/ShaderEx.cs
[^core-renderer]: Core.Renderer (decompiled, build 2026.10.10.5554)
[^sx-compatplan]: ShaderExtensions/KSA-2026.9.7-compatibility-plan.md
