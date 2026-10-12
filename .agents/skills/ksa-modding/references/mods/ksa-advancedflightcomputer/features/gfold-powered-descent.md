---
type: Mod Feature
title: G-FOLD fuel-optimal powered descent solved in-frame with Clarabel
description: Lossless-convexification powered descent as a second-order cone program in a site-fixed frame, solved synchronously with a time limit inside the per-vehicle step and flown as committed-trajectory tracking with periodic re-solves
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [native-interop, harmony-prefix, custom-physics]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.Celestial, KSA.IParentBody, KSA.FlightComputer]
tags: [landing, powered-descent, g-fold, socp, convex-optimization, clarabel, guidance]
---

For a landing on an airless body, or with an agile lander that points with RCS, AFC flies a fuel-optimal
powered descent computed by G-FOLD (Acikmese and Ploen 2007; Acikmese, Carson and Blackmore 2013): a
convex program whose solution is the whole thrust history from the current state to a soft touchdown on
the site. The solver code is game-independent; the KSA side is a frame adapter and a mode that flies the
plan. The project documents the full formulation in `docs/guidance/gfold.md`, including the constraint
matrices for a five-node case.

## How it works

### The convex program

- Variables per node: position, velocity, thrust acceleration `u`, log-mass `z`, slack `sigma`.
  Lossless convexification replaces the non-convex thrust annulus with `||u|| <= sigma` plus bounds on
  `sigma` (upper bound linearized in `z`, lower bound as a rotated cone).
- Constraints: trapezoidal dynamics links, initial state, terminal velocity and position, thrust
  pointing cone, glideslope cone toward the pad, velocity cap, ground, log-mass box.
- Objective: maximize final log-mass (minimum fuel, P4) or minimize landing error (P3).
- Time of flight is fixed per solve; an outer golden-section search (`SearchMinFuel`) picks the best
  `t_f` ([docs/guidance/gfold.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/guidance/gfold.md), [AdvancedFlightComputer/Features/Guidance/Gfold/GfoldPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Gfold/GfoldPlanner.cs)).
- The solver-neutral `ConicProblem` is solved by Clarabel through the
  [native interop](native-conic-solver-interop.md); G-FOLD always cold starts.

### Site frame

`KsaGfold` builds an "x is up" orthonormal frame at the site (terrain height), expresses the vehicle state
in it and maps the first commanded acceleration back to a CCI thrust direction and throttle. It assumes
constant gravity at the site, treats the body-fixed site frame as inertial (no Coriolis or centrifugal
terms) and ignores aerodynamics, which suits short terminal descents on slow-rotating, airless bodies;
the 6-DOF solver covers the rest ([AdvancedFlightComputer/Features/Guidance/Adapters/Gfold/KsaGfold.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Gfold/KsaGfold.cs#L1-L20)).

### Flying the plan

`GfoldDescent` flies committed-trajectory tracking: the planned thrust at the current time index is fed
forward with light PD feedback on the planned state, and the plan is re-solved on a cadence for the
remaining time of flight, falling back to the full search when that fails. Commands are low-pass
filtered; a planned coast is flown as a real coast (engine off below a fraction of full thrust, relit
above a higher one, ignition after a coast waits for alignment); orbital landings first remove
horizontal speed at an elevated vertical gate (500 m by default). A plan refused for propellant rests
the searches for 2 s of simulation time while the committed plan keeps flying
([AdvancedFlightComputer/Features/Guidance/Modes/GfoldDescent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/GfoldDescent.cs#L1-L130)).

### In-frame budget

The solve runs synchronously in the per-vehicle `PrepareWorker` prefix. The author's measurements at the
flown size (N = 50) put a cadence solve at about 10-13 ms with Clarabel and a bracketed search at about
256 ms; SCS at any useful tolerance did not fit in a 16.7 ms frame, which is why the interior-point solver
is used here. `GfoldPlanner.SolveTimeLimitS` bounds each call; it is a static, safe only because the
call is synchronous and each vehicle sets it right before its own solve
([AdvancedFlightComputer/Features/Guidance/Modes/GfoldDescent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/GfoldDescent.cs#L200-L222)).

## Lifecycle and timing

`frame:dispatch-solvers`, main thread, inside [the guidance step](guidance-vehicle-step-loop.md): the
flown state is the vehicle's current state after the previous physics step, and the command goes to the
next step through the stock flight computer. Solving on the main thread costs frame time but avoids
any synchronization; the author notes that moving it to a worker would make the static time limit a race.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Gfold/GfoldPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Gfold/GfoldPlanner.cs) | Problem assembly, P3/P4, time-of-flight search |
| [AdvancedFlightComputer/Features/Guidance/Gfold/GfoldParams.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Gfold/GfoldParams.cs) | Vehicle and constraint parameters |
| [AdvancedFlightComputer/Features/Guidance/Gfold/GfoldTrajectory.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Gfold/GfoldTrajectory.cs) | Solved trajectory |
| [AdvancedFlightComputer/Features/Guidance/Adapters/Gfold/KsaGfold.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Gfold/KsaGfold.cs) | Site frame and game-state mapping |
| [AdvancedFlightComputer/Features/Guidance/Modes/GfoldDescent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/GfoldDescent.cs) | Descent mode: cadence, tracking, gate, coast |
| [AdvancedFlightComputer/Features/Guidance/Conic/ConicProblem.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/ConicProblem.cs) | Solver-neutral cone program |
| [docs/guidance/gfold.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/guidance/gfold.md) | Full formulation and backend measurements |
| [tests/AdvancedFlightComputer.Guidance.Tests/Gfold/Program.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Gfold/Program.cs) | Console checks against a Python reference |

## Snippets

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/GfoldDescent.cs L203-L218
        // GfoldPlanner.SolveTimeLimitS is process-wide, which is safe ONLY because the solve below is synchronous and on this thread: each vehicle sets it immediately before its own call and the call has returned before any other vehicle is serviced.
        // Move the solve to a worker and this becomes a race - see GfoldSolveMs for why that move is worth making anyway.
        GfoldPlanner.SolveTimeLimitS = _s.GfoldSolveTimeLimitS > 0 ? _s.GfoldSolveTimeLimitS : null;
        var solveClock = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // On the cadence, prefer to keep the arrival time set at handoff (solve the remaining time); only re-run the full search when that fails or we have no plan yet.
            GfoldTrajectory traj = null;
            if (!_s.GfoldForceSearch && _s.GfoldPlan != null && _s.GfoldArrivalTime - now > GfoldMinTf)
            {
                double remaining = _s.GfoldArrivalTime - now;
                GfoldTrajectory t = GfoldPlanner.SolveMinFuel(
                    p, remaining, _s.GfoldNodes, [GfoldSolverTargetAltM, 0.0, 0.0],
                    options: GfoldOptions.Descent with { SlewReg = _s.GfoldSlewReg });
                if (t.Status is ConicStatus.Optimal or ConicStatus.OptimalInaccurate && FitsFuel(t, p))
```

## Game APIs touched

The solver touches none. The adapter reads vehicle state, site position on the rotating body and terrain
height, and writes attitude and throttle through the flight computer (see
[guidance attitude and gimbal actuation](guidance-attitude-and-gimbal-actuation.md)).

## Pitfalls and version notes

- A time-limited iterate is not a solution; check the status and keep flying the previous plan.
- Tighter tolerance on a first-order solver can make a time-of-flight search worse (more solves hit the
  iteration cap and are rejected).
- Earlier PoweredGuidance releases used ECOS (GPLv3); Clarabel replaced it at parity, which allowed the
  MIT license.
- The author reports that only single-craft ascent has been flown in game from this tree; G-FOLD is
  validated by console checks and harness tests.

## See also

- [6-DOF SCvx landing MPC](scvx-6dof-landing-mpc.md)
- [Native conic solver interop](native-conic-solver-interop.md)
- [Terminal hover and touchdown](terminal-hover-touchdown.md)
- [Deorbit landing planner](deorbit-landing-planner.md)
- [Fly a fuel-optimal descent with G-FOLD solved synchronously under a time limit](../../../howto/flight-and-orbits/guidance/plan-and-fly-a-powered-landing/plan-and-fly-a-powered-landing-1.md) - how-to article demonstrating this feature
- [Solve a cheaper problem synchronously inside the vehicle step with a solver time limit](../../../howto/flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/run-an-expensive-solve-without-stalling-the-frame-3.md) - how-to article demonstrating this feature

