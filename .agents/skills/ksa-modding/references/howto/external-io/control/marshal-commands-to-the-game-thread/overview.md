---
type: Game Feature
title: Marshal commands from background threads to the game thread
description: Let transport threads submit immutable commands and await a result while every KSA mutation happens at one fixed point in the frame, with a second drain point for writes the vehicle solver would otherwise overwrite
category: external-io
group: control
topic: marshal-commands-to-the-game-thread
demonstrated_by: [mods/gatos/features/game-thread-command-queue, mods/gatos/features/solver-phase-command-drain]
lifecycle: [frame:viewports, frame:draw-ui, frame:after-on-frame, frame:dispatch-solvers, thread:background]
game_types: [KSA.Program, KSA.Universe, KSA.Vehicle, KSA.GameViewport]
tags: [command-queue, threading, harmony-prefix, errno, batch, solver]
---

A KSA mod that listens on a socket, a serial port or a file watcher receives requests on threads the game does not own. KSA objects (`Vehicle`, `FlightComputer`, `Universe`) belong to the main thread and, for simulated state, only to the settled window between `ApplyVehicleSolvers` and `ExecuteNextVehicleSolvers` (see [the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)). The pattern in this topic keeps transport code away from game objects: a transport thread builds an immutable command, enqueues it and awaits a result; the game thread drains the queue at a known point and is the only code that touches KSA.

The worked implementation is gatOS, whose command queue serves five transports (9p files, HTTP, MQTT, MCP, serial) and the timed-command scheduler. Two articles cover it: the queue itself, and the second drain point that exists because KSA's asynchronous vehicle solver overwrites some main-thread writes.

## Engine background

| Type | File | Why it matters |
|---|---|---|
| `Program.PrepareFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2235) | Waits for the previous physics step, applies it, polls input, then calls the `ExecuteNext*Solvers` methods |
| `Universe.ApplyVehicleSolvers`, `Universe.ExecuteNextVehicleSolvers` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2139) | The settled window sits between these two; `ExecuteNextVehicleSolvers` calls `PrepareVehicleWorkers` and then queues the vehicle job |
| `Vehicle.PrepareWorker` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423) | Copies `_manualControlInputs` and the `FlightComputer` into the worker's state, per vehicle |
| `GameViewport.OnFrame` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L119) | Runs every frame for each viewport, whether the HUD is shown or hidden |

Timing facts that shape the design (all main thread, build 2026.10.10.5554):

- The frame pipeline is one frame deep. `PrepareFrame` joins and applies step N-1, then starts step N; the rest of `OnFrame` (viewports, UI, render) runs while step N is in flight.
- The `StarMapBeforeGui` and `StarMapAfterGui` hooks fire only when the HUD is shown; `StarMapAfterOnFrame` fires every frame, and `GameViewport.OnFrame` runs for the main viewport on hidden-HUD frames too. A drain placed in a GUI hook silently stops when the player hides the UI.
- A write made to a field that `FlightComputer.CopyFrom` copies, after the worker snapshot was taken, is erased when the next apply copies the worker's copy back.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: two queues, TaskCompletionSource, timeout and abandon](marshal-commands-to-the-game-thread-1.md) | gatos | `command-queue` | `thread:background`, `frame:viewports` | Transport code is game-free and testable; frame-phase writes reach the solver one dispatch later and some fields are overwritten |
| [2: solver-window drain in a `Priority.First` prefix](marshal-commands-to-the-game-thread-2.md) | gatos | `harmony-prefix` | `frame:dispatch-solvers` | Flight-computer setpoints, refills and teleports stick; needs a Harmony anchor on `Universe.ExecuteNextVehicleSolvers` |

Use approach 1 alone for anything the solver does not restore (manual throttle, staging, UI-driven state). Add approach 2 for the specific writes listed in its article.

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Schedule timed command sequences on render, wall or sim time](../schedule-timed-commands/overview.md): the second producer on the same queue
- [Host HTTP and MQTT servers in the game process](../../network/host-http-and-mqtt-servers-in-the-game-process/overview.md) and [Let AI agents drive the game through an MCP server](../let-ai-agents-control-the-game-over-mcp/overview.md): transports that submit to the queue
- [Change vehicles without racing the solver](../../../vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/overview.md): the vehicle-side view of the same window
- [Host a terminal with pty sessions and frame handoff](../../terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/overview.md): the same thread rule applied to a terminal
