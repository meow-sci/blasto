---
type: How-To
title: Two ConcurrentQueues with TaskCompletionSource, timeout and abandon
description: Build a command queue where transport threads await a result and the game thread drains per phase at a fixed point in the frame
category: external-io
group: control
topic: marshal-commands-to-the-game-thread
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/gatos/features/game-thread-command-queue]
techniques: [command-queue, background-thread, starmap-lifecycle]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapBeforeGui, starmap StarMapAfterOnFrame]
lifecycle: [thread:background, frame:viewports, frame:after-on-frame, frame:draw-ui]
game_types: [KSA.Program, KSA.Universe, KSA.Vehicle, KSA.GameViewport, KSA.CelestialSystem]
tags: [command-queue, taskcompletionsource, errno, batch, authority-gate]
---

Outcome: any number of background threads can submit a command and `await` its result, while every call into KSA happens inside one executor invoked from the main thread. A rejected command comes back as a typed outcome that every transport maps to the same error name. Requires StarMap and Harmony. Demonstrated by [Game-thread command queue](../../../../mods/gatos/features/game-thread-command-queue.md) in gatOS.

The queue class and the command record are game-free (they live in a separate assembly with no KSA references), so they can be unit-tested without the game. Only the executor, `KsaCatalog`, knows about KSA.

## Background: how the game does it

KSA has no cross-thread invocation facility for mods. The main thread runs `Program.OnFrame` once per rendered frame; inside it `PrepareFrame` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2235) applies the last physics step and starts the next. Simulated state is only consistent in the window between those two actions, and ImGui and Vulkan are main-thread only. A mod therefore needs its own mailbox: producers on any thread, one consumer in a main-thread hook.

## Lifecycle and timing

| Piece | Runs | Notes |
|---|---|---|
| `SubmitAsync`, `SubmitBatchAsync`, `Post` | `thread:background` | Enqueue only; never touch KSA |
| `Drain(CommandPhase.Frame, ...)` | `frame:viewports`: a prefix on `GameViewport.OnFrame` for the main viewport, every frame | The default; falls back to `StarMapBeforeGui`, or to `StarMapAfterOnFrame` on hidden-HUD frames, when the viewport hook is not installed |
| `Drain(CommandPhase.Solver, ...)` | `frame:dispatch-solvers` | Approach 2 |

The Frame drain runs after `PrepareFrame` has already dispatched the next physics step, so a Frame-phase write to simulated state is captured by the worker only at the following dispatch (one frame later), and is erased if the field is one of those the apply restores. [approach 2](marshal-commands-to-the-game-thread-2.md) handles those fields.

Hidden-HUD behaviour: the GUI hooks do not fire when the HUD is hidden, so the stock fallback is `StarMapAfterOnFrame`, which fires on every frame; gatOS uses a latch to run the per-frame work from exactly one of the hooks. The main-viewport `GameViewport.OnFrame` prefix runs with the HUD hidden as well, which makes it the primary drain point. See [the HUD-hidden table in the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-imgui-window).

## Steps

1. **Define an immutable command.** The record names its target by string, its action by a stable key, and derives the game-thread phase from the action so a caller cannot choose it wrongly.

   ```csharp
   // gatOS.SimFs/Commands/SimCommand.cs L38-L46, L86-L96
   public sealed record SimCommand(
       string VesselId,
       string Action,
       int Ordinal,
       double Value)
   {
       /// <summary>Sentinel for <see cref="Ordinal"/> meaning "vessel-level, no module".</summary>
       public const int NoOrdinal = -1;

       // ...
       /// <summary>The game-thread phase an action must execute in (KSA_GAME_INTEGRATION_PLAN §3.1).</summary>
       public static CommandPhase PhaseFor(string action)
           => SolverActions.Contains(action) ? CommandPhase.Solver : CommandPhase.Frame;

       /// <summary>
       ///     Which game-thread phase must execute this command — derived purely from
       ///     <see cref="Action"/> (see <see cref="SolverActions"/>), so it cannot be set wrong at a
       ///     construction site. Excluded from value-equality (no backing field); <see cref="Action"/>
       ///     already participates, and phase is a function of it.
       /// </summary>
       public CommandPhase Phase => PhaseFor(Action);
   ```

2. **Define outcomes and one errno mapping.** Every transport routes failures through the same table: a Linux errno number for 9p writes, a name for HTTP, MQTT, MCP and serial.

   ```csharp
   // gatOS.SimFs/Commands/CommandResult.cs L58-L85
   public static class CommandOutcomes
   {
       /// <summary>The Linux errno number a failed outcome reports (<c>0</c> for success).</summary>
       public static uint ToErrno(this CommandOutcome outcome) => outcome switch
       {
           CommandOutcome.Ok => 0,
           CommandOutcome.Invalid => LinuxErrno.EINVAL,
           CommandOutcome.NotFound => LinuxErrno.ENOENT,
           CommandOutcome.Denied => LinuxErrno.EACCES,
           CommandOutcome.Busy => LinuxErrno.EBUSY,
           CommandOutcome.Fault => LinuxErrno.EIO,
           CommandOutcome.TimedOut => LinuxErrno.ETIMEDOUT,
           CommandOutcome.Unsupported => LinuxErrno.EOPNOTSUPP,
           _ => LinuxErrno.EIO,
       };

       /// <summary>The canonical errno name (<c>"EINVAL"</c>, …) for text/JSON transports.</summary>
       public static string ErrnoName(this CommandOutcome outcome) => outcome switch
       {
           CommandOutcome.Ok => "OK",
           CommandOutcome.Invalid => "EINVAL",
           CommandOutcome.NotFound => "ENOENT",
           CommandOutcome.Denied => "EACCES",
           CommandOutcome.Busy => "EBUSY",
           CommandOutcome.TimedOut => "ETIMEDOUT",
           CommandOutcome.Unsupported => "EOPNOTSUPP",
           _ => "EIO",
       };
   ```

3. **Write the queue with two `ConcurrentQueue<Pending>` instances.** `SubmitAsync` enqueues and awaits with a timeout. On timeout the pending entry is marked `Abandoned` so the game thread skips a command the caller already gave up on, and the caller gets `ETIMEDOUT` (typical while the game is loading or a frame stalls; a simulation pause does not stop the drain, because `Program.OnFrame` runs every frame).

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

   The `TaskCompletionSource` is created with `RunContinuationsAsynchronously`, so the awaiting transport code never resumes inline on the game thread inside `Drain`:

   ```csharp
   // gatOS.SimFs/Commands/CommandQueue.cs L183-L192
       private sealed class Pending
       {
           /// <summary>The awaiting path (<see cref="SubmitAsync"/>/<see cref="SubmitBatchAsync"/>).</summary>
           internal Pending(SimCommand[] commands)
           {
               Commands = commands;
               // RunContinuationsAsynchronously: the awaiting transport thread must never resume inline
               // on the game thread inside Drain (threading rule 5 — nothing blocks the game thread).
               Completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
           }
   ```

4. **Add the atomic batch.** `SubmitBatchAsync` puts a whole group in one `Pending`; all commands must share a phase because "same tick" has no meaning across two drain points. The 9p batch file caps a group at 64 commands ([gatOS.SimFs/Commands/BatchFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/BatchFile.cs#L36-L38)).

   ```csharp
   // gatOS.SimFs/Commands/CommandQueue.cs L61-L76
       public Task<CommandResult> SubmitBatchAsync(IReadOnlyList<SimCommand> commands, CancellationToken ct)
       {
           if (commands.Count == 0)
               return Task.FromResult(new CommandResult(CommandOutcome.Invalid, "empty batch"));

           var group = commands.ToArray();
           var phase = group[0].Phase;
           foreach (var command in group)
           {
               if (command.Phase != phase)
                   return Task.FromResult(new CommandResult(CommandOutcome.Invalid,
                       "batch commands must all share one phase (Frame or Solver)"));
           }

           return EnqueueAsync(group, phase, ct);
       }
   ```

5. **Add a fire-and-forget `Post`** for producers that live on the game thread or span many ticks (the [timed-command scheduler](../schedule-timed-commands/overview.md)). It routes by phase, so mixing phases across posts is allowed, and reports through an `IPostObserver` invoked inline inside `Drain`, so the observer must be trivial.

6. **Write the drain.** It executes at most `maxCommands` per call, skips abandoned entries, runs a batch in full once dequeued, and completes the waiter with the first failure annotated with its position. A batch can therefore overshoot the cap by its own size.

   ```csharp
   // gatOS.SimFs/Commands/CommandQueue.cs L138-L181
       public int Drain(CommandPhase phase, ICommandExecutor executor, int maxCommands)
       {
           var queue = phase == CommandPhase.Solver ? _solver : _frame;
           var executed = 0;
           while (executed < maxCommands && queue.TryDequeue(out var pending))
           {
               if (pending.Abandoned)
                   continue;

               var commands = pending.Commands;
               CommandResult? failure = null;
               var result = CommandResult.Ok;
               for (var i = 0; i < commands.Length; i++)
               {
                   try
                   {
                       result = executor.Execute(commands[i]);
                   }
                   catch (Exception ex)
                   {
                       // The executor contracts not to throw; treat a leak as an integration fault.
                       ModLog.Log.Debug($"command executor threw for '{commands[i].Action}': {ex.Message}");
                       result = new CommandResult(CommandOutcome.Fault, ex.Message);
                   }

                   executed++;
                   // Batch commands are independent: a failure does not stop the rest, and the
                   // submitter gets the FIRST failure, annotated with which command it was.
                   if (!result.IsSuccess && failure is null)
                       failure = commands.Length == 1
                           ? result
                           : new CommandResult(result.Outcome,
                               $"command {i + 1}/{commands.Length} ({commands[i].Action}): {result.Message ?? "no detail"}");
               }

               var final = failure ?? (commands.Length == 1 ? result : CommandResult.Ok);
               pending.Completion?.TrySetResult(final);
               // Fire-and-forget posts have no waiter; their observer is called INLINE here, on the
               // game thread, so it must stay trivial (the IPostObserver contract).
               pending.Observer?.OnCommandResult(pending.Token, final);
           }

           return executed;
       }
   ```

7. **Write the executor, the only KSA-aware code.** gatOS's `KsaCatalog.Execute` applies, in order: a latched-degraded check (`EOPNOTSUPP`), routing of vessel-agnostic families, vehicle resolution by id (`ENOENT`), an authority gate (`EACCES` unless the vessel is the controlled one, or the action is exempt), dispatch, and a catch-all that latches the accessor degraded and returns `EIO`.

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

8. **Call the drain from the game thread, with a kill switch.** A drain exception disables control for the session instead of repeating every frame.

   ```csharp
   // gatOS.GameMod/Game/Mod.Game.cs L261-L277
       partial void DrainCommands()
       {
           if (_commandsDead || _commandQueue is not { } queue)
               return;

           try
           {
               EnsureControlObjects();
               using (_drainStats.Measure()) // alloc-free; usually an empty-queue no-op
                   queue.Drain(CommandPhase.Frame, _catalog!, Config.MaxCommandsPerFrame);
           }
           catch (Exception ex)
           {
               _commandsDead = true;
               ModLog.Log.Error($"gatOS control disabled after a drain error: {ex.Message}");
           }
       }
   ```

9. **Pick the hook.** gatOS runs the sampler, the schedule tick and the drain, in that order, from a prefix on the main viewport's `GameViewport.OnFrame` ([gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L574-L583)), installed with the camera feature. Without it the same work runs from `StarMapBeforeGui` and, on frames where the HUD is hidden, from `StarMapAfterOnFrame` ([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L450-L467)).

## Complete example

| File | Role |
|---|---|
| [gatOS.SimFs/Commands/SimCommand.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/SimCommand.cs) | Immutable command, phase derived from the action key |
| [gatOS.SimFs/Commands/CommandQueue.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/CommandQueue.cs) | Two-phase queue, submit, batch, post, drain |
| [gatOS.SimFs/Commands/CommandResult.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/CommandResult.cs) | Outcomes and the errno mapping |
| [gatOS.GameMod/Game/Ksa/KsaCatalog.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/KsaCatalog.cs) | Game-thread router, authority gate, health latching |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs) | `DrainCommands` and the main-viewport entry point |

## Variations and gotchas

- **Never execute game calls on the transport thread**, even read-only ones; resolve everything inside the executor.
- **Validate twice.** The 9p control files reject malformed input before enqueueing, but HTTP, MQTT and MCP bypass that parser, so actuators re-validate.
- **A runaway producer cannot stall the frame**: the per-frame cap (`MaxCommandsPerFrame`, default 64, clamped to 1-4096 in [gatOS.GameMod/Configuration/GatOsConfig.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Configuration/GatOsConfig.cs#L280-L282)) bounds drain time.
- **Timeouts are per caller, not per command.** The default is 2000 ms (clamped to 100-30000 ms); if the game thread does not reach the drain (loading, a stalled frame) writes time out rather than queue forever. A simulation pause is not such a case: the drain is tied to `Program.OnFrame`, which runs while the sim is paused.
- **Camera hook ordering.** If you also drive a camera, drain before it so commands issued this frame affect this frame's pose.

## See also

- [Overview](overview.md)
- [Approach 2: solver-window drain](marshal-commands-to-the-game-thread-2.md)
- [Game-thread command queue](../../../../mods/gatos/features/game-thread-command-queue.md), [Timed command scheduler](../../../../mods/gatos/features/timed-command-scheduler.md)
- [Host HTTP and MQTT servers in the game process](../../network/host-http-and-mqtt-servers-in-the-game-process/overview.md), [Let AI agents drive the game through an MCP server](../let-ai-agents-control-the-game-over-mcp/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
