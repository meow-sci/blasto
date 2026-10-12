---
type: Mod Feature
title: In-flight event detection and callouts
description: Infers liftoff, max Q, MECO/SECO, stage and booster separation, relights and landing from frame-to-frame deltas of engine, part, vehicle and decoupler counts, and announces them as fading banners
mod: ksa-public-telemetry
category: vehicles-and-parts
techniques: [starmap-lifecycle, imgui-drawlist, state-diffing]
hooks: [starmap StarMapAfterOnFrame, starmap StarMapAfterGui, harmony-postfix KSA.Program.DrawFps]
lifecycle: [frame:after-on-frame, frame:draw-ui]
game_types: [KSA.PartTree, KSA.Decoupler, KSA.Part, KSA.CelestialSystem, KSA.LookupCollection, KSA.Vehicle]
tags: [events, staging, max-q, meco, notifications, heuristics]
---

During flight, short banners slide in at the top-left: "LIFTOFF Vehicle has left the pad.",
"MAX Q", "MECO", "STAGE SEP", "BOOSTER SEP", "SES-1", "ENGINE CUTOFF", "LANDING BURN", "LANDING". The
same events become labelled dots on the timeline. The game raises no events for any of this, so the mod
infers them by diffing a few counters between consecutive samples. The README is upfront that this is
"not totally reliable".[^kpt-readme]

## How it works

Each sample, `MissionEventLog.Update(snapshot, liftoffThisFrame, missionVehicleCount, rebaseline)` runs
the detectors in order and records `MissionEvent(kind, missionTime)` values
([Telemetry/MissionEventLog.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionEventLog.cs#L78-L133)). Inputs come from the sampler:

| Signal | Source | Used for |
|---|---|---|
| `BurningEngineCount` | Engines on the diagram with `Throttled`/`Nominal` status | Ignition and cutoff edges |
| `PartCount` | `PartTree.Count` ([current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L134)) | Stage separation (part count dropped) |
| Mission vehicle count | Vehicles in `Universe.CurrentSystem.All` with the same `LaunchGameTime.Nanoseconds` ([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L243-L279)) | Stage separation (a new piece appeared) |
| `AttachedRadialDecouplers` | `Decoupler` modules whose `Connector` has a `Connection` and a `ToSurface`/`FromSurface` flag ([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L347-L373)) | Booster vs stage separation |
| `DynamicPressure` | Sampler | Max Q |
| `HasSurfaceContact`, `VerticalSpeed`, `RadarAltitude` | Sampler | Landing burn, landing |

Detectors ([Telemetry/MissionEventLog.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionEventLog.cs#L135-L252)):

- **Liftoff**: the clock's `LiftoffThisFrame` (see [Mission clock and liftoff detection](mission-clock-and-liftoff-detection.md)).
- **Max Q**: tracks the peak q; fires once, stamped at the peak time, when q falls below 95 % of a
  peak of at least 1 kPa.
- **Stage/booster separation**: fires when the mission vehicle count grows or the part count drops,
  debounced to one per 2 s; called `BOOSTER SEP` if the number of attached radial decouplers dropped in
  the same frame.
- **Cutoff**: burning count fell to zero after a burn of at least 1 s (2 s debounce). The first is
  `MECO`, the second `SECO`, later ones `ENGINE CUTOFF`.
- **Ignition**: burning count rose from zero after at least one cutoff. On a different vehicle than the
  one that cut off (the player switched to the upper stage) the first one is `SES-1`; descending below
  10 km radar altitude it is `LANDING BURN`; otherwise `ENGINE IGNITION`.
- **Landing**: new surface contact with |vertical speed| under 3 m/s.

Baselines: when the tracked vehicle changes, or signal is regained after a freeze, the previous-frame
counters are reset from the current frame instead of diffed, so switching vehicles does not fire
spurious events ([Telemetry/MissionEventLog.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionEventLog.cs#L97-L107)). The vehicle-count enumeration is
cached on `All.Count` and the key so it does not scan every frame
([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L258-L261)).

Callouts: `NotificationPanel.Update` picks up newly recorded events (tracking `Recorded.Count` and the
log's `Generation`), skips kinds the player muted in settings, keeps three at most, and draws notched
banners with a bold label and a muted explainer ([Rendering/NotificationPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/NotificationPanel.cs#L48-L99),
[Rendering/NotificationPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/NotificationPanel.cs#L178-L229)). Muting only hides the banner; the timeline still
shows the event.

## Lifecycle and timing

- Detection runs in `frame:after-on-frame` as part of `TelemetrySampler.Sample`, on the vehicle state
  applied in this frame's `PrepareFrame` (the next step is already running on the workers). Whatever
  split, decoupling or destruction the game performed earlier in the frame, from the solver apply,
  input or the UI, is already reflected in that frame's part count and vehicle list; the detector does
  not need to know which phase did it.
- Banners age by the UI `dt` and draw in `frame:draw-ui` on the foreground draw list.
- Detection is per sample, so at high time warp an event can be stamped up to one frame late in
  universe time, which at warp can be many seconds.

## Key code

| Path | Role |
|---|---|
| [Telemetry/MissionEventLog.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionEventLog.cs#L78-L252) | Edge detectors, debounces, naming |
| [Telemetry/MissionEvent.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionEvent.cs#L3-L60) | Event kinds, labels, explainers |
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L243-L279) | Count of vehicles sharing the launch key |
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L347-L373) | Attached radial decoupler count |
| [Rendering/NotificationPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/NotificationPanel.cs#L48-L229) | Banner queue and drawing |

## Snippets

```csharp
// Telemetry/MissionEventLog.cs L78-L107, L124-L128
    public void Update(
        TelemetrySnapshot snapshot, bool liftoffThisFrame, int missionVehicleCount, bool rebaseline)
    {
        _firedThisFrame.Clear();

        int burning = snapshot.BurningEngineCount;
        int parts = snapshot.PartCount;
        double now = snapshot.MissionElapsedSeconds;
        bool flying = snapshot.HasLiftoff;

        bool gainedVehicle = _previousVehicleCount >= 0 && missionVehicleCount > _previousVehicleCount;
        _previousVehicleCount = missionVehicleCount;

        int radial = snapshot.AttachedRadialDecouplers;
        bool releasedRadial = _previousRadialDecouplers >= 0 && radial < _previousRadialDecouplers;
        _previousRadialDecouplers = radial;

        DetectStaging(gainedVehicle, releasedRadial, flying, now);

        bool sameVehicle = string.Equals(_baselineVehicle, snapshot.VehicleName, StringComparison.Ordinal);

        if (!sameVehicle || rebaseline)
        {
            _baselineVehicle = snapshot.VehicleName;
            _previousBurning = burning;
            _previousPartCount = parts;
            _previousContact = snapshot.HasSurfaceContact;
            _burnStartedAt = burning > 0 ? now - MinBurnBeforeCutoff : double.NegativeInfinity;
            return;
        }
// ...
        DetectMaxQ(snapshot, flying, now);
        DetectStaging(parts < _previousPartCount, releasedRadial, flying, now);
        DetectIgnition(snapshot, burning, flying, now);
        DetectCutoff(snapshot, burning, flying, now, endingRunLength);
        DetectLanding(snapshot, flying, now);
```

```csharp
// Telemetry/TelemetrySampler.cs L347-L373
    private static void SampleDecouplers(Vehicle vehicle, TelemetrySnapshot snapshot)
    {
        PartTree parts = vehicle.Parts;

        if (parts is null)
        {
            return;
        }

        const Part.Connector.Flag surface =
            Part.Connector.Flag.ToSurface | Part.Connector.Flag.FromSurface;

        Span<Decoupler> decouplers = parts.Modules.Get<Decoupler>();
        int attached = 0;

        for (int i = 0; i < decouplers.Length; i++)
        {
            Part.Connector connector = decouplers[i].Connector;

            if (connector.Connection is not null && (connector.Flags & surface) != 0)
            {
                attached++;
            }
        }

        snapshot.AttachedRadialDecouplers = attached;
    }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ModuleList.Get<Decoupler>()` on `PartTree.Modules` | [current/decomp/KSA/ModuleList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleList.cs#L180), [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L62) | All decoupler modules |
| `Decoupler.Connector` | [current/decomp/KSA/Decoupler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Decoupler.cs#L65) | The decoupler's attachment point |
| `Part.Connector.Flag.ToSurface`/`FromSurface`, `Connector.Connection` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L194-L198), [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L238) | Radial (surface) attachment still connected |
| `Universe.CurrentSystem`, `CelestialSystem.All` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L114), [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L66) | Every object in the system |
| `LookupCollection.Count`, `GetIndex`, `TryGet` | [current/decomp/KSA/LookupCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LookupCollection.cs#L104), [current/decomp/KSA/LookupCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LookupCollection.cs#L186), [current/decomp/KSA/LookupCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LookupCollection.cs#L284) | Enumerate and look up vehicles by id |
| `PartTree.Count` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L134) | Part count |

## Pitfalls and version notes

- **Heuristic, as the README warns.** Any part loss (breakage, a jettisoned fairing, a decoupled
  payload) reads as a stage separation; an engine flame-out from propellant starvation reads as MECO; a
  throttle-to-zero for a few seconds and back is a cutoff plus a relight.
- **No SOI-change, orbit-insertion or deploy events.** The detectors listed above are all there is;
  the log has no sphere-of-influence or parachute detection.
- **Counting by launch key** treats every vehicle spawned with the same `LaunchGameTime` as one
  mission, including debris from that rocket.
- **Only the controlled vehicle is watched.** Events of a booster flying on its own are not seen unless
  the player switches to it, and switching rebaselines the counters.

## See also

- [Mission timeline with planned burns](mission-timeline-with-planned-burns.md)
- [Mission clock and liftoff detection](mission-clock-and-liftoff-detection.md)
- [Freeze on signal loss](freeze-on-signal-loss.md)
- [Broadcast overlay on the ImGui foreground draw list](foreground-drawlist-broadcast-overlay.md)
- [Debounced heuristics on burning-engine and part counts for webcast callouts](../../../howto/external-io/telemetry/detect-flight-events-from-snapshots/detect-flight-events-from-snapshots-2.md) - how-to article demonstrating this feature
- [Detect staging and callouts by diffing part count, vehicle count and attached radial decouplers](../../../howto/vehicles-and-parts/craft-tracking/track-and-classify-craft/track-and-classify-craft-3.md) - how-to article demonstrating this feature

[^kpt-readme]: README.md
