---
type: Game Feature
title: Choose and use the right StarMap lifecycle hook
description: What game state exists at StarMapBeforeMain, StarMapImmediateLoad and StarMapAllModsLoaded, how to pick between them, and how to verify a hook is bound and firing
category: getting-started
group: lifecycle
topic: choose-a-starmap-startup-hook
demonstrated_by:
  - mods/starmap/features/startup-lifecycle-hooks
  - mods/realatmospheres/features/reflection-only-mod-build
  - mods/shaderextensions/features/duck-typed-attribute-api
  - mods/ksa-advancedflightcomputer/features/superseded-mod-detection-and-settings-import
  - mods/compendium/features/cross-mod-json-catalog
  - mods/ksarmory/features/starmap-entry-and-fault-containment
  - mods/gatos/features/compile-gated-game-seam
  - mods/ksaero/features/game-version-check
  - mods/starmap/features/attribute-hook-discovery
lifecycle:
  - startup:starmap-before-main
  - startup:immediate-load
  - startup:asset-load
  - startup:all-mods-loaded
  - startup:game-loaded
  - frame:draw-ui
  - frame:celestials
  - frame:after-on-frame
game_types: [KSA.Program, KSA.ModLibrary, KSA.Mod, KSA.Universe, KSA.SimStep]
tags: [starmap, lifecycle, startup, hooks, before-main, immediate-load, all-mods-loaded]
---

Three of StarMap's seven hooks fire once, during KSA's startup, at very different points. Most "works in one hook
but not another" problems come from assuming more state exists than really does. This topic maps what exists at each
hook, tells you which to pick for a given job, and shows how to check that a hook is bound at all. The frame order
after startup is in [Game loop and mod lifecycle](../game-loop-and-lifecycle/overview.md); per-frame work across several hooks (latching the simulation step
while the HUD is hidden) is the topic `draw-ui-while-the-hud-is-hidden`.

## Engine background

The `Program` constructor runs once, before the frame loop. The relevant calls
(build 2026.10.10.5554):[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L689-L1327)

| Line | Step | Hook |
|---|---|---|
| - | StarMap discovers mods, loads entry DLLs | `StarMapBeforeMain` (per mod) |
| 692-723 | Console, log directories, `LogSystem.Initialize` | |
| 747-776 | `Glfw.Init`, window, `GameSettings.LoadFromFile` | |
| 794 | `new Renderer(...)` | |
| 842-861 | ImGui context, fonts, `Loading` screen | |
| 887-897 | New-mod popups (`ConfirmMods`), `Manifest.Save` | |
| 899 | `ModLibrary.PrepareAll`: per mod `MakeUsing`, `PrepareSystems`, `LoadSettings` | `StarMapImmediateLoad` (prefix of each `Mod.PrepareSystems`) |
| 900-901 | `PreloadAssetBundles`, `PreloadLanguages` | |
| 941-942 | `LoadEditorTags`, `ModLibrary.LoadAll` | `StarMapAllModsLoaded` (postfix of `LoadAll`) |
| 943 | `AssignDefaults` | |
| 978 / 985 | `ModLibrary.Bind` (GPU), `AttachGameData` | |
| 1069 | `Universe.LoadDefaultSystem` | |
| 1317 | `IsLoaded = true`; `Run` starts the frame loop | |

`PrepareSystems` is in [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L417-L441) and is called only from `PrepareAll`;
`LoadAll` is in [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L478-L522). StarMap's dispatchers are
[StarMap.Core/Patches/ModPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModPatches.cs#L8-L23) and
[StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L14-L28).

What exists at each hook:

| State | BeforeMain | ImmediateLoad | AllModsLoaded |
|---|---|---|---|
| `KSA.dll` loaded, cwd = game folder, manifest | yes | yes | yes |
| KSA log sinks, window, Vulkan renderer, ImGui, fonts | no | yes | yes |
| Your `KSA.Mod` (`Id`, `DirectoryPath`) | no | yes | yes (via `ModLibrary.Find`) |
| Asset bundles, part templates, system templates | no | no | yes |
| `AssignDefaults`, viewports, `Bind` (GPU), `AttachGameData`, universe, vehicles | no | no | no |
| Other StarMap mods initialized | earlier ones only | yes | yes |

Nothing in StarMap marks "the scene is ready": mods that need `Universe`, vehicles or viewports resolve them lazily on
the first frame hook (for example the current sim step via `Universe.GetLastSimStep`, [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2381)).

## Approaches

| Article | Mod | Technique | Lifecycle | Use when |
|---|---|---|---|---|
| [StarMapBeforeMain: patch code that runs during startup](./choose-a-starmap-startup-hook-1.md) | RealAtmospheres, ShaderExtensions | `starmap-lifecycle` | `startup:starmap-before-main` | You must patch code that runs in the `Program` constructor before `LoadAll` (shader compiles, `Program.Main`); no game state; failures abort the launch unless caught |
| [StarMapImmediateLoad: register data before assets deserialize](./choose-a-starmap-startup-hook-2.md) | KSA-AdvancedFlightComputer, KSArmory, Compendium | `starmap-lifecycle` | `startup:immediate-load` | A value, file or enum must exist before XML or shaders load; needs the `KSA.Mod` parameter |
| [StarMapAllModsLoaded: work that needs the full mod set](./choose-a-starmap-startup-hook-3.md) | KSArmory, KSAero, gatOS | `starmap-lifecycle` | `startup:all-mods-loaded` | The default: patches, version check, logging, warm-ups, reading loaded definitions |
| [Signature checklist against StarMap's binding rules](./choose-a-starmap-startup-hook-4.md) | StarMap, KSArmory | `starmap-lifecycle` | all | A hook is silent: verify visibility, signature and dispatch point, and add probes |

## Decision guide

1. Do you patch something that has already run by `AllModsLoaded` (shader compile, asset-list read)? Use `BeforeMain`.
2. Must something exist before your own mod's asset XML, or before another mod's? Use `ImmediateLoad`.
3. Everything else, including every Harmony patch on gameplay, rendering or UI code: `AllModsLoaded`.
4. Needs a scene (vehicle, celestial, viewport)? Do it from a frame hook or patch the game's method, not from any
   startup hook.

## See also

- [Game loop and mod lifecycle](../game-loop-and-lifecycle/overview.md), [How StarMap launches KSA and loads your mod](../../loader/how-starmap-launches-ksa/overview.md), [Keep mod exceptions out of KSA's frame loop](../contain-exceptions-in-starmap-hooks/overview.md),
  [Declare a dependency on another StarMap mod, required or optional](../../dependencies/declare-a-mod-dependency/overview.md), [Share types and design an inter-mod API between StarMap mods](../../dependencies/share-types-between-mods/overview.md)
- Not yet written: `measure-and-budget-mod-performance` (frame budgets), `draw-ui-while-the-hud-is-hidden`
- [Startup lifecycle hooks (BeforeMain, ImmediateLoad, AllModsLoaded)](../../../../mods/starmap/features/startup-lifecycle-hooks.md), [Discover lifecycle hooks by attribute type name](../../../../mods/starmap/features/attribute-hook-discovery.md),
  [Reflection-only mod build](../../../../mods/realatmospheres/features/reflection-only-mod-build.md), [Attribute duck typing as an inter-mod API](../../../../mods/shaderextensions/features/duck-typed-attribute-api.md),
  [Stand down for a superseded standalone mod and import its settings](../../../../mods/ksa-advancedflightcomputer/features/superseded-mod-detection-and-settings-import.md),
  [Cross-mod JSON catalog](../../../../mods/compendium/features/cross-mod-json-catalog.md), [StarMap entry class with per-hook fault containment](../../../../mods/ksarmory/features/starmap-entry-and-fault-containment.md),
  [Compile-gated game seam](../../../../mods/gatos/features/compile-gated-game-seam.md), [Game version check](../../../../mods/ksaero/features/game-version-check.md)
- [Measure what a mod costs per frame, throttle expensive work and warm up hot code](../../../testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/overview.md) - related topic
- [Keep mod UI and per-frame work running when the HUD is hidden (F2)](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md) - related topic

