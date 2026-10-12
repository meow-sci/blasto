---
type: Game Feature
title: Ship third-party DLLs next to your mod
description: Which assemblies belong in a StarMap mod folder, which never do, and how the per-mod load context finds the ones that do
category: getting-started
group: dependencies
topic: ship-third-party-dlls-with-a-mod
demonstrated_by:
  - mods/kitten-engineer-redux/features/dist-zip-and-post-build-deploy
  - mods/ksa-aircrafthud/features/modmenu-entry-vendored-attributes
  - mods/starmap/features/per-mod-assembly-load-context
  - mods/unscience/features/unscience-build-and-release
lifecycle: [startup:starmap-before-main]
game_types: []
tags: [third-party-dll, nuget, deps-json, assembly-load-context, packaging]
---

A mod folder is not a free-for-all: StarMap's per-mod load context decides, for every assembly the mod references,
whether the mod's own copy is used at all. Anything the game, StarMap or Harmony already provides wins, so shipping
those is pointless; anything else must be in the folder and, when a `deps.json` exists, listed in it.

## Engine background

`ModAssemblyLoadContext.Load` runs five steps ([StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L72)):

| Step | Source | Typical content |
|---|---|---|
| 1 | `AssemblyLoadContext.Default`, by simple name (case-insensitive) | `0Harmony` (loaded there by the launcher, [StarMap/Program.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/Program.cs#L9-L29)), `StarMap.Types`, framework |
| 2 | Assemblies already in the game context | `KSA`, `Brutal.*`, `StarMap.API`, `StarMap.Core` |
| 3 | The game context's resolver (`LoadFromAssemblyName`) | Game libraries not loaded yet |
| 4 | Contexts of dependency mods whose import set has the name | Shared contracts ([Share types and design an inter-mod API between StarMap mods](../share-types-between-mods/overview.md)) |
| 5 | The mod's `AssemblyDependencyResolver` on the entry DLL | Private dependencies beside the mod, via `deps.json` |

Timing: the entry assembly loads at `startup:starmap-before-main`; the rest resolve lazily. Native libraries never
go through the mod's `deps.json` (`LoadUnmanagedDll` is not overridden); only the runtime's default `DllImport`
probing of the calling assembly's folder applies, see [Ship and load native libraries from a mod folder](../ship-native-libraries-with-a-mod/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Copy the DLL beside the mod and let AssemblyDependencyResolver find it](./ship-third-party-dlls-with-a-mod-1.md) | Kitten-Engineer-Redux, KSA_AircraftHUD | `assembly-load-context` | `startup:starmap-before-main` | Trivial for one or two vendored DLLs; no `mod.toml` dependency; versions drift unless a package pins them |
| [Package NuGet dependencies with deps.json](./ship-third-party-dlls-with-a-mod-2.md) | StarMap, unscience | `assembly-load-context` | `startup:starmap-before-main` | Scales to many libraries with a verified closure; needs the deploy target and a check script |

## What to ship, what not

| Ship | Never ship |
|---|---|
| Your entry DLL, `mod.toml`, content | `0Harmony.dll`, `StarMap.API.dll` |
| Private NuGet DLLs (Tomlyn), vendored attribute DLLs (`ModMenu.Attributes.dll`) | `KSA.dll`, `Brutal.*.dll`, `Planet*.dll` and the rest of the game |
| `<Entry>.deps.json` when you have private dependencies | Debug leftovers from `bin` |
| Licence texts for third-party binaries | Native libraries in a `runtimes/<rid>/native/` layout, or under names the default probing cannot derive, unless the resolver of [Ship and load native libraries from a mod folder](../ship-native-libraries-with-a-mod/overview.md) is in place |

## See also

- [Share types and design an inter-mod API between StarMap mods](../share-types-between-mods/overview.md), [Package and publish a mod release (zip, CI publishing, installer)](../../build/package-and-publish-a-mod-release/overview.md), [How StarMap launches KSA and loads your mod](../../loader/how-starmap-launches-ksa/overview.md),
  [Reference the game's assemblies from a mod project without shipping them](../../build/reference-game-assemblies-from-a-mod-project/overview.md)
- [Minimal mod.toml, post-build deploy and release zip](../../../../mods/kitten-engineer-redux/features/dist-zip-and-post-build-deploy.md), [ModMenu entry with a vendored attributes DLL](../../../../mods/ksa-aircrafthud/features/modmenu-entry-vendored-attributes.md),
  [Isolate each mod in its own AssemblyLoadContext](../../../../mods/starmap/features/per-mod-assembly-load-context.md), [One shipped mod folder from thirty-plus projects, with dist validation and rolling GitHub releases](../../../../mods/unscience/features/unscience-build-and-release.md)
