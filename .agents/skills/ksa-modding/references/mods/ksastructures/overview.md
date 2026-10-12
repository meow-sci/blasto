---
type: Mod
title: KSAStructures
description: A StarMap code mod that stands a walkable office tower on Earth as launch-pad static objects dug into the terrain at load, with a lift, an underground garage lit by shadow-casting lamps, a spiral ramp, a chair and computer that fly the nearest craft, and a first-person kitten view
mod: ksastructures
tags: [buildings, static-objects, terrain, lift, garage, lighting, first-person, harmony, starmap]
repo: KSAStructures
license: MIT
author: LaurensDeV
loader: starmap
mod_id: KSAStructures
---

KSAStructures, by LaurensDeV, adds buildings to Kitten Space Agency that a kitten can walk into
and a car can drive under. There is one: the **Kessler Systems office tower**, 190 m west of
LC-39A on Earth, with a lift between garage, lobby and office, an underground garage reached by an
open ramp, a deep spiral ramp down a shaft into a hall 54 m below ground, ceiling lamps that cast
shadows, an office chair, a computer that flies the nearest craft, and a first-person view for a
kitten on foot.[^mod-readme] It is a sibling of KSACars, KSAGolf and KSArmory and shares their
tooling style; shared code is vendored rather than referenced.[^mod-claude]

The mod is unusually dense in engine technique for its size (43 C# files, about 7.5k lines): almost
every feature works around a specific engine limit, and the repository's `CLAUDE.md` records those
limits as "engine facts". This bundle verifies them against the decompiled build 2026.10.10.5554.

## Install footprint

`mod.toml` serves as both the content-mod manifest and the StarMap manifest:[^mod-modtoml]

```toml
# src/KSAStructures/mod.toml L1-L12
# KSA reads this as a content mod, StarMap reads it as a code mod. One folder does both.

name = "KSAStructures"

# Asset definitions, listed one by one rather than globbed: a file left out of this list is never
# loaded, and nothing reports it. They sit at the mod root beside Meshes/, Textures/ and Shaders/,
# so their relative paths resolve the same whether read against the mod root or the XML's own
# directory.
assets = ["KSAStructuresOffice.xml"]

[StarMap]
EntryAssembly = "KSAStructures"
```

| Path in the installed mod folder | What |
|---|---|
| `KSAStructures.dll`, `mod.toml` | Code and manifest; manifest id `KSAStructures` (`[[mods]] id = "KSAStructures"` in the user `manifest.toml`) |
| `KSAStructuresOffice.xml` | Static objects and colliders, materials, the chair and monitor parts, the `Structures` editor tag, the `<Shader>` asset |
| `Meshes/` | `KSAOffice.glb` (authored atlas), `KSAOfficeDeep.glb` (generated), `KSAOffice_ShadowCaster.glb` and `KSAOffice_DeepCaster.glb` (never drawn) |
| `Textures/` | 24 PNGs, one set per material, plus the pit height map |
| `Shaders/KSAStructuresDark.comp` | The compute shader for the underground dark pass |
| `Shaders/Content/KSAStructuresCore.glsl` | Written at every load; absolute includes into the install's shader library |
| `office-vault.txt` (optional) | A depth over 10 m digs a vault under the lift at load |
| `developer` (developer installs only) | Starts the file bridge |

The mod writes its log to `Logs/KSAStructures.log` in the KSA user folder. Because the site is dug
into Earth's terrain while the game loads, enabling or disabling the mod needs a restart.[^mod-readme]

## Loader model and entry class

StarMap loads `KSAStructures.dll` and instantiates the `[StarMapMod]` class
`KSAStructures.KSAStructuresMod`.[^mod-entry] Where each StarMap attribute fires is defined by
StarMap's Harmony patches on `Program`, `ModLibrary` and `Mod`.[^starmap-program]

| Hook | Fires at | What the mod does |
|---|---|---|
| `[StarMapImmediateLoad]` | prefix on `Mod.PrepareSystems` | Write the core shader include header; remember its `Mod`; log versions |
| `[StarMapAllModsLoaded]` | postfix on `ModLibrary.LoadAll` | `Office.Place` (dig the pits, add the two landmarks, install the office patches); the `Program.OnFrameCelestials` and `Program.OnGameLoaded` patches; first-person hooks; the dark pass; the bridge (developer only); the UI |
| `[StarMapBeforeGui]` | prefix on `Program.OnDrawUiFrame` | Menu entry, drawn in Dear ImGui's own main menu bar rather than KSA's (see [settings menu](features/settings-menu.md)) |
| `[StarMapAfterGui]` | postfix on `Program.OnDrawUiViewports` | The step (latched), settings window, V, Enter, lift keys, the monitor picture |
| `[StarMapAfterOnFrame]` | postfix on `Program.OnFrame` | Step fallback, bridge, release the frame latch |
| `[StarMapUnload]` | after StarMap's own `UnpatchAll()` at exit | Hand views back, restore KSA's fixed camera controller, flush the log (its unpatch calls are no-ops by then) |

Harmony patches, all under ids beginning `com.kesslersystems.ksastructures.`:[^mod-claude]

| Id | Target | Kind | Feature |
|---|---|---|---|
| `office` | `LocationReference.UpdateStaticObjectRenderData` | postfix | [Landmarks](features/office-landmarks-static-objects.md), [lift](features/lift-moving-landmark.md), [lamps](features/garage-spot-lights.md) |
| `office` | `Vehicle.PrepareWorker(SimStep)` | prefix (first) and finalizer (last) | [Terrain height](features/terrain-height-roof-override.md), [lift](features/lift-moving-landmark.md) |
| `office` | `Celestial.GetTerrainHeightFromDirCce(double3, bool)` | postfix | [Terrain height](features/terrain-height-roof-override.md) |
| `office` | `Vehicle.TeleportToLocation(Celestial, double, double)` | prefix and finalizer | [Terrain height](features/terrain-height-roof-override.md) |
| `office` | `SuperMeshRenderSystem.AddPoolToShadowBucket`, `SuperMeshShadowCasterProvider.PrepareShadowCasters` | prefix | [Shadow casters](features/invisible-shadow-casters.md) |
| `office` | `PhysicsEnvironment.RecomputePositionalValues`, `OceanRenderer.Render`, `RenderUnderwater`, `GetMaxVisibleDepth` | postfix / prefix | [Dry volume](features/dry-volume-below-sea-level.md) |
| `office` | `GroundClutterPlacementData.PopulateLaunchPadExclusionZones` (private) | postfix | [Clutter zones](features/ground-clutter-exclusion-zones.md) |
| `office` | `Universe.SerializeSystems` | postfix | [Furniture](features/office-furniture-craft.md) |
| `darkpass` | `SunbloomRenderer.Render(CommandBuffer, IViewport, int)` | prefix (first) | [Dark pass](features/underground-dark-compute-pass.md) |
| `firstperson` | `KittenEva.UpdateRenderData`, `AnimatedRenderable.Draw`, `StaticMeshRenderable.Draw`, `KittenLocomotion.StepGrounded` | prefix and postfix | [First person](features/first-person-kitten-view.md), [body and shadow](features/first-person-body-hiding-and-shadow.md) |
| `prerender` | `Program.OnFrameCelestials(double)` (private) | prefix | [Frame plumbing](features/frame-step-plumbing.md) |
| `worldreload` | `Program.OnGameLoaded()` | postfix | [Save loads](features/save-load-detection.md) |

## Dependencies

- **StarMap** loads the mod and ships `0Harmony.dll`, so patching needs nothing else installed.
  All game and loader assemblies are compile-only references (`Private="false"`).[^mod-csproj]
- **No KittenExtensions or ShaderExtensions.** The compute pass hooks KSA's own renderer directly,
  and the shader is a plain `<Shader>` asset compiled by KSA.
- **ModMenu (optional)** lists the mod through a copied attribute; nothing references it.
- **.NET 10 SDK** to build (`net10.0`); the game's assemblies come from `KSA_DLL_DIR`, `Import/`, a
  sibling `ksa-game-assemblies` checkout, or the install.

## Architecture

- **`Sim/` and `Ksa/`.** `Sim/` holds pure logic with no KSA types (step gate, frame latch, head
  look, roll stabilisation, the deep ramp's floors, bridge parsing, settings) and is linked
  wholesale into the headless tests; `Ksa/` binds to the game.[^mod-claude]
- **One facade for engine contact.** `KsaWorld` holds the clock, vehicle census, main-view borrowing
  and camera windows, so a game update breaks one file.[^mod-ksaworld]
- **The office** is one `partial class Office` across `Office.cs` (landmarks, render postfix, frames),
  `OfficeTerrain.cs` (decals, rim, terrain height, teleport), `OfficeLift.cs` and `OfficeFurniture.cs`,
  with `Underground` (lamps, casters), `OfficeDry` (ocean) and `ClutterZones` beside it.
- **Threads.** Main thread for everything in the step and the render postfixes; the vehicle worker
  for the `StepGrounded` and `RecomputePositionalValues` patches (handed data through `volatile`
  fields); a thread-pool timer for log flushing.
- **Data flow per frame.** `PrepareFrame` dispatches solvers (the `PrepareWorker` prefix moves lift
  riders) -> viewports (the replacement fixed controller asks the borrowed views for their pose) ->
  GUI pass (the step: lift, furniture, rim; keys; cameras; the vehicle step is already in flight) ->
  lights cleared -> `OnFrameCelestials` (the step again, only if the GUI pass was skipped) ->
  `Program.Render`: `UpdateRenderingResources` (render-data postfix draws the car and tower and submits
  lamps, per visible viewport) then `RenderGame` (shadow casters into the pool, dark pass before bloom,
  ocean skipped inside the dry volume) -> frame postfix (bridge).[^ksa-program]

## Features

| Feature | Category | Lifecycle |
|---|---|---|
| [Launch-pad landmarks that stand a walkable building on Earth](features/office-landmarks-static-objects.md) | celestials | startup:all-mods-loaded, frame:render, physics:bepu |
| [Terrain decal pits dug at load](features/terrain-decal-pits.md) | celestials | startup:all-mods-loaded |
| [Ground clutter kept off the launch pads nearest the camera](features/ground-clutter-exclusion-zones.md) | celestials | startup:all-mods-loaded, startup:game-loaded |
| [Terrain slope displacement held down live](features/live-terrain-material-override.md) | celestials | frame:draw-ui, frame:celestials, frame:after-on-frame |
| [Terrain height answered as roof, ramp or floor](features/terrain-height-roof-override.md) | physics | frame:dispatch-solvers, frame:viewports, frame:celestials |
| [A lift built from a moving launch-pad landmark](features/lift-moving-landmark.md) | physics | frame:draw-ui, frame:dispatch-solvers, frame:render, physics:bepu |
| [Garage spot lights submitted per viewport](features/garage-spot-lights.md) | rendering | startup:all-mods-loaded, frame:draw-ui, frame:render |
| [Invisible shadow casters](features/invisible-shadow-casters.md) | rendering | startup:all-mods-loaded, frame:render, render:shadows |
| [Underground dark compute pass](features/underground-dark-compute-pass.md) | shaders | startup:all-mods-loaded, render:bloom |
| [Core shader library include](features/core-shader-library-include.md) | shaders | startup:immediate-load, startup:asset-load, frame:after-on-frame |
| [Dry volume below sea level](features/dry-volume-below-sea-level.md) | celestials | startup:all-mods-loaded, physics:vehicle-solver, frame:draw-ui, render:planet, render:ocean |
| [Office furniture as single-part craft, kept out of saves](features/office-furniture-craft.md) | vehicles-and-parts | frame:draw-ui, event:save |
| [Fly the nearest craft from the office computer](features/computer-flies-nearest-craft.md) | vehicles-and-parts | frame:draw-ui, frame:viewports, event:key |
| [Live viewport image on an in-world monitor](features/viewport-image-on-monitor.md) | rendering | frame:draw-ui, frame:celestials, frame:after-on-frame |
| [A replacement fixed camera controller](features/level-horizon-camera-controller.md) | camera | frame:viewports, frame:input |
| [First-person view for a kitten on foot](features/first-person-kitten-view.md) | camera | frame:draw-ui, frame:viewports, physics:vehicle-solver |
| [First-person body hiding with a full-body shadow](features/first-person-body-hiding-and-shadow.md) | kittens | frame:draw-ui, render:prepass, render:shadows |
| [One step per frame across three hooks](features/frame-step-plumbing.md) | getting-started | frame:draw-ui, frame:celestials, frame:after-on-frame |
| [Noticing a save load with a Program.OnGameLoaded postfix](features/save-load-detection.md) | persistence | startup:game-loaded, event:save |
| [Settings menu, window and ModMenu listing](features/settings-menu.md) | ui | frame:draw-ui |
| [Per-mod log file](features/mod-log-file.md) | getting-started | startup:immediate-load, thread:background |
| [Developer file bridge and MCP server](features/dev-file-bridge-and-mcp.md) | testing-and-tooling | startup:all-mods-loaded, frame:after-on-frame |
| [Headless tests, assembly resolution and CI](features/headless-tests-and-ci.md) | testing-and-tooling | build time |
| [Asset XML, mesh and shader checks](features/asset-and-mesh-validation-tools.md) | testing-and-tooling | build time |
| [Generated spiral ramp](features/generated-deep-ramp.md) | assets | build time, startup:asset-load |

## Build, test and tooling

```bash
./tools/build.sh        # .NET 10, resolves the game's assemblies
./tools/test.sh         # headless xUnit tests over Sim/
./tools/check-all.sh    # everything CI runs; also the pre-push hook
./tools/deploy.sh       # build, validate parts, install, mark as developer
./tools/package.sh      # release zip: no symbols, no game DLLs
```

`tools/model/office-deep.py` regenerates the deep ramp; `tools/validate-parts.py`,
`tools/model/checkmesh.py`, `tools/meshinfo.py`, `tools/check-xml.sh` and `tools/check-shaders.sh`
check assets offline; `tools/ksa-mcp/server.py` and `tools/vis/vis.py` drive and judge a running
game. See [headless tests and CI](features/headless-tests-and-ci.md),
[asset checks](features/asset-and-mesh-validation-tools.md) and
[the bridge](features/dev-file-bridge-and-mcp.md).

## Compatibility notes

- **Targets KSA 2026.10.7.5541** (`ksa-assemblies.lock`, README).[^mod-lock] Claims here were
  checked against 2026.10.10.5554, where every patched member exists with the same signature.
- **Patched by name, private:** `Program.OnFrameCelestials`,
  `GroundClutterPlacementData.PopulateLaunchPadExclusionZones`. **Read by reflection:**
  `GameViewport.<FixedController>k__BackingField`, `ViewportBase.OptionFlags` (protected setter),
  `PlanetRenderer._biomeMaterialsMap` and `_biomeMaterialSlot`, `KittenRenderable._characterAvatar`,
  `ClusteredLightSystem._diffuseIrradianceImage`/`_specularIrradianceImage`,
  `StaticMeshRenderable`/`AnimatedRenderable.MaterialIndices`, `Program._screenshotCapture`,
  `FileReference.DoLoad`. Each failure is caught and degrades one feature with a logged warning.
- **Never-called "pin" methods** reference public patch targets so a signature change is a compile
  error.
- **Saves stay independent of the mod**: furniture is stripped on save, and the part ids are frozen.
- **Menu bar:** in every build in the assemblies history, the mod's target 2026.10.7.5541 included,
  KSA's own menu bar is a window named "Menu Bar" using `BeginMenuBar`, not ImGui's main menu bar, so
  the mod's `BeginMainMenuBar` entry does not sit inside it; `Program.DrawProgramMenusHook` is the
  hook into KSA's bar. See [settings menu](features/settings-menu.md).

## Secondary sources

- `README.md` - player-facing feature list, install and the "restart after enabling" note.[^mod-readme]
- `CLAUDE.md` - layout, every Harmony patch, frame plumbing, and the "engine facts this stands on"
  section, which this bundle verifies feature by feature.[^mod-claude]
- `src/KSAStructures/KSAStructuresOffice.xml` - heavily commented asset file explaining why each
  piece is a static object, a part, or never drawn.[^mod-office-xml]

[^mod-readme]: KSAStructures README
[^mod-claude]: KSAStructures CLAUDE.md
[^mod-modtoml]: src/KSAStructures/mod.toml
[^mod-csproj]: src/KSAStructures/KSAStructures.csproj
[^mod-entry]: src/KSAStructures/Ksa/KSAStructuresMod.cs
[^mod-office-xml]: src/KSAStructures/KSAStructuresOffice.xml
[^mod-ksaworld]: src/KSAStructures/Ksa/KsaWorld.cs
[^mod-lock]: ksa-assemblies.lock
[^starmap-program]: StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
