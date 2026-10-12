---
type: Game Feature
title: Test a mod against the real game with HeadlessHarness
description: Ship tests as a second StarMap mod that depends on HeadlessHarness and the mod under test so the real simulation runs without a GPU, swap the clock for simulation time and use the game's own private routines as the oracle
category: testing-and-tooling
group: offline-tests
topic: headless-test-harness-for-game-mods
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/headless-harness-test-mod
  - mods/ksa-advancedflightcomputer/features/validated-reflection-handles
  - mods/ksa-deltavmap/features/headless-harness-tests
lifecycle: [event:headless-test-run, thread:background]
game_types:
  - KSA.Universe
  - KSA.Vehicle
  - KSA.CelestialSystem
  - KSA.FlightComputer
  - KSA.Program
  - KSA.TransferPlanner
  - KSA.SequencePerformanceList
  - KSA.PartTree
tags: [testing, headlessharness, integration-tests, oracle, api-drift, consumer-mod]
---

Game-free tests ([Unit-test mod logic without launching the game](../unit-test-mod-logic-without-game/overview.md)) prove your maths. They cannot tell you whether your mod still agrees with the game: flight-plan propagation, vehicle workers, stock burn handling, the part tree and the transfer planner only exist in the live engine.
HeadlessHarness, a separate mod by the author of KSA-AdvancedFlightComputer and KSA-DeltaVMap (<https://github.com/Maximilian-Nesslauer/KSA-HeadlessHarness>, not part of this corpus), brings KSA up without a GPU and runs plug-in tests against the real simulation. Both mods use it the same way: a test-only StarMap mod that depends on the harness and the mod under test.

Its API is described here only as far as the consumer mods use it: a test implements `IHarnessTest` (`Name`, `Run(HeadlessSession)`, returning 0 for pass), is found by interface, and is selected by exact `Name`.

## Engine background

| Piece | Where | Why it matters to a harness test |
|---|---|---|
| `Universe.GetElapsedSeconds()` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2393-L2396) | The simulation clock; the harness steps far faster than real time, so time-gated mod logic must read this and not the wall clock |
| `Program.PrepareFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141) | The order the harness's `SimDriver` must imitate (apply solvers, `RaisePendingAlerts` per vehicle, flush derived part data); the harness does not run it |
| `Vehicle.CreateVehicle` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1426) | Builds a throwaway vehicle from a part root for fixtures |
| `TransferPlanner.PlanInterstellarFuel` | [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1033) | An example of a private stock routine used as an oracle by reflection |
| Frame order | [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) | What a headless step does and does not run |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: consumer mod with its own mod.toml, InternalsVisibleTo and a test base class](headless-test-harness-for-game-mods-1.md) | ksa-advancedflightcomputer ([tests/AdvancedFlightComputer.HarnessTests/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/mod.toml)) | `headless-harness` | `event:headless-test-run` | Full coverage of 72 tests with fixtures and a drift detector; needs a sibling harness checkout and saves for flight tests |
| [2: small suite of oracle tests plus a scene dump](headless-test-harness-for-game-mods-2.md) | ksa-deltavmap ([DeltaVMap.HarnessTests/InterstellarFuelTest.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.HarnessTests/InterstellarFuelTest.cs#L11-L18)) | `headless-harness` | `event:headless-test-run` | A smaller worked example focused on equality with stock private routines; some cases skip depending on the loaded system and environment variables |

Rules that both suites follow:

- **The oracle is the game, not a second derivation.** Call the stock routine (often private, so by reflection) and the mod's copy with the same inputs, and require equality.
- **Never catch in tests.** An escaping `MissingMemberException` or `TypeLoadException` is classified by the harness as game-API drift, an infrastructure failure; catching it downgrades it to an ordinary failure.
- **Despawn what you spawn, in `finally`.** A spawned vehicle keeps ticking into later tests.
- **Swap in simulation time** for any guidance clock, and reset it in `finally`.
- **Test names are API.** They are the filter keys.

## See also

- [Test Harmony patches without running the game](../test-harmony-patches-without-the-game/overview.md), [Unit-test mod logic without launching the game](../unit-test-mod-logic-without-game/overview.md), [Run the mod's whole simulation in a headless rig](../run-mod-simulation-in-a-headless-rig/overview.md)
- [Fly scripted scenarios unattended](../../in-game-testing/run-unattended-scenarios-and-report-a-verdict/overview.md), [Upgrade a mod to a new KSA build](../../game-updates/upgrade-a-mod-to-a-new-ksa-build/overview.md), [Declare a mod dependency](../../../getting-started/dependencies/declare-a-mod-dependency/overview.md), [Spawn a vehicle at runtime](../../../vehicles-and-parts/vehicles/spawn-a-vehicle-at-runtime/overview.md), [Read staged delta-v from the game](../../../vehicles-and-parts/staging-and-engines/read-staged-delta-v-from-the-game/overview.md), [Compute mission elapsed time and detect liftoff](../../../flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/overview.md)
- Mod features: [AFC headless harness test mod](../../../../mods/ksa-advancedflightcomputer/features/headless-harness-test-mod.md), [AFC validated reflection handles](../../../../mods/ksa-advancedflightcomputer/features/validated-reflection-handles.md), [KSA-DeltaVMap headless harness tests](../../../../mods/ksa-deltavmap/features/headless-harness-tests.md)
