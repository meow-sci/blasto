---
type: Game Feature
title: Share types and design an inter-mod API between StarMap mods
description: Why mods that load their own copy of a type cannot talk to each other, and five ways to share identity or avoid needing it - contract imports, API design, duck typing, name-based binding and stable fork identity
category: getting-started
group: dependencies
topic: share-types-between-mods
demonstrated_by:
  - mods/purrtty/features/custom-shell-contract-via-assembly-imports
  - mods/starmap/features/shared-contract-assemblies
  - mods/gatos/features/purrtty-custom-shell-contract
  - mods/ksarmory/features/armoury-pack-registration-api
  - mods/purrtty/features/custom-shell-registry-and-lifecycle
  - mods/shaderextensions/features/duck-typed-attribute-api
  - mods/mpfx/features/duck-typed-buffer-structs
  - mods/ksa-aircrafthud/features/sx-push-constants-and-buffers
  - mods/starmap/features/attribute-hook-discovery
  - mods/kittenextensions/features/continued-fork-identity
lifecycle: [startup:starmap-before-main, startup:all-mods-loaded]
game_types: [KSA.Program, KSA.KeyHash]
tags: [inter-mod-api, assembly-load-context, type-identity, contract-assembly, duck-typing]
---

StarMap loads every mod into its own `AssemblyLoadContext`. Two mods that each ship `Contract.dll` therefore get two
different assemblies with two different `Type`s and separate static fields: a cast fails, a registry is empty. This
topic collects the ways the corpus deals with that. They differ in whether they share the assembly (contract
import), avoid sharing (duck typing, name-based binding), or keep the shared identity stable across a fork.

## Engine background

| Piece | Behavior | Source |
|---|---|---|
| Per-mod context | `Load` resolves names from `Default`, the game context, then dependency contexts whose import set contains the name, then the mod's own `deps.json` | [StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L72) |
| Import set | `CalculateUseableAssemblies`: the dependency's `EntryAssembly` by default, else the importer's `ImportedAssemblies` | [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L109-L126) |
| `ExportedAssemblies` | parsed into `StarMapConfig` but never copied to `RuntimeMod.ExportedAssemblies` at StarMap 4dc60f8, so it has no effect | [StarMap.Core/Config/StarMapConfig.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Config/StarMapConfig.cs#L7-L25) |
| Scan window | A Harmony prefix on `Program.Main` runs after every mod is loaded and before the constructor parses XML | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1693-L1703) |

All import sets are fixed at `startup:starmap-before-main`; registrations happen at `startup:all-mods-loaded`
([Game loop and mod lifecycle](../../lifecycle/game-loop-and-lifecycle/overview.md)).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Importer lists the contract assembly in ImportedAssemblies](./share-types-between-mods-1.md) | purrTTY, gatOS | `inter-mod-api` | `startup:all-mods-loaded` | One shared `Assembly`, real types and statics; requires a dependency and a game-free contract; only the importer's list works today |
| [Text-only registration members with fault results and a CI-checked API surface](./share-types-between-mods-2.md) | KSArmory | `inter-mod-api` | `startup:starmap-before-main`, `startup:all-mods-loaded` | Survives refactors and needs no game DLLs in the pack; the surface must be guarded by a generated record |
| [Duck-typed attributes copied into the consumer](./share-types-between-mods-3.md) | ShaderExtensions, MPFX | `reflection` | `startup:starmap-before-main` | No reference and no type identity issue; coupled by names, one-shot scan |
| [StarMap's name-based attribute binding](./share-types-between-mods-4.md) | StarMap | `reflection` | `startup:starmap-before-main` | The host side of a name-based plugin contract; silent skips |
| [New mod id, unchanged assembly identity](./share-types-between-mods-5.md) | KittenExtensions | `toml-config` | `startup:starmap-before-main` | Dependents keep working after a fork; both forks cannot coexist |

## Choosing

- You own a registry other mods fill: approach 1 plus the surface discipline of approach 2.
- You want zero coupling to a host DLL: approach 3.
- You are building a plugin host: approach 4.
- You are continuing someone else's library: approach 5.

## See also

- [Declare a dependency on another StarMap mod, required or optional](../declare-a-mod-dependency/overview.md), [Write mod.toml - KSA keys and the [StarMap] table](../../loader/mod-toml-reference/overview.md), [How StarMap launches KSA and loads your mod](../../loader/how-starmap-launches-ksa/overview.md),
  [Ship third-party DLLs next to your mod](../ship-third-party-dlls-with-a-mod/overview.md)
- Not yet written: `host-a-terminal-with-pty-sessions-and-frame-handoff`, `feed-uniform-buffers-to-a-shaderextensions-shader`
- [Inter-mod shell contract shared through StarMap assembly imports](../../../../mods/purrtty/features/custom-shell-contract-via-assembly-imports.md), [Share contract assemblies between mods with ExportedAssemblies and ImportedAssemblies](../../../../mods/starmap/features/shared-contract-assemblies.md),
  [purrTTY custom shell contract](../../../../mods/gatos/features/purrtty-custom-shell-contract.md), [A three-member, text-only registration API for code packs, with a generated contract record](../../../../mods/ksarmory/features/armoury-pack-registration-api.md),
  [Custom shell registry, lifecycle and PTY bridge](../../../../mods/purrtty/features/custom-shell-registry-and-lifecycle.md), [Attribute duck typing as an inter-mod API](../../../../mods/shaderextensions/features/duck-typed-attribute-api.md),
  [Uniform buffer and push constant structs bound by attribute name](../../../../mods/mpfx/features/duck-typed-buffer-structs.md), [ShaderExtensions push constants and uniform buffers from C#](../../../../mods/ksa-aircrafthud/features/sx-push-constants-and-buffers.md),
  [Discover lifecycle hooks by attribute type name](../../../../mods/starmap/features/attribute-hook-discovery.md), [Continued fork with a new mod id and the old assembly name](../../../../mods/kittenextensions/features/continued-fork-identity.md)
- [Choose and use the right StarMap lifecycle hook](../../lifecycle/choose-a-starmap-startup-hook/overview.md) - related topic
- [Host a terminal emulator in the game with pty sessions, custom shells and thread-to-frame handoff](../../../external-io/terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/overview.md) - related topic
- [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../../../shaders/post-processing/feed-uniform-buffers-to-a-shaderextensions-shader/overview.md) - related topic

