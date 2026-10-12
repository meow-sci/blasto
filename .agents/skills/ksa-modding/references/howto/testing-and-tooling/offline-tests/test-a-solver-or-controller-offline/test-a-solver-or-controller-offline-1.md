---
type: How-To
title: Run a closed-loop harness with injected dispersions and check the solve is a pure function of its inputs
description: Fly the real receding-horizon loop on integrated dynamics with seeded thrust and gimbal errors, reduce each run to a few metrics, and prove determinism, input sensitivity and no cross-talk before moving a solve to a worker thread
category: testing-and-tooling
group: offline-tests
topic: test-a-solver-or-controller-offline
approach: 1
difficulty: advanced
prerequisites: [dotnet-sdk, game-free-solver]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/closed-loop-mpc-harness]
techniques: [console-checks, closed-loop-simulation, determinism-tests]
hooks: []
lifecycle: [thread:background, frame:dispatch-solvers]
game_types: [KSA.Vehicle]
tags: [testing, guidance, mpc, closed-loop, determinism, threading, solver]
---

Outcome: a console command that flies a guidance solver in closed loop (solve, fly the plan through integrated dynamics with injected errors, re-solve from where the vehicle really is) and prints a handful of numbers per run: miss distance, path ratio, plan jump, away-from-target distance, worst solver iterations and milliseconds. Two smaller commands establish the properties needed before the solve can move to a worker thread.
Demonstrated by KSA-AdvancedFlightComputer's 6-DOF landing guidance ([tests/AdvancedFlightComputer.Guidance.Tests/Scvx/McpHarness.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/McpHarness.cs#L329-L505)); the dev notes call the harness "the only thing that reproduced flight symptoms". Prerequisites: a solver in a game-free assembly (see [Check numerics against an independent implementation](../unit-test-mod-logic-without-game/unit-test-mod-logic-without-game-3.md)).

## Background: how the game does it

A single solve checked against a reference cannot show symptoms that only a loop produces: plans jumping between re-solves, the vehicle flying long curves instead of heading for the pad, solve times creeping up. In the game the loop is served from the vehicle step: the mod dispatches work in a prefix on `Vehicle.PrepareWorker(SimStep)`, called per vehicle on the main thread from `Universe.ExecuteNextVehicleSolvers` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2148)), and the solve itself runs on its own thread (`afc-6dof-solve`). The harness has no game: it replaces the vehicle with an integrator and the step with a cadence.

## Lifecycle and timing

None in the game frame: the harness runs on a developer machine against the same game-free solver assembly the mod ships. It protects in-game timing. The solve runs on a background thread (`thread:background`) and the step that dispatches it is at `frame:dispatch-solvers`, the last settled point before the next physics step; the determinism checks below are the evidence that moving the solve off the main thread leaves the answer unchanged. See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Take the scenario from a fixture, then disturb it on purpose.** Start, target and node count come from the reference CSV; the start is disturbed (8 and 6 m/s off-plane, 12 degrees of tilt, small body rates), which is what a real engage looks like. Several named weight and scaling sets are flown, including the shipped configuration.

2. **Cold-solve from a straight-line seed with hover thrust**, then set up the loop's bookkeeping and a fixed-seed random generator so runs repeat:

   ```csharp
   // tests/AdvancedFlightComputer.Guidance.Tests/Scvx/McpHarness.cs L348-L368
           // Cold solve.
           var xSeed = new double[n * NX];
           var uSeed = new double[n * NU];
           double m0 = x0[13];
           for (int k = 0; k < n; k++)
           {
               double t = (double)k / (n - 1);
               for (int i = 0; i < 3; i++)
               {
                   xSeed[k * NX + i] = x0[i] + t * (xf[i] - x0[i]);
                   xSeed[k * NX + 3 + i] = x0[3 + i] + t * (xf[3 + i] - x0[3 + i]);
               }
               for (int i = 0; i < 4; i++)
                   xSeed[k * NX + 6 + i] = x0[6 + i] * (1 - t) + xf[6 + i] * t;
               Normalise(xSeed, k);
               for (int i = 0; i < 3; i++) xSeed[k * NX + 10 + i] = x0[10 + i] * (1 - t);
               xSeed[k * NX + 13] = m0 * (1.0 - 0.08 * t);
               uSeed[k * NU + 2] = 1.05 * m0 * Math.Abs(dyn.Gz);
           }
           Array.Copy(x0, 0, xSeed, 0, NX);
           solver.Initialize(x0, xf, xSeed, uSeed, sigmaSeed);
   ```

   ```csharp
   // tests/AdvancedFlightComputer.Guidance.Tests/Scvx/McpHarness.cs L375-L397
           double[] plan = (double[])solver.ReferenceX.Clone();
           double[] planU = (double[])solver.ReferenceU.Clone();
           double sigma = solver.Sigma;

           var state = (double[])x0.Clone();
           var rng = new Random(4242);
           double totalMs = 0, pathLength = 0, planJump = 0, awayFromTarget = 0;
           double prevRange = Dist((state[0], state[1], state[2]), (xf[0], xf[1], xf[2]));
           long totalAdmm = 0;
           int worstAdmm = 0;
           double worstMs = 0;
           int cycles = 0;
           (double, double, double) prevPos = (state[0], state[1], state[2]);

           double simTime = 0.0;
           // Run until the burn is actually DONE. The previous cap of 60 cycles ended the
           // run at ~9.9 s of a ~12 s burn, so the reported "miss" was simply where the
           // vehicle happened to be part-way through - a harness artifact, not guidance.
           double budgetTime = sigma * 2.0;
           while (simTime < budgetTime && state[2] > xf[2] + 1.0 && cycles < 400)
           {
               double dtNode = sigma / (n - 1);
               double step = 0.4 * dtNode;                       // the mod's cadence
   ```

3. **Apply the plan's control through the true dynamics with injected errors.** Control varies linearly between nodes (trapezoidal collocation assumes that; holding node 0's value is a different control law and showed up as "plan jump" that had nothing to do with the solver). Thrust and gimbal errors are scaled by a `Dispersion` knob:

   ```csharp
   // tests/AdvancedFlightComputer.Guidance.Tests/Scvx/McpHarness.cs L404-L423
               double thrustErr = 1.0 + (rng.NextDouble() - 0.5) * 0.06 * Dispersion;
               double txErr = 1.0 + (rng.NextDouble() - 0.5) * 0.04 * Dispersion;
               double tyErr = 1.0 + (rng.NextDouble() - 0.5) * 0.04 * Dispersion;

               const int Sub = 8;
               var u = new double[NU];
               for (int sub = 0; sub < Sub; sub++)
               {
                   double tau = (sub + 0.5) / Sub * step;        // mid-substep along the plan
                   double sNode = Math.Clamp(tau / dtNode, 0.0, n - 1.001);
                   int ka = (int)sNode;
                   int kb = Math.Min(ka + 1, n - 1);
                   double fr = sNode - ka;
                   for (int j = 0; j < NU; j++)
                       u[j] = planU[ka * NU + j] * (1 - fr) + planU[kb * NU + j] * fr;
                   u[2] *= thrustErr;
                   u[0] *= txErr;
                   u[1] *= tyErr;
                   Integrate(state, u, dyn, step / Sub);
               }
   ```

4. **Re-solve from where the vehicle actually is**: shift the old plan by whole nodes, set node 0 to the measured state, reseed with a small trust region and solve under a stopwatch:

   ```csharp
   // tests/AdvancedFlightComputer.Guidance.Tests/Scvx/McpHarness.cs L435-L463
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
               if (c.FixedSigma)
               {
                   cfg.SigmaMin = newSigma * (1 - 1e-6);
                   cfg.SigmaMax = newSigma * (1 + 1e-6);
               }
               solver.Reseed(state, xs, us, newSigma, trustRegion: 0.05);

               var sw = System.Diagnostics.Stopwatch.StartNew();
               ScvxStatus st = solver.Solve(Budget);
               totalMs += sw.Elapsed.TotalMilliseconds;
               totalAdmm += solver.Trace.Sum(t => t.SolverIterations);
               foreach (ScvxIteration it in solver.Trace)
                   worstAdmm = Math.Max(worstAdmm, it.SolverIterations);
               worstMs = Math.Max(worstMs, sw.Elapsed.TotalMilliseconds);
               if (st is ScvxStatus.Failed or ScvxStatus.TrustRegionCollapsed)
                   break;
   ```

5. **Reduce each run to metrics that separate failure modes.** Miss; path length over straight-line distance (1.0 is direct); mean plan jump, the largest time-aligned position gap between consecutive plans; away-from-target, the summed increases in range, which tells a real loop from a curved but closing descent; worst solver iterations and milliseconds; and a flag when the burn time sits on its min or max bound:

   ```csharp
   // tests/AdvancedFlightComputer.Guidance.Tests/Scvx/McpHarness.cs L465-L476
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

6. **Sweep the knobs through flags.** `--mpc` runs a config sweep and the sub-flags ask one question each: iterations per cycle (`--budget`), truncation handling (`--trunc`), SCS init against solve time at several node counts (`--split`), solve-time tail against tolerance (`--tail`), and Moon against Earth under dynamic similarity (`--grav`). The results set mod constants: the default of 50 nodes cites a zero-dispersion table (plan jump 4.0 m at 20 nodes, 1.3 m at 50).

7. **Prove the solve is a pure function of its inputs before threading it.** Three different properties, each its own check (`--snapshot`): the same problem twice gives a bit-identical plan; changing the dynamics parameters changes the answer (otherwise the replay check is vacuous); and two solvers alive at once, stepped alternately, each match their solo run (the shape of a worker holding one solver while the step builds the next):

   ```csharp
   // tests/AdvancedFlightComputer.Guidance.Tests/Scvx/SnapshotCheck.cs L57-L78
           const double g0 = -9.81;
           const double biased = -9.81 + 3.0;      // as if a 3 m/s^2 bias had been committed

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
   ```

   The file's own comment lists what it cannot cover: that inputs reach the model only at a solve's entry needs KSA types and is checked by inspection (a one-line grep after any change to the adapter).

8. **Hammer the hand-off from a background thread** (`--thread`). The same solve inline and on a thread must be bit-identical, and a producer publishing immutable snapshots whose fields all carry a generation number must never be read torn. The reader drives the loop; an earlier version passed by measuring nothing:

```csharp
// tests/AdvancedFlightComputer.Guidance.Tests/Scvx/ThreadCheck.cs L91-L123
        // THE READER DRIVES. An earlier version had the producer run a fixed number of
        // generations and the reader spin until it finished - and the producer was done
        // before the reader's first iteration, so it made zero observations and passed
        // by measuring nothing. A concurrency check that can trivially observe nothing
        // is worse than none, because it reports success.
        _stopProducing = false;
        _published = null;

        var producer = new System.Threading.Thread(() =>
        {
            int gen = 0;
            while (!_stopProducing)
            {
                gen++;
                var u = new double[8 * NU];
                for (int i = 0; i < u.Length; i++) u[i] = gen;
                _published = new Snapshot(u, gen, gen, 8);
                publishes = gen;
            }
        }) { IsBackground = true };

        producer.Start();
        while (reads < TargetReads)
        {
            Snapshot s = _published;
            if (s == null) continue;
            reads++;
            // Every field of a generation carries the same number. If any disagrees,
            // the reader has seen parts of two different plans.
            if (s.Sigma != s.SolveTime || s.U[0] != s.Sigma || s.U[^1] != s.Sigma)
                tears++;
        }
        _stopProducing = true;
```

## Complete example

| File | Role |
|---|---|
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/McpHarness.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/McpHarness.cs#L329-L505) | The loop and metrics (`MpcHarness.Simulate`; the class is `MpcHarness`, the file is named `McpHarness.cs`) |
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/SnapshotCheck.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/SnapshotCheck.cs) | Replay, input sensitivity, cross-talk |
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/ThreadCheck.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/ThreadCheck.cs#L77-L127) | Off-thread identity and torn-read hammer |
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/python_ref/loop_ref.py](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/python_ref/loop_ref.py) | Reference loop that writes the scenario CSV |
| [build/ci/test-guidance.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/ci/test-guidance.ps1#L165) | Which flags CI runs |

## Variations and gotchas

- **The harness is not the game.** It checks the algorithm's behaviour in a loop; it cannot check that game state reaches the solver correctly. Keep the adapter thin and review it by inspection.
- **Fix the seed.** The error generator is `Random(4242)`; reproducible runs are what make a metric change attributable.
- **Run to the end of the burn.** An earlier cap of 60 cycles ended the run at 9.9 s of a 12 s burn and reported a "miss" that was only where the vehicle happened to be part-way; a harness artefact, not guidance.
- **Test the test.** Every property check needs a companion that proves it can fail: input sensitivity makes the replay check non-vacuous, and the reader-driven loop makes the concurrency check non-vacuous.
- **Game updates.** None of this reads a KSA build; the adapter that fills the problem is the only part exposed to API drift.

## See also

- [Overview](overview.md); [approach 2](test-a-solver-or-controller-offline-2.md)
- [Run an expensive solve without stalling the frame](../../../flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/overview.md), [Plan and fly a powered landing](../../../flight-and-orbits/guidance/plan-and-fly-a-powered-landing/overview.md), [Check numerics against an independent implementation](../unit-test-mod-logic-without-game/unit-test-mod-logic-without-game-3.md)
- Mod feature: [Closed-loop MPC harness](../../../../mods/ksa-advancedflightcomputer/features/closed-loop-mpc-harness.md)
