---
type: How-To
title: EventDiffer over consecutive immutable SimSnapshots
description: Compare each new snapshot with the previous one on the game thread and emit one event per edge, allocating nothing when nothing changed, then deliver the list to every transport
category: external-io
group: telemetry
topic: detect-flight-events-from-snapshots
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/snapshot-diff-flight-events, mods/gatos/features/telemetry-sampler-snapshot]
techniques: [edge-detection, immutable-snapshot, background-thread]
hooks: [harmony-prefix KSA.GameViewport.OnFrame]
lifecycle: [frame:viewports, frame:draw-ui, thread:background]
game_types: [KSA.Vehicle, KSA.Universe]
tags: [events, differ, sse, ndjson, zero-allocation, 9p]
---

Outcome: scripts block until "engine 0 flamed out" or "we entered the Moon's SOI" instead of polling. gatOS offers that through a blocking NDJSON file, server-sent events and an MQTT topic, with no KSA event bus behind any of them: the sampler compares each new snapshot with the previous one and emits an event for every edge it sees. The differ is pure, game-free C# and unit-tested without the game. Requires the snapshot pipeline of [Export live game state to background threads](../export-game-state-to-other-threads/overview.md). Demonstrated by [Snapshot-diff flight events](../../../../mods/gatos/features/snapshot-diff-flight-events.md).

## Background: how the game does it

The game exposes state, not changes. `Vehicle.Situation` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L549-L551)) is a property read from the vehicle's props, and an engine's `EngineControllerState.IsPropellantAvailable` ([current/decomp/KSA/EngineControllerState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineControllerState.cs#L1-L25)) is a flag in the module state; neither raises anything when it flips. gatOS's reader copies them into plain records ([gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L446-L496)), and the differ works on those records, never on game objects.

## Lifecycle and timing

- Detection runs on the game thread inside the sampler slot, right before publishing, so event timestamps are the sample's universal time and resolution is the sample rate (default 10 Hz). See [the sampler article](../export-game-state-to-other-threads/overview.md) for where the slot is and why it sees applied state.
- Gating: events off skips the diff, but side-channel queues (audio, IVA, scheduler, camera, stickers) are still drained so they never grow unbounded. Module-level events need the vessel detail pass, because docking, decoupler and engine propellant state come from it.
- Delivery runs on transport threads (`thread:background`): the 9p handle, the HTTP SSE writer and the MQTT publisher each await the store's publish signal. Nothing blocks the game thread.

## Steps

1. **Keep the previous published snapshot.** The sampler holds `_previous` and passes it, with the new sample's fields, to the differ right before building the next snapshot. The first snapshot of a session is the baseline and emits nothing.

   ```csharp
   // gatOS.GameMod/Game/TelemetrySampler.cs L231-L233
           var events = _settings.Events
               ? EventDiffer.Diff(_previous, ut, warp, activeId, vessels)
               : [];
   ```

2. **Diff global fields, then vessels.** Warp factor and active vessel id are compared directly. When the roster (ids in order) is unchanged, vessels are diffed pairwise by position with no allocation; the dictionary path exists only for the rare tick where a vessel appeared, vanished or reordered. The list is created lazily on the first event.

   ```csharp
   // gatOS.SimFs/Telemetry/EventDiffer.cs L32-L77
       public static IReadOnlyList<SimEvent> Diff(
           SimSnapshot? previous,
           double utSeconds,
           double warpFactor,
           string? activeVesselId,
           IReadOnlyList<VesselSnapshot> vessels)
       {
           if (previous is null)
               return [];

           List<SimEvent>? events = null;

           if (previous.WarpFactor != warpFactor)
               Add(ref events, new SimEvent(utSeconds, "warp-changed", null,
                   $"{Formats.Scalar(previous.WarpFactor)}→{Formats.Scalar(warpFactor)}"));

           if (previous.ActiveVesselId != activeVesselId)
               Add(ref events, new SimEvent(utSeconds, "active-changed", activeVesselId,
                   $"{previous.ActiveVesselId ?? "none"}→{activeVesselId ?? "none"}"));

           var before = previous.Vessels;
           if (SameRoster(before, vessels))
           {
               // The common case: same vessels in the same order — pairwise diff, no allocation.
               for (var i = 0; i < vessels.Count; i++)
                   DiffVessel(ref events, utSeconds, before[i], vessels[i]);
           }
           else
           {
               DiffChangedRoster(ref events, utSeconds, before, vessels);
           }

           return events ?? (IReadOnlyList<SimEvent>)[];
       }

       private static void Add(ref List<SimEvent>? events, SimEvent evt) => (events ??= []).Add(evt);

       private static bool SameRoster(IReadOnlyList<VesselSnapshot> before, IReadOnlyList<VesselSnapshot> now)
       {
           if (before.Count != now.Count)
               return false;
           for (var i = 0; i < now.Count; i++)
               if (!string.Equals(before[i].Id, now[i].Id, StringComparison.Ordinal))
                   return false;
           return true;
       }
   ```

3. **Detect vessel-level edges:** a text change in `Situation` becomes `situation-change`, a change of parent body becomes `soi-changed`.

   ```csharp
   // gatOS.SimFs/Telemetry/EventDiffer.cs L107-L117
       private static void DiffVessel(ref List<SimEvent>? events, double ut, VesselSnapshot was, VesselSnapshot now)
       {
           if (was.Situation != now.Situation)
               Add(ref events, new SimEvent(ut, "situation-change", now.Id,
                   $"{was.Situation}→{now.Situation}"));
           if (was.ParentBodyName != now.ParentBodyName)
               Add(ref events, new SimEvent(ut, "soi-changed", now.Id,
                   $"{was.ParentBodyName ?? "none"}→{now.ParentBodyName ?? "none"}"));

           DiffModules(ref events, ut, now.Id, was, now);
       }
   ```

4. **Detect module-level edges by stable index.** Every reader assigns `Index == list position`, so a positional walk over the shared prefix is the index match. An index that appears or disappears (a structural edit such as staging) is ignored rather than reported as a change. Engines report an `engine-state` flip and a `flameout` (active, then propellant lost):

   ```csharp
   // gatOS.SimFs/Telemetry/EventDiffer.cs L130-L144
           // Engines: active flip + flameout (propellant lost while active).
           var wasEngines = was.Engines;
           var nowEngines = now.Engines;
           for (int i = 0, n = Math.Min(wasEngines.Count, nowEngines.Count); i < n; i++)
           {
               var prev = wasEngines[i];
               var e = nowEngines[i];
               if (prev.Index != e.Index)
                   continue;
               if (prev.Active != e.Active)
                   Add(ref events, new SimEvent(ut, "engine-state", vesselId,
                       $"engine {e.Index} {(e.Active ? "on" : "off")}"));
               if (e.Active && prev.PropellantAvailable && !e.PropellantAvailable)
                   Add(ref events, new SimEvent(ut, "flameout", vesselId, $"engine {e.Index}"));
           }
   ```

   Decouplers report only the rising edge (firing is irreversible), docking ports report `docked` and `undocked`, animations report completion when the deployment state settles from Deploying or Retracting to Deployed or Retracted, and batteries report crossing empty (0.001) or full (0.999) with hysteresis:

   ```csharp
   // gatOS.SimFs/Telemetry/EventDiffer.cs L159-L192
           // Decouplers: fired (rising edge only — firing is irreversible).
           var wasDec = was.Decouplers;
           var nowDec = now.Decouplers;
           for (int i = 0, n = Math.Min(wasDec.Count, nowDec.Count); i < n; i++)
           {
               var prev = wasDec[i];
               var d = nowDec[i];
               if (prev.Index == d.Index && !prev.Fired && d.Fired)
                   Add(ref events, new SimEvent(ut, "decoupled", vesselId, $"decoupler {d.Index}"));
           }

           // Animations: settled to a terminal state (Deploying/Retracting → Deployed/Retracted).
           var wasAnim = was.Animations;
           var nowAnim = now.Animations;
           for (int i = 0, n = Math.Min(wasAnim.Count, nowAnim.Count); i < n; i++)
           {
               var prev = wasAnim[i];
               var a = nowAnim[i];
               if (prev.Index != a.Index || prev.DeploymentState == a.DeploymentState)
                   continue;
               if (a.DeploymentState is "Deployed" or "Retracted"
                   && prev.DeploymentState is "Deploying" or "Retracting")
                   Add(ref events, new SimEvent(ut, "animation-complete", vesselId,
                       $"animation {a.Index} {a.DeploymentState}"));
           }

           // Battery: crossing empty / full.
           if (was.BatteryChargeFraction is { } prevCharge && now.BatteryChargeFraction is { } charge)
           {
               if (prevCharge > 0.01 && charge <= 0.001)
                   Add(ref events, new SimEvent(ut, "battery-depleted", vesselId, "0"));
               else if (prevCharge < 0.999 && charge >= 0.999)
                   Add(ref events, new SimEvent(ut, "battery-charged", vesselId, "1"));
           }
   ```

5. **Fold side-channel events and publish with the snapshot.** The list rides in `NewEvents`, so every consumer sees the same events in the same order, tied to the snapshot sequence ([gatOS.GameMod/Game/TelemetrySampler.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/TelemetrySampler.cs#L231-L251)).

6. **Deliver to a blocking reader.** The 9p `events` file reports size 1 (a size of 0 makes the Linux v9fs client answer `read()` with instant EOF without asking the server), starts at the current sequence so only events after the open are delivered, parks on `WaitForNextAsync`, writes the NDJSON lines, then answers the kernel's two continuation reads with zero bytes so `cat` prints the line immediately:

   ```csharp
   // gatOS.SimFs/EventsFile.cs L78-L96
                   while (_pendingAt >= _pending.Length)
                   {
                       var snapshot = await _store.WaitForNextAsync(_lastSeq, token).ConfigureAwait(false);
                       _lastSeq = snapshot.Sequence;
                       if (snapshot.NewEvents.Count == 0)
                           continue;
                       using var lines = new MemoryStream();
                       foreach (var simEvent in snapshot.NewEvents)
                           lines.Write(Formats.EventLine(simEvent));
                       _pending = lines.ToArray();
                       _pendingAt = 0;
                   }

                   var length = (int)Math.Min(count, (uint)(_pending.Length - _pendingAt));
                   var chunk = _pending.AsMemory(_pendingAt, length);
                   _pendingAt += length;
                   if (_pendingAt >= _pending.Length)
                       _zerosOwed = 2; // complete the syscall: two 0-byte Rreads (spike rule 2)
                   return chunk;
   ```

   The same `NewEvents` list feeds SSE and the MQTT `events` topic; see [the 9P file-model article](../../processes/host-guest-transports-tcp-9p-and-ssh/overview.md) and [the HTTP and MQTT servers](../../network/host-http-and-mqtt-servers-in-the-game-process/overview.md).

## Complete example

| File | Role |
|---|---|
| [gatOS.SimFs/Telemetry/EventDiffer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Telemetry/EventDiffer.cs) | Pure diff of two snapshots |
| [gatOS.GameMod/Game/TelemetrySampler.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/TelemetrySampler.cs) | Diff call plus side-channel folding |
| [gatOS.SimFs/EventsFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/EventsFile.cs) | Blocking-event 9p file |
| [gatOS.SimFs/Snapshots/SnapshotStore.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Snapshots/SnapshotStore.cs) | `WaitForNextAsync` used by every event consumer |

## Variations and gotchas

- **Polling-based edge detection misses sub-sample transients.** Raise `sample_rate_hz` (up to 120) if a script needs finer timing.
- **Structural edits are invisible.** An engine that appears or disappears (staging, docking) is ignored by the index-based match.
- **Debris and docking.** Debris fragments created by KSA's part-failure system appear as `vessel-appeared`. Larger vessels can absorb smaller ones on docking, which also yields `vessel-removed`.
- **Keep the differ game-free** so it is unit-testable; only the accessor half of the sampler touches KSA.
- **Idle gaps.** When sampling idles (nobody listening) the sampler keeps `_previous` from before the idle, so the first diff after resuming reports every edge that happened during the gap as one burst, all stamped with the resume time.

## See also

- [Overview](overview.md)
- [Approach 2: debounced heuristics for webcast callouts](detect-flight-events-from-snapshots-2.md)
- [Snapshot-diff flight events](../../../../mods/gatos/features/snapshot-diff-flight-events.md), [Telemetry sampler and immutable snapshot](../../../../mods/gatos/features/telemetry-sampler-snapshot.md)
- [Export live game state to background threads](../export-game-state-to-other-threads/overview.md)
- [Connect a VM guest to the game with slirp TCP, a 9P filesystem and pinned SSH](../../processes/host-guest-transports-tcp-9p-and-ssh/overview.md)
