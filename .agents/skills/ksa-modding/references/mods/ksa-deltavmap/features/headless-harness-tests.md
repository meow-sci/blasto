---
type: Mod Feature
title: Headless harness tests that run against the real game simulation
description: A test-only StarMap mod depending on HeadlessHarness and DeltaVMap runs IHarnessTest classes on the live game and compares the mod's results with stock code, including private planner methods via reflection
mod: ksa-deltavmap
category: testing-and-tooling
techniques: [headless-harness, reflection, inter-mod-api, starmap-mod-dependencies]
hooks:
  - reflection KSA.TransferPlanner.PlanInterstellarFuel
  - reflection KSA.TransferPlanner.InterstellarFuelInputs
  - reflection KSA.TransferPlanner.MassAboard
  - reflection KSA.TransferPlanner.TankSpace
lifecycle: []
game_types:
  - KSA.TransferPlanner
  - KSA.SequencePerformanceList
  - KSA.PartTree
  - KSA.InterstellarBurnPlan
  - KSA.InterstellarDrive
  - KSA.Universe
  - KSA.CelestialSystem
  - KSA.Program
tags: [testing, headless, harness, starmap, reflection, regression, game-update]
---

The mod ships a second, test-only StarMap mod that never runs in normal play. When the
HeadlessHarness mod boots the real game without a GPU, it loads this assembly, discovers its
`IHarnessTest` classes and runs them against the live simulation: the real `CelestialSystem`, real
vehicles spawned from saves, and the game's own planners as oracles. Mechanism: `mod.toml` declares
non-optional `[[StarMap.ModDependencies]]` on `HeadlessHarness` (which marks the folder as a
harness consumer) and on `DeltaVMap` (the mod under test); the DLL holds an empty `[StarMapMod]`
class plus the tests; `DeltaVMap.csproj` exposes internals with
`<InternalsVisibleTo Include="DeltaVMap.HarnessTests" />`.[^harness-modtoml][^harness-testmod][^dvmap-csproj]

HeadlessHarness itself is not part of any checkout analyzed here. Everything below about its API
is inferred from how the consumer code uses it.

## How it works

**Packaging.** `DeltaVMap.HarnessTests.csproj` references `DeltaVMap.csproj`, `HeadlessHarness.dll`
(HintPath `..\..\HeadlessHarness\HeadlessHarness\bin\$(Configuration)\net10.0\`, a sibling checkout
built in the same configuration), `KSA.dll` and `Brutal.Core.Numerics.dll`, all with
`Private=false`: nothing is copied, everything resolves at runtime through StarMap's load context
and the harness's mods-folder resolution. It links `..\DeltaVMap\Layout\SceneChecks.cs` as a source
file (the mod removes that file from its own build with `<Compile Remove>`), imports the shared
`build/ModDeploy.targets` so it deploys into the game's mods folder, and sets
`GenerateLaunchProfile=false` because the suite is launched by `scripts/run-headless.ps1`, a script
the csproj comment names but which is not in this repository (it belongs to HeadlessHarness).[^harness-csproj]

**Harness surface as used by the tests** (all from namespaces `HeadlessHarness.Core` and
`HeadlessHarness.Harness`):

| Member | Use in the tests |
|---|---|
| `IHarnessTest` with `string Name` and `int Run(HeadlessSession session)` | One class per test; return 0 pass, 1 fail |
| `bool OptIn => true` | `SceneDumpTest` opts out of default runs (property on the interface, unverified) |
| `HeadlessSession.System` | The loaded `CelestialSystem` |
| `HarnessLog.Line(string)` | Tagged result lines (`[dvmap-...] PASS/FAIL/SKIP`) |
| `TestSupport.Verdict`, `VehicleEnvVar`, `CollectVehicleIds`, `DespawnNewVehicles` | Verdict text, the env var naming a test save, vehicle cleanup |
| `VehicleSpawner.CircularCci`, `VehicleSpawner.SpawnFromSave` | Spawn a vehicle from a save into a circular orbit (type not in the KSA decomp, so presumably HeadlessHarness; unverified) |

Discovery is by interface, so there is nothing to register. The author's
KSA-AdvancedFlightComputer repository uses the same pattern (identical `mod.toml` shape) and its
harness README names the harness repository, states that it "brings the real game up GPU-free and
runs plug-in tests against the live simulation", that `run-headless.ps1` lives in the harness, that
`-Tests` filters on exact `Name` values, and that the harness classifies `MissingMemberException` /
`TypeLoadException` as game-API drift.[^afc-harness-readme]

**What the DeltaVMap tests assert:**

| Test (`Name`) | Asserts |
|---|---|
| `dvmap-multistar-graph` | One forest root per `IIndependentRoot`, same count as `Universe.Roots`; every orbiting celestial is in the tree `IIndependentRoot.RootOf` names; stars and barycenters have a parking orbit and no surface; one `Interstellar` edge per other system from the home hub with unique node Ids; with the stock interstellar bodies loaded, root types, order, Alpha Centauri's three stars and mass, exoplanets, distances and capture speeds pinned to literal values. Logs SKIP for the multi-star part on a single-root system[^harness-multistar] |
| `dvmap-staged-dv` | Needs a save named by `TestSupport.VehicleEnvVar`. Spawns it, sets `Program.ControlledVehicle`, calls `PartTree.FlushDirtyDerived()` "standing in for Program.PrepareFrame", checks a flight read waits after `MarkDerivedDirty(DerivedData.Sequences)`, matches the total against a fresh `SequencePerformanceList.RecomputeForFlight(0f).TotalDeltaV`, checks the first sequence's start mass weighs the whole vehicle, and despawns in `finally`[^harness-stageddv][^ksa-parttree] |
| `dvmap-interstellar-fuel` | Calls the private `TransferPlanner.PlanInterstellarFuel` with the private nested `InterstellarFuelInputs` through reflection (`GetNestedType`, `GetMethod`, `Activator.CreateInstance`) at 0.001, 0.01 and 0.1 c and requires `InterstellarFuel.Plan` to produce exactly equal burns, coast and scooped hydrogen; compares `InterstellarFuel.ReadPhase` bit for bit with the private `MassAboard` / `TankSpace`[^harness-fuel][^ksa-transferplanner] |
| `dvmap-stock-planner` | Every private `TransferPlanner` key `StockPlannerBridge` binds resolves; the plan-type and destination writes read back correctly and `Restore` puts stock back exactly; the full button path on a second root[^harness-planner] |
| `dvmap-scene-dump` (opt-in) | Composes the real scene in all three layout modes, collapsed and with the nearest system opened, holds it to `SceneChecks` at 1600x1000 and 1300x860, and writes text and fit-preview SVGs to `%TEMP%\dvmap-scene`[^harness-scene] |

The pattern worth copying: wherever the mod re-implements or feeds a private game routine, a
harness test calls the private routine through reflection with the same inputs and demands equality,
so a game update that changes the routine fails a test instead of silently drifting.

## Lifecycle and timing

- The test mod declares `[StarMapMod]` but no lifecycle methods, so on a normal launch it does
  nothing and attaches to no frame point.[^harness-testmod] `hooks` lists only the private stock
  members the tests reach by reflection.
- When and on which thread HeadlessHarness calls `IHarnessTest.Run` is decided by the harness and is
  not visible in this repository (unverified). What the tests show: there is no frame loop
  (`StagedDvTest` calls `PartTree.FlushDirtyDerived()` itself because "this headless run has no
  frame loop"), so tests drive the main-thread steps they depend on by hand, and `Universe.Roots`,
  `Universe.GetElapsedTime()` and `Program.ControlledVehicle` are populated as in a loaded game.
  `lifecycle` is therefore empty: the tests run outside `Program.OnFrame`, on whatever thread the
  harness uses, not on a thread the mod owns.
- Choose the universe per run: the multi-star checks need `-System SolSystemInterstellar`
  (or the `KSA_HEADLESS_SYSTEM` environment variable named in the SKIP message).

## Key code

| Path | Role |
|---|---|
| [DeltaVMap.HarnessTests/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.HarnessTests/mod.toml) | Harness consumer manifest with the two dependencies |
| [DeltaVMap.HarnessTests/DeltaVMap.HarnessTests.csproj](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.HarnessTests/DeltaVMap.HarnessTests.csproj) | Private=false references, linked SceneChecks, deploy import |
| [DeltaVMap.HarnessTests/TestMod.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.HarnessTests/TestMod.cs) | Empty `[StarMapMod]` |
| [DeltaVMap.HarnessTests/MultiStarGraphTest.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.HarnessTests/MultiStarGraphTest.cs) | Forest and interstellar checks on the real universe |
| [DeltaVMap.HarnessTests/StagedDvTest.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.HarnessTests/StagedDvTest.cs) | Staged delta-v seam against stock |
| [DeltaVMap.HarnessTests/InterstellarFuelTest.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.HarnessTests/InterstellarFuelTest.cs) | Equality with the private stock fuel planner |
| [DeltaVMap.HarnessTests/StockPlannerKeysTest.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.HarnessTests/StockPlannerKeysTest.cs) | Private-key presence and round trip |
| [DeltaVMap.HarnessTests/SceneDumpTest.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.HarnessTests/SceneDumpTest.cs) | Opt-in real-scene layout dump |

## Snippets

```toml
# DeltaVMap.HarnessTests/mod.toml L1-L15
name = "DeltaVMap.HarnessTests"

[StarMap]
EntryAssembly = "DeltaVMap.HarnessTests"

# The HeadlessHarness dependency marks this folder as a harness consumer, so the harness loads this
# assembly and discovers its IHarnessTest classes during a headless run. DeltaVMap is the mod under
# test.
[[StarMap.ModDependencies]]
ModId = "HeadlessHarness"
Optional = false

[[StarMap.ModDependencies]]
ModId = "DeltaVMap"
Optional = false
```

```csharp
// DeltaVMap.HarnessTests/InterstellarFuelTest.cs L43-L87
public int Run(HeadlessSession session)
{
    bool ok = true;
    try
    {
        Type planner = typeof(TransferPlanner);
        Type? inputsType = planner.GetNestedType("InterstellarFuelInputs", BindingFlags.NonPublic);
        MethodInfo? plan = planner.GetMethod("PlanInterstellarFuel", BindingFlags.NonPublic | BindingFlags.Static);
        if (inputsType == null || plan == null)
        {
            HarnessLog.Line($"{Tag} FAIL: TransferPlanner.InterstellarFuelInputs or PlanInterstellarFuel not found; re-verify the stock planner.");
            return 1;
        }
        // ...
        foreach (double speedC in new[] { 0.001, 0.01, 0.1 })
        {
            var inputs = new DriveFuelInputs(drive, Mass, HydrogenAboard, speedC * C,
                0.3 * AntimatterAboard, 0.3 * AntimatterAboard, FinalSpeed, gas, Distance, HydrogenSpace);
            // ...
            DriveFuelPlan ours = InterstellarFuel.Plan(in inputs);
            // ...
            object stockInputs = Activator.CreateInstance(inputsType, drive, inputs.Mass, inputs.HydrogenAboard, inputs.CruiseSpeed,
                inputs.DepartureAntimatter, inputs.BrakeAntimatter, inputs.FinalSpeed, inputs.GasDensity, inputs.Distance,
                inputs.HydrogenSpace)!;
            object stock = plan.Invoke(null, new[] { stockInputs })!;

            var departure = (InterstellarBurnEstimate)Read(stock, "Departure");
            var brake = (InterstellarBurnEstimate)Read(stock, "Brake");
            double coast = (double)Read(stock, "CoastSeconds");
            // ...
            ok &= Check($"speed-up burn matches stock at {at}", ours.Departure == departure);
            ok &= Check($"brake burn matches stock at {at}", ours.Brake == brake);
            ok &= Check($"coast matches stock at {at}", ours.CoastSeconds == coast);
            ok &= Check($"scooped hydrogen matches stock at {at}", ours.Scooped == scooped);
```

```csharp
// DeltaVMap.HarnessTests/StagedDvTest.cs L44-L51
Orbit orbit = VehicleSpawner.CircularCci(home, body.MeanRadius + 500_000.0, Universe.GetElapsedTime());
Vehicle vehicle = VehicleSpawner.SpawnFromSave(saveId, system, home, "DvMapStagedDvTest", orbit);
Program.ControlledVehicle = vehicle;
// Stands in for Program.PrepareFrame, which this headless run has no frame loop for.
PartTree.FlushDirtyDerived();
StagedDv.Reset();

ok &= CheckWaitsForDerivedFlush(vehicle);
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `TransferPlanner.PlanInterstellarFuel`, `InterstellarFuelInputs`, `MassAboard`, `TankSpace` (private) | [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs) | Oracles for the fuel estimate |
| `PartTree.FlushDirtyDerived()`, `PartTree.MarkDerivedDirty(DerivedData)` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs) | Reproduce the `PrepareFrame` flush and a dirty tree |
| `SequencePerformanceList.RecomputeForFlight`, `TotalDeltaV`, `PerformanceSequences` | (KSA.SequencePerformanceList) | Stock staged delta-v oracle |
| `Universe.Roots`, `IIndependentRoot.RootOf`, `CelestialSystem.All`, `InterstellarGasDensity` | (KSA) | Forest and fuel inputs |

## Pitfalls and version notes

- The test DLL must never ship enabled for players; it is a separate mod folder and the release zip
  (`PackageRelease`) contains only `DeltaVMap.dll` and its `mod.toml`.
- Tests that spawn vehicles must despawn them in `finally` (`TestSupport.DespawnNewVehicles`) and
  reset `Program.ControlledVehicle`, or they leak into later tests.
- A single-root universe silently covers less; the tests log an explicit SKIP naming the system to
  run with instead of passing vacuously.
- The fuel test compares with `==` on records of doubles: the mod must feed the stock routine in
  exactly the same order with the same float-evaluated inputs, which is why `ReadPhase` mirrors
  stock's float arithmetic.

## See also

- [Offline layout and math tests](./offline-layout-and-math-tests.md)
- [Stock Transfer Planner bridge](./stock-transfer-planner-bridge.md)
- [Vehicle staged delta-v readout](./vehicle-staged-dv-readout.md)
- [Interstellar routes and fuel](./interstellar-routes-and-fuel.md)
- [Mod deploy targets and release](./mod-deploy-targets-and-release.md)
- [Demand equality with the game's private routines using a small oracle suite](../../../howto/testing-and-tooling/offline-tests/headless-test-harness-for-game-mods/headless-test-harness-for-game-mods-2.md) - how-to article demonstrating this feature

[^harness-modtoml]: DeltaVMap.HarnessTests/mod.toml
[^harness-csproj]: DeltaVMap.HarnessTests/DeltaVMap.HarnessTests.csproj
[^harness-testmod]: DeltaVMap.HarnessTests/TestMod.cs
[^harness-multistar]: DeltaVMap.HarnessTests/MultiStarGraphTest.cs
[^harness-stageddv]: DeltaVMap.HarnessTests/StagedDvTest.cs
[^harness-fuel]: DeltaVMap.HarnessTests/InterstellarFuelTest.cs
[^harness-planner]: DeltaVMap.HarnessTests/StockPlannerKeysTest.cs
[^harness-scene]: DeltaVMap.HarnessTests/SceneDumpTest.cs
[^dvmap-csproj]: DeltaVMap/DeltaVMap.csproj
[^afc-harness-readme]: KSA-AdvancedFlightComputer tests/AdvancedFlightComputer.HarnessTests/README.md
[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
