---
type: Mod Feature
title: Dump the in-game solver problem as JSON and replay it offline
description: The convex-ascent request writes the exact problem it built from the live vehicle to plain SI JSON before solving, and a test-console flag re-solves that file offline with a full trace, with round-trip checks keeping old files readable
mod: ksa-advancedflightcomputer
category: testing-and-tooling
techniques: [harmony-prefix, problem-dump-replay, json-dto, game-free-assemblies]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.Universe, KSA.IParentBody]
tags: [testing, replay, json, system-text-json, diagnostics, convex-ascent, scvx, reproducibility]
---

When AFC's convex ascent planner fails to converge in the game, the failure is not chased in the game.
Every Calculate request writes the exact problem it built from the live vehicle (body, lift-off state,
every stage's thrust table and solids, the sampled drag table, the atmosphere, target and limits) to a
JSON file, and the test console's `--ascent-replay <file>` solves that file again outside the game with
the whole iteration trace. Round-trip checks keep the format stable and older files readable. The idea
carries over to any mod that feeds game-derived input to an expensive or fragile computation: put the
problem in a plain data type, serialize that exact object where it is built, keep a runner that loads it.

## How it works

**The format.** `AscentProblemFile` sits in the game-free `AdvancedFlightComputer.Guidance.Scvx`
assembly next to the solver, so the mod and the console share it.

- Private `ProblemDto` and `StageDto` mirror the domain types. `AscentProblem` uses `required` init
  properties and `AscentStage` validates in its constructor (`AscentModel.cs` L107-L119), so
  `FromJson` rebuilds through that constructor rather than deserializing into it.
- Every number is SI: CCI position and velocity, mass, thrust in N, flow in kg/s, pressures and the
  q limit in Pa, the q-alpha limit in Pa rad.
- Sentinels become `null`: a NaN throttle floor, an unpinned burn, a seed throttle of 1 and an infinite
  acceleration limit. `FromJson` maps `null` back, so a field added later reads as "not set" in an old file.
- Only a `KsaAscentAtmosphere` is written: `[rho0, p0, scale height, gamma]`, the nose-first `Cd` curve
  at the default Mach breakpoints, and the whole `Cd` table, Mach by angle of attack, when there is one.
  `FromJson` prefers the table, falls back to the curve laid flat across angle of attack, then vacuum.
- `WriteIndented = true`, so a file can be read and edited by hand.

**The game side.** `TryStartAscentPlan` copies everything into plain values (the body's `Mu`, radius and
spin, the stage model recomputed at ambient pressure, the drag table, the CCI state rotated to the
launch-window instant, the insertion target) and defines a local `Build(stages, qLimit)` returning an
`AscentProblem` (`ConvexAscent.cs` L214-L365). It writes `Build(count, qMax)`, every stage at the
requested max-q, to `<temp>/afc-convex-ascent/<vehicle id>.json`, puts the path (or `not written:
<reason>`) into the `convex ascent requested` log line, then hands the same `Build` to `AscentPlanJob`.

**The replay.** `--ascent-replay <file>` calls `AscentCheck.Replay`, which returns 2 for a missing file,
loads it, applies `--stages N`, `--qmax <kPa>`, `--qamax <Pa rad>` and `--floor <fraction>`, prints the
body, target and every stage, then runs `AscentScvx.Solve` with callbacks that print each iteration
(ratio, status, trust region, burn times, defect, path and terminal violation, delta-v, cost, Clarabel
iterations and ms) and each phase. Exit code 0 means converged, 1 not.

| Round-trip check | Flag, in CI | Asserts |
|---|---|---|
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/AscentGLimitCheck.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/AscentGLimitCheck.cs#L135-L159) | `--ascent-glimit`, yes | The acceleration limit survives; a file with the field deleted by regex reads back unlimited |
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/AscentSolidCheck.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/AscentSolidCheck.cs#L351-L371) | `--ascent-solids`, no | Solid thrust and flow tables, the pinned burn and carry-over survive |
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/AscentAoaCheck.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/AscentAoaCheck.cs#L201-L205) | `--ascent-aoa`, no | The drag table's angle-of-attack axis survives (Cd at 10 deg within 1e-6) |

The CI column follows [build/ci/test-guidance.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/ci/test-guidance.ps1#L165).

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Build the problem, write the JSON | `ApplyAutopilot` -> `StepAscentPlanner` -> `TryStartAscentPlan`, in the `Vehicle.PrepareWorker` prefix | `frame:dispatch-solvers` (main thread) |
| Solve it | `AscentPlanJob` thread `afc-ascent-scvx` | `thread:background`, see [convex ascent](convex-ascent-scvx.md) |
| Replay | Scvx test console | none (developer machine) |

The request is served from the vehicle step because "this is the one point in the frame where the part
tree is settled and nothing else is reading it" (`Autopilot.cs` L1020-L1021). `Vehicle.PrepareWorker`
runs per vehicle on the main thread inside `Universe.ExecuteNextVehicleSolvers`, before the vehicle job
is queued.[^ksa-universe] The file is written synchronously there, before `new AscentPlanJob(...)`
starts its thread, so it records exactly what the solve receives and the solve thread never touches the
disk. The cost is one small write per Calculate click.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentProblemFile.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentProblemFile.cs) | DTOs, `ToJson`, tolerant `FromJson` |
| [AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs#L419-L449) | Writes the dump, logs its path, starts the job with the same `Build` |
| [AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs#L1017-L1021) | Serves the request from the `PrepareWorker` prefix |
| [AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentModel.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentModel.cs#L346-L381) | `AscentProblem`, the serialized type |
| [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentPlan.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentPlan.cs#L429-L463) | `AscentPlanJob`, the solve thread that consumes the same `Build` |
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/AscentCheck.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/AscentCheck.cs#L79-L142) | `Replay`: load, override, trace, exit code |
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/Program.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/Program.cs#L143-L147) | `--ascent-replay` dispatch (help text at L50-L51) |

## Snippets

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs L419-L451
        // The whole problem, every stage, as JSON beside the temp files: the test console's --ascent-replay solves it again offline, which is how a plan that fails in the game gets diagnosed.
        string dump = "";
        try
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "afc-convex-ascent");
            System.IO.Directory.CreateDirectory(dir);
            dump = System.IO.Path.Combine(dir, $"{string.Concat(vehicleId.Split(System.IO.Path.GetInvalidFileNameChars()))}.json");
            System.IO.File.WriteAllText(dump, AscentProblemFile.ToJson(Build(count, qMax)));
        }
        catch (Exception e)
        {
            dump = "not written: " + e.Message;
        }
        // ...
        job = new AscentPlanJob(Build, count, initial, qMax, Publish);
        error = "";
        return true;
```

```csharp
// AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentProblemFile.cs L112-L136
    public static AscentProblem FromJson(string json)
    {
        ProblemDto dto = JsonSerializer.Deserialize<ProblemDto>(json)
            ?? throw new InvalidDataException("not an ascent problem");
        AscentAtmosphere air = VacuumAscentAtmosphere.Instance;
        if (dto.Atmosphere is { Length: 4 } a)
        {
            var atm = new ExponentialAtmosphere(a[0], a[1], a[2], a[3]);
            AeroTable? table = null;
            if (dto.CdMach is { Length: >= 2 } machs && dto.CdAlphaDeg is { Length: >= 2 } alphas
                && dto.CdTable is { } grid && grid.Length == machs.Length * alphas.Length)
            {
                table = new AeroTable(machs, alphas, grid);
            }
            else if (dto.CdMach is { Length: >= 2 } mach && dto.Cd is { } cd && cd.Length == mach.Length)
            {
                // The curve is all the dynamics read, so it is laid down flat across alpha.
                double[] alpha = [0.0, 180.0];
                var values = new double[mach.Length * 2];
                for (int i = 0; i < mach.Length; i++)
                    values[i * 2] = values[i * 2 + 1] = cd[i];
                table = new AeroTable(mach, alpha, values);
            }
            air = new KsaAscentAtmosphere(atm, table);
        }
```

## Game APIs touched

The dump and replay call no game API; these are the members whose values end up in the file.

| Member | Field |
|---|---|
| `Vehicle.PrepareWorker` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396)) | Harmony prefix target where the problem is built and written |
| `Vehicle.TotalMass` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L567)) | `M0` |
| `IParentBody.Mu`, `GetAngularVelocity`, `GetAtmosphereRadius` ([current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L15-L78)) | `Mu`, `Omega`, insertion-radius floor |

## Pitfalls and version notes

- One file per vehicle id in the OS temp folder, overwritten by every request; copy it to keep it.
- The file holds every stage at the requested max-q, but the job starts with the fewest stages whose
  ideal delta-v reaches 1.1 times the insertion speed, may drop a stage that barely burns, and may retry
  once at a looser max-q (`QRelaxFactor` 1.15 times the lowest max-q any seed reached, rounded up to
  5 kPa; `KsaAscentPlan.cs` L500-L551). Use `--stages` and `--qmax` to replay one attempt.
- `--stages N` is not `AscentPhasePlan.Truncate` (L226-L242): it rebuilds the last stage with the
  seven-argument constructor, dropping its solids, throttle floor, pinned time, carry-over, seed throttle
  and acceleration limit, and adds no carry-over propellant.
- `AscentProblem.Settings` and `LiftoffDirection` are not serialized. The game sets neither, so a replay
  uses the defaults of whichever build runs it.
- The drag table is resampled at `AeroTable`'s default breakpoints, not the sweep's own.

## See also

- [Convex ascent with SCvx](convex-ascent-scvx.md)
- [Game-free guidance checks](game-free-guidance-checks.md)
- [Drag table measurement](drag-table-measurement.md)
- [Guidance flight recorder](guidance-flight-telemetry-csv.md)
- [Dump the exact problem before the solve and replay it offline with full tracing](../../../howto/testing-and-tooling/offline-tests/test-a-solver-or-controller-offline/test-a-solver-or-controller-offline-2.md) - how-to article demonstrating this feature
- [Check numerics against an independent implementation with console checks and Python reference fixtures](../../../howto/testing-and-tooling/offline-tests/unit-test-mod-logic-without-game/unit-test-mod-logic-without-game-3.md) - how-to article demonstrating this feature

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
