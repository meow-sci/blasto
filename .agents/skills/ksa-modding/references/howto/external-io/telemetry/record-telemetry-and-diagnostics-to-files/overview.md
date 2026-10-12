---
type: Game Feature
title: Record telemetry and diagnostics to files without stalling the frame
description: Write flight telemetry to files for later analysis, as an on-demand export of a history buffer, a continuous per-step recorder that never throws and never waits on the disk, or one-shot latched validation dumps
category: external-io
group: telemetry
topic: record-telemetry-and-diagnostics-to-files
demonstrated_by:
  - mods/kitten-engineer-redux/features/telemetry-history-and-csv-export
  - mods/ksa-advancedflightcomputer/features/guidance-flight-telemetry-csv
  - mods/ksa-deltavmap/features/in-game-validation-dumps-and-perf
  - mods/ksa-deltavmap/features/offline-layout-and-math-tests
lifecycle: [frame:draw-ui, frame:dispatch-solvers]
game_types:
  - KSA.Vehicle
  - KSA.Universe
  - KSA.Program
  - KSA.App
  - KSA.CelestialSystem
  - KSA.Celestial
  - Brutal.Logging.DefaultCategory
  - Brutal.Logging.LogCategory
tags: [csv, logging, telemetry, diagnostics, buffered-io, svg, dedupe]
---

A mod that needs data after the fact (a landing that went wrong, a graph the player wants in a spreadsheet, a layout that must be eyeballed) writes it to a file. Three shapes appear in the reference mods and they differ in who triggers the write, how often rows are produced, and how disk errors are contained:

- an **on-demand export** of an in-memory history that the HUD already fills;
- a **continuous recorder** fed every simulation step, with buffered writes and an exception-proof sink;
- a **one-shot dump** that fires once when the universe exists, built by pure code that offline tests also run.

All three run on the main thread. None may stall the frame or throw into the render path.

## Engine background

| Type or member | Use | File |
|---|---|---|
| `Constants.DocumentsFolderPath`, `LogsFolderPath` | Root for exports and logs: `<Documents>/My Games/Kitten Space Agency` and its `logs` folder | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L283) |
| `Program.GetPlayerTime()` | Wall-clock time of the current frame (the unclamped `App.Run` stopwatch value); keeps counting while paused and ignores time warp | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5263-L5266) |
| `App.Run` | Computes `dtPlayer`, clamped to `1 / MinTargetFrameRate` | [current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L39-L74) |
| `Universe.GetElapsedSeconds()`, `Universe.GetLastSimStep()` | Simulation time and step length, the clocks for time-warp-correct rows | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2392-L2396) |
| `Vehicle.PrepareWorker` | Called per vehicle on the main thread inside `Universe.ExecuteNextVehicleSolvers`, once per physics step | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423) |
| `Universe.CurrentSystem` | Null until a system is loaded; the guard for one-shot dumps | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L114-L114) |
| `Brutal.Logging.DefaultCategory.Log` | The game's log (`Info`, `Warning`, `Error`, `Debug`) | [current/decomp/Brutal.Logging/DefaultCategory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/DefaultCategory.cs) |

See [the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for where `frame:draw-ui` and `frame:dispatch-solvers` sit relative to the physics step.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: wall-clock interval feeding a history array, CSV on click](record-telemetry-and-diagnostics-to-files-1.md) | kitten-engineer-redux | `telemetry-sampling` | `frame:draw-ui` | Simple, no extra hooks; ticks while paused, ignores warp, synchronous export |
| [2: static single-owner buffered sink](record-telemetry-and-diagnostics-to-files-2.md) | ksa-advancedflightcomputer | `buffered-file-sink` | `frame:dispatch-solvers` | Per-step rows with exact numbers and bounded I/O cost; one owner, batches lost on failure |
| [3: latched dumps from StarMapAfterGui behind a flag](record-telemetry-and-diagnostics-to-files-3.md) | ksa-deltavmap | `starmap-lifecycle` | `frame:draw-ui` | Zero cost in release; one shot per session and a hardcoded directory |

## See also

- [Export live game state to background threads](../export-game-state-to-other-threads/overview.md): the fixed-interval sample clock for sim-time sampling
- [Compute mission elapsed time and detect liftoff](../../../flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/overview.md), [Schedule timed command sequences](../../control/schedule-timed-commands/overview.md)
- [Write a per-mod log file](../../../getting-started/lifecycle/write-a-per-mod-log-file/overview.md)
- Related topics in other categories: drawing a monospace text grid, driving a running game through a file bridge, measuring and budgeting mod performance, unit-testing mod logic without the game, laying out a tree or graph diagram and testing a solver or controller offline
- [Drive a running game from scripts through a file-drop command bridge](../../../testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/overview.md) - related topic
- [Measure what a mod costs per frame, throttle expensive work and warm up hot code](../../../testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/overview.md) - related topic
- [Unit-test mod logic without launching the game](../../../testing-and-tooling/offline-tests/unit-test-mod-logic-without-game/overview.md) - related topic
- [Lay out a tree or graph diagram and keep it usable at scale](../../../ui/draw-lists/lay-out-a-tree-or-graph-diagram/overview.md) - related topic
- [Test a solver or controller offline: closed loop, purity and replay](../../../testing-and-tooling/offline-tests/test-a-solver-or-controller-offline/overview.md) - related topic
- [Draw per-frame text efficiently: monospace grids and allocation-free labels](../../../ui/draw-lists/draw-a-monospace-text-grid/overview.md) - related topic

