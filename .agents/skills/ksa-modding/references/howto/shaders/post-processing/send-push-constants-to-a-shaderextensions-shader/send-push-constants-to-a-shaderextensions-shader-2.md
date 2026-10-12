---
type: How-To
title: "Push constants for a shader: per-side clock and strengths in a settings-driven mod"
description: Keep a mod-owned animation clock per pass, optionally scaled by time warp, write it as a push constant every frame and write parameter buffers only when a setting changes
category: shaders
group: post-processing
topic: send-push-constants-to-a-shaderextensions-shader
approach: 2
difficulty: intermediate
prerequisites: [starmap, shaderextensions, kittenextensions]
demonstrated_by:
  - mods/mpfx/features/film-grain-and-experimental-effects
  - mods/mpfx/features/duck-typed-buffer-structs
  - mods/mpfx/features/per-frame-parameter-upload
  - mods/mpfx/features/effect-shader-contract
techniques: [vulkan-pipeline, glsl-shader]
hooks: [starmap StarMapAfterGui, xml-asset MPFXPushConstantsFloatBufferAsset]
lifecycle: [startup:asset-load, frame:draw-ui, frame:render, render:post-process]
game_types: [KSA.LookupCollection, KSA.KeyHash, KSA.Universe, KSA.Program, Core.Renderer]
tags: [push-constants, animation-clock, time-warp, dirty-flag, neutral-values, mpfx, film-grain]
---
MPFX animates film grain with a clock it owns and writes as a push constant every frame, while its other parameters are uniform buffers
rewritten only when a setting changes. This article shows that split: why the clock is the mod's and not the shared time buffer, how a warp
multiplier is added, how disabled effects get neutral values, and why all of it sits in `[StarMapAfterGui]`. It needs StarMap and ShaderExtensions.

## Background: how the game does it

`[StarMapAfterGui]` is a postfix on `Program.OnDrawUiViewports` inside `OnFrame`'s `if (DrawUI)` block, between `PrepareImGui` and `ImGui.Render`;
`Render` and `RenderGame` come later in the same `OnFrame` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2261-L2286)). Push constants are copied at record time, so
the value written there is the one used by this frame's passes. Uniform buffers are single host-coherent allocations: `Renderer.MaxFramesInFlight`
is 2, so a CPU rewrite can become visible to a frame the GPU is still executing, which is invisible for slider-driven settings but not for
per-frame exactness.

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Elements and delegates registered | ShaderExtensions' `Program.Main` prefix | `startup:starmap-before-main` |
| Push constant objects created | `ModLibrary.LoadAll` | `startup:asset-load` |
| Clock advance and push-constant write | `[StarMapAfterGui]` | `frame:draw-ui` |
| Buffer writes | Same hook, only when `UpdateValues` is set | `frame:draw-ui` |
| Bytes captured / buffers read by GPU | `RenderGame` recording / execution | `frame:render`, `render:post-process` |

While the HUD is hidden (`DrawUI == false`) `AfterGui` is not called: the buffers keep their last values so the grade stays on, but the grain
clock stops and the grain freezes. The [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) shows the hook order.

## Steps

1. **Declare a push-constant struct and an element per pass side.** One float is enough for a clock:

   ```csharp
   // MPFX/Buffers/MPFXPushConstantsFloatBuffer.cs L9-L17
   [StructLayout(LayoutKind.Sequential, Pack = 1)]
   [SxPushConstant("MPFXPushConstantsFloatBufferAsset")]
   public struct MPFXPushConstantsFloatBuffer
   {
       public float a;

       // lookup delegate fields must be static fields on the buffer element type
       [SxPushConstantLookup] public static MPFXSpanLookup<MPFXPushConstantsFloatBuffer> LookupSpan; // gives a Span<T> of length Size
   }
   ```

   ```xml
   <!-- MPFX/MPFXAssests.xml L59-L66 -->
   	<PostProcessingShader Id="MPFXFilmGrainPre" Path="Shaders/FilmGrainPre.frag" RenderPassId="136">
   		<MPFXVec4BufferAsset Id="MPFXFilmGrainBuffer" Size="1" />
   		<MPFXPushConstantsFloatBufferAsset Id="MPFXFilmgrainPreTimeBuffer" />
   	</PostProcessingShader>
   	<GlobalPostShader Id="MPFXFilmGrainPost" Path="Shaders/FilmGrainPost.frag" RenderPassId="136">
   		<MPFXVec4BufferAsset Id="MPFXFilmGrainBuffer" />
   		<MPFXPushConstantsFloatBufferAsset Id="MPFXFilmgrainPostTimeBuffer" />
   	</GlobalPostShader>
   ```

   Sources: [MPFX/Buffers/MPFXPushConstantsFloatBuffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXPushConstantsFloatBuffer.cs#L9-L17), [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml#L59-L66). The buffer is shared by `Id`, the push constants are not
   shareable, so each side has its own.

2. **Read it in GLSL.** The block name is irrelevant; the layout is what matters:

   ```glsl
   // MPFX/Shaders/FilmGrainPre.frag L15-L22
   layout(set = 1, binding = 2) uniform MPFXVec4BufferAsset {
     vec4 data; // preGrainSize, preGrainStrength, postGrainSize, postGrainStrength
     vec4 config; // width, height
   };

   layout(push_constant) uniform MPFXPushConstantsFloatAsset {
     float framenumPre;
   };
   ```

   Source: [MPFX/Shaders/FilmGrainPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/FilmGrainPre.frag#L15-L22).

3. **Accumulate your own clock.** Per side, add `dt` scaled by a user multiplier and, if wanted, by `Universe.SimulationSpeed` (the warp rate,
   [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L122)). Grain should keep moving on pause, so the base clock is wall-clock `dt`; a multiplier of 0 freezes it:

   ```csharp
   // MPFX/MPFX.cs L128-L138
               if (MPFXPushConstantsFloatBuffer.LookupSpan != null)
               {
                   CurrentProfile.FilmGrainPreTime += (float)dt * CurrentProfile.FilmGrainPreTimeMultiplier * (CurrentProfile.FilmGrainPreTimeWarpMultiplier ? (float)Universe.SimulationSpeed : 1f);
                   CurrentProfile.FilmGrainPostTime += (float)dt * CurrentProfile.FilmGrainPostTimeMultiplier * (CurrentProfile.FilmGrainPostTimeWarpMultiplier ? (float)Universe.SimulationSpeed : 1f);

                   Span<MPFXPushConstantsFloatBuffer> FilmgrainPreTime = MPFXPushConstantsFloatBuffer.LookupSpan(KeyHash.Make("MPFXFilmgrainPreTimeBuffer"));
                   FilmgrainPreTime[0].a = CurrentProfile.FilmGrainPreTime;

                   Span<MPFXPushConstantsFloatBuffer> FilmgrainPostTime = MPFXPushConstantsFloatBuffer.LookupSpan(KeyHash.Make("MPFXFilmgrainPostTimeBuffer"));
                   FilmgrainPostTime[0].a = CurrentProfile.FilmGrainPostTime;
               }
   ```

   Source: [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L128-L138). `dt` is the argument of `AfterGui(double dt)`.

4. **Write parameter buffers on change, with neutral values for disabled effects.** `UpdateValues` starts true and every widget ORs its change
   into it; each effect writes its identity value when unchecked, so each shader reduces to a copy:

   ```csharp
   // MPFX/MPFX.cs L140-L148
               if (UpdateValues)
               {
                   UpdateValues = false;

                   if (MPFXDefaultBuffer.LookupSpan != null)
                   {
                       Span<MPFXDefaultBuffer> BrightnessData = MPFXDefaultBuffer.LookupSpan(KeyHash.Make("MPFXBrightnessBuffer"));
                       BrightnessData[0].a = CurrentProfile.BrightnessPreImgui ? CurrentProfile.BrightnessFloatPreImgui : 1f;
                       BrightnessData[0].b = CurrentProfile.BrightnessPostImgui ? CurrentProfile.BrightnessFloatPostImgui : 1f;
   ```

   Source: [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L140-L148). The pass still costs a full draw; neutral values do not remove it.

5. **Guard the shader against the disabled case** (`data.x == 0` returns the clamped input) as in
   [MPFX/Shaders/FilmGrainPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/FilmGrainPre.frag#L250-L256).

## Complete example

| File | Role |
|---|---|
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs) | `AfterGui`: clocks, dirty-flag upload |
| [MPFX/Buffers/MPFXPushConstantsFloatBuffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXPushConstantsFloatBuffer.cs) | Push-constant struct |
| [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml) | One push-constant element per side |
| [MPFX/Shaders/FilmGrainPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/FilmGrainPre.frag) | Consumer |

## Variations and gotchas

- **The clock is a `float` that never wraps.** It loses precision over very long sessions or with high multipliers (not tested in game).
- **Settings are not persisted.** New profiles are not saved; they would have to be added to an asset XML file.
- **Some edits do not raise the flag.** A newly selected profile reaches the GPU only after the next widget change.
- **A dirty-flag write can show one frame early** because the GPU may still be reading the previous frame; use a push constant when a value must
  change exactly on a frame boundary.
- **Hidden HUD.** For an effect that must keep animating without the HUD, write from an `OnFrameEditor` or `OnFrameViewports` postfix instead (previous article).
- **Vehicle editor.** `RenderEditor` is not patched, so the values are unused there.

## See also

- Siblings: [enabled flag per pass](send-push-constants-to-a-shaderextensions-shader-1.md), [ShaderTime block](send-push-constants-to-a-shaderextensions-shader-3.md), [sim seconds through a stock field](send-push-constants-to-a-shaderextensions-shader-4.md), [overview](overview.md)
- [Per-frame effect parameter upload](../../../../mods/mpfx/features/per-frame-parameter-upload.md), [Film grain and experimental effects](../../../../mods/mpfx/features/film-grain-and-experimental-effects.md), [Uniform buffer and push constant structs bound by attribute name](../../../../mods/mpfx/features/duck-typed-buffer-structs.md), [Shader contract for chain passes](../../../../mods/mpfx/features/effect-shader-contract.md)
- [Add film grain and dither away banding](../../screen-effects/add-film-grain-and-blue-noise-dithering/overview.md), [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../feed-uniform-buffers-to-a-shaderextensions-shader/overview.md)
