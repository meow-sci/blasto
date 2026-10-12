---
type: Mod Feature
title: Mission clock and liftoff detection
description: A T-minus/T-plus clock that keys each mission on the vehicle's LaunchGameTime (shared by every stage split from it), detects the real liftoff moment from contact, power and climb rate, supports a hotkey countdown, and persists liftoff epochs and launch sites in the config
mod: ksa-public-telemetry
category: flight-and-orbits
techniques: [starmap-lifecycle, json-config]
hooks: [starmap StarMapAfterOnFrame, starmap StarMapAfterGui, harmony-postfix KSA.Program.DrawFps]
lifecycle: [frame:after-on-frame, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.UniverseTime, KSA.Universe, KSA.CelestialSystem, KSA.LookupCollection]
tags: [mission-clock, met, liftoff, countdown, universe-time, staging]
---

The centre of the bar shows `T+ 00:02:31` under the mission name (or the vehicle name). The clock
starts at the moment the vehicle actually leaves the pad, not when it was spawned, keeps counting for
boosters and upper stages after separation, survives quicksave/quickload, and can show a `T-`
countdown that the player starts with a hotkey before launch.

## How it works

1. **Mission key.** The game sets `Vehicle.LaunchGameTime = Universe.GetElapsedTime()` in the vehicle
   constructor ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1431-L1436)) and `Vehicle.Split` copies it to the new vehicle
   together with `HasLaunched` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1527), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1666-L1667)).[^ksa-vehicle]
   So every piece of one rocket carries the same value. The mod uses
   `LaunchGameTime.Nanoseconds` (an `Int128`, [current/decomp/KSA/UniverseTime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseTime.cs#L28)) as the key of a `Mission`
   that owns a `MissionClock` and a `MissionEventLog`; `MissionRegistry.Resolve` keeps up to eight
   missions with LRU eviction ([Telemetry/MissionRegistry.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionRegistry.cs#L6-L58),
   [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L100-L129)).[^ksa-universetime]
2. **Liftoff detection.** `MissionClock.Update` remembers that the vehicle was ever in surface contact,
   then declares liftoff on the first frame with no contact, at least one burning engine and vertical
   speed above 0.5 m/s; the epoch is the current `Universe.GetElapsedSeconds()`
   ([Telemetry/MissionClock.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionClock.cs#L56-L104)).[^ksa-universe] A vehicle that was never seen grounded but
   has `HasLaunched` (for example one loaded already in flight) falls back to `LaunchGameTime` as the
   epoch and is flagged `EpochInferred`.
3. **Countdown.** The numpad `*` hotkey (or the settings button) calls
   `StartCountdown(seconds)`, which stores `now + seconds` as `CountdownZero` until liftoff. Before
   liftoff the elapsed value is `now - CountdownZero` (negative, shown as `T-`)
   ([Telemetry/MissionClock.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionClock.cs#L20-L35), [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L19-L24),
   [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L191-L201)).
4. **Revert handling.** If universe time jumps backwards by more than a second for a known mission
   (quickload, revert), the mission's clock and events are reset; a clock that sees time earlier than
   its liftoff clamps the epoch ([Telemetry/MissionRegistry.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionRegistry.cs#L34-L58),
   [Telemetry/MissionClock.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionClock.cs#L98-L103)).
5. **Persistence.** On the liftoff frame the registry records the epoch and the launch site (parent
   body id plus CCF position) under the mission key; `DrawEverything` notices `EpochsChanged` and writes
   `MissionEpochs` and `MissionOrigins` into the JSON config. At startup they are fed back with
   `ApplyEpochs`, and `Resolve` seeds a stored liftoff into a fresh clock, so after a reload the T+ is
   still measured from the original liftoff ([Telemetry/MissionRegistry.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionRegistry.cs#L63-L139),
   [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L133-L138), [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L44-L45)). The keys are
   `Int128` values written as invariant-culture strings; at most 32 are kept, oldest key first out.
6. **Drawing.** `MissionClockPanel` draws a `T+`/`T-` prefix and the time with tabular digits, dimmed
   while frozen ([Rendering/MissionClockPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/MissionClockPanel.cs#L37-L84),
   [Rendering/Format.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Format.cs#L62-L106)).

## Lifecycle and timing

- Clock state advances in `frame:after-on-frame` (`[StarMapAfterOnFrame]`), from the vehicle state that
  this frame's `PrepareFrame` applied; the next physics step is already running on the workers by then,
  on its own copies. The panel draws the result in `frame:draw-ui` the next frame.
- `Universe.GetElapsedSeconds()` is simulation time, so the clock follows time warp and pauses with
  the game, like a real mission elapsed time ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2393-L2402)). It returns
  `_lastSimStep.NextTime`, which `ApplyVehicleSolvers` advances right after applying the vehicle results
  ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1947-L1948)), so in the after-frame hook "now" is the time of the state being read,
  not the target time of the step in flight.
- The countdown hotkey is polled in the UI hook (`ImGui.IsKeyPressed`), but it only writes a number;
  the next after-frame sample picks it up.

## Key code

| Path | Role |
|---|---|
| [Telemetry/MissionClock.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionClock.cs#L56-L113) | Liftoff detection, elapsed and mission-time conversion |
| [Telemetry/MissionRegistry.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionRegistry.cs#L34-L139) | Mission keyed by launch time, revert detection, epoch persistence |
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L100-L137) | Wiring into the per-frame sample |
| [Rendering/MissionClockPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/MissionClockPanel.cs#L37-L84) | Clock drawing |

## Snippets

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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Vehicle.LaunchGameTime` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L164), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1436) | Mission key (spawn time, copied on split) |
| `Vehicle.Split` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1527), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1666-L1667) | Why separated stages share the key |
| `Vehicle.HasLaunched`, `MarkLaunched` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L391), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L905-L908) | Fallback epoch for vehicles never seen grounded |
| `UniverseTime.Nanoseconds`, `Seconds()` | [current/decomp/KSA/UniverseTime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseTime.cs#L28), [current/decomp/KSA/UniverseTime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseTime.cs#L95) | Key and seconds conversion |
| `Universe.GetElapsedSeconds` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2393) | Simulation clock |
| `Program.UpdateGauges` mission time | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2782) | The stock clock is `now - LaunchGameTime`, i.e. time since spawn |

## Pitfalls and version notes

- **The stock MET is time since spawn.** The game's own elapsed-time gauge subtracts `LaunchGameTime`,
  which is set when the vehicle object is created, so it starts ticking on the pad. That is why the mod
  detects liftoff itself.
- **Detection needs engines.** A vehicle lifted by something other than a burning engine (a crane, a
  physics bounce) is not considered launched until an engine burns.
- **Config grows with missions.** Epochs are trimmed to 32, by smallest key (oldest spawn time), not by
  last use.
- **Keys are only unique per spawn time.** Two vehicles created at the same universe time share a
  mission. Besides splits, that happens when they spawn while the simulation is paused: each step
  spans `dtPlayer` times the simulation speed ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2615-L2627)), so the
  universe time does not move at speed zero.

## See also

- [In-flight event detection and callouts](flight-event-detection-and-callouts.md)
- [Mission timeline with planned burns](mission-timeline-with-planned-burns.md)
- [JSON config in the KSA documents folder](json-config-in-documents.md)
- [Rebindable numpad hotkeys](rebindable-numpad-hotkeys.md)
- [Run a T-/T+ mission clock keyed on LaunchGameTime with contact-loss liftoff detection](../../../howto/flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/compute-mission-elapsed-time-and-detect-liftoff-1.md) - how-to article demonstrating this feature
- [Infer a reload from universe time running backwards](../../../howto/persistence/saves/detect-save-load/detect-save-load-5.md) - how-to article demonstrating this feature

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-universetime]: KSA.UniverseTime (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
