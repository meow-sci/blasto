---
type: How-To
title: Infer a reload from universe time running backwards
description: Detect a quickload or revert with no patch by remembering the last universe time seen per mission and resetting that mission when the clock drops by more than a tolerance
category: persistence
group: saves
topic: detect-save-load
approach: 5
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/mission-clock-and-liftoff-detection]
techniques: [starmap-lifecycle, json-config]
hooks: [starmap StarMapAfterOnFrame, starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, frame:after-on-frame, frame:draw-ui]
game_types: [KSA.Universe, KSA.Vehicle, KSA.UniverseTime]
tags: [saves, load, quickload, revert, universe-clock, mission-clock]
---

Outcome: a per-mission clock (T+ 00:02:31) that restarts correctly after a quickload or revert, with no Harmony patch at all: the mod notices that universe time went backwards for a mission it already knew. Liftoff epochs are persisted in the mod's config so T+ also survives a restart. Demonstrated by [Mission clock and liftoff detection](../../../../mods/ksa-public-telemetry/features/mission-clock-and-liftoff-detection.md).

## Background: how the game does it

A save load replaces the world and puts the universe clock at the save's `GameTime` (`Universe.DeserializeSave` resets the last and next sim steps to a zero-length step at that time: [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2478)). Vehicles are rebuilt from the save, but their `Vehicle.LaunchGameTime` is the same value the saved vehicle had, and `Vehicle.Split` copies it to the new vehicle together with `HasLaunched`, so every stage of one rocket shares a launch time ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1431-L1436)). That is a stable, load-proof key. What changes across a load to an earlier moment is that `Universe.GetElapsedSeconds()` jumps backwards.

## Lifecycle and timing

The sampler runs in `[StarMapAfterOnFrame]` (`frame:after-on-frame`): by then `PrepareFrame` has applied the previous physics step, so the vehicle state read is consistent, and `Universe.GetElapsedSeconds()` is the time of that state. The panel draws the result in the next frame's UI pass (`frame:draw-ui`). The config is loaded once in `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`) ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L39-L45)). See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Key state on something a load preserves.** The mission is keyed on `LaunchGameTime.Nanoseconds` (an `Int128`), not on the `Vehicle` object, so a loaded save finds the same mission:

   ```csharp
   // Telemetry/TelemetrySampler.cs L100-L106
   double now = Universe.GetElapsedSeconds();
   UniverseTime launchTime = vehicle.LaunchGameTime;

   snapshot.HasLaunched = vehicle.HasLaunched;
   snapshot.LaunchUniverseSeconds = launchTime.Seconds();

   Mission mission = _missions.Resolve(launchTime.Nanoseconds, now);
   ```

2. **Remember the last universe time seen and compare.** `Resolve` creates a mission the first time a key is seen; for a known mission, a time more than a second earlier than the last one seen means a quickload or revert, so the mission's clock and events are reset:

   ```csharp
   // Telemetry/MissionRegistry.cs L23, L34-L58
   private const double RevertToleranceSeconds = 1.0;
   // ...
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

   The tolerance has to be larger than any legitimate jitter of the clock source; one second works because `GetElapsedSeconds()` only advances.

3. **Make the clock itself robust.** If a clock sees time earlier than its liftoff it clamps the epoch rather than going negative:

   ```csharp
   // Telemetry/MissionClock.cs L98-L103
   if (nowUniverseSeconds < _liftoffUniverseSeconds)
   {
       _liftoffUniverseSeconds = nowUniverseSeconds;
   }

   ElapsedSeconds = nowUniverseSeconds - _liftoffUniverseSeconds;
   ```

4. **Persist what a reset must not lose.** On the liftoff frame the registry records the epoch and the launch site; the draw loop notices `EpochsChanged` and writes both into the JSON config. At startup `ApplyEpochs` feeds them back, and `Resolve` seeds a stored liftoff into a fresh clock when it is not in the future:

   ```csharp
   // Telemetry/MissionRegistry.cs L63-L75
   public void RecordLiftoff(Int128 launchKey, double liftoffUniverseSeconds, LaunchOrigin origin)
   {
       if (_epochs.TryGetValue(launchKey, out double known)
           && Math.Abs(known - liftoffUniverseSeconds) < 1e-6)
       {
           return;
       }

       _epochs[launchKey] = liftoffUniverseSeconds;
       _origins[launchKey] = origin;
       EpochsChanged = true;
       EvictEpochs();
   }
   ```

   ```csharp
   // TelemetryOverlayMod.cs L133-L138
   if (TelemetrySampler.Missions.EpochsChanged)
   {
       _config.MissionEpochs = TelemetrySampler.Missions.CaptureEpochs();
       _config.MissionOrigins = TelemetrySampler.Missions.CaptureOrigins();
       ConfigStore.MarkDirty();
   }
   ```

   The seeding line in `Resolve` is the `if (!mission.Clock.HasLiftoff && _epochs.TryGetValue(...) && epoch <= nowUniverseSeconds)` block of step 2. The persisted file and its write policy are covered in [Persist mod settings as JSON with System.Text.Json](../../settings-formats/persist-mod-settings-as-json/overview.md).

## Complete example

| File | Role |
|---|---|
| [Telemetry/MissionRegistry.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionRegistry.cs#L21-L140) | Mission registry: keying, revert detection, epoch persistence |
| [Telemetry/MissionClock.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionClock.cs) | Liftoff detection, `SeedLiftoff`, elapsed time |
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L95-L135) | The per-frame wiring |
| [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L39-L45) | Loading persisted epochs |

## Variations and gotchas

- **Only a backwards load is detected.** A quickload to a later moment than the last one seen, or to within the tolerance, looks like normal progress. This is why KSArmory and KSAGolf prefer an explicit hook ([Latch Program.OnGameLoaded and forget the old world in the next frame step](detect-save-load-1.md)) for state that must reset on every load.
- **A load to the same save at the same time** (an immediate quickload) produces no signal.
- **No hook, no patch risk.** Nothing can break on a game update except `Vehicle.LaunchGameTime` itself, and a lost patch cannot silently stop detection.
- **Do not use it for engine objects.** Because the mission key is data, not an object, this approach fits a clock or counters; state that holds `Vehicle` references must be reset by a hook.

## See also

- [Latch Program.OnGameLoaded and forget the old world in the next frame step](detect-save-load-1.md), [Skip detection: rebuild a derived registry from the live world every frame](detect-save-load-6.md), [overview](./overview.md)
- [Mission clock and liftoff detection](../../../../mods/ksa-public-telemetry/features/mission-clock-and-liftoff-detection.md)
- [Persist mod settings as JSON with System.Text.Json](../../settings-formats/persist-mod-settings-as-json/overview.md)
