---
type: Game Feature
title: "Test a solver or controller offline: closed loop, purity and replay"
description: Build a console harness that solves, integrates dynamics with injected dispersions and re-solves, check determinism and no cross-talk before threading a solve, and replay the exact problem the live game fed the solver
category: testing-and-tooling
group: offline-tests
topic: test-a-solver-or-controller-offline
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/closed-loop-mpc-harness
  - mods/ksa-advancedflightcomputer/features/solver-problem-json-replay
lifecycle: [thread:background, frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.Universe, KSA.IParentBody]
tags: [testing, solver, guidance, mpc, closed-loop, determinism, replay]
---

A guidance solver or controller has two kinds of bugs that a unit test of one solve will not find: bugs that only show in a loop (plans that jump between re-solves, a vehicle that tours instead of closing, solve times that creep up) and bugs that only show with the live game's inputs (a plan that fails to converge from one particular vehicle state). Both are tested offline by keeping the solver in a game-free assembly and giving the test console two tools: a closed-loop simulator and a problem file the game writes for replay.
A third, preparatory concern is threading: before a solve moves to a worker, prove it is a pure function of its inputs.

## Engine background

| Piece | Where | Use |
|---|---|---|
| `Vehicle.PrepareWorker(SimStep)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396) | The per-vehicle main-thread point, right before a vehicle's snapshot, where the mod builds (and dumps) the plain-data problem |
| `Universe.ExecuteNextVehicleSolvers` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2220) | Starts the next physics step; `PrepareWorker` runs inside it, so state is settled until then |
| Frame order | [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) | `frame:dispatch-solvers` is the last settled point; the solve itself runs on a background thread |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: RK4 plant with injected dispersions driven by console flags](test-a-solver-or-controller-offline-1.md) | ksa-advancedflightcomputer ([tests/AdvancedFlightComputer.Guidance.Tests/Scvx/McpHarness.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/McpHarness.cs#L329-L505)) | `console-checks` | `thread:background`, `frame:dispatch-solvers` | Reproduces loop-only symptoms and gates the threading move; the plant is a model of the vehicle, not the game |
| [2: problem file written before the solve, re-solved by a console flag](test-a-solver-or-controller-offline-2.md) | ksa-advancedflightcomputer ([AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs#L419-L451)) | `json-config` | `frame:dispatch-solvers`, `thread:background` | Reproduces the live failure exactly; needs a stable file format and a data-only problem type |

Both depend on the same design rule: the solver and its problem type live in a game-free assembly, game state is copied into plain values at one point (the adapter), and nothing in the solver reads the game. That rule is also what makes background solves safe.

## See also

- [Unit-test mod logic without launching the game](../unit-test-mod-logic-without-game/overview.md), [Run an expensive solve without stalling the frame](../../../flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/overview.md), [Plan and fly a powered landing](../../../flight-and-orbits/guidance/plan-and-fly-a-powered-landing/overview.md)
- Mod features: [Closed-loop MPC harness](../../../../mods/ksa-advancedflightcomputer/features/closed-loop-mpc-harness.md), [Solver problem JSON replay](../../../../mods/ksa-advancedflightcomputer/features/solver-problem-json-replay.md)
