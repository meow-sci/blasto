---
type: Mod Feature
title: Closed-loop MPC harness and solve-purity checks without the game
description: A console harness that flies the 6-DOF planner closed-loop through RK4 dynamics with injected thrust and gimbal errors to measure plan jump, path ratio and solve cost, plus determinism and threading checks that gated the worker-thread move
mod: ksa-advancedflightcomputer
category: testing-and-tooling
techniques:
  - closed-loop-simulation
  - determinism-check
  - background-thread
  - game-free-assemblies
  - reference-fixtures
hooks: []
lifecycle: []
game_types: []
tags: [testing, mpc, closed-loop, simulation, determinism, threading, scvx, benchmarking]
---

AFC's 6-DOF landing guidance had flight symptoms no single solve showed: plans jumping between
re-solves, the vehicle flying long curves instead of heading for the pad, solve times creeping up. So the
Scvx test console has a closed-loop simulator, `MpcHarness` (`--mpc`), that runs the real
receding-horizon loop (solve, fly the plan through integrated dynamics with injected errors, re-solve from
where the vehicle really is) and reduces each run to a few numbers. The dev notes call it "the only thing
that reproduced flight symptoms". Two smaller checks in the same console, `--snapshot` and `--thread`,
established the properties needed before the solve could move to a worker thread. Note the class is
`MpcHarness` but the file is named `McpHarness.cs`; the mismatch is in the repo.

## How it works

**Scenario.** Start, target and node count come from `python_ref/loop_ref.csv` (rows: start state,
target, converged trajectory whose length gives N), found by walking up to 8 directories from the
executable. `loop_ref.py` writes that fixture by reproducing the original SCvx loop with CVXPY (Clarabel)
and JAX; `--loop` checks the C# solver against it, the harness only borrows the scenario. The start is then disturbed on purpose (8 and 6 m/s off-plane, 12 degree
tilt, small body rates), "what a real engage looks like". Five named weight and scaling sets are flown,
including the reference weights and `MOD AS SHIPPED`.

**Loop** (`Simulate`):

1. Cold solve from a straight-line seed with hover thrust (`Solve(30)`).
2. Each cycle advances 0.4 node intervals, "the mod's cadence". Thrust (plus or minus 3 percent) and gimbal
   (plus or minus 2 percent) errors come from a fixed-seed `Random(4242)`, scaled by `Dispersion`.
3. The plan's control is interpolated linearly between nodes, as trapezoidal collocation assumes, and
   integrated with 8 RK4 substeps of `Dynamics6Dof.Eval`, renormalising the quaternion.
4. The old plan is shifted by whole nodes, node 0 set to the measured state, `Reseed` with a 0.05 trust
   region, then `Solve(Budget)` under a stopwatch; ADMM counts come from `solver.Trace`.
5. Stop at twice the burn time, 1 m above the target, 400 cycles, or a failed solve.

**Metrics.** Miss; path length over straight-line distance (1.0 is direct); mean PLAN JUMP, the largest
time-aligned position gap between consecutive plans; AWAY-FROM, the summed increases in range to target,
which tells a real loop from a curved but closing descent; worst ADMM iterations and ms per cycle; and a
flag when the burn time sits on its `SigmaMax` or `SigmaMin` bound.

| Flag | Question |
|---|---|
| `--mpc` | Config sweep; plan jump at 0, 3, 6 percent thrust error and budgets 5, 15; truncation handling; ADMM cap sweep; node sweep at zero dispersion |
| `--mpc --budget` | Do 1, 2, 3 or 5 SCvx iterations per cycle track equally well |
| `--mpc --trunc` | ADMM cap 2000 with and without escalation to 12000 |
| `--mpc --split` | SCS init against solve time, p50/p90/p99/max, at N = 30, 50, 80 |
| `--mpc --tail` | Solve-time tail against tolerance (1e-5 to 1e-3) and Anderson memory |
| `--mpc --grav` | Moon against Earth at equal TWR, then falling TWR, under dynamic similarity, clean and dispersed |

The results set mod constants: the default of 50 nodes cites a zero-dispersion `--mpc` table (plan jump
4.0 m at 20 nodes, 1.3 m at 50), and the panel's "can this be done at all" check cites `--mpc --grav`
(TWR 1.25 needs 510 m of a 300 m descent and misses by 173 m), in `SixDof.cs` L18-L25 and L1672-L1681.

**`--snapshot` (`SnapshotCheck`): is a solve a pure function of its inputs?** Using the engage state
logged in flight 20260808-104651, 30 nodes and 8 `Iterate()` calls: the same problem twice must give a
bit-identical `ReferenceX` (no hidden cache, clock or static); 3 m/s^2 more gravity, as a bias would add,
must change it (otherwise the replay test is vacuous); and two solvers with different gravity, stepped
alternately, must each match their solo run, which is the shape of a worker holding one solver while the
step builds the next. Its comment states what it cannot cover: that inputs reach the model only at a
solve's entry (`Ksa6DofGuidance.CommitInputs`), which needs KSA types and is checked by inspection.

**`--thread` (`ThreadCheck`).** The same solve inline and on a background `Thread` must be bit-identical,
which rules out shared mutable state in the native SCS layer. Then a producer thread publishes immutable
`Snapshot` records (mirroring the mod's `Ksa6DofPlan` record) whose fields all carry the generation number
through a `volatile` field, and the reader counts torn reads over 2,000,000 reads. The reader drives the
loop because an earlier version "passed by measuring nothing".

## Lifecycle and timing

None in the game frame: these are console runs on a developer machine against the same game-free solver
assembly the mod ships. They protect in-game timing: in the mod the step dispatches on the main thread in
the `Vehicle.PrepareWorker` prefix and the solve runs on the `afc-6dof-solve` thread (see
[6-DOF SCvx landing MPC](scvx-6dof-landing-mpc.md)); `--snapshot` and `--thread` are the evidence that
moving the solve there leaves the answer unchanged.

## Key code

| Path | Role |
|---|---|
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/McpHarness.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/McpHarness.cs#L329-L505) | `MpcHarness.Simulate`: the loop and metrics; sweeps in `Run` (L57-L324) |
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/SnapshotCheck.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/SnapshotCheck.cs) | Replay, input sensitivity, cross-talk |
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/ThreadCheck.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/ThreadCheck.cs#L77-L127) | Off-thread identity, torn-read hammer |
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/Program.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/Program.cs#L190-L198) | `--mpc` sub-flags; `--snapshot` and `--thread` at L108-L116 |
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/python_ref/loop_ref.py](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/python_ref/loop_ref.py#L1-L16) | Reference loop and CSV layout |
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/python_ref/loop_ref.csv](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/python_ref/loop_ref.csv) | Scenario fixture |
| [AdvancedFlightComputer/Features/Guidance/Scvx/SixDof/Scvx6DofSolver.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Scvx/SixDof/Scvx6DofSolver.cs#L267-L355) | `Reseed`, `Solve`, `Iterate` |
| [AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs#L18-L25) | Constants justified by harness runs |
| [docs/guidance/scvx-bridge-devnotes.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/guidance/scvx-bridge-devnotes.md#L333-L342) | Table of console modes and what each answers |

## Snippets

```csharp
// tests/AdvancedFlightComputer.Guidance.Tests/Scvx/McpHarness.cs L435-L476
            // --- re-solve from where the vehicle ACTUALLY is ---
            int shift = Math.Clamp((int)Math.Round(step / dtNode), 0, n - 2);
            var xs = new double[n * NX];
            var us = new double[n * NU];
            for (int k = 0; k < n; k++)
            {
                int src = Math.Min(k + shift, n - 1);
                Array.Copy(plan, src * NX, xs, k * NX, NX);
                Array.Copy(planU, src * NU, us, k * NU, NU);
            }
            Array.Copy(state, 0, xs, 0, NX);

            double newSigma = Math.Max(sigma - step, cfg.SigmaMin);
            // ...
            solver.Reseed(state, xs, us, newSigma, trustRegion: 0.05);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            ScvxStatus st = solver.Solve(Budget);
            totalMs += sw.Elapsed.TotalMilliseconds;
            // ...
            // PLAN JUMP: how far the new plan sits from the OLD plan, time-aligned.
            double jump = 0;
            double[] fresh = solver.ReferenceX;
            for (int k = 0; k < n - shift; k++)
            {
                int old = k + shift;
                double dx = fresh[k * NX] - plan[old * NX];
                double dy = fresh[k * NX + 1] - plan[old * NX + 1];
                double dz = fresh[k * NX + 2] - plan[old * NX + 2];
                jump = Math.Max(jump, Math.Sqrt(dx * dx + dy * dy + dz * dz));
            }
            planJump += jump;
```

```csharp
// tests/AdvancedFlightComputer.Guidance.Tests/Scvx/SnapshotCheck.cs L60-L104
        double[] a1 = Solve(g0, out double sigmaA);
        double[] a2 = Solve(g0, out _);
        double[] b1 = Solve(biased, out double sigmaB);

        bool replay = Identical(a1, a2);
        bool matters = !Identical(a1, b1);

        Console.WriteLine($"  solve at gz {g0:F2}                        : sigma {sigmaA:F4} s");
        Console.WriteLine($"  same again                                : "
                          + $"{(replay ? "bit-identical" : "DIFFERS - something is not in the inputs")}");
        Console.WriteLine($"  solve at gz {biased:F2} (3 m/s^2 of bias)     : sigma {sigmaB:F4} s   "
                          + $"{(matters ? "differs - the inputs reach the model" : "IDENTICAL - inputs ignored")}");

        // Cross-talk: two solvers alive at once, interleaved, must be unaffected by
        // each other. This is the shape the threaded version takes.
        (double[] x, double[] y) = Interleaved(g0, biased);
        bool noCrossTalk = Identical(x, a1) && Identical(y, b1);
        Console.WriteLine($"  two solvers interleaved                   : "
                          + $"{(noCrossTalk ? "both match their solo runs" : "DIFFER - shared state between instances")}");
    // ...
    private static (double[] a, double[] b) Interleaved(double gzA, double gzB)
    {
        (Scvx6DofSolver sa, _) = Build(gzA);
        (Scvx6DofSolver sb, _) = Build(gzB);
        for (int i = 0; i < 8; i++)
        {
            sa.Iterate();
            sb.Iterate();
        }
        return (sa.ReferenceX.ToArray(), sb.ReferenceX.ToArray());
    }
```

## Game APIs touched

None, by design.

## Pitfalls and version notes

- The plant is the planner's own model: `Integrate` calls `Dynamics6Dof.Eval` with the same parameters,
  so only the injected errors and discretisation separate plan from flight. The dev notes name measured
  inertia, roll authority and gimbal limit as the suspected reason an in-flight spiral did not reproduce
  offline (L361-L363).
- Holding node 0's control across a step instead of interpolating produced fake plan jump; the comment in
  `Simulate` records it.
- `MpcHarness` keeps its knobs (`Dispersion`, `Budget`, `AdmmCap`, `Eps`) in static fields that each mode
  sets and resets; a new mode must restore them.
- `--snapshot` and `--thread` are missing from `--help`, and none of `--mpc`, `--snapshot`, `--thread`
  runs in CI ([build/ci/test-guidance.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/ci/test-guidance.ps1#L165)).
- `ThreadCheck`'s comment names `PoweredGuidance6Dof`, which does not exist at the pinned ref.
- Each bit-identical comparison is within one process; it says nothing about other machines or builds.

## See also

- [6-DOF SCvx landing MPC](scvx-6dof-landing-mpc.md)
- [Game-free guidance checks](game-free-guidance-checks.md)
- [Native conic solver interop](native-conic-solver-interop.md)
- [Guidance flight recorder](guidance-flight-telemetry-csv.md)
- [Run a closed-loop harness with injected dispersions and check the solve is a pure function of its inputs](../../../howto/testing-and-tooling/offline-tests/test-a-solver-or-controller-offline/test-a-solver-or-controller-offline-1.md) - how-to article demonstrating this feature

