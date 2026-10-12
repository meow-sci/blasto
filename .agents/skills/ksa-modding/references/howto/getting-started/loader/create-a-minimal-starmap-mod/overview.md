---
type: Game Feature
title: Create a minimal StarMap mod
description: What KSA and StarMap require of a code mod folder, and two ways to stand one up - by hand from the discovery rules, or from a repo template that grows into a library plus entry layout
category: getting-started
group: loader
topic: create-a-minimal-starmap-mod
demonstrated_by:
  - mods/starmap/features/manifest-mod-discovery
  - mods/starmap/features/attribute-hook-discovery
  - mods/kitten-engineer-redux/features/dist-zip-and-post-build-deploy
  - mods/unscience/features/fixme-mod-name-standalone-mod-template
  - mods/unscience/features/lib-isubmod-contract
  - mods/unscience/features/unscience-supermod-host
lifecycle: [startup:starmap-before-main, startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Mod, KSA.ModLibrary, KSA.Program]
tags: [starmap, skeleton, mod-toml, csproj, getting-started, template]
---

A StarMap mod is a KSA content mod plus an entry assembly: a folder with a `mod.toml`, a DLL and a
class marked `[StarMapMod]`. KSA reads the `mod.toml` and loads any listed content; StarMap reads the
same file's optional `[StarMap]` table and loads the DLL. This topic is the shortest path from an
empty directory to a mod that logs from the running game, and then the layout most large mods grow
into.

## Engine background

| Type | File | What a mod author needs to know |
|---|---|---|
| `KSA.ModLibrary` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L234-L326) | `PrepareManifest` lists folders under `Content/` and `<Documents>/.../mods/`; unlisted folders become entries with `Enabled = false`, `New = true`. `PrepareAll` (L373) builds a `Mod` per enabled entry, `LoadAll` (L478) loads assets |
| `KSA.Mod` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L14-L111) | Deserialized from `mod.toml` with `[TomlField]` members; `Id` is the folder name and `DirectoryPath` the folder |
| `KSA.Program` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L943) | The constructor calls `ModLibrary.PrepareAll`, `PreloadAssetBundles`, `LoadAll` in this order; StarMap's hooks hang off these calls |
| StarMap discovery | [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L36-L63) | Defaults `EntryAssembly` to the manifest id when `[StarMap]` is absent, skips silently if `<EntryAssembly>.dll` is missing, takes the first `[StarMapMod]` class |

Hook timing, in the lifecycle vocabulary of [Game loop and mod lifecycle](../../lifecycle/game-loop-and-lifecycle/overview.md):

| Hook | Value | Fires |
|---|---|---|
| `StarMapBeforeMain` | `startup:starmap-before-main` | Directly after the mod's hooks are bound, before `Program.Main` |
| `StarMapImmediateLoad(KSA.Mod)` | `startup:immediate-load` | Prefix of `Mod.PrepareSystems`, once per enabled mod with code |
| `StarMapAllModsLoaded` | `startup:all-mods-loaded` | Postfix of `ModLibrary.LoadAll` |
| `StarMapBeforeGui` / `StarMapAfterGui` | `frame:draw-ui` | Every frame while the HUD is shown |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Hand-built project from the discovery rules](./create-a-minimal-starmap-mod-1.md) | StarMap rules, shown with Kitten-Engineer-Redux | `starmap-lifecycle`, one-line `mod.toml` | `startup:*` | Least to read and nothing to clone; you own the csproj and deploy plumbing. Best for a first mod or a single-purpose patch mod |
| [Scaffold with mkmod.ts, library hosted standalone or by a supermod](./create-a-minimal-starmap-mod-2.md) | unscience | `starmap-lifecycle` with an `ISubmod` contract | `startup:all-mods-loaded`, `frame:draw-ui`, `event:shutdown` | More structure (entry project, library, shared abstractions, Bun script) but features stay host-agnostic and can later join an aggregating mod |

## Common rules both approaches rely on

- The folder name is the manifest id, the default `EntryAssembly` and, without a `[StarMap]` table,
  the DLL file name.[^sm-runtimemod]
- A hook method must be public and have the exact parameter list; otherwise it is silently ignored.
  `StarMapImmediateLoad` needs a `KSA.Mod` parameter.
- New folders start disabled; the player enables them in KSA's dialog and StarMap offers a restart
  (see [Folder and manifest rules as StarMap consumes them](../how-starmap-launches-ksa/how-starmap-launches-ksa-2.md)).
- Game DLLs are compile-time references only (`Private=false`); see
  [Reference the game's assemblies from a mod project without shipping them](../../build/reference-game-assemblies-from-a-mod-project/overview.md).

## See also

- [Game loop and mod lifecycle](../../lifecycle/game-loop-and-lifecycle/overview.md) - frame order and where each hook sits
- [Write mod.toml - KSA keys and the [StarMap] table](../mod-toml-reference/overview.md), [How StarMap launches KSA and loads your mod](../how-starmap-launches-ksa/overview.md), [Choose and use the right StarMap lifecycle hook](../../lifecycle/choose-a-starmap-startup-hook/overview.md),
  [Deploy a mod on every build and debug it from an IDE](../../build/deploy-a-mod-on-every-build/overview.md)
- Not yet written: `isolate-patch-failures-and-fault-proof-patch-bodies` (patch-body fault containment); see
  [Build a KSA mod that references only StarMap.API and Harmony](../../build/build-a-mod-without-game-references/overview.md) for reference-free builds
- Mod feature pages: [Discover code mods through KSA's mod manifest](../../../../mods/starmap/features/manifest-mod-discovery.md),
  [Discover lifecycle hooks by attribute type name](../../../../mods/starmap/features/attribute-hook-discovery.md),
  [Minimal mod.toml, post-build deploy and release zip](../../../../mods/kitten-engineer-redux/features/dist-zip-and-post-build-deploy.md),
  [Minimal standalone StarMap mod template with a separate feature library](../../../../mods/unscience/features/fixme-mod-name-standalone-mod-template.md),
  [ISubmod contract for features that run standalone or inside a supermod](../../../../mods/unscience/features/lib-isubmod-contract.md), [Supermod host that aggregates 31 feature panels into one window](../../../../mods/unscience/features/unscience-supermod-host.md)
- [Isolating patch failures and keeping patch bodies from throwing](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md) - related topic

[^sm-runtimemod]: StarMap: StarMap.Core/ModRepository/RuntimeMod.cs
