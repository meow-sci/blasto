---
type: Mod Feature
title: Timed command scheduler
description: A game-free scheduler that plays back scripts of millisecond-offset commands on render, wall or sim-time clocks, ticked on the game thread immediately before the command drain so a command due this frame executes this frame
mod: gatos
category: external-io
techniques: [command-queue, starmap-lifecycle, harmony-prefix]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapBeforeGui]
lifecycle: [frame:viewports, frame:draw-ui, thread:background]
game_types: [KSA.Universe, KSA.App]
tags: [scheduler, timeline, cinematics, commands, clocks, warp]
---

Scripts and cinematic tools need frame-accurate sequences (stage at T+0, lights at T+250 ms,
camera cue at T+1.2 s) without a guest process sleeping on its own unreliable clock. gatOS lets a
client write a whole timeline to `/sim/ctl/timed_batch` and have the host play it back against a
chosen clock. The scheduler never learns what a command does; it only posts gatOS's own
`SimCommand`s, so it adds no KSA coupling at all.

## How it works

**Grammar** ([gatOS.SimFs/Commands/TimedBatchFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/TimedBatchFile.cs#L1-L40)): lines of
`<offsetMs> <path> <value...>`, where the path is any `/sim` control file, plus `@id`, `@clock`,
`@rate`, `@loop`, `@group` directives, terminated by `commit`. Offsets are absolute from the
schedule's start (never deltas), so rounding cannot accumulate. The whole script is validated before
anything is registered (all-or-nothing); mixing Frame and Solver phase commands is allowed because
each entry routes itself.

**Clocks** (sourced once per tick in `Mod.TickSchedules`,
[gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L381-L437)):

| Base | Source | Behaviour |
|---|---|---|
| `render` (default) | the frame's `dtPlayer` | KSA clamps it to `1 / MinTargetFrameRate` ([current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L49-L53)), so a hitch is not caught up; right for cinematics |
| `wall` | a host `Stopwatch` | parked and reset while no schedule is live, so idle time is never banked |
| `ut` | the sim-time delta from `Universe.GetElapsedSeconds()` | follows warp; clamped at 0 on a rewind |

**Catch-up policy** ([gatOS.SimFs/Commands/Scheduler.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/Scheduler.cs)): when a hitch makes many entries due
at once, every trigger fires in order (a skipped stage separation is a bug), but only the last
non-trigger value per path is posted; the remainder is counted in `<id>/dropped` and reported as a
throttled `schedule.dropped` event.

**Players and control.** Committed schedules are live players under `/sim/ctl/schedules/<id>/`
(pause, scrub, rate, loop, cancel through `schedule.*` actions that route straight to the game-free
`ScheduleStore`). Completed players stay readable and are evicted oldest-finished-first only under
cap pressure; a running camera track is never evicted. Due commands go through
`CommandQueue.Post`, the fire-and-forget path, with an observer that records failures into
`<id>/last_error`.

## Lifecycle and timing

- Commit runs on a transport thread (reserve id, validate, queue); everything else is game-thread.
- The tick runs immediately before the Frame-phase drain, in the main-viewport `GameViewport.OnFrame`
  prefix (or `StarMapBeforeGui`, or the F2 fallback), so a command due this frame is in the queue
  that drains this frame, and camera cues land before this frame's matrices are built.
- Solver-phase entries drain at the next `Universe.ExecuteNextVehicleSolvers` prefix.
- Scheduled commands count against `max_commands_per_frame` like any other.
- Idle cost: two integer compares; a tick fault disables scheduling for the session.

## Key code

| Path | Role |
|---|---|
| [gatOS.SimFs/Commands/TimedBatchFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/TimedBatchFile.cs) | the `timed_batch` grammar and commit |
| [gatOS.SimFs/Commands/PlaybackClock.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/PlaybackClock.cs) | the one timeline primitive (render, wall, ut) |
| [gatOS.SimFs/Commands/Scheduler.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/Scheduler.cs) | cursor and coalescing catch-up |
| [gatOS.SimFs/Commands/ScheduleStore.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/ScheduleStore.cs) | live-player registry and `schedule.*` executor |
| [gatOS.SimFs/Commands/ScheduleTree.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/ScheduleTree.cs) | `/sim/ctl/schedules/` view |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L381-L437) | the game-thread tick |
| [plans/SCHEDULER_ASBUILT.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/plans/SCHEDULER_ASBUILT.md) | as-built notes |

## Snippets

```csharp
// gatOS.GameMod/Game/Mod.Game.cs L386-L429
        try
        {
            // Free (a static field read behind AggressiveInlining), and needed before Activate so a
            // schedule's `started` event carries the sim time it actually started at.
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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Universe.GetElapsedSeconds()` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2393-L2396) | the `ut` clock |
| `App` frame loop `dtPlayer` clamp | [current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L44-L69) | why the `render` clock never catches up |

## Pitfalls and version notes

- Use the `ut` clock for anything tied to orbital events under warp; guest wall clocks are unrelated
  to sim time.
- A schedule's `started` event carries the sim time read before `Activate`, so the time is the
  frame it actually started.
- Reuse the due-list buffer each tick; the scheduler is allocation-free on the game thread.

## See also

- [Game-thread command queue](game-thread-command-queue.md)
- [Programmable camera director](programmable-camera-director.md)
- [Frame hooks with an F2-proof fallback](frame-hooks-and-f2-fallback.md)
- [External API clients and SDK](external-api-clients.md)
- [gatOS overview](../overview.md)
- [Scheduler posting SimCommands before the drain, with render, wall and ut clocks](../../../howto/external-io/control/schedule-timed-commands/schedule-timed-commands.md) - how-to article demonstrating this feature
- [Choose render, wall or sim time for timed command playback](../../../howto/flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/compute-mission-elapsed-time-and-detect-liftoff-3.md) - how-to article demonstrating this feature

