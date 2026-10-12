---
type: How-To
title: Dump the exact problem before the solve and replay it offline with full tracing
description: Serialize the plain-data problem the game fed a solver to a versioned SI JSON file at the moment it is built, then re-solve that file in a test console with iteration tracing and command-line overrides
category: testing-and-tooling
group: offline-tests
topic: test-a-solver-or-controller-offline
approach: 2
difficulty: advanced
prerequisites: [dotnet-sdk, game-free-solver]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/solver-problem-json-replay]
techniques: [json-config, console-checks, problem-replay]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers, thread:background]
game_types: [KSA.Vehicle, KSA.Universe]
tags: [testing, solver, replay, json, diagnostics, round-trip]
---

Outcome: when a solve fails or misbehaves in the game, you do not chase it in the game. Every request writes the exact problem it built from the live vehicle to a JSON file; a command in the test console loads that file, solves it again outside the game with the whole iteration trace, and can override stages, limits and floors from the command line.
Demonstrated by KSA-AdvancedFlightComputer's convex ascent planner ([AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs#L419-L451)). The idea carries to any mod that feeds game-derived input to an expensive or fragile computation: put the problem in a plain data type, serialize that exact object where it is built, keep a runner that loads it.

## Background: how the game does it

The request is served from the vehicle step because that is the one point in the frame where the part tree is settled and nothing else is reading it. `Vehicle.PrepareWorker` runs per vehicle on the main thread inside `Universe.ExecuteNextVehicleSolvers`, before the vehicle job is queued ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2148)). The mod copies everything it needs into plain values there (body constants, stage model, drag table, state rotated to the launch-window instant, target) and defines a local `Build(stages, qLimit)` returning the problem; the solve then runs on a separate thread, which never touches game state or the disk.

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Build the problem and write the JSON | the `Vehicle.PrepareWorker` prefix, on the main thread | `frame:dispatch-solvers` |
| Solve it | a job thread (`afc-ascent-scvx`) | `thread:background` |
| Replay | the test console | none (developer machine) |

The file is written synchronously, before the job thread starts, so it records exactly what the solve receives; the cost is one small write per Calculate click. See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Put the problem in a plain data type in the game-free assembly**, next to the solver, so the mod and the console share it. AFC's `AscentProblem` uses `required` init properties and its stage type validates in its constructor, so loading rebuilds through the constructor rather than deserialising into it.

2. **Serialise through private DTOs in SI units.** Every number is SI (CCI position and velocity, mass, thrust in N, flow in kg/s, pressures in Pa). Sentinels become `null`: a NaN throttle floor, an unpinned burn, a seed throttle of 1, an infinite acceleration limit. Loading maps `null` back, so a field added later reads as "not set" in an old file:

   ```csharp
   // AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentProblemFile.cs L70-L84
               Stages = p.Stages.Select(s => new StageDto
               {
                   Thrust = s.Thrust,
                   MassFlow = s.MassFlow,
                   PropellantMass = s.PropellantMass,
                   JettisonMass = s.JettisonMass,
                   DragArea = s.DragArea,
                   PressureGrid = s.PressureGrid,
                   ThrustAtPressure = s.ThrustAtPressure,
                   ThrottleMin = double.IsNaN(s.ThrottleMin) ? null : s.ThrottleMin,
                   FixedBurnTime = s.IsPinned ? s.FixedBurnTime : null,
                   LiquidCarriesOver = s.LiquidCarriesOver,
                   SeedThrottle = s.SeedThrottle < 1.0 ? s.SeedThrottle : null,
                   AccelerationLimit = double.IsFinite(s.AccelerationLimit) ? s.AccelerationLimit : null,
                   SolidTime = s.Solid?.Time,
   ```

   `WriteIndented = true` ([AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentProblemFile.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentProblemFile.cs#L109)) keeps the file readable and editable by hand.

3. **Write the dump where the problem is built, before the job starts**, and put the path in the log line that announces the request. A failed write must not fail the request:

   ```csharp
   // AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs L419-L431
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
   ```

   ```csharp
   // AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs L449
           job = new AscentPlanJob(Build, count, initial, qMax, Publish);
   ```

4. **Write a replay command in the test console.** It loads the file, applies overrides (`--stages N`, `--qmax <kPa>`, `--qamax <Pa rad>`, `--floor <fraction>`), prints the body, target and every stage, then runs the solver with callbacks that print each iteration and phase. Return 2 for a missing file, 0 for converged, 1 otherwise:

   ```csharp
   // tests/AdvancedFlightComputer.Guidance.Tests/Scvx/AscentCheck.cs L79-L90
       /// <summary>Solve a problem the game wrote out (see AscentProblemFile), optionally with fewer stages, printing the whole trace.</summary>
       internal static int Replay(string path, string[] args)
       {
           if (!File.Exists(path))
           {
               Console.WriteLine($"no such problem file: '{path}'");
               return 2;
           }
           AscentProblem p = AscentProblemFile.FromJson(File.ReadAllText(path));
           int si = Array.IndexOf(args, "--stages");
           if (si >= 0 && si + 1 < args.Length && int.TryParse(args[si + 1], out int k) && k >= 1 && k < p.Stages.Length)
           {
   ```

   ```csharp
   // tests/AdvancedFlightComputer.Guidance.Tests/Scvx/AscentCheck.cs L131-L141
           AscentSolution sol = AscentScvx.Solve(p, it =>
               Console.WriteLine($"iter {it.Index}: rho={it.Rho:+0.00;-0.00} {it.Status,-7} tr={it.TrustRegion:F3}  "
                   + $"sig={string.Join("/", it.SigmaSeconds.Select(x => x.ToString("F0")))}s  pred={it.Predicted:E1}  "
                   + $"defect_n={it.DefectNorm:E2}  pviol={it.PathViolation:E1}  term={it.TerminalViolation:E1}  "
                   + $"dv={it.DeltaV:F0}  J={it.Cost:E5}  clarabel={it.SolverIterations} {it.SolveMs:F0} ms"),
               phase => Console.WriteLine("-- " + phase));
           Console.WriteLine($"{sol.Message}: kick {sol.KickDeg:F3} deg, seed {sol.SeedSeconds:F1} s, total {sol.SolveSeconds:F1} s");
           if (sol.Nodes > 0)
               Console.WriteLine($"to orbit {sol.FinalMass / 1000.0:F2} t, burns {string.Join(" / ", sol.BurnTime.Select(b => b.ToString("F1")))} s, "
                   + $"max q {sol.DynamicPressure.Max() / 1000.0:F1} kPa, max q-alpha {sol.QAlpha.Max():F0}, miss {sol.TerminalResidual[0]:F0} m / {sol.TerminalResidual[1]:F2} m/s");
           return sol.Converged ? 0 : 1;
   ```

5. **Keep old files readable with round-trip checks.** A check serialises, deserialises and compares; another deletes a field from the JSON with a regex and demands the old behaviour (here: a file without the acceleration limit reads back unlimited):

```csharp
// tests/AdvancedFlightComputer.Guidance.Tests/Scvx/AscentGLimitCheck.cs L136-L149
    private static void RoundTrip()
    {
        Console.WriteLine();
        Console.WriteLine("the problem file carries the limit");
        AscentProblem p = SaturnV(3.0 * G0);
        string json = AscentProblemFile.ToJson(p);
        AscentProblem q = AscentProblemFile.FromJson(json);
        Check("the limit survives", q.Stages.Zip(p.Stages).All(pair => pair.First.AccelerationLimit == pair.Second.AccelerationLimit),
              $"{q.Stages[0].AccelerationLimit:F5} m/s^2");
        string older = Regex.Replace(json, @"\s*""AccelerationLimit"":\s*[^,\r\n]+,?", "");
        AscentProblem r = AscentProblemFile.FromJson(older);
        Check("a file without it reads back unlimited", !older.Contains("AccelerationLimit") && r.Stages.All(s => double.IsPositiveInfinity(s.AccelerationLimit)));
        AscentProblem free = AscentProblemFile.FromJson(AscentProblemFile.ToJson(AscentCheck.SaturnV()));
        Check("an unlimited problem stays unlimited", free.Stages.All(s => double.IsPositiveInfinity(s.AccelerationLimit)));
```

6. **Run the cheap round trips in CI** (`--ascent-glimit` is in the CI flag list; the solids and angle-of-attack variants are run by hand) and use the replay when a user reports a failed plan: ask for the file named in the `convex ascent requested` log line.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentProblemFile.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentProblemFile.cs) | DTOs, `ToJson`, tolerant `FromJson` |
| [AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs#L419-L451) | Writes the dump, logs its path, starts the job with the same `Build` |
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/AscentCheck.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/AscentCheck.cs#L79-L142) | `Replay`: load, override, trace, exit code |
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/AscentGLimitCheck.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/AscentGLimitCheck.cs#L135-L159) | Round-trip and old-file checks |

## Variations and gotchas

- **Dump what the solve receives, not what the UI shows.** Both the dump and the job start from the same `Build`, so they cannot disagree.
- **Version by tolerance.** Nullable fields that read back as "not set" keep old dumps usable without a version number; breaking changes need a new field name.
- **Constructor validation is a feature.** A stage with solids cannot take an acceleration limit and a limit that is not a positive number is refused; the round-trip check asserts both.
- **Temp files.** The dump goes to `<temp>/afc-convex-ascent/<vehicle id>.json` with invalid filename characters stripped from the vehicle id.
- **Game updates.** The file format is independent of KSA; only the code that builds the problem from game state needs review after a build.

## See also

- [Overview](overview.md); [approach 1](test-a-solver-or-controller-offline-1.md)
- [Run an expensive solve without stalling the frame](../../../flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/overview.md), [Fly closed-loop ascent guidance](../../../flight-and-orbits/guidance/fly-closed-loop-ascent-guidance/overview.md)
- Mod feature: [Solver problem JSON replay](../../../../mods/ksa-advancedflightcomputer/features/solver-problem-json-replay.md)
