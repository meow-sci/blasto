---
type: How-To
title: Detect staging and callouts by diffing part count, vehicle count and attached radial decouplers
description: Infer liftoff, stage and booster separation, cutoff and max Q from counters sampled after every frame and compared with the previous sample
category: vehicles-and-parts
group: craft-tracking
topic: track-and-classify-craft
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/flight-event-detection-and-callouts]
techniques: [state-diffing]
hooks: [starmap StarMapAfterOnFrame, starmap StarMapAfterGui]
lifecycle: [frame:after-on-frame, frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.Universe
  - KSA.CelestialSystem
  - KSA.StructuralLoad
  - KSA.PhysicsBubble
  - KSA.Part
  - KSA.Program
  - KSA.Decoupler
tags: [staging, events, state-diffing, decoupler, callouts]
---
Outcome: banners and timeline marks for liftoff, stage separation, booster separation and engine cutoffs, produced by diffing a few counters between consecutive samples. The game raises none of these events, so the mod infers them; its own README calls the detector
"not totally reliable".

Prerequisite: StarMap. Demonstrated by [ksa-public-telemetry/flight-event-detection-and-callouts](../../../../mods/ksa-public-telemetry/features/flight-event-detection-and-callouts.md).

## Background: how the game does it

Staging is `Vehicle.Split`: the vehicle keeps its remaining parts and a new `Vehicle` is created with the separated root, so after a stage separation the original's part count drops and the number of vehicles in `Universe.CurrentSystem.All` grows. Vehicles created from one launch share the same
`LaunchGameTime`, so counting those identifies the mission's pieces ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L164)[^ksa-vehicle]). A radial booster hangs on a surface connector: `Part.Connector.Flag.ToSurface` / `FromSurface` with a non-null `Connection`, and a `Decoupler` module has a `Connector`
[current/decomp/KSA/Decoupler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Decoupler.cs#L65)[^ksa-decoupler]. The distinction between a stage and a booster separation is how many radial decouplers are still attached.

## Lifecycle and timing

Detection runs in `frame:after-on-frame` as part of `TelemetrySampler.Sample`, on the vehicle state applied in this frame's `PrepareFrame`. Whatever split, decoupling or destruction happened earlier in the frame (solver apply, input, UI) is already reflected in the part count and vehicle list, so the detector
does not need to know which phase did it. Banners age and draw in `frame:draw-ui`. Detection is per sample, so at high time warp an event can be stamped up to one frame late in universe time ([game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).

## Steps

1. **Count the vehicles that share the launch key, cached on the collection size.** Skip disposed ones:

```csharp
// Telemetry/TelemetrySampler.cs L246-L279
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

2. **Count radial decouplers still attached** (surface connector flag set and a live connection):

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

3. **Diff against the previous sample, and rebaseline on a change of tracked vehicle** so switching craft does not fire events:

```csharp
// Telemetry/MissionEventLog.cs L78-L107,L124-L128
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

4. **Interpret the edges.** Stage or booster separation fires when the mission vehicle count grows or the part count drops, debounced to one per 2 s, and is a booster separation if the attached radial decoupler count dropped in the same frame. Cutoff is the burning count falling to zero after a burn of at least 1 s (first is MECO, second SECO). Ignition is
   the burning count rising from zero after a cutoff; Max Q fires at the dynamic-pressure peak once q falls below 95% of a peak of at least 1 kPa. Landing is new surface contact below 3 m/s.

## Complete example

- [Telemetry/MissionEventLog.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionEventLog.cs#L1-L200)[^ksa-public-telemetry-missioneventlog] - edge detectors, debounces and naming.
- [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L95-L138)[^ksa-public-telemetry-telemetrysampler] - how counts feed `Update`.

## Variations and gotchas

- **Heuristic.** Any part loss (breakage, a jettisoned fairing, a decoupled payload) reads as a stage separation; propellant starvation reads as MECO; a throttle-to-zero and back is a cutoff plus relight.
- **Counting by launch key** treats every vehicle spawned with the same `LaunchGameTime` as one mission, including debris from that rocket.
- **Only the controlled vehicle is watched**; switching rebaselines.
- **No SOI, orbit-insertion or parachute detection** exists in this detector.
- **Inferred, not exact.** The detector uses only counters. If you can hook the operation itself (for example a Harmony postfix on `Vehicle.Split`, see [Stage a vehicle from code](../../staging-and-engines/stage-a-vehicle-from-code/overview.md)) you get exact edges instead of inferred ones.

## See also

- [Overview](overview.md); [Article 4](track-and-classify-craft-4.md)
- [ksa-public-telemetry/flight-event-detection-and-callouts](../../../../mods/ksa-public-telemetry/features/flight-event-detection-and-callouts.md)
- [Stage a vehicle from code](../../staging-and-engines/stage-a-vehicle-from-code/overview.md)

[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-decoupler]: current/decomp/KSA/Decoupler.cs (decompiled, build 2026.10.10.5554)
[^ksa-public-telemetry-missioneventlog]: KSA-Public-Telemetry: Telemetry/MissionEventLog.cs
[^ksa-public-telemetry-telemetrysampler]: KSA-Public-Telemetry: Telemetry/TelemetrySampler.cs
