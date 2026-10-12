---
type: Mod Feature
title: In-game tests as a separate HeadlessHarness consumer mod
description: Ship tests as a second StarMap mod that depends on HeadlessHarness and the mod under test, discovered by interface and run against the real simulation without a GPU, with API-drift classification, sim-time clocks and test-scoped Harmony owners
mod: ksa-advancedflightcomputer
category: testing-and-tooling
techniques: [starmap-lifecycle, harmony-prefix, reflection]
hooks: []
lifecycle: [event:headless-test-run]
game_types: [KSA.Vehicle, KSA.Universe, KSA.CelestialSystem, KSA.FlightComputer]
tags: [testing, headless, harness, regression, api-drift, starmap]
---

Most of AFC's behavior depends on the live game: flight-plan propagation, the vehicle workers, stock
burn handling, the part tree. AFC tests it inside the real game with **HeadlessHarness**
(<https://github.com/Maximilian-Nesslauer/KSA-HeadlessHarness>, not part of this catalog), which brings
KSA up without a GPU and runs plug-in tests against the simulation. The tests are a separate mod,
`AdvancedFlightComputer.HarnessTests`, that never ships with the release and does nothing on a normal
launch. 72 named tests cover every feature; their names are listed and described in the test
README ([tests/AdvancedFlightComputer.HarnessTests/README.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/README.md)).

## How it works

- **A consumer mod.** Its `mod.toml` names a StarMap entry assembly and two required dependencies:
  `HeadlessHarness` (which marks the folder as a harness consumer) and `AdvancedFlightComputer`, the mod
  under test ([tests/AdvancedFlightComputer.HarnessTests/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/mod.toml)). `TestMod` is an empty
  `[StarMapMod]` class, so the folder is a valid mod with no lifecycle hooks
  ([tests/AdvancedFlightComputer.HarnessTests/TestMod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/TestMod.cs)).
- **Access to internals.** The main project declares `<InternalsVisibleTo Include="AdvancedFlightComputer.HarnessTests" />`,
  and the test project references it with a `ProjectReference` and HeadlessHarness via a HintPath to a
  sibling checkout ([tests/AdvancedFlightComputer.HarnessTests/AdvancedFlightComputer.HarnessTests.csproj](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/AdvancedFlightComputer.HarnessTests.csproj)).
- **Discovery by interface.** Every test derives from `AfcTest : IHarnessTest`; the harness finds them,
  and `KSA_HEADLESS_TESTS` filters by exact `Name`. `AfcTest.Run` wraps `Execute(TestContext)` and
  swaps the guidance wall clock for simulation time (`GuidanceClock.Source = Universe.GetElapsedSeconds`),
  because the harness steps far faster than real time ([tests/AdvancedFlightComputer.HarnessTests/Framework/AfcTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Framework/AfcTest.cs)).
- **Reporting.** `TestContext.Check`, `CheckRel`, `CheckAbs`, `CheckMixed`, `Fail`, `Skip`, `Info`;
  tests never catch exceptions, because the harness classifies an escaping `MissingMemberException` or
  `TypeLoadException` as game-API drift (an infrastructure failure), and catching would downgrade it to
  an ordinary failure.
- **Fixtures.** `VehicleFixtures.SpawnDesign` / `SpawnFromSaveData` spawn throwaway vehicles from shipped
  default saves or designs the suite builds (each spawn deserializes its own part tree, since
  `Vehicle.CreateVehicle` shares `root.Tree`), and tests despawn them in `finally` so they do not tick into
  later tests ([tests/AdvancedFlightComputer.HarnessTests/Fixtures/VehicleFixtures.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Fixtures/VehicleFixtures.cs)). Flight tests use
  player saves named by `KSA_HEADLESS_VEHICLE(S)` and skip when absent.
- **Test-scoped patches.** Tests that need a feature's Harmony patches apply them on a test-scoped Harmony
  id and remove them afterwards (for example `AutoStageTestPatches`, the driver test installing the
  production guidance hooks), so the suite exercises real patch code without depending on the mod's own
  load state.
- **Oracles.** The README states the rule: the oracle is the game's own propagation, never a re-derivation
  of the math under test. IL-level tests run transpilers over
  `PatchProcessor.GetOriginalInstructions` of the current game method
  ([tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs)), and
  `afc-reflection-targets` resolves every reflection handle and anchor against the running build.
- **Harness contract.** The suite needs a HeadlessHarness whose `SimDriver.Step` runs
  `FlightComputer.RaisePendingAlerts` like `Program.PrepareFrame`, or the burn-removal checks fail.

## Lifecycle and timing

The test mod loads like any StarMap mod and registers no StarMap hook, so there is no
`startup:all-mods-loaded` work of its own. HeadlessHarness (not in this catalog, so its internals are
taken from AFC's comments) discovers the `IHarnessTest` classes during a headless run, drives the session
and steps the simulation itself (its own `SimDriver`), so tests run between steps on the main thread
with the universe loaded; this concept labels that `event:headless-test-run`. The README states the one
frame-order contract the suite relies on: `SimDriver.Step` must call `FlightComputer.RaisePendingAlerts`
as `Program.PrepareFrame` does. Because the harness does not run `Program.PrepareFrame`, features must not depend on
caches it refreshes (AFC enumerates `Universe.CurrentSystem.All` instead of `Program.VehiclesInFrame` partly
for this reason, see [post-solver vehicle tick](post-solver-vehicle-tick.md)).

## Key code

| Path | Role |
|---|---|
| [tests/AdvancedFlightComputer.HarnessTests/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/mod.toml) | Consumer mod manifest |
| [tests/AdvancedFlightComputer.HarnessTests/TestMod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/TestMod.cs) | Empty `[StarMapMod]` |
| [tests/AdvancedFlightComputer.HarnessTests/Framework/AfcTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Framework/AfcTest.cs) | Base class: clock swap, no catch |
| [tests/AdvancedFlightComputer.HarnessTests/Framework/TestContext.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Framework/TestContext.cs) | Checks, skips, verdict |
| [tests/AdvancedFlightComputer.HarnessTests/Fixtures/VehicleFixtures.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Fixtures/VehicleFixtures.cs) | Throwaway vehicle spawning |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Core/ReflectionTargetsTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Core/ReflectionTargetsTest.cs) | Drift detector |
| [tests/AdvancedFlightComputer.HarnessTests/README.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/README.md) | Running instructions and the full test list |

## Snippets

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

## Game APIs touched

| Member | Use |
|---|---|
| `Vehicle.CreateVehicle` (via fixtures), `CelestialSystem` | Spawning and lookups |
| `Universe.GetElapsedSeconds` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs)) | Simulation clock for time-gated logic |
| `HarmonyLib.PatchProcessor.GetOriginalInstructions` | IL-level transpiler tests |

## Pitfalls and version notes

- Leave the deployed test mod disabled for normal play.
- Renaming a test breaks `KSA_HEADLESS_TESTS` invocations; names are API.
- Some flight tests need specific player saves (for example a launch stage that mixes boosters with a
  core); `afc-autostage-spent-drop` fails rather than skips when none qualifies, on purpose.

## See also

- [Game-free guidance checks](game-free-guidance-checks.md)
- [Validated reflection handles](validated-reflection-handles.md)
- [CI reference assemblies](ci-reference-assemblies.md)
- [Mod deploy MSBuild targets](mod-deploy-msbuild-targets.md)
- [Build a consumer mod with its own mod.toml, InternalsVisibleTo and a test base class](../../../howto/testing-and-tooling/offline-tests/headless-test-harness-for-game-mods/headless-test-harness-for-game-mods-1.md) - how-to article demonstrating this feature
- [Run transpilers over the game's real IL and scope test patches to a disposable Harmony owner](../../../howto/testing-and-tooling/offline-tests/test-harmony-patches-without-the-game/test-harmony-patches-without-the-game-3.md) - how-to article demonstrating this feature

