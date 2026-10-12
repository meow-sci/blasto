---
type: How-To
title: Run a T-/T+ mission clock keyed on LaunchGameTime with contact-loss liftoff detection
description: Start the clock when the vehicle actually leaves the ground, key it on LaunchGameTime so separated stages share it, detect reverts and persist epochs across reloads
category: flight-and-orbits
group: flight-state
topic: compute-mission-elapsed-time-and-detect-liftoff
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/mission-clock-and-liftoff-detection]
techniques: [starmap-lifecycle, json-config]
hooks: [starmap StarMapAfterOnFrame]
lifecycle: [frame:after-on-frame, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.UniverseTime, KSA.Universe, KSA.SituationEx]
tags: [mission-clock, liftoff, launch-game-time, revert, countdown, persistence]
---

Outcome: a `T+ 00:02:31` clock that starts at real liftoff, keeps counting for boosters and upper stages after separation,
survives quicksave and quickload and can show a `T-` countdown. KSA-Public-Telemetry implements it.[Mission clock and liftoff detection](../../../../mods/ksa-public-telemetry/features/mission-clock-and-liftoff-detection.md)
Prerequisite: StarMap.

## Background: how the game does it

`Vehicle.LaunchGameTime` is initialised to `Universe.GetElapsedTime()` when the `Vehicle` object is created, not at
liftoff, and `Vehicle.Split` copies it and `HasLaunched` to the new vehicle, so every piece of a rocket shares
one value.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1431-L1437) [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1660-L1668) It is a `UniverseTime` whose `Nanoseconds` is an `Int128`,
usable as a dictionary key.[^ksa-universetime] [current/decomp/KSA/UniverseTime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseTime.cs#L28) Contact is `Vehicle.Situation.HasAnyContact()` (a
`SituationEx` extension); `Universe.GetElapsedSeconds()` is simulation time, so the clock follows warp and pause.[current/decomp/KSA/SituationEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SituationEx.cs#L18)

## Lifecycle and timing

The clock advances in `frame:after-on-frame` (`[StarMapAfterOnFrame]`) from the vehicle state that this frame's
`PrepareFrame` applied; the next physics step is already running on worker copies, so main-thread reads need no lock.
In that hook "now" (`Universe.GetElapsedSeconds()`) is the time of the state being read, not the target time of the step
in flight. The panel draws the result in `frame:draw-ui` the next frame. The countdown hotkey is polled in the UI hook,
only writes a number, and the next after-frame sample picks it up. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Key a mission on `LaunchGameTime.Nanoseconds`.** One `Mission` per key owns a `MissionClock` and an event log; the
   registry resolves it each frame, resets it when universe time jumps back by more than a second (quickload, revert),
   and seeds a stored liftoff into a fresh clock:

```csharp
// Telemetry/MissionRegistry.cs L34-L58
public Mission Resolve(Int128 launchKey, double nowUniverseSeconds)
{
    if (!_missions.TryGetValue(launchKey, out Mission? mission))
    {
        mission = new Mission(launchKey);
        _missions[launchKey] = mission;
        Evict();
    }
    else if (nowUniverseSeconds < mission.LastSeenUniverseSeconds - RevertToleranceSeconds)
    {
        mission.Reset();
    }

    if (!mission.Clock.HasLiftoff
        && _epochs.TryGetValue(launchKey, out double epoch)
        && epoch <= nowUniverseSeconds)
    {
        mission.Clock.SeedLiftoff(epoch);
    }

    mission.LastSeenUniverseSeconds = nowUniverseSeconds;
    mission.LastTouched = ++_clock;

    return mission;
}
```

2. **Detect liftoff from observations**, not from `HasLaunched` alone: remember that the vehicle was ever in surface contact,
   then declare liftoff on the first frame with no contact, a burning engine and a climb rate above 0.5 m/s. A vehicle never
   seen grounded but with `HasLaunched` (loaded in flight) falls back to `LaunchGameTime` and is flagged inferred. Elapsed
   time is `now - liftoff`, clamped if time ever reads earlier than the liftoff:

```csharp
// Telemetry/MissionClock.cs L66-L104
    if (hasSurfaceContact)
    {
        _sawGrounded = true;
    }

    if (!_hasLiftoff)
    {
        bool observedLiftoff = _sawGrounded
            && !hasSurfaceContact
            && isUnderPower
            && verticalSpeed > MinAscentRate;

        if (observedLiftoff)
        {
            _liftoffUniverseSeconds = nowUniverseSeconds;
            _hasLiftoff = true;
            EpochInferred = false;
            LiftoffThisFrame = true;
        }
        else if (!_sawGrounded && hasLaunched)
        {
            _liftoffUniverseSeconds = launchGameSeconds;
            _hasLiftoff = true;
            EpochInferred = true;
        }
        else
        {
            ElapsedSeconds = _hasCountdown ? nowUniverseSeconds - _countdownZero : 0.0;
            return;
        }
    }

    if (nowUniverseSeconds < _liftoffUniverseSeconds)
    {
        _liftoffUniverseSeconds = nowUniverseSeconds;
    }

    ElapsedSeconds = nowUniverseSeconds - _liftoffUniverseSeconds;
}
```

3. **Countdown.** A hotkey stores `now + seconds` as the countdown zero; before liftoff the elapsed value is
   `now - countdownZero`, negative and shown as `T-` (see [Telemetry/MissionClock.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionClock.cs#L20-L35)).
4. **Persist epochs.** When the registry records a liftoff, write the epoch (and the launch site) into your JSON config keyed
   by the `Int128` as an invariant-culture string; feed them back at startup so T+ is still measured from the original
   liftoff after a reload ([Telemetry/MissionRegistry.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionRegistry.cs#L63-L139)). The mod keeps at most 32 keys.
5. **Draw** `T+`/`T-` with tabular digits and dim it while frozen ([Rendering/MissionClockPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/MissionClockPanel.cs#L37-L84)).

## Complete example

| File | Role |
|---|---|
| [Telemetry/MissionClock.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionClock.cs#L56-L113) | liftoff detection, elapsed and mission-time conversion |
| [Telemetry/MissionRegistry.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionRegistry.cs#L34-L139) | mission keyed by launch time, revert detection, epoch persistence |
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L100-L137) | wiring into the per-frame sample |
| [Rendering/MissionClockPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/MissionClockPanel.cs#L37-L84) | clock drawing |

## Variations and gotchas

- **`HasLaunched` is not liftoff.** It is a flag the game sets (for example in `StartCrewMissions` and `MarkLaunched`), not a measurement of leaving the pad;
  the contact-loss rule is what makes the clock start at the moment the vehicle leaves it.
- **A vehicle loaded in flight** has no grounded observation; the fallback epoch is `LaunchGameTime`, which is vehicle
  creation, so mark such clocks as inferred.
- **Reverts and quickloads** make time go backwards; without the revert check old mission events remain.
- Keys are `Int128`; JSON has no 128-bit integer, so store strings.
- Sampling happens after the frame: an `Exit` frame is sampled too, but nothing is drawn until the next UI frame.

## See also

- [Overview](overview.md); [applied sim step](compute-mission-elapsed-time-and-detect-liftoff-2.md); [timed playback](compute-mission-elapsed-time-and-detect-liftoff-3.md)
- [Mission clock and liftoff detection](../../../../mods/ksa-public-telemetry/features/mission-clock-and-liftoff-detection.md)
- [Read a vehicle's position, velocity, attitude, orbit and flight instruments](../read-vehicle-flight-state/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-universetime]: KSA.UniverseTime (decompiled, build 2026.10.10.5554)
