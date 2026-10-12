---
type: Mod
title: AdvancedFlightComputer
description: Maneuver quick-tools, flyby targeting and multi-pass burns in the stock Transfer Planner, RCS-only burns, automatic staging and burn removal, hyperbolic targets, and closed-loop powered guidance for ascent, boostback and landing with native convex solvers
mod: ksa-advancedflightcomputer
tags:
  - maneuver-planning
  - transfer-planner
  - multi-pass
  - rcs
  - staging
  - guidance
  - upfg
  - g-fold
  - scvx
  - native-interop
  - harmony
  - testing
repo: KSA-AdvancedFlightComputer
license: MIT
author: Maximilian Nesslauer and Oliver Cairns (cairn5)
loader: starmap
mod_id: AdvancedFlightComputer
---

AdvancedFlightComputer (AFC) extends the stock flight computer and Transfer Planner instead of replacing
them. Planning features add plan types and controls to the stock planner and create ordinary stock burns;
execution features hook the stock Auto burn path; powered guidance flies vehicles through the stock
flight computer with closed-loop guidance laws and two vendored native convex solvers. It is written by
Maximilian Nesslauer, with the powered guidance originally the PoweredGuidance mod by Oliver Cairns
(cairn5), co-author of the project ([README.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/README.md), [LICENSE](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/LICENSE)). At about 80,000 lines of C# in 322
files (including tests) with roughly 30 Harmony patch targets, it is the largest and most heavily tested
mod in this catalog, and its comments document the game's frame order in unusual detail.

## Install footprint

`mod.toml` ([AdvancedFlightComputer/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/mod.toml)):

```toml
# AdvancedFlightComputer/mod.toml L1-L9
name = "AdvancedFlightComputer"
patches = [ "Patches/HyperbolicBodies.xml", "Patches/AutoStageButton.xml" ]

[StarMap]
EntryAssembly = "AdvancedFlightComputer"

[[StarMap.ModDependencies]]
ModId = "KittenExtensions"
Optional = true
```

Manifest id `AdvancedFlightComputer`; installed into `Documents\My Games\Kitten Space Agency\mods\AdvancedFlightComputer\`.

| Deployed file | Purpose |
|---|---|
| `AdvancedFlightComputer.dll`, `mod.toml` | Entry assembly and manifest |
| `AdvancedFlightComputer.Guidance.{Numerics,Conic,Gfold,Scvx}.dll` | Game-free numerical assemblies |
| `clarabel_c.dll`, `scs.dll` / `libclarabel_c.so`, `libscs.so` | Native conic solvers, one pair per platform |
| `Patches/HyperbolicBodies.xml`, `Patches/AutoStageButton.xml` | KittenExtensions XML patches |
| `LICENSE`, `THIRD-PARTY-NOTICES.md`, `licenses/*.txt` | License texts for vendored code |

Files AFC writes at run time beside the mod: `autostage.toml`, `autostage-vehicles/<vehicle-id>.toml`,
`autoremove.toml`, `multipass.toml`, `rcs-exec.toml`. Nothing is written into game saves.

## Loader model and entry class

One StarMap entry class, `AdvancedFlightComputer.Mod` ([AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs)). Every feature is
applied manually in its own Harmony owner (`com.maxi.advancedflightcomputer.<Feature>`) after its
reflection handles validate (see [feature-gated patch sets](features/feature-gated-patch-sets.md)).

| Hook | Where StarMap fires it | What AFC does |
|---|---|---|
| `[StarMapImmediateLoad]` | Prefix on `KSA.Mod.PrepareSystems` ([StarMap.Core/Patches/ModPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModPatches.cs)) | Registers the AUTOSTAGE gauge enum before `Gauges.xml` binds it |
| `[StarMapAllModsLoaded]` | Postfix on `ModLibrary.LoadAll` ([StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs)) | Version check, validation and every feature's patch block |
| `[StarMapAfterGui]` | Postfix on `Program.OnDrawUiViewports` ([StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs)) | Guidance panel and overlays |
| `[StarMapUnload]` | `ModLoader.Dispose`, from `StarMapCore.DeInit` after the game's `Main` has returned and after `Harmony.UnpatchAll()` removed every patch ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)) | Ordered teardown: flush staging, unpatch, reset registries; under StarMap the patches are already gone and no frame follows (see [feature-gated patch sets](features/feature-gated-patch-sets.md)) |

## Dependencies

| Dependency | Kind | Notes |
|---|---|---|
| StarMap | Required loader (tested 0.4.6); `StarMap.API` 0.3.6 at build | Per-mod `AssemblyLoadContext` resolves the satellite assemblies ([StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs)) |
| Lib.Harmony 2.4.2 | Build reference; supplied at run time by StarMap | All patches |
| KittenExtensions | Optional (tested v0.4.0) | Applies the two XML patches; without it no hyperbolic targets and no AUTOSTAGE gauge button ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs)) |
| Clarabel (Apache-2.0), SCS (MIT) | Vendored native code | Built with Rust and Zig, loaded through a `DllImportResolver` |
| HeadlessHarness | Test-only | The separate test mod depends on it |

The standalone AutoStage, AutoRemoveFinishedBurns and PoweredGuidance mods are merged into AFC and must be
deleted, not disabled.

## Architecture

| Subsystem | Folder | Main hooks |
|---|---|---|
| Core | `AdvancedFlightComputer/Core` | `Universe.ApplyVehicleSolvers` tick, `FlightComputer.RaisePendingAlerts` burn end, `FlightComputer.ComputeControl` command sink, `UncompressedSave.Load/Write`, `Vehicle.Dispose/SetName`, Mods settings transpiler |
| Planner | `Features/ManeuverTools`, `Features/PlanWindow` | `TransferPlanner.DrawPlanWindow` prefix, transpiler and postfix; `TransferPlanner.OnPreRender`; `BurnContextMenu.Draw` transpiler |
| Flyby and multi-pass | `Features/Flyby`, `Features/MultiPass` | Inline UI via the plan-window transpiler; execution via the shared tick and burn-end hooks |
| Hyperbolic targets | `Features/HyperbolicTargets` | Planner, `OrbitalTransfers`, `RefineBurnTask`, `PatchedConic` patches plus an XML patch |
| RCS burns | `Features/RcsTranslation` | `Vehicle.SetEnum` prefix, command sink writer, burn editor and gauge patches, `Universe.AutoWarpTo` |
| Staging and burn removal | `Features/AutoStage`, `Features/AutoRemove` | Shared tick, `Vehicle.PrepareWorker`, `SequenceList`, gauge enum, `Part.DrawPartInfo` |
| Guidance | `Features/Guidance` | `Vehicle.PrepareWorker` prefix, `FlightComputer.UpdateAttitudeTarget` postfix, `Program.DrawProgramMenusHook` postfix |

Frame placement (verified against `Program.PrepareFrame` in the decomp, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2222)):

1. `Universe.ApplyVehicleSolvers` prefix: staging samples the previous state.
2. Its postfix (`frame:apply-solvers`): AutoStage, multi-pass and the RCS executor tick with fresh physics
   results, before the input drain (see [post-solver vehicle tick](features/post-solver-vehicle-tick.md)).
3. `InputEvents.ApplyInputEvents` (`frame:input`): queued burn adds and gauge clicks; `Vehicle.SetEnum`
   interception starts RCS burns.
4. `FlightComputer.RaisePendingAlerts` postfix (`frame:prepare`): multi-pass and automatic removal react to the
   burn stock just ended (see [stock burn end detection](features/stock-burn-end-detection.md)).
5. `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`): the guidance step and the new-engine seed write
   what the next physics step will see (see [guidance vehicle step loop](features/guidance-vehicle-step-loop.md)).
6. Vehicle workers (`physics:vehicle-solver`): `ComputeControl` postfix writes RCS pulses and gimbals;
   `UpdateAttitudeTarget` postfix adds the guidance rate feedforward.
7. Draw (`frame:draw-ui`) and pre-render (`frame:pre-render`): planner windows, previews, panels, overlays.
   The next physics step is already running, so nothing here writes the flight computer directly.
8. Render (`frame:render`): the gauge pass packs the AUTOSTAGE and Auto button state through
   `GaugeButtonFlightComputer.PackData`.

The flight computer the workers fly is a copy taken in `Vehicle.PrepareWorker` and written back over
the vehicle's at the next `ApplyVehicleSolvers`, so steps 2 to 5 are the only places where a
flight-computer write reaches the next step (see
[flight-computer worker copy channels](features/flight-computer-worker-copy-channels.md)).

| Thread | Owner | Synchronization |
|---|---|---|
| Main | All patches except the worker-side ones below | Game frame order |
| Vehicle workers | `ComputeControl` and `UpdateAttitudeTarget` postfixes; the `PatchedConic.FindClosestApproaches` prefix when flight plans are propagated there | Immutable commands keyed by `BurnPlan` or `VehicleConfigInfo`, published before the solver job is queued |
| `ThreadPool` (stock) | Porkchop and refine tasks reached by hyperbolic-target patches | Patches touch only thread-safe state; locked logging |
| `afc-6dof-solve` | 6-DOF SCvx solver, one per guided vehicle | Lock-protected single request/result slot, immutable inputs |
| `afc-ascent-scvx` | Convex ascent solve | Plain-data problem built on the main thread, immutable plan returned |

## Features

| Feature | Category | Lifecycle |
|---|---|---|
| [MSBuild deploy and packaging for a StarMap mod with native payload](features/mod-deploy-msbuild-targets.md) | getting-started | build time |
| [Stand down for a superseded standalone mod and import its settings](features/superseded-mod-detection-and-settings-import.md) | getting-started | `startup:all-mods-loaded` |
| [Warn-once logging, debug switches and a perf scope](features/warn-once-logging-and-diagnostics.md) | getting-started | `frame:draw-ui`, `frame:apply-solvers`, `physics:vehicle-solver`, `thread:background` |
| [Feature-gated Harmony patch sets with rollback](features/feature-gated-patch-sets.md) | patching | `startup:all-mods-loaded`, `event:mod-unload` |
| [Inject UI and swap calls inside the stock plan window with anchored transpilers](features/plan-window-transpiler-injection.md) | patching | `startup:all-mods-loaded`, `frame:draw-ui` |
| [Main-thread vehicle tick right after the solver apply](features/post-solver-vehicle-tick.md) | patching | `frame:apply-solvers` |
| [Reach the physics worker through the references FlightComputer.CopyFrom shares](features/flight-computer-worker-copy-channels.md) | patching | `frame:apply-solvers`, `frame:dispatch-solvers`, `physics:vehicle-solver` |
| [One ComputeControl postfix as an actuator command sink](features/flight-computer-command-sink.md) | patching | `physics:vehicle-solver` |
| [Validated reflection handles with per-feature tags](features/validated-reflection-handles.md) | patching | `startup:all-mods-loaded` |
| [A custom gauge button action backed by a mod enum](features/autostage-gauge-button.md) | ui | `startup:immediate-load`, `startup:asset-load`, `frame:draw-ui`, `frame:render`, `frame:input` |
| [A detachable console-window panel and world-space trajectory overlays](features/guidance-panel-and-overlays.md) | ui | `frame:draw-ui` |
| [Entries in the stock orbit right-click menu](features/orbit-context-menu-shortcuts.md) | ui | `frame:draw-ui` |
| [Player messages through the game's timed alerts](features/stock-timed-alerts.md) | ui | `frame:apply-solvers`, `frame:prepare`, `frame:draw-ui` |
| [RCS options in the burn editor and a re-enabled Auto gauge button](features/rcs-burn-editor-and-gauge-ui.md) | ui | `frame:draw-ui`, `frame:render` |
| [Sections on the stock Mods settings page](features/mods-settings-page-sections.md) | ui | `frame:draw-ui` |
| [Stock-styled rows inside game console windows](features/stock-styled-console-ui.md) | ui | `frame:draw-ui` |
| [Save-scoped TOML registries beside the mod](features/save-scoped-sidecar-registries.md) | persistence | `event:save-load`, `event:save-write`, `event:vehicle-destroyed` |
| [6-DOF successive-convexification landing as a receding-horizon MPC on a solver thread](features/scvx-6dof-landing-mpc.md) | flight-and-orbits | `frame:dispatch-solvers`, `thread:background`, `physics:vehicle-solver` |
| [Aim a transfer at a flyby periapsis instead of the body center](features/flyby-periapsis-targeting.md) | flight-and-orbits | `frame:draw-ui` |
| [Booster boostback with a drag-integrated impact predictor](features/boostback-impact-prediction.md) | flight-and-orbits | `frame:dispatch-solvers` |
| [Confirm a planned flyby by propagating it through the game's patched conics](features/flyby-encounter-prediction.md) | flight-and-orbits | `frame:draw-ui`, `frame:pre-render` |
| [Convert a vehicle's state into a landing-site frame through the game's own transforms](features/vehicle-state-to-site-frame.md) | flight-and-orbits | `frame:dispatch-solvers`, `frame:draw-ui` |
| [Create a stock maneuver node from a computed impulse](features/planner-burn-node-creation.md) | flight-and-orbits | `frame:draw-ui`, `frame:input` |
| [Custom plan types in the stock Transfer Planner](features/transfer-planner-custom-plan-types.md) | flight-and-orbits | `startup:all-mods-loaded`, `frame:draw-ui`, `event:mod-unload` |
| [Detect which planned burn stock just ended](features/stock-burn-end-detection.md) | flight-and-orbits | `frame:prepare` |
| [G-FOLD fuel-optimal powered descent solved in-frame with Clarabel](features/gfold-powered-descent.md) | flight-and-orbits | `frame:dispatch-solvers` |
| [Launch window to a target vehicle's plane, co-elliptic chase orbit and warp to the window](features/launch-window-to-target-plane.md) | flight-and-orbits | `frame:dispatch-solvers`, `frame:draw-ui`, `frame:input` |
| [Minimum-propellant ascent planned offline with SCvx and flown against air-relative speed](features/convex-ascent-scvx.md) | flight-and-orbits | `frame:dispatch-solvers`, `thread:background`, `frame:draw-ui` |
| [Multi-pass execution as a per-vehicle state machine on stock Auto burns](features/multi-pass-execution.md) | flight-and-orbits | `frame:apply-solvers`, `frame:prepare`, `frame:input` |
| [Multi-pass Hohmann departures with a period schedule and a shifted window](features/multi-pass-hohmann-schedule.md) | flight-and-orbits | `frame:draw-ui`, `frame:apply-solvers` |
| [Per-vehicle autopilot step in a Vehicle.PrepareWorker prefix](features/guidance-vehicle-step-loop.md) | flight-and-orbits | `frame:dispatch-solvers`, `frame:draw-ui`, `event:save-load`, `event:vehicle-destroyed` |
| [Plan a deorbit to a landing site and let stock Auto fly the node](features/deorbit-landing-planner.md) | flight-and-orbits | `frame:dispatch-solvers`, `physics:vehicle-solver` |
| [Preview a planned burn's trajectory with the game's own orbit lines](features/planner-preview-trajectories.md) | flight-and-orbits | `frame:draw-ui`, `frame:pre-render` |
| [RCS-only burn execution through the stock Auto button](features/rcs-burn-execution.md) | flight-and-orbits | `frame:input`, `frame:apply-solvers`, `physics:vehicle-solver` |
| [Remove the last finished Auto burn](features/auto-remove-finished-burns.md) | flight-and-orbits | `frame:prepare` |
| [Set apse, match and set inclination with an ecliptic or equatorial reference](features/apse-and-inclination-quick-tools.md) | flight-and-orbits | `frame:draw-ui` |
| [Split a maneuver into passes with a forward-chained flight plan](features/multi-pass-burn-splitting.md) | flight-and-orbits | `frame:draw-ui`, `frame:apply-solvers` |
| [Steer an unpowered descent onto a site with angle of attack, using a measured lift curve and an impact Jacobian](features/aerodynamic-glide-impact-steering.md) | flight-and-orbits | `frame:dispatch-solvers` |
| [Steer through the stock flight computer with a rate feedforward and direct gimbal commands](features/guidance-attitude-and-gimbal-actuation.md) | flight-and-orbits | `frame:dispatch-solvers`, `physics:vehicle-solver` |
| [Terminal hover and touchdown with keypad nudges](features/terminal-hover-touchdown.md) | flight-and-orbits | `frame:dispatch-solvers`, `frame:draw-ui` |
| [Transfer Planner targets on hyperbolic orbits](features/hyperbolic-transfer-targets.md) | flight-and-orbits | `startup:asset-load`, `frame:draw-ui`, `thread:background`, `physics:vehicle-solver` |
| [UPFG ascent to orbit on KSA's staging model](features/upfg-ascent-guidance.md) | flight-and-orbits | `frame:dispatch-solvers`, `frame:draw-ui` |
| [Vehicle control ownership, takeover detection and hand-back](features/vehicle-control-ownership.md) | flight-and-orbits | `frame:apply-solvers`, `frame:dispatch-solvers` |
| [Automatic staging that keeps Auto burns alive](features/automatic-staging.md) | vehicles-and-parts | `frame:apply-solvers`, `frame:input`, `frame:dispatch-solvers` |
| [Per-stage thrust, Isp and burn time from the game's staging simulator](features/stage-model-from-staging-simulator.md) | vehicles-and-parts | `frame:dispatch-solvers` |
| [Predict a jettison and drop spent boosters early](features/spent-stage-drop.md) | vehicles-and-parts | `frame:apply-solvers` |
| [RCS jet selection with axis groups or a linear program](features/rcs-thruster-allocation.md) | vehicles-and-parts | `frame:apply-solvers`, `physics:vehicle-solver` |
| [Staging delays per part variant and per sequence](features/staging-delays.md) | vehicles-and-parts | `frame:apply-solvers`, `frame:draw-ui`, `event:mod-unload` |
| [Thrust at ambient pressure, the structural throttle cap and thrust-to-throttle inversion](features/engine-performance-and-throttle-inversion.md) | vehicles-and-parts | `frame:dispatch-solvers` |
| [Measure a vehicle's drag table by calling the game's force function on a state copy](features/drag-table-measurement.md) | physics | `frame:dispatch-solvers`, `frame:draw-ui` |
| [Draw several predicted orbits with a brightness ramp and custom map markers](features/multi-pass-preview-rendering.md) | rendering | `frame:draw-ui`, `frame:pre-render` |
| [Optional KittenExtensions XML patches from mod.toml](features/optional-kittenextensions-patches.md) | assets | `startup:asset-load` |
| [Ship and call native solver libraries from a mod](features/native-conic-solver-interop.md) | external-io | `frame:dispatch-solvers`, `thread:background` |
| [Build a KSA mod in CI from private reference assemblies](features/ci-reference-assemblies.md) | testing-and-tooling | build time |
| [Build vendored native libraries for Windows and Linux and check the ABI against C#](features/native-solver-build-and-abi-check.md) | testing-and-tooling | build time |
| [Closed-loop MPC harness and solve-purity checks without the game](features/closed-loop-mpc-harness.md) | testing-and-tooling | build time |
| [Dump the in-game solver problem as JSON and replay it offline](features/solver-problem-json-replay.md) | testing-and-tooling | `frame:dispatch-solvers` |
| [Game-free numerical assemblies with console checks against Python references](features/game-free-guidance-checks.md) | testing-and-tooling | build time |
| [Guidance flight recorder with per-cycle CSV, plan snapshots and an event log](features/guidance-flight-telemetry-csv.md) | testing-and-tooling | `frame:dispatch-solvers`, `frame:draw-ui` |
| [In-game tests as a separate HeadlessHarness consumer mod](features/headless-harness-test-mod.md) | testing-and-tooling | `event:headless-test-run` |

## Build, test and tooling

- Build: `dotnet build AdvancedFlightComputer.slnx` against game assemblies in `$(KsaDir)`; deployment and
  packaging through shared MSBuild targets (see [mod deploy MSBuild targets](features/mod-deploy-msbuild-targets.md)).
- Native solvers: `build/build-clarabel.ps1` (Rust) and `build/build-scs.ps1` (Zig) with hash-pinned
  toolchains and an ABI check (see [native solver build and ABI check](features/native-solver-build-and-abi-check.md)).
- Tests: 72 in-game tests in a HeadlessHarness consumer mod (see
  [headless harness test mod](features/headless-harness-test-mod.md)) and game-free console checks against Python
  references (see [game-free guidance checks](features/game-free-guidance-checks.md)), plus a closed-loop MPC
  simulator and solve-purity checks (see [closed-loop MPC harness](features/closed-loop-mpc-harness.md)).
- Diagnostics: the convex ascent dumps the exact problem it built as JSON for offline replay (see
  [solver problem JSON replay](features/solver-problem-json-replay.md)), and the 6-DOF guidance can record
  every flight to CSV (see [guidance flight telemetry](features/guidance-flight-telemetry-csv.md)).
- CI: GitHub Actions builds natives on Windows and Linux and the mod against private reference assemblies;
  nightlies are promoted to releases without rebuilding (see [CI reference assemblies](features/ci-reference-assemblies.md)).

## Compatibility

- Tested against KSA build 2026.10.7.5541 (`TestedGameVersion` in `Mod.cs`); this catalog's decomp is build
  2026.10.10.5554. A mismatch only logs a warning.
- Release 0.10.0 was required for 2026.10.7.5541, where the stock flight computer started ending auto burns
  itself (removing a finished burn when another follows, loading the next, stopping warp, stopping a dry burn):
  AFC moved burn removal, multi-pass and RCS completion onto the stock end-of-burn path
  ([.github/release-notes/0.10.0.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/.github/release-notes/0.10.0.md)). The same area (`FlightComputer.RaisePendingAlerts`, `EndAutoBurn`,
  burn-mode handling in `Vehicle.PrepareWorker`) is the most likely to break again.
- Every reflection target and IL anchor is validated per feature at load and by the headless
  `afc-reflection-targets` test; a game rename disables one feature with a log line.
- Conflicts: the standalone AutoStage (detected by its gauge enum; AFC's stager stands down) and
  AutoRemoveFinishedBurns (detected by type; warns that the next burn may be lost). Delete their folders,
  because KittenExtensions applies XML patches of disabled mods too (see
  [superseded mod detection and settings import](features/superseded-mod-detection-and-settings-import.md)).
- The author states that of the guidance modes only single-craft ascent has been flown in the game from this
  tree; the others are covered by harness and console tests ([docs/guidance/README.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/guidance/README.md)).

## Secondary sources

| Document | Worth reading for |
|---|---|
| [docs/control-ownership.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/control-ownership.md) | Every flight-computer field AFC writes, who writes it and how it is released |
| [docs/guidance/runtime.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/guidance/runtime.md) | How guidance hangs on the mod: hooks, ownership, staging, panel |
| [docs/guidance/gfold.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/guidance/gfold.md) | G-FOLD formulation down to the matrices, backend measurements |
| [docs/guidance/scvx.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/guidance/scvx.md) | 3-DOF and 6-DOF SCvx as a specification |
| [docs/guidance/scvx-bridge.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/guidance/scvx-bridge.md) | From the 6-DOF solver to vehicle commands, MPC design |
| [docs/guidance/scvx-bridge-devnotes.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/guidance/scvx-bridge-devnotes.md) | Lessons from flying the 6-DOF path in KSA |
| [docs/guidance/reference-material/impact-steering.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/guidance/reference-material/impact-steering.md) | Impact-point Jacobian steering laws compared |
| [docs/guidance/native-build.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/guidance/native-build.md) | Native build, ABI check, CI |
| [docs/releasing.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/releasing.md) | Nightly and release process |
| [tests/AdvancedFlightComputer.HarnessTests/README.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/README.md) | Every in-game test and what it asserts |
