---
type: Game Feature
title: Include KSA's own GLSL library from a mod shader
description: How KSA resolves shader includes and why a mod needs a generated header with absolute paths to reuse Global.glsl, AtmosphereLuts.glsl and TextureSet.glsl
category: shaders
group: glsl-workflow
topic: include-the-game-shader-library-in-mod-glsl
demonstrated_by:
  - mods/ksarmory/features/core-shader-library-include
  - mods/ksastructures/features/core-shader-library-include
  - mods/realatmospheres/features/cloud-pass-shading-and-ring-shadows
lifecycle: [startup:immediate-load, startup:asset-load]
game_types:
  - KSA.ShaderReference
  - KSA.AssetReloader.ShaderReloader
  - RenderCore.Shaders.ShaderCompilerResolve
  - Brutal.ShaderCApi.ShaderC
  - KSA.Mod
  - KSA.ModLibrary
tags: [glsl, include, shaderc, global-glsl, atmosphere-luts, texture-set, hot-reload, immediate-load]
---
A mod compute shader that wants the game's camera and lighting block, its bindless texture array or the atmosphere LUT lookups should include the game's own headers instead of copying them. The game's include resolver has no search path, a mod's shaders live in a different
folder tree from `Content/Core/Shaders`, and the relative path between them differs per player. The solution two mods use: write a small header with absolute include paths at load, before any shader compiles, and include it by a fixed relative name.

## Engine background

- **Resolution rule.** KSA compiles shaders with shaderc and its own include callback `ShaderCompilerResolve.Resolve`, which calls `ShaderC.Callbacks.Resolve` and then reports the resolved path ([current/decomp/RenderCore.Shaders/ShaderCompilerResolve.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Shaders/ShaderCompilerResolve.cs#L16-L36)).
  The resolver builds the path as `Utf8.Path.Combine(GetDirectoryName(requestingSource), source)` ([current/decomp/Brutal.ShaderCApi/ShaderC.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ShaderCApi/ShaderC.cs#L247-L255)); `Utf8.Path.Combine` is `System.IO.Path.Combine` underneath ([current/decomp/Brutal.Strings/Utf8.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Strings/Utf8.cs#L260-L263)),
  which returns the second argument unchanged when it is rooted. So an absolute `#include` is used as-is and a relative one is always relative to the file that asked.
- **Reload bookkeeping.** After every include resolves, `ShaderReference.OnIncludeResolved` maps the path through `ShaderReloader.GetModPath`, which takes the substring from the first `"Content"` ([current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L140-L166), [current/decomp/KSA.AssetReloader/ShaderReloader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.AssetReloader/ShaderReloader.cs#L54-L58)).
  A path with no `Content` makes that `Substring(-1)` throw; the catch logs an error naming the shader on every compile (the shader still compiles). So a generated header must live in a folder called `Content`.
- **When shaders compile.** `ModLibrary.PrepareAll` calls `Mod.PrepareSystems` per mod ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L446)), which is where StarMap fires `[StarMapImmediateLoad]`. `ModLibrary.LoadAll` later runs every registered loader, including the mod's `ShaderReference`s,
  in `Parallel.ForEachAsync` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L507-L519)). Immediate load is the last mod hook before the compile; `[StarMapAllModsLoaded]` is a postfix on `LoadAll` and is too late.
- **What the headers provide.** `Common/Global.glsl` declares `SET_GLOBAL` 0 and the `Global` and `GlobalConstants` blocks; `Atmosphere/AtmosphereLuts.glsl` the LUT lookup functions; `Common/TextureSet.glsl` the bindless `globalTextures[]` at `SET_TEXTURE` (it hits an `#error` if `SET_TEXTURE` is not defined first).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Generated Shaders/Content/CoreAtmosphere.glsl](include-the-game-shader-library-in-mod-glsl-1.md) | KSArmory | `starmap-lifecycle` | `startup:immediate-load`, `startup:asset-load` | The full recipe with the offline check script; no fallback if the header cannot be written |
| [Generated KSAStructuresCore.glsl under a folder called Content](include-the-game-shader-library-in-mod-glsl-2.md) | KSAStructures | `starmap-lifecycle` | `startup:immediate-load`, `startup:asset-load` | Same trick, with the reasons for the `Content` folder, the forward slashes and the asset XML spelled out |
| [Relative include of a stock helper inside a patched copy](include-the-game-shader-library-in-mod-glsl-3.md) | RealAtmospheres | `glsl-shader` | `startup:starmap-before-main`, `startup:asset-load` | No generated file: a plain relative include works because the whole stock tree is copied |

## See also

- [A generated include that reaches KSA's own shader library from a mod shader](../../../../mods/ksarmory/features/core-shader-library-include.md), [Core shader library include](../../../../mods/ksastructures/features/core-shader-library-include.md)
- [Compile and write GLSL at runtime for a mod's own pipeline](../compile-glsl-at-runtime/overview.md), [Compile-check a mod's GLSL without launching the game](../check-mod-shaders-offline-with-glslang/overview.md), [Recompile a mod's shader while the game runs](../reload-a-mod-shader-in-a-running-game/overview.md), [Build a Vulkan pipeline compatible with KSA's scene render target](../../../rendering/vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/overview.md)
