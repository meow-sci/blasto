---
type: Game Feature
title: Patch vehicle paint and canopy shaders in memory at compile time
description: How a Harmony prefix on ShaderModuleUtils.FromFile and Utils.SetShaderFromMod can compile transformed GLSL source instead of the file on disk, and how to make the game recompile
category: shaders
group: stock-shaders
topic: patch-game-shaders-in-memory-at-compile-time
demonstrated_by:
  - mods/unscience/features/humble-arteest-vehicle-paint-shader-patch
  - mods/unscience/features/free-fallin-canopy-projection-shader
  - mods/gatos/features/vehicle-and-eva-paint
  - mods/realatmospheres/features/atmosphere-uniform-side-channel
lifecycle: [startup:all-mods-loaded, frame:prepare, frame:render, render:atmosphere]
game_types:
  - RenderCore.ShaderModuleUtils
  - KSA.ShaderReference
  - KSA.Rendering.Utils
  - KSA.PartTreeRenderData
  - RenderCore.Pipelines.SimplePipeline
  - KSA.Program
  - KSA.AtmosphereRenderer
tags: [glsl, shader-patching, harmony-prefix, in-memory, fromstring, renderer-rebuild, stock-shaders]
---
The text-patched shadow tree of [Replace or text-patch the game's GLSL without editing game files](../replace-or-patch-game-glsl-without-editing-game-files/overview.md) copies shaders to disk. A smaller mod can skip the files entirely: a Harmony prefix on the compile
call reads the stock source, transforms it as a string and hands the result to the compiler. This topic covers the three mods that do it for part and canopy shaders,
what to match on, and how to make the game recompile after a patch is enabled or retuned.

## Engine background

- **The seam.** `ShaderReference.Compile` and `CompileVariantWithCustomOptions` call `ShaderModuleUtils.FromFile(device, ModPath, out Stage, options)`
  ([current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L85-L137)). The same class has `FromString(device, source, stage, options, debugName)` for compiling an in-memory
  source ([current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L117-L159) shows `FromFile`'s file read and compile).
- **Which pipelines reach `FromFile`.** Part model pipelines compile `MeshIndirectFrag` per variant through `CompileVariantWithCustomOptions` (macro defines such as
  `ENABLE_EMISSIVE`). Model pipelines that pass `useCustomOptions == false` to `Utils.SetShaderFromMod` bind the cached module and never call `FromFile`
  ([current/decomp/KSA.Rendering/Utils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/Utils.cs#L636-L652)).
- **Deferred rebuilds.** `Program.RendererRebuildNeeded` (a public static bool, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L430)) is the game's own flag for recompiling shader variants; it is
  consumed at the top of `PrepareFrame` (L2143), by `Render` (which then skips recording the frame, L2445) or by `PostRender` (L2351), each calling `RebuildRenderer`, which waits
  for the device to go idle before destroying pipelines ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5105)).
- **Part state flags.** Bits 0 to 10 of the per-instance `StateBitFlag` are used by the game; the whole 32-bit word is forwarded to the fragment shader as `inStateFlags`
  ([current/Content/Core/Shaders/Mesh/MeshIndirect.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/MeshIndirect.frag#L31)).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [MeshIndirect.frag patch for vehicle paint](patch-game-shaders-in-memory-at-compile-time-1.md) | unscience (humble-arteest) | `harmony-prefix` | `startup:all-mods-loaded`, `frame:prepare`, `frame:render` | The pure recipe: prefix on `FromFile`, anchor line, `FromString`, rebuild on enable |
| [Model.vert, Model_Skinned.vert and ModelPbr.frag for canopy projection](patch-game-shaders-in-memory-at-compile-time-2.md) | unscience (free-fallin) | `harmony-prefix` | `startup:all-mods-loaded`, `frame:render` | Needs a second prefix on `SetShaderFromMod` to force the compile path; descriptor-set numbers hard-coded in the patch are a risk |
| [Compile-time transform with runtime masters](patch-game-shaders-in-memory-at-compile-time-3.md) | gatOS | `glsl-shader` | `frame:viewports`, `frame:prepare`, `frame:render` | Reversible install, conflict check against other `FromFile` prefixes, rebuild wired to the setting change |

## See also

- [Per-part vehicle paint through free state-flag bits and an in-memory part shader patch](../../../../mods/unscience/features/humble-arteest-vehicle-paint-shader-patch.md), [Material-gated full-canopy projection by patching KSA's model shaders in memory](../../../../mods/unscience/features/free-fallin-canopy-projection-shader.md), [Vehicle and EVA paint](../../../../mods/gatos/features/vehicle-and-eva-paint.md)
- [Replace or text-patch the game's GLSL without editing game files](../replace-or-patch-game-glsl-without-editing-game-files/overview.md), [Smuggle data into stock shaders through unused uniforms, state bits and sentinel values](../send-extra-data-to-stock-shaders-without-new-uniforms/overview.md), [Recolour vessel parts and kittens with a patched part shader](../../../rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/overview.md), [Recreate mod render targets when KSA rebuilds its renderer](../../../rendering/vulkan-passes/handle-renderer-rebuilds/overview.md)
