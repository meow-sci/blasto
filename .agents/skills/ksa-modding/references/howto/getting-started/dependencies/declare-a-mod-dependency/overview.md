---
type: Game Feature
title: Declare a dependency on another StarMap mod, required or optional
description: How StarMap orders and parks mods by their ModDependencies, what Optional and ImportedAssemblies change, and how to integrate with or stand down from a mod you do not declare
category: getting-started
group: dependencies
topic: declare-a-mod-dependency
demonstrated_by:
  - mods/starmap/features/mod-dependency-graph
  - mods/ksarmory/features/armoury-pack-registration-api
  - mods/shaderextensions/features/generated-mod-toml
  - mods/gatos/features/purrtty-custom-shell-contract
  - mods/purrtty/features/custom-shell-contract-via-assembly-imports
  - mods/ksa-advancedflightcomputer/features/optional-kittenextensions-patches
  - mods/ksa-advancedflightcomputer/features/superseded-mod-detection-and-settings-import
lifecycle: [startup:starmap-before-main, startup:all-mods-loaded, startup:immediate-load]
game_types: [KSA.ModLibrary]
tags: [mod-dependency, mod-toml, imported-assemblies, optional, load-order, starmap]
---

KSA has no mod dependencies; StarMap adds them. A `[[StarMap.ModDependencies]]` entry orders initialization, parks a
mod until the dependency is up, and controls which of the dependency's assemblies the dependent's load context may
import. This topic covers declaring a required dependency, declaring an optional one with a vendored fallback, and the
no-declaration alternative: probing for a mod at `StarMapAllModsLoaded`.

## Engine background

| Piece | Where | Behavior |
|---|---|---|
| Registry and waiting graph | [StarMap.Core/ModRepository/ModRegistry.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModRegistry.cs#L9-L32) | `_mods` (every mod whose DLL loaded), `WaitingMods`, `WaitingModsDependencyGraph` |
| `AllDependenciesLoaded` | [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L78-L107) | Registered dependencies get an import set, missing ones park the mod |
| `CalculateUseableAssemblies` | [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L109-L126) | Default: the dependency's `EntryAssembly` if neither side lists anything |
| `CheckForDependentMods` | [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L164-L182) | Releases waiters; copies `ImportedAssemblies` verbatim (empty by default) |
| `TryLoadWaitingMods` | [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L106-L131) | After the pass, initializes parked mods whose missing dependencies are all `Optional` |
| `ModLibrary.PrepareAll` / `LoadAll` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L522) | Why immediate-load registrations are visible to every `AllModsLoaded` hook |

Everything resolves in `startup:starmap-before-main`; consequences land in `startup:immediate-load` and
`startup:all-mods-loaded` ([Game loop and mod lifecycle](../../lifecycle/game-loop-and-lifecycle/overview.md)).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Required dependency with ImportedAssemblies](./declare-a-mod-dependency-1.md) | KSArmory, ShaderExtensions | `toml-config` | `startup:starmap-before-main` | Guaranteed ordering and shared types; the mod does not load without the dependency |
| [Optional = true with vendored fallback copies of the contract](./declare-a-mod-dependency-2.md) | gatOS, purrTTY | `inter-mod-api` | `startup:all-mods-loaded` | Works with or without the other mod; needs vendored DLLs and own-copy detection |
| [Late-bound type lookup, immediate-load marker and copy-once settings import](./declare-a-mod-dependency-3.md) | KSA-AdvancedFlightComputer | `reflection` | `startup:immediate-load`, `startup:all-mods-loaded` | No declaration needed and covers conflicting mods; depends on the other mod's names staying stable |

## Rules of thumb

- `ModId` is the dependency's **folder name**; list `ImportedAssemblies` explicitly.
- List dependencies above dependents in `manifest.toml`; avoid chains deeper than one level.
- A content-only dependency (no DLL) can never be satisfied.
- Detection by probing sees only enabled mods (a disabled mod's DLL is never loaded).

## See also

- [Share types and design an inter-mod API between StarMap mods](../share-types-between-mods/overview.md), [Write mod.toml - KSA keys and the [StarMap] table](../../loader/mod-toml-reference/overview.md), [Generate mod.toml and StarMap dependencies from the project file](../../build/generate-mod-toml-from-msbuild/overview.md),
  [Choose and use the right StarMap lifecycle hook](../../lifecycle/choose-a-starmap-startup-hook/overview.md), [How StarMap launches KSA and loads your mod](../../loader/how-starmap-launches-ksa/overview.md)
- [Order mod loading with a ModDependencies graph](../../../../mods/starmap/features/mod-dependency-graph.md), [A three-member, text-only registration API for code packs, with a generated contract record](../../../../mods/ksarmory/features/armoury-pack-registration-api.md),
  [mod.toml generated by MSBuild with a StarMap dependency](../../../../mods/shaderextensions/features/generated-mod-toml.md), [purrTTY custom shell contract](../../../../mods/gatos/features/purrtty-custom-shell-contract.md),
  [Inter-mod shell contract shared through StarMap assembly imports](../../../../mods/purrtty/features/custom-shell-contract-via-assembly-imports.md),
  [Optional KittenExtensions XML patches from mod.toml](../../../../mods/ksa-advancedflightcomputer/features/optional-kittenextensions-patches.md),
  [Stand down for a superseded standalone mod and import its settings](../../../../mods/ksa-advancedflightcomputer/features/superseded-mod-detection-and-settings-import.md)
