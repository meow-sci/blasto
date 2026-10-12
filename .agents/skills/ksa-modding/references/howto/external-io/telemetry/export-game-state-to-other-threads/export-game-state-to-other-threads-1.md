---
type: How-To
title: Sampler builds an immutable snapshot, volatile swap, TaskCompletionSource wake
description: Publish one immutable SimSnapshot from the game thread with a single volatile reference swap so any number of transport threads read lock-free and async waiters wake on publish, sampled by a drift-free clock
category: external-io
group: telemetry
topic: export-game-state-to-other-threads
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/telemetry-sampler-snapshot]
techniques: [immutable-snapshot, background-thread, starmap-lifecycle]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapBeforeGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:viewports, frame:draw-ui, frame:after-on-frame, thread:background]
game_types: [KSA.Universe, KSA.Program, KSA.Vehicle, KSA.CelestialSystem]
tags: [snapshot, volatile, taskcompletionsource, sample-clock, memoization, allocation]
---

Outcome: all of a mod's transports (files, HTTP, MQTT, MCP, serial in gatOS) serve the same data and none of them ever touches a KSA object. They read the latest `SimSnapshot` from a store; a sampler on the game thread builds the next one a few times per second. This is the canonical pattern for exporting live game state from a KSA mod to other threads without locks and without stalling the frame. Requires StarMap. Demonstrated by [Telemetry sampler and immutable snapshot](../../../../mods/gatos/features/telemetry-sampler-snapshot.md).

## Background: how the game does it

The vehicle step runs on worker threads on copies; its results reach the main-thread `Vehicle` objects in `Universe.ApplyVehicleSolvers` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952)), early in `PrepareFrame`. A hook that runs later in the same frame reads the state of the step applied at the start of that frame, one step behind the step that is computing. Two exceptions from the decomp: `Parachute.UpdateModules` mutates live `Parachute` objects from the worker, and part-tree derived data can be rebuilt on the worker if a tree is dirty (gatOS does not report parachutes, and `PrepareFrame` flushes dirty trees before dispatch).

There is no game facility to hand state to other threads, so the mod defines its own contract: build an immutable value on the main thread, publish a reference.

## Lifecycle and timing

| Piece | Runs |
|---|---|
| `TelemetrySampler.Tick(dt, active)` and `Sample()` | Game thread. With the camera feature on (default) from the prefix on the main `GameViewport.OnFrame` (`frame:viewports`, every frame); otherwise `StarMapBeforeGui` (`frame:draw-ui`), or `StarMapAfterOnFrame` (`frame:after-on-frame`) when the HUD is hidden |
| `SnapshotStore.Publish` | Game thread; one reference write, then wake waiters |
| Readers, `WaitForNextAsync` | Transport threads (`thread:background`); see either the old or the new snapshot, never a torn one |

The sampler idles when nobody listens: `active` is true only while the VM is starting or running, or a 9p, HTTP, MQTT or MCP client is connected, otherwise the sample clock is reset and nothing is read. The hidden-HUD behaviour follows the shared frame hook ([see the marshalling topic](../../control/marshal-commands-to-the-game-thread/overview.md)).

## Steps

1. **Define an immutable snapshot.** A `record` of sequence number, sim time, warp, active vessel id and read-only lists. `Sequence` is what waiters compare. An `Empty` sentinel means readers never see null.

   ```csharp
   // gatOS.SimFs/Snapshots/SimSnapshot.cs L27-L39
   public sealed record SimSnapshot(
       long Sequence,
       double UtSeconds,
       double WarpFactor,
       string? ActiveVesselId,
       IReadOnlyList<VesselSnapshot> Vessels,
       IReadOnlyList<SimEvent> NewEvents,
       string GameVersion,
       double SampleRateHz,
       IReadOnlyList<AccessorHealthSnapshot> Accessors)
   {
       /// <summary>The pre-first-publish snapshot: sequence 0, no vessels, no events.</summary>
       public static SimSnapshot Empty { get; } = new(0, 0, 1, null, [], [], "", 0, []);
   ```

2. **Write the store: one volatile reference and a signal.** `Publish` swaps the snapshot first, then completes the old signal, so a waiter that captured the old signal wakes and re-reads, and one that captured the new signal waits for the next publish. `RunContinuationsAsynchronously` keeps waiter code off the game thread.

   ```csharp
   // gatOS.SimFs/Snapshots/SnapshotStore.cs L10-L27
       private volatile SimSnapshot _current = SimSnapshot.Empty;
       private volatile TaskCompletionSource _signal = NewSignal();

       /// <summary>The latest published snapshot (never null; starts at <see cref="SimSnapshot.Empty"/>).</summary>
       public SimSnapshot Current => _current;

       /// <summary>
       ///     Publishes a snapshot. Single writer by contract (the game-thread sampler);
       ///     <paramref name="snapshot"/>.Sequence must be greater than the current one.
       /// </summary>
       public void Publish(SimSnapshot snapshot)
       {
           _current = snapshot;
           // Swap first, then complete: a waiter that captured the old signal wakes and re-reads
           // _current; one that already captured the new signal waits for the next publish.
           var completed = Interlocked.Exchange(ref _signal, NewSignal());
           completed.SetResult();
       }
   ```

   ```csharp
   // gatOS.SimFs/Snapshots/SnapshotStore.cs L34-L56
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

       private static TaskCompletionSource NewSignal()
           => new(TaskCreationOptions.RunContinuationsAsynchronously);
   }
   ```

3. **Tick the sampler through a clock, behind two gates.** `active` and the master enable flag gate the work; a rate retuned in-game is picked up without a restart; the measured `Sample()` records a thread-local allocation delta so an unexpected per-sample allocation shows up in the status window.

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

4. **Sample inside per-vehicle try/catch.** The vehicle list can contain vehicles being torn down, debris and EVA kittens; one failing vehicle must not kill the snapshot. Reuse unchanged sub-objects by reference so consumers can reference-compare, and build fresh lists rather than publishing mutable ones.

   ```csharp
   // gatOS.GameMod/Game/TelemetrySampler.cs L181-L213
           var ut = Sanitize.Finite(Universe.GetElapsedSeconds());
           var warp = Sanitize.Finite(Universe.SimulationSpeed);
           var activeId = Program.ControlledVehicle?.Id;
           var detail = _settings.VesselDetail;

           var vessels = new List<VesselSnapshot>(_previous?.Vessels.Count ?? 4);
           IReadOnlyList<BodySnapshot> bodies = [];
           SystemSnapshot? systemSummary = null;
           if (Universe.CurrentSystem is { } system)
           {
               foreach (var astronomical in system.All.UnsafeAsList())
               {
                   if (astronomical is not Vehicle vehicle)
                       continue;
                   try
                   {
                       var vessel = VesselReader.Sample(vehicle, activeId, ut, detail);
                       // The parts list is its own gate + cache (the welds anchor picker), separate from
                       // the G3 detail pass so it can be enabled without the heavier per-module reads.
                       if (_settings.VesselParts)
                           vessel = vessel with { Parts = PartsReader.Sample(vehicle, ut) };
                       vessels.Add(vessel);
                   }
                   catch (Exception ex)
                   {
                       // One vehicle mid-teardown must not kill the snapshot; log the first only.
                       if (!_vehicleErrorLogged)
                       {
                           _vehicleErrorLogged = true;
                           ModLog.Log.Debug($"telemetry: a vehicle sample failed (logged once): {ex.Message}");
                       }
                   }
               }
   ```

5. **Construct and publish.** Fold event lists in, increment the sequence, remember the previous snapshot for the event differ ([Detect flight events when the game has no event bus](../detect-flight-events-from-snapshots/overview.md)) and publish:

   ```csharp
   // gatOS.GameMod/Game/TelemetrySampler.cs L252-L254, L271-L272
           var snapshot = new SimSnapshot(++_sequence, ut, warp, activeId, vessels, events,
               GameVersion(), _appliedRateHz, _health.Snapshot())
           {
           // ...
           _previous = snapshot;
           _store.Publish(snapshot);
   ```

6. **Gate expensive streams at the source.** `TelemetrySettings` holds volatile per-stream gates (`vessel_detail`, `vessel_parts`, `bodies`, `events`) written by the settings UI and read each tick; a disabled stream skips its KSA reads and shrinks the snapshot, so every transport serves less by construction.

7. **Memoize pure projections per snapshot.** N readers of one value should cost one format. A `ConditionalWeakTable` keyed by the snapshot instance holds the vessel-by-id map and the formatted stream line; the cache dies with the snapshot and needs no change to the record shape.

   ```csharp
   // gatOS.SimFs/Snapshots/SnapshotIndex.cs L22-L48
       private static readonly ConditionalWeakTable<SimSnapshot, Entry> Table = new();

       private sealed class Entry
       {
           internal volatile Dictionary<string, VesselSnapshot>? VesselsById;
           internal volatile ConcurrentDictionary<string, byte[]>? StreamLines;
       }

       /// <summary>
       ///     The vessel with this id in <paramref name="snapshot"/>, or null — an O(1) dictionary hit
       ///     replacing the per-read O(N) list scans (and their closure allocations) every transport
       ///     used to do.
       /// </summary>
       public static VesselSnapshot? VesselById(SimSnapshot snapshot, string vesselId)
       {
           var entry = Table.GetOrCreateValue(snapshot);
           var byId = entry.VesselsById;
           if (byId is null)
           {
               byId = new Dictionary<string, VesselSnapshot>(snapshot.Vessels.Count, StringComparer.Ordinal);
               for (var i = 0; i < snapshot.Vessels.Count; i++)
                   byId.TryAdd(snapshot.Vessels[i].Id, snapshot.Vessels[i]);
               entry.VesselsById = byId;
           }

           return byId.GetValueOrDefault(vesselId);
       }
   ```

8. **Compute the activity flag at the call site.** The sampler wrapper decides whether anyone can read:

   ```csharp
   // gatOS.GameMod/Game/Mod.Game.cs L227-L248
       partial void SampleTelemetry(double dt)
       {
           if (_samplerDead || _simStore is not { } store)
               return;

           try
           {
               EnsureControlObjects();
               _telemetry ??= new TelemetrySampler(store, _telemetrySettings, _health!, _sampleStats,
                   _sampleAllocStats, _weldManager!, _thugLife!, _ivaPhysics!, _ivaStats, _audioStore,
                   _scheduleStore, _cameraDirector, Config.DebugNamespace, _faceFx, _stickerStore);
               // Sample only while something can actually read /sim: the VM is up, or a host-side
               // transport client is connected (9p / HTTP / MQTT). Otherwise the sampler idles for free.
               var state = CurrentVmStatus.State;
               var active = state is VmState.Starting or VmState.Running
                            || (_simServer?.ActiveSessions ?? 0) > 0
                            || (_httpServer?.ActiveSessions ?? 0) > 0
                            || (_mqttBroker?.ConnectedClients ?? 0) > 0
                            || (_mcpServer?.ActiveRequests ?? 0) > 0;
               _telemetry.Tick(dt, active);
           }
           catch (Exception ex)
   ```

## Choosing the sampling cadence: the drift-free SampleClock

Sampling every frame wastes the frame budget and publishes more snapshots than a network reader can use. `SampleClock` accumulates the frame `dt` and fires at most once per call. It keeps the sub-interval remainder so the cadence does not drift, but a backlog longer than one interval (a hitch) is dropped, never replayed: telemetry wants the latest state, and one sample per frame is all a snapshot swap can publish anyway.

```csharp
// gatOS.SimFs/Telemetry/SampleClock.cs L22-L53
    /// <summary>
    ///     Changes the cadence live (the user retuned <c>sample_rate_hz</c> in-game). The accumulated
    ///     phase is kept, so a rate change never fires an immediate extra sample nor stalls a due one;
    ///     a non-positive or non-finite rate is ignored (the clock keeps its current interval).
    /// </summary>
    public void SetRate(double rateHz)
    {
        if (rateHz > 0 && double.IsFinite(rateHz))
            _intervalSeconds = 1.0 / rateHz;
    }

    /// <summary>Advances by one frame; true = take a sample now.</summary>
    public bool Tick(double dt)
    {
        if (dt > 0 && double.IsFinite(dt))
            _accumulator += dt;
        if (_accumulator < _intervalSeconds)
            return false;

        // Keep sub-interval phase for drift-free cadence, but a backlog of more than one
        // interval (a long frame hitch) is dropped outright, never burst-replayed.
        _accumulator -= _intervalSeconds;
        if (_accumulator >= _intervalSeconds)
            _accumulator = 0;
        return true;
    }

    /// <summary>
    ///     Drops accumulated time (used when sampling was gated off, so reactivation does not
    ///     fire instantly from a stale accumulator — harmless, but keeps cadence honest).
    /// </summary>
    public void Reset() => _accumulator = 0;
```

Notes:

- The clock is game-free (a plain class over `double`), so it is unit-testable without KSA.
- `dt` is the frame's player-clock delta, which `App.Run` clamps to `1 / MinTargetFrameRate` ([current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L49-L53)), so the clock follows rendered frames, not wall time, and does not advance faster than the clamp during a stall.
- The default is 10 Hz (`sample_rate_hz`, clamped to 1-120 in [gatOS.GameMod/Configuration/GatOsConfig.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Configuration/GatOsConfig.cs#L728-L728)). Sample time is `Universe.GetElapsedSeconds()`, so a reader that integrates should use the change in `ut` between snapshots, not wall time.
- Reset the clock when sampling is gated off, so reactivation does not fire from a stale accumulator.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/TelemetrySampler.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/TelemetrySampler.cs) | `Tick`, `Sample`, event folding, publish |
| [gatOS.SimFs/Telemetry/SampleClock.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Telemetry/SampleClock.cs) | Drift-free rate limiter that drops backlog |
| [gatOS.SimFs/Snapshots/SnapshotStore.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Snapshots/SnapshotStore.cs) | Single-writer volatile exchange with async wait |
| [gatOS.SimFs/Snapshots/SimSnapshot.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Snapshots/SimSnapshot.cs) | Immutable record and `Empty` sentinel |
| [gatOS.SimFs/Snapshots/SnapshotIndex.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Snapshots/SnapshotIndex.cs) | Per-snapshot memoized projections |
| [gatOS.SimFs/TelemetrySettings.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/TelemetrySettings.cs) | Volatile cadence and per-stream gates |

## Variations and gotchas

- **Single writer by contract.** `Publish` assumes only the game-thread sampler calls it and that sequences increase.
- **A sampler exception latches sampling off** for the session (`_samplerDead`) and readers keep the last snapshot. Log once, not per frame.
- **`Universe.GetElapsedSeconds()`** replaced the old sim-time getter when KSA moved to nanosecond `UniverseTime`; gatOS's source comment says it throws before the first sim step, but in build 2026.10.10.5554 it is `_lastSimStep.NextTime.Seconds()` over a static `SimStep` struct field ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2393-L2402)), which reads as 0 for the default struct. Treat gatOS's try/catch as a defensive guard and do not sample before a system is loaded.
- **Never publish mutable collections**; consumers may iterate while you build the next one.
- **Allocation.** One snapshot per sample is fine at 10 Hz; watch the per-sample allocation counter if you raise the rate.
- **Memo races are benign.** Two threads may compute the same projection once; both results are correct because the input is immutable.

## See also

- [Overview](overview.md)
- [Approach 2: plain-struct snapshot sampled after the frame](export-game-state-to-other-threads-2.md)
- [Telemetry sampler and immutable snapshot](../../../../mods/gatos/features/telemetry-sampler-snapshot.md)
- [Marshal commands from background threads to the game thread](../../control/marshal-commands-to-the-game-thread/overview.md)
- [Host HTTP and MQTT servers in the game process](../../network/host-http-and-mqtt-servers-in-the-game-process/overview.md)
- [Read vehicle flight state](../../../flight-and-orbits/flight-state/read-vehicle-flight-state/overview.md)
