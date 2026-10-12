---
type: How-To
title: Wall-clock player-time interval feeding a fixed-length history array
description: Sample six flight metrics every 0.25 s of player time from the HUD draw into shifted arrays for a graph, and export them as CSV to the KSA documents folder when the player clicks Export
category: external-io
group: telemetry
topic: record-telemetry-and-diagnostics-to-files
approach: 1
difficulty: beginner
prerequisites: [harmony]
demonstrated_by: [mods/kitten-engineer-redux/features/telemetry-history-and-csv-export]
techniques: [telemetry-sampling, harmony-postfix]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi]
lifecycle: [frame:draw-ui]
game_types: [KSA.Vehicle, KSA.Program, KSA.App]
tags: [csv, history, wall-clock, sampling, export]
---

Outcome: a HUD section graphs the last three minutes of altitude, surface speed, thrust-to-weight, thrust, acceleration and mass, and a button exports the buffer to a CSV file under the KSA documents folder. The buffer is filled from the UI pass itself, throttled by a clock check, so there is no extra hook, thread or timer. Demonstrated by [Telemetry history and CSV export](../../../../mods/kitten-engineer-redux/features/telemetry-history-and-csv-export.md) in Kitten-Engineer-Redux.

## Background: how the game does it

`Program.GetPlayerTime()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5263-L5266)) returns `_lastPlayerTime`, the `currentPlayerTime` that `App.Run` computes from a `Stopwatch` and passes to `OnFrame` ([current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L39-L74)). It is wall-clock time, not the simulation clock: it keeps counting while the simulation is paused, and it does not speed up under time warp. That is the reason this approach is simple and the reason it has limits.

## Lifecycle and timing

- `Record` runs inside the flight HUD's postfix on `Vehicle.OnDrawUi` (`frame:draw-ui`), before the window is drawn, so it reads main-thread vehicle state applied earlier in this frame ([lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).
- Sampling stops whenever the HUD is not drawn: HUD hidden, game UI hidden (`DrawUI` false, where `Vehicle.OnDrawUi` is not called), editor open, or no controlled vehicle. Gaps are not marked in the data.
- Export is synchronous file I/O on the main thread, triggered by a button click; it returns a status string shown in the section.

## Steps

1. **Allocate fixed-length arrays and a wall-clock interval.** Six `float[720]` arrays at a 0.25 s interval give three minutes:

   ```csharp
   // TelemetryHistory.cs L22-L43
   internal static class TelemetryHistory
   {
       public const int Capacity = 720;
       public const double SampleInterval = 0.25;

       private static readonly float[][] _samples =
       {
           new float[Capacity],
           new float[Capacity],
           new float[Capacity],
           new float[Capacity],
           new float[Capacity],
           new float[Capacity]
       };

       private static int _count;
       private static double _lastSampleTime = double.NegativeInfinity;
       private static string? _vehicleId;

       public static int Count => _count;

       public static string? LastExportPath { get; private set; }
   ```

2. **Record from the draw hook, throttled by `Program.GetPlayerTime()`.** Clear when the vehicle changes; return unless the interval has elapsed; when the buffer is full, shift every array left by one so the graph can index a contiguous, oldest-first span; then write one value per metric:

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

   The call site passes the vehicle and values the HUD already computed ([FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L143-L143)).

3. **Export on click.** Build the file name from the sanitised vehicle id and the local time, write `time_s` as negative seconds before the newest sample, use the invariant culture so a comma-decimal machine does not shift columns, and return a status string instead of throwing:

   ```csharp
   // TelemetryHistory.cs L110-L145
       public static string ExportCsv()
       {
           if (_count == 0)
               return "No samples to export yet";

           try
           {
               Directory.CreateDirectory(SettingsStore.ExportDirectory);
               string vehicleName = SanitizeFileName(_vehicleId ?? "vehicle");
               string fileName = $"telemetry_{vehicleName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
               string path = Path.Combine(SettingsStore.ExportDirectory, fileName);

               var builder = new StringBuilder();
               builder.AppendLine("time_s,altitude_m,surface_speed_mps,twr,thrust_kn,acceleration_g,mass_kg");
               for (int i = 0; i < _count; i++)
               {
                   double time = -(_count - 1 - i) * SampleInterval;
                   builder.Append(time.ToString("F2", CultureInfo.InvariantCulture));
                   for (int metric = 0; metric < _samples.Length; metric++)
                   {
                       builder.Append(',');
                       builder.Append(_samples[metric][i].ToString("G7", CultureInfo.InvariantCulture));
                   }
                   builder.AppendLine();
               }

               File.WriteAllText(path, builder.ToString());
               LastExportPath = path;
               return $"Exported {_count} samples to {fileName}";
           }
           catch (Exception ex)
           {
               return $"Export failed: {ex.Message}";
           }
       }

   ```

   The target directory is `Documents/My Games/Kitten Space Agency/KittenEngineerReduxData/exports` ([SettingsStore.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SettingsStore.cs#L46-L59)); the "Open folder" button starts the OS file browser on it with `Process.Start(UseShellExecute = true)` ([SettingsStore.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SettingsStore.cs#L130-L141)).

## The wall-clock sampling interval, and when it is the wrong clock

`Program.GetPlayerTime()` suits a UI graph of the last few minutes of real time, and the throttle costs one subtraction per frame. Its costs are:

| Situation | Effect on the buffer |
|---|---|
| Simulation paused | Samples keep accumulating; the graph fills with a flat line |
| Time warp | Samples stay 0.25 s apart in real time, so a CSV taken under warp covers much more simulated time than `time_s` suggests |
| Low frame rate | The real spacing is "the first frame at least 0.25 s after the last sample", so it drifts longer than 0.25 s while the axis assumes uniform spacing |
| HUD hidden | Sampling stops with no marker |

If the file must be time-warp-correct, sample on simulation time instead: record `Universe.GetElapsedSeconds()` as a column, or drive the sampling from a drift-free accumulator as in [Export live game state to background threads](../export-game-state-to-other-threads/overview.md) (that article's sample clock keeps phase but drops backlog after a hitch). Wall-clock sampling is the right choice only when the thing being graphed is itself a real-time UI value.

## Complete example

| File | Role |
|---|---|
| [TelemetryHistory.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/TelemetryHistory.cs) | Buffers, throttled recording, range helper, CSV export |
| [FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L421-L467) | History section: metric chips, graph, Clear / Export CSV / Open folder |
| [SettingsStore.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SettingsStore.cs) | Data directory and `OpenFolder` |

## Variations and gotchas

- **Shifting versus a ring buffer.** Shifting 719 floats per array (about 17 KB for all six) per sample keeps the span contiguous; a ring with a head index avoids the copy but makes the reader handle wrap-around.
- **Switching vehicle and back discards the history,** because the buffer is cleared on any `Vehicle.Id` change.
- **Export errors are caught and shown as text;** nothing is logged. Log them as well if you need a trail.
- **For larger or continuous recordings,** do not write a growing file from the draw on the click; use a buffered sink as in [approach 2](record-telemetry-and-diagnostics-to-files-2.md).

## See also

- [Overview](overview.md)
- [Approach 2: buffered file sink](record-telemetry-and-diagnostics-to-files-2.md)
- [Telemetry history and CSV export](../../../../mods/kitten-engineer-redux/features/telemetry-history-and-csv-export.md)
- [Compute mission elapsed time and detect liftoff](../../../flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/overview.md)
