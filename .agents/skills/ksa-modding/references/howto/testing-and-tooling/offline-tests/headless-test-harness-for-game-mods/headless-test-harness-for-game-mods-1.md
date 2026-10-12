---
type: How-To
title: Build a consumer mod with its own mod.toml, InternalsVisibleTo and a test base class
description: Ship tests as a second StarMap mod that depends on HeadlessHarness and the mod under test, so the real simulation runs without a GPU, with simulation time substituted for the clock and fixtures that clean up after themselves
category: testing-and-tooling
group: offline-tests
topic: headless-test-harness-for-game-mods
approach: 1
difficulty: advanced
prerequisites: [starmap, headlessharness]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/headless-harness-test-mod
  - mods/ksa-advancedflightcomputer/features/validated-reflection-handles
techniques: [headless-harness, starmap-lifecycle, reflection]
hooks: []
lifecycle: [event:headless-test-run]
game_types: [KSA.Universe, KSA.Vehicle, KSA.CelestialSystem, KSA.PartTree, KSA.FlightComputer]
tags: [testing, headlessharness, iharnesstest, consumer-mod, fixtures, api-drift]
---

Outcome: a second mod, `<Mod>.HarnessTests`, that never ships, does nothing on a normal launch, and when HeadlessHarness runs the game without a GPU, finds your `IHarnessTest` classes and runs them against the real universe, vehicles, part trees and planners. KSA-AdvancedFlightComputer runs 72 named tests this way.
Demonstrated by KSA-AdvancedFlightComputer ([tests/AdvancedFlightComputer.HarnessTests/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/mod.toml)). Prerequisites: StarMap (see [Create a minimal StarMap mod](../../../getting-started/loader/create-a-minimal-starmap-mod/overview.md)) and a checkout of HeadlessHarness, an external mod whose source is not part of this corpus: its contract is described here only as far as the consumer mods use it.

## Background: how the game does it

The harness drives the actual game classes, so tests assert against the engine itself. Three engine facts shape the tests. `Universe.GetElapsedSeconds()` is the simulation clock ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2393-L2396)); the harness steps it far faster than real time. `Program.PrepareFrame` runs `FlightComputer.RaisePendingAlerts` for each vehicle in frame ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2188-L2195)), and since the harness does not run `PrepareFrame`, its `SimDriver.Step` has to imitate that order or the burn-removal checks fail. And `Vehicle.CreateVehicle(system, body2Cce, bodyRates, parent, id, root, orbit)` builds a vehicle from a part root ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1426)); a spawned vehicle keeps ticking until it is despawned.

## Lifecycle and timing

The test mod loads like any StarMap mod and registers no hook, so it does no `startup:*` work of its own. During a headless run the harness discovers the `IHarnessTest` classes, drives the session and steps the simulation itself (tests obtain a `SimDriver` with `Session.CreateDriver()` and step it), with the universe loaded; this corpus labels that moment `event:headless-test-run`. Which thread the tests run on is the harness's business and is not documented by the consumer mods, so write tests that touch game state only through the session and driver. Features must not depend on caches that `Program.PrepareFrame` refreshes (AFC enumerates `Universe.CurrentSystem.All` rather than `Program.VehiclesInFrame` partly for this reason). See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Write a manifest that depends on the harness and the mod under test.** The `HeadlessHarness` dependency is what marks the folder as a harness consumer:

   ```toml
   # tests/AdvancedFlightComputer.HarnessTests/mod.toml L1-L15
   name = "AdvancedFlightComputer.HarnessTests"

   [StarMap]
   EntryAssembly = "AdvancedFlightComputer.HarnessTests"

   # The HeadlessHarness dependency marks this folder as a harness consumer, so the harness loads this
   # assembly and discovers its IHarnessTest classes during a headless run. AdvancedFlightComputer is
   # the mod under test.
   [[StarMap.ModDependencies]]
   ModId = "HeadlessHarness"
   Optional = false

   [[StarMap.ModDependencies]]
   ModId = "AdvancedFlightComputer"
   Optional = false
   ```

2. **Add an empty `[StarMapMod]` class** so the deployed folder is a valid mod install:

   ```csharp
   // tests/AdvancedFlightComputer.HarnessTests/TestMod.cs L5-L11
   // Marks the assembly as a StarMap mod so the deployed folder is a valid mod install. No lifecycle
   // hooks: the DLL only carries IHarnessTest classes, which HeadlessHarness loads and runs itself
   // during a headless run; on a normal (GPU) launch this mod does nothing.
   [StarMapMod]
   public sealed class TestMod
   {
   }
   ```

3. **Let the test assembly see the mod's internals** with `InternalsVisibleTo` in the mod under test:

   ```xml
   <!-- AdvancedFlightComputer/AdvancedFlightComputer.csproj L68-L72 -->
     <!-- The headless test mod asserts against internal feature code (OrbitManeuvers). -->
     <ItemGroup>
       <InternalsVisibleTo Include="AdvancedFlightComputer.HarnessTests" />
     </ItemGroup>
   ```

4. **Reference everything as `Private=false`.** The game DLLs, the harness and the mod under test all resolve at runtime (StarMap's load context plus the harness's mods-folder resolution), so nothing is copied into the output. The harness HintPath expects its repo checked out as a sibling and follows `$(Configuration)`; build both in the same configuration:

   ```xml
   <!-- tests/AdvancedFlightComputer.HarnessTests/AdvancedFlightComputer.HarnessTests.csproj L26-L37 -->
     <ItemGroup>
       <ProjectReference Include="../../AdvancedFlightComputer/AdvancedFlightComputer.csproj">
         <Private>false</Private>
       </ProjectReference>
       <Reference Include="HeadlessHarness">
         <HintPath>../../../HeadlessHarness/HeadlessHarness/bin/$(Configuration)/net10.0/HeadlessHarness.dll</HintPath>
         <Private>false</Private>
       </Reference>
       <Reference Include="KSA">
         <HintPath>../../../../refs/game/KSA.dll</HintPath>
         <Private>false</Private>
       </Reference>
   ```

5. **Derive every test from one base class.** Discovery is by interface, so there is nothing to register; the name is also the `KSA_HEADLESS_TESTS` filter key, so renaming a test breaks existing invocations. The base swaps the guidance wall clock for simulation time and restores it in `finally`, and it deliberately catches nothing: the harness classifies an escaping `MissingMemberException` or `TypeLoadException` as game-API drift, an infrastructure failure, and a catch would downgrade it to an ordinary failure:

   ```csharp
   // tests/AdvancedFlightComputer.HarnessTests/Framework/AfcTest.cs L7-L32
   // Base for every test here. Exceptions are deliberately not caught: HarnessRunner classifies a
   // MissingMemberException or TypeLoadException escaping a test as game-API drift, an infrastructure
   // failure, and catching it would downgrade that to an ordinary FAIL.
   public abstract class AfcTest : IHarnessTest
   {
       // Also the KSA_HEADLESS_TESTS filter key, so a rename breaks existing invocations.
       public abstract string Name { get; }

       public virtual bool OptIn => false;

       public int Run(HeadlessSession session)
       {
           TestContext t = new TestContext(Name, session);
           // The harness steps the simulation far faster than real time, so the guidance gates read the
           // simulation time and advance as they do at 1x in game, not with how fast this machine runs.
           GuidanceClock.Source = static () => (long)(Universe.GetElapsedSeconds() * 1000.0);
           try
           {
               Execute(t);
           }
           finally
           {
               GuidanceClock.Reset();
           }
           return t.Finish();
       }
   ```

6. **Spawn throwaway vehicles from fixtures and despawn in `finally`.** Each spawn deserializes its own part tree because `Vehicle.CreateVehicle` shares `root.Tree`, and a failed spawn rolls back the registration:

   ```csharp
   // tests/AdvancedFlightComputer.HarnessTests/Fixtures/VehicleFixtures.cs L7-L34
   // Spawning from a shipped default save or a design this suite builds itself; VehicleSpawner covers
   // player saves and live-vehicle copies. Despawn from a finally, or the vehicle keeps ticking into
   // later tests.
   //
   // Each spawn deserializes its own part tree: Vehicle.CreateVehicle(..., Part root, ...) does
   // Parts = root.Tree, so reusing one design without deserializing would alias the tree across both.
   public static class VehicleFixtures
   {
       // No staged state. A test that reads engines, sequences or fuel flow wants SpawnFromSaveData.
       public static Vehicle SpawnDesign(
           CelestialSystem system, IParentBody parent, PartInstance design, string id, Orbit orbit)
       {
           EnsureIdAvailable(system, id);
           PartTree tree = PartTree.Deserialize(design);
           Vehicle? vehicle = null;
           try
           {
               vehicle = Vehicle.CreateVehicle(
                   system, doubleQuat.Identity, double3.Zero, parent, id, tree.Root, orbit);
               parent.Children.Add(vehicle);
               return vehicle;
           }
           catch
           {
               RollBackSpawn(system, id, vehicle, tree);
               throw;
           }
       }
   ```

7. **Add a drift detector.** One test resolves every string-keyed handle and IL anchor against the running build, so a game-side rename fails here instead of silently disabling a feature at load:

   ```csharp
   // tests/AdvancedFlightComputer.HarnessTests/Features/Core/ReflectionTargetsTest.cs L12-L32

   namespace AdvancedFlightComputer.HarnessTests;

   // Every string-keyed handle into the game resolves against the loaded build. That covers the
   // reflection keys each feature validates before it patches, the IL anchors the transpilers look
   // for, and the typed plan-window accessors, which also have to read and write through. A game-side
   // rename fails here instead of silently disabling a feature at load.
   public sealed class ReflectionTargetsTest : AfcTest
   {
       public override string Name => "afc-reflection-targets";

       protected override void Execute(TestContext t)
       {
           t.Check("Core keys resolve", GameReflection.ValidateCore());
           t.Check("HyperbolicTargets keys resolve", GameReflection.ValidateHyperbolicTargets());
           t.Check("ManeuverTools keys resolve", GameReflection.ValidateManeuverTools());
           t.Check("PlanWindow keys resolve", GameReflection.ValidatePlanWindow());
           t.Check("MultiPass keys resolve", GameReflection.ValidateMultiPass());
           t.Check("RcsTranslation keys resolve", GameReflection.ValidateRcsTranslation());
           t.Check("AutoStage keys resolve", GameReflection.ValidateAutoStage());
           t.Check("AutoRemove keys resolve", GameReflection.ValidateAutoRemove());
   ```

8. **Pick the oracle carefully.** The README rule: the oracle is the game's own propagation, never a re-derivation of the maths under test. Time-gated logic that reads simulation time (step 5) advances as it does at 1x in game, not with how fast the machine runs.

9. **Run it.** Build this solution and HeadlessHarness, deploy both (the `CopyToMods` targets), then run the harness's own launcher script with `-Tests <name>[,<name>]`. Flight tests read saves named by `KSA_HEADLESS_VEHICLE(S)` and skip when absent. Leave the deployed test mod disabled for normal play ([tests/AdvancedFlightComputer.HarnessTests/README.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/README.md)).

## Complete example

| File | Role |
|---|---|
| [tests/AdvancedFlightComputer.HarnessTests/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/mod.toml) | Consumer mod manifest |
| [tests/AdvancedFlightComputer.HarnessTests/TestMod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/TestMod.cs) | Empty `[StarMapMod]` |
| [tests/AdvancedFlightComputer.HarnessTests/AdvancedFlightComputer.HarnessTests.csproj](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/AdvancedFlightComputer.HarnessTests.csproj) | References, all `Private=false` |
| [tests/AdvancedFlightComputer.HarnessTests/Framework/AfcTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Framework/AfcTest.cs) | Base class: clock swap, no catch |
| [tests/AdvancedFlightComputer.HarnessTests/Fixtures/VehicleFixtures.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Fixtures/VehicleFixtures.cs) | Throwaway vehicle spawning |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Core/ReflectionTargetsTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Core/ReflectionTargetsTest.cs) | Drift detector |

## Variations and gotchas

- **The harness is external.** Its API (`IHarnessTest`, `HeadlessSession`, `HarnessLog`, `VehicleSpawner`) is known here only from the consumer mods; check the harness repository for the current contract before relying on a member.
- **Harness version matters.** AFC's suite needs a harness whose `SimDriver.Step` runs `RaisePendingAlerts` like `PrepareFrame`; with an older harness the burn-removal checks fail.
- **No game patches are active by default.** The harness manifest does not run the mod's `OnFullyLoaded`, so tests that need a feature's Harmony patches apply them on a test-scoped Harmony id and remove them afterwards (see [Test Harmony patches without running the game](../test-harmony-patches-without-the-game/overview.md)).
- **Tests are API.** Names are filter keys; a test that fails on purpose when no suitable save exists (`afc-autostage-spent-drop`) says so in the README.
- **Game updates.** Run the whole suite on every new build; `MissingMemberException` and `TypeLoadException` reports are your list of broken couplings (see [Upgrade a mod to a new KSA build](../../game-updates/upgrade-a-mod-to-a-new-ksa-build/overview.md)).

## See also

- [Overview](overview.md); [approach 2](headless-test-harness-for-game-mods-2.md)
- [Test Harmony patches without running the game](../test-harmony-patches-without-the-game/overview.md), [Declare a mod dependency](../../../getting-started/dependencies/declare-a-mod-dependency/overview.md), [Spawn a vehicle at runtime](../../../vehicles-and-parts/vehicles/spawn-a-vehicle-at-runtime/overview.md)
- Mod features: [AFC headless harness test mod](../../../../mods/ksa-advancedflightcomputer/features/headless-harness-test-mod.md), [AFC validated reflection handles](../../../../mods/ksa-advancedflightcomputer/features/validated-reflection-handles.md)
