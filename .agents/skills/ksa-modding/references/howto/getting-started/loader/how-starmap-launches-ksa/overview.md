---
type: Game Feature
title: How StarMap launches KSA and loads your mod
description: End-to-end reference for StarMap.exe - config, load contexts, working-directory trick, manifest discovery, dependency ordering and hook binding - with the instance-path and lazy-assembly sub-topics
category: getting-started
group: loader
topic: how-starmap-launches-ksa
demonstrated_by:
  - mods/starmap/features/host-process-launch
  - mods/starmap/features/manifest-mod-discovery
  - mods/starmap/features/per-mod-assembly-load-context
  - mods/starmap/features/mod-dependency-graph
  - mods/starmap/features/attribute-hook-discovery
  - mods/starmap/features/new-mods-restart-flow
  - mods/ksagolf/features/content-and-code-mod-layout
  - mods/starmap/features/instance-path-override
  - mods/realatmospheres/features/shader-file-redirect
lifecycle: [startup:starmap-before-main, startup:immediate-load, startup:all-mods-loaded, startup:asset-load]
game_types: [KSA.Program, KSA.App, KSA.ModLibrary, KSA.ModManifest, KSA.ModEntry, KSA.Mod, KSA.Constants]
tags: [starmap, launcher, assembly-load-context, manifest, instance, boot-sequence]
---

KSA has no mod-loading hook of its own beyond reading `manifest.toml` and each `mod.toml`. StarMap fills the
gap by being the host: the player runs `StarMap.exe`, which loads the game as a library, runs mods' startup code,
installs Harmony patches that dispatch the lifecycle attributes, and then calls the game's `Main`. This topic
explains that sequence so a modder can predict what is loaded, in which context and when.

## Engine background

| Type | File | Why it matters |
|---|---|---|
| `KSA.Program.Main` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1693-L1737) | Resets the working directory from `BaseDirectory`; the `CliArgs` table (L102) recognizes only `-build-info`, `-fixed-viewport`, `-force-x11` |
| `KSA.Program` constructor | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L689-L1327) | The startup sequence: logging, window, renderer, ImGui, `ModLibrary.PrepareAll` (L899), `LoadAll` (L942) |
| `KSA.App.Run` | [current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L39-L74) | The frame loop; returning ends `RunGame` |
| `KSA.ModLibrary` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L152-L326) | `CONTENT_MANIFEST`, `LocalManifestPath`, `LocalModsFolderPath`, `PrepareManifest`, `AddMods`, `PrepareAll`, `LoadAll` |
| `KSA.Constants.DocumentsFolderPath` | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L287) | Root of every per-user path; `LogsFolderPath` derives from it |

Where StarMap hooks in, with the lifecycle vocabulary of [Game loop and mod lifecycle](../../lifecycle/game-loop-and-lifecycle/overview.md):

| Phase | Value | What happens |
|---|---|---|
| Host start, config, contexts | `startup:starmap-before-main` | `StarMap/Program.cs`, `GameSurveyer` |
| Manifest, mod contexts, dependency graph, `StarMapBeforeMain` | `startup:starmap-before-main` | `ModLoader.PrepareMods` |
| Per-mod `Mod.PrepareSystems` prefix | `startup:immediate-load` | `StarMapImmediateLoad(KSA.Mod)` |
| `ModLibrary.LoadAll` postfix | `startup:all-mods-loaded` | `StarMapAllModsLoaded` |
| Restart prompt on `ModLibrary.PrepareAll` prefix | `startup:asset-load` | New enabled mods trigger a relaunch |

## Approaches

This topic is a reference: article 1 is the narrative, articles 2 to 4 are sub-topics that each have a
practical goal.

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Host process, contexts and hook binding walkthrough](./how-starmap-launches-ksa-1.md) | StarMap | `assembly-load-context` | `startup:*` | Complete numbered sequence and a context-resolution diagram; no code to copy except the launcher pattern |
| [Folder and manifest rules as StarMap consumes them](./how-starmap-launches-ksa-2.md) | StarMap, KSAGolf | `toml-config` | `startup:starmap-before-main`, `startup:asset-load` | Explains why a copied mod is ignored and how enabling a New mod leads to the restart prompt |
| [Manual Harmony prefix on Constants.DocumentsFolderPath](./how-starmap-launches-ksa-3.md) | StarMap | `harmony-prefix` | `startup:starmap-before-main` | Redirects all user data per instance; the restart drops the CLI flag, the environment variable survives |
| [Preload into the core load context from StarMapBeforeMain](./how-starmap-launches-ksa-4.md) | RealAtmospheres | `assembly-load-context` | `startup:starmap-before-main` | Needed to patch a lazily loaded game DLL; easy to get wrong by loading a second copy |

## Key facts at a glance

- `StarMap.exe` is a .NET console host, not an injector; it must be started from its own folder.
- Three kinds of context: `Default` (StarMap, Harmony, framework), one core context (game, StarMap.Core,
  StarMap.API) and one context per mod. Harmony and the game are shared; everything else in a mod folder is
  private.[StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L36-L63)
- StarMap sets the working directory and `APP_CONTEXT_BASE_DIRECTORY` to the game folder because KSA's `Main`
  trusts `BaseDirectory`.[StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L25-L56)
- The game ignores unknown command-line arguments, so StarMap's own flags and `--restarted` are harmless.
- Nothing catches exceptions in mod hooks: see [Keep mod exceptions out of KSA's frame loop](../../lifecycle/contain-exceptions-in-starmap-hooks/overview.md).

## See also

- [Game loop and mod lifecycle](../../lifecycle/game-loop-and-lifecycle/overview.md), [Choose and use the right StarMap lifecycle hook](../../lifecycle/choose-a-starmap-startup-hook/overview.md), [Write mod.toml - KSA keys and the [StarMap] table](../mod-toml-reference/overview.md),
  [Ship assets and code in one mod folder and find it at runtime](../ship-content-and-code-in-one-mod/overview.md), [Declare a dependency on another StarMap mod, required or optional](../../dependencies/declare-a-mod-dependency/overview.md)
- [Create a minimal StarMap mod](../create-a-minimal-starmap-mod/overview.md), [Deploy a mod on every build and debug it from an IDE](../../build/deploy-a-mod-on-every-build/overview.md), [Log from a mod: per-mod log file, game log category and log-once](../../lifecycle/write-a-per-mod-log-file/overview.md),
  [Clean up on game exit and relaunch the game from a mod](../../lifecycle/clean-up-on-game-exit/overview.md)
- Mod feature pages: [Host KSA in-process from a launcher executable](../../../../mods/starmap/features/host-process-launch.md), [Discover code mods through KSA's mod manifest](../../../../mods/starmap/features/manifest-mod-discovery.md),
  [Isolate each mod in its own AssemblyLoadContext](../../../../mods/starmap/features/per-mod-assembly-load-context.md), [Order mod loading with a ModDependencies graph](../../../../mods/starmap/features/mod-dependency-graph.md),
  [Discover lifecycle hooks by attribute type name](../../../../mods/starmap/features/attribute-hook-discovery.md), [Restart when new mods are enabled](../../../../mods/starmap/features/new-mods-restart-flow.md),
  [Redirect the KSA documents folder per instance](../../../../mods/starmap/features/instance-path-override.md), [Content-plus-code mod layout](../../../../mods/ksagolf/features/content-and-code-mod-layout.md),
  [Shader file redirect into a patched shadow tree](../../../../mods/realatmospheres/features/shader-file-redirect.md)
