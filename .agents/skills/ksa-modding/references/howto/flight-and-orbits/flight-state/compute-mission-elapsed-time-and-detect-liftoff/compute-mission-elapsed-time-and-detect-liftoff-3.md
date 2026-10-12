---
type: How-To
title: Choose render, wall or sim time for timed command playback
description: Drive a schedule of timed commands from the clamped player delta, a wall stopwatch or the sim-time delta, with a catch-up policy that never drops triggers
category: flight-and-orbits
group: flight-state
topic: compute-mission-elapsed-time-and-detect-liftoff
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/gatos/features/timed-command-scheduler]
techniques: [command-queue]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapBeforeGui]
lifecycle: [frame:viewports, frame:draw-ui]
game_types: [KSA.Universe, KSA.UniverseTime, KSA.Program]
tags: [scheduler, clock, time-warp, wall-clock, dt-player, catch-up]
---

Outcome: a scheduler that plays a timeline ("stage at T+0, lights at T+250 ms") against a clock chosen per schedule. gatOS lets clients
write a timed batch and picks `render`, `wall` or `ut` as the base.[Timed command scheduler](../../../../mods/gatos/features/timed-command-scheduler.md) Prerequisites: StarMap and
Harmony only if you hook `GameViewport.OnFrame`.

## Background: how the game does it

Three clocks are available to a main-thread hook: the frame's `dtPlayer` (wall delta, clamped by the game to
`1 / MinTargetFrameRate`, so a hitch is not caught up), a host `Stopwatch` (unclamped wall time) and simulation time
`Universe.GetElapsedSeconds()` (follows warp, stops on pause). Which is right depends on what the command does: a camera cue
wants render time; an orbital event wants sim time. A rewind (quickload) can make the sim delta negative, so clamp it at 0.[^ksa-app]
[current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L49-L53) [^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2393-L2402)

## Lifecycle and timing

The tick runs immediately before the frame-phase command drain, in the main-viewport `GameViewport.OnFrame` prefix (or
`StarMapBeforeGui`, or a fallback), so a command due this frame is in the queue that drains this frame, and camera cues land before
the frame's matrices are built. Solver-phase entries drain at the next `Universe.ExecuteNextVehicleSolvers` prefix. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define the grammar**: lines of `<offsetMs> <path> <value...>` with absolute offsets from the schedule start (never deltas, so rounding
   cannot accumulate), validated all-or-nothing before registration (see [gatOS.SimFs/Commands/TimedBatchFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/TimedBatchFile.cs#L1-L40)).
2. **Compute one delta per clock per tick.** Park the wall stopwatch while no schedule is live so idle time is never banked; seed the sim
   delta on the first tick:

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

3. **Advance every schedule** with the three deltas (`AdvanceAll(dt*1000, wallMs, utMs)`) and collect due entries into a reused list.
4. **Post due commands fire-and-forget** through the command queue, with an observer that records failures per schedule.
5. **Catch up without losing triggers.** When a hitch makes many entries due at once, fire every trigger in order but post only the last
   non-trigger value per path; count the rest as dropped ([gatOS.SimFs/Commands/Scheduler.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/Scheduler.cs)).
6. **Choose the base per schedule**: `render` (default) for cinematics, `wall` for real-time pacing, `ut` for orbital events.

## Complete example

| File | Role |
|---|---|
| [gatOS.SimFs/Commands/TimedBatchFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/TimedBatchFile.cs) | the `timed_batch` grammar and commit |
| [gatOS.SimFs/Commands/PlaybackClock.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/PlaybackClock.cs) | the one timeline primitive (render, wall, ut) |
| [gatOS.SimFs/Commands/Scheduler.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/Scheduler.cs) | cursor and coalescing catch-up |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L381-L437) | the game-thread tick |

## Variations and gotchas

- `dtPlayer` is clamped, so a long hitch is not caught up on the render clock; that is right for cinematics and wrong for
  orbital timing.
- The sim clock runs backwards after a quickload; clamp at zero.
- Idle cost should be two integer compares; a tick fault disables scheduling for the session rather than throwing every frame.
- Schedule commit runs on a transport thread; only the tick is game-thread.

## See also

- [Overview](overview.md); [liftoff clock](compute-mission-elapsed-time-and-detect-liftoff-1.md); [applied sim step](compute-mission-elapsed-time-and-detect-liftoff-2.md)
- [Timed command scheduler](../../../../mods/gatos/features/timed-command-scheduler.md)
- [Set, limit and request simulation speed from a mod](../set-simulation-speed-from-a-mod/overview.md)

[^ksa-app]: KSA.App (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
