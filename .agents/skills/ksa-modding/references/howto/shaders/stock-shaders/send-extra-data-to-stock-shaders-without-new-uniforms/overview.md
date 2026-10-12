---
type: Game Feature
title: Smuggle data into stock shaders through unused uniforms, state bits and sentinel values
description: Stock shaders have fixed inputs, so a patched shader borrows fields that are free or irrelevant for the case at hand and decodes them in GLSL
category: shaders
group: stock-shaders
topic: send-extra-data-to-stock-shaders-without-new-uniforms
demonstrated_by:
  - mods/realatmospheres/features/atmosphere-uniform-side-channel
  - mods/unscience/features/humble-arteest-vehicle-paint-shader-patch
  - mods/gatos/features/vehicle-and-eva-paint
  - mods/unscience/features/free-fallin-canopy-projection-shader
  - mods/realatmospheres/features/mie-phase-sentinel-dispatch
  - mods/realatmospheres/features/texture-and-phase-asset-scripts
lifecycle: [frame:render, render:atmosphere, frame:draw-ui, startup:starmap-before-main, frame:prepare]
game_types:
  - KSA.AtmosphereRenderer
  - KSA.Atmosphere.Rendering.ShaderStructs.AtmosphereData
  - KSA.PartTreeRenderData
  - KSA.MaterialData
  - KSA.DistanceReference
  - KSA.MieScatteringReference
  - KSA.Program
tags: [side-channel, uniform, state-flags, sentinel, material-extradata, stock-shaders, glsl]
---
A mod that patches a stock shader often needs to tell it something the shader has no input for: a clock, an on/off switch, a colour, a mode. Adding a uniform means changing descriptor layouts and
renderer code the mod does not own. This topic collects the four ways mods avoid that: write the value into a field the shader already receives and does not need, pack it into spare bits of a flag word,
mark the material the shader already reads, or choose behaviour from an out-of-range value of an existing parameter. Each ends in patched GLSL that decodes the value.

## Engine background

| Carrier | Where it lives | Free capacity |
|---|---|---|
| `AtmosphereData` fields (`OzoneExtent`, ring slots) | One struct per atmospheric body, filled by `AtmosphereRenderer.FillPlanetAtmosphereData` ([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1136-L1164)) | Ozone extent on bodies with no ozone; the ring normal, inner and outer radius on bodies without rings |
| `PerInstanceData.StateBitFlag` / `inStateFlags` | Cached per part by the private `PartTreeRenderData.WriteState` ([current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1210)) and `WriteDynamicState` (L1088); the game uses bits 0 to 10 ([current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L196-L219)) | Bits 11 to 31 |
| `MaterialData.ExtraData` (`float4`) | Per material, in the material buffer ([current/decomp/KSA/MaterialData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MaterialData.cs)); GLSL `Material.extraData` ([current/Content/Core/Shaders/Common/MaterialSet.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/MaterialSet.glsl#L12-L26)) | Whatever the material type does not read |
| Existing XML parameters | `MieScatteringReference.PhaseFunctionAsymmetry` ([current/decomp/KSA/MieScatteringReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MieScatteringReference.cs)) flows unmodified into the uniform ([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1143)) | Values outside the physical range |

Whatever the carrier, the same rules apply: patch the shader (see [Patch vehicle paint and canopy shaders in memory at compile time](../patch-game-shaders-in-memory-at-compile-time/overview.md) or [Replace or text-patch the game's GLSL without editing game files](../replace-or-patch-game-glsl-without-editing-game-files/overview.md)),
keep the carrier's stock meaning intact for everything else, and verify on each game update that the field is still unused.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Temporary value swap in a prefix, restore in a postfix](send-extra-data-to-stock-shaders-without-new-uniforms-1.md) | RealAtmospheres | `harmony-postfix` | `render:atmosphere`, `frame:render` | Per-body, per-call data with no layout change; needs reflection and fails soft |
| [Spare high bits of the part state flags](send-extra-data-to-stock-shaders-without-new-uniforms-2.md) | unscience, gatOS | `harmony-postfix` | `frame:render`, `frame:prepare` | 21 bits per instance, cached by the game; the writers are private |
| [Marker in a material's ExtraData](send-extra-data-to-stock-shaders-without-new-uniforms-3.md) | unscience (free-fallin) | `glsl-shader` | `frame:render` | Per material, no C# hook per frame; descriptor-set numbers in the patch are fragile |
| [Sentinel dispatch on an existing parameter](send-extra-data-to-stock-shaders-without-new-uniforms-4.md) | RealAtmospheres | `glsl-shader` | `startup:starmap-before-main`, `render:atmosphere` | Data in the XML itself, no C#; the value must stay out of the physical range |

## See also

- [Atmosphere uniform side channel](../../../../mods/realatmospheres/features/atmosphere-uniform-side-channel.md), [Per-part vehicle paint through free state-flag bits and an in-memory part shader patch](../../../../mods/unscience/features/humble-arteest-vehicle-paint-shader-patch.md), [Mie phase sentinel dispatch](../../../../mods/realatmospheres/features/mie-phase-sentinel-dispatch.md)
- [Patch vehicle paint and canopy shaders in memory at compile time](../patch-game-shaders-in-memory-at-compile-time/overview.md), [Branch per body inside shared game shaders](../branch-per-body-inside-shared-game-shaders/overview.md), [Recolour vessel parts and kittens with a patched part shader](../../../rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/overview.md), [Recreate mod render targets when KSA rebuilds its renderer](../../../rendering/vulkan-passes/handle-renderer-rebuilds/overview.md), [Compile-check a mod's GLSL without launching the game](../../glsl-workflow/check-mod-shaders-offline-with-glslang/overview.md)
