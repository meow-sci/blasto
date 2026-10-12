---
type: How-To
title: Check numerics against an independent implementation with console checks and Python reference fixtures
description: Put solver and numerics code in game-free assemblies joined to the mod by project reference, then compare their output with JAX and CVXPY fixtures from console checks that a CI script runs
category: testing-and-tooling
group: offline-tests
topic: unit-test-mod-logic-without-game
approach: 3
difficulty: advanced
prerequisites: [dotnet-sdk, python]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/game-free-guidance-checks
  - mods/ksa-advancedflightcomputer/features/solver-problem-json-replay
techniques: [console-checks, reference-fixtures, msbuild-project-reference]
hooks: []
lifecycle: [event:headless-test-run, thread:background]
game_types: []
tags: [testing, numerics, solver, reference-fixtures, jax, cvxpy, ci]
---

Outcome: thousands of lines of guidance numerics (dual-number Jacobians, convex-subproblem assembly, a landing loop, native solver bindings) are checked in CI on machines with no game, against outputs produced by a different implementation, so a numerical bug cannot hide by being wrong in the same way twice.
Demonstrated by KSA-AdvancedFlightComputer ([tests/AdvancedFlightComputer.Guidance.Tests/Scvx/Program.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/Program.cs#L1-L35)). Prerequisites: a numerics layer that never names a game type and a short Python script per reference.

## Background: how the game does it

The game offers nothing to compare against; the independent implementation is the point. The only engine contact is the adapter that copies vehicle state into plain data before a solve. That copy is also what makes a background solve thread safe (see [Run an expensive solve without stalling the frame](../../../flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/overview.md)), and it is why the solver can be tested with no game: the problem it receives is just numbers.

## Lifecycle and timing

Build and CI time. The code under test runs in the game on a worker thread (`thread:background`) fed from the vehicle's solver-phase hooks; here it runs as an ordinary console process. See the [frame and solver phases](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Split numerics into assemblies with no game reference.** AFC has four (`Numerics`, `Conic`, `Gfold`, `Scvx`) that are plain `net10.0` libraries. The mod project removes their folders from its own compile items and references them as projects, so game-facing adapters sit beside the solvers in the tree but compile separately:

   ```xml
   <!-- AdvancedFlightComputer/AdvancedFlightComputer.csproj L60-L66 -->
     <ItemGroup>
       <Compile Remove="Features\Guidance\Numerics\**\*.cs;Features\Guidance\Conic\**\*.cs;Features\Guidance\Gfold\**\*.cs;Features\Guidance\Scvx\**\*.cs" />
       <ProjectReference Include="Features\Guidance\Numerics\AdvancedFlightComputer.Guidance.Numerics.csproj" />
       <ProjectReference Include="Features\Guidance\Conic\AdvancedFlightComputer.Guidance.Conic.csproj" />
       <ProjectReference Include="Features\Guidance\Gfold\AdvancedFlightComputer.Guidance.Gfold.csproj" />
       <ProjectReference Include="Features\Guidance\Scvx\AdvancedFlightComputer.Guidance.Scvx.csproj" />
     </ItemGroup>
   ```

2. **Write the reference in Python and save fixtures as CSV.** The script states what it validates and why the test points are chosen; JAX supplies every slope by autodiff, so agreement validates the hand-written forward-mode derivatives in C#:

   ```python
   # tests/AdvancedFlightComputer.Guidance.Tests/Scvx/python_ref/dyn_ref.py L1-L16
   # Reference dynamics + Jacobians for validating Scvx.Core/Dynamics6Dof.cs.
   #
   # Model identical to 6dof.py (that file is the source of truth); this one only
   # evaluates f, A = df/dX and B = df/du at a set of test points and writes them
   # for the C# side to diff against. JAX supplies every slope by autodiff, so
   # agreement validates the hand-written forward-mode AD in C#.
   #
   # Test points deliberately span the regimes the SCvx loop actually visits:
   # an identity-attitude hover, a tilted vehicle with body rates (where R(q),
   # q(x)w and w x Iw are all non-trivial), the gimbal at its stop, and pseudo-
   # random states — the last catching sign errors a symmetric case would hide.
   #
   # Usage:  python dyn_ref.py            (writes dyn_ref.csv next to this file)

   import os
   import numpy as np
   ```

3. **Write a console checker that reads the fixture and diffs.** One executable takes flags per check: the header comment documents usage, and each flag returns an exit code. The finite-difference flag is a second independent cross-check that cannot share a seeding bug with the autodiff path:

   ```csharp
   // tests/AdvancedFlightComputer.Guidance.Tests/Scvx/Program.cs L1-L12
   using System.Globalization;
   using AdvancedFlightComputer.Guidance.Conic;
   using AdvancedFlightComputer.Guidance.Scvx.SixDof;

   // Validates Scvx.Core's forward-mode AD against JAX.
   //
   // python_ref/dyn_ref.py evaluates the same dynamics with jax.jacobian at a set
   // of test points and writes f, A and B per case; this diffs the C# results
   // against them. Agreement to machine precision means the hand-written
   // derivative rules and the state/control layout are both right.
   //
   //   usage: Scvx.Console [path/to/dyn_ref.csv] [--verbose]
   ```
   ```csharp
   // tests/AdvancedFlightComputer.Guidance.Tests/Scvx/Program.cs L296-L329
       }
       cases++;
   }

   // Independent cross-check: central finite differences against the same F().
   // This validates the derivative rules a second way (it cannot share a seeding
   // bug with the AD path) and measures what accuracy FD actually delivers here -
   // directly relevant to bolting on a black-box aero model later, where AD isn't
   // available and FD is the fallback.
   if (args.Contains("--fd"))
   {
       Console.WriteLine();
       Console.WriteLine("AD vs central finite differences (same C# F):");
       double worstFd = 0;
       int fdCase = 0;
       foreach (string line in File.ReadLines(csv))
       {
           if (line.Length == 0 || line[0] == '#') continue;
           double[] vals = line.Split(',')
               .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
           double[] x = vals[0..NX];
           double[] u = vals[NX..(NX + NU)];

           double[] A = new double[NX * NX], B = new double[NX * NU];
           Dynamics6Dof.Jacobian(x, u, p, [], A, B);
           double[] Afd = FdStateJacobian(x, u, p);

           double scale = A.Max(Math.Abs);
           double worst = 0;
           for (int i = 0; i < A.Length; i++)
               worst = Math.Max(worst, Math.Abs(A[i] - Afd[i]) / scale);
           worstFd = Math.Max(worstFd, worst);
           Console.WriteLine($"  case {fdCase}: max rel diff {worst:E2}");
           fdCase++;
   ```

4. **Check whole subproblems and loops the same way.** `--sub-scs` solves the same SCvx subproblem that `python_ref/sub_ref.py` solved with CVXPY and diffs the trajectory; `--loop` runs the full loop against `loop_ref.py`. G-FOLD has its own console with a reference descent (`81 120`), a Clarabel smoke test, a real-time budget run and acceleration-limit checks, with `gfold_ref.py` as reference.

5. **Link single game-facing files into tiny executables.** The one adapter that has a concurrency contract is compiled into a test exe by link and checked for one winner among concurrent dispatches, no late results from a disposed worker, and a completed result that waits for collection:

   ```xml
   <!-- tests/AdvancedFlightComputer.Guidance.Tests/Worker.Tests/Worker.Tests.csproj L1-L10 -->
   <Project Sdk="Microsoft.NET.Sdk">
     <PropertyGroup>
       <OutputType>Exe</OutputType>
       <TargetFramework>net10.0</TargetFramework>
       <ImplicitUsings>enable</ImplicitUsings>
       <Nullable>disable</Nullable>
     </PropertyGroup>
     <ItemGroup>
       <Compile Include="../../../AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSolveWorker.cs" Link="Ksa6DofSolveWorker.cs" />
     </ItemGroup>
   ```

6. **Run every check from one CI script that stops on the first failure.** It restores against lock files, builds with `--no-restore`, runs each flag as a separate process and treats a non-zero exit as failure:

   ```powershell
   # build/ci/test-guidance.ps1 L153-L167
   foreach ($command in @(@("81", "120"), @("--clarabel-smoke"), @("--realtime"), @("--terminal-braking"), @("--accel-limit"))) {
       Invoke-Checked -FilePath "dotnet" -ArgumentList ($gfoldBase + $command) -FailureMessage "The Gfold $($command -join ' ') check failed."
   }

   $scvxProject = Join-Path $RepositoryRoot $projects.ScvxConsole
   $scvxBase = @(
       "run", "--no-build",
       "--project", $scvxProject,
       "--configuration", $Configuration,
       "--runtime", $RuntimeIdentifier,
       "--"
   )
   foreach ($command in @("--fd", "--aero", "--impact", "--sub-scs", "--loop", "--ascent", "--ascent-glimit", "--cold-shift", "--hermite")) {
       Invoke-Checked -FilePath "dotnet" -ArgumentList ($scvxBase + $command) -FailureMessage "The Scvx $command check failed."
   }
   ```

7. **Round-trip the problem file.** Because the serialized problem type lives on the game-free side, the mod and the offline console share it (see [Test a solver or controller offline](../test-a-solver-or-controller-offline/overview.md)).

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/AdvancedFlightComputer.csproj](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/AdvancedFlightComputer.csproj#L60-L66) | Excludes the numerics folders and references them as projects |
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/Program.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/Program.cs) | SCvx console checks |
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/python_ref/dyn_ref.py](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/python_ref/dyn_ref.py) | JAX reference for the dynamics Jacobians |
| [tests/AdvancedFlightComputer.Guidance.Tests/Gfold/python_ref/gfold_ref.py](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Gfold/python_ref/gfold_ref.py) | G-FOLD reference |
| [tests/AdvancedFlightComputer.Guidance.Tests/Worker.Tests/Program.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Worker.Tests/Program.cs) | Concurrency contract of the worker adapter |
| [build/ci/test-guidance.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/ci/test-guidance.ps1) | Local and CI runner |

## Variations and gotchas

- **Fixtures are checked in.** Regenerating them needs Python with JAX or CVXPY; CI only reads the CSV, so it needs neither.
- **Native solvers need the right platform.** Only the `Conic` assembly calls native code (Clarabel, SCS), so the script refuses to run for a runtime identifier other than the host's and copies the natives beside each console.
- **Tolerances state a claim.** "Agreement to machine precision" for autodiff against JAX, a looser bound for finite differences; write the tolerance with the reason.
- **Locked restore.** Restoring once without `--runtime` and then building with `--no-restore --runtime` avoids lock-file mismatches (NU1004), as the script's comments explain.
- **Game updates.** None of this depends on a KSA build; only the adapter that fills the problem does.

## See also

- [Overview](overview.md); [approach 2](unit-test-mod-logic-without-game-2.md), [approach 4](unit-test-mod-logic-without-game-4.md)
- [Test a solver or controller offline](../test-a-solver-or-controller-offline/overview.md), [Run one check script from the pre-push hook and from CI](../run-repo-checks-from-a-pre-push-hook-and-ci/overview.md)
- Mod features: [Game-free guidance checks](../../../../mods/ksa-advancedflightcomputer/features/game-free-guidance-checks.md), [Solver problem JSON replay](../../../../mods/ksa-advancedflightcomputer/features/solver-problem-json-replay.md)
