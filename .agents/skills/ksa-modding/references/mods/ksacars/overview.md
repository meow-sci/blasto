---
type: Mod
title: KSACars (Fast & Purrious)
description: Driveable cars for KSA, a beach buggy and a flying Cadillac Eldorado, built as single parts whose springs, tyres, engine, rockets, lights, sounds and driver animation are simulated by a StarMap code mod and injected into the engine's vehicle step
mod: ksacars
tags:
  - cars
  - wheels
  - suspension
  - harmony
  - starmap
  - xml-assets
  - glb
  - kittens
  - ik
  - sound
  - lights
  - exhaust
  - mcp
  - testing
repo: KSACars
license: MIT
author: LaurensDeV
loader: starmap
mod_id: FastAndPurrious
---

**Fast & Purrious** (repository and assembly `KSACars`, published by GitHub user LaurensDeV, MIT)
adds two cars to Kitten Space Agency: a fibreglass **Beach Buggy** on long-travel coil-overs with
two seats, and a red 1976 **Cadillac Eldorado** convertible with four seats that also flies on
rockets under its floor, boosts on two tail rockets, presses itself down with bonnet and boot
thrusters, hovers, and pushes rocks with an optional nose scoop.[^ksacars-readme] KSA has no
wheels, so everything that makes a car drive is the mod's own: springs, tyres, an engine and a
gearbox stepped once a frame and injected into the engine's vehicle step. The driver kitten holds
and turns the steering wheel, kittens get out and back in through EVA doors, the lamps are real
spotlights, the engines are recorded V8 and flat-four loops, and a car bounces rather than breaks.

The repository is also an unusually well-documented piece of reverse engineering: `CLAUDE.md` and
`docs/` record the engine's frame order, reference frames, terrain, cameras and the things KSA
cannot do yet, and the tooling covers API-surface tracking, a file-drop control bridge and an MCP
server for driving the game from an agent.[^ksacars-claude]

## Install footprint

| Item | Value |
|---|---|
| Mod id (folder and `manifest.toml` id) | `FastAndPurrious` |
| `mod.toml` `name` | `FastAndPurrious` |
| `assets` | `KSACarsAssets.xml`, `KSACarsGameData.xml`, `KSACarsSounds.xml` (listed one by one) |
| `[StarMap] EntryAssembly` | `KSACars` (loads `KSACars.dll`) |
| Folder | `KSACars.dll`, `mod.toml`, the three XML files at the root, `Meshes/*.glb` (Buggy, Eldorado, EldoIris, EldoScoop), `Textures/*.png` (diffuse, normal, ORM, emissive), `Sounds/*.wav` (start, idle, load, stop per car) |
| Parts | `KSACars_Prefab_Buggy`, `KSACars_Prefab_Eldorado`, in a new editor tag `Vehicles` |
| Version at this ref | 0.1.7 (csproj `<Version>`, set by semantic-release) |

The player unzips the release into `<KSA user dir>/mods/`, adds `[[mods]] id = "FastAndPurrious"
enabled = true` to `manifest.toml`, and launches through StarMap. The mod writes
`Logs/KSACars.log` beside KSA's own logs.[^ksacars-readme][^ksacars-modtoml] Details:
[one folder as both mods](./features/mod-folder-content-and-code-mod.md).

## Loader model and entry class

StarMap loads `KSACars.dll` into its own `AssemblyLoadContext` and instantiates `KSACarsMod`, the
`[StarMapMod]` class. All game and loader assemblies are compile-time references with
`Private="false"`.[^ksacars-mod][^ksacars-csproj]

| Hook | StarMap mechanism | What KSACars does |
|---|---|---|
| `[StarMapImmediateLoad] OnImmediateLoad(Mod)` | prefix on `Mod.PrepareSystems`[^starmap-modpatches] | Logs the mod id, mod version, KSA build compiled for vs running |
| `[StarMapAllModsLoaded] OnFullyLoaded()` | postfix on `ModLibrary.LoadAll`[^starmap-modlibrarypatches] | Installs nine Harmony patches; creates the command bridge on developer installs |
| `[StarMapAfterGui] OnAfterGui(double)` | postfix on `Program.OnDrawUiViewports`[^starmap-programpatcher] | Once-per-frame car step (find cars, pose subparts, sounds) via a frame latch; car panel; craft mover and gizmos |
| `[StarMapAfterOnFrame] OnAfterFrame(double, double)` | postfix on `Program.OnFrame` | Fallback car step when the UI is hidden; bridge update; releases the latch in `finally` |
| `[StarMapUnload] Unload()` | direct invoke from `ModLoader.Dispose`, after `StarMapCore.DeInit` has called `UnpatchAll()` with no id[^starmap-core][^starmap-modloader] | Stops sounds and calls each hook's `Remove`, both no-ops by then (every Harmony patch in the process is already removed and `Program.Dispose` has released the audio system), then flushes the log |

Both frame hooks run after `Program.PrepareFrame` has called `Universe.ExecuteNextVehicleSolvers`,
so the next physics step is already in flight whenever the mod's StarMap code runs. That is why the
car physics lives in the `Vehicle.PrepareWorker` prefix and the frame hooks only find, pose and
play the cars (see [the once-per-frame step](./features/frame-step-latch-and-car-registry.md)).[^ksa-program]

The nine Harmony patches, all installed at `StarMapAllModsLoaded`:

| Patch | Target | Runs at | Feature |
|---|---|---|---|
| prefix | `Vehicle.PrepareWorker` | `frame:dispatch-solvers`, inside `ExecuteNextVehicleSolvers`, per vehicle before its snapshot | [Impulse injection](./features/prepareworker-impulse-injection.md) |
| postfix | `Vehicle.UpdateSeatedCrewRenderData` | `frame:render`, inside `RenderGame` / `RenderViewport`, per viewport | [Seated crew of unflown cars](./features/seated-crew-of-unflown-craft.md) |
| postfix | `PartTree.UpdateRenderData` | `frame:render`, inside `Render` -> `UpdateRenderingResources`, per viewport | [Headlights](./features/headlights-as-spotlights.md) |
| postfix (private) | `PartTreeRenderData.WriteState` | `frame:render`, same pass, when a slot is rewritten | [Coloured lenses](./features/coloured-emissive-lenses.md) |
| postfix | `Vehicle.AddVolumetricExhaustInstances` | `frame:pre-render`, inside `OnPreRender` | [Exhaust plumes](./features/exhaust-plumes-without-engine.md) |
| prefix | `Universe.DestroyVehicleFromEvent` | `frame:apply-solvers` (render events), `frame:input` (explode command) | [Crash veto](./features/crash-veto.md) |
| prefix + postfix, postfix | `Vehicle.OnKey`, `Vehicle.ProcessInput` | `frame:input` (`Glfw.PollEvents`, `ApplyInputEvents`) | [Flight keys](./features/drive-with-flight-keys.md) |
| postfixes | `GaugeCanvas.IsContextVisible`, `Vehicle.AreAllEnginesInactive` | `frame:draw-ui` (`OnDrawUiThreadSafe`, `OnDrawUiFrame`) | [Engine HUD](./features/engine-hud-for-engineless-craft.md) |
| postfix (private) | `PhysicsBubble.KittenWantsWake` | `physics:vehicle-solver`, worker threads | [Off rails](./features/keep-thrusting-craft-off-rails.md) |

## Dependencies

| Dependency | How it is resolved |
|---|---|
| StarMap | Required for the code half; the player launches `StarMap.exe` / `dotnet StarMap.dll` |
| Harmony (`0Harmony.dll`, Lib.Harmony 2.4.2) | Ships with StarMap; referenced with `Private="false"` |
| KSA assemblies (`KSA.dll`, `Brutal.*`, `BepuUtilities`, `Planet.Render.Core`, `StarMap.API`) | Compile-time only, found by `Directory.Build.props`; hashes pinned in `ksa-assemblies.lock`[^ksacars-lock] |
| ModMenu (optional) | Soft dependency through a name-matched copy of its attribute; inert when absent |
| KittenExtensions, ShaderExtensions, native libraries | None |

The content half (parts, art, sounds) needs no loader: without StarMap the cars appear in the
editor and render but do not drive.[^ksacars-notes]

## Architecture

- **`src/KSACars/Sim/`** holds every algorithm with no KSA types: the car model (`BuggyDrive`,
  `BuggyProfile`), steering-grip IK, righting, lift/hover/boost/downforce, iris hatches, headlight
  beams, terrain ray, picking, the frame latch and the bridge command parser. It is linked into the
  headless xUnit project.
- **`src/KSACars/Ksa/`** binds to the game: the entry class, the nine patches, `Buggies` (the active
  cars and their per-step physics and per-frame poses), `KsaWorld` (most direct KSA contact, kept in
  one place), sounds, panel, craft mover, bridge, log and build info.
- **Threads.** Car physics runs on the main thread inside the `Vehicle.PrepareWorker` prefix,
  between `Universe.ApplyVehicleSolvers` and the vehicle solver job being queued; the result is
  integrated on the worker in the same frame.[^ksa-program][^ksa-universe] Only the
  `KittenWantsWake` postfix runs on vehicle-solver worker threads (reading a concurrent dictionary
  the main thread writes). The log has a timer thread that only flushes the file.
- **Data flow per frame.** `PrepareFrame`: apply last step -> keys polled and applied -> prefix
  reads the craft's held keys, the physics state in the planet-fixed frame and the height field
  under each hub, and writes velocity and spin -> worker integrates. GUI hook: find cars, pose
  wheels/arms/coils/steering/irises/scoops, attach the driver-hands IK, update sounds, draw the
  panel. Render: lamps, lens colours, plumes and unflown crews are submitted from patches at the
  engine's own submission points.

## Features


| Feature | Category | Lifecycle |
|---|---|---|
| [One folder as both a content mod and a StarMap code mod](./features/mod-folder-content-and-code-mod.md) | getting-started | startup:asset-load, startup:immediate-load, startup:all-mods-loaded |
| [Per-mod log file, build stamp and fault limiter](./features/mod-log-file.md) | getting-started | startup:immediate-load, startup:all-mods-loaded, frame:draw-ui, frame:after-on-frame, thread:background |
| [Once-per-frame car step from two StarMap hooks, with a registry of the mod's craft](./features/frame-step-latch-and-car-registry.md) | getting-started | frame:draw-ui, frame:after-on-frame |
| [Car part assets: GLB atlases, PBR materials and subparts](./features/car-part-assets-glb-pbr.md) | assets | startup:asset-load |
| [Art and audio pipeline tooling](./features/art-and-audio-pipeline-tooling.md) | assets | (build time) |
| [Car part game data](./features/car-part-gamedata.md) | vehicles-and-parts | startup:asset-load |
| [Vehicle library presets](./features/vehicle-library-presets.md) | vehicles-and-parts | (install time) |
| [Veto structural destruction for one kind of craft](./features/crash-veto.md) | vehicles-and-parts | startup:asset-load, startup:all-mods-loaded, frame:apply-solvers, frame:input, physics:vehicle-solver, event:vehicle-destroyed |
| [Spawn a saved craft parked at a latitude and longitude](./features/spawn-parked-craft.md) | vehicles-and-parts | frame:after-on-frame, frame:input |
| [Inject wheel forces from a Vehicle.PrepareWorker prefix](./features/prepareworker-impulse-injection.md) | physics | startup:all-mods-loaded, frame:dispatch-solvers |
| [Wheel, suspension and tyre model](./features/wheel-suspension-tyre-model.md) | physics | frame:dispatch-solvers |
| [Terrain contact under each wheel](./features/terrain-contact-under-wheels.md) | physics | frame:dispatch-solvers |
| [Rocket lift, hover, boost and downforce as velocity deltas](./features/rocket-lift-hover-boost-downforce.md) | physics | frame:dispatch-solvers, frame:draw-ui |
| [Switchable scoop colliders and rocks that come loose](./features/scoop-colliders-and-loose-clutter.md) | physics | startup:asset-load, frame:draw-ui, frame:after-on-frame, frame:dispatch-solvers, physics:bepu |
| [Launch pad surface contact](./features/launch-pad-surface-contact.md) | celestials | frame:dispatch-solvers |
| [Seats and EVA doors](./features/seats-and-eva-doors.md) | kittens | startup:asset-load, event:eva |
| [Driver kitten's hands on the steering wheel](./features/driver-hands-steering-ik.md) | kittens | frame:draw-ui, frame:render |
| [Seated crew of unflown cars](./features/seated-crew-of-unflown-craft.md) | kittens | frame:render |
| [Pose subparts at runtime](./features/pose-subparts-at-runtime.md) | rendering | frame:draw-ui, frame:after-on-frame |
| [Headlamps and tail lamps as KSA spotlights](./features/headlights-as-spotlights.md) | rendering | startup:all-mods-loaded, frame:draw-ui, frame:render |
| [Coloured emissive lenses per subpart](./features/coloured-emissive-lenses.md) | rendering | startup:all-mods-loaded, frame:render |
| [Volumetric exhaust plumes with no engine behind them](./features/exhaust-plumes-without-engine.md) | rendering | startup:all-mods-loaded, frame:pre-render, render:exhaust |
| [Engine sound from two crossfaded, re-pitched loops](./features/engine-sound-crossfade.md) | audio | startup:asset-load, frame:draw-ui, frame:after-on-frame, frame:post-render |
| [Rocket roar from KSA's stock engine sound, with no engine](./features/rocket-sound-without-engine.md) | audio | frame:draw-ui, frame:after-on-frame, frame:post-render |
| [Drive with the flight keys](./features/drive-with-flight-keys.md) | ui | frame:input, frame:dispatch-solvers |
| [KSA's engine panel and throttle gauge for a craft with no engine](./features/engine-hud-for-engineless-craft.md) | ui | startup:all-mods-loaded, frame:draw-ui, frame:gauges, frame:dispatch-solvers |
| [Car panel and ModMenu entry](./features/car-panel-and-modmenu.md) | ui | frame:draw-ui |
| [Craft mover with mouse picking and ground placement](./features/craft-mover-mouse-pick-place.md) | ui | frame:draw-ui, frame:input |
| [Keep a craft with mod thrust off rails](./features/keep-thrusting-craft-off-rails.md) | patching | startup:all-mods-loaded, frame:dispatch-solvers, physics:vehicle-solver |
| [File-drop command bridge](./features/file-drop-command-bridge.md) | testing-and-tooling | startup:all-mods-loaded, frame:after-on-frame |
| [KSA MCP server over the bridge](./features/ksa-mcp-server.md) | testing-and-tooling | (outside the game) |
| [API surface pinning and KSA upgrade diffing](./features/api-surface-pinning-and-diff.md) | testing-and-tooling | (build time) |
| [Headless simulation tests and the Sim/Ksa boundary](./features/headless-sim-tests.md) | testing-and-tooling | (build time) |
| [Build, deploy, package and release pipeline](./features/package-deploy-release.md) | testing-and-tooling | (build time) |

## Build, test and tooling

- **Build**: .NET 10 SDK (`global.json`), `tools/build.sh`; the game's DLLs come from `KSA_DLL_DIR`,
  `Import/`, a sibling `ksa-game-assemblies` checkout or the game install.
- **Test**: `tools/test.sh` runs the headless suite over `Sim/` (no game needed); `tools/check-all.sh`
  runs every CI check and is the pre-push hook.
- **Deploy and run**: `tools/deploy.sh` (validates parts, writes the `developer` marker),
  `tools/run.sh` (launch through StarMap from WSL and tail the log), `tools/install-testcraft.sh`.
- **In-game automation**: the developer-only file bridge and `tools/ksa-mcp/server.py`.
- **Upgrades**: `ksa-assemblies.lock`, `tools/api-surface.sh`, `tools/ksa-api-diff.sh`,
  `tools/decompile-assemblies.sh`, the daily `ksa-version.yml` workflow and the `upgrade-ksa` skill.
- **Release**: Conventional Commits, semantic-release on `main`, a draft GitHub release completed
  by a packaging job, SpaceDock publishing.

## Compatibility

- Built and documented against KSA build **2026.10.10.5554** (the lock's `build` line and the
  README); `Build` logs the compiled-for and running builds at load.[^ksacars-lock]
- KSA has no official code-modding API. Breakage patterns the repo guards against: signature changes
  on the seven public Harmony targets (compile errors through `PinTheSignature`), renamed private
  targets (`PartTreeRenderData.WriteState`, `PhysicsBubble.KittenWantsWake`; each checks at install
  and switches off with a warning), renamed reflected fields (`Vehicle._manualControlInputs`,
  `_engineFlags`, `KittenRenderable._characterAvatar`, `_seatedIdleActionAnims`,
  `Program._screenshotCapture`; each degrades with a warning), and members that keep their name but
  change meaning (only the decompiled diff finds those).[^ksacars-surface][^ksacars-upgrade-skill]
- `docs/BLOCKED-ON-KSA.md` lists what the cars cannot do or do only around the engine (no wheels,
  one terrain friction for every collider, crew drawn only for the flown craft, the private kitten
  animator, Fill Seats seating a kitten already on EVA) and must be rechecked after each KSA update.[^ksacars-blocked]
- A shipped part's subpart list is append-only: removing a subpart crashes every save holding the
  part.
- `CLAUDE.md` states that both cars were driven through the bridge before the repository was split
  out and that nothing has been seen in game since the split; `CHECKLIST.md` tracks what has been
  driven.[^ksacars-checklist]

## Secondary sources

| Document | Worth reading for |
|---|---|
| [CLAUDE.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/CLAUDE.md) | How a car is driven, adding a car, CI and releases, the KSA update procedure |
| [docs/KSA-MODDING-NOTES.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/docs/KSA-MODDING-NOTES.md) | StarMap contract, reference frames, spawning vehicles and EVA kittens, part XML gates, atlas loader rules, sound, explosions, particles, clutter, buoyancy, dents |
| [docs/KSA-FRAME-ORDER.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/docs/KSA-FRAME-ORDER.md) | The frame in order with decomp line references (older build), sim step and epoch rules[^ksacars-frame-order] |
| [docs/FRAMES-AND-EPOCHS.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/docs/FRAMES-AND-EPOCHS.md) | Why Ecl samples taken at different instants drift by ~500 m per frame[^ksacars-epochs] |
| [docs/KSA-CAMERAS.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/docs/KSA-CAMERAS.md) | Cameras and viewports[^ksacars-cameras] |
| [docs/KSA-TERRAIN.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/docs/KSA-TERRAIN.md) | Height-field resolution, quantisation, single-precision modifiers, launch-site decals[^ksacars-terrain] |
| [docs/KSA-API-SURFACE.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/docs/KSA-API-SURFACE.md) | The generated list of 370 members the DLL binds to |
| [docs/BLOCKED-ON-KSA.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/docs/BLOCKED-ON-KSA.md) | Engine limitations and what would unblock each |
| [.claude/skills/ksa-blender/SKILL.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/.claude/skills/ksa-blender/SKILL.md) | Authoring art in Blender over MCP and the GLB export contract[^ksacars-blender-skill] |
| [.claude/skills/upgrade-ksa/SKILL.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/.claude/skills/upgrade-ksa/SKILL.md) | The KSA upgrade procedure as an agent skill |
| [CONTRIBUTING.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/CONTRIBUTING.md) | Contribution rules[^ksacars-contributing] |

[^ksacars-readme]: README.md
[^ksacars-claude]: CLAUDE.md
[^ksacars-modtoml]: src/KSACars/mod.toml
[^ksacars-csproj]: src/KSACars/KSACars.csproj
[^ksacars-mod]: src/KSACars/Ksa/KSACarsMod.cs
[^ksacars-lock]: ksa-assemblies.lock
[^ksacars-notes]: docs/KSA-MODDING-NOTES.md
[^ksacars-frame-order]: docs/KSA-FRAME-ORDER.md
[^ksacars-epochs]: docs/FRAMES-AND-EPOCHS.md
[^ksacars-cameras]: docs/KSA-CAMERAS.md
[^ksacars-terrain]: docs/KSA-TERRAIN.md
[^ksacars-surface]: docs/KSA-API-SURFACE.md
[^ksacars-blocked]: docs/BLOCKED-ON-KSA.md
[^ksacars-checklist]: CHECKLIST.md
[^ksacars-contributing]: CONTRIBUTING.md
[^ksacars-blender-skill]: .claude/skills/ksa-blender/SKILL.md
[^ksacars-upgrade-skill]: .claude/skills/upgrade-ksa/SKILL.md
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^starmap-modlibrarypatches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^starmap-modpatches]: StarMap.Core/Patches/ModPatches.cs
[^ksa-program]: KSA.Program (decompiled)
[^ksa-universe]: KSA.Universe (decompiled)
[^starmap-core]: StarMap.Core/StarMapCore.cs
[^starmap-modloader]: StarMap.Core/ModRepository/ModLoader.cs
