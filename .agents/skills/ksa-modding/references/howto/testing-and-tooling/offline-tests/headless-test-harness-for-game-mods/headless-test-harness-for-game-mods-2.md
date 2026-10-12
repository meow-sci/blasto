---
type: How-To
title: Demand equality with the game's private routines using a small oracle suite
description: Write a few HeadlessHarness tests that call the stock private routines your mod re-implements with the same inputs through reflection, so a game update that changes either side fails a test instead of drifting silently
category: testing-and-tooling
group: offline-tests
topic: headless-test-harness-for-game-mods
approach: 2
difficulty: advanced
prerequisites: [starmap, headlessharness, reflection]
demonstrated_by: [mods/ksa-deltavmap/features/headless-harness-tests]
techniques: [headless-harness, reflection, oracle-tests]
hooks: []
lifecycle: [event:headless-test-run]
game_types: [KSA.TransferPlanner, KSA.SequencePerformanceList, KSA.PartTree, KSA.Universe, KSA.Vehicle]
tags: [testing, headlessharness, oracle, reflection, api-drift, transfer-planner]
---

Outcome: a handful of tests that run the real game headless and fail the day a stock formula or a private key your mod relies on changes. Instead of re-deriving the maths in the test, each test calls the game's own private routine and the mod's copy with the same inputs and demands identical results.
Demonstrated by KSA-DeltaVMap ([DeltaVMap.HarnessTests/InterstellarFuelTest.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.HarnessTests/InterstellarFuelTest.cs#L11-L18)). Prerequisites: the consumer-mod setup from [approach 1](headless-test-harness-for-game-mods-1.md).

## Background: how the game does it

DeltaVMap estimates antimatter fuel for interstellar trips by feeding a public `InterstellarBurnPlan` in the order stock does. The stock routine is private: `TransferPlanner.PlanInterstellarFuel(ref readonly InterstellarFuelInputs)` ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1033)), taking a private record `InterstellarFuelInputs` ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L19)). The propellant reads that feed the estimate come from `TransferPlanner.MassAboard` and `TankSpace` ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1155), [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1299)). A mod that copies this logic has no way to notice a change in the original except to run both.
Staged delta-v comes from the game's own `SequencePerformanceList`; the mod only adds a seam around it, and that seam is what is tested.

## Lifecycle and timing

Test time only, inside a headless run (`event:headless-test-run`). The suite stands in for `Program.PrepareFrame`, which the headless run has no frame loop for: the staged-delta-v test calls `PartTree.FlushDirtyDerived()` itself, the step `PrepareFrame` runs just before it dispatches `Universe.ExecuteNextVehicleSolvers`. See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Set up the consumer mod** as in approach 1: a `mod.toml` with non-optional dependencies on `HeadlessHarness` and `DeltaVMap`, an empty `[StarMapMod]`, `InternalsVisibleTo` from the mod, and references with `Private=false`. DeltaVMap also links one shared source file into the test project and removes it from the mod's own build:

   ```xml
   <!-- DeltaVMap.HarnessTests/DeltaVMap.HarnessTests.csproj L15-L35 -->
     <!--
       HeadlessHarness consumer carrying the DeltaVMap test suite. Game DLLs, the harness, and the mod
       under test all resolve at runtime (StarMap's load context plus the harness's mods-folder
       resolution), so every reference is Private=false and nothing is copied into the output. The
       HeadlessHarness HintPath expects its repo checked out as a sibling of this one and follows
       $(Configuration), so build the harness in the same configuration; adjust the path to your local
       clone.
     -->
     <!-- The scene layout rules, shared with the offline tests and kept out of the mod itself. -->
     <ItemGroup>
       <Compile Include="..\DeltaVMap\Layout\SceneChecks.cs" Link="SceneChecks.cs" />
     </ItemGroup>

     <ItemGroup>
       <ProjectReference Include="..\DeltaVMap\DeltaVMap.csproj">
         <Private>false</Private>
       </ProjectReference>
       <Reference Include="HeadlessHarness">
         <HintPath>..\..\HeadlessHarness\HeadlessHarness\bin\$(Configuration)\net10.0\HeadlessHarness.dll</HintPath>
         <Private>false</Private>
       </Reference>
   ```

   The suite is launched by HeadlessHarness's own `scripts/run-headless.ps1` (named in the csproj; the script belongs to the harness repository), not from the IDE.

2. **Implement `IHarnessTest`.** A test has a `Name` (the filter key) and `Run(HeadlessSession)` returning 0 or 1; `session.System` is the loaded `CelestialSystem`. Discovery is by interface. Write the contract at the top of each test, as this one does:

   ```csharp
   // DeltaVMap.HarnessTests/InterstellarFuelTest.cs L11-L20
   // Locks the antimatter fuel estimate to the game's own interstellar planner. DeltaVMap feeds the
   // public InterstellarBurnPlan in the order TransferPlanner.PlanInterstellarFuel does; that method
   // is private, so this test calls it through reflection with the same inputs and requires an
   // identical result. A game update that changes the planner's sequence (another burn, a different
   // distance split, a new scoop rule) then fails here instead of drifting silently. The drive is
   // synthetic, because InterstellarBurnPlan.Burn reads only its numeric fields. The propellant
   // reads that feed the estimate are checked against the stock sums on a real vessel.
   public sealed class InterstellarFuelTest : IHarnessTest
   {
       private const string Tag = "[dvmap-interstellar-fuel]";
   ```

3. **Resolve the private routine by reflection and fail clearly if it moved.** The not-found branch returns failure with a message that says to re-verify the stock planner:

   ```csharp
   // DeltaVMap.HarnessTests/InterstellarFuelTest.cs L48-L55
               Type planner = typeof(TransferPlanner);
               Type? inputsType = planner.GetNestedType("InterstellarFuelInputs", BindingFlags.NonPublic);
               MethodInfo? plan = planner.GetMethod("PlanInterstellarFuel", BindingFlags.NonPublic | BindingFlags.Static);
               if (inputsType == null || plan == null)
               {
                   HarnessLog.Line($"{Tag} FAIL: TransferPlanner.InterstellarFuelInputs or PlanInterstellarFuel not found; re-verify the stock planner.");
                   return 1;
               }
   ```

4. **Call both implementations with identical inputs and compare exactly.** Three cruise speeds are enough to cover the branches; the stock record is read back by property name, and a missing property throws `MissingMemberException` so the harness reports API drift:

   ```csharp
   // DeltaVMap.HarnessTests/InterstellarFuelTest.cs L64-L87
               foreach (double speedC in new[] { 0.001, 0.01, 0.1 })
               {
                   var inputs = new DriveFuelInputs(drive, Mass, HydrogenAboard, speedC * C,
                       0.3 * AntimatterAboard, 0.3 * AntimatterAboard, FinalSpeed, gas, Distance, HydrogenSpace);

                   var watch = Stopwatch.StartNew();
                   DriveFuelPlan ours = InterstellarFuel.Plan(in inputs);
                   watch.Stop();

                   object stockInputs = Activator.CreateInstance(inputsType, drive, inputs.Mass, inputs.HydrogenAboard, inputs.CruiseSpeed,
                       inputs.DepartureAntimatter, inputs.BrakeAntimatter, inputs.FinalSpeed, inputs.GasDensity, inputs.Distance,
                       inputs.HydrogenSpace)!;
                   object stock = plan.Invoke(null, new[] { stockInputs })!;

                   var departure = (InterstellarBurnEstimate)Read(stock, "Departure");
                   var brake = (InterstellarBurnEstimate)Read(stock, "Brake");
                   double coast = (double)Read(stock, "CoastSeconds");
                   double scooped = (double)Read(stock, "Scooped");

                   string at = FormattableString.Invariant($"{speedC:0.###} c");
                   ok &= Check($"speed-up burn matches stock at {at}", ours.Departure == departure);
                   ok &= Check($"brake burn matches stock at {at}", ours.Brake == brake);
                   ok &= Check($"coast matches stock at {at}", ours.CoastSeconds == coast);
                   ok &= Check($"scooped hydrogen matches stock at {at}", ours.Scooped == scooped);
   ```

   ```csharp
   // DeltaVMap.HarnessTests/InterstellarFuelTest.cs L203-L209
       private static object Read(object record, string property)
       {
           PropertyInfo? info = record.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
           if (info == null)
               throw new MissingMemberException(record.GetType().Name, property);
           return info.GetValue(record)!;
       }
   ```

5. **Test the seam around a stock value, not the value.** For staged delta-v, the number comes from the game's `SequencePerformanceList`; the test checks the readout equals a fresh stock accumulator, that the cache is keyed on the part tree, that a flight read waits for pending derived data, and that stock's start mass still weighs the whole vehicle (so a dropped mass term would make every figure too favourable):

   ```csharp
   // DeltaVMap.HarnessTests/StagedDvTest.cs L11-L19
   // flight read waits for pending derived data, and that stock's start mass still weighs the whole
   // vehicle. The readout adds nothing to stock's masses, so a game update that drops a mass term from
   // the model would make every figure too favourable.
   public sealed class StagedDvTest : IHarnessTest
   {
       private const double DvEqualityTolerance = 0.01;

       // Stock sums the part masses in floats, so its start mass will not match the vehicle total to
       // the last kilogram.
   ```
   ```csharp
   // DeltaVMap.HarnessTests/StagedDvTest.cs L99-L107
           double stockTotal = reference.TotalDeltaV;
           return Check(FormattableString.Invariant(
                   $"total matches stock's own accumulator ({dv:F1} vs {stockTotal:F1} m/s)"),
               Math.Abs(dv - stockTotal) <= Math.Max(DvEqualityTolerance, Math.Abs(stockTotal) * 1e-4));
       }

       // Before anything is jettisoned, the first sequence starts with the whole vehicle. Engines ship
       // as sub-parts, so the save must carry sub-part inert mass for the comparison to catch a model
       // that leaves it out.
   ```

6. **Resolve every private key the mod binds to, then exercise the write path.** The "open stock transfer planner" button preselects a plan through private `TransferPlanner` statics; if one is renamed the button silently degrades to just opening the window. The test fails on any missing key, writes the plan type, reads it back and restores what stock held:

   ```csharp
   // DeltaVMap.HarnessTests/StockPlannerKeysTest.cs L11-L40
   // The OPEN STOCK TRANSFER PLANNER button preselects the interstellar plan through private
   // TransferPlanner statics. A game update that renames or retypes one of them only degrades the
   // button to opening the window, so this test resolves every key and fails on any that is gone.
   // It then writes the plan type and, in a system with several roots, a destination root the way
   // the button does, reads the state back and restores what stock held before. With several roots
   // and a vessel in the system, it also runs the full button path, source included, and checks
   // which departure root lets the destination through.
   public sealed class StockPlannerKeysTest : IHarnessTest
   {
       private const string Tag = "[dvmap-stock-planner]";

       public string Name => "dvmap-stock-planner";

       public int Run(HeadlessSession session)
       {
           bool ok = true;
           try
           {
               List<string> missing = StockPlannerBridge.MissingKeys();
               foreach (string key in missing)
                   HarnessLog.Line($"{Tag} missing key: {key}");
               ok &= Check("every TransferPlanner key resolves", missing.Count == 0);
               if (missing.Count > 0)
               {
                   HarnessLog.Line($"{Tag} {TestSupport.Verdict(false)}");
                   return 1;
               }

               ok &= CheckSelection(null);
   ```

7. **Skip, do not fail, when the environment cannot cover a case** and say what was not covered; despawn what you spawned in `finally`:

   ```csharp
   // DeltaVMap.HarnessTests/StagedDvTest.cs L26-L31, L72-L77
           string? saveId = Environment.GetEnvironmentVariable(TestSupport.VehicleEnvVar);
           if (string.IsNullOrEmpty(saveId))
           {
               HarnessLog.Line($"[dvmap-staged-dv] SKIP: {TestSupport.VehicleEnvVar} not set.");
               return 0;
           }
   // ...
           finally
           {
               Program.ControlledVehicle = null;
               StagedDv.Reset();
               TestSupport.DespawnNewVehicles(system, preexisting);
           }
   ```

## Complete example

| File | Role |
|---|---|
| [DeltaVMap.HarnessTests/DeltaVMap.HarnessTests.csproj](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.HarnessTests/DeltaVMap.HarnessTests.csproj) | Packaging, sibling harness HintPath, linked source |
| [DeltaVMap.HarnessTests/InterstellarFuelTest.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.HarnessTests/InterstellarFuelTest.cs) | Equality with the private fuel planner |
| [DeltaVMap.HarnessTests/StagedDvTest.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.HarnessTests/StagedDvTest.cs) | Seam tests around the stock delta-v accumulator |
| [DeltaVMap.HarnessTests/StockPlannerKeysTest.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.HarnessTests/StockPlannerKeysTest.cs) | Private key resolution and button path |
| [DeltaVMap.HarnessTests/MultiStarGraphTest.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.HarnessTests/MultiStarGraphTest.cs) | Multi-star forest and interstellar legs (skips on single-root systems) |
| [DeltaVMap.HarnessTests/TestMod.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.HarnessTests/TestMod.cs) | Empty `[StarMapMod]` |

## Variations and gotchas

- **HeadlessHarness is external.** Its API surface (`IHarnessTest`, `HeadlessSession`, `HarnessLog`, `TestSupport`, `VehicleSpawner`) is inferred from how this mod uses it; check the harness repository before relying on a member.
- **Coverage depends on the loaded system.** The multi-star part skips with a message on a single-root system and needs the harness started with a multi-star system selected; a green run may not have covered it.
- **Environment-dependent tests.** The staged-dV test skips unless an environment variable names a save; set it in CI or the cover is silently absent.
- **Equality is the point.** Do not loosen it to a tolerance unless stock itself uses floating-point accumulation; the test fails "the day either side drifts" by design.
- **Game updates.** These tests are the first thing to run on a new build; a failure here names the stock routine that changed.

## See also

- [Overview](overview.md); [approach 1](headless-test-harness-for-game-mods-1.md)
- [Test Harmony patches without running the game](../test-harmony-patches-without-the-game/overview.md), [Read staged delta-v from the game](../../../vehicles-and-parts/staging-and-engines/read-staged-delta-v-from-the-game/overview.md)
- Mod feature: [KSA-DeltaVMap headless harness tests](../../../../mods/ksa-deltavmap/features/headless-harness-tests.md)
