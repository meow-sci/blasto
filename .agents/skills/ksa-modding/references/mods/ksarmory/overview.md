---
type: Mod
title: KSArmory
description: Weapon systems for Kitten Space Agency built as reusable machinery (sensors, fire control, sub-frame projectiles, articulated mounts, a ballistic computer, compute-shader effects) with each weapon a registry entry plus art, bound to the game by six StarMap hooks and six Harmony patches
mod: ksarmory
tags: [weapons, sensors, guidance, projectiles, harmony, compute-shaders, imgui, cameras, tooling]
repo: KSArmory
license: MIT
author: LaurensDeV
loader: starmap
mod_id: KSArmory
---

KSArmory is a StarMap code mod by LaurensDeV (branded "Kessler Systems Armory") that adds weapon
parts, sensors and projectiles to Kitten Space Agency's spacecraft sandbox.[^readme] Its stated
design is machinery first: a search sensor with a closest-point-of-approach threat model, fire
control, articulated mounts, projectiles the mod integrates itself to sub-frame accuracy, and an
ImGui operator panel, so that a weapon system is a registry entry plus its art.[^readme] It ships a
Pantsir-style launcher vehicle, three single-round missile rails, a CIWS gun, a naval gun mount, a
chin turret, a bomb rack and a MIRV bus on that machinery, plus two optical directors and two
countermeasure dispensers; the Mk 42 and M197 art is credited to Mallikas.[^readme][^assets-xml]
Other mods can add weapons as data-only "weapon packs".[^weapon-packs-doc]

For a modder the repository is mostly interesting as a large, heavily commented worked example of
binding custom simulation to KSA's frame: when to step, what instant each sample belongs to,
which engine renderers can be reused, and how to degrade when a reflected member moves.[^frame-order-doc][^frames-doc]

## Install footprint

`src/KSArmory/mod.toml` is both the content manifest and the StarMap manifest:[^mod-toml]

| Key | Value | Effect |
|---|---|---|
| `name` | `KSArmory` | Manifest id; players add `[[mods]] id = "KSArmory"` to KSA's `manifest.toml`[^readme] |
| `assets` | `KSArmoryAssets.xml`, `KSArmoryGameData.xml`, `KSArmoryParticles.xml`, `KSArmorySounds.xml` | Listed one by one; an unlisted XML is never loaded and nothing reports it |
| `[StarMap] EntryAssembly` | `KSArmory` | The assembly StarMap loads and scans for `[StarMapMod]` |

| Asset file | Declares |
|---|---|
| `KSArmoryAssets.xml` | 11 `<MeshAtlas>` GLBs, 14 `<PbrMaterial>`, 41 `<SubPart>` templates, 14 `<Part>`s (launcher vehicle, rails, directors, pod, rack, CIWS, MIRV bus, gun mounts, dispensers)[^assets-xml] |
| `KSArmoryGameData.xml` | 14 `<PartGameData>` (mass, colliders, connectors, editor tags), 1 `<SubPartGameData>`, 2 `<EditorTagDef>`[^gamedata-xml] |
| `KSArmoryParticles.xml` | 9 `<ParticleEmitter>`, 1 `<Explosion>`, and the 9 compute `<Shader>` assets the mod dispatches itself[^particles-xml] |
| `KSArmorySounds.xml` | 8 `<Sound>`, 2 `<CompoundSound>`, 8 `<SpatialSoundData>`[^sounds-xml] |
| `KSArmory/Weapons.xml`, `KSArmory/Bodies.xml` | Not KSA assets: the mod's own weapon definitions and per-body burst data, read by the same folder convention as any weapon pack[^weapons-xml][^bodies-xml] |

The XML sits at the mod root beside `Meshes/` (11 `.glb`), `Textures/` (PNG), `Sounds/` (6 `.wav`)
and `Shaders/` (9 `.comp`), mirroring Core's layout so relative paths resolve the same way either
way they are read.[^mod-toml][^assets-xml] The deployed folder also gets `KSArmory.dll` and, on a
developer machine only, an empty `developer` marker file.[^claude-md]

## Loader model and entry class

StarMap loads `KSArmory.dll` and instantiates `KSArmoryMod`, the type carrying `[StarMapMod]`.[^entry]
Every frame hook is wrapped so a fault is logged and repeated faults disable the mod rather than
the game.[^entry]

| Hook | Lifecycle | What KSArmory does there |
|---|---|---|
| `[StarMapImmediateLoad] OnImmediateLoad(Mod)` | `startup:immediate-load` (prefix on `Mod.PrepareSystems`)[^starmap-mod-patches] | Writes the generated shader-include header for this install; logs built-for and running KSA builds[^entry] |
| `[StarMapAllModsLoaded] OnFullyLoaded()` | `startup:all-mods-loaded` (postfix on `ModLibrary.LoadAll`)[^starmap-modlibrary-patches] | Registers asset-only weapon packs, freezes and audits the catalogue, repairs saves, starts the JIT warm-up, installs the six Harmony patches, builds the rosters and UI[^entry] |
| `[StarMapAfterOnFrame] OnAfterFrame` | `frame:after-on-frame` (postfix on `Program.OnFrame`)[^starmap-program-patcher] | `StepOnce` fallback; the developer file bridge |
| `[StarMapBeforeGui] OnBeforeGui` | `frame:draw-ui` (prefix on `OnDrawUiFrame`) | Adds the "Mods" menu entry before KSA fills the menu bar |
| `[StarMapAfterGui] OnAfterGui` | `frame:draw-ui` (postfix on `OnDrawUiViewports`) | `StepOnce` (simulation, cameras), panel, overlays, rings, world clicks |
| `[StarMapUnload] Unload` | unload | Restores timewarp, cameras and the stock fixed controller, returns pooled emitters and audio channels, removes five of the six patches[^entry] |

`StepOnce` runs the whole simulation once per frame from whichever hook claims it first
(`Sim/FrameLatch.cs`): the after-GUI postfix normally, the `OnFrameCelestials` prefix when the HUD is
hidden (the UI pass is skipped when `Program.DrawUI` is false), and the after-frame postfix as the
last resort.[^entry][^ksa-program]

Six Harmony patches, each installed by hand in `OnFullyLoaded` with its own Harmony id and
degrading to a logged warning if the target is missing:[^entry]

| Patch | Target | Lifecycle | Why |
|---|---|---|---|
| `AttitudeHook` prefix | `Vehicle.PrepareWorker` (public virtual, Vehicle.cs L2396) | `frame:dispatch-solvers` | The only window where attitude, staging, mass and leak writes survive the worker snapshot[^attitude-hook][^ksa-vehicle] |
| `PreRenderHook` prefix | `Program.OnFrameCelestials` (private, Program.cs L2701) | `frame:celestials` | Steps before the render on frames with the UI hidden[^prerender-hook][^ksa-program] |
| `CloudPassHook` prefix | `SunbloomRenderer.Render` | `render:bloom` | Records the mod's compute passes while scene colour is storage-writable and before bloom and tonemap[^cloudpass-hook][^ksa-sunbloom] |
| `WorldReloadHook` postfix | `Program.OnGameLoaded` (Program.cs L2599, called from save loading) | `startup:game-loaded`, `event:save-load` | The only signal that a save replaced the world[^worldreload-hook][^ksa-uncompressedsave] |
| `RoundBodyDrawHook` postfix | `Vehicle.UpdateRenderData` (L3713, from `UpdateRenderingResources`) | `frame:render` | Draws a launcher's round bodies after the engine culled the sub-pixel launcher[^roundbody-hook][^ksa-vehicle] |
| `LooseBodyDrawHook` prefix | `PartModelRenderer.UpdateRenderData` | `frame:render` | Instances shell and orphaned round meshes per viewport[^loosebody-hook][^ksa-partmodelrenderer] |

Harmony itself is not shipped: the csproj references the game folder's `0Harmony.dll` with
`Private=false`, like every KSA assembly.[^csproj]

## Dependencies

| Dependency | How it is resolved |
|---|---|
| StarMap | Required loader; provides `StarMap.API.dll` (attributes) and loads the mod's assembly[^readme][^csproj] |
| Harmony (`0Harmony.dll`) | Loaded with the game through StarMap; referenced, not copied[^csproj] |
| KSA assemblies (`KSA.dll`, `Brutal.*`, `Planet.Render.Core`, `BepuUtilities`, Vulkan, ImGui, GLFW) | Referenced with `Private=false` from a folder resolved by `Directory.Build.props` (env var, `Import/`, sibling assemblies checkout, then the install), pinned by `ksa-assemblies.lock`[^csproj][^lock][^claude-md] |
| ModMenu | Not a dependency: a copied, inert `ModMenuEntryAttribute` lets ModMenu list the mod when present[^modmenu-entry] |
| KittenExtensions, ShaderExtensions | Not used; compute shaders are plain `<Shader>` assets compiled by KSA and dispatched by the mod[^particles-xml] |
| Native libraries | None; a portable `net10.0` assembly covers Windows and Linux[^readme] |

## Architecture

| Layer | Contents |
|---|---|
| `src/KSArmory/Sim/` (195 files) | Engine-free simulation: profiles and registry, rounds, guidance, fuses, blast and damage laws, ballistic computer, camera geometry, statistics. Must not reference KSA types; the test project links it wholesale and `tools/check-boundary.sh` enforces it[^claude-md] |
| `src/KSArmory/Ksa/` (110 files) | Everything that binds to the game; `KsaWorld.cs` funnels most engine contact, `KSArmoryMod.cs` owns the hooks, `Ksa/Ui/` the panel[^claude-md][^ksaworld] |
| Weapon packs | Other mods' `KSArmory/` folders read at `startup:all-mods-loaded` (asset-only packs) or registered from code through `Armoury` before it; the catalogue is then frozen and audited against declared parts[^entry][^extensibility-doc] |
| Compute shaders | Nine `.comp` files dispatched from the `SunbloomRenderer.Render` prefix: mushroom cloud (march and resolve), shock refraction, sensor look, fireball light, ground rings, bullet holes, leaks, tracers[^particles-xml] |

**Data flow per frame.** Physics results arrive in `PrepareFrame` (`Universe.ApplyVehicleSolvers`); the next worker step is dispatched
(with the `PrepareWorker` prefix writing commands); in the UI pass `StepOnce` samples the world once
for every system, steps every round on the simulated step, runs fire control, effects and cameras;
the panel and overlays draw; render-time patches place round bodies and record the compute passes.[^frame-order-doc][^entry][^ksa-universe]

**Threads.** Simulation, drawing and engine writes are main-thread. Two tasks run off it: the JIT
warm-up of the kill path at load and the feedback upload when a player clicks Send; the file bridge
is polled on the main thread.[^claude-md]

## Features

All 80 feature concepts, grouped by category. Lifecycle values follow the bundle conventions.

| Feature | Category | Lifecycle |
|---|---|---|
| [Asset-only weapon packs read from a KSArmory folder in every installed mod](./features/weapon-packs-from-mod-folders.md) | assets | `startup:all-mods-loaded` |
| [Headless Blender model generation and offline GLB checks](./features/blender-model-pipeline-and-checks.md) | assets | `startup:asset-load` |
| [Weapon parts as inert KSA parts split into an Assets file and a GameData file](./features/weapon-part-definitions-xml.md) | assets | `startup:asset-load` |
| [Spatialised gun and rocket-motor sounds driven by held FMOD channels](./features/spatial-gun-and-motor-sounds.md) | audio | `startup:asset-load`, `frame:draw-ui`, `event:starmap-unload` |
| [The burst's bang, played when its blast front reaches the listener](./features/burst-sound-at-blast-arrival.md) | audio | `startup:asset-load`, `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` |
| [Borrow the player's main view and hand it back](./features/borrow-main-view-camera.md) | camera | `frame:draw-ui`, `frame:celestials`, `frame:viewports`, `event:save-load` |
| [Chase camera riding a mod-simulated round](./features/chase-camera-on-a-round.md) | camera | `frame:input`, `frame:viewports`, `frame:draw-ui`, `frame:celestials`, `frame:controller`, `frame:render` |
| [Director camera windows on leased secondary viewports](./features/director-camera-window.md) | camera | `frame:draw-ui`, `frame:celestials`, `frame:viewports`, `frame:render`, `render:imgui` |
| [Terrain ground-impact test against the engine's height field](./features/terrain-ground-impact-test.md) | celestials | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` |
| [A three-member, text-only registration API for code packs, with a generated contract record](./features/armoury-pack-registration-api.md) | external-io | `startup:starmap-before-main`, `startup:all-mods-loaded` |
| [File bug reports from an ImGui form through a guarded issue service](./features/in-game-bug-reports.md) | external-io | `frame:draw-ui`, `thread:background` |
| [A ballistic computer that flies any player-built rocket to a latitude and longitude](./features/icbm-ballistic-computer.md) | flight-and-orbits | `frame:draw-ui`, `frame:dispatch-solvers` |
| [A MIRV post-boost bus that trims with RCS, re-points between releases and lets its warheads go](./features/mirv-bus-trim-and-release.md) | flight-and-orbits | `frame:draw-ui`, `frame:dispatch-solvers`, `physics:vehicle-solver` |
| [Designate a place on the ground that moves with the planet, and show what a falling store can still reach](./features/ground-target-designation-and-reach.md) | flight-and-orbits | `frame:draw-ui` |
| [Guided bomb tail kit and a flown bomb sight](./features/guided-bomb-tail-kit-and-sight.md) | flight-and-orbits | `frame:draw-ui`, `frame:celestials`, `render:bloom`, `render:imgui` |
| [Hold timewarp down while rounds or burns are in flight](./features/timewarp-policy-during-engagements.md) | flight-and-orbits | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame`, `event:starmap-unload` |
| [Seekers, command uplink and laser designation](./features/seekers-uplink-and-laser-homing.md) | flight-and-orbits | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` |
| [True proportional navigation for mod-flown missiles](./features/proportional-navigation-guidance.md) | flight-and-orbits | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` |
| [JIT-compile the mod and the engine's destroy path on a background thread at load](./features/jit-warmup-kill-path.md) | getting-started | `startup:all-mods-loaded`, `thread:background` |
| [Own log file, build stamps and a developer marker file](./features/own-log-file-and-developer-switch.md) | getting-started | `startup:immediate-load`, `startup:all-mods-loaded`, `thread:background` |
| [StarMap entry class with per-hook fault containment](./features/starmap-entry-and-fault-containment.md) | getting-started | `startup:immediate-load`, `startup:all-mods-loaded`, `frame:draw-ui`, `frame:after-on-frame`, `event:starmap-unload` |
| [Command vehicles from a Vehicle.PrepareWorker prefix](./features/prepareworker-prefix-command-window.md) | patching | `startup:all-mods-loaded`, `frame:dispatch-solvers` |
| [One simulation step per frame from whichever hook runs first](./features/dual-hook-simulation-step.md) | patching | `startup:all-mods-loaded`, `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` |
| [Detect a save load with a postfix on Program.OnGameLoaded](./features/detect-save-load-ongameloaded.md) | persistence | `startup:all-mods-loaded`, `startup:game-loaded`, `event:save-load`, `frame:draw-ui` |
| [Per-craft settings as a JSON sidecar inside the save folder](./features/per-craft-settings-json-sidecar.md) | persistence | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame`, `event:save` |
| [Repair saves at startup after removing a part's subparts](./features/startup-save-repair.md) | persistence | `startup:all-mods-loaded` |
| [Analytic sub-frame fuses by closest approach](./features/analytic-subframe-fuses.md) | physics | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` |
| [Ballistic lead for unguided shells, from a fixed-point solve to flying the shell](./features/unguided-ballistic-lead.md) | physics | `frame:draw-ui` |
| [Integrate on the step KSA applied, consumed once](./features/consume-applied-sim-step.md) | physics | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` |
| [Mod-simulated rounds integrated in sub-steps inside the frame](./features/mod-simulated-rounds.md) | physics | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` |
| [One ecliptic frame and one epoch for everything simulated and drawn](./features/ecliptic-frames-and-epochs.md) | physics | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` |
| [Per-triangle hull hit test with the engine's part ray cast](./features/hull-triangle-hit-test.md) | physics | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` |
| [Point gravity, body fall and one-reference air for mod-integrated bodies](./features/point-gravity-and-atmosphere-model.md) | physics | `frame:draw-ui` |
| [Push a craft with a blast wind by writing an impulse into its physics state](./features/blast-shove-impulse.md) | physics | `frame:draw-ui`, `frame:dispatch-solvers` |
| [Mod-authored particle emitters taken from KSA's shared pool and re-anchored each frame](./features/pooled-particle-emitters.md) | rendering | `startup:asset-load`, `frame:draw-ui`, `frame:celestials`, `frame:pre-render` |
| [Motor and flare smoke through KSA's volumetric booster-trail renderer](./features/trail-renderer-smoke.md) | rendering | `frame:draw-ui`, `frame:celestials`, `frame:pre-render`, `render:atmosphere` |
| [Round bodies drawn past the engine's pixel cull and as part-model instances](./features/round-body-draw-hooks.md) | rendering | `startup:all-mods-loaded`, `frame:render` |
| [Rounds drawn as hidden launcher subparts flown by transform writes](./features/rounds-as-hidden-launcher-subparts.md) | rendering | `startup:asset-load`, `frame:draw-ui`, `frame:celestials` |
| [Set off KSA's own explosion at an arbitrary point, anchored on the planet](./features/ksa-explosions-from-a-point.md) | rendering | `startup:asset-load`, `startup:all-mods-loaded`, `frame:draw-ui` |
| [World-anchored overlays through KSA's gizmo renderer](./features/gizmo-world-overlays.md) | rendering | `frame:draw-ui`, `render:scene-opaque` |
| [A burst's whiteout and a blast front's view shake, painted into the scene image instead of moving the camera](./features/burst-flash-and-view-shake.md) | shaders | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame`, `render:bloom` |
| [A generated include that reaches KSA's own shader library from a mod shader](./features/core-shader-library-include.md) | shaders | `startup:immediate-load`, `startup:asset-load` |
| [A mod compute pass recorded into KSA's frame just before bloom](./features/compute-pass-before-bloom.md) | shaders | `startup:asset-load`, `startup:all-mods-loaded`, `render:bloom` |
| [A raymarched mushroom cloud, held body-fixed and drawn before bloom](./features/raymarched-mushroom-cloud.md) | shaders | `startup:asset-load`, `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame`, `render:bloom` |
| [Fireball light through LightDebug, filled in where the light pre-pass dropped it](./features/fireball-light-injection.md) | shaders | `frame:draw-ui`, `frame:celestials`, `render:bloom` |
| [Gun tracers added into the scene by one tiled compute dispatch](./features/compute-shell-tracers.md) | shaders | `frame:draw-ui`, `frame:celestials`, `render:bloom` |
| [Shell holes held in the struck sub-part's frame and painted from depth](./features/bullet-hole-decals.md) | shaders | `frame:draw-ui`, `frame:celestials`, `render:bloom` |
| [Targeting rings painted on the depth buffer](./features/depth-painted-ground-rings.md) | shaders | `frame:draw-ui`, `render:bloom` |
| [The blast front drawn as a bend in the light, by copying the scene and writing it back at an offset](./features/shock-front-refraction.md) | shaders | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame`, `render:bloom` |
| [TV and thermal look for a camera window, recorded before bloom](./features/sensor-look-pass.md) | shaders | `frame:draw-ui`, `frame:celestials`, `render:bloom` |
| [What a high or airless burst lights in the sky, chosen by the air at the burst](./features/high-altitude-burst-sky.md) | shaders | `startup:all-mods-loaded`, `frame:draw-ui`, `frame:celestials`, `frame:pre-render`, `frame:render`, `render:particles`, `render:bloom` |
| [Compare mod variants in flight with interleaved batches and rank statistics](./features/shot-night-statistics.md) | testing-and-tooling | `frame:draw-ui` |
| [Deploy into the KSA mods folder, package a clean archive and cut releases from commits](./features/build-deploy-package-release.md) | testing-and-tooling | build time |
| [Drive a running game from outside through a file command bridge and an MCP server](./features/file-command-bridge-and-mcp.md) | testing-and-tooling | `frame:after-on-frame` |
| [Fly scripted scenarios unattended and report a pass or fail verdict](./features/unattended-scenario-runner.md) | testing-and-tooling | `startup:all-mods-loaded`, `frame:draw-ui`, `frame:after-on-frame` |
| [Headless tests enforced by a game-free Sim/ source boundary](./features/sim-boundary-headless-tests.md) | testing-and-tooling | build time |
| [One check script shared by the pre-push hook and CI](./features/repo-static-checks.md) | testing-and-tooling | build time |
| [Pin the KSA build with a hashed assemblies lock file](./features/ksa-assemblies-lock.md) | testing-and-tooling | `startup:immediate-load` |
| [Record the exact KSA API a mod binds to from its compiled metadata](./features/api-surface-tracking.md) | testing-and-tooling | build time |
| [Cursor-ray picking and world click tools](./features/cursor-ray-world-click-tools.md) | ui | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` |
| [Gunner's sight painted over a borrowed view or a camera window](./features/gunner-sight-overlay.md) | ui | `frame:draw-ui`, `frame:viewports` |
| [Operator panel in ImGui with a menu-bar entry and ModMenu registration](./features/operator-panel-imgui.md) | ui | `frame:draw-ui` |
| [Radar PPI scope and shaded-relief terrain map drawn with ImGui primitives](./features/radar-scope-and-relief-map.md) | ui | `frame:draw-ui` |
| [World-anchored screen markers on full-screen ImGui overlay windows](./features/world-anchored-screen-markers.md) | ui | `frame:draw-ui` |
| [A weapon system is data plus art, discovered by part Id](./features/weapon-registry-and-profiles.md) | vehicles-and-parts | `startup:all-mods-loaded`, `frame:draw-ui` |
| [Articulated turrets by rewriting subpart transforms every frame](./features/articulated-mount-subparts.md) | vehicles-and-parts | `frame:draw-ui`, `frame:celestials` |
| [Blast damage per part against the engine's crash tolerance, with health and engine dents](./features/blast-damage-and-part-health.md) | vehicles-and-parts | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame`, `physics:vehicle-solver` |
| [Closest-point-of-approach threat classification](./features/cpa-threat-classification.md) | vehicles-and-parts | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` |
| [Destroy vehicles and break off parts safely from a frame hook](./features/safe-vehicle-destruction-and-part-failure.md) | vehicles-and-parts | `frame:draw-ui`, `frame:apply-solvers` |
| [Fire control ladder and per-craft target allocation](./features/fire-control-and-target-allocation.md) | vehicles-and-parts | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` |
| [Flare and chaff dispenser parts with simulated decoys](./features/countermeasure-dispensers.md) | vehicles-and-parts | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame`, `startup:asset-load` |
| [Fly any vehicle from a mod through its flight computer and input actions](./features/fly-another-vehicle.md) | vehicles-and-parts | `frame:draw-ui`, `frame:dispatch-solvers` |
| [Gun travel limits swept from the craft's own geometry with part ray casts](./features/self-collision-travel-map.md) | vehicles-and-parts | `frame:draw-ui`, `frame:celestials` |
| [Holed tanks that drain through the engine's mole states and throw a painted stream](./features/holed-tank-leaks.md) | vehicles-and-parts | `frame:draw-ui`, `frame:celestials`, `frame:dispatch-solvers`, `render:bloom` |
| [IFF teams, alliances and the per-frame team roster](./features/iff-teams-and-allegiance.md) | vehicles-and-parts | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` |
| [Launcher parts get lighter as their stores leave](./features/per-part-store-mass.md) | vehicles-and-parts | `frame:draw-ui`, `frame:dispatch-solvers` |
| [Optical director heads with mast and roll-nod gimbals](./features/optical-director-heads.md) | vehicles-and-parts | `frame:draw-ui`, `frame:celestials`, `frame:viewports`, `frame:render` |
| [Radar search cone, masking and lock dwell](./features/radar-search-and-lock.md) | vehicles-and-parts | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` |
| [Separate a launcher from its stack by firing the decoupler on its own joint](./features/launcher-separation-at-decoupler.md) | vehicles-and-parts | `frame:draw-ui`, `frame:input` |
| [Spawn test drones and teleport craft from the panel, a click or a script](./features/spawn-and-teleport-test-craft.md) | vehicles-and-parts | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame`, `frame:input`, `frame:dispatch-solvers`, `physics:vehicle-solver` |

## Build, test and tooling

- `tools/build.sh`, `deploy.sh`, `run.sh`, `package.sh`: build with the right SDK, install with a
  developer marker, launch through StarMap, package a release that refuses foreign DLLs.[^claude-md]
- `tools/test.sh`: the xUnit suite under `tests/KSArmory.Tests` that links `Sim/` and flies engagements headlessly.[^claude-md]
- `tools/check-all.sh`: about twenty offline checks shared by the pre-push hook and CI;
  `tools/validate-parts.py` and `tools/model/` checkers for assets.[^claude-md]
- `tools/scenario.sh`, `shot-batch.sh`, `shot-report.py`, `tools/ksa-mcp/server.py`: unattended
  in-game scenarios, statistical shot nights and an MCP server over the file bridge.[^claude-md][^visual-testing-doc]
- `ksa-assemblies.lock`, `tools/api-surface.sh`, `tools/ksa-api-diff.sh`, `tools/decompile-assemblies.sh`:
  pin the KSA build and track the exact API the mod binds to.[^lock][^api-surface-doc]

## Compatibility notes

- Built and documented against KSA `2026.10.7.5541` (README and lock); this bundle checked the
  engine claims against build `2026.10.10.5554`.[^readme][^lock]
- Reflection sites degrade with one warning when a member moves: `Program._volumetricTrailRenderer`,
  `Program._screenshotCapture`, the `GameViewport` fixed-controller backing field, the
  `ViewportBase.OptionFlags` setter, a `ClusteredLightSystem` field, weather and cloud-shadow
  buffers, `Vehicle` update-state and manual-input fields.[^ksaworld]
- Public patch targets are pinned by never-called methods so a signature change is a build error;
  the private `OnFrameCelestials` target cannot be, and falls back to the after-frame hook.[^attitude-hook][^prerender-hook]
- Content traps that crash saves: a shipped part's subpart list is append-only and a shipped
  `<Part>` Id must not be renamed.[^claude-md]
- The mod's own docs cite older builds and line numbers in a different corpus; viewport tables in
  `docs/KSA-CAMERAS.md` name a `Viewport` class that 5554 no longer has.[^cameras-doc]
- `Unload` removes five patches but not `CloudPassHook`, although `CloudPassHook.Remove` exists (observed in code; effect on a hot unload not tested).[^entry][^cloudpass-hook]
- `docs/BLOCKED-ON-KSA.md` lists what the mod cannot build yet and why.[^blocked-doc]

## Secondary sources

| Doc | Worth reading for |
|---|---|
| [docs/KSA-FRAME-ORDER.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/KSA-FRAME-ORDER.md) | The engine's frame order and which instant each sample belongs to[^frame-order-doc] |
| [docs/FRAMES-AND-EPOCHS.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/FRAMES-AND-EPOCHS.md) | Ecliptic frames, epochs and the failure shapes of a mismatch[^frames-doc] |
| [docs/KSA-MODDING-NOTES.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/KSA-MODDING-NOTES.md) | Loader contract, part XML, reference frames, gotchas[^modding-notes] |
| [docs/KSA-CAMERAS.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/KSA-CAMERAS.md) | Camera modes, controllers, viewports (check against current builds)[^cameras-doc] |
| [docs/KSA-TERRAIN.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/KSA-TERRAIN.md) | Where the engine thinks the ground is[^terrain-doc] |
| [docs/KSA-API-SURFACE.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/KSA-API-SURFACE.md) | Generated list of every KSA member the mod binds to[^api-surface-doc] |
| [docs/BLOCKED-ON-KSA.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/BLOCKED-ON-KSA.md) | What cannot be built, with the engine reason[^blocked-doc] |
| [docs/EXTENSIBILITY.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/EXTENSIBILITY.md), [docs/WEAPON-PACKS.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/WEAPON-PACKS.md), [docs/PACK-API-SURFACE.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/PACK-API-SURFACE.md) | Weapon packs: registration, definition format, bound API[^extensibility-doc][^weapon-packs-doc][^pack-api-doc] |
| [docs/MODULARITY.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/MODULARITY.md) | How far the profile and registry split generalises[^modularity-doc] |
| [docs/NUCLEAR-EFFECT.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/NUCLEAR-EFFECT.md), [docs/DAMAGE-DECALS.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/DAMAGE-DECALS.md) | Which engine volumetric renderers a mod can reach; decal projection plan[^nuclear-effect-doc][^decals-doc] |
| [docs/ICBM-GUIDANCE.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/ICBM-GUIDANCE.md) | The ballistic computer's algorithm and frames[^icbm-doc] |
| [docs/SHOT-PROTOCOL.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/SHOT-PROTOCOL.md), [docs/VISUAL-TESTING.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/VISUAL-TESTING.md) | Statistical testing in game; seeing what the mod draws from a terminal[^shot-protocol-doc][^visual-testing-doc] |
| [docs/FROM-KSP-MODDING.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/FROM-KSP-MODDING.md) | Concept map for KSP part modders[^from-ksp-doc] |
| [CLAUDE.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/CLAUDE.md) | File-by-file layout table and the design decisions behind them (300 KB; grep it)[^claude-md] |

[^readme]: README.md
[^assets-xml]: src/KSArmory/KSArmoryAssets.xml
[^weapon-packs-doc]: docs/WEAPON-PACKS.md
[^frame-order-doc]: docs/KSA-FRAME-ORDER.md
[^frames-doc]: docs/FRAMES-AND-EPOCHS.md
[^mod-toml]: src/KSArmory/mod.toml
[^gamedata-xml]: src/KSArmory/KSArmoryGameData.xml
[^particles-xml]: src/KSArmory/KSArmoryParticles.xml
[^sounds-xml]: src/KSArmory/KSArmorySounds.xml
[^weapons-xml]: src/KSArmory/KSArmory/Weapons.xml
[^bodies-xml]: src/KSArmory/KSArmory/Bodies.xml
[^claude-md]: CLAUDE.md
[^entry]: src/KSArmory/Ksa/KSArmoryMod.cs
[^starmap-mod-patches]: StarMap.Core/Patches/ModPatches.cs
[^starmap-modlibrary-patches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^starmap-program-patcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^attitude-hook]: src/KSArmory/Ksa/AttitudeHook.cs
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^prerender-hook]: src/KSArmory/Ksa/PreRenderHook.cs
[^cloudpass-hook]: src/KSArmory/Ksa/CloudPassHook.cs
[^ksa-sunbloom]: KSA.SunbloomRenderer (decompiled, build 2026.10.10.5554)
[^worldreload-hook]: src/KSArmory/Ksa/WorldReloadHook.cs
[^ksa-uncompressedsave]: KSA.UncompressedSave (decompiled, build 2026.10.10.5554)
[^roundbody-hook]: src/KSArmory/Ksa/RoundBodyDrawHook.cs
[^loosebody-hook]: src/KSArmory/Ksa/LooseBodyDrawHook.cs
[^ksa-partmodelrenderer]: KSA.PartModelRenderer (decompiled, build 2026.10.10.5554)
[^csproj]: src/KSArmory/KSArmory.csproj
[^lock]: ksa-assemblies.lock
[^modmenu-entry]: src/KSArmory/Ksa/Ui/ModMenuEntry.cs
[^ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^extensibility-doc]: docs/EXTENSIBILITY.md
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^visual-testing-doc]: docs/VISUAL-TESTING.md
[^api-surface-doc]: docs/KSA-API-SURFACE.md
[^cameras-doc]: docs/KSA-CAMERAS.md
[^blocked-doc]: docs/BLOCKED-ON-KSA.md
[^modding-notes]: docs/KSA-MODDING-NOTES.md
[^terrain-doc]: docs/KSA-TERRAIN.md
[^pack-api-doc]: docs/PACK-API-SURFACE.md
[^modularity-doc]: docs/MODULARITY.md
[^nuclear-effect-doc]: docs/NUCLEAR-EFFECT.md
[^decals-doc]: docs/DAMAGE-DECALS.md
[^icbm-doc]: docs/ICBM-GUIDANCE.md
[^shot-protocol-doc]: docs/SHOT-PROTOCOL.md
[^from-ksp-doc]: docs/FROM-KSP-MODDING.md
