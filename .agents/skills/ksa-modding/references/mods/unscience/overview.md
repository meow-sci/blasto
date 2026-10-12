---
type: Mod
title: unscience
description: A meow-sci monorepo supermod that bundles 31 feature sub-mods (welding, scaling, decals, plumes, rings, cameras, kittens, saves and more) behind one StarMap entry class, one Harmony instance and one ImGui toolbox
mod: unscience
tags: [supermod, monorepo, starmap, harmony, imgui, vulkan, saves, physics, rendering]
repo: unscience
license: MIT
author: meow sci
loader: starmap
mod_id: unscience
---

unscience is meow sci's "supermod": one Kitten Space Agency mod that lets the player do absurd things to the
game (weld vessels and planets together, resize craft, paint and decal hulls, spawn and animate kittens, add
rings to moons, mount live cameras on parts, fly an EVA kitten as a rocket, load new parts without restarting)
through a suite of 31 bundled sub-mods with pop-culture names.[^readme-md] The repository is a monorepo of about
630 C# files: one `.lib` project per feature, a development host for almost every feature, a shared infrastructure library,
18 managed test projects and the `unscience` host that is the only thing shipped.[^repository-index-md]

For modders it is the largest single catalog of KSA integration techniques in this corpus: Harmony prefixes,
postfixes, finalizers and transpilers on render, physics, save and input paths; custom Vulkan draws inside the
game's own passes; runtime part, mesh, texture, material and ring-tree construction; Bepu collider scoping; a
main-thread physics handoff; and a crash-safe, versioned save sidecar. This bundle documents 106 reusable
features across 34 sub-mod concepts.

## Install footprint

The shipped manifest ([unscience/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/mod.toml)) has no `assets` and no mod dependencies:

```toml
# unscience/mod.toml L1-L7
name = "unscience"
description = "unscience"
version = "0.1.0"
author = "meow sci"

[StarMap]
EntryAssembly = "MeowSci.Unscience"
```

| Item | Value |
|---|---|
| Manifest id | `unscience` (folder `<KSA mods>/unscience/`, enabled in the KSA mod manifest) |
| Entry assembly | `MeowSci.Unscience.dll` |
| Other files | `MeowSci.Unscience.pdb` and `.deps.json`, 32 `MeowSci.*Lib.dll` / `MeowSci.KsaAbstractions.dll` feature and shared assemblies, `Tomlyn.dll`, `LICENSE`, `third-party-licenses/` |
| Game content | None; no XML asset bundles, shaders or models ship (shader changes are compiled in memory at run time) |
| User data | `<Documents>/My Games/Kitten Space Agency/.unscience/`: window layout, presets, settings, copied PNG/GLB/sound libraries[^ksa-abstractions-lib-ksapaths-cs] |
| Per save | `unscience.json` beside each save's `universe.xml` |

The CI version stamp rewrites `version` to `1.<run>.0` before packaging; see
[build and release](./features/unscience-build-and-release.md).

## Loader model and entry class

unscience is a StarMap mod. StarMap creates a per-mod `AssemblyLoadContext` for `MeowSci.Unscience.dll`
before the game's `Main` runs and dispatches attribute-marked methods on `MeowSci.Unscience.Mod`
([unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs)):[^unscience-mod-cs][^starmap-modloader]

| Hook | Lifecycle | What unscience does |
|---|---|---|
| `[StarMapImmediateLoad]` | (never runs) | Nothing. The method takes no arguments, but StarMap only registers an `ImmediateLoad` method with exactly one `KSA.Mod` parameter and silently skips any other signature, so it is never dispatched[^starmap-baseattributes][^starmap-runtimemod] |
| `[StarMapAllModsLoaded]` (postfix on `ModLibrary.LoadAll`) | `startup:all-mods-loaded` | Construct and `Initialize()` all 31 `ISubmod`s in display order, restore window and header state, hand controller objects to the patcher, register the hidden-HUD replay, build the scene-save wiring, then `Patcher.Patch()` |
| `[StarMapBeforeGui]` (prefix on `Program.OnDrawUiFrame`) | `frame:draw-ui` | `Update(dt)` on every submod, visible or not |
| `[StarMapAfterGui]` (postfix on `Program.OnDrawUiViewports`) | `frame:draw-ui` | F11 toggle, the "Unscience Toolbox" window, every submod's floating windows, window-layout autosave timer |
| `HiddenUiFrameHook` (prefix on `Program.OnDrawUiConsole`) | `frame:draw-ui` | Replays the `Update(dt)` pass when the HUD is hidden (F2), because the game then skips both StarMap UI targets |
| `[StarMapUnload]` | shutdown | Save layout, dispose submods, detach save callbacks, unpatch. StarMap's `DeInit` has already called `UnpatchAll()` with no owner id before it dispatches this attribute, so the unpatch is a no-op at game exit[^starmap-core]. It also runs after KSA's `Program.Dispose` (StarMap disposes its core only when `Program.Main` has returned), which has already destroyed the Vulkan device, terminated GLFW and released FMOD, so sub-mod `Dispose` code that frees GPU or audio handles runs against torn-down systems[^starmap-gamesurveyer][^ksa-program] |

StarMap's GUI attributes are plain Harmony patches on `Program` methods that the game calls only while
`Program.DrawUI` is true; `OnDrawUiConsole` runs either way, which is why the host needs the extra hook.[^starmap-programpatcher][^ksa-program]
Everything that must run at a precise physics or render moment does not use these UI hooks at all; it is a
Harmony patch registered by the consolidated patcher (see Architecture). Details:
[supermod host](./features/unscience-supermod-host.md),
[consolidated patcher](./features/unscience-consolidated-patcher.md),
[hidden-UI frame hook](./features/lib-hidden-ui-frame-hook.md).

## Dependencies

| Dependency | Version | How it is resolved |
|---|---|---|
| StarMap loader (`StarMap.API`) | 0.3.6 | Compile-time package with `PrivateAssets="all"`; at run time the loader's own copy is found first by the mod's load context |
| Harmony (`Lib.Harmony`) | 2.4.2 | Same: not shipped; StarMap loads `0Harmony.dll` into the default context before the game starts[^starmap-mod-alc] |
| Tomlyn | 0.19.0 | Shipped in the mod folder, resolved through `MeowSci.Unscience.deps.json`; used for presets and settings files |
| Game assemblies (`KSA`, `Brutal.*`, Bepu, Vulkan bindings) | game build | Compile-time HintPaths through `KSAFolder` with `Private=false`; never shipped[^directory-build-props] |
| KittenExtensions, ShaderExtensions, ModMenu | | Not used. Shader changes are made by prefixing the game's shader loader, and the UI is the mod's own window plus a menu-bar entry |
| Native libraries | | None; no P/Invoke. GPU work goes through the game's managed Vulkan wrappers |

## Architecture

**Supermod host pattern.** Every feature is written as an `ISubmod` (`Name`, `Tooltip`, `Initialize`,
`Update(dt)`, `RenderContent`, optional `RenderFloatingWindows`, `Dispose`) inside its own `.lib` project, so
the same class can be hosted by a one-feature development mod (windows on F8 to F12) or by the supermod. The host
holds a list of 31 submods, updates all of them every frame, renders the visible ones as collapsing headers
with a View menu for visibility and tooltips, and isolates each call in `try/catch`.[^ksa-abstractions-lib-isubmod-cs]
See [the ISubmod contract](./features/lib-isubmod-contract.md) and the
[standalone template](./features/fixme-mod-name-standalone-mod-template.md).

**One Harmony instance.** `Patcher.Patch()` creates `new Harmony("MeowSci.Unscience")` and applies each
feature's static patch class through `TryApply(name, action)`, so a game update that breaks one target logs
and disables only that feature. Shared, ordered infrastructure goes first: hotkey guard, hidden-UI hook,
render patches, menu bar, native save hooks and the physics handoff.[^unscience-patcher-cs]

**Shared library `ksa-abstractions.lib`.** Holds what several features must agree on: the
[`PrepareFrame` physics handoff](./features/lib-physics-frame-hook.md) (one transpiler that runs queued vessel
mutations after solver results are applied and before the next step is dispatched), the
[part render filter](./features/lib-part-render-filter.md) (one owner-registered prefix/postfix set on
`PartTreeRenderData.Compose*`), [IVA force render](./features/lib-iva-force-render.md), the
[hotkey guard](./features/lib-hotkey-guard.md), game-state providers, easing, XKCD colors, shared copied
PNG/GLB/sound libraries, and the whole persistence stack.[^ksa-abstractions-lib-readme-md]

**Threads and frame placement.** All feature state is owned by the main thread. Work that changes simulated
state mostly runs inside `Program.PrepareFrame` before the next step is dispatched: through the shared
handoff (`frame:prepare`) or a prefix on `Universe.ExecuteNextVehicleSolvers` / `ExecuteNextClothSolvers`
(`frame:dispatch-solvers`). The exceptions run from the UI phase, after dispatch: Blinky and It's So Shiny
rebuild part trees from a button click (a documented race), while DOH spawning and Sphinx's queued edits
first wait for the solver jobs. Render-side work either submits data before command recording (Pyro's
plumes in `OnPreRender`; part-instance filtering and render-data postfixes in `UpdateRenderingResources`;
canopy and kitten submissions at the start of `RenderGame`) or records its own draws into a specific phase
(Thug Life in the scene-opaque pass, Sphinx in the prepass and scene-opaque passes, Graffiti after the last
resolve before the map grid). A few patches run inside the vehicle worker or Bepu passes and are written
to be allocation-free and scoped to the mod's own objects. The only mod-owned background thread is Parts Now's loader task,
whose completion is polled from `Update(dt)`. The lifecycle column in the tables below gives each feature's
exact placement.

**Scene-save sidecar.** The host discovers 35 versioned JSON records from 30 submods, captures them in a
postfix on `GameSave.Populate`, writes `unscience.json` only after `UncompressedSave.Write` succeeded,
defers loads to the `PrepareFrame` handoff, resets old ownership before KSA destroys the world and replays
restores in a fixed order after it rebuilds it. Records that fail to restore are retained, not lost. See
[scene save sidecar](./features/unscience-scene-save-sidecar.md),
[native save hooks](./features/lib-native-save-hooks.md),
[coordinator](./features/lib-scene-save-coordinator.md) and
[part identity](./features/lib-save-part-identity.md).[^unscience-unsciencesaves-cs]

## Features

Grouped by sub-mod, host first. Thirty-one sub-mods are bundled into the shipped mod; Pebbles has no
standalone host, and two projects with their own `mod.toml` are not bundled: `jplrepo` (a menu-bar
experiment) and `fixme-mod-name` (the template new features are scaffolded from). Feature slugs carry the
sub-mod prefix; `lib-*` features belong to the shared `ksa-abstractions.lib`.

### unscience (supermod host)

[Sub-mod concept](./submods/unscience.md): The only shipped mod: one StarMap entry class that constructs all 31 bundled feature sub-mods, applies their patches through one Harmony instance, renders them in one toolbox window and adds a per-save scene sidecar.

| Feature | Category | Lifecycle |
|---|---|---|
| [One shipped mod folder from thirty-plus projects, with dist validation and rolling GitHub releases](./features/unscience-build-and-release.md) | testing-and-tooling | `startup:starmap-before-main` |
| [One Harmony instance applying 30-plus feature patch classes in isolation](./features/unscience-consolidated-patcher.md) | patching | `startup:all-mods-loaded` |
| [Native-free managed test executables that run production patches against stand-in KSA types](./features/unscience-managed-test-harnesses.md) | testing-and-tooling | `frame:prepare`, `event:save`, `event:load` |
| [Add an entry to the game's main menu bar through DrawProgramMenusHook](./features/unscience-menu-bar-entry.md) | ui | `frame:draw-ui` |
| [Per-save scene sidecar wiring and the participant inventory](./features/unscience-scene-save-sidecar.md) | persistence | `startup:all-mods-loaded`, `event:save`, `event:load`, `frame:prepare`, `frame:draw-ui` |
| [Supermod host that aggregates 31 feature panels into one window](./features/unscience-supermod-host.md) | getting-started | `startup:all-mods-loaded`, `frame:draw-ui` |

### Shared library: ksa-abstractions.lib

Infrastructure every sub-mod builds on; no sub-mod of its own.

| Feature | Category | Lifecycle |
|---|---|---|
| [Thin static providers that funnel game-state access through one library](./features/lib-game-state-providers.md) | getting-started | `frame:draw-ui` |
| [Keep per-frame mod work running while the HUD is hidden](./features/lib-hidden-ui-frame-hook.md) | patching | `frame:draw-ui` |
| [Block game hotkeys while an ImGui text field has focus](./features/lib-hotkey-guard.md) | ui | `frame:input` |
| [ISubmod contract for features that run standalone or inside a supermod](./features/lib-isubmod-contract.md) | getting-started | `startup:all-mods-loaded`, `frame:draw-ui` |
| [Force IVA interior models to render outside IVA](./features/lib-iva-force-render.md) | rendering | `frame:render`, `frame:draw-ui`, `editor:render` |
| [Native save and load hooks that bracket every KSA save transaction](./features/lib-native-save-hooks.md) | persistence | `event:save`, `event:load`, `frame:input`, `frame:draw-ui`, `frame:prepare` |
| [Hide individual part meshes by compacting the per-frame instance lists](./features/lib-part-render-filter.md) | rendering | `frame:render` |
| [Physics-safe vessel mutation at the PrepareFrame solver handoff](./features/lib-physics-frame-hook.md) | patching | `frame:prepare` |
| [Stable vehicle and part identity across save and reload](./features/lib-save-part-identity.md) | persistence | `event:save`, `event:load`, `frame:prepare` |
| [Scene save coordinator with ordered reset, deferred replay and retained records](./features/lib-scene-save-coordinator.md) | persistence | `event:save`, `event:load`, `frame:prepare` |
| [Shared copied-import media catalogs with an ImGui file browser](./features/lib-shared-file-library.md) | ui | `frame:draw-ui` |
| [Enumerate the game's 954 named XKCD colors by reflection](./features/lib-xkcd-color-catalog.md) | ui | `frame:draw-ui` |

### Blinky

[Sub-mod concept](./submods/blinky.md): Builds NxM grids of real engine parts on a live vessel at runtime and lights them as an LCD display by switching engine controllers, with feed wiring, patterns and scrolling text.

| Feature | Category | Lifecycle |
|---|---|---|
| [Feed runtime-created engines through their declared feed connectors](./features/blinky-engine-pixel-feed-wiring.md) | vehicles-and-parts | `frame:draw-ui`, `frame:prepare`, `event:load` |
| [Build NxM grids of real engine parts on a live vessel at runtime](./features/blinky-runtime-part-grid-builder.md) | vehicles-and-parts | `frame:draw-ui`, `frame:prepare`, `event:load` |
| [Drive a pixel display by switching engine controllers on and off](./features/blinky-scroll-animation-and-patterns.md) | vehicles-and-parts | `frame:draw-ui`, `frame:input`, `frame:render`, `event:load` |

### Bloomin' Onion

[Sub-mod concept](./submods/bloomin-onion.md): Defines brand-new planetary rings at runtime for any celestial body, with painted band textures, dust and rock-field settings, presets, and a renderer rebuild through the game's own settings path.

| Feature | Category | Lifecycle |
|---|---|---|
| [Procedural ring band textures uploaded through the game's own TextureReference.Bind](./features/bloomin-onion-painted-ring-textures.md) | rendering | `frame:draw-ui`, `frame:prepare`, `render:atmosphere` |
| [Add planetary rings to any celestial at runtime](./features/bloomin-onion-runtime-planetary-rings.md) | celestials | `frame:draw-ui`, `frame:prepare`, `render:atmosphere`, `render:scene-opaque` |

### BYO Music

[Sub-mod concept](./submods/byo-music.md): Attaches imported OGG, WAV or MP3 files to vessels as 3D sounds on KSA's own FMOD system, with seamless loops, real-time gaps between plays, live edits and scene saves.

| Feature | Category | Lifecycle |
|---|---|---|
| [Play imported sound files in 3D at a vessel through nonblocking FMOD streams on the game's Sfx group](./features/byo-music-vessel-3d-audio.md) | audio | `frame:draw-ui`, `frame:post-render`, `event:save`, `event:load`, `frame:prepare` |

### Camera Controller Override

[Sub-mod concept](./submods/camera-controller-override.md): Cinematic camera animations and keyframe sequences that pre-empt the stock Orbit and Fly camera controllers while a sequence plays, with eased target-relative moves and saved recipes.

| Feature | Category | Lifecycle |
|---|---|---|
| [Cinematic camera moves by pre-empting the Orbit and Fly controllers](./features/camera-controller-override-camera-animations.md) | camera | `frame:viewports`, `frame:draw-ui` |
| [Keyframe camera sequences with parallel groups, return-to-start and saved recipes](./features/camera-controller-override-keyframe-sequences.md) | camera | `frame:viewports`, `frame:draw-ui`, `event:save`, `event:load` |

### Dent Wizard

[Sub-mod concept](./submods/dent-wizard.md): Fires an existing vessel, EVA kitten or debris fragment from the camera at a clicked vessel or terrain point with target-relative speed, launching at the shared physics handoff.

| Feature | Category | Lifecycle |
|---|---|---|
| [Camera-origin vessel launcher with target-relative velocity](./features/dent-wizard-vessel-launcher.md) | physics | `frame:draw-ui`, `frame:prepare` |

### DOH (Dynamically Originating Hominids)

[Sub-mod concept](./submods/doh.md): Spawns EVA kittens on demand next to any vehicle, optionally with per-kitten tints made by cloning GPU materials inside the game's fixed 512-slot material pool.

| Feature | Category | Lifecycle |
|---|---|---|
| [Spawn EVA kittens anywhere by replicating EVADoor.CreateKittenEva](./features/doh-kitten-spawning.md) | kittens | `frame:draw-ui` |
| [Per-kitten tints by cloning GPU materials inside the fixed 512-slot material pool](./features/doh-runtime-gpu-materials.md) | kittens | `frame:draw-ui`, `event:save`, `event:load`, `frame:prepare` |

### Don't Stifle Me

[Sub-mod concept](./submods/dont-stifle-me.md): Vehicle-editor un-limiter: lifts the 0.5x-2x part scale clamp, restores per-axis gizmo scaling with optional unsnapped values, and can widen parachute diameter to 2-1000 m with exact restore of authored bounds.

| Feature | Category | Lifecycle |
|---|---|---|
| [Don't Stifle Me editor free scaling (unclamped, per-axis, unsnapped)](./features/dont-stifle-me-editor-free-scaling.md) | vehicles-and-parts | `editor:on-frame`, `editor:pre-render`, `frame:input`, `frame:draw-ui` |
| [Parachute diameter unclamp ("jpl said no clamps")](./features/dont-stifle-me-parachute-diameter-unclamp.md) | vehicles-and-parts | `editor:draw-ui`, `frame:draw-ui`, `event:save`, `event:load`, `event:unload` |

### Eternal Flame

[Sub-mod concept](./submods/eternal-flame.md): Keeps monitored vessels topped up with fuel and battery charge from a solver-dispatch prefix that runs before the vehicle worker snapshots module state.

| Feature | Category | Lifecycle |
|---|---|---|
| [Infinite fuel and battery refill before the vehicle solver snapshot](./features/eternal-flame-resource-refill.md) | vehicles-and-parts | `frame:dispatch-solvers` |

### fixme-mod-name (template)

[Sub-mod concept](./submods/fixme-mod-name.md): The repository's template mod pair, copied and renamed by mkmod.ts to start a new feature, showing the minimal StarMap host, Harmony patcher with hotkey guard and sibling library project.

| Feature | Category | Lifecycle |
|---|---|---|
| [Minimal standalone StarMap mod template with a separate feature library](./features/fixme-mod-name-standalone-mod-template.md) | getting-started | `startup:all-mods-loaded`, `frame:draw-ui` |

### Free Fallin

[Sub-mod concept](./submods/free-fallin.md): Applies one global look to every parachute canopy by swapping a runtime material into each canopy renderable as it draws, with tints, PBR controls, composited or projected PNG images and exact stock restore.

| Feature | Category | Lifecycle |
|---|---|---|
| [Swap every parachute canopy's material at draw time](./features/free-fallin-canopy-material-substitution.md) | vehicles-and-parts | `startup:all-mods-loaded`, `frame:draw-ui`, `event:load`, `frame:render` |
| [Material-gated full-canopy projection by patching KSA's model shaders in memory](./features/free-fallin-canopy-projection-shader.md) | shaders | `startup:all-mods-loaded`, `frame:prepare`, `frame:render` |
| [Runtime canopy textures from PNGs, CPU compositing over a BC7 stock texture, and owned GPU assets](./features/free-fallin-canopy-texture-compositing.md) | assets | `frame:draw-ui`, `event:load` |

### Garry's Torch

[Sub-mod concept](./submods/garrys-torch.md): Welds one vessel to a part of another by re-teleporting it every physics handoff, with per-axis part scaling, scoped collision suppression, eased animations, presets and a public API.

| Feature | Category | Lifecycle |
|---|---|---|
| [Non-cumulative per-axis part scaling with captured baselines](./features/garrys-torch-per-axis-part-scaling.md) | vehicles-and-parts | `frame:draw-ui`, `frame:prepare`, `frame:render` |
| [Per-vessel collision suppression by bracketing ConstraintSim passes](./features/garrys-torch-scoped-collision-suppression.md) | physics | `frame:prepare`, `physics:vehicle-solver`, `physics:bepu` |
| [Part-anchored vehicle welding by per-frame teleport at the physics handoff](./features/garrys-torch-vehicle-welding.md) | physics | `frame:prepare`, `frame:draw-ui` |
| [Eased weld animation queues and TOML weld presets](./features/garrys-torch-weld-animation-and-presets.md) | physics | `frame:prepare`, `frame:draw-ui` |

### Glass

[Sub-mod concept](./submods/glass.md): Overrides the main camera field of view with lens presets from 15 degree telephoto to 120 degree fisheye, kept in place by two main-camera-only Harmony prefixes.

| Feature | Category | Lifecycle |
|---|---|---|
| [Main-camera field-of-view override with lens presets](./features/glass-fov-override.md) | camera | `frame:draw-ui`, `frame:input`, `event:load` |

### Godzilla

[Sub-mod concept](./submods/godzilla.md): Resizes vessels and EVA kittens at runtime with Smart or Basic scaling and independent visual, collider and physics channels, all applied at the physics handoff and exactly restorable.

| Feature | Category | Lifecycle |
|---|---|---|
| [Collider-only scaling through native owned shapes with nominal bounds preserved](./features/godzilla-collider-scaling.md) | physics | `frame:prepare`, `physics:vehicle-solver`, `physics:bepu` |
| [Whole-vessel scaling with Smart and Basic modes and three independent channels](./features/godzilla-vessel-scaling.md) | vehicles-and-parts | `frame:prepare`, `frame:draw-ui` |
| [Render-only vessel scaling with draw-matrix and cull-radius patches](./features/godzilla-visual-scaling.md) | rendering | `frame:render`, `frame:prepare` |

### Graffiti

[Sub-mod concept](./submods/graffiti.md): Click-to-place projected PNG decals on vehicle hulls, deployed parachute canopies, EVA kittens and terrain, rendered in a custom Vulkan pass after the main view's final resolve.

| Feature | Category | Lifecycle |
|---|---|---|
| [PNG decals as mod-owned slots in KSA's bindless texture table](./features/graffiti-bindless-decal-textures.md) | rendering | `frame:draw-ui`, `event:load` |
| [Per-frame decal anchoring to parts, canopy cloth and terrain](./features/graffiti-decal-anchoring.md) | rendering | `frame:draw-ui` |
| [One-shot click placement with mesh, cloth, kitten and terrain picking](./features/graffiti-decal-placement-picking.md) | rendering | `frame:draw-ui` |
| [Projected PNG decal pass recorded after the main-view resolve](./features/graffiti-projected-decal-pass.md) | rendering | `startup:all-mods-loaded`, `frame:draw-ui`, `render:grid` |

### Hot Pursuit

[Sub-mod concept](./submods/hot-pursuit.md): Mounts live cameras on clicked vessel parts by leasing the stock secondary viewports and replacing the fixed camera controller only for those viewports, with mesh-precise placement and stable part anchoring.

| Feature | Category | Lifecycle |
|---|---|---|
| [Click-to-mount picking on vessel meshes with stable part anchoring](./features/hot-pursuit-mesh-ray-placement.md) | camera | `frame:draw-ui`, `frame:viewports` |
| [Live part-mounted cameras in leased stock secondary viewports](./features/hot-pursuit-part-mounted-cameras.md) | camera | `frame:viewports`, `frame:draw-ui`, `frame:render`, `event:save`, `event:load` |

### Humble Arteest

[Sub-mod concept](./submods/humble-arteest.md): Three recoloring tools on three render paths, per-part vehicle paint in free state-flag bits with an in-memory shader patch, kitten material tints and visor toggle, and engine heat-glow overrides.

| Feature | Category | Lifecycle |
|---|---|---|
| [Engine glow override through the dynamic part instance submission](./features/humble-arteest-engine-emissive.md) | rendering | `startup:all-mods-loaded`, `frame:draw-ui`, `frame:render` |
| [Kitten tinting by staged writes into the GPU material buffer](./features/humble-arteest-kitten-color.md) | kittens | `frame:draw-ui`, `event:load` |
| [Hide kitten helmet visors with a draw-call transpiler](./features/humble-arteest-kitten-visor-toggle.md) | kittens | `startup:all-mods-loaded`, `frame:draw-ui`, `frame:render` |
| [Per-part vehicle paint through free state-flag bits and an in-memory part shader patch](./features/humble-arteest-vehicle-paint-shader-patch.md) | shaders | `startup:all-mods-loaded`, `frame:draw-ui`, `frame:render` |

### I Feel Seen

[Sub-mod concept](./submods/i-feel-seen.md): Keeps selected vessels and EVA kittens rendered at any distance by bypassing the game's one-pixel cull with skip-original prefixes on two Vehicle render methods.

| Feature | Category | Lifecycle |
|---|---|---|
| [Bypass the one-pixel render cull for chosen vessels](./features/i-feel-seen-render-distance-override.md) | rendering | `frame:render`, `frame:draw-ui` |

### Iron Man

[Sub-mod concept](./submods/iron-man.md): Lets one live EVA kitten be edited in the stock vehicle editor with private attachment nodes, then flown as an ordinary rocket vessel with headward controls, native flight-computer gauges, early equipment rendering and upright surface teleports.

| Feature | Category | Lifecycle |
|---|---|---|
| [Per-instance attachment nodes that survive saves, copies and unload](./features/iron-man-connector-serialization.md) | persistence | `frame:prepare`, `event:save`, `event:load`, `event:part-copy`, `event:unload` |
| [Submit EVA kitten equipment before the part-instance upload](./features/iron-man-equipment-render-ordering.md) | rendering | `frame:render` |
| [Fly one EVA kitten as an ordinary rocket vessel](./features/iron-man-kitten-rocket-flight.md) | kittens | `frame:draw-ui`, `frame:prepare`, `frame:input`, `frame:dispatch-solvers`, `physics:vehicle-solver` |
| [Open the stock vehicle editor on a live EVA kitten](./features/iron-man-kitten-vehicle-editing.md) | kittens | `frame:prepare`, `frame:input`, `frame:viewports`, `editor:on-frame`, `editor:render` |
| [Upright surface teleport for Iron Man kittens by adapting one placement call](./features/iron-man-surface-teleport-correction.md) | kittens | `frame:draw-ui`, `frame:input` |

### It's So Shiny

[Sub-mod concept](./submods/its-so-shiny.md): Builds Blinky-style pixel grids from stock light parts instead of engines, powered through battery connections and switched with the stock light switch, with pattern, static and scroll controls.

| Feature | Category | Lifecycle |
|---|---|---|
| [Light-part pixel grids switched through stock light switches](./features/its-so-shiny-light-part-grids.md) | rendering | `frame:draw-ui`, `frame:render`, `physics:vehicle-solver`, `event:load` |

### jplrepo

[Sub-mod concept](./submods/jplrepo.md): A tiny development tribute mod that animates cheer phrases on KSA's top menu bar using a call-site transpiler on Program.DrawMenuBar and a prefix on DrawProgramMenusHook.

| Feature | Category | Lifecycle |
|---|---|---|
| [Animated menu-bar text via a call-site transpiler and a hook prefix](./features/jplrepo-menu-bar-animation.md) | ui | `frame:draw-ui` |

### Kitchen Sink

[Sub-mod concept](./submods/kitchen-sink.md): A grab bag of small KSA fixes: per-vessel G-load invincibility, a free-flying IVA camera, a see-into-the-capsule rendering experiment, forced IVA interior rendering and an editor refresh for invisible sub-parts.

| Feature | Category | Lifecycle |
|---|---|---|
| [See into the stock capsule by hiding its two opaque window models](./features/kitchen-sink-capsule-glass.md) | rendering | `frame:draw-ui`, `frame:render`, `event:save`, `event:load` |
| [Kitchen Sink G-load protection for exact vessel instances](./features/kitchen-sink-g-load-protection.md) | physics | `physics:vehicle-solver`, `frame:draw-ui`, `event:save`, `event:load` |
| [Free-flying IVA camera by swapping the main viewport's IVA controller](./features/kitchen-sink-iva-camera-unlock.md) | camera | `frame:viewports`, `frame:input`, `frame:controller`, `frame:draw-ui`, `frame:render`, `event:save`, `event:load` |

### Kitten Animations

[Sub-mod concept](./submods/kitten-animations.md): Plays any loaded animation clip on a selected EVA kitten against the game's per-frame clip choice, triggers facial expressions through a mod-owned processor, and tunes animation strength and locomotion animation settings.

| Feature | Category | Lifecycle |
|---|---|---|
| [Catalog every animation clip, blend sampler and anim processor a live kitten has loaded](./features/kitten-animations-clip-catalog.md) | kittens | `frame:draw-ui` |
| [Facial expressions through a mod-owned CatExpressionAnim, plus anim-processor strength overrides](./features/kitten-animations-facial-expressions.md) | kittens | `frame:draw-ui`, `frame:render` |
| [Force any animation clip on an EVA kitten from a prefix on AnimatedRenderable.UpdateAnimation](./features/kitten-animations-forced-clip-playback.md) | kittens | `frame:draw-ui`, `frame:render` |
| [Live animation tuning on KittenLocomotionTuning.Current with a scoped reset and a scene-save record](./features/kitten-animations-locomotion-tuning.md) | kittens | `frame:draw-ui`, `frame:render`, `frame:dispatch-solvers`, `event:save`, `event:load`, `frame:prepare` |

### Kiwi's Marbles

[Sub-mod concept](./submods/kiwis-marbles.md): Welds planets and moons to other celestials or vessels at a CCI-frame offset by rewriting their orbits in a solver-dispatch prefix, with explicit parent-tree fix-ups, weld chains and scene-save restore.

| Feature | Category | Lifecycle |
|---|---|---|
| [Celestial welding by rewriting a body's orbit before every sim step](./features/kiwis-marbles-celestial-welding.md) | celestials | `frame:dispatch-solvers`, `frame:draw-ui`, `event:save`, `event:load` |
| [Cross-parent celestial welds with explicit IParentBody.Children re-parenting](./features/kiwis-marbles-cross-parent-reparenting.md) | celestials | `frame:dispatch-solvers`, `event:load` |

### Parts Now

[Sub-mod concept](./submods/parts-now.md): Loads, reloads and unloads Part and SubPart mods in a running game, from pasted XML or an existing mod folder, by replaying KSA's boot asset pipeline one state per frame behind validation, a reflection self-test and a fail-closed purge.

| Feature | Category | Lifecycle |
|---|---|---|
| [Validate Part asset bundles with fifteen rules before anything is written or registered](./features/parts-now-bundle-validation.md) | assets | `frame:draw-ui` |
| [Reserve headroom in KSA's shared interleaved mesh buffer before it is allocated](./features/parts-now-mesh-headroom-reservation.md) | rendering | `startup:all-mods-loaded`, `frame:draw-ui` |
| [Turn pasted Part XML into a real, enabled KSA mod folder from inside the game](./features/parts-now-paste-xml-mod-writer.md) | assets | `frame:draw-ui` |
| [One reflection layer with a startup self-test that disables loading instead of crashing](./features/parts-now-reflection-self-test.md) | patching | `startup:all-mods-loaded`, `frame:draw-ui` |
| [Load, reload and roll back Part mods at runtime with a one-state-per-frame loader](./features/parts-now-runtime-part-loading.md) | assets | `frame:draw-ui`, `thread:background` |
| [Unload runtime-registered parts behind a fail-closed safety gate](./features/parts-now-runtime-unload-purge.md) | assets | `frame:draw-ui` |
| [Render part-browser thumbnails at runtime on KSA's offscreen thumbnail viewport](./features/parts-now-thumbnail-generation.md) | rendering | `frame:draw-ui` |

### Pebbles

[Sub-mod concept](./submods/pebbles.md): Replaces the meshes, materials, scale and colliders of chosen ground clutter types on one celestial with stock or imported GLB models, authored in a floating collider workshop and committed at the solver handoff with exact restore.

| Feature | Category | Lifecycle |
|---|---|---|
| [Cancellable one-shot GPU upload submission with publish-after-fence and retire-after-idle](./features/pebbles-asset-upload-submission.md) | rendering | `frame:draw-ui`, `frame:dispatch-solvers` |
| [Authored clutter colliders mapped onto native collider templates with a private convex-hull subclass](./features/pebbles-clutter-colliders.md) | physics | `frame:dispatch-solvers`, `physics:vehicle-solver`, `physics:bepu` |
| [Removed and displaced clutter carried per grid spacing across swaps and saves](./features/pebbles-clutter-state-persistence.md) | persistence | `frame:dispatch-solvers`, `event:save`, `event:load` |
| [Bounded GLB 2.0 import into unregistered CPU-only meshes with frozen path and hash ids](./features/pebbles-glb-import.md) | assets | `frame:draw-ui`, `frame:dispatch-solvers` |
| [GLB PBR materials converted to native clutter maps with private bindless textures](./features/pebbles-glb-material-conversion.md) | assets | `frame:draw-ui`, `frame:dispatch-solvers` |
| [Per-celestial ground clutter replacement with transactional apply and restore](./features/pebbles-ground-clutter-replacement.md) | celestials | `frame:dispatch-solvers`, `frame:draw-ui`, `event:renderer-rebuild`, `event:load` |
| [Private clutter material buffer and source-color shader variant scoped to owned construction](./features/pebbles-private-clutter-materials.md) | rendering | `frame:dispatch-solvers`, `event:renderer-rebuild` |
| [Floating collider workshop with a textured preview, CPU-projected gizmos and detached undo history](./features/pebbles-workshop-editor.md) | ui | `frame:draw-ui`, `render:imgui` |
| [Independent offscreen Vulkan preview rendered before the GUI and shown as an ImGui image](./features/pebbles-workshop-offscreen-preview.md) | rendering | `frame:draw-ui`, `render:imgui` |

### Pyro

[Sub-mod concept](./submods/pyro.md): Places engine-free volumetric exhaust plumes on any vessel part by submitting mod-owned exhaust instances into the game's own plume batch, with nozzle physics, per-plume look overrides, a template editor, presets and on/off cycles.

| Feature | Category | Lifecycle |
|---|---|---|
| [Edit the game's shared exhaust templates and refresh every live plume](./features/pyro-exhaust-template-editor.md) | rendering | `frame:draw-ui`, `frame:pre-render`, `render:exhaust`, `event:save`, `event:load` |
| [Synthesize plume physics from nozzle parameters with an isentropic model](./features/pyro-isentropic-nozzle-model.md) | rendering | `frame:pre-render` |
| [Override one caller's data at a shared game method with a scoped prefix](./features/pyro-scoped-exhaust-look-override.md) | patching | `frame:pre-render` |
| [Submit engine-free volumetric exhaust plumes into the game's plume batch](./features/pyro-volumetric-exhaust-plumes.md) | rendering | `frame:pre-render`, `render:exhaust`, `frame:draw-ui`, `event:load` |

### Rocky McRock Face

[Sub-mod concept](./submods/rocky-mcrock-face.md): Swaps the meshes, textures and rock-field settings of existing planetary rings at runtime by mutating the ring reference tree and rebuilding the ring renderer, with a catalog of every loaded mesh and texture.

| Feature | Category | Lifecycle |
|---|---|---|
| [Enumerate every loaded mesh and bound texture from ModLibrary's internal registries](./features/rocky-mcrock-face-asset-catalog.md) | assets | `frame:draw-ui`, `frame:prepare` |
| [Clone an atlas-interleaved part mesh into a Simple mesh the ring renderer can draw](./features/rocky-mcrock-face-interleaved-mesh-clone.md) | rendering | `frame:draw-ui`, `frame:prepare`, `render:scene-opaque` |
| [Swap the meshes and textures of existing planetary rings at runtime](./features/rocky-mcrock-face-ring-asset-swap.md) | celestials | `frame:draw-ui`, `frame:prepare`, `render:scene-opaque`, `render:atmosphere` |

### Save Yourself

[Sub-mod concept](./submods/save-yourself.md): Opt-in timed auto saves that write ordinary native KSA saves through the game's own save path every N wall-clock seconds, with a sanitised name prefix and a global TOML preference file.

| Feature | Category | Lifecycle |
|---|---|---|
| [Timed auto saves through the game's own save path](./features/save-yourself-timed-autosave.md) | persistence | `frame:draw-ui`, `event:save` |

### Skittles

[Sub-mod concept](./submods/skittles.md): Global ImGui theme manager that re-themes every game and mod window through ImGui.GetStyle(), with built-in schemes, a live style editor, TOML theme files and scene-saved styles.

| Feature | Category | Lifecycle |
|---|---|---|
| [Global ImGui themes by rewriting ImGui.GetStyle(), with TOML theme files and scene-saved styles](./features/skittles-imgui-theme-manager.md) | ui | `startup:all-mods-loaded`, `frame:draw-ui`, `event:save`, `event:load`, `frame:prepare` |

### Sphinx

[Sub-mod concept](./submods/sphinx.md): Places imported textured GLBs as body-fixed statics with automatic box or mesh colliders, live transforms and UV mapping, drawn through the game's static-object pipelines from three render postfixes and restored by scene saves.

| Feature | Category | Lifecycle |
|---|---|---|
| [Body-fixed GLB statics placed by terrain click or beside a vessel](./features/sphinx-glb-statics-placement.md) | celestials | `frame:draw-ui`, `frame:render`, `event:save`, `event:load` |
| [Automatic box or mesh colliders for imported statics with scoped Bepu hooks](./features/sphinx-static-colliders.md) | physics | `physics:bepu`, `physics:vehicle-solver`, `frame:draw-ui` |
| [Drawing private meshes through the native static-object pipelines with three postfixes](./features/sphinx-static-object-render-hooks.md) | rendering | `frame:render`, `render:prepass`, `render:scene-opaque` |
| [Live per-static UV scale and offset with shared-PNG texture overrides](./features/sphinx-uv-texture-overrides.md) | rendering | `frame:draw-ui`, `frame:render`, `event:save`, `event:load` |

### The Tick

[Sub-mod concept](./submods/the-tick.md): Makes chosen vessels indestructible by forces with a crash-tolerance getter prefix and a worker-thread discard of structural destruction events, leaving shared part templates untouched.

| Feature | Category | Lifecycle |
|---|---|---|
| [Per-vessel indestructibility without touching shared part templates](./features/the-tick-vessel-indestructibility.md) | physics | `physics:vehicle-solver`, `physics:bepu`, `frame:draw-ui` |

### Thug Life

[Sub-mod concept](./submods/thug-life.md): Draws the pixel-art sunglasses meme as a textured quad anchored to any vessel part or kitten face, recorded with stock unlit shaders inside the scene's opaque pass from a postfix on SuperMeshRenderSystem.RenderMainPass.

| Feature | Category | Lifecycle |
|---|---|---|
| [Upload a procedural pixel-art texture and cut it out with geometry](./features/thug-life-procedural-texture.md) | rendering | `frame:draw-ui` |
| [Draw a part-anchored textured quad inside the scene's opaque pass](./features/thug-life-textured-quad-render-pass.md) | rendering | `render:scene-opaque`, `frame:draw-ui`, `startup:all-mods-loaded` |

### Zippo

[Sub-mod concept](./submods/zippo.md): Selects vessels and light parts, writes light intensity, color and switch state, queues eased transitions and runs Disco party-light recipes on private per-instance light templates, with a public API and scene-save restore.

| Feature | Category | Lifecycle |
|---|---|---|
| [Disco party lights with per-instance light templates and owned actuators](./features/zippo-disco-party-lights.md) | rendering | `frame:draw-ui`, `frame:render`, `physics:vehicle-solver`, `event:save`, `event:load` |
| [Per-light queues of eased color and intensity transitions](./features/zippo-light-animation-queue.md) | rendering | `frame:draw-ui`, `frame:render`, `event:save`, `event:load` |
| [Light part selection and template-level color and intensity writes with a public API](./features/zippo-light-control.md) | rendering | `frame:draw-ui`, `frame:render`, `physics:vehicle-solver`, `event:save`, `event:load` |

## Build, test and tooling

- `dotnet build` of `ksa-mod-experiments.slnx` compiles every project; only `unscience/unscience.csproj`
  deploys, copying its `MeowSci.*` reference closure, `Tomlyn.dll` and licenses into the KSA mods folder.
  `KSAFolder` comes from `KSA_DLL_DIR`, a sibling `ksa-game-assemblies` checkout, or per-OS defaults.
  See [build and release](./features/unscience-build-and-release.md).
- GitHub Actions builds every push, validates the distribution with `scripts/check-dist.py` and publishes
  rolling `1.<run>.0` releases from `main` and `-beta` prereleases from `feature/*`.[^github-workflows-release-yml]
- 18 native-free managed test executables link production sources against stand-in `KSA` types and
  apply the real Harmony patches; see [managed test harnesses](./features/unscience-managed-test-harnesses.md).
- `mkmod.ts` scaffolds a new feature from the `fixme-mod-name` template.
- The repository's agent rules require keeping `scope/` (the integration map), `REPOSITORY_INDEX.md`, READMEs
  and save coverage in sync with every integration change.[^agents-md]

## Compatibility notes

- The root README says the checkout compiles against KSA **2026.10.7.5541**; the last full integration
  audit was **2026.9.22.5482**, and in-game acceptance of many 5482 changes is listed as pending (the
  authors could not run the Windows-only game). This bundle's engine claims were checked against build
  **2026.10.10.5554**.[^readme-md][^scope-full-scope-md]
- Known breakage patterns recorded by the authors across upgrades: private fields and methods reached by
  reflection or `AccessTools` (renamed or moved), transpiler call seams in `Program.PrepareFrame`, render
  hooks whose target methods were removed (5482 replaced per-module `UpdateRenderData` prefixes with the
  shared `PartRenderFilter` on `PartTreeRenderData.Compose`), Vulkan descriptor and enum migrations (5438,
  5541), shader `#include` headers and per-instance flag bits, and a native save path that changed
  return type (`Write()` returns `bool` since 5482).[^plans-ksa-5482-upgrade-md]
- The authors' upgrade workflow and per-area risk notes are in `scope/FULL_SCOPE.md`; the master index of
  every game member touched is `scope/game-integration-surface.md`. Known gaps are tracked in `ISSUES.md`.
- The checkout contains a stale `decomp/` copy of older game sources; it is not authoritative.

## Secondary sources

Analysis documents inside the repository worth reading alongside this bundle:

| Document | What it covers |
|---|---|
| [REPOSITORY_INDEX.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/REPOSITORY_INDEX.md) | Every project with its feature list, classes and version notes |
| [scope/FULL_SCOPE.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/FULL_SCOPE.md) | Version baseline, game-update workflow, table of area files |
| [scope/game-integration-surface.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/game-integration-surface.md) | Master cross-reference of every game type, member, reflection string, shader and asset used |
| [scope/00-architecture-and-abstractions.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/00-architecture-and-abstractions.md) | Host, consolidated Harmony, StarMap lifecycle map, shared library |
| [scope/vehicle-physics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/vehicle-physics.md) | Dent Wizard, Eternal Flame, Garry's Torch, Godzilla, I Feel Seen, The Tick; solver timing |
| [scope/celestial-and-lights.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/celestial-and-lights.md) | Kiwi's Marbles and Zippo |
| [scope/camera.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/camera.md) | Camera controllers, FOV, secondary viewports, IVA camera |
| [scope/pixel-grids-and-render.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/pixel-grids-and-render.md) | Blinky, It's So Shiny, Thug Life, render filter |
| [scope/character-and-materials.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/character-and-materials.md) | Doh, Humble Arteest, Kitten Animations |
| [scope/part-editor-and-robotics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/part-editor-and-robotics.md) | Parts Now, Don't Stifle Me |
| [scope/iron-man.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/iron-man.md) | Iron Man |
| [scope/exhaust-plumes.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/exhaust-plumes.md) | Pyro |
| [scope/decals.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/decals.md) | Graffiti |
| [scope/parachutes.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/parachutes.md) | Free Fallin |
| [scope/ground-clutter.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/ground-clutter.md) and [scope/ground-clutter-glb-materials.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/ground-clutter-glb-materials.md) | Pebbles |
| [scope/statics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/statics.md) | Sphinx |
| [scope/rings.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/rings.md) | Bloomin' Onion, Rocky McRock Face |
| [scope/saves.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/saves.md) | Save and load integration across all features |
| [scope/audio.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/audio.md), [scope/ui-customization.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/ui-customization.md), [scope/standalone-mods.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/standalone-mods.md) | BYO Music; Skittles and UI; standalone hosts |
| [plans/SAVES.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/plans/SAVES.md), [plans/saves-state-inventory.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/plans/saves-state-inventory.md), [plans/saves-acceptance.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/plans/saves-acceptance.md) | Save design, per-feature state inventory, acceptance status |
| [plans/KSA_5482_UPGRADE.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/plans/KSA_5482_UPGRADE.md) | Most recent full upgrade evidence (other `plans/KSA_*` files cover earlier builds) |
| [unscience/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/README.md) | Host usage, scene-save behavior and failure messages |

## See also

- [Sub-mod concepts](./submods/index.md) and [feature concepts](./features/index.md)

[^readme-md]: README.md
[^repository-index-md]: REPOSITORY_INDEX.md
[^ksa-abstractions-lib-ksapaths-cs]: ksa-abstractions.lib/KsaPaths.cs
[^unscience-mod-cs]: unscience/Mod.cs
[^starmap-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^starmap-core]: StarMap.Core/StarMapCore.cs
[^starmap-baseattributes]: StarMap.API/BaseAttributes.cs
[^starmap-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^starmap-gamesurveyer]: StarMap/GameSurveyer.cs
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^starmap-mod-alc]: StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
[^directory-build-props]: Directory.Build.props
[^ksa-abstractions-lib-isubmod-cs]: ksa-abstractions.lib/ISubmod.cs
[^unscience-patcher-cs]: unscience/Patcher.cs
[^ksa-abstractions-lib-readme-md]: ksa-abstractions.lib/README.md
[^unscience-unsciencesaves-cs]: unscience/UnscienceSaves.cs
[^github-workflows-release-yml]: .github/workflows/release.yml
[^agents-md]: AGENTS.md
[^scope-full-scope-md]: scope/FULL_SCOPE.md
[^plans-ksa-5482-upgrade-md]: plans/KSA_5482_UPGRADE.md
