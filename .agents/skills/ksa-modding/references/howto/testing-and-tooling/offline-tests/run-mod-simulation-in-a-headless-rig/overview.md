---
type: Game Feature
title: Run the mod's whole simulation in a headless rig behind a sim boundary
description: Drive vehicles, rounds or balls through the mod's own step function in a plain test with a rig that supplies mass, inertia and a terrain stand-in, and vary the step to expose phase and epoch errors
category: testing-and-tooling
group: offline-tests
topic: run-mod-simulation-in-a-headless-rig
demonstrated_by:
  - mods/ksacars/features/headless-sim-tests
  - mods/ksarmory/features/sim-boundary-headless-tests
  - mods/ksarmory/features/mod-simulated-rounds
  - mods/ksarmory/features/consume-applied-sim-step
  - mods/ksagolf/features/headless-sim-tests
  - mods/ksagolf/features/swept-hull-contact
  - mods/ksagolf/features/dual-hook-frame-stepping
lifecycle: [frame:after-on-frame, frame:draw-ui]
game_types: [Brutal.Numerics.double3, KSA.SimStep, KSA.Universe]
tags: [testing, rig, headless, simulation, epoch, step-variation, regression]
---

Mods that move their own objects (cars, rounds, balls) have their most important logic in a step function: given contacts, inputs and a time step, produce forces and a new state. Testing that function needs a stand-in for the part of the engine that integrates it. A rig is that stand-in: a small loop that supplies what the engine would (mass, inertia, centre of mass, gravity, a ground, a stream of frame steps) and calls the mod's step function exactly as the game does.
Handling, gunnery and putting then become regression tests that run in seconds, with no game.

The prerequisite is the boundary from [Unit-test mod logic without launching the game](../unit-test-mod-logic-without-game/overview.md): the step function must live in a folder that names no KSA type. The rig is the next layer on top of it.

## Engine background

A rig has to be faithful to the engine in the few ways that matter, and the engine publishes them through one API.

| Piece | Where | Why it matters to a rig |
|---|---|---|
| `Universe.GetLastSimStep()` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2381-L2384) | The step the engine just applied; a mod integrates across this and not the wall-clock delta |
| `SimStep` | [current/decomp/KSA/SimStep.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SimStep.cs) | `PreviousTime`, `NextTime`, `DeltaTime`; the span between two `NextTime` values can exceed one `DeltaTime` when a frame was missed |
| Step publication | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1948-L1950) | `_lastSimStep` is assigned after the vehicle results are applied on the main thread, so a per-frame hook sees end-of-step samples |
| `Program.OnFrame` UI guard | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2270) | A hook inside `if (DrawUI)` does not run on frames where the UI is hidden or a capture is warming up, while the world keeps stepping |
| Frame order | [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) | Which sample is valid at which moment |

The step varies. It follows the display's frame pacing (8.33 ms and 25 ms alternation on a 120 Hz panel at 60 fps), and it changes by an order of magnitude or two when the player changes simulation speed. A rig that always uses one constant step cannot tell a right phase from a wrong one.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: rigid-body rig using the engine's mass and inertia](run-mod-simulation-in-a-headless-rig-1.md) | ksacars ([tests/KSACars.Tests/BuggyDriveTests.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tests/KSACars.Tests/BuggyDriveTests.cs#L222-L284)) | `headless-tests` | `frame:after-on-frame` | Tests the model's feel (settling, steering, braking); the rig mirrors the engine's inertia by hand |
| [2: one driver seam shared by the game and the tests](run-mod-simulation-in-a-headless-rig-2.md) | ksarmory ([src/KSArmory/Sim/RoundDriver.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/RoundDriver.cs#L64-L82)) | `sim-boundary` | `frame:after-on-frame`, `frame:draw-ui` | A test failure is a game failure and the rig cannot drift; every environment term must be a lookup the caller supplies |
| [3: ball and contact physics with an epoch layer](run-mod-simulation-in-a-headless-rig-3.md) | ksagolf ([tests/KSAGolf.Tests/SkippedFrameTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/SkippedFrameTests.cs#L19-L54)) | `headless-tests` | `frame:draw-ui`, `frame:after-on-frame` | Differences-only maths needs no big coordinates; proves behaviour across skipped frames, but covers bookkeeping rather than the engine |

Common practice across the three:

- **Vary the step.** Alternate 16 and 20 ms, drop from 1x to 0.25x to 0.01x, skip a frame, and assert that nothing jumps.
- **Assert in the units that matter.** Express a phase error in metres at 29.8 km/s of ecliptic motion, not as an abstract tolerance.
- **Write down the failure a test must catch.** A regression test counts only if it fails against the old code.
- **Never trust a rig alone.** It models the engine; keep a short list of what has been driven in game and use a [HeadlessHarness test](../headless-test-harness-for-game-mods/overview.md) where the real simulation can be the oracle.

## See also

- [Unit-test mod logic without launching the game](../unit-test-mod-logic-without-game/overview.md), [Test a mod against the real game with HeadlessHarness](../headless-test-harness-for-game-mods/overview.md)
- [Simulate bodies outside the vehicle system](../../../physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/overview.md)
- Mod features: [KSACars](../../../../mods/ksacars/features/headless-sim-tests.md), [KSArmory](../../../../mods/ksarmory/features/sim-boundary-headless-tests.md), [KSAGolf](../../../../mods/ksagolf/features/headless-sim-tests.md)
