---
type: How-To
title: Fly a fuel-optimal descent with G-FOLD solved synchronously under a time limit
description: Build a site-fixed landing frame on a rotating body, solve a lossless-convexification powered descent inside the per-vehicle step with a solver time limit, and fly the committed trajectory with feed-forward thrust and PD feedback
category: flight-and-orbits
group: guidance
topic: plan-and-fly-a-powered-landing
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/gfold-powered-descent]
techniques: [custom-physics, harmony-prefix, native-interop]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.Celestial, KSA.IParentBody, KSA.FlightComputer, KSA.Orbit]
tags: [landing, g-fold, convex-optimization, site-frame, rotating-body, trajectory-tracking, clarabel]
---
Outcome: a lander that, from any state above an airless body, computes the minimum-propellant thrust history to a soft touchdown on a chosen site and flies it, re-solving as it descends. The solver (G-FOLD, lossless convexification of the thrust annulus) is game-independent; this article covers the KSA side: the site frame, the live vehicle state it needs, calling the solver inside the vehicle step with a time limit, and tracking the result. KSA-AdvancedFlightComputer (AFC) is the reference implementation.[G-FOLD powered descent](../../../../mods/ksa-advancedflightcomputer/features/gfold-powered-descent.md)
Prerequisites: StarMap and Harmony. The solver backend is native (Clarabel) and is out of scope; treat `GfoldPlanner.SolveMinFuel` / `SearchMinFuel` as a black box that takes a `GfoldParams` and returns a `GfoldTrajectory`. The thrust and throttle helpers it relies on are explained in [Compute a suicide-burn start altitude and the thrust it needs](../compute-a-suicide-burn/compute-a-suicide-burn-3.md).

## Background: how the game does it

The game gives you the pieces but no landing guidance:

- **Where the ground is.** `Celestial.GetTerrainHeightFromDirCcf(direction)` returns terrain height above `MeanRadius` for a body-fixed (CCF) unit direction; the game places surface objects at `MeanRadius + height`.[^ksa-celestial] [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789-L789)
- **A site that rotates with the body.** `IParentBody.GetCcf2Cci()` rotates body-fixed vectors into the parent's inertial frame (CCI), `GetAngularVelocity()` is the spin rate about CCI +Z and `GetAngularVelocityCci()` the vector form.[^ksa-iparentbody] [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L39-L45)
- **Vehicle state.** `Vehicle.Orbit.StateVectors.PositionCci` / `VelocityCci` are inertial, `TotalMass` and `PropellantMass` are floats.[^ksa-vehicle] CCI and CCF are defined in [Understand KSA coordinate frames and epochs](../../flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md).

G-FOLD wants a local "x is up" frame at the site with uniform gravity; AFC accepts the approximations this implies (constant gravity at the site, body-fixed frame treated as inertial, no aerodynamics), which is why it refuses atmospheric bodies and leaves those to [approach 2](plan-and-fly-a-powered-landing-2.md).

## Lifecycle and timing

- **Where it runs:** `StepGfoldDescent` is called for each landing vehicle from the `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`, main thread). `Universe.PrepareVehicleWorkers` calls it for every vehicle right before the next step is dispatched,[^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2141-L2149) so the state read is the last committed one and the throttle and direction written here are what the next physics step uses.
- **Cost:** the solve is synchronous on the main thread. AFC's author measured about 10-13 ms for a cadence solve and about 256 ms for a bracketed time-of-flight search at 50 nodes (Clarabel), which is why interior-point is used and why a time limit is set.
- **Why not a worker thread:** the time limit is a process-wide static; it is safe only because each vehicle sets it immediately before its own call and the call returns before the next vehicle is served. For the threaded variant see [approach 2](plan-and-fly-a-powered-landing-2.md) and [Run an expensive solve without stalling the frame](../run-an-expensive-solve-without-stalling-the-frame/overview.md).
- See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Turn the landing site into a CCI point that follows the rotation.** The site is stored as latitude and longitude, converted to a CCF unit vector, carried into CCI with `GetCcf2Cci()` and spun forward about CCI Z. Terrain height is sampled once and cached:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Landing.cs L336-L360
// The site's CCI direction dtFuture seconds from now.
// A body spins about its own CCI Z axis (per IParentBody.GetAngularVelocityCci), so the future position is the current one carried around Z.
private static double3 SiteDirCciAt(IParentBody parent, double dtFuture)
{
    double3 dirNow = SiteDirCcf().Transform(parent.GetCcf2Cci());
    return RotZ(dirNow, parent.GetAngularVelocity() * dtFuture);
}

// Terrain height of the site above the body's mean-radius sphere, sampled from KSA's own heightmap (the game places surface objects at MeanRadius + this).
// The site is fixed in CCF, so the value is cached and only re-sampled when the inputs (or the body) change.

private static double SiteTerrainHeight(IParentBody parent)
{
    if (_s.SiteLatDeg != _s.SiteTerrainCacheLat || _s.SiteLonDeg != _s.SiteTerrainCacheLon
        || !ReferenceEquals(parent, _s.SiteTerrainCacheBody))
    {
        _s.SiteTerrainHeightM = (parent as Celestial)?.GetTerrainHeightFromDirCcf(SiteDirCcf()) ?? 0.0;
        if (!double.IsFinite(_s.SiteTerrainHeightM))
            _s.SiteTerrainHeightM = 0.0;
        _s.SiteTerrainCacheLat = _s.SiteLatDeg;
        _s.SiteTerrainCacheLon = _s.SiteLonDeg;
        _s.SiteTerrainCacheBody = parent;
    }
    return _s.SiteTerrainHeightM;
}
```

2. **Build the "x is up" site frame** (any stable horizontal pair works, because the dynamics and glideslope cone are symmetric in the horizontal plane):

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/Gfold/KsaGfold.cs L45-L52
internal static Frame BuildFrame(double3 siteCci)
{
    double3 up = double3.Normalize(siteCci);
    double3 reference = Math.Abs(up.Z) < 0.99 ? new double3(0, 0, 1) : new double3(1, 0, 0);
    double3 ey = double3.Normalize(double3.Cross(up, reference));
    double3 ez = double3.Cross(up, ey);
    return new Frame(siteCci, up, ey, ez);
}
```

3. **Refuse what the model cannot do**, before solving: atmospheric bodies and engines that cannot follow a throttle.

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/Gfold/KsaGfold.cs L126-L136
internal static string RefusalReason(Vehicle vehicle, IParentBody parent)
{
    // This model assumes constant exhaust velocity and no aerodynamic forces.
    if (parent?.GetAtmosphereReference()?.Physical != null)
        return "G-FOLD is limited to airless bodies. Use terminal hover in atmosphere.";
    if (!KsaEnginePerf.SupportsThrottleControl(vehicle))
        return "G-FOLD requires active, supplied liquid engines with shutdown control.";
    return "";
}

// Maps the solved first-node control to a unit CCI thrust direction and throttle fraction.
```

4. **Build the parameters from the live vehicle.** Thrust and exhaust velocity come from the uncapped engine model at the pressure of the reference point, mass and propellant from the vehicle, and the state is expressed in the site frame with the body rotation removed, so a landing at rest on the ground means final velocity zero:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/Gfold/KsaGfold.cs L67-L69, L82-L88
double pressure = KsaEnginePerf.AmbientPressureAt(parent, refPosCci.Length() - parent.MeanRadius);
(double thrust, double massFlow) = KsaEnginePerf.UncappedAtPressure(vehicle, pressure);
double exhaustVel = massFlow > 0 ? thrust / massFlow : 0.0;
// ...
double3 r = refPosCci;   // vehicle CoM
double3 v = vehicle.Orbit.StateVectors.VelocityCci;
// Surface-relative velocity: the site frame co-rotates with the body, so remove the rotation to make "land at rest on the ground" = vf 0.
double3 vSrf = v - double3.Cross(parent.GetAngularVelocityCci(), r);

double3 rLocal = frame.PointToLocal(r);
double3 vLocal = frame.VecToLocal(vSrf);
```

5. **Solve with a time limit and keep the old plan on failure.** On the re-solve cadence the previous time of flight is carried forward (solve the remaining time) and the full time-of-flight search runs only when that fails; a plan that exceeds the propellant aboard is rejected:

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

6. **Fly the committed plan by time index every frame.** Interpolate the reference state, feed forward the planned thrust acceleration (skipping node 0, whose control is an artifact of the relaxed initial path), and add PD feedback in the site frame:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/GfoldDescent.cs L314-L332
double sf = Math.Clamp(elapsed / plan.Dt, 0.0, n - 1);
int s0 = Math.Clamp((int)Math.Floor(sf), 0, n - 2);
double sfrac = Math.Clamp(sf - s0, 0.0, 1.0);
double3 refPos = Lerp(Node(plan.Position, s0), Node(plan.Position, s0 + 1), sfrac);
double3 refVel = Lerp(Node(plan.Velocity, s0), Node(plan.Velocity, s0 + 1), sfrac);

// Feed-forward THRUST: skip node 0 (its control is the unconstrained artifact), so the first step uses node 1's real, cone-respecting thrust.
double tf = Math.Max(elapsed / plan.Dt, 1.0);
int t0 = Math.Clamp((int)Math.Floor(tf), 1, n - 2);
double tfrac = Math.Clamp(tf - t0, 0.0, 1.0);
double3 ff = Lerp(Node(plan.AccelCmd, t0), Node(plan.AccelCmd, t0 + 1), tfrac);
// The final burn sample is valid only until arrival. A failed re-plan must not repeat it indefinitely.
if (elapsed >= plan.TimeOfFlight)
    ff = new double3(parent.Mu / r.LengthSquared(), 0, 0);

double3 curPos = f.PointToLocal(r);
double3 curVel = f.VecToLocal(vSrf);
double3 fb = _s.GfoldKp * (refPos - curPos) + _s.GfoldKd * (refVel - curVel);
double3 cmd = ff + fb; // local thrust acceleration
```

7. **Convert to throttle and direction.** Divide the demand by full thrust at the live pressure; below a coast fraction the engine is off, above a higher relight fraction it relights, and ignition waits for alignment. The throttle comes from `KsaEnginePerf.CommandForThrust` (see the linked thrust article), the direction is the command clamped to the pointing cone around local up, low-pass filtered, and handed to the flight computer as an attitude target (see [Drive the flight computer and attitude modes from a mod](../../autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md)).
8. **Hand over near the ground.** AFC hands the last stretch to the terminal hover of [approach 3](plan-and-fly-a-powered-landing-3.md) through `TerminalHoverAvailable`, asked before the plan lets go; if the hover refuses, the plan flies to touchdown instead.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Adapters/Gfold/KsaGfold.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Gfold/KsaGfold.cs) | site frame, parameter build, first-control mapping |
| [AdvancedFlightComputer/Features/Guidance/Modes/GfoldDescent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/GfoldDescent.cs#L109-L174) | per-step state machine: gate, hand-off, solve cadence, tracking |
| [AdvancedFlightComputer/Features/Guidance/Modes/GfoldDescent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/GfoldDescent.cs#L178-L299) | plan solve with bracketed time-of-flight search |
| [AdvancedFlightComputer/Features/Guidance/Modes/GfoldDescent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/GfoldDescent.cs#L302-L393) | committed-trajectory tracking |
| [AdvancedFlightComputer/Features/Guidance/Modes/Landing.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Landing.cs#L336-L360) | site direction and terrain height |
| [AdvancedFlightComputer/Features/Guidance/Gfold/GfoldPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Gfold/GfoldPlanner.cs) | problem assembly and time-of-flight search (solver side) |

## Variations and gotchas

- **Airless bodies only.** `RefusalReason` returns a message for any parent with an atmosphere reference; use [approach 2](plan-and-fly-a-powered-landing-2.md) or the terminal hover in atmosphere.
- **A time-limited iterate is not a solution.** Check the solver status (`ConicStatus.Optimal` / `OptimalInaccurate`) and keep flying the previous plan.
- **Inside the last seconds before arrival** (`GfoldMinTf`) no valid flight time exists; AFC freezes the committed plan there rather than re-solving into a degenerate problem.
- **Refused for propellant** rests the full search for 2 s of simulation time while the committed plan keeps flying.
- **Vehicle height.** The allowance for vehicle height is applied as an offset on the target altitude, never by shifting the reference point: an attitude-dependent reference injects modelling error as the vehicle rotates.
- **Per-vehicle state.** Every plan, gain and integrator lives in a per-vehicle state object; the static solver limit is the only shared value.
- The author reports in-game flights only for single-craft ascent; G-FOLD is validated by console checks and harness tests.

## See also

- [Overview](overview.md); [6-DOF MPC on a background thread](plan-and-fly-a-powered-landing-2.md); [Sink-profile hover](plan-and-fly-a-powered-landing-3.md); [Time-sliced deorbit planner](plan-and-fly-a-powered-landing-4.md)
- [G-FOLD powered descent (mod feature)](../../../../mods/ksa-advancedflightcomputer/features/gfold-powered-descent.md)
- [Build a site frame and compute a launch window](../../transfers/build-a-site-frame-and-compute-a-launch-window/overview.md) for the same site-frame idea on the ascent side
- [Run an autopilot for every vehicle each step](../../autopilot/run-an-autopilot-for-every-vehicle-each-step/overview.md)

[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
