---
type: Mod Feature
title: Guidance flight recorder with per-cycle CSV, plan snapshots and an event log
description: A static, single-owner telemetry sink that never throws or waits on the disk, writing a per-step CSV, rate-limited whole-plan snapshots and an event log per 6-DOF run, plus vehicle-prefixed log lines keyed on a bucketed stage signature
mod: ksa-advancedflightcomputer
category: testing-and-tooling
techniques: [harmony-prefix, imgui-window, buffered-file-sink, change-signature-logging]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker, starmap StarMapAfterGui]
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.Universe, Brutal.Logging.DefaultCategory, Brutal.Logging.LogCategory]
tags: [telemetry, logging, csv, diagnostics, flight-recorder, 6-dof, invariant-culture, brutal-logging]
---

AFC's 6-DOF landing guidance can record every flight to disk, so a bad descent is read back afterwards
instead of described from memory. `SixDofLog` is a static sink that writes three files per run; the
player turns it on with "Log telemetry to file" on the 6-DOF page of the guidance panel. Its companion
`GuidanceLog` formats the guidance game-log lines: each names the craft, and staging is logged only when
a coarse stage signature changes. The pattern suits any controller that runs every step: buffer rows in
memory, keep the sink pure and exception-proof, and record the plan as well as the measured state.

## How it works

| File | Content |
|---|---|
| `<run>-cycle.csv` | One row per guidance step that commands the craft: 14-element state, tilt, solve status, iterations and ms, defects with channel and node, plan drift, trust region, thrust demand against capability, throttle, model and allocated torque, TWR, ambient pressure, stop distance, glideslope violation, acceleration bias, error text |
| `<run>-plan.csv` | Every node of the published plan, at most once per `PlanSnapshotInterval` (1 s sim time): on engage, after a successful solve, when logging starts mid-flight |
| `<run>-events.log` | `t(s)  event` lines: engage, node steps, refused re-solves with reason, hand-off, touchdown, faults, cold-solve progress |

Files go to `MyDocuments/My Games/Kitten Space Agency/navbox-logs`, named by local start time
`yyyyMMdd-HHmmss`, plus `-1`, `-2` when any of the three already exists. The plan file exists because a
cycle row only says where the vehicle went: it cannot tell "the plan loops and the craft followed it"
from "the plan was straight and the craft diverged", which have opposite causes (devnotes L344-L351).

Rules the sink follows, each stated in the source:

- **Never throw.** Every entry point catches into `LastError`, which the panel shows. It is called from
  the guidance step and from the ImGui draw, and an exception escaping the draw unwinds past `End()`, so
  the game reports "missing End" and names the wrong function (devnotes L296-L300).
- **Never stall.** Text goes into three `StringBuilder`s; `Cycle` flushes with `File.AppendAllText`
  every 60 rows (`FlushEveryRows`). A failed flush drops its buffer rather than retrying a growing one,
  and 3 failures in a row (`MaxFailedFlushes`) switch logging off.
- **One owner.** `Start(owner, ...)` refuses while another craft's run is open, `Stop(owner)` from a
  non-owner is ignored, and every write checks `Writable(owner)`. The owner is the per-vehicle
  `VehicleAutopilotState`; the panel says "another vehicle owns the log" instead of pretending.
- **Pure sink.** `Cycle` takes a `CycleRow` struct that `LogCycle` fills, so the logger cannot reach back
  into guidance state.
- **Exact numbers.** Doubles use `ToString("R", CultureInfo.InvariantCulture)`, non-finite values become
  empty fields, text is quoted. A comma-decimal locale would otherwise shift columns, and the round-trip
  format lets a logged state go back into the solver bit for bit: the console checks embed the engage
  state "from flight 20260808-104651" (see [closed-loop MPC harness](closed-loop-mpc-harness.md)).

`LogSixDofFault` also appends every 6-DOF exception with a one-line state summary to
`navbox-logs/6dof-faults.log`, telemetry on or not, because `Console.Error` "under StarMap goes nowhere
anyone can read".

`GuidanceLog.Debug` prints only when `DebugConfig.Guidance` is set (DEBUG builds); `Info` always prints.
Both go to `DefaultCategory.Log` with the prefix `[AFC] Guidance '<vehicle.Id>': `. `StageSignature`
writes `Seq:Engines:Bucket(Thrust):Bucket(Isp):Mode` per stage with `Bucket(v) = round(ln v / ln 1.05)`,
one step per 5 percent and no mass, so draining propellant does not change it. `RefreshStageModel` logs
the full `DescribeStages` text only when the signature changes.

## Lifecycle and timing

| Work | Call path | Lifecycle |
|---|---|---|
| Rows, plan snapshots, most events; `Start` on engage, `Stop` on touchdown or hand-off | `OnPrepareWorker` -> `ApplyAutopilot` -> `Step6Dof` -> `Step6DofCore` -> `ApplyPlanCommand` -> `LogCycle` | `frame:dispatch-solvers` |
| Stage-signature line | `ApplyAutopilot` -> `RefreshStageModel` (250 ms wall-clock gate) | `frame:dispatch-solvers` |
| Toggle (`Start` with a plan snapshot, or `Stop`), status, Flush button, disengage | `[StarMapAfterGui] DrawGui` -> 6-DOF page | `frame:draw-ui` |

`Universe.ExecuteNextVehicleSolvers` calls `PrepareVehicleWorkers`, which calls `Vehicle.PrepareWorker`
for each vehicle in turn on the main thread before the vehicle solver job is queued.[^ksa-universe] AFC
prefixes that method, and the panel draws from `[StarMapAfterGui]`, also on the main thread. Every writer
is therefore on one thread, which is why three unsynchronised static `StringBuilder`s are safe; `SixDof.cs`
documents the same invariant for its per-vehicle pointer `_s` (L33-L51). The `afc-6dof-solve` worker
writes nothing to either log: results are logged after the main thread collects them. The price is a
synchronous file append on the main thread every 60 rows.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/SixDofLog.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/SixDofLog.cs#L52-L122) | The sink: ownership, run naming, buffering, flush, CSV formatting |
| [AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs#L587-L660) | `LogCycle` fills a row; engage, toggle (L847-L867) and fault file (L915-L949) |
| [AdvancedFlightComputer/Features/Guidance/Ui/Gauges/SixDofGauge.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Gauges/SixDofGauge.cs#L41-L106) | Checkbox, owner-aware status line, Flush button |
| [AdvancedFlightComputer/Features/Guidance/GuidanceLog.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/GuidanceLog.cs) | Vehicle-prefixed lines, stage description and signature |
| [AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs#L474-L483) | Logs staging only on a signature change |
| [AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs#L26-L27) | Harmony prefix on `Vehicle.PrepareWorker` |
| [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L216-L218) | `[StarMapAfterGui]` panel draw |
| [docs/guidance/scvx-bridge-devnotes.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/guidance/scvx-bridge-devnotes.md#L344-L351) | Why the plan snapshots and the two rules exist |

## Snippets

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

## Game APIs touched

| Member | Use |
|---|---|
| `Vehicle.PrepareWorker` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396)) | Harmony prefix target; the guidance step and its rows run here |
| `Universe.PrepareVehicleWorkers` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2155)) | Calls it per vehicle on the main thread |
| `DefaultCategory.Log` ([current/decomp/Brutal.Logging/DefaultCategory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/DefaultCategory.cs)), `LogCategory.Debug` / `Info` ([current/decomp/Brutal.Logging/LogCategory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/LogCategory.cs#L110-L115)) | Game log target of `GuidanceLog` |

## Pitfalls and version notes

- The sink is lock-free only because every caller is on the main thread. Logging from a solver thread or
  a postfix on a vehicle job thread needs a lock or a queue.
- One craft is recorded at a time; a second engaging craft is silently not logged, by design.
- Plan and event text does not count toward the 60-row flush; it waits for the next flush, `Stop` or the
  Flush button, which is why faults also go straight to `6dof-faults.log`.
- The docs mention `tools/readlog.py` for summarising a run; no such file exists at the pinned ref.
- The folder is `navbox-logs` and the events header says `navbox`; nothing else in the repo uses the name.
- The stage-signature line is a debug line and does not print in Release builds.

## See also

- [6-DOF SCvx landing MPC](scvx-6dof-landing-mpc.md)
- [Guidance vehicle step loop](guidance-vehicle-step-loop.md)
- [Guidance panel and overlays](guidance-panel-and-overlays.md)
- [Warn-once logging and diagnostics](warn-once-logging-and-diagnostics.md)
- [Static single-owner buffered sink with per-step CSV, plan snapshots and event log](../../../howto/external-io/telemetry/record-telemetry-and-diagnostics-to-files/record-telemetry-and-diagnostics-to-files-2.md) - how-to article demonstrating this feature

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
