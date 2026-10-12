---
type: Game Feature
title: Run an expensive solve without stalling the frame
description: Move a heavy guidance or planning solve (convex ascent, descent MPC, deorbit planner, guidance preview) off the frame and the vehicle worker, and pick it up when it finishes
category: flight-and-orbits
group: guidance
topic: run-an-expensive-solve-without-stalling-the-frame
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/convex-ascent-scvx
  - mods/ksa-advancedflightcomputer/features/scvx-6dof-landing-mpc
  - mods/ksa-advancedflightcomputer/features/gfold-powered-descent
  - mods/ksa-advancedflightcomputer/features/deorbit-landing-planner
  - mods/ksarmory/features/guided-bomb-tail-kit-and-sight
  - mods/compendium/features/root-closest-approach-search
lifecycle: [frame:dispatch-solvers, thread:background, frame:draw-ui, frame:gauges]
game_types: [KSA.Vehicle, KSA.FlightComputer, KSA.Celestial, KSA.Universe]
tags: [performance, background-thread, time-slicing, rate-limit, cache, guidance, solver, frame-budget]
---
A guidance or planning solve that takes longer than a few milliseconds must not run inside the frame. KSA gives a mod no scheduler or job API, so the solution is always one of five patterns, chosen by how long the solve takes and whether it can be split. This topic compares them; the other guidance topics ([landing](../plan-and-fly-a-powered-landing/overview.md), [ascent](../fly-closed-loop-ascent-guidance/overview.md), [ballistic prediction](../predict-impact-point-and-build-a-ballistic-computer/overview.md)) use them.

## Engine background

- **The frame.** `Program.PrepareFrame` applies the finished physics step, then `Universe.ExecuteNextVehicleSolvers` runs `PrepareVehicleWorkers` (which calls `Vehicle.PrepareWorker(SimStep)` on the main thread for each vehicle, serially) and queues the vehicle solver on the game's worker threads. `PrepareWorker` is the last settled main-thread point before the next step starts.[^ksa-program] [^ksa-universe] [^ksa-vehicle] See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).
- **What that implies.** Anything slow in a `PrepareWorker` prefix delays every later vehicle and the frame; anything a mod thread reads from the part tree, flight computer or orbit may be mutated underneath it. The solution is to read on the main thread, copy, and let the other side work on the copy.
- **Time warp.** Simulated time can jump by hours between frames, so budgets and rate limits must be in wall-clock time.

## Approaches

| Article | Mod | Technique | Lifecycle | Use when |
|---|---|---|---|---|
| [Plain data in, worker solve, immutable plan out](run-an-expensive-solve-without-stalling-the-frame-1.md) | KSA-AdvancedFlightComputer | background thread | `frame:dispatch-solvers`, `thread:background` | One plan that takes seconds |
| [Receding-horizon MPC with wall-clock budget](run-an-expensive-solve-without-stalling-the-frame-2.md) | KSA-AdvancedFlightComputer | background thread | `frame:dispatch-solvers`, `thread:background` | A repeated re-solve, tens to hundreds of milliseconds |
| [Synchronous solve with a solver time limit](run-an-expensive-solve-without-stalling-the-frame-3.md) | KSA-AdvancedFlightComputer | custom physics | `frame:dispatch-solvers` | A solve of about ten milliseconds; no thread wanted |
| [C# iterator time-sliced across frames](run-an-expensive-solve-without-stalling-the-frame-4.md) | KSA-AdvancedFlightComputer | iterator time-slicing | `frame:dispatch-solvers` | A splittable search; main-thread state, no locks |
| [Wall-clock rate limit and throttled caches](run-an-expensive-solve-without-stalling-the-frame-5.md) | KSArmory, Compendium | rate-limited solve | `frame:draw-ui`, `frame:gauges` | A cheap-ish computation you can simply run less often |

## See also

- [Plan and fly a powered landing](../plan-and-fly-a-powered-landing/overview.md); [Fly a closed-loop ascent to a target orbit](../fly-closed-loop-ascent-guidance/overview.md)
- [Find the closest approach to a target along a trajectory](../../map-view/find-closest-approach-along-the-flight-plan/overview.md)
- [Measure what a mod costs per frame, throttle expensive work and warm up hot code](../../../testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/overview.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
