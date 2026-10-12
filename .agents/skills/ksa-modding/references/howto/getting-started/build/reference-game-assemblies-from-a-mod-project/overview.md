---
type: Game Feature
title: Reference the game's assemblies from a mod project without shipping them
description: How a mod project compiles against the installed KSA DLLs with Private=false, and three ways to locate them - a KSADir property, tiered discovery and a shared props file
category: getting-started
group: build
topic: reference-game-assemblies-from-a-mod-project
demonstrated_by:
  - mods/ksa-aircrafthud/features/props-based-build
  - mods/ksaero/features/mod-deploy-targets
  - mods/ksagolf/features/content-and-code-mod-layout
  - mods/kittenextensions/features/msbuild-mod-toml-generation
lifecycle: []
game_types: []
tags: [msbuild, references, private-false, props, ksadir]
---

KSA has no SDK package: a mod compiles against the DLLs in the game folder (`KSA.dll`, `Brutal.*`, `Planet.*`, `Tomlet`,
`BepuPhysics`, `BepuUtilities` and friends). Those files are copyrighted and already loaded by the game, so every
project references them with `Private=false`: the compiler sees the real API, the build output never contains them, and
StarMap's load context would return the game's loaded copy first anyway.[StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L47)
The approaches differ in how the folder is found.

## Engine background

No game type is involved. The relevant engine fact is load-context precedence: an assembly name already loaded in
`Default` or the game context beats a copy in the mod folder. `StarMap.API.dll` and `0Harmony.dll` are not game files;
projects take them from NuGet (`StarMap.API` 0.3.6, `Lib.Harmony` 2.4.2) or from a StarMap folder, also non-private.
Build-time only; no lifecycle value applies ([Game loop and mod lifecycle](../../lifecycle/game-loop-and-lifecycle/overview.md) for run-time behavior).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [KsaDir property with wildcard Reference items and HintPath](./reference-game-assemblies-from-a-mod-project-1.md) | KSA_AircraftHUD, KSAero | `msbuild` | build | Smallest setup; hard-coded or defaulted path per machine |
| [Tiered KsaDllDir resolution in Directory.Build.props](./reference-game-assemblies-from-a-mod-project-2.md) | KSAGolf | `msbuild` | build | Zero configuration for contributors and CI; more MSBuild to maintain |
| [Shared KSADLLs.props glob for several mods](./reference-game-assemblies-from-a-mod-project-3.md) | KittenExtensions, ShaderExtensions, MPFX | `msbuild`, `github-actions` | build | One tiny reusable file; each repo carries a copy; paths come from user files or the command line |

## Rules

- `Private=false` on every game and loader assembly; verify the output folder contains none.
- Never commit game DLLs; resolve a folder instead.
- Fail with one readable error when the folder is wrong ([Tiered KsaDllDir resolution in Directory.Build.props](./reference-game-assemblies-from-a-mod-project-2.md) shows how).
- Builds without any game reference are an alternative: [Build a KSA mod that references only StarMap.API and Harmony](../build-a-mod-without-game-references/overview.md).

## See also

- [Deploy a mod on every build and debug it from an IDE](../deploy-a-mod-on-every-build/overview.md), [Build a mod in CI when the game DLLs are private](../build-in-ci-without-the-game-installed/overview.md), [Ship third-party DLLs next to your mod](../../dependencies/ship-third-party-dlls-with-a-mod/overview.md),
  [Create a minimal StarMap mod](../../loader/create-a-minimal-starmap-mod/overview.md)
- [Props-based build straight into the mods folder](../../../../mods/ksa-aircrafthud/features/props-based-build.md), [Build and deploy targets](../../../../mods/ksaero/features/mod-deploy-targets.md),
  [Content-plus-code mod layout](../../../../mods/ksagolf/features/content-and-code-mod-layout.md), [MSBuild-generated mod.toml and deploy](../../../../mods/kittenextensions/features/msbuild-mod-toml-generation.md)
