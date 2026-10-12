---
type: Mod Feature
title: Canopy-glass post-process chain
description: Three ShaderExtensions PostProcessingShader passes (refracted two-pass Gaussian blur, then a composite with chromatic aberration, edge blur, glowing dirt and grain) that make the IVA scene look as if seen through a canopy
mod: ksa-aircrafthud
category: shaders
techniques: [post-processing, glsl-shader, xml-asset, vulkan-texture]
hooks: [harmony-postfix KSA.Program.OnFrameEditor]
lifecycle: [startup:asset-load, frame:viewports, render:post-process]
game_types: [KSA.Mod, KSA.Program]
tags:
  - shaders
  - post-processing
  - shaderextensions
  - gaussian-blur
  - refraction
  - dirt-mask
  - chromatic-aberration
  - glsl
  - iva
---

In the IVA camera the scene behind the HUD is treated as if seen through a scratched, slightly
curved canopy: the image is refracted by a normal map, blurred more toward the edges, smudges glow
where bright light passes through them, colors fringe toward the corners, and blue-noise grain hides
banding. It is built from three `PostProcessingShader` assets declared in the mod's asset XML and
ordered by `RenderPassId`; the C# side only toggles it and feeds a time value.[^ah-assets]
[^ah-postprocess]

## How it works

**Registration.** `mod.toml` lists `assets = [ "HudAssests.xml" ]`; KSA loads each listed file as an
`AssetBundle` ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L157-L168)) and
ShaderExtensions has registered `PostProcessingShader` as an XML asset type (via KittenExtensions'
`KxAsset`), so each element becomes a `PostProcessingShaderAsset`.[^ksa-mod] [^sx-ppasset] Each
shader runs with the game's `ScreenspaceVert` full-screen vertex shader by default
([current/Content/Core/DefaultAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/DefaultAssets.xml#L94)).[^ksa-defaultassets]

| Pass (`RenderPassId`) | Asset | Inputs (as written for ShaderExtensions v0.4.0) | Does |
|---|---|---|---|
| 62 | `GlassBlurH`, [Shaders/GaussianBlurH.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/GaussianBlurH.frag) | `Source` = scene; `dirtMask` texture; push constant `PPPushConstantsBuffer0` | Barrel-curve the UV, offset it by the dirt mask's normal (`RefractionUVs`), 97-tap horizontal Gaussian |
| 63 | `GlassBlurV`, [Shaders/GaussianBlurV.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/GaussianBlurV.frag) | `Source` = pass 62 output; `PPPushConstantsBuffer1` | 97-tap vertical Gaussian |
| 64 | `PostProcess`, [Shaders/PostProcess.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/PostProcess.frag) | `Source` = blurred image; `PreviousInput` = input of pass 62 (the unblurred scene) via `<PostProcessingInputReference RenderPassId="62" SubpassId="0" />`; `noiseTex` = STBN; `dirtMask`; `PPPushConstantsBuffer2` | Composite (below) and dither |

The composite in `PostProcess.frag`, when `pp.enabled == 1`
([Shaders/PostProcess.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/PostProcess.frag#L22-L53)):

1. Zoom the UV in by 2.5 percent around the center.
2. Sample the blurred `Source` with `ChromaticAberration(..., -15.0)`: red and blue offset along the
   radius, growing with `r^2`.
3. Curve the UV and refract it through the dirt mask, then sample the sharp `PreviousInput` there.
4. `DirtMask(...)`: mix sharp toward half-brightness blur with a radial mask that rises
   exponentially toward the corners (sharp center, soft edges), add the dirt amount (mask blue
   channel, gated by blur luminance), then add `dirt * blur * 12` so smudges glow in bright light
   ([Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl#L60-L112)).
5. Always, enabled or not: `BlueNoiseDither(color, noiseTex, pp.frame, 0.25)` (see
   [Spatiotemporal blue-noise dithering](blue-noise-dithering.md)).

**The dirt mask.** [Textures/DirtMask.PNG](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Textures/DirtMask.PNG) is a 2560x1440 RGB image used
as packed data, not color: red and green are a tangent-space normal (`rg * 2 - 1`, scaled by
`RefractionStrength = 50` texels) that bends the sample position, and blue is the amount of dirt at
that pixel. The shader also reads alpha, which an RGB PNG supplies as 1.[^ah-dirtmask]
[^ah-functions]

**Gaussian kernel.** `GaussianBlur` takes 48 samples each side with a non-linear distribution
(`sign(t) * |t|^2`) across a 512-pixel radius and sigma 16, clamping UVs half a texel inside the
image ([Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl#L114-L150)). Splitting H and V turns a
97x97 kernel into 2x97 taps per pixel.

**Switching.** Post-process shaders cannot be disabled in ShaderExtensions (README limitation), so
each shader branches on `pp.enabled`. `SetPostProcess(1, FrameTime)` runs when the camera is `IVA`
or "Show Effects" is on, `SetPostProcess(0, FrameTime)` otherwise
([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1019-L1027)); see
[ShaderExtensions push constants and uniform buffers from C#](sx-push-constants-and-buffers.md).
Disabled, pass 64 outputs `PreviousInput` unchanged except for the grain.[^ah-main]

## Lifecycle and timing

- **`startup:asset-load`.** The XML is parsed in `ModLibrary.LoadAll` / `Mod.LoadAssetBundles`;
  shaders compile and bind when the game binds assets.
- **`frame:viewports`.** The push constants are written from the `Program.OnFrameEditor` postfix,
  while the HUD is drawn.
- **`render:post-process`.** ShaderExtensions records the pre-ImGui chain inside its replacement for
  the `BeginRenderPass` that precedes the final composite in `Program.RenderGame`, sorting passes by
  `RenderPassId`, then copies the chain output back into the game's offscreen color image
  ([ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs#L38-L46),
  [ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L64-L90)).[^sx-pphandler]
  [^sx-chain] The game then composites and draws ImGui on top, so the HUD and all windows stay sharp.
  The chain works on the game's HDR offscreen image (`Program.ColorFormat` is
  `R16G16B16A16SFloat`), before the tonemap composite
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L223)).[^ksa-program]
  Push constant values are copied into the command buffer when each pass is recorded
  ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L95-L103)), so a value written
  earlier in the same frame is used in that frame.[^sx-shaderex]

## Key code

| Path | Role |
|---|---|
| [HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml#L3-L19) | Three passes (plus a commented-out single-pass `GlassBlur`) |
| [Shaders/GaussianBlurH.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/GaussianBlurH.frag) | Curved, refracted horizontal blur |
| [Shaders/GaussianBlurV.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/GaussianBlurV.frag) | Vertical blur |
| [Shaders/PostProcess.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/PostProcess.frag) | Composite and dither |
| [Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl) | Shared helpers, include-guarded |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L936-L954) | `SetPostProcess` |

## Snippets

```xml
<!-- HudAssests.xml L7-L19 -->
	<PostProcessingShader Id="GlassBlurH" Path="Shaders/GaussianBlurH.frag" RenderPassId="62" RequiresUniqueRenderpass="true" >
		<PPPushConstantsBufferAsset Id="PPPushConstantsBuffer0" Size="1" />
		<TextureBinding Path="Textures/DirtMask.png" />
	</PostProcessingShader>
	<PostProcessingShader Id="GlassBlurV" Path="Shaders/GaussianBlurV.frag" RenderPassId="63" RequiresUniqueRenderpass="true" >
		<PPPushConstantsBufferAsset Id="PPPushConstantsBuffer1" Size="1" />
	</PostProcessingShader>
	<PostProcessingShader Id="PostProcess" Path="Shaders/PostProcess.frag" RenderPassId="64" SubpassId="0" >
		<PPPushConstantsBufferAsset Id="PPPushConstantsBuffer2" Size="1" />
		<PostProcessingInputReference RenderPassId="62" SubpassId="0" />
		<TextureBinding Path="Textures/stbn_vec3_128x128x64.png" />
		<TextureBinding Path="Textures/DirtMask.png" />
	</PostProcessingShader>
```

```glsl
// Shaders/Functions.glsl L83-L112
// normal.x, normal.y, dirt, alpha
vec4 RefractionUVs(vec2 uv, vec2 texelSize, sampler2D dirtMask)
{
	vec4 dirtMaskRGBA = texture(dirtMask, uv);

    // tangent-space normal
    vec2 n = dirtMaskRGBA.rg * 2.0 - 1.0;
    vec2 refractOffset = n * RefractionStrength * texelSize;
    vec2 refractedUv = uv + refractOffset;

    return vec4(refractedUv, dirtMaskRGBA.b, dirtMaskRGBA.a);
}

const float brightness = 0.5;
vec4 DirtMask(vec2 uv, vec2 texelSize, vec4 color, vec4 blur, float dirt)
{
    float lum = Luminance(blur.rgb);
	float lumMask = smoothstep(0.02, 0.2, lum);

    float mask = RadialGradient(uv, texelSize);

	dirt *= lumMask;

    float finalMask = clamp(mask + dirt, 0.0, 1.0);

    color = mix(color, brightness * blur, finalMask);
    color += dirt * (blur * 12.0);

    return color;
}
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Mod.Assets` (`[TomlField("assets")]`), `Mod.LoadAssetBundles()` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L65-L66) | Loads `HudAssests.xml` |
| `ScreenspaceVert` shader asset | [current/Content/Core/DefaultAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/DefaultAssets.xml#L94) | Default vertex stage of every pass |
| `Program.RenderGame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4948-L4950) | Composite and ImGui draw after the chain |

## Pitfalls and version notes

- **Does not load on the pinned ShaderExtensions.** Since the dynamic-rendering rewrite
  (ShaderExtensions commit `2d76148`, for KSA 2026.8.5), any pass that declares `SubpassId` or
  `RequiresUniqueRenderpass="true"` is skipped with a logged error
  ([ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L263-L282)).
  All three AircraftHUD passes declare one of them.[^sx-chain] To port: delete both attributes,
  keep the `RenderPassId` order, drop `SubpassId` from the input reference, and shift every extra
  binding up by one because the shared time buffer now sits at set 1, binding 1 and extras start at
  binding 2 (ShaderExtensions [README.md](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/README.md#L199-L208)).[^sx-readme]
- **Binding order is XML order.** `ShaderEx.Bind` appends descriptor bindings in the order of the
  child elements (push-constant elements take no binding), which is how `PreviousInput`, `noiseTex`
  and `dirtMask` got bindings 1, 2, 3 in v0.4.0
  ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L36-L50)). Reordering XML
  children silently swaps textures.
- **v0.4.0 subpass mismatch (unverified at runtime).** In ShaderExtensions v0.4.0 (commit
  `5317c7a`, not the pinned ref) a pass without `RequiresUniqueRenderpass` was bound as an input
  attachment at binding 0, while `PostProcess.frag` declares `sampler2D Source` (its `subpassInput`
  line is commented out). Whether the author's build tolerated this cannot be confirmed here.
- **File name case.** The XML asks for `Textures/DirtMask.png`; the file is `DirtMask.PNG`. Fine on
  Windows, a missing texture on case-sensitive file systems.
- **Push constant left on.** `SetPostProcess` is only reached when the HUD draws. Leaving IVA for the
  map, free camera or a no-vehicle scene returns from `DrawHud` earlier, so the last value (on)
  stays in the push constant and the glass effect remains
  ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L960-L976)).
- **Grain when disabled.** The dither line runs after the `if/else`, so the "off" state still adds
  multiplicative noise of up to plus or minus 12.5 percent.
- [Shaders/RadialBlur.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/RadialBlur.frag) is an earlier single-pass version, kept
  but commented out in the XML; it calls `ScreenNoise`, which `Functions.glsl` only defines as
  `ScreenNoise_DEPRECATED`, so it would not compile as is.[^ah-radialblur]
- Pre-ImGui post-processing only targets the main window (ShaderExtensions README).
- **Not visible with CMAA2.** `RenderGame` calls `RenderAaPreparePasses` before the
  `BeginRenderPass` that ShaderExtensions replaces. With CMAA2 selected, that call has already
  composited the offscreen image into CMAA2's own LDR target, and `RenderFinalComposite` only blits
  that target, so the chain's copy-back arrives too late to be seen
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4628-L4644),
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4945-L4950),
  [current/decomp/KSA.Rendering.PostProcessing/Cmaa2Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.PostProcessing/Cmaa2Renderer.cs#L166-L176)).
  The default anti-aliasing setting is 2x MSAA
  ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1303)),
  which takes the normal composite path.[^ksa-gamesettings] This follows
  from the code; it was not observed in game.[^ksa-program] [^ksa-cmaa2]
- **`CurvedUV` ignores its arguments.** The helper reads the file-level constants `curveStrength =
  -0.15` and `curveScale = 1.15` instead of its `strength` and `scale` parameters, so the `-0.1, 1.12`
  passed by `GaussianBlurH.frag` and `PostProcess.frag` have no effect
  ([Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl#L42-L58)).[^ah-functions]

## See also

- [Spatiotemporal blue-noise dithering](blue-noise-dithering.md)
- [ShaderExtensions push constants and uniform buffers from C#](sx-push-constants-and-buffers.md)
- [Curved, chromatic HUD window via an ImGuiShader](imgui-window-hud-shader.md)
- [KittenExtensions custom XML asset types](../../kittenextensions/features/custom-xml-asset-types.md)
- [Declare post-processing assets: a small file with a chain, an input reference and an ImGuiShader](../../../howto/shaders/post-processing/declare-post-processing-shader-assets-in-xml/declare-post-processing-shader-assets-in-xml-2.md) - how-to article demonstrating this feature
- [Push constants for a shader: one element per pass with an enabled flag and time, written from OnFrameEditor](../../../howto/shaders/post-processing/send-push-constants-to-a-shaderextensions-shader/send-push-constants-to-a-shaderextensions-shader-1.md) - how-to article demonstrating this feature
- [Write a post-processing fragment shader: extra textures and the port from subpasses to dynamic rendering](../../../howto/shaders/post-processing/write-a-post-processing-fragment-shader/write-a-post-processing-fragment-shader-2.md) - how-to article demonstrating this feature
- [Bend the picture: chromatic aberration and radial edge blur in a glass composite pass](../../../howto/shaders/screen-effects/bend-the-picture-with-crt-curvature-and-chromatic-aberration/bend-the-picture-with-crt-curvature-and-chromatic-aberration-2.md) - how-to article demonstrating this feature
- [Blur the screen: a non-linear tap distribution and a refracted horizontal pass](../../../howto/shaders/screen-effects/blur-the-screen-with-a-separable-gaussian/blur-the-screen-with-a-separable-gaussian-2.md) - how-to article demonstrating this feature
- [Refract the scene: normal-mask UV offsets and luminance-gated dirt in a fragment pass](../../../howto/shaders/screen-effects/refract-and-distort-the-scene/refract-and-distort-the-scene-1.md) - how-to article demonstrating this feature

[^ah-assets]: KSA_AircraftHUD/HudAssests.xml
[^ah-postprocess]: KSA_AircraftHUD/Shaders/PostProcess.frag
[^ah-functions]: KSA_AircraftHUD/Shaders/Functions.glsl
[^ah-dirtmask]: KSA_AircraftHUD/Textures/DirtMask.PNG
[^ah-radialblur]: KSA_AircraftHUD/Shaders/RadialBlur.frag
[^ah-main]: KSA_AircraftHUD/AircraftHUDMain.cs
[^sx-readme]: ShaderExtensions/README.md
[^sx-ppasset]: ShaderExtensions PostProcessingShaderAsset.cs
[^sx-chain]: ShaderExtensions PostProcessingChain.cs
[^sx-pphandler]: ShaderExtensions PostProcessingHandler.cs
[^sx-shaderex]: ShaderExtensions ShaderEx.cs
[^ksa-mod]: KSA.Mod (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-cmaa2]: KSA.Rendering.PostProcessing.Cmaa2Renderer (decompiled, build 2026.10.10.5554)
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-defaultassets]: Content/Core/DefaultAssets.xml (build 2026.10.10.5554)
