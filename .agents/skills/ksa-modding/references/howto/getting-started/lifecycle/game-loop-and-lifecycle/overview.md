---
type: Game Feature
title: Game loop and mod lifecycle
description: How KSA starts, loads mods and runs each frame, and where StarMap hooks, Harmony patches and worker-thread code fit relative to the physics step
category: getting-started
group: lifecycle
topic: game-loop-and-lifecycle
demonstrated_by:
  - mods/starmap/features/startup-lifecycle-hooks
  - mods/starmap/features/frame-lifecycle-hooks
  - mods/starmap/features/unload-and-shutdown
  - mods/starmap/features/attribute-hook-discovery
  - mods/ksaero/features/patch-refusal-and-rollback
  - mods/ksacars/features/frame-step-latch-and-car-registry
  - mods/ksarmory/features/starmap-entry-and-fault-containment
  - mods/ksa-measuretools/features/resilient-patching-and-diagnostics
  - mods/gatos/features/frame-hooks-and-f2-fallback
  - mods/unscience/features/lib-hidden-ui-frame-hook
  - mods/unscience/features/lib-hotkey-guard
  - mods/ksa-public-telemetry/features/draw-while-ui-hidden-drawfps-postfix
  - mods/ksa-aircrafthud/features/always-on-onframeeditor-hook
  - mods/ksa-measuretools/features/measure-menu-and-tool-window
  - mods/ksarmory/features/dual-hook-simulation-step
  - mods/ksarmory/features/detect-save-load-ongameloaded
  - mods/ksa-advancedflightcomputer/features/post-solver-vehicle-tick
  - mods/purrtty/features/gate-game-input-while-terminal-focused
  - mods/shaderextensions/features/rendergame-render-pass-transpiler
  - mods/unscience/features/lib-physics-frame-hook
  - mods/ksacars/features/prepareworker-impulse-injection
  - mods/ksarmory/features/prepareworker-prefix-command-window
  - mods/ksarmory/features/consume-applied-sim-step
  - mods/ksa-advancedflightcomputer/features/guidance-vehicle-step-loop
  - mods/ksa-advancedflightcomputer/features/flight-computer-worker-copy-channels
  - mods/compendium/features/root-navigation-targets
  - mods/gatos/features/solver-phase-command-drain
  - mods/unscience/features/eternal-flame-resource-refill
  - mods/ohmystars/features/attitude-mode-handoff-patches
  - mods/ksaero/features/computedrag-call-site-transpiler
  - mods/ksaero/features/body-of-revolution-aerodynamics
  - mods/ksacars/features/keep-thrusting-craft-off-rails
  - mods/ksagolf/features/course-colliders-as-clutter
  - mods/ksa-advancedflightcomputer/features/flight-computer-command-sink
lifecycle:
  - startup:starmap-before-main
  - startup:immediate-load
  - startup:asset-load
  - startup:all-mods-loaded
  - startup:game-loaded
  - frame:prepare
  - frame:apply-solvers
  - frame:input
  - frame:dispatch-solvers
  - frame:imgui-new-frame
  - frame:viewports
  - frame:draw-ui
  - frame:imgui-render
  - frame:gauges
  - frame:controller
  - frame:lights
  - frame:celestials
  - frame:pre-render
  - frame:render
  - frame:post-render
  - frame:after-on-frame
  - physics:vehicle-solver
  - physics:orbit-solver
  - physics:cloth-solver
  - physics:bepu
  - thread:background
game_types:
  - KSA.App
  - KSA.Program
  - KSA.Universe
  - KSA.JobSystems
  - KSA.VehicleUpdateTask
  - KSA.PhysicsBubble
  - KSA.PhysicsStates
  - KSA.VehicleUpdateState
  - KSA.VehicleUpdateData
  - KSA.Vehicle
  - KSA.ConstraintSim
  - KSA.InputEvents
  - KSA.ModLibrary
  - KSA.Mod
  - KSA.SimStep
  - KSA.GameViewport
tags: [lifecycle, frame, game-loop, physics, threading, starmap, harmony, imgui, rendering, timing]
---

KSA runs one main-thread frame per `Program.OnFrame` call. Physics runs on worker threads in a
one-frame-deep pipeline: each frame first joins and applies the step started by the previous frame,
handles input, then starts the next step, and only then builds its UI and renders while that step is
in flight. Mod code only works if it runs at the right point in this sequence, and every other article
in this knowledge base refers to the points defined here.

This page is the reference map for build 2026.10.10.5554: the startup and loading sequence with the
StarMap hook at each step, the per-frame order, the physics step, the render passes, the ImGui window,
the threading model and the lifecycle vocabulary used in frontmatter. The numbered articles show the
four ways mods attach to it.

## Engine background

| Type | File | Role in the loop |
|---|---|---|
| `KSA.App` | [current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs) | Run loop: computes `dtPlayer`, calls `OnFrame` until exit |
| `KSA.Program` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs) | Constructor loads everything; `OnFrame`, `PrepareFrame`, UI, `OnPreRender`, `Render`, `RenderGame`, `PostRender` |
| `KSA.ModLibrary`, `KSA.Mod` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs), [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs) | Manifest, per-mod preparation and asset loading |
| `KSA.Universe` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs) | Simulation clock, solver dispatch (`ExecuteNext*Solvers`) and apply (`Apply*Solvers`), system load |
| `KSA.JobSystems` | [current/decomp/KSA/JobSystems.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/JobSystems.cs) | Job schedulers and worker pools |
| `KSA.VehicleUpdateTask`, `KSA.PhysicsBubble` | [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs), [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs) | Worker-side vehicle step: bubbles, substeps, module updates, integration |
| `KSA.PhysicsStates` | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs) | Force model (`ComputeDerivatives`, `ComputeDrag`), integrators |
| `KSA.VehicleUpdateState`, `KSA.VehicleUpdateData` | [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs), [current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs) | Per-vehicle worker state and the staging area copied back at apply |
| `KSA.ConstraintSim` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs) | Bepu simulation per bubble, shape registry lock |
| `KSA.InputEvents` | [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs) | Deferred state-change buffers applied in the settled window |
| StarMap patches | [StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs), [StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs), [StarMap.Core/Patches/ModPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModPatches.cs) | Where the StarMap lifecycle attributes fire |

## Process start and loading sequence

With StarMap, the game is not started by its own executable: `StarMap.exe` loads the game assembly
into an `AssemblyLoadContext`, instantiates every enabled mod, applies its own Harmony patches and
then invokes the game's entry point. Everything below runs on the main thread, in this order, except
the `ILoader.Load` pass inside row 10 and the `IBinder.Bind` pass inside row 11, which run on .NET
thread-pool threads.

| # | Where | What runs | StarMap hook (implemented by) | Lifecycle value |
|---|---|---|---|---|
| 1 | StarMap `Program.Main` [StarMap/Program.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/Program.cs#L9-L29) | Reads `StarMapConfig.json`, loads `0Harmony.dll`, builds a `CoreAssemblyLoadContext` for the game directory | none | - |
| 2 | `GameSurveyer.TryLoadCoreAndGame` [StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L25-L49) | Loads `StarMap.Core.dll` and the game assembly into that context, sets the working directory to the game folder, calls `IStarMapCore.Init` | none | - |
| 3 | `ModLoader.Init` -> `ModLoader.PrepareMods` [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L46-L104) | `ModLoader.Init` first applies `DocumentsPathPatches`: when `-InstancePath` or `STARMAP_INSTANCE_PATH` is set, a prefix on `Constants.DocumentsFolderPath` under its own Harmony id redirects the documents folder for StarMap, every mod and the game [StarMap.Core/Patches/DocumentsPathPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/DocumentsPathPatches.cs#L12-L31). `PrepareMods` then calls the game's `ModLibrary.PrepareManifest` (before the game's `Main`), then for each enabled manifest entry creates a `ModAssemblyLoadContext` from `[StarMap] EntryAssembly` in `mod.toml`, or from `<manifest id>.dll` (the folder name) when the table is absent [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L49-L57); mods with missing `ModDependencies` wait until their dependency loads | none | `startup:starmap-before-main` |
| 4 | `RuntimeMod.InitializeMod` [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L129-L162) | Finds the class marked `[StarMapMod]`, runs its parameterless constructor, registers every method whose attribute signature check passes, then invokes the `[StarMapBeforeMain]` method | `StarMapBeforeMain` (direct `MethodInfo.Invoke`, no patch) | `startup:starmap-before-main` |
| 5 | `StarMapCore.Init` [StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L25-L29) | `Harmony.PatchAll` on `StarMap.Core`: installs `ProgramPatcher`, `ModLibraryPatches`, `ModPatches` (after all mod constructors and `BeforeMain` methods have run) | - | - |
| 6 | `GameSurveyer.RunGame` [StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L51-L56) -> `Program.Main` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1693-L1703) | CLI args, `new Program(args)`, `Run()`. Harmony prefixes on `Program.Main` registered from `StarMapBeforeMain` run first, after every mod's `BeforeMain` and StarMap's own patches and before the constructor: KittenExtensions [KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs#L13-L18) and ShaderExtensions [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L20-L25) inject XML serializer overrides there | - | none in the vocabulary; bundles label such a prefix `startup:starmap-before-main`, the closest value |
| 7 | `Program..ctor` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L689-L885) | Logging, GLFW window, `GameSettings.LoadFromFile`, `ModLibrary.OnApplicationStart`, Vulkan `Renderer`, `ImGui.CreateContext`, loading screen, console commands | none | - |
| 8 | `ModLibrary.PrepareAll` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L886-L899), body [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L447) | New-mod confirmation, then for every enabled manifest entry: `Mod.MakeUsing`, `RegisterTo`, `Mod.PrepareSystems` (reads system XML headers for the system picker), `LoadSettings` | `ModLibraryPatches.BeforePrepareAll` prefix (restart prompt when new mods were enabled) [StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L30-L76); `StarMapImmediateLoad(Mod)` from the `ModPatches.OnLoadMod` prefix on `Mod.PrepareSystems` [StarMap.Core/Patches/ModPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModPatches.cs#L11-L22), once per mod, in manifest order. A mod's own prefix on `PrepareAll` registered in `StarMapBeforeMain` (KittenExtensions' XML patch run, [KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs#L21-L28)) runs before StarMap's prefix: neither sets a priority, Harmony orders equal-priority patches by registration, and StarMap registers its patches in row 5 | `startup:immediate-load` |
| 9 | `PreloadAssetBundles`, `PreloadLanguages`, system selection [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L900-L939) | `Mod.LoadAssetBundles` and `Mod.LoadSystems` in preload mode (`Mod.Preload = true`; `LoadSystems` reads every system file because `SystemLibrary.Default` is still null), then the system picker or the last-used system | none | `startup:asset-load` |
| 10 | `ModLibrary.LoadEditorTags`, `ModLibrary.LoadAll`, `AssignDefaults` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L941-L943), body [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L478-L522) | First `LoadEditorTags` [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L464-L476): per mod `Mod.LoadEditorTagDefinitions` deserializes every `assets` file again with `XmlLoader.Load<AssetBundle>` while `Preload` is still true, registers the `EditorTagDef` entries, then `VehicleEditor.MarkEditorTagDefinitionsLoaded` reads their flags. Then `LoadAll`: `Preload = false`, clears each mod's bundles and `SystemLibrary.Lookup`, then per mod `LoadFonts`, `LoadAssetBundles` (mod.toml `assets`), `LoadSystems` (only the selected system's file), planet meshes and heightmaps, and collects each mod's `Console.OnLoad`/`OnBoot` command lists; then every registered `ILoader.Load` in parallel on .NET thread-pool threads (`Parallel.ForEachAsync`; the main thread only pumps `Loading.OnFrame` until it finishes) [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L507-L519). This pass loads the file assets that `FileReference.OnDataLoad` registered outside preload [current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L39-L55), including every `<Shader>`, which `ShaderReference.DoLoad` compiles through `RenderCore.ShaderModuleUtils.FromFile` [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L168-L181); `ModLibrary.OnDataLoaded`, `Universe.OnDataLoaded`. Then `AssignDefaults` | `StarMapAllModsLoaded` from the `ModLibraryPatches.AfterLoad` postfix [StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L17-L28) | `startup:asset-load`, then `startup:all-mods-loaded` |
| 11 | Rest of the constructor [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L944-L1065) | Viewports, render targets, `ModLibrary.OnBoot` console commands, `JobSystems.Initialize`, `ModLibrary.Bind(renderer)` (every registered `IBinder.Bind` in `Parallel.ForEachAsync` on .NET thread-pool threads, each with its own staging pool, while the main thread pumps `Loading.OnFrame`) [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1849-L1861), `AttachGameData` (part game data merged into the templates), sounds, renderers | none | - |
| 12 | `Universe.LoadDefaultSystem` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1066-L1074) -> `Universe.LoadSystem` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L232-L260) | Builds the `CelestialSystem`, assigns crew, then `Universe.OnLoaded` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2534-L2547) runs the `follow`/`control` console commands and the collected mod `OnLoad` commands | none | `startup:game-loaded` |
| 13 | End of the constructor [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1270-L1329) | Part thumbnails, `IsLoaded = true`, welcome popup, `VehicleEditor.Initialize` | none | - |
| 14 | `App.Run` [current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L39-L74) | The frame loop starts; `Program.OnFrame` runs once per iteration (next section) | `StarMapBeforeGui` and `StarMapAfterGui` on frames with the HUD shown (`DrawUI`), `StarMapAfterOnFrame` every frame | `frame:*` |
| 15 | Loading a save: `UncompressedSave.Load` [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L61-L81) | `Universe.DeserializeSave` (calls `JobSystems.WaitAll`, destroys vehicles and bubbles, resets the sim step) [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2462), then `Program.OnGameLoaded` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2599-L2606), which only closes menus | none | `startup:game-loaded` |
| 16 | Exit: `PrepareFrame` sees `ShouldClose` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2223-L2227) | `Run` returns, `Program` is disposed, `RunGame` returns and the `GameSurveyer` disposes `StarMapCore`: `UnpatchAll()` with no id (which also removes the row 3 `-InstancePath` redirect, so `Constants.DocumentsFolderPath` returns the default folder from here on), then `ModLoader.Dispose` invokes `[StarMapUnload]` methods [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L143-L151) | `StarMapUnload` (direct invoke after the id-less `UnpatchAll` has removed every Harmony patch in the process, StarMap's and every mod's) | - |

## The run loop

`Program` derives from `KSA.App`, not from `Core.App`: the two classes have different `OnFrame`
signatures and only `KSA.App.OnFrame(double currentPlayerTime, double dtPlayer)` matches the override
in `Program` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L49). `KSA.App.Run` has no frame limiter and no sleep; it passes the
wall-clock time since start and a frame delta clamped to `1 / GameSettings.Current.Simulation.MinTargetFrameRate`
(default 30, so at most about 33 ms) [current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L47-L69) [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1045).[^ksa-app]
`Core.App` [current/decomp/Core/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/App.cs#L32-L70) is a different base class with a `FrameLimit` sleep; it is not on the
KSA frame path. When the game runs slower than the minimum target frame rate, player time (and therefore
simulated time) runs slower than wall-clock time.

## Per-frame sequence

`Program.OnFrame` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2334) in build 2026.10.10.5554, in order. Everything here
runs on the main thread. "Settled" means the physics workers are idle and main-thread vehicle and orbit
state holds the result of the last completed step; "in flight" means the next step is running on worker
threads.[^ksa-program]

| # | Method (lines) | What runs | Lifecycle value | Physics | Safe for mods | Unsafe for mods |
|---|---|---|---|---|---|---|
| 1 | `PrepareFrame` start [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2150) | Renderer rebuild when flagged, screenshot capture bookkeeping, `ExplosionFlashSystem.Update` | `frame:prepare` | in flight | - | - |
| 2 | waits [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2151-L2162) | `JobSystems.NearestOrbitAndPerformanceWorker.Wait`, `OrbitSolvers.Wait`, `VehicleSolver.Wait`, `ClothSolvers.Wait` | `frame:prepare` | joins | - | - |
| 3 | apply [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2163-L2171) | `Universe.ApplyOrbitSolvers`, `ApplyVehicleSolvers`, `ApplyClothSolvers` | `frame:apply-solvers` | settled | Reading the fresh step result (postfix on an `Apply*` method) | - |
| 4 | input [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2172-L2181) | Worker park timeouts, `WaitForPreviousPresent`, `Glfw.PollEvents` (fires `Program.OnKey`, `OnMouseButton`, `OnCursorPos`, `OnScroll`), `InputEvents.ApplyInputEvents` | `frame:input` | settled | Key handlers and queued `InputEvents` commands mutate vehicles here | - |
| 5 | bookkeeping [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2182-L2215) | Editor teardown, `RefreshVehiclesInFrame`, flight computer alerts, queue `NearestOrbitPointJob`, menu pause, `Universe.ProcessAutoWarp` | `frame:prepare` | settled | Teleports, part and resource changes, spawning and destroying vehicles | - |
| 6 | dispatch [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2216-L2221) | `Universe.GetJobSimStep`, `ExecuteNextClothSolvers`, `PartTree.FlushDirtyDerived`, `PartTree.FlushDirtyResourceManagers`, `ExecuteNextVehicleSolvers`, `ExecuteNextOrbitSolvers` | `frame:dispatch-solvers` | starts | A prefix on `ExecuteNextVehicleSolvers` (all vehicles) or on `Vehicle.PrepareWorker` (per vehicle, called inside it before that vehicle's snapshot and before the vehicle job is queued) is the last settled point for vehicle state; the cloth step is already running | Anything after this returns |
| 7 | `Network.Tick`, exit checks [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2222-L2235) | Network tick; on window close `RequestExit` and return `OnFrameResult.Exit`; a font rebuild also returns `Exit`; either ends `OnFrame` early (no UI, no render that frame) [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2243-L2248) | `frame:prepare` | in flight | - | Simulated state |
| 8 | gauge canvases [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2250-L2259) | `GaugeCanvas.UpdateCanvasesOnFrame` (controlled vehicle) or `BurnCanvas.OnFrame` | `frame:gauges` | in flight | - | - |
| 9 | `PrepareImGui` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2260-L2261), body [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2133-L2139) | `GizmosRenderer.ResetInstances`, `ImGuiBackend.NewFrame`, `ImGui.NewFrame`, `ImGuiHelper.StartFrame` | `frame:imgui-new-frame` | in flight | ImGui calls from here on | ImGui calls before this |
| 10 | `OnFrameEditor` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2567-L2578) | Creates the `VehicleEditor` when `EditorFlag` is set, `Editor.OnFrame` | `frame:viewports` (a patch on `VehicleEditor.OnFrame` inside it is `editor:on-frame`) | in flight | Editor state. A postfix here runs every frame, in flight too, and is the earliest ImGui point of a flight frame; it also runs with `DrawUI` false (AircraftHUD draws its HUD there) | - |
| 11 | `OnFrameViewports` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2608-L2689) | `IViewport.OnFrame` for every viewport (for a `GameViewport`: active controller `OnFrame`, then `Camera.OnFrame` [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L119)), burn nodes, hovered viewport, `CursorTarget.BeginFrame`, clearing `Astronomical.CursorHoverEligible` on every body of the current system [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2664-L2674), crew portraits | `frame:viewports` | in flight | Reading this frame's camera | Reading stock hover flags (cleared here, set again in rows 13 and 14) |
| 12 | `OnDrawUiFrame` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3160), only when `DrawUI` | Alerts, popups, debug windows, `Universe.DrawCelestialWindows`, `GameSettings.OnDrawUi`, `ImGuiWindow.DrawAllStaticWindows`, renderer debug UIs | `frame:draw-ui` | in flight | `StarMapBeforeGui` runs as a prefix here | Mutating simulated state |
| 13 | `OnDrawUiViewports` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3162-L3176), only when `DrawUI` | `DrawImGui` on secondary game viewports: the viewport's ImGui window, its world UI (`Universe.OnDrawUi(this)`, drawn into that window's draw list) and, when hovered, its own menu bar with `DrawProgramMenusHook` [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L193-L260) | `frame:draw-ui` | in flight | `StarMapAfterGui` runs as a postfix here | Mutating simulated state |
| 14 | `OnDrawUiThreadSafe` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3178-L3241), only when `DrawUI` | `MainViewport.SetPosition` from the ImGui main viewport, menu bar, vehicle patch and resource windows, gauge canvases, Esc menu, `Universe.OnDrawUi(MainViewport)` and `BurnContextMenu.Draw` (only while `EditorFlag` is false) [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3229-L3233), transfer planner, controller UI, `Editor.OnDrawUi`, launch menu | `frame:draw-ui` | in flight | ImGui; queue state changes through `InputEvents` | Mutating simulated state |
| 15 | `DrawFps`, `OnDrawUiConsole` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2274-L2275), bodies [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3248-L3284) [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3119-L3129) | Frame statistics overlay (on the main viewport's background draw list, `ImGuiHelper.GetMainOverlayDrawList` [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L43-L46), only with frame statistics on), console window; both are called outside the `DrawUI` guard | `frame:draw-ui` | in flight | The two UI-phase calls after the guarded block that still run with `DrawUI` false (with `OnFrameEditor` and `OnFrameViewports` earlier): a Harmony prefix on `OnDrawUiConsole` or a postfix on the private static `DrawFps` keeps a mod's UI up on hidden-HUD frames | - |
| 16 | `ImGui.Render` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2277-L2287) | Editor performance job queued, then ImGui draw data is finalized | `frame:imgui-render` | in flight | - | ImGui calls after this |
| 17 | post-UI updates [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2288-L2315) | `IsLoaded` guard, `OnFrameLaunchMenu`, `OnFrameHoveredOrbiters`, `UpdateGauges` and `GaugeData.AnimateAll`, deferred buffer disposal, `StreamingUploader.Poll` | `frame:gauges` | in flight | - | - |
| 18 | `OnFrameController` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2520-L2552) | Syncs the GLFW cursor mode and ImGui `NoMouse` flag with the active controller (camera motion itself happened in step 11) | `frame:controller` | in flight | - | - |
| 19 | lights [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2317-L2318) | `LightSystem.ClearLights`, `LightSystem.OnFrame` | `frame:lights` | in flight | Registering lights for this frame | - |
| 20 | `OnFrameCelestials` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2701-L2732) | Nearby celestial for the camera, `PlanetRenderer.OnFrame` | `frame:celestials` | in flight | - | - |
| 21 | `OnPreRender` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2322-L2323) | Exhaust, particles, swapchain acquire, `Universe.OnPreRender` (see Render sequence) | `frame:pre-render` | in flight | Exhaust, particle and per-viewport `Universe.OnPreRender` work; static-object instances and lights are still collected after this, inside `Render` (row 22) | - |
| 22 | `Render` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2324-L2330) | `UpdateRenderingResources` (per visible viewport: vehicle and `Universe.UpdateRenderData` render data, which reaches `LocationReference.UpdateStaticObjectRenderData`, then the part-model instance upload `PartModelRenderer.UpdateRenderData`; then `WriteForwardLights` and `LightSystem.UpdateRenderResources`), then `RenderGame` or `RenderEditor`, Vulkan command recording. `KittenEva` vehicles are skipped here; kitten avatars and part trees, parachute canopies and seated crew are submitted at the start of `RenderViewport` / `RenderGame` instead (Render sequence row 1a) [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4349-L4374) | `frame:render` | in flight | Last point to add instances and lights for this frame (a postfix on a render-data method); recording into a render phase | - |
| 23 | `PostRender` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2331-L2333) | Submit, present (`FrameNumber++` follows the `PostRender` call back in `OnFrame`, L2332) | `frame:post-render` | in flight | Readbacks | - |
| 24 | StarMap postfix on `OnFrame` [StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L38-L48) | `StarMapAfterOnFrame(currentPlayerTime, dtPlayer)`; Harmony postfixes run on every return, so this also fires on the early-exit frames | `frame:after-on-frame` | in flight | Per-frame bookkeeping, non-simulation work | Mutating simulated state |

## The physics step

KSA runs a one-frame-deep pipeline. `PrepareFrame` of frame N waits for step N-1, applies it, drains
input, then dispatches step N, which runs on worker threads while frame N builds its UI and renders.
Step N is applied at the start of frame N+1.[^ksa-universe]

### Time step

- `Universe.GetJobSimStep(dtPlayer)` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2615-L2627) returns a `SimStep`
  (`PreviousTime`, `NextTime`, `DeltaTime`) [current/decomp/KSA/SimStep.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SimStep.cs#L3-L10) starting at the last applied
  step's `NextTime` and spanning `dtPlayer * SimulationSpeed * _achievedSpeedFraction`.
- `_achievedSpeedFraction` is an adaptive throttle computed in `ApplyVehicleSolvers` from the last
  vehicle-solver tick time, so heavy physics slows simulated time instead of dropping steps
  [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1926-L1946).
- When paused (`IsPaused` is `SimulationSpeed == 0.0` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1799-L1802)) the solvers are
  still dispatched every frame with `DeltaTime == 0`; there is no paused, warp or `IsLoaded` gate, only
  `CurrentSystem != null` inside each `Universe` method.
- On the main thread, `Universe.GetElapsedTime()` is the applied step's `NextTime`
  [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2399-L2402), `GetLastSimStep()` is the step applied this frame and
  `GetNextSimStep()` is the step in flight [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2380-L2390).

### Dispatch (`ExecuteNext*Solvers`, main thread)

| Method | What it schedules | Snapshot taken |
|---|---|---|
| `ExecuteNextClothSolvers` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2114-L2124) | `ChuteClothSystem.SnapshotAndKick`: one `ChuteClothJob` per open canopy on `JobSystems.ClothSolvers` | vehicle inputs copied on the main thread |
| `PartTree.FlushDirtyDerived`, `FlushDirtyResourceManagers` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2218-L2219) | none (main-thread flush of derived part data before the vehicle snapshot) | - |
| `ExecuteNextVehicleSolvers` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2139) (only when `CurrentSystem` is set) | `PrepareVehicleWorkers` calls the virtual `Vehicle.PrepareWorker(SimStep)` for every vehicle in `CurrentSystem.All` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2141-L2155), then `SyncGroundClutter` (main-thread clutter exclusions and `PhysicsBubble.SyncGroundClutterStatics`), then one `VehicleUpdateTask` on `JobSystems.VehicleSolver` | at the end of each `PrepareWorker`, `VehicleUpdateState.PrepareFromVehicle` copies the manual control inputs [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L296-L306) and `VehicleUpdateData.Prepare` copies origin and kinematics and the flight computer (`NewFlightComputer.CopyFrom`, the copy that `FlightComputer.CopyFrom` writes back at apply) [current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84-L104) |
| `ExecuteNextOrbitSolvers` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2098-L2112) | one `CelestialUpdateTask` per `IOrbitSolved` (celestials and orbiting stars, not vehicles) on `JobSystems.OrbitSolvers` | the task reads the celestial's `Orbit` |

### Worker side

`VehicleUpdateTask.Run` [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L261-L298) runs on a `VehicleSolver` thread-pool
runner: trim and merge physics bubbles, `PhysicsBubble.Prepare`, build contact islands, run the islands
as a `ParallelBatch` on `JobSystems.VehicleWorkerPool`, split bubbles, `PublishResults`. Inside a bubble:

- `PhysicsBubble.StepFrameTo` [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L688-L714) sends a bubble with two or more
  vehicles straight to `FullPhysicsStepUntil` [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1391-L1653). A bubble with one
  vehicle goes through `ApplySingleVehicleMotion` [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1264-L1352): on rails it takes
  one analytic segment (along its orbit in freefall, or surface motion when landed, floating or bottomed); it drops
  to full physics when it is off rails (manoeuvring, rolling, sailing or dragging; actuators, keyframe
  animation, a waking kitten or high-fidelity ocean physics force that), when its freefall is inside or
  will enter the atmosphere or reach an impact (`FreefallNeedsFullPhysics`
  [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1123-L1149)), or when loose clutter bodies are present. Inside full physics a
  freefall vehicle in the atmosphere is switched off rails every substep
  [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1458-L1461), so every vehicle in air is integrated with forces.
- Substeps are variable, not fixed: each is the minimum of the remaining time, the next flight-computer
  tick, freefall events, the situation limit from `PhysicsStates.UpdateSituationAndComputeTimestep`
  [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L542-L599) (a 2 s free-flight cap, longer only for RK8
  long steps, then rotation-rate, buoyancy and parachute limits; nothing shortens a substep for drag)
  and, in a bubble with several vehicles, the predicted contact time (never cut below 1/60 s). A
  constrained substep is then subdivided into Bepu timesteps of at most 1/60 s.
- Per substep: `FlightComputer.ComputeControl` at its own cadence, active nozzles and chutes, then an
  `UpdateModulesJob` per vehicle (`VehicleUpdateState.UpdateModules`: rockets, gimbals, tanks, parachutes,
  generators and the other module updaters), then a constrained or unconstrained integration step
  [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1631-L1645).
- `PhysicsStates.ComputeDerivatives` [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L956-L1087) is the force model: gravity
  of the parent body, frame terms, buoyancy and `ComputeDrag` for ocean and atmosphere
  [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1005) [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1013), nozzle thrust and mass flow
  [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1019-L1032), parachute forces [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1033-L1085). It runs
  twice per unconstrained Verlet substep [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1130) [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1175),
  12 times per RK8 long step (substep over 2 s, CCI bubble frame, outside the physics radius, no chutes,
  so never with drag) [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L2396) [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1196-L1199),
  and once per constrained substep [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L2205).
- `ComputeDrag` is private and `AggressiveInlining` [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1089-L1111); patch its call
  site in `ComputeDerivatives`, not the method itself (see approach 4).
- Bepu runs inside constrained substeps: `ConstraintSim.Simulate` calls `Simulation.Timestep` with a
  `BepuWorkerDispatcher` over `VehicleWorkerPool` [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L887-L904)
  [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L220-L221), in Bepu timesteps of at most 1/60 s
  [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L2189-L2290).
- Parachute cloth is a separate single-threaded Bepu simulation per canopy on `ClothSolvers`; it is
  visual and double-buffered.

Workers write into per-vehicle `VehicleUpdateData` staging and module `StateUpdater`s, not into the live
`Vehicle`. Vehicle props, the physics environment and per-module state lists are copy-on-write: the
worker reads the live object until it first needs to change it [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L183-L239).
Exceptions: `Parachute.UpdateModules` mutates live `Parachute` objects from the worker
[current/decomp/KSA/Parachute.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Parachute.cs#L1096-L1176).

### Apply (`Apply*Solvers`, main thread)

- `ApplyOrbitSolvers` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1905-L1920): each ready `CelestialUpdateTask` updates its
  celestial's orbit position and cached points.
- `ApplyVehicleSolvers` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952): updates `_achievedSpeedFraction`, calls
  `VehicleUpdateTask.ApplyResultsToMainThread` [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L226-L259), sets
  `_lastSimStep = _nextSimStep` (universe time advances here) and refreshes per-frame data with `CurrentSystem.UpdatePerFrameData`
  [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L274-L285), which walks every root's tree and runs each
  body's `UpdatePerFrameData`. For a vehicle that recomputes its cached ECL state and its navball data
  and markers (`Vehicle.UpdateNavballData`, `UpdateNavBallMarkers`
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2645-L2679)), so a patch on the navball update runs here,
  before this frame's `ApplyInputEvents`, and a target changed by an input event reaches the navball one
  frame later. The per-vehicle write-back is `Vehicle.UpdateFromTaskResultsUnsynchronized`
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2435-L2544): it replaces `_kinematicStates`, `_bubbleOrigin`, props and
  environment when new, orbit state vectors, attitude, and copies the flight computer back
  unconditionally. It runs inside a `ParallelBatch`, so it can execute on a `VehicleWorker` thread while
  the main thread waits; `UpdateFromTaskResultsSynchronized` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2547-L2555) and the
  render events (docking, impacts, part failures) follow on the main thread.
- `ApplyClothSolvers` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1954-L1960): copies cloth positions into the parachutes'
  double buffers.

### Why main-thread mutation after dispatch is racy

Between step 6 and the next frame's step 3 the vehicle solver owns the simulation:

| What a mod writes during `frame:draw-ui` | What happens |
|---|---|
| Position, velocity, attitude (`Vehicle.GetPhysicsStatesMutable`) | The worker integrates from its snapshot and never sees the write; the next `ApplyVehicleSolvers` overwrites it [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2440-L2441) |
| Flight computer fields | Overwritten by `FlightComputer.CopyFrom` at apply [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2530) |
| Props, environment, module states | Copy-on-write race: lost if the worker already copied, otherwise possibly read torn |
| Parachute state | Data race with the worker's own writes |
| Lazy derived part-tree data (`PartTree.SubstanceStores`, a `SequencePerformanceList` recompute, anything that reaches `PartTree.EnsureDerived`) while the tree has pending `_derivedDirty` bits | Rebuilds shared tree state (substance stores, solid-motor stacks, sequence caches) on the main thread while the worker may call `EnsureDerived` on the same tree: `PhysicsBubble.RunVehiclePostWorkInner` recomputes the shared `SequencePerformanceList` of the controlled vehicle while the `Sequence` or `EngineControl` gauge canvas is enabled (`ResourceGroups.IsOpen` is `Program.SequenceCanvas.Enabled` [current/decomp/KSA/ResourceGroups.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ResourceGroups.cs#L15), `Program.EngineControlGaugeOpen`) [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L793-L796); canvases start enabled (`GaugeCanvas._enabled = true` [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L143)), so with the default UI this runs on every vehicle step. On a clean tree `EnsureDerived` only reads [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L521-L564), and `PrepareFrame` flushes every pending tree (`PartTree.FlushDirtyDerived`) before dispatch, so trees are normally clean during the draw; KSA-DeltaVMap still checks the private `_derivedDirty` before its own recompute |
| Bubbles, shapes, parts, teleports | Throws: `ConstraintSim.UnlockShapes` refuses while the step runs ("stage the change and apply it at the frame sync point") [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L124-L133) |

`JobSystems.WaitAll` [current/decomp/KSA/JobSystems.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/JobSystems.cs#L61-L67) joins the workers but does not apply their results,
so a write after a bare `WaitAll` is still overwritten at the next apply. `Universe.DeserializeSave`
gets away with it only because it also destroys every physics bubble [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2436-L2443).
The game's own route for state changes is the `InputEvents` buffers applied in step 4.

## Render sequence

Rendering is recorded on the main thread into one primary command buffer per frame in flight and
submitted in `PostRender`. There is no renderer registry, event or delegate list inside `RenderGame`;
mods add content through data the game already draws (gizmo instances, lights, particle emitters,
ImGui draw lists and draw callbacks) or patch a specific sub-renderer call.[^ksa-program]

`OnPreRender` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2372-L2441) (`frame:pre-render`):

1. Volumetric exhaust instances, exhaust lights and plume-trail segments for every vehicle in frame,
   particle spawning, `ExplosionSystem.Update`, `ParticleSystem.UpdateEmitters` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2376-L2417).
2. `Renderer.TryAcquireNextFrame` acquires the swapchain image [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2420).
3. `Universe.OnPreRender(viewport)` for every visible viewport with orbit lines, outside the editor
   [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2422-L2435); it forwards to `CelestialSystem.OnPreRender` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1872-L1875).
4. `Editor.OnPreRender` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2437).

`Render` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2443-L2481) (`frame:render`): on a failed acquire or a pending rebuild it
rebuilds the renderer and returns `Exit` (`PostRender` is skipped). Otherwise it submits the async
compute pool, runs the ocean readback, updates per-viewport shader data, calls
`UpdateRenderingResources` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2468) (global bindings, vehicle and editor render data,
`Universe.UpdateRenderData` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4371), lights, descriptor sets
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4308-L4436)), then records `RenderGame` or, when `EditorFlag` is set,
`RenderEditor` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2471-L2478).

`RenderGame` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4646-L4955), in recording order. Steps 1 to 22 use Vulkan dynamic
rendering scopes or compute dispatches on offscreen targets; only the composite and ImGui (steps 24 and
25) are inside the single swapchain render pass that steps 23 and 26 begin and end.

| # | Lines | Call(s) | `render:` phase | Condition |
|---|---|---|---|---|
| 1 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4664-L4683) | Clustered-light dummy clear, `UiCoverageMask.RecordMaskGeneration`, `RenderViewport` for each visible secondary viewport | (none) | secondary viewports only |
| 1a | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4684-L4711) | Main-view setup: `PlanetRenderer.OnPreRender`, `SuperMeshRenderSystem.SetViewOptions` / `ClearBuckets`; then, per vehicle in frame, `UpdateRenderData` for `KittenEva` only (kitten part tree and `KittenRenderable` animation and draw) and `UpdateParachuteRenderData` (canopy `ChuteRenderable.Draw`), then `ControlledVehicle.UpdateSeatedCrewRenderData`, PBR spheres. `RenderViewport` does the same for each secondary viewport [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4481-L4495). These calls only submit instances to the mesh buckets that the prepass, shadow and scene passes consume; kitten part-tree instances composed here come after the part-model upload in `UpdateRenderingResources` | (none: submission; a patch here is `frame:render`) | vehicle loop only when `Editor == null` |
| 2 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4713) | `PrePassRenderer.Render` | `prepass` | always |
| 3 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4715-L4719) | `AmbientOcclusionRenderer.Render` (compute) | `ambient-occlusion` | setting and valid prepass depth |
| 4 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4721-L4728) | `LightSystem.RenderShadowPass`, `DispatchComputePasses` | `shadows` | local lights |
| 5 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4732-L4736) | Planet mesh generation, ground clutter update | `planet` | always |
| 6 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4739-L4769) | Terrain sun shadow map, cascaded shadow maps (terrain, super meshes, parts, splines), post filter | `shadows` | settings |
| 7 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4775-L4783) | `RaytracingRenderer.RunRaytracePass` | `raytrace` | ray tracing active and camera in IVA |
| 8 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4784-L4790) | Depth copy, clutter depth prepass | `prepass` | always |
| 9 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4792-L4848) | Offscreen scope: Milky Way, stars, sun, static celestials, generic meshes, splines, part models, static objects, rings, `PlanetRenderer.Render` (L4835), `SuperMeshRenderSystem.RenderMainPass` (L4839), `GizmosRenderer.Render` (L4842), blended static objects | `scene-opaque` (sky, `planet` colour, `gizmos` meshes inside it) | planet only when a nearby celestial is billboarded |
| 10 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4850-L4851) | `ParticleSystem.UpdateParticles`, `WriteCommandsColorOpaque` | `particles` | particles setting |
| 11 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4861-L4866) | Planet exporter, ground-track map, `OceanRenderer.Render` | `ocean` | nearby celestial |
| 12 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4868-L4882) | `PlanetTransparenciesRenderer.Render` (atmosphere, clouds) or volumetric trails only | `atmosphere` | `ShowAtmosphere` |
| 13 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4888-L4892) | `VolumetricExhaustRenderer.Render` | `exhaust` | always |
| 14 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4893-L4904) | MSAA transparencies, part glass, `SuperMeshRenderSystem.RenderTranslucencyPass` (L4901), translucent particles (L4903), resolve | `translucency` | always |
| 15 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4906-L4909) | `OceanRenderer.RenderUnderwater` | `ocean` | nearby celestial |
| 16 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4910-L4916) | `SunbloomRenderer.Render`, `OverallBloomRenderer.Render` | `bloom` | settings inside the renderers |
| 17 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4917-L4926) | `PartSelectedRenderer.Run`, `SingleToMultisamplePass.Run` | (none) | MSAA |
| 18 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4927-L4930) | `OrbitLinePass.Run` | `orbit-lines` | `DrawUI` |
| 19 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4931-L4932) | `GizmoPass.Run`, resolve | `gizmos` | always (not gated by `DrawUI`) |
| 20 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4933-L4936) | `GridPass.Run` | `grid` | map camera, `DrawUI`, map-grid setting |
| 21 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4937-L4944) | `GaugeCanvas.CanvasesRender`, `ImGauge.PrepareRender`. Each canvas's `CanvasRenderer.Render` first refills its instance buffer: `GaugeBundle.UpdateInstanceBuffer` calls every component binding's `PackData` [current/decomp/KSA/CanvasRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CanvasRenderer.cs#L49-L59) [current/decomp/KSA/GaugeBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeBundle.cs#L116-L146), so gauge-binding state such as a button's `IsDisabled` or `IsSet` (for example `GaugeButtonFlightComputer.PackData` -> `Vehicle.IsFlightComputerDisabled`) is evaluated here, at `frame:render`, besides the hover and release calls from `GaugeCanvas.OnDrawUi` in the draw | `gauges` | `DrawUI` (and a vehicle, burn or Esc menu for canvases) |
| 22 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4945-L4947) | Offscreen screenshot capture, `RenderAaPreparePasses` (CMAA2 tonemap and compute) | `aa` | CMAA2 enabled |
| 23 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4948) | `BeginRenderPass` on the swapchain render pass | - | - |
| 24 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4949) | `RenderFinalComposite`: tonemap composite (`CompositeFrag`) or the CMAA2 final blit | `post-process` | always |
| 25 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4950) | `ImGuiBackend.Vulkan.RenderDrawData` (ImGui draw callbacks execute here) | `imgui` | always, even with `DrawUI` false |
| 26 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4951-L4954) | `EndRenderPass`, swapchain screenshot grab, `End` | - | - |

`RenderEditor` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4957-L5079) is a reduced version: prepass, ambient occlusion,
shadows (parts and splines only), the offscreen scope (stars, meshes, super meshes, gizmo meshes),
translucency, `GizmoPass`, bloom, `ImGauge`, then aa, composite and ImGui in the swapchain pass. It has
no planet, ray tracing, particles, ocean, atmosphere, exhaust, orbit lines or grid.

`PostRender` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2336-L2370) (`frame:post-render`): `Renderer.TrySubmitFrame` (submit
and present) [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2343), per-frame data clear, ImGui platform windows, screenshot
`OnPostRender` (which restores `DrawUI`), renderer rebuild on resize, `ResourceFrameIndex` advance,
`GameAudio.UpdateAudio`, `GizmoPass.PostRender`, `RenderGlobals.OnFrameEnd`, vehicles-in-frame clear.

Notes for render mods:

- The base game has no generic post-process chain. `post-process` in this bundle means the composite
  at step 24 plus whatever a mod inserts: ShaderExtensions replaces the `BeginRenderPass` call at step
  23 with a method that first records its per-window ImGui shaders (rewriting the tagged ImGui draw
  lists that step 25 then draws) and its pre-ImGui chain, then begins the pass; and it inserts its
  post-ImGui chain right after `EndRenderPass` at step 26, before the swapchain screenshot grab, with a
  transpiler on `RenderGame` [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L151-L225).
- A pass inserted between step 22 and the composite at step 24 that edits the offscreen color image
  (ShaderExtensions' pre-ImGui chain copies its result back into it) is not visible when CMAA2 is the
  selected anti-aliasing: step 22 has already composited the offscreen image into CMAA2's LDR target,
  and step 24 only blits that target
  [current/decomp/KSA.Rendering.PostProcessing/Cmaa2Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.PostProcessing/Cmaa2Renderer.cs#L166-L176)
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4628-L4644). With MSAA (the default,
  [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1303)) or no anti-aliasing, step 24
  composites the edited image.
- `GizmosRenderer.ResetInstances` runs at the top of every frame [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2260), so gizmo
  instances must be submitted between that reset and the render: in the UI phase or in a
  `frame:celestials` prefix, not in `StarMapAfterOnFrame`, which lands after the render.
- A postfix on a sub-renderer runs once per call, not once per frame. `SuperMeshRenderSystem.RenderMainPass`
  and `RenderTranslucencyPass` are called from `RenderViewport` for every visible secondary viewport
  (the four optional camera windows and the two crew portraits), recorded before the main view
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4568) [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4597),
  then from `RenderGame` for the main view (L4839, L4901) or from `RenderEditor` in the editor
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5047-L5053). Compare the `IViewport` argument with the main
  viewport, or draw with that viewport's own camera, when the work belongs to one view.

## The ImGui window

ImGui is single-context and main-thread only (`ImGui.CreateContext` in the constructor
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L842-L852)). Within a frame:

- `PrepareImGui` opens the frame (`ImGuiBackend.NewFrame`, `ImGui.NewFrame`, `ImGuiHelper.StartFrame`)
  at step 9, after physics dispatch and before the editor and viewports update
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2133-L2139).
- `ImGui.Render` closes it at step 16 [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2286). Draw data recorded between the two
  is rendered later in the `imgui` render phase.
- ImGui calls belong between the two. Calls made in `PrepareFrame`, in a `frame:celestials` or render
  patch, or in `StarMapAfterOnFrame` are outside the ImGui frame: Dear ImGui expects calls between
  `NewFrame` and `Render`, and anything submitted after `ImGui.Render` is not in this frame's draw data.
  Worker threads must never call ImGui.
- Window submission order inside the window is: `OnFrameEditor`, `OnFrameViewports`,
  `OnDrawUiFrame` (`StarMapBeforeGui` first), `OnDrawUiViewports` (`StarMapAfterGui` last),
  `OnDrawUiThreadSafe`, `DrawFps`, `OnDrawUiConsole`. `StarMapAfterGui` therefore runs before the
  menu bar, gauge canvases, `Universe.OnDrawUi` and `Editor.OnDrawUi`, not after all of the game's UI.
- Stock per-frame hover state is produced late in that order. Step 11 clears
  `Astronomical.CursorHoverEligible` on every body; `IOrbiter.OnDrawUi` sets it again only for the
  `CursorTarget` hit-test viewport [current/decomp/KSA/IOrbiter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IOrbiter.cs#L175-L245),
  reached through `Universe.OnDrawUi` from a secondary viewport's `GameViewport.DrawImGui` (step 13) or
  for the main viewport from `OnDrawUiThreadSafe` (step 14, flight only).
  `CelestialSystem.OnDrawUi` resets each viewport's `ViewportPartPicker` and the vehicles' `UpdateHighlight`
  refills it in the same call [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L363-L443)
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2903-L2927), and `MainViewport.Position`
  is refreshed at the start of step 14. A `StarMapAfterGui` hook therefore sees the main viewport's
  part picker and position from the previous frame and its body hover flags already cleared, while an
  input callback in the next frame's `PollEvents` sees what the previous UI pass left. KSA-MeasureTools
  reads both in its click handler and its AfterGui preview
  [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L220-L238).

`DrawUI` is a static property toggled by the `ToggleUi` input action [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L526-L540)
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1800-L1804) and forced off by screenshot capture
[current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L231). When it is false:

| Still runs | Skipped |
|---|---|
| `PrepareImGui` and `ImGui.Render`, `OnFrameEditor`, `OnFrameViewports` (without burn-node updates), `DrawFps`, `OnDrawUiConsole` | `OnDrawUiFrame` (and the `StarMapBeforeGui` prefix), `OnDrawUiViewports` (and the `StarMapAfterGui` postfix), `OnDrawUiThreadSafe` |

A mod that must keep drawing with the HUD hidden (a broadcast overlay, a HUD that replaces the stock
one, a terminal) hooks `OnDrawUiConsole` instead, which runs every frame inside the ImGui window
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2274-L2275). KSA-Public-Telemetry postfixes the private static `DrawFps`, called one
line earlier, through reflection
([HiddenUiPatch.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/HiddenUiPatch.cs#L40-L96),
[Keep drawing while the game UI is hidden](../../../../mods/ksa-public-telemetry/features/draw-while-ui-hidden-drawfps-postfix.md)); both hooks also fire while
`DrawUI` is true, so they must return early then.[^ksa-public-telemetry-hiddenuipatch] A postfix on `OnFrameEditor` or `OnFrameViewports` also runs with the
HUD hidden; it submits its window before every game window instead of after them, and only the
`OnFrameViewports` postfix sees this frame's camera. AircraftHUD draws its HUD from an `OnFrameEditor`
postfix ([Always-on ImGui hook from a Program.OnFrameEditor postfix](../../../../mods/ksa-aircrafthud/features/always-on-onframeeditor-hook.md)).
None of these run on the frames where `PrepareFrame` returns `Exit` (step 7). Render-side consumers
of `DrawUI` (orbit lines, gauges, the ImGui pass) are listed under Render sequence.

## Input timing

- GLFW callbacks are registered on the window in the constructor (`OnKey`, `OnMouseButton`,
  `OnCursorPos`, `OnScroll`) [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L787-L792) and fire inside `Glfw.PollEvents` in
  `PrepareFrame` (step 4), after the solvers are applied and before the next step is dispatched.
- `Program.OnKey` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1754) offers each key first to `GameSettings.OnKeyAll`
  [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L3422-L3435) (which swallows keys while the user is typing in a settings
  field), then `Popup.OnKeyAll`, then the console; it reaches `DispatchKeyEvent` (editor, camera,
  controller, global toggles, controlled vehicle) only if the console is closed and ImGui does not
  want the keyboard (`ImGui.GetIO().WantCaptureKeyboard`), which is the value ImGui computed in the
  previous frame.
- The stock console executes commands in this step: an Enter press goes through
  `ConsoleWindowEx.OnKey` -> `ConsoleWindow.HandleInputAction(Submit)` -> `TerminalInterface.Execute`
  [current/decomp/KSA/ConsoleWindowEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleWindowEx.cs#L14-L38), so console commands run while the solvers
  are settled. Code that calls `TerminalInterface.Execute` from a UI hook (purrtty's Game Console
  shell) runs the same commands after the next step has been dispatched.[^ksa-consolewindowex]
- Mouse buttons bound to game actions skip the key chain: `Program.OnMouseButton` calls
  `DispatchKeyEvent` directly unless a popup or ImGui over the main viewport takes the click
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2020).
- `Program.OnMouseButton` latches state around its handlers: before the `try`, the first press of a
  sequence sets `_inputViewport = HoveredViewport` and every press sets its bit in
  `_heldMouseButtons`; the `finally` clears the bit on release and drops `_inputViewport` when no
  button is held [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2003-L2097). A Harmony
  prefix that returns `false` skips both, so consuming a press and its release is balanced, but
  consuming a release whose press stock saw leaves the latch set (and any latch a handler armed on the
  press, such as `BurnContextMenu._rightClickValid`) until the mod clears it, as KSA-MeasureTools does
  by reflection [MeasureTools/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Core/GameReflection.cs#L8-L52).
- Inside these callbacks the camera's view and projection matrices are still last frame's
  (`Camera.OnFrame` runs in step 11) and `Cursor.DesktopPosition` is the ImGui mouse position copied in
  last frame's `PrepareImGui` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2133-L2139),
  so a click unprojects against the image the player saw. A following camera's `PositionEcl` is
  computed live from the followed body [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L110-L130)
  and already includes the step applied in step 3.
- Vehicle actions raised by keys and by UI are queued into `InputEvents` typed buffers and applied by
  `InputEvents.ApplyInputEvents` [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1114-L1140) right after `Glfw.PollEvents`.
  Commands queued by UI code during `frame:draw-ui` therefore apply at the next frame's step 4, while
  the solvers are settled. The buffers are plain arrays with no locking
  [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L943-L994), so only the main thread may add to them.

## Threading model

| Thread | Created by | Runs | Mod rules |
|---|---|---|---|
| Main thread | `App.Run` [current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L39-L74) | Everything in `Program.OnFrame`: input callbacks, apply and dispatch, ImGui, viewports, Vulkan command recording, submit and present, all StarMap hooks | The only thread allowed to touch ImGui, Vulkan resources, `InputEvents` buffers and simulated state (and the latter only in the settled window) |
| `JobScheduler` runners: `OrbitSolvers`, `VehicleSolver` (1 runner, highest priority), `ClothSolvers`, `NearestOrbitAndPerformanceWorker` (1 runner) | `JobSystems.Initialize` [current/decomp/KSA/JobSystems.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/JobSystems.cs#L32-L59) | One `IJob` batch each, posted to the .NET thread pool by `JobRunner` [current/decomp/Brutal.Concurrency.Jobs/JobRunner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Concurrency.Jobs/JobRunner.cs#L110-L116) | `Wait` is a busy spin [current/decomp/Brutal.Concurrency.Jobs/JobRunner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Concurrency.Jobs/JobRunner.cs#L149-L154); `Queue` throws "Jobs in progress" if a runner is busy [current/decomp/Brutal.Concurrency.Jobs/JobScheduler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Concurrency.Jobs/JobScheduler.cs#L42-L51) |
| `VehicleWorkerPool`, `RenderWorkerPool` | `JobSystems.Initialize` | Dedicated `DynamicWorkerPool` threads: bubble jobs, per-substep module updates, Bepu solver bodies; `ParallelBatch` callers help execute queued items while they wait | Code patched into the vehicle step runs here, per substep, in parallel across vehicles |
| .NET thread pool, startup loader and binder passes | `ModLibrary.LoadAll` (startup row 10) [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L507-L519) and `ModLibrary.Bind` (startup row 11) [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1849-L1861) | `Parallel.ForEachAsync` over every registered `ILoader` (file assets load and every `<Shader>` compiles [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L85-L118)), then over every `IBinder` (GPU uploads through a per-binder staging pool) | Patches on a loader or binder path (`FileReference.DoLoad` overrides, `ShaderModuleUtils.FromFile`, `IBinder.Bind` implementations) run concurrently with each other and must be thread-safe; `ShaderReference` keeps its compile options and include callback `[ThreadStatic]` for this reason. Example: Real Atmospheres' [`FromFile` prefix](../../../../mods/realatmospheres/features/shader-file-redirect.md) |
| GPU | `Renderer.TrySubmitFrame` in `PostRender` | The command buffer recorded in `RenderGame`, plus the async compute pool submitted in `Render` | Per-frame resources rotate through `ResourceFrameIndex`; `PrepareFrame` waits for the previous present [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2175) |
| Mod-owned threads and tasks | the mod | Anything (`thread:background`) | Never touch game objects; hand results to the main thread through a queue drained in a main-thread hook |

The pool sizes derive from `GameSettings.Simulation.OrbitSolvers` (about a quarter for each scheduler and
three quarters for vehicle workers) [current/decomp/KSA/JobSystems.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/JobSystems.cs#L34-L43).

The nearest-orbit job queued in step 5 reads flight plans and cached orbit points while the rest of
`PrepareFrame` runs. Mutations made after it is queued (for example a teleport in a
`GetJobSimStep` hook) must first call `JobSystems.NearestOrbitAndPerformanceWorker.Wait()`; the game's
own `InputEvents` run before it is queued [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2181) [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2196-L2201).

## Lifecycle vocabulary map

Every value from the content conventions (`docs/content-conventions.md`) mapped to the exact method in
build 2026.10.10.5554. "Hook" is how mods attach there.

| Value | Exact method (lines) | Hook |
|---|---|---|
| `startup:starmap-before-main` | `RuntimeMod.InitializeMod` [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L129-L162), before `Program.Main` | `StarMapBeforeMain`, mod constructor; bundles also use it for `Program.Main` prefixes registered there (startup row 6) |
| `startup:immediate-load` | `Mod.PrepareSystems` [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L417-L441) called from `ModLibrary.PrepareAll` [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L443) | `StarMapImmediateLoad` (StarMap prefix), only for a method with exactly one `KSA.Mod` parameter; a zero-argument method fails `IsValidSignature` and never runs |
| `startup:asset-load` | `ModLibrary.PreloadAssetBundles` [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L449-L462) and `ModLibrary.LoadAll` [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L478-L522) (`Mod.LoadAssetBundles` [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L157-L169)), with `ModLibrary.LoadEditorTags` [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L464-L476) between them | XML assets, Harmony on the `Mod` loaders; file loaders and shader compiles (`FileReference.DoLoad`, `ShaderModuleUtils.FromFile`) run on thread-pool threads inside `LoadAll`. KittenExtensions' XML patch run also counts as `startup:asset-load` but is a prefix on `ModLibrary.PrepareAll` [KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs#L21-L28), so it finishes before the first `startup:immediate-load` call |
| `startup:all-mods-loaded` | end of `ModLibrary.LoadAll`, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L942) | `StarMapAllModsLoaded` (StarMap postfix) |
| `startup:game-loaded` | `Universe.LoadSystem` -> `Universe.OnLoaded` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2534-L2547); after a save load `Universe.DeserializeSave` + `Program.OnGameLoaded` [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L80-L81) | Harmony postfix on `Universe.OnLoaded`, `Universe.DeserializeSave` or `Program.OnGameLoaded`; mod.toml console `OnLoad` commands |
| `frame:prepare` | `Program.PrepareFrame` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2236) | Harmony on `PrepareFrame` or the `Universe` methods it calls |
| `frame:apply-solvers` | `Universe.ApplyOrbitSolvers`, `ApplyVehicleSolvers`, `ApplyClothSolvers` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2163-L2171) | Harmony postfix on `ApplyVehicleSolvers` |
| `frame:input` | `Glfw.PollEvents` -> `Program.OnKey` and friends, `InputEvents.ApplyInputEvents` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2179-L2181) | Harmony on `Program.OnKey`, `GameSettings.OnKeyAll`, `Vehicle.OnKey`; enqueue `InputEvents` |
| `frame:dispatch-solvers` | `Universe.GetJobSimStep`, `ExecuteNextClothSolvers`, `ExecuteNextVehicleSolvers`, `ExecuteNextOrbitSolvers` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2216-L2221) | Harmony prefix on `ExecuteNextVehicleSolvers` or `Vehicle.PrepareWorker`; transpiler on `PrepareFrame` |
| `frame:imgui-new-frame` | `Program.PrepareImGui` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2133-L2139) | Harmony postfix on `PrepareImGui` |
| `frame:viewports` | `Program.OnFrameViewports` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2608-L2689) (`GameViewport.OnFrame` moves cameras) | Harmony on `OnFrameViewports`, camera or controller types; a postfix on `Program.OnFrameEditor` used in flight (runs just before `OnFrameViewports`) |
| `frame:draw-ui` | `OnDrawUiFrame`, `OnDrawUiViewports`, `OnDrawUiThreadSafe`, `DrawFps`, `OnDrawUiConsole` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2264-L2276) | `StarMapBeforeGui`, `StarMapAfterGui`; Harmony on `OnDrawUiConsole` or `DrawFps` for hidden-HUD frames |
| `frame:imgui-render` | `ImGui.Render` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2286) | none needed |
| `frame:gauges` | `GaugeCanvas.UpdateCanvasesOnFrame` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2253), `UpdateGauges`, `GaugeData.AnimateAll` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2296-L2312) | Harmony on `Program.UpdateGauges` |
| `frame:controller` | `Program.OnFrameController` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2520-L2552) (cursor mode only) | rarely useful |
| `frame:lights` | `LightSystem.ClearLights`, `LightSystem.OnFrame` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2317-L2318) | register lights after the clear |
| `frame:celestials` | `Program.OnFrameCelestials` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2701-L2732) | Harmony prefix for a pre-render step that runs with the HUD hidden |
| `frame:pre-render` | `Program.OnPreRender` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2372-L2441) (exhaust, particles, swapchain acquire, `Universe.OnPreRender`, `Editor.OnPreRender`) | Harmony on `OnPreRender` or `Astronomical.OnPreRender` overrides |
| `frame:render` | `Program.Render` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2443-L2481) -> `UpdateRenderingResources` (incl. `Universe.UpdateRenderData`), `RenderGame` / `RenderEditor` | Harmony on sub-renderer calls or a transpiler on `RenderGame` |
| `frame:post-render` | `Program.PostRender` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2336-L2370) | Harmony on `PostRender` |
| `frame:after-on-frame` | StarMap postfix on `Program.OnFrame` [StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L38-L48) | `StarMapAfterOnFrame` |
| `physics:vehicle-solver` | `VehicleUpdateTask.Run` [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L261-L298) -> `PhysicsBubble` jobs -> `PhysicsStates.ComputeDerivatives` | Harmony transpiler or prefix on worker-side methods (approach 4) |
| `physics:orbit-solver` | `CelestialUpdateTask` on `JobSystems.OrbitSolvers` [current/decomp/KSA/CelestialUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialUpdateTask.cs#L52-L64), one per `IOrbitSolved` (celestials, orbiting stars); vehicle flight-plan and burn-plan prediction (`RecalculateFlightPlan`, `RecalculateBurnPlan`) runs in the vehicle solver's post-work, `PhysicsBubble.RunVehiclePostWorkInner` [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L757-L772), so it is `physics:vehicle-solver` | Harmony on `CelestialUpdateTask` |
| `physics:cloth-solver` | `ChuteClothJob` on `JobSystems.ClothSolvers` [current/decomp/KSA/ChuteClothJob.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChuteClothJob.cs#L20-L33) | rarely patched |
| `physics:bepu` | `ConstraintSim.Simulate` -> `Simulation.Timestep` [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L887-L904) | colliders and constraints through game types, not Bepu callbacks |
| `render:<phase>` | `Program.RenderGame` table above | Harmony postfix on the sub-renderer that records the phase |
| `editor:<phase>` | `VehicleEditor.OnFrame` (called from `Program.OnFrameEditor`, whose own patches are `frame:viewports`) [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L2429), `VehicleEditor.OnDrawUi` [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L5747), `VehicleEditor.OnPreRender` [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L5425), `Program.RenderEditor` | Harmony on `VehicleEditor` |
| `thread:background` | mod-owned threads | marshal back through a main-thread hook |
| `event:<name>` | e.g. `event:key` = `Program.OnKey` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1754), `event:save` = `UncompressedSave.Load` / `Write` | Harmony on the event's method |

## Corrections to earlier write-ups

Several mods document the frame order themselves. Checked against this build:

- KSArmory, KSACars and KSAGolf ship a `KSA-FRAME-ORDER.md` written for build 2026.8.22.5348
  [docs/KSA-FRAME-ORDER.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/KSA-FRAME-ORDER.md#L1-L10). Its order is still right, but `PrepareFrame` now waits on
  four schedulers (it lists two), `ApplyClothSolvers`, `ExecuteNextClothSolvers` and the `PartTree`
  flushes are new, there is no `Cursor.UpdateInputRay` in `Program`, and `DrawUI` is toggled by the
  regular `ToggleUi` input action, not a debug key [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1800-L1804). Line numbers in it
  have drifted by 100 to 350 lines.
- unscience's scope notes say `Universe.DeserializeSave` joins only the orbit, vehicle and cloth solvers
  [scope/00-architecture-and-abstractions.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/00-architecture-and-abstractions.md#L21-L23); it calls `JobSystems.WaitAll`, which also
  joins the nearest-orbit worker [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2437) [current/decomp/KSA/JobSystems.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/JobSystems.cs#L61-L67). The
  `HiddenUiFrameHook` remark that the menu bar draws after `OnDrawUiConsole` is wrong: `DrawMenuBar`
  runs in `OnDrawUiThreadSafe`, before it [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3186-L3189).
- unscience, gatOS and purrtty declare `[StarMapImmediateLoad]` methods with no parameter; StarMap's
  signature check rejects them and never calls them [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L150).
  Their bodies are empty or log-only, so nothing is lost.
- gatOS's comment that `StarMapAfterGui` is where "the vehicle-solver workers have finished"
  [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L371-L377) does not hold; the workers are in flight during both GUI
  hooks, which is why gatOS calls `JobSystems.VehicleSolver.Wait()` (a main-thread stall until the
  step finishes) before its welds and IVA drivers. For welds the wait matters: `Vehicle.Teleport`
  removes the vessel from its physics bubble, which is only legal while the worker is idle, and that
  removal is also why the next apply does not overwrite the teleport (see
  [approach 3](game-loop-and-lifecycle-3.md#variations-and-gotchas)). For the IVA driver it does not
  make the `Vehicle` reads any fresher, since those fields change only at `ApplyVehicleSolvers`.
  gatOS's telemetry sample, schedule tick and Frame-phase command drain also do not run in
  `StarMapBeforeGui` as its threading notes describe: with the camera feature on (the default) they
  run in a Harmony prefix on the main viewport's `GameViewport.OnFrame` (`frame:viewports`, every
  frame, HUD visible or not), and `StarMapBeforeGui` or the `StarMapAfterOnFrame` fallback runs them
  only when that hook is absent
  [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L574-L583) [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L450-L467).
- purrtty's input patch comments say they run "on the GLFW poll (tick) thread"
  [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L157-L163); GLFW callbacks fire inside `Glfw.PollEvents` on the
  main thread [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2179).

## Approaches

| Article | Mods | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. Hook the frame with StarMap lifecycle attributes](game-loop-and-lifecycle-1.md) | KSAero, KSACars, KSArmory, KSA-MeasureTools, OhMyStars, ShaderExtensions | `starmap-lifecycle` | `startup:*`, `frame:draw-ui`, `frame:after-on-frame` | No patching, stable across updates; only three frame points, two skipped with the HUD hidden, all after physics dispatch |
| [2. Hook any frame point with Harmony patches on Program and Universe](game-loop-and-lifecycle-2.md) | unscience, KSArmory, KSA-Public-Telemetry, KSA-MeasureTools, purrtty, KSA-AdvancedFlightComputer, gatOS, ShaderExtensions | `harmony-prefix`, `harmony-postfix`, `harmony-transpiler` | `frame:input`, `frame:draw-ui`, `frame:celestials`, `frame:render`, `event:save` | Any point in the frame; private targets break on game updates and must degrade |
| [3. Mutate simulation state safely relative to the physics step](game-loop-and-lifecycle-3.md) | unscience, KSACars, KSArmory, KSA-AdvancedFlightComputer, Compendium, gatOS | `InputEvents` buffers, `harmony-prefix` on `Vehicle.PrepareWorker` / `ExecuteNextVehicleSolvers`, `harmony-transpiler` on `PrepareFrame` | `frame:input`, `frame:dispatch-solvers` | The only writes the next step sees; one frame of latency from UI; per-frame, not per-substep |
| [4. Run code inside the physics step on worker threads](game-loop-and-lifecycle-4.md) | KSAero, KSACars, KSAGolf | `harmony-transpiler` on `ComputeDerivatives`, worker-side prefixes and postfixes | `physics:vehicle-solver`, `physics:bepu` | Per-substep accuracy; multithreaded, hot, and fragile against solver changes |

## See also

- [Approach 1: StarMap lifecycle attributes](game-loop-and-lifecycle-1.md)
- [Approach 2: Harmony patches on Program and Universe](game-loop-and-lifecycle-2.md)
- [Approach 3: mutate simulation state safely](game-loop-and-lifecycle-3.md)
- [Approach 4: run code inside the physics step](game-loop-and-lifecycle-4.md)
- The lifecycle vocabulary is defined in the project's content conventions (`docs/content-conventions.md`
  in the repository root); this page maps each value to code.

[^ksa-app]: KSA.App (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-public-telemetry-hiddenuipatch]: KSA-Public-Telemetry: HiddenUiPatch.cs
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-consolewindowex]: KSA.ConsoleWindowEx (decompiled, build 2026.10.10.5554)
