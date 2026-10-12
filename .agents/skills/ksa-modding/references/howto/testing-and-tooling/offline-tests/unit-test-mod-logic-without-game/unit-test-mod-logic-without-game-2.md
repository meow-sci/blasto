---
type: How-To
title: Check a model with a console test executable that returns a verdict as its exit code
description: Source-link a game-free model file into a plain Exe, assert physical invariants against synthetic inputs and stock reference values, and let the process exit code be the test result
category: testing-and-tooling
group: offline-tests
topic: unit-test-mod-logic-without-game
approach: 2
difficulty: beginner
prerequisites: [dotnet-sdk]
demonstrated_by: [mods/ksaero/features/headless-model-tests, mods/ksa-deltavmap/features/offline-layout-and-math-tests]
techniques: [headless-tests, msbuild-linked-sources, console-checks]
hooks: []
lifecycle: [event:headless-test-run]
game_types: []
tags: [testing, console, invariants, source-link, exit-code, no-framework]
---

Outcome: `dotnet run --project <Mod>.Tests` prints `PASS` or a failure count, returns exit code 0 or 1 and needs no test framework, no game and no StarMap. This is the cheapest option for a small mod: one extra csproj and one `Program.cs`.
Demonstrated by KSAero ([KSAero.Tests/Program.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero.Tests/Program.cs#L4-L23)) and by KSA-DeltaVMap's offline harness ([DeltaVMap.Tests/Program.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.Tests/Program.cs#L118-L139)). Prerequisite: the maths lives in files that name no game type.

## Background: how the game does it

There is no engine hook to test here. The only engine facts are the formulas your model replaces or mirrors; copy them into the test as reference values so a drift in the mod shows up as a disagreement with stock. KSAero's test re-implements the stock box drag (`VehicleProperties.RecomputeAerodynamicProperties` plus the `0.1 * surfaceArea` term of `PhysicsStates.ComputeDrag`, [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1090-L1099) and [current/decomp/KSA/VehicleProperties.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleProperties.cs#L115-L130)) to print side-by-side tables, and DeltaVMap compares transfer-window maths with known stock content values.

## Lifecycle and timing

Development time only. The code under test runs in the game in places that are hard to test in flight: KSAero's model runs per substep in `physics:vehicle-solver` on worker threads, with no UI, which is exactly why a desktop executable is the practical way to look at it. See the [frame and physics phases](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Keep the model file free of game types.** KSAero's `AeroModel.cs` has no `using` directives and calls only `System.Math`; everything that touches KSA (bounding box to cylinder, frames) lives in the Harmony patch file, and the model takes plain doubles.

2. **Create a console project that compiles the model in by link.** Do not reference the mod project: that would drag in `KSA.dll` and friends:

   ```xml
   <!-- KSAero.Tests/KSAero.Tests.csproj L1-L12 -->
   <Project Sdk="Microsoft.NET.Sdk">
     <PropertyGroup>
       <OutputType>Exe</OutputType>
       <TargetFramework>net10.0</TargetFramework>
       <ImplicitUsings>enable</ImplicitUsings>
       <Nullable>enable</Nullable>
     </PropertyGroup>
     <ItemGroup>
       <!-- The model has no game references, so it is compiled in directly and runs without KSA. -->
       <Compile Include="..\KSAero\AeroModel.cs" Link="AeroModel.cs" />
     </ItemGroup>
   </Project>
   ```

3. **Share many pure files when the mod is bigger.** DeltaVMap links whole folders and individual files, and documents why each is pure; the mod removes files from its own build when a harness test compiles them instead:

   ```xml
   <!-- DeltaVMap.Tests/DeltaVMap.Tests.csproj L19-L30 -->
     <ItemGroup>
       <Compile Include="..\DeltaVMap\Layout\*.cs"
                Exclude="..\DeltaVMap\Layout\VisualTreeAdapter.cs;..\DeltaVMap\Layout\LayoutDump.cs" />
       <!-- Shared debug config (pure, no game deps) so the harness defaults to the same
            dump directory as the in-game LayoutDump. -->
       <Compile Include="..\DeltaVMap\Core\DebugConfig.cs" />
       <!-- The shared number formats are pure, so the harness locks the strings every panel,
            badge and tooltip shows. -->
       <Compile Include="..\DeltaVMap\Core\Format.cs" />
       <!-- The closed-form delta-v kernel is pure (no game types), so the harness can lock its
            formulas down directly: the comet velocity-match, the Hohmann reduction it
            generalizes, the intercept split, and the atmospheric landing fraction. -->
   ```

4. **Write a three-line harness and no framework.** A local `Check` counts and prints failures; helper functions build inputs (here, the unit velocity direction for a nose pitched up by alpha and rolled by phi):

   ```csharp
   // KSAero.Tests/Program.cs L4-L23
   CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

   int failures = 0;
   void Check(bool ok, string what)
   {
       if (!ok)
       {
           failures++;
           Console.WriteLine($"FAIL  {what}");
       }
   }

   const double Deg = Math.PI / 180.0;

   // Nose pitched up by alpha above the airspeed, rolled by phi about the body axis.
   AeroCoefficients At(AeroBody body, double alphaDeg, double mach, double rollDeg = 0.0)
   {
       double a = alphaDeg * Deg, p = rollDeg * Deg;
       return AeroModel.Evaluate(in body, Math.Cos(a), Math.Sin(a) * Math.Sin(p), -Math.Sin(a) * Math.Cos(p), mach);
   }
   ```

5. **Assert invariants, not just examples.** Axisymmetry, force decomposition, monotonic drag, continuity and stock reference values over synthetic bodies and sweeps catch what a few point checks miss:

   ```csharp
   // KSAero.Tests/Program.cs L63-L80
               // Axisymmetric: rolling the vehicle changes nothing but the direction.
               for (double roll = 15.0; roll < 360.0; roll += 37.0)
               {
                   var r = At(body, alpha, mach, roll);
                   Check(Math.Abs(r.Lift - c.Lift) < 1e-12 && Math.Abs(r.Drag - c.Drag) < 1e-12, $"{name}: roll changes coefficients at a={alpha} r={roll}");
               }

               // The force vector carries exactly C_L and C_D. Velocity is u, lift axis is e_L.
               double ux = Math.Cos(a), uz = -Math.Sin(a);
               double sign = ux < 0 ? -1.0 : 1.0;
               double cosLead = Math.Abs(ux), sinLead = Math.Abs(uz);
               double ex = sign * sinLead, ez = cosLead;
               double alongU = c.ForceX * ux + c.ForceZ * uz;
               double alongL = c.ForceX * ex + c.ForceZ * ez;
               Check(Math.Abs(alongU + c.Drag) < 1e-9, $"{name}: force along airspeed != -C_D at a={alpha} M={mach}");
               Check(Math.Abs(alongL - c.Lift) < 1e-9, $"{name}: force along lift axis != C_L at a={alpha} M={mach}");
               Check(Math.Abs(c.ForceY) < 1e-12, $"{name}: side force in the pitch plane at a={alpha}");
               Check(c.Drag >= 0.0, $"{name}: negative drag at a={alpha} M={mach}");
   ```

6. **Make the exit code the verdict** so CI and a pre-push hook need no parsing:

   ```csharp
   // KSAero.Tests/Program.cs L154, L180
   Console.WriteLine($"{(failures == 0 ? "PASS" : $"{failures} FAILURES")}");
   // ...
   return failures == 0 ? 0 : 1;
   ```

7. **Use a tolerance helper for numeric comparisons against known values.** DeltaVMap's harness returns 0 or 1 per check and sums them into the process exit code:

   ```csharp
   // DeltaVMap.Tests/Program.cs L1291-L1299
   static int Approx(string name, double actual, double expected, double relTolerance)
   {
       double denom = Math.Max(1.0, Math.Abs(expected));
       if (Math.Abs(actual - expected) / denom <= relTolerance)
           return 0;
       Console.WriteLine($"  FAIL {name}: got {actual}, expected {expected}");
       return 1;
   }
   ```

   Example use, locking a lead angle and a synodic period to independently evaluated numbers:

   ```csharp
   // DeltaVMap.Tests/Program.cs L1190-L1204
       // Lead angle: Earth (inner) to Mars (outer) leads by ~+44 deg; Earth to Venus (inner) by
       // ~-54 deg. The single formula gives the sign from the wrap.
       {
           double mars = TransferWindow.TargetPhaseAngle(aEarth, aMars, false) * rad2deg;
           double venus = TransferWindow.TargetPhaseAngle(aEarth, aVenus, false) * rad2deg;
           fails += Approx("Earth->Mars target phase ~ +44 deg", mars, 44.0, 0.05);
           fails += Approx("Earth->Venus target phase ~ -54 deg", venus, -54.0, 0.05);
       }

       // Synodic period Earth/Mars is ~2.14 yr (sanity, approximate). Compared in Julian years.
       {
           double julianYear = 3.15576e7;
           double synodicYears = TransferWindow.SynodicPeriod(tEarth, tMars, false) / julianYear;
           fails += Approx("Earth/Mars synodic period ~2.14 yr", synodicYears, 2.14, 0.02);
       }
   ```

8. **Run it:** `dotnet run --project KSAero.Tests`; expect `PASS` and the printed coefficient tables, exit code 0.

## Complete example

| File | Role |
|---|---|
| [KSAero.Tests/KSAero.Tests.csproj](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero.Tests/KSAero.Tests.csproj) | Console project, source link only |
| [KSAero.Tests/Program.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero.Tests/Program.cs) | Harness, invariants, tables, exit code |
| [KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs) | The game-free model under test |
| [DeltaVMap.Tests/DeltaVMap.Tests.csproj](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.Tests/DeltaVMap.Tests.csproj) | Linking folders and single pure files |
| [DeltaVMap.Tests/Program.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.Tests/Program.cs) | Layout runner, overlap reports and number checks |

## Variations and gotchas

- **No framework means no test discovery.** You get no per-test isolation, filtering or reports; once a mod has more than a few dozen checks, move to approach 1 or 4.
- **Print before you exit.** A crash and a failed check both exit non-zero, but only a failed `Check` names what broke; keep the `FAIL` lines.
- **Tables are documentation.** KSAero prints the coefficient tables its README quotes; running the executable regenerates them.
- **Keep game types out of linked files.** A single stray `using KSA;` in a linked file breaks the console build, which is the intended alarm.
- **Game updates.** Pure models are immune; keep stock reference values (copied from the decompiled game) next to a comment saying which build they came from.

## See also

- [Overview](overview.md); [approach 1](unit-test-mod-logic-without-game-1.md), [approach 3](unit-test-mod-logic-without-game-3.md)
- Mod features: [KSAero headless model tests](../../../../mods/ksaero/features/headless-model-tests.md), [KSA-DeltaVMap offline layout and math tests](../../../../mods/ksa-deltavmap/features/offline-layout-and-math-tests.md)
