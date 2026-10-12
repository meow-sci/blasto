---
type: Game Feature
title: Plan and fly a powered landing
description: Plan a deorbit burn to a site on a rotating body, fly a fuel-optimal powered descent, and finish with a hover and touchdown under engine power
category: flight-and-orbits
group: guidance
topic: plan-and-fly-a-powered-landing
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/gfold-powered-descent
  - mods/ksa-advancedflightcomputer/features/scvx-6dof-landing-mpc
  - mods/ksa-advancedflightcomputer/features/terminal-hover-touchdown
  - mods/ksa-advancedflightcomputer/features/deorbit-landing-planner
lifecycle: [frame:dispatch-solvers, thread:background, physics:vehicle-solver, frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.Celestial
  - KSA.IParentBody
  - KSA.FlightComputer
  - KSA.ManualControlInputs
  - KSA.Burn
  - KSA.BurnTarget
  - KSA.FlightPlan
  - KSA.PatchedConic
  - KSA.Orbit
  - KSA.Universe
tags: [landing, deorbit, powered-descent, g-fold, scvx, hover, touchdown, rotating-body]
---
KSA has no landing autopilot: stock flies manoeuvre nodes and attitude holds, nothing more. A powered landing therefore breaks into four problems a mod has to solve, and the only mod that does is KSA-AdvancedFlightComputer (AFC), whose four landing features are the four approaches below. They are stages of one chain (deorbit, braking and descent, hover, touchdown), not alternatives for the same problem, except that approaches 1 and 2 are two descent solvers.

## Engine background

- **The ground and the site.** `Celestial.GetTerrainHeightFromDirCcf(direction)` samples terrain height for a body-fixed direction; `IParentBody.GetCcf2Cci()` / `GetCcf2Cci(UniverseTime)` rotate a body-fixed site into the inertial frame (CCI) at any time and `GetAngularVelocity()` is the spin rate about CCI +Z.[^ksa-celestial] [^ksa-iparentbody]
- **The vehicle.** `Vehicle.GetRadarAltitude()`, `TotalMass`, `PropellantMass`, `TotalMassPropsBody` and `Situation.HasAnyContact()` (touchdown).[^ksa-vehicle]
- **Stock burns.** `Burn.Create`, `FlightComputer.AddBurn`, `BurnTarget` and `FlightPlan.TryFindPatch` give a deorbit node that the stock Auto autopilot flies.[^ksa-burn] [^ksa-flightplan] [^ksa-flightcomputer]
- **Where to run control code.** `Universe.PrepareVehicleWorkers` calls `Vehicle.PrepareWorker` on the main thread for each vehicle just before the next physics step is dispatched; a prefix there reads settled state and writes what the next step sees.[^ksa-universe] See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).
- **Cost control.** Solves that take more than a few milliseconds are time-limited, time-sliced or moved to a background thread; see [Run an expensive solve without stalling the frame](../run-an-expensive-solve-without-stalling-the-frame/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [G-FOLD solved synchronously with a time limit](plan-and-fly-a-powered-landing-1.md) | KSA-AdvancedFlightComputer | custom physics, harmony prefix | `frame:dispatch-solvers` | Fuel-optimal and cheap enough for the frame (about 10-13 ms); airless bodies only, constant gravity, no aerodynamics |
| [6-DOF landing MPC on a background thread](plan-and-fly-a-powered-landing-2.md) | KSA-AdvancedFlightComputer | background thread | `frame:dispatch-solvers`, `thread:background` | Handles atmosphere, inertia and gimbals; needs a single result slot, immutable inputs and wall-clock budgets |
| [Sink-profile hover controller](plan-and-fly-a-powered-landing-3.md) | KSA-AdvancedFlightComputer | harmony prefix, PID | `frame:dispatch-solvers`, `frame:draw-ui` | Simple, robust final stage with contact-flag touchdown; needs a throttleable engine that can hover |
| [Time-sliced Lambert search, then a stock node](plan-and-fly-a-powered-landing-4.md) | KSA-AdvancedFlightComputer | iterator time-slicing, harmony prefix | `frame:dispatch-solvers`, `physics:vehicle-solver` | No thread, stock timing; costs about 3 ms/frame and a node-edit race to avoid |

## See also

- [Compute a suicide-burn start altitude and the thrust it needs](../compute-a-suicide-burn/overview.md)
- [Fly a closed-loop ascent to a target orbit](../fly-closed-loop-ascent-guidance/overview.md)
- [Run an expensive solve without stalling the frame](../run-an-expensive-solve-without-stalling-the-frame/overview.md)
- [Build a site frame and compute a launch window](../../transfers/build-a-site-frame-and-compute-a-launch-window/overview.md)
- [Aim a transfer at a flyby periapsis](../../transfers/aim-a-transfer-at-a-flyby-periapsis/overview.md)
- [Execute planned burns with the stock autopilot](../../autopilot/execute-planned-burns-with-the-stock-autopilot/overview.md)

[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-burn]: KSA.Burn (decompiled, build 2026.10.10.5554)
[^ksa-flightplan]: KSA.FlightPlan (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
