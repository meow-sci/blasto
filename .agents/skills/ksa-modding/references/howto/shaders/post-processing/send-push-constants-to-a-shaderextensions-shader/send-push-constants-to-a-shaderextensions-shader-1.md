---
type: How-To
title: "Push constants for a shader: one element per pass with an enabled flag and time, written from OnFrameEditor"
description: Declare a push-constant struct, give each pass its own element, and toggle an always-on post-processing pass with an enabled flag that selects an early pass-through branch
category: shaders
group: post-processing
topic: send-push-constants-to-a-shaderextensions-shader
approach: 1
difficulty: intermediate
prerequisites: [starmap, shaderextensions, kittenextensions, harmony]
demonstrated_by:
  - mods/ksa-aircrafthud/features/sx-push-constants-and-buffers
  - mods/shaderextensions/features/push-constants
  - mods/ksa-aircrafthud/features/glass-post-process-chain
techniques: [vulkan-pipeline, glsl-shader, harmony-postfix]
hooks:
  - harmony-postfix KSA.Program.OnFrameEditor
  - xml-asset PPPushConstantsBufferAsset
  - harmony-prefix KSA.Program.Main (ShaderExtensions AssetEx.Init)
lifecycle: [startup:starmap-before-main, startup:asset-load, frame:viewports, render:post-process]
game_types: [KSA.LookupCollection, KSA.KeyHash, Brutal.VulkanApi.VkDeviceExtensions, KSA.Program]
tags: [push-constants, toggle, enabled-flag, pass-through, aircrafthud, std430, frame-timing]
---
A ShaderExtensions pass cannot be removed while the game runs, but it can be told to do nothing. KSA_AircraftHUD does that with a
two-field push constant, `enabled` and `frame`, written every frame to each of its three glass passes. This article walks through
declaring the struct, the XML element, the GLSL block, the per-frame write and the early pass-through branch. Prerequisites are
ShaderExtensions with KittenExtensions Continued; the mod needs no assembly reference to ShaderExtensions (see
[Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../feed-uniform-buffers-to-a-shaderextensions-shader/overview.md)). The pinned ShaderExtensions skips this mod's passes for a different
reason (removed subpass attributes); the push-constant mechanism itself is current.

## Background: how the game does it

Vulkan push constants are a small block of bytes (guaranteed 128 bytes) carried in the command buffer. ShaderExtensions keeps the value
in a managed field of the XML element object and issues `PushConstants` for each range when the pass records
([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L95-L103)). `GetSpan(key)` returns a one-element span over that field
([ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L126-L162)). Because the bytes are copied at record time, a write made earlier in the same frame
is used in that frame and a write after `RenderGame` applies to the next one. The key facts about the game side are in the overview.

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Struct discovered, element registered, lookup delegate set | ShaderExtensions' `Program.Main` prefix | `startup:starmap-before-main` |
| Element object created and registered by `Id` | `ModLibrary.LoadAll` (no GPU resource, so lookups work once assets are loaded) | `startup:asset-load` |
| Write | The mod's `Program.OnFrameEditor` postfix, inside `DrawHud` | `frame:viewports` |
| Bytes captured | `Program.RenderGame` records each pass | `render:post-process` |

The `OnFrameEditor` postfix is the earliest ImGui point of a flight frame and runs even when the HUD is hidden, so the toggle keeps
working there. `[StarMapAfterGui]` (as MPFX uses, in the next article) does not fire while the HUD is hidden. See the
[game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare the struct.** The attribute names the XML element; the static `Span<T>` delegate is the only lookup push constants accept:

   ```csharp
   // ShaderExtensionsHelpers.cs L64-L73
       [StructLayout(LayoutKind.Sequential, Pack = 1)]
       [SxPushConstant("PPPushConstantsBufferAsset")]
       public struct PPPushConstantsBuffer
       {
           public int enabled;
           public float frame;

           // lookup delegate fields must be static fields on the buffer element type
           [SxPushConstantLookup] public static AHSpanLookup<PPPushConstantsBuffer> LookupSpan; // gives a Span<T> of length Size
       }
   ```

   Source: [ShaderExtensionsHelpers.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ShaderExtensionsHelpers.cs#L64-L73). The attribute and delegate shims go in `namespace ShaderExtensions` of your assembly
   ([ShaderExtensionsHelpers.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ShaderExtensionsHelpers.cs#L12-L33)). The struct size must be a multiple of 4 and the total per shader at most 128 bytes.

2. **Add one element per pass.** A push constant belongs to one shader and cannot be shared (a repeated `Id` logs a duplicate error and
   keeps the first), so the mod declares three:

   ```xml
   <!-- HudAssests.xml L7-L13 (KSA_AircraftHUD, excerpt) -->
   	<PostProcessingShader Id="GlassBlurH" Path="Shaders/GaussianBlurH.frag" RenderPassId="62" RequiresUniqueRenderpass="true" >
   		<PPPushConstantsBufferAsset Id="PPPushConstantsBuffer0" Size="1" />
   		<TextureBinding Path="Textures/DirtMask.png" />
   	</PostProcessingShader>
   	<PostProcessingShader Id="GlassBlurV" Path="Shaders/GaussianBlurV.frag" RenderPassId="63" RequiresUniqueRenderpass="true" >
   		<PPPushConstantsBufferAsset Id="PPPushConstantsBuffer1" Size="1" />
   	</PostProcessingShader>
   ```

   Source: [HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml#L7-L13) (the two attributes are the old-API ones the pinned ShaderExtensions rejects). The `Size` attribute is ignored: the
   element has only `Id` and `Stage` (default `FragmentBit`).

3. **Match the GLSL block.** `push_constant` blocks take no `set` or `binding`. The HUD's shaders read them as:

   ```glsl
   // Shaders/GaussianBlurH.frag L12-L15
   layout(push_constant, std430) uniform Params {
      int enabled;
      float frame;
   } pp;
   ```

   Source: [Shaders/GaussianBlurH.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/GaussianBlurH.frag#L12-L15). Layout must be equal byte for byte with the C# struct.

4. **Write every frame, with cached hashes.** The hashes are cached once; the write fills all three spans:

   ```csharp
   // AircraftHUDMain.cs L936-L954
           public void SetPostProcess(int enabled, float frame)
           {
               if (PPPushConstantsBuffer.LookupSpan != null)
               {
                   //CurrentProfile.FilmGrainPreTime += (float)dt * CurrentProfile.FilmGrainPreTimeMultiplier * (CurrentProfile.FilmGrainPreTimeWarpMultiplier ? (float)Universe.SimulationSpeed : 1f);

                   Span<PPPushConstantsBuffer> PP0 = PPPushConstantsBuffer.LookupSpan(PPBuffer0Hash);
                   PP0[0].enabled = enabled;
                   PP0[0].frame = frame;

                   Span<PPPushConstantsBuffer> PP1 = PPPushConstantsBuffer.LookupSpan(PPBuffer1Hash);
                   PP1[0].enabled = enabled;
                   PP1[0].frame = frame;

                   Span<PPPushConstantsBuffer> PP2 = PPPushConstantsBuffer.LookupSpan(PPBuffer2Hash);
                   PP2[0].enabled = enabled;
                   PP2[0].frame = frame;
               }
           }
   ```

   Source: [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L936-L954). The caller picks the flag:

   ```csharp
   // AircraftHUDMain.cs L1019-L1027
               if (CamMode.Equals(CameraMode.IVA) || effectsOutsideIVA)
               {
                   SxImGui.CustomShader(HudShader);
                   SetPostProcess(1, FrameTime);
               }
               else
               {
                   SetPostProcess(0, FrameTime);
               }
   ```

   Source: [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1019-L1027), reached from the `OnFrameEditor` postfix ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1332-L1340)).

5. **Branch to a pass-through in the shader.** With `enabled == 0` the pass copies its input. The uniform branch is coherent across the
   screen, so it costs almost nothing beyond the fullscreen draw itself:

   ```glsl
   // Shaders/PostProcess.frag L22-L54
   void main()
   {
   	vec4 color;
   	if (pp.enabled == 1)
   	{
   		vec2 zoomUv = Uv - 0.5;
   		zoomUv *= 0.975;
   		zoomUv += 0.5;

   		vec2 texelSize = 1.0 / vec2(textureSize(Source, 0));

   		color = ChromaticAberration(Source, zoomUv, texelSize, -15.0);

   		vec2 curveUv = CurvedUV(zoomUv, -0.1, 1.12);
   		vec4 refracted = RefractionUVs(curveUv, texelSize, dirtMask);

   		vec4 original = texture(PreviousInput, refracted.rg);
   		color = DirtMask(Uv, texelSize, original, color, refracted.b);

   //		color = mix(vec4(0,0,0,1), color, refracted.a);

   		outColor = color;
   	}
   	else
   	{
   		color = texture(PreviousInput, Uv);
   	}

   	// RGB Blue Noise (STBN) LDR Dithering
   	color = BlueNoiseDither(color, noiseTex, pp.frame, 0.25);

   	outColor = color;
   }
   ```

   Source: [Shaders/PostProcess.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/PostProcess.frag#L22-L54) (v0.4.0 binding numbers; see the port notes in
   [Write a fullscreen post-processing fragment shader for KSA](../write-a-post-processing-fragment-shader/overview.md)). Note the dither line after the branch: the "off" state still adds grain.

## Complete example

| File | Role |
|---|---|
| [ShaderExtensionsHelpers.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ShaderExtensionsHelpers.cs) | Shims and the struct |
| [HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml) | One element per pass |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs) | `SetPostProcess`, caller, `OnFrameEditor` postfix |
| [Shaders/PostProcess.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/PostProcess.frag), [Shaders/GaussianBlurH.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/GaussianBlurH.frag) | Shader side |

## Variations and gotchas

- **Stuck on.** `SetPostProcess` is only reached when the HUD draws. Leaving IVA for the map or a no-vehicle scene returns from `DrawHud` earlier,
  so the last value (on) stays and the glass effect remains.
- **Typos throw.** `GetSpan` dereferences `LookupCollection.Get`, which returns null for a missing key; the mod only null-checks the delegate.
- **Overlapping stage ranges.** If a push constant overlaps another range with different stage flags, every stage must be included in each
  `vkCmdPushConstants`; `ImGuiShader` fragment ranges overlap the 32-byte vertex range at offset 0, so prefer a uniform buffer there.
- **Layout.** Use `Pack = 1` or explicit padding so the C# bytes equal the GLSL block; keep the total under 128 bytes.
- **Neutral values instead of a flag** is the other way to disable a pass; see the MPFX article.

## See also

- Siblings: [per-side clock in a settings mod](send-push-constants-to-a-shaderextensions-shader-2.md), [ShaderTime block](send-push-constants-to-a-shaderextensions-shader-3.md), [sim seconds through a stock field](send-push-constants-to-a-shaderextensions-shader-4.md), [overview](overview.md)
- [ShaderExtensions push constants and uniform buffers from C#](../../../../mods/ksa-aircrafthud/features/sx-push-constants-and-buffers.md), [Mod-defined push constants](../../../../mods/shaderextensions/features/push-constants.md), [Canopy-glass post-process chain](../../../../mods/ksa-aircrafthud/features/glass-post-process-chain.md)
- [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../feed-uniform-buffers-to-a-shaderextensions-shader/overview.md), [Draw full-screen click-through overlays and choose the right ImGui draw list](../../../ui/overlays/draw-a-fullscreen-click-through-overlay/overview.md)
