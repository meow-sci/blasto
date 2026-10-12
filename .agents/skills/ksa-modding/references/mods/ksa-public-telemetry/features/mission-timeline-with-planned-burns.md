---
type: Mod Feature
title: Mission timeline with planned burns
description: A scrolling mission timeline centred on now that shows detected events behind the marker and the flight computer's planned maneuver burns ahead of it, read from Vehicle.FlightComputer.BurnPlan each frame and converted to mission time
mod: ksa-public-telemetry
category: flight-and-orbits
techniques: [imgui-drawlist, starmap-lifecycle]
hooks: [starmap StarMapAfterOnFrame, starmap StarMapAfterGui, harmony-postfix KSA.Program.DrawFps]
lifecycle: [frame:after-on-frame, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.FlightComputer, KSA.BurnPlan, KSA.Burn, KSA.BurnTarget, KSA.UniverseTime]
tags: [timeline, maneuvers, burn-plan, flight-computer, imgui, drawlist]
---

Under the clock runs a thin horizontal track with a "now" dot in the middle. Recorded events
(LIFTOFF, MAX Q, MECO, ...) slide off to the left as time passes, and every maneuver node the player has
planned shows up on the right as a dimmed "BURN" label that approaches the marker.[^kpt-readme] The
window is 240 s wide by default and adjustable from 60 to 1800 s.

## How it works

1. **Read the plan.** Each sample, `SamplePlannedBurns` reads `vehicle.FlightComputer?.BurnPlan`; if
   there is a plan with `HasActiveBurns`, it walks `BurnCount` entries with `TryGetBurn(i, out Burn?)`,
   skips burns without `HasDeltaV`, and converts each `burn.Time.Seconds()` to mission time with
   `MissionClock.MissionTimeFor` ([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L281-L306)). The plan lives on the
   flight computer ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L163), [current/decomp/KSA/BurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnPlan.cs#L22-L42),
   [current/decomp/KSA/BurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnPlan.cs#L201-L210), [current/decomp/KSA/Burn.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Burn.cs#L36), [current/decomp/KSA/Burn.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Burn.cs#L151)).[^ksa-burnplan]
2. **Mission-time conversion.** `MissionTimeFor` subtracts the liftoff epoch, or the countdown zero
   before liftoff, or "now" when neither exists, so pre-launch burns sit relative to the present
   ([Telemetry/MissionClock.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionClock.cs#L106-L113)).
3. **Predictions are replaced each frame.** `MissionEventLog.SetPredictions` clears and refills a
   separate `Predicted` list with `PlannedBurn` events flagged `IsPrediction`, so edits and deletions in
   the maneuver planner show up immediately; `DeservesCallout` is false for predictions, so they never
   produce a banner ([Telemetry/MissionEventLog.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionEventLog.cs#L260-L271), [Telemetry/MissionEvent.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionEvent.cs#L59)).
4. **Draw.** `TimelinePanel` collects recorded and predicted events within half a window of now, sorts
   them by time, maps each to `x = left + width * ((t - now + half) / (2 * half))`, and draws a faded
   two-colour track (`Gfx.FadedTrack`, segments of `AddRectFilledMultiColor` fading out at both ends), a
   dot per event, and labels alternating above and below the track with simple collision avoidance:
   two "rows", each with a cursor, and a hairline leader when a label had to shift away from its dot
   ([Rendering/TimelinePanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/TimelinePanel.cs#L38-L188), [Rendering/Gfx.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Gfx.cs#L150-L198)).

## Lifecycle and timing

- **Plan read in `frame:after-on-frame`.** `Vehicle.FlightComputer` is the main-thread copy; the game
  refreshes it from the worker result with `FlightComputer.CopyFrom(updateData.NewFlightComputer)` when
  it applies the vehicle step in `PrepareFrame` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2529-L2531)).[^ksa-vehicle] Reading
  after the frame sees that copy plus anything the player changed in the planner UI this frame.
- **Drawn in `frame:draw-ui`** from the snapshot's `Events` reference on the next frame.
- Event times are in mission seconds (simulation time), so markers move at warp speed under time warp.

## Key code

| Path | Role |
|---|---|
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L281-L306) | Read the burn plan |
| [Telemetry/MissionClock.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionClock.cs#L106-L113) | Universe time to mission time |
| [Telemetry/MissionEventLog.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionEventLog.cs#L260-L271) | Prediction list |
| [Rendering/TimelinePanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/TimelinePanel.cs#L38-L188) | Window, mapping, labels, leaders |

## Snippets

```csharp
// Telemetry/TelemetrySampler.cs L281-L306
    private static void SamplePlannedBurns(Vehicle vehicle, Mission mission, double nowUniverseSeconds)
    {
        _burnTimes.Clear();

        FlightComputer? computer = vehicle.FlightComputer;
        BurnPlan? plan = computer?.BurnPlan;

        if (plan is null || !plan.HasActiveBurns)
        {
            mission.Events.ClearPredictions();
            return;
        }

        int count = plan.BurnCount;
        for (int i = 0; i < count; i++)
        {
            if (!plan.TryGetBurn(i, out Burn? burn) || burn is null || !burn.HasDeltaV)
            {
                continue;
            }

            _burnTimes.Add(mission.Clock.MissionTimeFor(burn.Time.Seconds(), nowUniverseSeconds));
        }

        mission.Events.SetPredictions(CollectionsMarshal.AsSpan(_burnTimes));
    }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Vehicle.FlightComputer`, `Vehicle.BurnPlan` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L468), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L553) | The vehicle's flight computer and its plan |
| `FlightComputer.BurnPlan` | [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L163) | Planned burns |
| `BurnPlan.HasActiveBurns`, `BurnCount`, `TryGetBurn(int, out Burn?)` | [current/decomp/KSA/BurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnPlan.cs#L22-L42), [current/decomp/KSA/BurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnPlan.cs#L201-L210) | Enumerate burns |
| `Burn.Time`, `Burn.HasDeltaV` | [current/decomp/KSA/Burn.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Burn.cs#L36), [current/decomp/KSA/Burn.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Burn.cs#L151) | Burn epoch and empty-node filter |
| `UniverseTime.Seconds()` | [current/decomp/KSA/UniverseTime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseTime.cs#L95) | Epoch in seconds |

## Pitfalls and version notes

- **Only the controlled vehicle's plan** is read; burns planned for a target or another vessel are not
  shown.
- **Node time, not ignition.** `Burn.Time` is the node's impulsive instant: `Burn.ImpulseTime` returns it
  unchanged except for interstellar burns ([current/decomp/KSA/Burn.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Burn.cs#L113-L129)), and the flight computer
  starts the engine half the burn duration earlier (`FlightComputer.TimeBurn` sets
  `IgnitionTime = ImpulsiveInstant - 0.5 * duration` on its `BurnTarget`,
  [current/decomp/KSA/BurnTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnTarget.cs#L8-L10),
  [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1126-L1141)). The BURN marker therefore reaches "now" about
  half a burn after ignition.[^ksa-flightcomputer] [^ksa-burntarget]
- **Labels are all "BURN".** Burns are not numbered or named.
- **Window clipping.** Events outside half the window are dropped entirely, so a burn several orbits
  away does not appear until it is within the window.

## See also

- [In-flight event detection and callouts](flight-event-detection-and-callouts.md)
- [Mission clock and liftoff detection](mission-clock-and-liftoff-detection.md)
- [Broadcast overlay on the ImGui foreground draw list](foreground-drawlist-broadcast-overlay.md)
- [Read planned burns with a TryGetBurn loop and draw them on a mission timeline](../../../howto/flight-and-orbits/autopilot/create-and-read-maneuver-nodes/create-and-read-maneuver-nodes-5.md) - how-to article demonstrating this feature

[^kpt-readme]: README.md
[^ksa-burnplan]: KSA.BurnPlan (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-burntarget]: KSA.BurnTarget (decompiled, build 2026.10.10.5554)
