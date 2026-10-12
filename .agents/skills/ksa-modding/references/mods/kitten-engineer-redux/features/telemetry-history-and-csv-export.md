---
type: Mod Feature
title: Telemetry history and CSV export
description: Samples six flight metrics every 0.25 s of wall-clock player time into fixed 720-entry arrays from the HUD draw, resets on vehicle change, and writes the window to a timestamped CSV under the mod's Documents data folder
mod: kitten-engineer-redux
category: external-io
techniques: [telemetry-sampling, csv-export]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi]
lifecycle: [frame:draw-ui]
game_types: [KSA.Program, KSA.Vehicle, KSA.App, KSA.Constants]
tags: [telemetry, history, sampling, csv, export, ring-buffer, flight]
---

The HUD's History section graphs the last three minutes of altitude, surface speed, TWR, thrust,
acceleration or mass, and can export the buffer as CSV. The buffer is filled from the UI pass itself,
throttled by a clock check, so there is no extra hook, thread or timer.

## How it works

- **Storage.** Six `float[720]` arrays, one per `TelemetryMetric`, plus a count; at a 0.25 s sample
  interval that is three minutes ([TelemetryHistory.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/TelemetryHistory.cs#L22-L43)).[^ker-telemetry]
- **Sampling.** `Record(vehicle, engines, actualTwr)` is called on every HUD frame. It clears the
  buffer when `vehicle.Id` differs from the last recorded vehicle, returns unless at least 0.25 s of
  `Program.GetPlayerTime()` has passed since the last sample, and then writes one value per metric:
  `GetBarometricAltitude()`, `GetSurfaceSpeed()`, the HUD's actual TWR, live nozzle thrust in kN,
  `AccelerationBody.Length() / STANDARD_GRAVITY`, and `TotalMass`
  ([TelemetryHistory.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/TelemetryHistory.cs#L45-L77)).
- **Full buffer.** When all 720 slots are used, every array is shifted left by one with `Array.Copy`
  and the newest sample goes in the last slot. This keeps `GetSamples` a contiguous, oldest-first
  `ReadOnlySpan<float>` that the graph can index directly, at the cost of copying 719 floats (about
  2.9 KB) per array, about 17 KB for all six, per sample.
- **Export.** `ExportCsv()` writes `time_s,altitude_m,surface_speed_mps,twr,thrust_kn,acceleration_g,mass_kg`
  with `time_s` as negative seconds before the newest sample, invariant-culture numbers, to
  `Documents/My Games/Kitten Space Agency/KittenEngineerReduxData/exports/telemetry_<vehicle>_<yyyyMMdd_HHmmss>.csv`;
  the vehicle id is sanitised to letters and digits ([TelemetryHistory.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/TelemetryHistory.cs#L110-L152)).
  The HUD's "Open folder" button starts the OS file browser on that path with
  `Process.Start(UseShellExecute = true)` ([SettingsStore.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SettingsStore.cs#L130-L141)).[^ker-settingsstore]

## Lifecycle and timing

- `Record` runs inside the flight HUD postfix on `Vehicle.OnDrawUi` (`frame:draw-ui`) before the
  window is drawn ([FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L143)), so it samples main-thread state applied
  this frame.[^ker-flighthud]
- `Program.GetPlayerTime()` returns `_lastPlayerTime`, the value `PrepareFrame` stores from the
  `currentPlayerTime` that `App` passes to `OnFrame` from a `Stopwatch`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5263-L5266),
  [current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L69)). It is wall-clock time, not the
  simulation clock: samples stay 0.25 s apart in real time under time warp, and keep accumulating
  while the simulation is paused.[^ksa-program] [^ksa-app]
- Sampling stops whenever the HUD is not drawn: HUD hidden with `Visible = false`, game UI hidden
  (`DrawUI`), editor open, or no controlled vehicle. Gaps are not marked; the graph's time axis
  assumes uniform 0.25 s spacing, although the actual spacing is "the first frame at least 0.25 s
  after the last sample" and so drifts slightly longer at low frame rates.
- Export is synchronous file I/O on the main thread, triggered by a button click; it returns a status
  string shown in the section.

## Key code

| Path | Role |
|---|---|
| [TelemetryHistory.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/TelemetryHistory.cs) | Buffers, throttled recording, range helper, CSV export |
| [FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L421-L467) | History section: metric chips, graph, Clear / Export CSV / Open folder |

## Snippets

```csharp
// TelemetryHistory.cs L45-L77
    public static void Record(Vehicle vehicle, ActualEnginePerformanceInfo engines, float actualTwr)
    {
        if (_vehicleId != vehicle.Id)
        {
            Clear();
            _vehicleId = vehicle.Id;
        }

        double playerTime = Program.GetPlayerTime();
        if (playerTime - _lastSampleTime < SampleInterval)
            return;
        _lastSampleTime = playerTime;

        int index;
        if (_count < Capacity)
        {
            index = _count++;
        }
        else
        {
            for (int i = 0; i < _samples.Length; i++)
                Array.Copy(_samples[i], 1, _samples[i], 0, Capacity - 1);
            index = Capacity - 1;
        }

        _samples[(int)TelemetryMetric.Altitude][index] = (float)vehicle.GetBarometricAltitude();
        _samples[(int)TelemetryMetric.SurfaceSpeed][index] = (float)vehicle.GetSurfaceSpeed();
        _samples[(int)TelemetryMetric.Twr][index] = actualTwr;
        _samples[(int)TelemetryMetric.Thrust][index] = engines.TotalThrust / 1000f;
        _samples[(int)TelemetryMetric.Acceleration][index] =
            (float)(vehicle.AccelerationBody.Length() / Constants.STANDARD_GRAVITY);
        _samples[(int)TelemetryMetric.Mass][index] = vehicle.TotalMass;
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Program.GetPlayerTime()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5263-L5266) | Wall-clock sample throttle |
| `Astronomical.Id` (via `Vehicle.Id`) | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L102) | Detect vehicle switches |
| `Vehicle.GetBarometricAltitude()`, `GetSurfaceSpeed()`, `AccelerationBody`, `TotalMass` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L567-L573) | Sampled values |

## Pitfalls and version notes

- Shifting the whole array on every sample is simple and keeps the span contiguous; a true ring
  buffer with a head index avoids the copy but needs the reader to handle wrap-around.
- Because the clock is wall time, a CSV taken under time warp covers much more simulated time than
  `time_s` suggests. Record `Universe.GetElapsedTime()` as an extra column if you need sim time.
- Switching to another vehicle and back discards the history.
- Export failures are caught and shown as text; nothing is logged.

## See also

- [Draw-list line graph](draw-list-line-graph.md)
- [Flight HUD on the controlled vehicle](flight-hud-controlled-vehicle-postfix.md)
- [JSON settings with autosave](json-settings-autosave.md)
- [KSA-AdvancedFlightComputer: guidance flight telemetry CSV](../../ksa-advancedflightcomputer/features/guidance-flight-telemetry-csv.md)
- [Wall-clock player-time interval feeding a fixed-length history array](../../../howto/external-io/telemetry/record-telemetry-and-diagnostics-to-files/record-telemetry-and-diagnostics-to-files-1.md) - how-to article demonstrating this feature

[^ker-telemetry]: TelemetryHistory.cs
[^ker-flighthud]: FlightHud.cs
[^ker-settingsstore]: SettingsStore.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-app]: KSA.App (decompiled, build 2026.10.10.5554)
