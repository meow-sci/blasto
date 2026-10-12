---
type: Mod Feature
title: Game-thread command queue
description: Every external write (file, HTTP, MQTT, MCP, serial) becomes an immutable command that a transport thread enqueues and awaits while the game thread drains, routes and executes it with errno-style results
mod: gatos
category: external-io
techniques: [starmap-lifecycle, harmony-prefix, background-thread, command-queue]
hooks:
  - harmony-prefix KSA.GameViewport.OnFrame
  - starmap StarMapBeforeGui
  - starmap StarMapAfterOnFrame
  - harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers
lifecycle: [frame:viewports, frame:draw-ui, frame:after-on-frame, frame:dispatch-solvers, thread:background]
game_types: [KSA.Program, KSA.Universe, KSA.Vehicle]
tags: [commands, threading, concurrency, errno, control, batch]
---

gatOS lets shell scripts, HTTP clients, MQTT publishers, AI agents and a serial port fly the
vessel, yet game state is only ever mutated on the main thread. The seam is one small,
game-free class: transport threads build an immutable `SimCommand`, enqueue it and await a
`CommandResult`; the game thread drains the queue at a fixed point in the frame and executes each
command through `KsaCatalog`, the only KSA-aware router. A rejected write comes back as a real
errno, so `echo 1 > ctl/stage` fails like any Unix write.

## How it works

**Command shape.** `SimCommand(VesselId, Action, Ordinal, Value)` plus optional `Values`
(vectors), `Token` and `Aux` (symbolic arguments). Actions are stable string keys such as
`vessel.ignite`, `engine.active`, `vessel.attitude_mode`. The game-thread phase is derived from
the action key, never passed by callers ([gatOS.SimFs/Commands/SimCommand.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/SimCommand.cs#L38-L96)).

**Queue.** `CommandQueue` holds two `ConcurrentQueue<Pending>`, one per phase
([gatOS.SimFs/Commands/CommandQueue.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/CommandQueue.cs#L29-L129)):

- `SubmitAsync` enqueues and awaits a `TaskCompletionSource` with a timeout
  (`command_timeout_ms`, default 2000). On timeout the pending entry is marked `Abandoned` so the
  game thread skips it, and the caller gets `ETIMEDOUT` (for example while the game is paused or loading).
- `SubmitBatchAsync` (`/sim/ctl/batch`, up to 64 commands) puts the whole group in one `Pending`
  so it executes in order inside one drain, same tick, never split; mixed phases are rejected.
- `Post` is fire-and-forget for the scheduler, reporting through an `IPostObserver` callback
  invoked inline on the game thread.
- TCSs use `RunContinuationsAsynchronously` so awaiting transport code never resumes inline on the
  game thread.

**Drain.** `Drain(phase, executor, maxCommands)` dequeues up to `max_commands_per_frame` (default
64) commands, executes each, and completes the waiter with the first failure of a batch annotated
with its position ([gatOS.SimFs/Commands/CommandQueue.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/CommandQueue.cs#L138-L181)).

**Routing and gates.** `KsaCatalog.Execute` ([gatOS.GameMod/Game/Ksa/KsaCatalog.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/KsaCatalog.cs#L30-L135)):

1. Refuses actions whose accessor is latched degraded (`EOPNOTSUPP`).
2. Routes vessel-agnostic families first (`debug.warp`, thug_life, FX, IVA, `audio.*`,
   `schedule.*`, `camera.*`, `paint.*`).
3. Resolves the target `Vehicle` by id from `Universe.CurrentSystem.All` (`ENOENT` if gone).
4. Applies the authority gate: with `control_all_vessels=false` only `Program.ControlledVehicle` is
   commandable, except `debug.*` and the explicit `AnyVesselActions` set (`EACCES`).
5. Dispatches to a static actuator method by action key ([gatOS.GameMod/Game/Ksa/KsaCatalog.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/KsaCatalog.cs#L153-L211)).
6. Catches any exception, latches the accessor degraded in `KsaHealth` and returns `EIO`.

**Errno mapping.** `CommandOutcome` maps once to Linux errno numbers for 9p writes and to names for
HTTP/MQTT/serial: `Invalid`=EINVAL, `NotFound`=ENOENT, `Denied`=EACCES, `Busy`=EBUSY, `Fault`=EIO,
`TimedOut`=ETIMEDOUT, `Unsupported`=EOPNOTSUPP ([gatOS.SimFs/Commands/CommandResult.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/CommandResult.cs#L58-L86)).

## Lifecycle and timing

| Phase | Drained in | Window |
|---|---|---|
| `Frame` (most actions) | main-viewport `GameViewport.OnFrame` prefix (`frame:viewports`, every frame, installed when `[camera] camera_enabled` is true, the default); without that hook `StarMapBeforeGui`, or `StarMapAfterOnFrame` on hidden-HUD frames | after `PrepareFrame`; the next vehicle step is already dispatched |
| `Solver` (flight computer setpoints, refills, teleport, impulse, `rcs_mode`) | `Priority.First` Harmony prefix on `Universe.ExecuteNextVehicleSolvers` | inside `PrepareFrame`, after `ApplyVehicleSolvers`, before `PrepareVehicleWorkers` |

What a Frame-phase write means for physics: `Vehicle.PrepareWorker` copies `_manualControlInputs`
(a struct) and snapshots the `FlightComputer` into the worker state at dispatch
([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423)). A Frame-phase write therefore reaches the
solver at the next dispatch (one frame later) if the field is not restored at apply; fields that
apply restores need the Solver phase (see
[Solver-phase drain for flight computer setpoints](solver-phase-command-drain.md)).
Transport threads only ever touch the queue (`thread:background`).

## Key code

| Path | Role |
|---|---|
| [gatOS.SimFs/Commands/SimCommand.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/SimCommand.cs) | immutable command, phase derived from action |
| [gatOS.SimFs/Commands/CommandQueue.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/CommandQueue.cs) | two-phase queue, submit/await, batch, post, drain |
| [gatOS.SimFs/Commands/CommandResult.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/CommandResult.cs) | outcomes and the errno mapping |
| [gatOS.SimFs/Commands/BatchFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/BatchFile.cs) | `/sim/ctl/batch` atomic group file |
| [gatOS.GameMod/Game/Ksa/KsaCatalog.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/KsaCatalog.cs) | game-thread router, authority gate, health latching |
| [gatOS.GameMod/Game/Ksa/KsaHealth.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/KsaHealth.cs) | degraded-accessor latches published to `/sim/status/accessors` |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L261-L277) | Frame-phase `DrainCommands` with `PerfStat` timing |

## Snippets

```csharp
// gatOS.SimFs/Commands/CommandQueue.cs L109-L129
    private async Task<CommandResult> EnqueueAsync(SimCommand[] group, CommandPhase phase, CancellationToken ct)
    {
        if (!ControlEnabled)
            return new CommandResult(CommandOutcome.Denied, "control is disabled in gatos.toml");

        var pending = new Pending(group);
        (phase == CommandPhase.Solver ? _solver : _frame).Enqueue(pending);

        try
        {
            return await pending.Completion!.Task.WaitAsync(_timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Abandon it so the game thread skips executing a command the writer already gave up on.
            pending.Abandoned = true;
            return new CommandResult(CommandOutcome.TimedOut,
                "the game thread did not execute the command in time (paused or loading?)");
        }
        // OperationCanceledException (the write was flushed/clunked) propagates to the caller.
    }
```

```csharp
// gatOS.GameMod/Game/Ksa/KsaCatalog.cs L113-L134
            // control_vessel targets the vehicle named by the token (the one to take control of),
            // not the (sender) VesselId.
            var targetId = command.Action == SimActions.DebugControlVessel ? command.Token ?? command.VesselId : command.VesselId;
            var vehicle = ResolveVehicle(targetId);
            if (vehicle is null)
                return new CommandResult(CommandOutcome.NotFound, $"vessel '{targetId}' is gone");

            // Authority gate (G-D1): with all_vessels=false only the controlled vehicle is commandable.
            // The cheat namespace is exempt — it is its own opt-in (G-D2) — and so is the
            // AnyVesselActions set: deliberate per-vessel operations that work on any addressed
            // vessel (the controls moved out of the /sim/debug namespace).
            var anyVessel = isDebug || AnyVesselActions.Contains(command.Action);
            if (!anyVessel && !allVessels && Program.ControlledVehicle?.Id != vehicle.Id)
                return new CommandResult(CommandOutcome.Denied, "control is restricted to the active vessel");

            return Finish(accessor, Dispatch(vehicle, command));
        }
        catch (Exception ex)
        {
            health.Fault(accessor, SafeUt(), ex.Message);
            return new CommandResult(CommandOutcome.Fault, ex.Message);
        }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Universe.CurrentSystem.All` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L66-L66) | resolve a vehicle by `Id` |
| `Program.ControlledVehicle` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs) | authority gate |
| `Vehicle.PrepareWorker` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423) | where Frame-phase writes are captured for the next step |

## Pitfalls and version notes

- Never execute game calls on the transport thread, even "read-only" ones; resolve everything in
  the executor.
- Validate twice: the 9p control files reject malformed input with `EINVAL` before enqueueing, but
  `POST /v1/command`, MQTT and MCP bypass the file parser, so actuators re-validate (repo AGENTS.md playbook).
- The per-frame cap means a runaway script cannot stall the frame; a batch can overshoot the cap by
  its own size because it is never split.
- A drain exception latches control off for the session (`_commandsDead`).

## See also

- [Solver-phase drain for flight computer setpoints](solver-phase-command-drain.md)
- [Ignition, staging and module actuators](ignition-staging-and-module-actuators.md)
- [Manual throttle and RCS inputs](manual-throttle-and-rcs-inputs.md)
- [The /sim file models](sim-filesystem-file-models.md)
- [Timed command scheduler](timed-command-scheduler.md)
- [KSA anchor churn tracking](ksa-anchor-churn-tracking.md)
- [gatOS overview](../overview.md)
- [Two ConcurrentQueues with TaskCompletionSource, timeout and abandon](../../../howto/external-io/control/marshal-commands-to-the-game-thread/marshal-commands-to-the-game-thread-1.md) - how-to article demonstrating this feature
- [Solver-window drain in a Priority.First prefix on ExecuteNextVehicleSolvers](../../../howto/external-io/control/marshal-commands-to-the-game-thread/marshal-commands-to-the-game-thread-2.md) - how-to article demonstrating this feature

