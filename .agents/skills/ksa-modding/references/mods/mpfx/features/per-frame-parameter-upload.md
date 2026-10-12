---
type: Mod Feature
title: Per-frame effect parameter upload
description: From StarMapAfterGui, MPFX writes film grain time into push constants every frame the HUD is shown and rewrites every effect uniform buffer through ShaderExtensions span lookups only when a dirty flag set by the settings window is raised, substituting neutral values for disabled effects
mod: mpfx
category: shaders
techniques: [starmap-lifecycle, vulkan-buffer, post-processing]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:render]
game_types: [KSA.Program, KSA.Universe, KSA.KeyHash, Core.Renderer]
tags: [shaders, uniform-buffer, push-constants, dirty-flag, starmap, frame-timing]
---

MPFX has no render hook of its own. Everything it sends to the GPU is written from its
`[StarMapAfterGui]` method, `AfterGui(double dt)`, before the settings window is drawn. Uniform buffers
are rewritten only when something changed. The film grain time is a push constant written every frame.
ShaderExtensions picks both up later in the same frame, when it records the post-process passes in
`Program.RenderGame`.[^mpfx-main]

## How it works

1. **Push constants every frame.** If `MPFXPushConstantsFloatBuffer.LookupSpan` has been assigned, MPFX
   advances `FilmGrainPreTime` and `FilmGrainPostTime` by
   `dt * TimeMultiplier * (TimeWarpMultiplier ? Universe.SimulationSpeed : 1)`. It writes them through
   the span lookups for `MPFXFilmgrainPreTimeBuffer` and `MPFXFilmgrainPostTimeBuffer`
   ([MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L120-L138)). It keeps its own clock, instead of the `ShaderTime` block, so that
   each side can be sped up, frozen (multiplier 0) or tied to time warp.
2. **Uniform buffers on change.** `public static bool UpdateValues = true` starts raised, so the first
   frame uploads everything. When it is set, MPFX clears it and, per buffer type, guards on
   `LookupSpan != null`. It then fetches a `Span<T>` by `KeyHash.Make("<Id>")` and assigns `[0].a` (pre)
   and `[0].b` (post) ([MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L140-L221)).
3. **Neutral substitution.** A disabled effect gets its identity value, not its slider value: brightness,
   contrast and saturation 1, temperature 0, hue 0, overlay and vignette `float4.Zero`, color balance a
   zero matrix (flag `[3,3] = 0`), and blur radius 0. The slider value survives in the profile, so
   re-enabling restores it. The shader side treats these values as pass-through (see
   [Shader contract for chain passes](effect-shader-contract.md)).
4. **Unit conversion on the CPU.** Hue is entered in degrees and uploaded in radians
   (`/ 180f * Math.PI`). The film grain buffer's second `vec4` carries `renderer.Extent` width and
   height. Blur weights are computed from the radius (see
   [Separable Gaussian blur with CPU-computed weights](separable-gaussian-blur.md)).
5. **Raising the flag.** Every widget in the settings window ORs its return value into a local
   `valuesChanged`, and the window ends with `UpdateValues |= valuesChanged`
   ([MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L1265)). Upload happens at the top of the method, so an edit
   made in frame N is uploaded in frame N+1 and rendered in frame N+1.

## Lifecycle and timing

- **Hook: `frame:draw-ui`.** StarMap invokes `[StarMapAfterGui]` from a postfix on
  `Program.OnDrawUiViewports` ([StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L26-L36)). In
  `Program.OnFrame`, that call is inside `if (DrawUI)`, between `PrepareImGui` and `ImGui.Render`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2261-L2286)).[^starmap-programpatcher] [^ksa-program]
  Like every StarMap frame hook it runs after `PrepareFrame` has dispatched the next physics step.
  MPFX touches no simulated state there; it only reads `Universe.SimulationSpeed`, a main-thread
  static.
- **Consumed in `frame:render`.** Later in the same `OnFrame`, `Render` calls `RenderGame`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2443-L2480)). Each pass then calls
  `shader.PushConstants`, which copies the push-constant field's current bytes into the command buffer
  ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L95-L103),
  [ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs#L121)). So the push-constant value
  recorded is the one MPFX wrote this frame.[^sx-shaderex] The uniform buffers are host-coherent mapped
  memory, read by the GPU when it executes the passes, not when they are recorded.[^sx-shaderbinding]
  `RenderGame` is skipped on frames where `Render` rebuilds the renderer, in the vehicle editor and
  on early-exit frames. The written values simply persist until the next recorded frame.
- **GPU still reading.** `AfterGui` runs before `OnPreRender`, where `Renderer.TryAcquireNextFrame`
  waits on the fence of the frame slot it reuses (`MaxFramesInFlight` is 2)
  ([current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L336-L351)). The
  previous frame's passes may therefore still be executing when MPFX rewrites a buffer, because
  each buffer is a single allocation, not one per frame in flight. Push constants avoid this, since
  their bytes are copied into the command buffer at record time.[^core-renderer]
- **Why this hook.** It is the only per-frame hook MPFX needs: the window needs an open ImGui frame,
  and the upload must happen before command recording. Doing both in one method means no extra
  synchronisation.
- **HUD hidden.** With `DrawUI == false`, `AfterGui` is not called. The buffers keep their last values,
  so the grade stays on, but the film grain clock stops and the grain freezes.

## Key code

| Path | Role |
|---|---|
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L120-L221) | `AfterGui`: push constants, dirty-flag upload of every buffer |
| [MPFX/MPFXProfile.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXProfile.cs#L33-L215) | All parameter fields, one enable flag and one value per side |
| [ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L63-L162) (ShaderExtensions) | Mapped buffer and push-constant storage behind the spans |

## Snippets

```csharp
// MPFX/MPFX.cs L120-L160
[StarMapAfterGui]
public void AfterGui(double dt)
{
    Renderer renderer = Program.GetRenderer();

    //Vehicle? rocket = Program.ControlledVehicle;
    //float t = rocket.GetManualThrottle();

    if (MPFXPushConstantsFloatBuffer.LookupSpan != null)
    {
        CurrentProfile.FilmGrainPreTime += (float)dt * CurrentProfile.FilmGrainPreTimeMultiplier * (CurrentProfile.FilmGrainPreTimeWarpMultiplier ? (float)Universe.SimulationSpeed : 1f);
        CurrentProfile.FilmGrainPostTime += (float)dt * CurrentProfile.FilmGrainPostTimeMultiplier * (CurrentProfile.FilmGrainPostTimeWarpMultiplier ? (float)Universe.SimulationSpeed : 1f);

        Span<MPFXPushConstantsFloatBuffer> FilmgrainPreTime = MPFXPushConstantsFloatBuffer.LookupSpan(KeyHash.Make("MPFXFilmgrainPreTimeBuffer"));
        FilmgrainPreTime[0].a = CurrentProfile.FilmGrainPreTime;

        Span<MPFXPushConstantsFloatBuffer> FilmgrainPostTime = MPFXPushConstantsFloatBuffer.LookupSpan(KeyHash.Make("MPFXFilmgrainPostTimeBuffer"));
        FilmgrainPostTime[0].a = CurrentProfile.FilmGrainPostTime;
    }

    if (UpdateValues)
    {
        UpdateValues = false;

        if (MPFXDefaultBuffer.LookupSpan != null)
        {
            Span<MPFXDefaultBuffer> BrightnessData = MPFXDefaultBuffer.LookupSpan(KeyHash.Make("MPFXBrightnessBuffer"));
            BrightnessData[0].a = CurrentProfile.BrightnessPreImgui ? CurrentProfile.BrightnessFloatPreImgui : 1f;
            BrightnessData[0].b = CurrentProfile.BrightnessPostImgui ? CurrentProfile.BrightnessFloatPostImgui : 1f;

            Span<MPFXDefaultBuffer> ContrastData = MPFXDefaultBuffer.LookupSpan(KeyHash.Make("MPFXContrastBuffer"));
            ContrastData[0].a = CurrentProfile.ContrastPreImgui ? CurrentProfile.ContrastFloatPreImgui : 1f;
            ContrastData[0].b = CurrentProfile.ContrastPostImgui ? CurrentProfile.ContrastFloatPostImgui : 1f;

            Span<MPFXDefaultBuffer> SaturationData = MPFXDefaultBuffer.LookupSpan(KeyHash.Make("MPFXSaturationBuffer"));
            SaturationData[0].a = CurrentProfile.SaturationPreImgui ? CurrentProfile.SaturationFloatPreImgui : 1f;
            SaturationData[0].b = CurrentProfile.SaturationPostImgui ? CurrentProfile.SaturationFloatPostImgui : 1f;

            Span<MPFXDefaultBuffer> ColorTempData = MPFXDefaultBuffer.LookupSpan(KeyHash.Make("MPFXColorTempBuffer"));
            ColorTempData[0].a = CurrentProfile.ColorTempPreImgui ? CurrentProfile.ColorTempFloatPreImgui : 0f;
            ColorTempData[0].b = CurrentProfile.ColorTempPostImgui ? CurrentProfile.ColorTempFloatPostImgui : 0f;
            // ...
```

```csharp
// MPFX/MPFX.cs L187-L214
if (MPFXVec4Buffer.LookupSpan != null)
{
    Span<MPFXVec4Buffer> colorOverlayData = MPFXVec4Buffer.LookupSpan(KeyHash.Make("MPFXColorOverlayBuffer"));
    colorOverlayData[0].a = CurrentProfile.ColorOverlayPreImgui ? CurrentProfile.ColorOverlayFloatPreImgui : float4.Zero;
    colorOverlayData[0].b = CurrentProfile.ColorOverlayPostImgui ? CurrentProfile.ColorOverlayFloatPostImgui : float4.Zero;

    Span<MPFXVec4Buffer> vignettePreData = MPFXVec4Buffer.LookupSpan(KeyHash.Make("MPFXVignettePreBuffer"));
    vignettePreData[0].a = CurrentProfile.VignettePreImgui ? CurrentProfile.VignetteFloatPreImgui : float4.Zero;
    vignettePreData[0].b = CurrentProfile.VignettePreImgui ? CurrentProfile.VignetteColorPreImgui : float4.Zero;

    Span<MPFXVec4Buffer> vignettePostData = MPFXVec4Buffer.LookupSpan(KeyHash.Make("MPFXVignettePostBuffer"));
    vignettePostData[0].a = CurrentProfile.VignettePostImgui ? CurrentProfile.VignetteFloatPostImgui : float4.Zero;
    vignettePostData[0].b = CurrentProfile.VignettePostImgui ? CurrentProfile.VignetteColorPostImgui : float4.Zero;

    Span<MPFXVec4Buffer> FilmGrainData = MPFXVec4Buffer.LookupSpan(KeyHash.Make("MPFXFilmGrainBuffer"));
    FilmGrainData[0].a = new float4(
        CurrentProfile.FilmGrainPreImgui ? CurrentProfile.FilmGrainData.XY : float2.Zero,
        CurrentProfile.FilmGrainPostImgui ? CurrentProfile.FilmGrainData.ZW : float2.Zero
    );
    FilmGrainData[0].b = new float4(renderer.Extent.Width, renderer.Extent.Height, 0.0f, 0.0f); // width, height, framenum
}

if (MPFXMat4Buffer.LookupSpan != null)
{
    Span<MPFXMat4Buffer> ColorBalanceData = MPFXMat4Buffer.LookupSpan(KeyHash.Make("MPFXColorBalanceBuffer"));
    ColorBalanceData[0].a = CurrentProfile.ColorBalancePreImgui ? CurrentProfile.ColorBalanceMatPreImgui : new float4x4();
    ColorBalanceData[0].b = CurrentProfile.ColorBalancePostImgui ? CurrentProfile.ColorBalanceMatPostImgui : new float4x4();
}
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Program.GetRenderer()`, `Renderer.Extent` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L557-L560), [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L96) | Screen size for film grain and window placement[^core-renderer] |
| `Universe.SimulationSpeed` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L122) | Optional time-warp scaling of grain animation[^ksa-universe] |
| `KeyHash.Make` | [current/decomp/KSA/KeyHash.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KeyHash.cs#L15) | Buffer lookup key from the XML `Id`[^ksa-keyhash] |
| `Program.OnDrawUiViewports` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3162) | Target of StarMap's `AfterGui` postfix |

## Pitfalls and version notes

- MPFX re-hashes every Id string with `KeyHash.Make` on each upload. That is cheap, but caching the
  `KeyHash` values is the cleaner pattern.
- `ApplyProfile` swaps `CurrentProfile` without raising `UpdateValues`
  ([MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L35-L40)). A newly selected profile reaches the GPU only after the next
  widget change.
- Some edits do not raise the flag. The pre-side per-channel Green sliders discard
  `ImguiSliderRow`'s return value ([MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L399-L409)), and the pre-side Color
  Temperature Reset does not set `valuesChanged` ([MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L574-L578)).
- The film grain time accumulates in a `float` and never wraps, so it loses precision over very long
  sessions or with high multipliers. This has not been tested in game.
- The upload happens before the window code. Resizing the window does not refresh the film grain
  resolution until something else raises the flag.
- A dirty-flag write can show up one frame early, in the frame the GPU is still executing (see
  "GPU still reading" above). For a settings slider that is invisible; for data that must change
  exactly on a frame boundary, use one buffer slice per frame in flight or a push constant.

## See also

- [Effect settings window and ModMenu entry](effect-settings-window.md)
- [Uniform buffer and push constant structs bound by attribute name](duck-typed-buffer-structs.md)
- [Film grain and experimental effects](film-grain-and-experimental-effects.md)
- [Push constants for a shader: per-side clock and strengths in a settings-driven mod](../../../howto/shaders/post-processing/send-push-constants-to-a-shaderextensions-shader/send-push-constants-to-a-shaderextensions-shader-2.md) - how-to article demonstrating this feature

[^mpfx-main]: MPFX/MPFX.cs
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^sx-shaderex]: ShaderExtensions ShaderEx.cs
[^sx-shaderbinding]: ShaderExtensions ShaderBinding.cs
[^core-renderer]: Core.Renderer (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-keyhash]: KSA.KeyHash (decompiled, build 2026.10.10.5554)
