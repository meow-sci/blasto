---
type: Game Feature
title: Export live game state to background threads with immutable snapshots and a fixed sample clock
description: Keep transport threads away from KSA objects by building one immutable snapshot on the game thread and publishing it with a single volatile swap, sampled at a drift-free fixed cadence
category: external-io
group: telemetry
topic: export-game-state-to-other-threads
demonstrated_by:
  - mods/gatos/features/telemetry-sampler-snapshot
  - mods/ksa-public-telemetry/features/live-flight-telemetry-sampling
lifecycle: [frame:viewports, frame:draw-ui, frame:after-on-frame, thread:background]
game_types: [KSA.Universe, KSA.Program, KSA.Vehicle, KSA.CelestialSystem, KSA.App]
tags: [telemetry, snapshot, immutable, volatile, sampling, threading, sample-clock]
---

KSA objects belong to the main thread. A mod that serves telemetry to a socket, a file system, a message broker or a subprocess cannot hand those objects to another thread: the vehicle step runs on workers on copies, `Vehicle` and `Orbit` fields change when the next step is applied, and a vehicle can be torn down while a reader is looking at it. The fix is to copy once, on the game thread, into a plain immutable value, and let every other thread read only that value.

This topic covers both halves of the problem: how to hand the value to other threads (a volatile reference swap, with async waiters), and how often to build it (a fixed cadence that does not drift or replay a backlog after a hitch).

## Engine background

| Type or member | File | Why it matters |
|---|---|---|
| `Program.PrepareFrame` and `Universe.ApplyVehicleSolvers` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952) | The finished physics step is copied into the main-thread `Vehicle` objects here, early in the frame; reads in later hooks see that applied state |
| `Vehicle.PrepareWorker` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423) | The next step starts on worker copies, so main-thread fields are stable until the next apply |
| `Universe.GetElapsedSeconds()`, `Universe.GetLastSimStep()` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2392-L2396) | Simulation time and last step length (`DeltaTime` 0 means effectively paused) |
| `Universe.CurrentSystem.All`, `Program.ControlledVehicle` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L64-L64) | The vehicle list and the active vessel |
| `App.Run` | [current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L39-L74) | Computes `dtPlayer`, clamped to `1 / MinTargetFrameRate`; this is the `dt` a sample clock receives |

Where to sample is a timing choice: the main-viewport `GameViewport.OnFrame` prefix, `StarMapBeforeGui`, `StarMapAfterGui` or `StarMapAfterOnFrame` all run after `PrepareFrame` and so see the same applied state; they differ in whether they run with the HUD hidden. See [the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [the hidden-HUD table](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-imgui-window).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: sampler builds an immutable snapshot, volatile swap, TaskCompletionSource wake](export-game-state-to-other-threads-1.md) | gatos | `immutable-snapshot` | `frame:viewports`, `thread:background` | Lock-free multi-reader and async waiters; allocates a snapshot per sample and needs a published value type |
| [2: plain-struct snapshot sampled after the frame, drawn next frame](export-game-state-to-other-threads-2.md) | ksa-public-telemetry | `snapshot-copy` | `frame:after-on-frame`, `frame:draw-ui` | No threads, no locks, one frame of latency; only valid when every reader is on the main thread |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Read vehicle flight state](../../../flight-and-orbits/flight-state/read-vehicle-flight-state/overview.md), [Compute mission elapsed time and detect liftoff](../../../flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/overview.md)
- [Marshal commands from background threads to the game thread](../../control/marshal-commands-to-the-game-thread/overview.md): the write-side counterpart
- [Host HTTP and MQTT servers in the game process](../../network/host-http-and-mqtt-servers-in-the-game-process/overview.md), [Detect flight events when the game has no event bus](../detect-flight-events-from-snapshots/overview.md), [Record telemetry and diagnostics to files](../record-telemetry-and-diagnostics-to-files/overview.md)
- [Schedule timed command sequences](../../control/schedule-timed-commands/overview.md)
- Measuring and budgeting mod performance is covered in the testing-and-tooling category.
- [Measure what a mod costs per frame, throttle expensive work and warm up hot code](../../../testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/overview.md) - related topic

