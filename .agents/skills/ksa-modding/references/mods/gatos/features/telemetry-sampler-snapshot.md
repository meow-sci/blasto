---
type: Mod Feature
title: Telemetry sampler and snapshot store
description: A rate-limited game-thread sampler reads every vehicle and body into one immutable snapshot and publishes it with a single volatile swap that any number of transport threads read without locks
mod: gatos
category: external-io
techniques: [starmap-lifecycle, harmony-prefix, immutable-snapshot, background-thread]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapBeforeGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:viewports, frame:draw-ui, frame:after-on-frame, thread:background]
game_types: [KSA.Universe, KSA.Program, KSA.Vehicle, KSA.CelestialSystem, KSA.VersionInfo]
tags: [telemetry, snapshot, threading, volatile, performance, zero-alloc]
---

Every gatOS transport (the `/sim` 9p filesystem, HTTP, MQTT, MCP, serial) serves the same data,
and none of them ever touches a KSA object. They all read one immutable `SimSnapshot` that a
sampler builds on the game thread a few times per second and publishes by swapping one volatile
reference. This is the core pattern for exporting live game state from a KSA mod to other threads
or processes without locks and without stalling the frame.

## How it works

1. **Rate limit.** `TelemetrySampler.Tick(dt, active)` feeds the frame's `dt` into a
   `SampleClock` (default 10 Hz, `sample_rate_hz` 1-120). The clock keeps sub-interval phase for
   drift-free cadence but drops any backlog after a long frame instead of burst-replaying it
   ([gatOS.SimFs/Telemetry/SampleClock.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Telemetry/SampleClock.cs#L33-L47)).
2. **Idle when nobody listens.** The caller passes `active = VM starting/running OR any 9p/HTTP/MQTT/MCP
   client connected`; otherwise the sampler resets its clock and does nothing
   ([gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L227-L258)).
3. **Read.** `Sample()` reads `Universe.GetElapsedSeconds()`, `Universe.SimulationSpeed`,
   `Program.ControlledVehicle`, then walks `Universe.CurrentSystem.All.UnsafeAsList()` and hands each
   `Vehicle` to `VesselReader.Sample` inside its own try/catch, so one vehicle mid-teardown cannot
   kill the snapshot ([gatOS.GameMod/Game/TelemetrySampler.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/TelemetrySampler.cs#L179-L229)). Bodies are sampled at an
   optional slower sub-cadence and re-published by reference in between.
4. **Gate at the source.** `TelemetrySettings` holds volatile per-stream gates (`vessel_detail`,
   `vessel_parts`, `bodies`, `events`) written by the ImGui menu and read every tick. A disabled
   stream skips its KSA reads and shrinks the snapshot, so every transport serves less by
   construction.
5. **Fold side channels.** Events from the snapshot differ plus queued audio, IVA, scheduler,
   camera and sticker events are concatenated into `NewEvents`.
6. **Publish.** `SnapshotStore.Publish` swaps the volatile `_current` reference, then completes a
   `TaskCompletionSource` so async waiters (SSE, `tail -f` on `stream`, MQTT push) wake
   ([gatOS.SimFs/Snapshots/SnapshotStore.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Snapshots/SnapshotStore.cs#L20-L52)).

Consumers never lock: they read `store.Current` and format on demand. `SnapshotIndex` memoizes
pure projections (vessel-by-id map, NDJSON stream lines) per snapshot instance in a
`ConditionalWeakTable`, so N readers of one value cost one format and the cache dies with the
snapshot ([gatOS.SimFs/Snapshots/SnapshotIndex.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Snapshots/SnapshotIndex.cs#L6-L27)).

Performance discipline: the sampler measures itself with `PerfStat` and records
`GC.GetAllocatedBytesForCurrentThread()` deltas per sample as an allocation tripwire shown in the
status window ([gatOS.GameMod/Game/TelemetrySampler.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/TelemetrySampler.cs#L157-L165)). Warp ladder and game version
strings are cached after the first read.

## Lifecycle and timing

- **Where it runs.** With the camera feature enabled (default) the sampler runs from the Harmony
  prefix on the main `GameViewport.OnFrame` (`frame:viewports`); otherwise from `StarMapBeforeGui`
  (`frame:draw-ui`), or from `StarMapAfterOnFrame` when the HUD is hidden. See
  [Frame hooks with an F2-proof fallback](frame-hooks-and-f2-fallback.md).
- **What it sees.** All three slots follow `Program.PrepareFrame`, where `Universe.ApplyVehicleSolvers`
  has just copied the finished vehicle step into the main-thread `Vehicle` objects
  ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952)). The next step is already running, but on
  worker copies prepared by `Vehicle.PrepareWorker`, and its results are only applied in the next
  `PrepareFrame`.[^ksa-vehicle] So main-thread reads in these slots see the state of the step
  applied at the start of this frame, one step behind the step that is computing. Two exceptions
  from the decomp: `Parachute.UpdateModules` mutates live `Parachute` objects from the worker, and
  part-tree derived data can be rebuilt on the worker if a tree is dirty (gatOS does not report
  parachutes, and `PrepareFrame` flushes dirty trees before dispatch).
- **Publishing.** The swap is a single reference write; transport threads (`thread:background`)
  see either the old or the new snapshot, never a torn one.

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/TelemetrySampler.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/TelemetrySampler.cs#L141-L166) | `Tick`: gate, rate change, clock, measured `Sample()` |
| [gatOS.GameMod/Game/TelemetrySampler.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/TelemetrySampler.cs#L179-L273) | `Sample`: enumerate vehicles and bodies, fold events, publish |
| [gatOS.SimFs/Telemetry/SampleClock.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Telemetry/SampleClock.cs) | drift-free rate limiter that drops backlog |
| [gatOS.SimFs/TelemetrySettings.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/TelemetrySettings.cs) | volatile cadence and per-stream gates (menu writes, sampler reads) |
| [gatOS.SimFs/Snapshots/SnapshotStore.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Snapshots/SnapshotStore.cs) | single-writer volatile exchange with async wait |
| [gatOS.SimFs/Snapshots/SimSnapshot.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Snapshots/SimSnapshot.cs#L27-L39) | immutable `record` snapshot and `Empty` sentinel |
| [gatOS.SimFs/Snapshots/SnapshotIndex.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Snapshots/SnapshotIndex.cs) | per-snapshot memoized projections |

## Snippets

```csharp
// gatOS.SimFs/Snapshots/SnapshotStore.cs L20-L52
    public void Publish(SimSnapshot snapshot)
    {
        _current = snapshot;
        // Swap first, then complete: a waiter that captured the old signal wakes and re-reads
        // _current; one that already captured the new signal waits for the next publish.
        var completed = Interlocked.Exchange(ref _signal, NewSignal());
        completed.SetResult();
    }

    /// <summary>
    ///     Completes with the first snapshot whose sequence exceeds
    ///     <paramref name="afterSequence"/> (immediately when one is already current). Used by
    ///     the stream/events files; cancellation propagates from Tflush/clunk.
    /// </summary>
    public async ValueTask<SimSnapshot> WaitForNextAsync(long afterSequence, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = _current;
            if (snapshot.Sequence > afterSequence)
                return snapshot;

            var signal = _signal;
            // Re-check after capturing the signal: a publish between the two volatile reads
            // would otherwise be missed (it completed the previous signal, not this one).
            snapshot = _current;
            if (snapshot.Sequence > afterSequence)
                return snapshot;

            await signal.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }
```

```csharp
// gatOS.GameMod/Game/TelemetrySampler.cs L141-L166
    internal void Tick(double dt, bool active)
    {
        if (!active || !_settings.Enabled)
        {
            _clock.Reset();
            return;
        }

        // Pick up an in-game rate change (cheap int read; only touches the clock when it moved).
        var rate = _settings.SampleRateHz;
        if (rate != _appliedRateHz)
        {
            _appliedRateHz = rate;
            _clock.SetRate(rate);
        }

        if (_clock.Tick(dt))
            using (_sampleStats.Measure()) // two timestamp reads; alloc-free
            {
                // Alloc/tick tripwire (GP3): a thread-local counter read before/after — alloc-free
                // to record, and the number the status window shows as "sample alloc".
                var allocBefore = GC.GetAllocatedBytesForCurrentThread();
                Sample();
                _allocStats.Add(GC.GetAllocatedBytesForCurrentThread() - allocBefore);
            }
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Universe.GetElapsedSeconds()` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs) | `time/ut` |
| `Universe.SimulationSpeed`, `GetSimulationSpeeds()`, `IsAutoWarpActive`, `AutoWarpTime` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L118-L131) | warp state |
| `Universe.GetLastSimStep().DeltaTime` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs) | `time/sim_dt` |
| `Universe.CurrentSystem.All` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs) | vehicle enumeration |
| `Program.ControlledVehicle` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs) | active vessel id |
| `VersionInfo.Current.VersionString` | [current/decomp/KSA/VersionInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionInfo.cs) | `status/game_version` |

## Pitfalls and version notes

- `Universe.GetElapsedSeconds()` replaced the old `SimTime` getter when KSA moved to the
  nanosecond `UniverseTime` (rev 5211, per gatOS anchor notes); still present in 5554.
- Sample inside per-vehicle try/catch: the vehicle list can contain vehicles being torn down,
  debris fragments (`Vehicle.IsDebris`) and EVA kittens (`KittenEva : Vehicle`).
- Do not publish mutable collections; gatOS builds fresh lists per sample and re-uses unchanged
  sub-objects by reference so consumers can reference-compare.
- A sampler exception latches sampling off for the session (`_samplerDead`) and `/sim` keeps
  serving the last snapshot.

## See also

- [Vessel flight-state reads](vessel-flight-state-reads.md)
- [Part and module state reads](part-module-state-reads.md)
- [Celestial body catalog reads](celestial-body-catalog-reads.md)
- [Snapshot-diff flight events](snapshot-diff-flight-events.md)
- [The /sim file models](sim-filesystem-file-models.md)
- [Logging shim and perf stats](logging-and-perfstats.md)
- [gatOS overview](../overview.md)
- [EventDiffer over consecutive immutable SimSnapshots](../../../howto/external-io/telemetry/detect-flight-events-from-snapshots/detect-flight-events-from-snapshots-1.md) - how-to article demonstrating this feature
- [Sampler builds an immutable snapshot, volatile swap, TaskCompletionSource wake](../../../howto/external-io/telemetry/export-game-state-to-other-threads/export-game-state-to-other-threads-1.md) - how-to article demonstrating this feature

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
