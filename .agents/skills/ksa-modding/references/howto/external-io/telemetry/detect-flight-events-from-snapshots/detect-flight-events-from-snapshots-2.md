---
type: How-To
title: Debounced heuristics on burning-engine and part counts for webcast callouts
description: Infer liftoff, max q, stage separation, MECO, ignition and landing from a few counters in consecutive samples, with debounces and per-vehicle baselines so a flickering count never repeats a callout
category: external-io
group: telemetry
topic: detect-flight-events-from-snapshots
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/flight-event-detection-and-callouts]
techniques: [edge-detection, starmap-lifecycle]
hooks: [starmap StarMapAfterOnFrame, starmap StarMapAfterGui]
lifecycle: [frame:after-on-frame, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.Universe, KSA.PartTree, KSA.Decoupler]
tags: [events, heuristics, debounce, staging, max-q, meco, broadcast]
---

Outcome: during flight, banners such as LIFTOFF, MAX Q, MECO, STAGE SEP, BOOSTER SEP, SES-1, ENGINE CUTOFF, LANDING BURN and LANDING appear and the same events become dots on a timeline. The game raises none of these events, so the mod infers them by diffing a few counters between consecutive samples. The README is upfront that this is not totally reliable. Requires StarMap. Demonstrated by [Flight event detection and callouts](../../../../mods/ksa-public-telemetry/features/flight-event-detection-and-callouts.md) in KSA-Public-Telemetry.

## Background: how the game does it

Nothing in KSA says "a stage separated". What changes is observable state: the number of burning engines, the part count of the controlled vehicle's `PartTree` ([current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L134-L134)), the number of vehicles launched together (staging splits one vehicle into two), and the state of `Decoupler` modules reached through `parts.Modules.Get<Decoupler>()`. Each of these is a main-thread read of state applied earlier in the frame by `Universe.ApplyVehicleSolvers` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952)).

## Lifecycle and timing

- Detection runs in `frame:after-on-frame` as part of `TelemetrySampler.Sample`, on the vehicle state applied in this frame's `PrepareFrame`. Whatever split, decoupling or destruction the game performed earlier in the frame is already reflected in that frame's part count and vehicle list.
- Banners age by the UI `dt` and draw in `frame:draw-ui` on the foreground draw list.
- Detection is per sample, so at high time warp an event can be stamped up to one frame late in universe time, which at warp can be many seconds.
- The after-frame hook also fires on frames where `PrepareFrame` returned `Exit`; see [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Feed the detector plain counters.** The sampler fills `BurningEngineCount`, `PartCount`, `AttachedRadialDecouplers`, `DynamicPressure`, `HasSurfaceContact`, `VerticalSpeed` and `RadarAltitude` into the snapshot (see [approach 2 of the snapshot topic](../export-game-state-to-other-threads/overview.md)), and calls the event log once per sample:

   ```csharp
   // Telemetry/TelemetrySampler.cs L131-L137
           mission.Events.Update(
               snapshot,
               mission.Clock.LiftoffThisFrame,
               CountMissionVehicles(launchTime.Nanoseconds),
               _signal.RegainedThisFrame);
           SamplePlannedBurns(vehicle, mission, now);
           snapshot.Events = mission.Events;
   ```

2. **Count mission vehicles, cached.** Staging produces a second vehicle that shares the launch time of the first. Counting vehicles in `Universe.CurrentSystem.All` with the same `LaunchGameTime.Nanoseconds` detects "a new piece appeared"; cache the count on `All.Count` and the key so the scan does not run every frame.

   ```csharp
   // Telemetry/TelemetrySampler.cs L243-L279
       private static int _vehicleCountAllCount = -1;
       private static Int128 _vehicleCountKey;
       private static int _vehicleCount;
       private static int CountMissionVehicles(Int128 launchKey)
       {
           CelestialSystem? system = Universe.CurrentSystem;

           if (system is null)
           {
               _vehicleCountAllCount = -1;
               return 0;
           }

           LookupCollection<Astronomical> all = system.All;

           if (all.Count == _vehicleCountAllCount && launchKey == _vehicleCountKey)
           {
               return _vehicleCount;
           }

           int count = 0;

           for (int i = 0; i < all.Count; i++)
           {
               if (all.GetIndex(i) is Vehicle other
                   && !other.IsDisposed
                   && other.LaunchGameTime.Nanoseconds == launchKey)
               {
                   count++;
               }
           }

           _vehicleCountAllCount = all.Count;
           _vehicleCountKey = launchKey;
           _vehicleCount = count;
           return count;
       }
   ```

3. **Count attached radial decouplers** to tell a booster separation from a stage separation: a decoupler whose connector has a connection and a surface flag is attached radially.

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

4. **Reset baselines instead of diffing across a change.** When the tracked vehicle changes, or signal is regained after a freeze, the previous-frame counters are re-seeded from the current frame, so switching vehicles does not fire spurious events. Previous counters are updated at the end of each sample.

   ```csharp
   // Telemetry/MissionEventLog.cs L78-L133
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

           if (burning > 0 && _previousBurning == 0)
           {
               _burnStartedAt = now;
           }

           double endingRunLength = burning > 0 || _burnStartedAt == double.NegativeInfinity
               ? 0.0
               : now - _burnStartedAt;

           if (liftoffThisFrame && !_liftoffFired)
           {
               _liftoffFired = true;
               Record(new MissionEvent(MissionEventKind.Liftoff, 0.0));
           }

           DetectMaxQ(snapshot, flying, now);
           DetectStaging(parts < _previousPartCount, releasedRadial, flying, now);
           DetectIgnition(snapshot, burning, flying, now);
           DetectCutoff(snapshot, burning, flying, now, endingRunLength);
           DetectLanding(snapshot, flying, now);

           _previousBurning = burning;
           _previousPartCount = parts;
           _previousContact = snapshot.HasSurfaceContact;
       }
   ```

5. **Write each detector as an edge plus a debounce.** Cutoff means the burning count fell to zero after a burn of at least one second, at most once per two seconds; the first is MECO, the second SECO, later ones ENGINE CUTOFF:

   ```csharp
   // Telemetry/MissionEventLog.cs L227-L252
       private void DetectCutoff(
           TelemetrySnapshot snapshot, int burning, bool flying, double now, double endingRunLength)
       {
           if (!flying || burning > 0 || _previousBurning == 0)
           {
               return;
           }

           if (endingRunLength < MinBurnBeforeCutoff || now - _lastCutoffTime < CutoffDebounce)
           {
               return;
           }

           _lastCutoffTime = now;
           _cutoffVehicle = snapshot.VehicleName;

           MissionEventKind kind = _cutoffCount switch
           {
               0 => MissionEventKind.Meco,
               1 => MissionEventKind.Seco,
               _ => MissionEventKind.EngineCutoff,
           };

           _cutoffCount++;
           Record(new MissionEvent(kind, now));
       }
   ```

   Staging fires when the vehicle count grows or the part count drops, debounced to one per two seconds, and is called BOOSTER SEP if the radial decoupler count dropped in the same frame:

   ```csharp
   // Telemetry/MissionEventLog.cs L158-L174
       private void DetectStaging(bool separated, bool radial, bool flying, double now)
       {
           if (!flying || !separated)
           {
               return;
           }

           if (now - _lastStageSepTime < StageSepDebounce)
           {
               return;
           }

           _lastStageSepTime = now;

           Record(new MissionEvent(
               radial ? MissionEventKind.BoosterSep : MissionEventKind.StageSep, now));
       }
   ```

   Max Q tracks the peak dynamic pressure and fires once, stamped at the peak time, when q falls below 95 percent of a peak of at least 1 kPa ([Telemetry/MissionEventLog.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionEventLog.cs#L135-L156)). Ignition rises from zero after at least one cutoff; on a different vehicle than the one that cut off (the player switched to the upper stage) the first is SES-1, descending below 10 km radar altitude it is LANDING BURN, otherwise ENGINE IGNITION ([Telemetry/MissionEventLog.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionEventLog.cs#L176-L204)). Landing is a new surface contact with an absolute vertical speed under 3 m/s ([Telemetry/MissionEventLog.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionEventLog.cs#L206-L225)).

6. **Show events without re-showing old ones.** The banner panel tracks how many events it has seen and the log's `Generation`, skips kinds the player muted, keeps at most three on screen, and draws them from the next draw hook ([Rendering/NotificationPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/NotificationPanel.cs#L48-L99)). Muting only hides the banner; the timeline still shows the event.

## Complete example

| File | Role |
|---|---|
| [Telemetry/MissionEventLog.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionEventLog.cs) | Edge detectors, debounces, naming |
| [Telemetry/MissionEvent.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionEvent.cs) | Event kinds, labels, explainers |
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs) | Counters, vehicle count, decoupler count |
| [Rendering/NotificationPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/NotificationPanel.cs) | Banner queue and drawing |

## Variations and gotchas

- **Heuristic by design.** Counting heuristics can miss an event or misname one (the README calls the feature not totally reliable); the debounces defend against repeats, not against misses.
- **Debounce constants matter.** The two-second windows (cutoff, staging, ignition, landing) trade missed rapid events for stability; tune them per use.
- **`Reset()` on mission change.** The log clears its state and bumps `Generation` so UI consumers that track the event list re-read it.
- **Predictions are separate.** Planned-burn predictions are stored in a `Predicted` list (`SetPredictions`) and do not feed the detectors.
- **If you also serve other threads,** diff the full snapshot as in [approach 1](detect-flight-events-from-snapshots-1.md) instead of counters.

## See also

- [Overview](overview.md)
- [Approach 1: EventDiffer over immutable snapshots](detect-flight-events-from-snapshots-1.md)
- [Flight event detection and callouts](../../../../mods/ksa-public-telemetry/features/flight-event-detection-and-callouts.md)
- [Export live game state to background threads](../export-game-state-to-other-threads/overview.md)
- [Compute mission elapsed time and detect liftoff](../../../flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/overview.md)
