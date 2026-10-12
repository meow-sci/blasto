---
type: How-To
title: Scheduler posting SimCommands before the drain, with render, wall and ut clocks
description: Parse a timed script once, advance one playback clock per tick on the game thread and post due commands into the game-thread queue with catch-up that never skips a trigger
category: external-io
group: control
topic: schedule-timed-commands
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/timed-command-scheduler]
techniques: [command-queue, background-thread]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapBeforeGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:viewports, frame:draw-ui, thread:background]
game_types: [KSA.Universe, KSA.App, KSA.Program]
tags: [scheduler, clocks, catch-up, coalescing, playback]
---

Outcome: a client writes a whole timeline once, and the mod plays it back on the game thread against a render, wall or sim-time clock, with pause, scrub, rate and loop. The scheduler never learns what a command does; it only posts the same immutable commands as any other producer, so it adds no coupling to KSA. Requires the command queue from [Marshal commands from background threads to the game thread](../marshal-commands-to-the-game-thread/overview.md). Demonstrated by [Timed command scheduler](../../../../mods/gatos/features/timed-command-scheduler.md) in gatOS.

## Background: how the game does it

The game offers no timer service. What it exposes are the three clocks in the [overview](./overview.md): `dtPlayer` from `App.Run`, which is clamped by `MinTargetFrameRate`:

```csharp
// current/decomp/KSA/App.cs L49-L53
			double totalSeconds = _watch.Elapsed.TotalSeconds;
			double num5 = totalSeconds - num;
			num = totalSeconds;
			float num6 = 1f / GameSettings.Current.Simulation.MinTargetFrameRate;
			double dtPlayer = double.Min(num5, num6);
```

and `Universe.GetElapsedSeconds()` for simulation time. A mod picks per script which one it rides; the choice is the script author's, because the three behave differently after a stall or under time warp.

## Lifecycle and timing

| Step | Where |
|---|---|
| Commit: reserve id, validate, queue the schedule | `thread:background` (a transport thread) |
| Activate, advance clocks, collect due commands, `Post` | `frame:viewports`: the main-viewport `GameViewport.OnFrame` prefix, every frame, HUD visible or hidden |
| Fallback when that hook is absent | `StarMapBeforeGui` (`frame:draw-ui`, HUD visible), `StarMapAfterOnFrame` (`frame:after-on-frame`, covers hidden-HUD frames) |
| Frame-phase commands execute | the drain that runs immediately after the tick, same frame |
| Solver-phase commands execute | at the next `Universe.ExecuteNextVehicleSolvers` prefix ([see the solver-window drain](../marshal-commands-to-the-game-thread/overview.md)) |

The tick runs immediately before the Frame-phase drain, so a command due this frame is in the queue that drains this frame, and camera cues land before this frame's matrices are built. See [the per-frame sequence](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence) and [the hidden-HUD table](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-imgui-window) for why the hook choice matters. Scheduled commands count against the per-frame drain cap like any other.

## Steps

1. **Define the script grammar.** gatOS accepts `<offsetMs> <path> <value...>` lines plus `@id`, `@clock`, `@rate`, `@loop` and `@group` directives that must precede the first entry, terminated by `commit`. Offsets are absolute from the schedule start, never deltas, so rounding cannot accumulate and re-timing one line does not shift the rest. Everything is validated before anything is registered (all-or-nothing). The grammar is documented in [gatOS.SimFs/Commands/TimedBatchFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/TimedBatchFile.cs#L10-L40); an example script from the repository specification:

   ```sh
   # SPEC_9P_FILESYSTEM.md L1069-L1080
   cat > /sim/ctl/timed_batch <<'EOF'
   @id      launch-seq     # optional; auto "#N" otherwise. [A-Za-z0-9_.-]{1,64}
   @clock   render         # render | wall | ut   (case-insensitive; default schedule_default_clock)
   @rate    1.0            # 0..100; 0 freezes the timeline
   @loop    0              # 0 | 1
   @group   take-3         # optional; joins a shared-clock group, same charset as @id

   0        vessels/by-id/Hunter/ctl/throttle  1
   1200     vessels/by-id/Hunter/ctl/ignite    1
   3400     debug/time/warp                    1
   commit
   EOF
   ```

2. **Parse once, at commit.** Each entry stores its deadline, a normalized path, the pre-parsed `SimCommand` and whether the target is a trigger (an impulse such as stage, ignite or fire) or a state control (a setpoint). Parsing never happens on the tick path.

   ```csharp
   // gatOS.SimFs/Commands/Schedule.cs L23-L23
   public readonly record struct ScheduleEntry(double DeadlineMs, string Path, SimCommand Command, bool IsTrigger);
   ```

3. **Write one timeline primitive.** `PlaybackClock` owns only the timeline: its base, rate (0 to 100, 0 freezes), loop, pause and scrub cursor. Every property is published through `Volatile` so transport threads can read status without a lock. It must be advanced exactly once per clock per tick, which matters when several schedules share a clock (`@group`).

   ```csharp
   // gatOS.SimFs/Commands/PlaybackClock.cs L168-L206
       public void Advance(double renderDeltaMs, double wallDeltaMs, double utDeltaMs)
       {
           if (!_started || _paused)
               return;

           var delta = Base switch
           {
               ClockBase.Wall => wallDeltaMs,
               ClockBase.Ut => utDeltaMs,
               _ => renderDeltaMs,
           };
           if (!double.IsFinite(delta) || delta <= 0)
               return;

           var rate = Rate;
           if (rate <= 0)
               return;

           var duration = DurationMs;
           var position = Read(ref _position) + (delta * rate);
           if (_loop && duration > 0)
           {
               var wraps = 0;
               while (position >= duration)
               {
                   position -= duration;
                   wraps++;
               }

               if (wraps > 0)
                   Interlocked.Add(ref _loopCount, wraps);
           }
           else if (duration > 0 && position > duration)
           {
               position = duration;
           }

           Write(ref _position, position);
       }
   ```

4. **Tick on the game thread, just before the drain.** The wall clock is parked (stopped and reset) while no schedule is live so idle time is never banked into the first tick of the next one; the ut delta is clamped at zero so a scene load that rewinds sim time does not rewind a timeline; the due list is a reused buffer.

   ```csharp
   // gatOS.GameMod/Game/Mod.Game.cs L390-L400, L402-L429
               var ut = SafeUt();
               schedules.Activate(ut);
               if (schedules.Count == 0)
               {
                   // Nothing live. Park the wall clock (stopped, at zero) so the next schedule's first
                   // tick advances by one frame rather than by however long the registry sat empty, and
                   // forget the UT reading for the same reason.
                   _scheduleWallClock.Reset();
                   _scheduleUtSeeded = false;
                   return;
               }
               // ...
               using (_scheduleStats.Measure()) // alloc-free; the status window's schedule readout
               {
                   double wallMs;
                   if (_scheduleWallClock.IsRunning)
                   {
                       wallMs = _scheduleWallClock.Elapsed.TotalMilliseconds;
                       _scheduleWallClock.Restart();
                   }
                   else
                   {
                       wallMs = 0; // first tick of a run: there is no previous instant to measure from
                       _scheduleWallClock.Start();
                   }

                   var utMs = _scheduleUtSeeded ? Math.Max(0, (ut - _scheduleLastUt) * 1000.0) : 0;
                   _scheduleLastUt = ut;
                   _scheduleUtSeeded = true;

                   schedules.AdvanceAll(dt * 1000.0, wallMs, utMs);

                   _scheduleDue.Clear(); // reused every tick — never reallocated (GP3 discipline)
                   schedules.Tick(_scheduleDue, ut);
                   for (var i = 0; i < _scheduleDue.Count; i++)
                   {
                       var due = _scheduleDue[i];
                       // Fire-and-forget: the schedule outlives any write that could have waited on it,
                       // so outcomes come back through the observer into schedules/<id>/last_error.
                       queue.Post(due.Command, due.Observer, due.Token);
   ```

   `SafeUt` wraps `Universe.GetElapsedSeconds()` in a try/catch returning 0; its source comment says the getter throws before the first sim step, which the decomp of this build does not show (it reads a static `SimStep` struct and yields 0), so treat the guard as defensive ([gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L441-L455)).

5. **Choose the catch-up policy.** When a hitch, a scrub or a high rate makes many entries due at once, every trigger fires in order (a skipped stage separation is a bug), while state controls coalesce to the last write per path. Cross-path order is preserved and what is dropped is counted, never silent (`<id>/dropped` and a throttled `schedule.dropped` event). The steady state, nothing due, is one comparison with no allocation.

   ```csharp
   // gatOS.SimFs/Commands/Scheduler.cs L96-L120
           var loops = _clock.LoopCount;
           var wrapped = loops != _lastLoopCount;
           _lastLoopCount = loops;

           var position = _clock.PositionMs;
           var tailStart = _cursor;

           if (!wrapped)
           {
               if (tailStart >= entries.Length || entries[tailStart].DeadlineMs > position)
                   return 0; // the steady state: one comparison, zero allocations

               var end = tailStart + 1;
               while (end < entries.Length && entries[end].DeadlineMs <= position)
                   end++;
               _cursor = end;

               if (end - tailStart == 1)
               {
                   due.Add(new DueCommand(entries[tailStart].Command, observer, tailStart));
                   return 0; // the common non-idle tick: still zero allocations
               }

               return Emit(due, observer, tailStart, end, 0, 0);
           }
   ```

   A scrub re-seats the cursor by binary search and fires nothing, because seeking is navigation, not playback; on a loop wrap the tail of the finished cycle fires before the new cycle's already-due head.

6. **Post fire-and-forget with an observer.** Due commands go through `CommandQueue.Post`; the observer, invoked inline on the game thread, records only the first failure of a player into its `last_error`, and the player keeps running.

7. **Expose players for control.** Committed schedules become live players with pause, scrub, rate, loop and cancel through `schedule.*` actions routed to a game-free `ScheduleStore`. Finished players stay readable and are evicted oldest-finished-first only under cap pressure; a running player is never evicted.

## Complete example

| File | Role |
|---|---|
| [gatOS.SimFs/Commands/TimedBatchFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/TimedBatchFile.cs) | Grammar and commit |
| [gatOS.SimFs/Commands/PlaybackClock.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/PlaybackClock.cs) | Clock bases, rate, loop, scrub |
| [gatOS.SimFs/Commands/Scheduler.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/Scheduler.cs) | Cursor and coalescing catch-up |
| [gatOS.SimFs/Commands/ScheduleStore.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/ScheduleStore.cs) | Live-player registry and `schedule.*` executor |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L378-L438) | The game-thread tick |

## Variations and gotchas

- **Pick the clock for the job.** `render` keeps a camera move in step with rendered motion but lags a host recorder; `wall` can demand a catch-up burst after a stall; `ut` is right for mission events, which diverge from wall time under warp.
- **A schedule committed on a transport thread appears at the next game tick**; the id is reserved immediately, so a duplicate-id commit in the gap is still rejected.
- **Groups share one clock instance**, so pausing or scrubbing any member moves all. The group clock's base, rate and loop come from the first member to create it.
- **Allocation discipline.** Reuse the due list; only the two-or-more-due case touches scratch collections, allocated once per player.
- **A tick fault disables scheduling for the session** instead of repeating every frame.

## See also

- [Overview](overview.md)
- [Timed command scheduler](../../../../mods/gatos/features/timed-command-scheduler.md), [Programmable camera director](../../../../mods/gatos/features/programmable-camera-director.md)
- [Marshal commands from background threads to the game thread](../marshal-commands-to-the-game-thread/overview.md)
- [Compute mission elapsed time and detect liftoff](../../../flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/overview.md)
- [Play cinematic camera tracks](../../../camera/special-views/play-cinematic-camera-tracks/overview.md)
- [Export live game state to background threads](../../telemetry/export-game-state-to-other-threads/overview.md)
