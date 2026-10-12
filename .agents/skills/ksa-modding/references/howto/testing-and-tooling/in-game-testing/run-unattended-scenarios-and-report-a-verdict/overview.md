---
type: Game Feature
title: Fly scripted scenarios unattended, report PASS or FAIL and A/B test variants
description: Run a developer-only state machine from a request file at launch, load a save and play a scripted engagement, log tagged lines that a shell harness turns into an exit code, and compare variants with rank statistics
category: testing-and-tooling
group: in-game-testing
topic: run-unattended-scenarios-and-report-a-verdict
demonstrated_by:
  - mods/ksarmory/features/unattended-scenario-runner
  - mods/ksarmory/features/shot-night-statistics
  - mods/ksacars/features/file-drop-command-bridge
  - mods/ksagolf/features/file-command-bridge
  - mods/ksastructures/features/dev-file-bridge-and-mcp
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:after-on-frame, startup:game-loaded]
game_types: [KSA.GameSaves, KSA.Popup, KSA.Program, KSA.Universe, KSA.SimSpeed, KSA.SimStep]
tags: [testing, scenario, unattended, exit-code, statistics, ab-test, developer-only]
---

A normal KSA session needs a window and a Vulkan renderer; the GPU-less runs in this corpus come from the external HeadlessHarness mod, which has no renderer and so cannot show how anything looks ([Test a mod against the real game with HeadlessHarness](../../offline-tests/headless-test-harness-for-game-mods/overview.md)). The full game can still be run unattended. KSArmory, a weapons gameplay mod, flies whole scripted in-game test runs
(a missile engagement against spawned drones, a store dropped from a climbing craft, a gun scored shell by shell, a ballistic shot from pad to impact) with nobody at the keyboard: a shell script writes a request file, launches the game, the mod loads a save and drives
the scenario as a state machine, every line it logs starts with the tag `SCENARIO`, and the script greps that one tag and exits non-zero on `FAIL` or `TIMEOUT`. A step further, the same runner can fly interleaved variants and a Python report judges noisy results with a rank test.
The topic is about the engineering of unattended in-game test runs for a mod; the weapons gameplay is just the thing under test.

## Engine background

| Fact | Where | Consequence for a runner |
|---|---|---|
| `GameSaves.LoadSaveGame(string)` loads a named save and logs an error (no exception) for an unknown name | [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L283-L302) | The runner cannot detect a bad name from the call; it waits for a flown craft with a timeout |
| `Program.OnFrame` runs the StarMap postfix after `PostRender` on every frame; the UI hooks sit inside `if (DrawUI)` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2269), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2331-L2332) | The runner's per-frame update must run on a hook that still fires when the HUD is hidden or a screenshot is in progress |
| `Universe.GetLastSimStep()` is the simulated step last applied; `SetSimulationSpeed` sets the speed | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2381-L2384), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L700-L703) | Two clocks: player time for wall-clock budgets, simulated time for world budgets and ages |
| `Popup` keeps a private static list `Popups`; `AnyOpen` is public, `Active` is the flag the OKAY button clears | [current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L32), [current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L44), [current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L34-L38) | An open popup (the update notice) takes the keyboard and with it the controlled craft's throttle, so an unattended run must close them |

See the [frame order in the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: request file, state machine, budgets and tagged SCENARIO lines](run-unattended-scenarios-and-report-a-verdict-1.md) | ksarmory ([src/KSArmory/Ksa/ScenarioRunner.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ScenarioRunner.cs), [tools/scenario.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/scenario.sh)) | `starmap-lifecycle` | `startup:all-mods-loaded`, `frame:draw-ui` | A pass/fail gate for behaviour a log can answer; cannot judge appearance, one scenario per launch |
| [2: bridge commands that load a save and step an exact number of seconds](run-unattended-scenarios-and-report-a-verdict-2.md) | ksacars ([src/KSACars/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Bridge.cs)), ksagolf, ksastructures | `file-ipc` | `frame:after-on-frame` | Interactive, scriptable from anywhere; the script owns pass/fail logic |
| [3: overnight batch with seeded interleaving and rank-sum verdicts](run-unattended-scenarios-and-report-a-verdict-3.md) | ksarmory ([tools/shot-batch.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/shot-batch.sh), [tools/shot-report.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/shot-report.py)) | `statistics` | outside the game | Settles small differences in noisy outcomes; needs tens of runs per arm |

## See also

- [Drive a running game through a file bridge](../drive-a-running-game-through-a-file-bridge/overview.md), [Test a mod against the real game with HeadlessHarness](../../offline-tests/headless-test-harness-for-game-mods/overview.md), [Visual regression testing for a KSA mod](../visual-regression-for-mods/overview.md), [Take clean screenshots from code](../take-screenshots-from-code/overview.md)
- [Measure what a mod costs per frame](../measure-and-budget-mod-performance/overview.md), [Show alerts and popups in game](../../../ui/windows/show-alerts-and-popups-in-game/overview.md), [Set simulation speed from a mod](../../../flight-and-orbits/flight-state/set-simulation-speed-from-a-mod/overview.md), [Compute mission elapsed time and detect liftoff](../../../flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/overview.md)
- Mod features: [KSArmory unattended scenario runner](../../../../mods/ksarmory/features/unattended-scenario-runner.md), [KSArmory shot-night statistics](../../../../mods/ksarmory/features/shot-night-statistics.md)
