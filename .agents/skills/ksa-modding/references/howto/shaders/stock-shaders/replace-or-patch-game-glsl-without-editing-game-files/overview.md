---
type: Game Feature
title: Replace or text-patch the game's GLSL without editing game files
description: How KSA compiles its own shaders from the Content/Core/Shaders tree and the one seam, ShaderModuleUtils.FromFile, that lets a mod compile patched copies instead
category: shaders
group: stock-shaders
topic: replace-or-patch-game-glsl-without-editing-game-files
demonstrated_by:
  - mods/realatmospheres/features/shader-file-redirect
  - mods/realatmospheres/features/shadow-tree-anchor-patching
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types:
  - RenderCore.ShaderModuleUtils
  - KSA.ShaderReference
  - RenderCore.Shaders.ShaderCompilerResolve
  - KSA.AssetReloader.ShaderReloader
  - KSA.ModLibrary
  - KSA.AtmosphereRenderer
tags: [glsl, shader-patching, redirect, shadow-tree, harmony-prefix, stock-shaders, realatmospheres]
---
KSA's own shaders are plain GLSL files under `Content/Core/Shaders` that the game compiles with shaderc at startup. A mod that wants to change
atmosphere scattering, clouds or terrain lighting cannot ship a replacement in its own folder (the stock `<Shader>` entries point at the
install), and editing the install is not an option. The approach this topic documents: copy the stock tree into the mod folder at launch,
patch the copy as text, and redirect every compile whose path points into the stock tree to the copy.

## Engine background

- **Where stock shaders come from.** `Core/DefaultAssets.xml` lists them as `<Shader>` entries, for example `AtmosphereCompute` is
  `Shaders/Atmosphere/Atmosphere.comp` ([current/Content/Core/DefaultAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/DefaultAssets.xml#L235)). Each `FileReference` registers itself as a loader
  when its bundle loads outside preload ([current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L39-L55)), and `ModLibrary.LoadAll` runs the loaders with
  `Parallel.ForEachAsync` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L478-L522)), so stock shaders compile on thread-pool threads.
- **One compile seam.** `ShaderReference.Compile` and `CompileVariantWithCustomOptions` both end in
  `ShaderModuleUtils.FromFile(device, ModPath, out Stage, options)` ([current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L85-L138)). `FromFile` reads
  the file, compiles it with shaderc and takes the stage from the extension
  ([current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L117-L159)). A two-parameter overload forwards to it. Some renderers compile from a hard-coded
  path on the main thread through the same method (the CACAO ambient-occlusion passes).
- **Includes.** shaderc resolves every `#include` relative to the directory of the requesting file
  ([current/decomp/Brutal.ShaderCApi/ShaderC.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ShaderCApi/ShaderC.cs#L247-L255)), so a copied tree keeps its internal includes working.
- **Reload bookkeeping.** `ShaderReference.OnIncludeResolved` maps each include through `ShaderReloader.GetModPath`, which takes the substring from the first
  `"Content"` in the path ([current/decomp/KSA.AssetReloader/ShaderReloader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.AssetReloader/ShaderReloader.cs#L54-L58)); a copy outside a folder whose name contains `Content`
  breaks the dirty-file tracking.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Shadow tree plus a FromFile path-rewrite prefix, with anchored text patches](replace-or-patch-game-glsl-without-editing-game-files.md) | RealAtmospheres | `harmony-prefix` | `startup:starmap-before-main`, `startup:asset-load` | One prefix covers every stock shader; patches are exact-count and idempotent so a game update skips instead of corrupting; the whole tree is copied each launch |

For changes that need no files on disk see [Patch vehicle paint and canopy shaders in memory at compile time](../patch-game-shaders-in-memory-at-compile-time/overview.md).

## See also

- [Shader file redirect into a patched shadow tree](../../../../mods/realatmospheres/features/shader-file-redirect.md), [Anchor-verified text patching of the shadow tree](../../../../mods/realatmospheres/features/shadow-tree-anchor-patching.md), [Shared shadow for two cooperating mods](../../../../mods/realatmospheres/features/cross-mod-shared-shadow.md)
- [Patch vehicle paint and canopy shaders in memory at compile time](../patch-game-shaders-in-memory-at-compile-time/overview.md), [Patch Core or another mod's XML data without copying files](../../../assets/xml-patching/patch-game-xml-data/overview.md), [Smuggle data into stock shaders through unused uniforms, state bits and sentinel values](../send-extra-data-to-stock-shaders-without-new-uniforms/overview.md), [Compile-check a mod's GLSL without launching the game](../../glsl-workflow/check-mod-shaders-offline-with-glslang/overview.md)
