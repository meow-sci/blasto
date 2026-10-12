---
type: How-To
title: Static single-owner buffered sink with per-step CSV, plan snapshots and event log
description: Record one CSV row per guidance step into memory, flush every 60 rows, swallow every I/O error, allow one owner vehicle per run and log only when a bucketed state signature changes
category: external-io
group: telemetry
topic: record-telemetry-and-diagnostics-to-files
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/guidance-flight-telemetry-csv]
techniques: [buffered-file-sink, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker, starmap StarMapAfterGui]
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.Universe, Brutal.Logging.DefaultCategory]
tags: [csv, logging, buffered, flush, single-owner, signature, dedupe]
---

Outcome: a controller that runs every physics step can record every flight to disk so a bad descent is read back afterwards instead of described from memory. Rows go into memory, flush in batches, and no I/O error ever reaches the game. Alongside the per-step CSV the sink writes a rate-limited snapshot of the whole plan and an event log, and the mod's game-log lines are keyed on a bucketed signature so a repeated state is not logged again. Requires StarMap and Harmony. Demonstrated by [Guidance flight telemetry CSV](../../../../mods/ksa-advancedflightcomputer/features/guidance-flight-telemetry-csv.md) in KSA-AdvancedFlightComputer.

## Background: how the game does it

`Universe.ExecuteNextVehicleSolvers` calls `PrepareVehicleWorkers`, which calls `Vehicle.PrepareWorker` for each vehicle in turn, on the main thread, before the vehicle solver job is queued ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2155), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423)). A prefix on `PrepareWorker` therefore runs once per physics step per vehicle on the main thread, in the settled window where vehicle state matches the last applied step. AFC's autopilot runs from that prefix; the recorder is called from the same place.

## Lifecycle and timing

| Work | Where | Lifecycle |
|---|---|---|
| Rows, plan snapshots, most events; `Start` on engage, `Stop` on touchdown or hand-off | `OnPrepareWorker` -> ... -> `LogCycle` | `frame:dispatch-solvers` |
| Stage-signature line | Autopilot refresh (250 ms wall-clock gate) | `frame:dispatch-solvers` |
| Toggle, status, Flush button, disengage | `[StarMapAfterGui]` panel | `frame:draw-ui` |

Every writer runs on the main thread, which is why three unsynchronised static `StringBuilder`s are safe. A worker thread (the 6-DOF solver) writes nothing to either log: results are logged after the main thread collects them. The price is a synchronous file append on the main thread every 60 rows. The 6-DOF solver itself is the subject of [Run an expensive solve without stalling the frame](../../../flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/overview.md).

## Steps

1. **Define the sink as a static class with an owner.** `Start(owner, ...)` refuses while another craft's run is open, `Stop(owner)` from a non-owner is ignored, and every write goes through `Writable(owner)`. A refusal is not an error: it means another craft is being recorded.

   ```csharp
   // AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/SixDofLog.cs L52-L56, L127-L133, L157-L157
       internal static bool Start(object owner, string vehicleName, string bodyName)
       {
           if (Enabled && Owner != null && !ReferenceEquals(Owner, owner))
               return false;

       // ...
       internal static string Stop(object owner)
       {
           if (Owner != null && !ReferenceEquals(Owner, owner))
               return "";
           Owner = null;
           return Stop();
       }
       // ...
       private static bool Writable(object owner) => Enabled && ReferenceEquals(Owner, owner);
   ```

2. **Name each run by local start time and avoid clashes.** Files go to `MyDocuments/My Games/Kitten Space Agency/navbox-logs`, named `yyyyMMdd-HHmmss`, plus `-1`, `-2` when any of the three files already exists:

   ```csharp
   // AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/SixDofLog.cs L70-L79
               string runTime = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
               RunName = runTime;
               int suffix = 1;
               while (File.Exists(Path.Combine(Directory, RunName + "-cycle.csv"))
                   || File.Exists(Path.Combine(Directory, RunName + "-plan.csv"))
                   || File.Exists(Path.Combine(Directory, RunName + "-events.log")))
                   RunName = runTime + "-" + suffix++;
               _cyclePath = Path.Combine(Directory, RunName + "-cycle.csv");
               _planPath = Path.Combine(Directory, RunName + "-plan.csv");
               _eventsPath = Path.Combine(Directory, RunName + "-events.log");
   ```

3. **Keep the sink pure.** `Cycle` takes a `CycleRow` struct that the caller fills, so the logger cannot reach back into guidance state. Numbers are written with the invariant culture and the round-trip format `R`, and non-finite values become empty fields:

   ```csharp
   // AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/SixDofLog.cs L178-L186, L218-L228, L305-L311
       internal static void Cycle(object owner, in CycleRow r)
       {
           if (!Writable(owner))
               return;
           try
           {
               var sb = _cycle;
               sb.Append(_cycleIndex++).Append(',');
               F(sb, r.T); F(sb, r.Alt);
               // ...
               sb.Append(Csv(r.Error));
               sb.AppendLine();

               if (++_pendingRows >= FlushEveryRows)
                   Flush();
           }
           catch (Exception e)
           {
               LastError = e.Message;
           }
       }
       // ...
       // Invariant culture throughout: a machine with a comma decimal separator would otherwise write "1,234" into a comma-separated file and silently shift every column right of it.
       // Round-trip format, so a logged state can be fed back into the solver offline bit for bit.
       private static void F(StringBuilder sb, double v, bool last = false)
       {
           sb.Append(double.IsFinite(v) ? v.ToString("R", CultureInfo.InvariantCulture) : "");
           if (!last) sb.Append(',');
       }
   ```

4. **Buffer rows in memory; flush every N rows; never retry a growing buffer.** A failed flush drops its buffer, records the message and, after three failures in a row, switches logging off:

   ```csharp
   // AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/SixDofLog.cs L278-L311
       internal static string Flush()
       {
           if (!Enabled && _cycle.Length == 0)
               return "";
           try
           {
               if (_cycle.Length > 0) { File.AppendAllText(_cyclePath, _cycle.ToString()); RowsWritten += _pendingRows; _cycle.Clear(); }
               if (_plan.Length > 0) { File.AppendAllText(_planPath, _plan.ToString()); _plan.Clear(); }
               if (_events.Length > 0) { File.AppendAllText(_eventsPath, _events.ToString()); _events.Clear(); }
               _pendingRows = 0;
               _failedFlushes = 0;
           }
           catch (Exception e)
           {
               // What a failed write could not store is dropped. Kept, it would make every later row call this again with a larger buffer, on the sim thread. A run whose writes keep failing stops logging.
               _cycle.Clear();
               _plan.Clear();
               _events.Clear();
               _pendingRows = 0;
               LastError = e.Message;
               if (++_failedFlushes >= MaxFailedFlushes)
                   Enabled = false;
               return e.Message;
           }
           return "";
       }

       // Invariant culture throughout: a machine with a comma decimal separator would otherwise write "1,234" into a comma-separated file and silently shift every column right of it.
       // Round-trip format, so a logged state can be fed back into the solver offline bit for bit.
       private static void F(StringBuilder sb, double v, bool last = false)
       {
           sb.Append(double.IsFinite(v) ? v.ToString("R", CultureInfo.InvariantCulture) : "");
           if (!last) sb.Append(',');
       }
   ```

5. **Never throw.** Every entry point catches into `LastError`, which the panel shows. The sink is called from the guidance step and from the ImGui draw, and an exception escaping the draw unwinds past `End()`, so the game reports a missing `End` and names the wrong function.

6. **Snapshot the plan, rate-limited.** A cycle row only says where the vehicle went; it cannot distinguish "the plan loops and the craft followed it" from "the plan was straight and the craft diverged". The whole plan is written at most once per `PlanSnapshotInterval` (1 s of sim time) ([AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/SixDofLog.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/SixDofLog.cs#L234-L276)).

7. **Log game-log lines only when a bucketed signature changes.** `GuidanceLog` prefixes every line with the craft id so a log with several guided vehicles still reads, and `StageSignature` buckets each stage's thrust and Isp in 5 percent steps (and ignores mass, so draining propellant does not change it):

   ```csharp
   // AdvancedFlightComputer/Features/Guidance/GuidanceLog.cs L66-L83
       internal static string StageSignature(UpfgVehicle model)
       {
           if (model == null || model.Stages.Count == 0)
               return "none";

           var text = new StringBuilder();
           for (int i = 0; i < model.Stages.Count; i++)
           {
               UpfgStage stage = model.Stages[i];
               text.Append(stage.Seq).Append(':').Append(stage.Engines).Append(':')
                   .Append(Bucket(stage.Thrust)).Append(':').Append(Bucket(stage.Isp)).Append(':')
                   .Append(stage.Mode).Append(' ');
           }
           return text.ToString();
       }

       private static long Bucket(double value) =>
           value > 0.0 ? (long)Math.Round(Math.Log(value) / Math.Log(1.05)) : 0;
   ```

   The refresh compares the signature with the stored one and logs the full stage description only when it differs:

   ```csharp
   // AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs L474-L483
               // One line when the staging changes shape, not one per refresh.
               if (GuidanceLog.Enabled)
               {
                   string signature = GuidanceLog.StageSignature(_s.StageModel);
                   if (signature != _s.StageModelSignature)
                   {
                       _s.StageModelSignature = signature;
                       GuidanceLog.Debug(vehicle, $"stage model: {GuidanceLog.DescribeStages(_s.StageModel)}; KSA total {_s.StageModelKsaDv:F0} m/s.");
                   }
               }
   ```

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/SixDofLog.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/SixDofLog.cs) | The sink: ownership, run naming, buffering, flush, CSV formatting |
| [AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs#L587-L660) | `LogCycle` fills a row |
| [AdvancedFlightComputer/Features/Guidance/GuidanceLog.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/GuidanceLog.cs) | Vehicle-prefixed lines, stage description and signature |
| [AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs#L26-L27) | Harmony prefix on `Vehicle.PrepareWorker` |
| [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L216-L218) | `[StarMapAfterGui]` panel draw |

## Variations and gotchas

- **Fault file.** Every 6-DOF exception is appended with a one-line state summary to `navbox-logs/6dof-faults.log`, telemetry on or not, because `Console.Error` under StarMap goes nowhere anyone can read.
- **Batches can be lost.** A failed write drops what it could not store; that is the price of never blocking a physics step on a bad disk.
- **Debug versus info lines.** `GuidanceLog.Debug` prints only when `DebugConfig.Guidance` is set; `Info` always prints, both to `DefaultCategory.Log`.
- **One owner means a second craft is not recorded.** Interleaving two craft in one CSV would make every column ambiguous.
- **Locale.** Without the invariant culture a comma-decimal machine writes `1,234` into a comma-separated file and silently shifts every column.

## See also

- [Overview](overview.md)
- [Approach 1: history array and CSV on click](record-telemetry-and-diagnostics-to-files-1.md)
- [Approach 3: latched dumps](record-telemetry-and-diagnostics-to-files-3.md)
- [Guidance flight telemetry CSV](../../../../mods/ksa-advancedflightcomputer/features/guidance-flight-telemetry-csv.md)
- [Write a per-mod log file](../../../getting-started/lifecycle/write-a-per-mod-log-file/overview.md)
