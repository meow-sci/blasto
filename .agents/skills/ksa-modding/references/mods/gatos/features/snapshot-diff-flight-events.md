---
type: Mod Feature
title: Snapshot-diff flight events
description: KSA has no public event bus for flight events, so gatOS derives situation, SOI, engine, flameout, docking, decoupling, animation and battery events by diffing consecutive immutable snapshots
mod: gatos
category: external-io
techniques: [immutable-snapshot, edge-detection, background-thread]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapBeforeGui]
lifecycle: [frame:viewports, frame:draw-ui, thread:background]
game_types: [KSA.Vehicle]
tags: [events, telemetry, diffing, ndjson, sse, mqtt, zero-alloc]
---

Scripts want to block until "engine 0 flamed out" or "we entered the Moon's SOI" instead of
polling. gatOS offers that through `/sim/events` (a blocking NDJSON file), `GET /v1/events` (SSE)
and the `gatos/events` MQTT topic. There is no KSA event bus behind it: the sampler compares each
new snapshot with the previous one and emits an event for every edge it sees. The differ is pure,
game-free C# and unit-tested without the game.

## How it works

`EventDiffer.Diff(previous, ut, warp, activeId, vessels)` runs inside the sampler right before
publishing ([gatOS.GameMod/Game/TelemetrySampler.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/TelemetrySampler.cs#L231-L251)):

| Event | Edge detected |
|---|---|
| `warp-changed` | `SimSnapshot.WarpFactor` differs |
| `active-changed` | controlled vessel id differs |
| `vessel-appeared` / `vessel-removed` | roster differs (id-matched slow path) |
| `situation-change` | `VesselSnapshot.Situation` text differs |
| `soi-changed` | parent body id differs |
| `engine-state` / `flameout` | engine `Active` flips; active engine loses `PropellantAvailable` |
| `docked` / `undocked` | docking port `Docked` flips |
| `decoupled` | decoupler `Fired` rising edge |
| `animation-complete` | deployment state settles from Deploying/Retracting to Deployed/Retracted |
| `battery-depleted` / `battery-charged` | charge fraction crosses 0.001 / 0.999 |

The first snapshot of a session is the baseline and emits nothing. Other subsystems (audio,
IVA physics, scheduler, camera, stickers) queue their own events, which the sampler drains and
appends to the same `NewEvents` list so every transport carries them.

**Zero allocation in the common case.** Most ticks have no events. The differ allocates its list
lazily on the first event and, when the vessel roster is unchanged, walks vessels and module lists
by position (every reader assigns `Index == list position`), falling back to an id dictionary only
when vessels appeared, vanished or reordered ([gatOS.SimFs/Telemetry/EventDiffer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Telemetry/EventDiffer.cs#L32-L77)).

**Delivering a blocking file over 9p.** `/sim/events` reports `Size = 1` (a 0 size makes the Linux
v9fs client answer `read()` with instant EOF without asking the server), parks the read on
`SnapshotStore.WaitForNextAsync` until a snapshot carries events, returns the NDJSON lines, then
answers the kernel's two continuation reads with 0 bytes so `cat` prints the line immediately
([gatOS.SimFs/EventsFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/EventsFile.cs#L61-L102)). See [The /sim file models](sim-filesystem-file-models.md).

## Lifecycle and timing

- Detection runs on the main thread inside the sampler slot, so event timestamps are the sample's
  `ut` and resolution is the sample rate (default 10 Hz): an edge that flips and flips back
  between samples is missed.
- Gating: `telemetry_events` off skips the diff; side-channel queues are still drained so they
  never grow unbounded. Module-level events need `telemetry_vessel_detail`, because docking,
  decoupler and engine propellant state come from the detail pass.
- Delivery runs on transport threads (`thread:background`): the 9p handle, the HTTP SSE writer and
  the MQTT publisher each await the store's publish signal; nothing blocks the game thread.

## Key code

| Path | Role |
|---|---|
| [gatOS.SimFs/Telemetry/EventDiffer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Telemetry/EventDiffer.cs) | pure diff of two snapshots |
| [gatOS.GameMod/Game/TelemetrySampler.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/TelemetrySampler.cs#L231-L251) | diff plus side-channel event folding |
| [gatOS.SimFs/EventsFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/EventsFile.cs) | blocking-event 9p file |
| [gatOS.SimFs/Snapshots/SnapshotStore.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Snapshots/SnapshotStore.cs#L34-L52) | `WaitForNextAsync` used by every event consumer |

## Snippets

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

## Game APIs touched

None directly. The differ consumes `VesselSnapshot` records built from `KSA.Vehicle` by
[Vessel flight-state reads](vessel-flight-state-reads.md) and
[Part and module state reads](part-module-state-reads.md); `Vehicle.Situation` is the source of
`situation-change`.[^ksa-vehicle]

## Pitfalls and version notes

- Polling-based edge detection misses sub-sample transients; raise `sample_rate_hz` (up to 120)
  if a script needs finer timing.
- Ordinal-based matching assumes structural edits are rare; an engine that appears or disappears
  (staging, docking) is ignored rather than reported as a state change.
- Debris fragments created by KSA's part-failure system appear as `vessel-appeared` events.
- `docked` identity can change on docking (larger vessels absorb smaller ones, rev 5076 per the
  gatOS AGENTS.md upgrade notes), which also yields `vessel-removed`.

## See also

- [Telemetry sampler and snapshot store](telemetry-sampler-snapshot.md)
- [The /sim file models](sim-filesystem-file-models.md)
- [HTTP /v1 API server](http-v1-api-server.md)
- [Embedded MQTT broker](embedded-mqtt-broker.md)
- [gatOS overview](../overview.md)
- [Sensor, StreamFile, EventsFile and CommandFile models under Linux netfslib rules](../../../howto/external-io/processes/host-guest-transports-tcp-9p-and-ssh/host-guest-transports-tcp-9p-and-ssh-3.md) - how-to article demonstrating this feature
- [EventDiffer over consecutive immutable SimSnapshots](../../../howto/external-io/telemetry/detect-flight-events-from-snapshots/detect-flight-events-from-snapshots-1.md) - how-to article demonstrating this feature

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
