---
type: Game Feature
title: Test Harmony patches without running the game
description: Apply the real production patch code to stand-in KSA types in a console test, run a patch-apply check over a new game build offline, or install a test-scoped Harmony owner inside a headless run
category: testing-and-tooling
group: offline-tests
topic: test-harmony-patches-without-the-game
demonstrated_by:
  - mods/unscience/features/unscience-managed-test-harnesses
  - mods/purrtty/features/ksa-upgrade-coupling-review-skill
  - mods/ksa-advancedflightcomputer/features/headless-harness-test-mod
  - mods/ksa-advancedflightcomputer/features/validated-reflection-handles
lifecycle:
  - frame:prepare
  - event:save
  - event:load
  - event:headless-test-run
  - startup:all-mods-loaded
  - event:game-update
game_types:
  - KSA.Program
  - KSA.Part
  - KSA.Universe
  - KSA.SimStep
  - KSA.UncompressedSave
  - KSA.Vehicle
  - KSA.TransferPlanner
  - KSA.FlightComputer
tags: [testing, harmony, patches, transpiler, game-update, headlessharness]
---

A Harmony patch is code that only matters when it is attached to a game method, and it can fail in three different ways: the target moved (a patch that silently does not apply), the rewritten method is wrong (a transpiler that matches the wrong anchor), or the patch body misbehaves at the right moment (wrong order, missing callback). Compiling against the new game does not reveal any of these for string-keyed targets, and starting the game takes minutes and needs a GPU.
The three approaches here test patches at three different distances from the real game: stand-in types (no game at all), the new DLLs loaded offline (no game process), and the real simulation in a headless run (no GPU).

## Engine background

| Piece | Where | Use |
|---|---|---|
| `Program.PrepareFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141) | The call order a frame-loop transpiler anchors on; approach 1 mirrors it in a stand-in |
| `Program.DrawProgramMenusHook` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4018) | A public empty hook, the kind of target an offline patch-apply check resolves |
| `TransferPlanner.DrawPlanWindow` | [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L305) | A method whose IL a transpiler test reads with `PatchProcessor.GetOriginalInstructions` |
| `SimStep` | [current/decomp/KSA/SimStep.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SimStep.cs) | A `readonly struct` in the game; stand-ins use a record struct with the same members |
| Frame order | [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) | Where each patched method runs |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: stand-in KSA types in the test, production patches linked in](test-harmony-patches-without-the-game-1.md) | unscience ([the-tick.tests/GameFixture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.tests/GameFixture.cs#L7-L22)) | `harmony-prefix`, `stand-in-types` | `frame:prepare`, `event:save`, `event:load` | Runs anywhere, in milliseconds; the stand-in is a model that can drift from the game |
| [2: throwaway console app running CreateClassProcessor on every patch class](test-harmony-patches-without-the-game-2.md) | purrtty ([purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L38-L71)) | `patch-apply-check` | `event:game-update` | Uses the real new DLLs, so it catches target drift; proves only that patches apply |
| [3: IL-level transpiler tests and test-scoped Harmony owners](test-harmony-patches-without-the-game-3.md) | ksa-advancedflightcomputer ([tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs#L16-L47)) | `harmony-transpiler`, `headless-harness` | `event:headless-test-run` | Exact IL of the real method; needs the harness and the game DLLs |

Choosing: use approach 1 for patch logic and frame-order contracts that you can model, approach 2 as the first command after every game update, and approach 3 where the exact IL of a real method decides whether your transpiler works. Equality oracles against stock private routines are covered in [Test a mod against the real game with HeadlessHarness](../headless-test-harness-for-game-mods/overview.md).

## See also

- [Test a mod against the real game with HeadlessHarness](../headless-test-harness-for-game-mods/overview.md), [Unit-test mod logic without launching the game](../unit-test-mod-logic-without-game/overview.md)
- [Upgrade a mod to a new KSA build](../../game-updates/upgrade-a-mod-to-a-new-ksa-build/overview.md), [Track the game API your mod uses](../../game-updates/track-the-game-api-your-mod-uses/overview.md)
- [Isolate patch failures](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md), [Validate patch targets and detect game-update breakage](../../../patching/safety/validate-patch-targets-and-detect-game-update-breakage/overview.md), [Write a Harmony transpiler](../../../patching/transpilers/write-a-harmony-transpiler/overview.md), [Simulate bodies outside the vehicle system](../../../physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/overview.md)
- Mod features: [Unscience managed test harnesses](../../../../mods/unscience/features/unscience-managed-test-harnesses.md), [purrTTY upgrade coupling review skill](../../../../mods/purrtty/features/ksa-upgrade-coupling-review-skill.md), [AFC headless harness test mod](../../../../mods/ksa-advancedflightcomputer/features/headless-harness-test-mod.md)
