---
type: How-To
title: Transform basis vectors with the game's own quaternion type to build a site-frame attitude
description: Convert a vehicle's attitude and surface-relative velocity into a local site frame by rotating known basis vectors with the game's Transform, never assuming a quaternion convention, and derive body axes from the measured thrust direction
category: flight-and-orbits
group: flight-state
topic: read-flight-state-in-a-local-horizon-frame
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/vehicle-state-to-site-frame]
techniques: [frame-conversion, custom-physics]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.IParentBody
  - KSA.GimbalController
  - KSA.GimbalControllerData
  - Brutal.Numerics.doubleQuat
tags: [attitude, quaternion, site-frame, surface-relative, body-axes, guidance]
---

Outcome: a vehicle state (position, surface-relative velocity, attitude, body rates, mass) in a flat local
frame at a site, suitable for a solver written against textbook conventions. KSA-AdvancedFlightComputer's
`KsaFrameBridge` is the demonstrating code.[Convert a vehicle's state into a landing-site frame through the game's own transforms](../../../../mods/ksa-advancedflightcomputer/features/vehicle-state-to-site-frame.md)
Prerequisites: StarMap and Harmony for the guidance hook; the conversion is plain C#.

## Background: how the game does it

A guidance model usually assumes flat ground, gravity along -Z, thrust along body +Z and scalar-first Hamilton
quaternions. KSA works in CCI about a round rotating body, uses body +X as a rocket's long axis (the bridge's
default thrust fallback) and stores `doubleQuat` scalar-last. All three mismatches fail silently, so the bridge
never reinterprets quaternion components: it rotates the three basis vectors with the game's own `Transform`,
builds a rotation matrix, and converts that.[^ksa-advancedflightcomputer-ksaframebridge]
The body-to-CCI rotation is composed like the game's own navigation:
`doubleQuat.Concatenate(vehicle.Body2Cce, parent.GetCce2Cci())`.[^ksa-flightcomputernavigation] [current/decomp/KSA/FlightComputerNavigation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputerNavigation.cs#L31-L36)
Surface-relative velocity needs `IParentBody.GetAngularVelocityCci()`.[current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L87)

## Lifecycle and timing

The state reads run on the main thread. The guidance step runs in the `Vehicle.PrepareWorker` prefix
(`frame:dispatch-solvers`) after `Universe.ApplyVehicleSolvers` copied the finished step back, so `Body2Cce`,
`BodyRates`, `Orbit.StateVectors` and `TotalMass` are one committed state. The result is a plain `double[]`, which
lets the solve run on another thread without touching game objects. Overlay conversions run in
`StarMapAfterGui` (`frame:draw-ui`) on the same state. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Build a site frame** at a CCI position: up is the radial direction, X and Y come from a fixed reference axis (CCI Z, or
   X near the poles). The rows are the axes, so CCI-to-site is three dot products:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/KsaFrameBridge.cs L42-L50
public static SiteFrame BuildSiteFrame(double3 siteCci)
{
    double3 up = double3.Normalize(siteCci);
    double3 reference = Math.Abs(up.Z) < 0.99 ? new double3(0, 0, 1) : new double3(1, 0, 0);
    double3 ex = double3.Normalize(double3.Cross(reference, up));
    double3 ey = double3.Cross(up, ex);
    return new SiteFrame(siteCci, ex, ey, up);
}

```

2. **Get the body axes from the vehicle's own thrust**, not from an assumed layout: take the thrust direction
   (`GimbalControllerData.ThrustDirVehicleAsmb`) of the highest-`MaximumThrust` gimbal among activated engines (all gimbals if none
   is active), falling back to +X:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/KsaFrameBridge.cs L55-L77
public static void BodyAxes(Vehicle vehicle, out double3 mx, out double3 my, out double3 mz)
{
    // Model +Z is the thrust axis. Take it from the highest-thrust gimbal among the activated engines, or among all gimbals when none is activated; fall back to KSA's long axis (+X) if the vehicle has no gimballed engine to measure.
    double3 thrust = new(1, 0, 0);
    double best = 0;
    Span<GimbalController> gimbals = vehicle.Parts.Modules.Get<GimbalController>();
    bool anyActive = KsaGimbalControl.AnyEngineActive(gimbals);
    foreach (GimbalController gc in gimbals)
    {
        if (!KsaGimbalControl.CountsForPlanning(gc, anyActive))
            continue;
        if (gc.Data.MaximumThrust <= best)
            continue;
        best = gc.Data.MaximumThrust;
        float3 d = gc.Data.ThrustDirVehicleAsmb;
        thrust = new double3(d.X, d.Y, d.Z);
    }

    mz = double3.Normalize(thrust);
    double3 reference = Math.Abs(mz.Z) < 0.99 ? new double3(0, 0, 1) : new double3(1, 0, 0);
    mx = double3.Normalize(double3.Cross(reference, mz));
    my = double3.Cross(mz, mx);
}
```

3. **Read the body-to-CCI rotation as columns** by transforming the basis vectors; this is the convention-agnostic step:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/KsaFrameBridge.cs L79-L92
/// <summary>
/// Body -> CCI rotation as three column vectors, read out of KSA's quaternion by transforming the basis vectors. This is the step that makes the whole bridge convention-agnostic: whatever handedness or scalar position KSA uses, its own Transform is the definition, and we only ever consume the resulting matrix.
/// </summary>
public static void BodyToCciColumns(Vehicle vehicle, out double3 c0, out double3 c1, out double3 c2)
{
    doubleQuat body2Cci = BodyToCci(vehicle);
    c0 = new double3(1, 0, 0).Transform(body2Cci);
    c1 = new double3(0, 1, 0).Transform(body2Cci);
    c2 = new double3(0, 0, 1).Transform(body2Cci);
}

/// <summary>KSA's own body->CCI composition, matching FlightComputerNavigation.</summary>
public static doubleQuat BodyToCci(Vehicle vehicle) =>
    doubleQuat.Concatenate(vehicle.Body2Cce, vehicle.Orbit.Parent.GetCce2Cci());
```

4. **Build the model state** with surface-relative velocity `v - omega x r`, the attitude (site<-CCI * CCI<-KSA body *
   KSA body<-model body, composed from the columns), body rates projected on the model axes, and mass:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/KsaFrameBridge.cs L97-L124
public static double[] ToModelState(Vehicle vehicle, in SiteFrame frame)
{
    var x = new double[14];

    Orbit orbit = vehicle.Orbit;
    IParentBody parent = orbit.Parent;
    double3 rCci = orbit.StateVectors.PositionCci;
    double3 vCci = orbit.StateVectors.VelocityCci
                   - double3.Cross(parent.GetAngularVelocityCci(), rCci);

    double3 r = frame.PosToLocal(rCci);
    double3 v = frame.VecToLocal(vCci);
    x[0] = r.X; x[1] = r.Y; x[2] = r.Z;
    x[3] = v.X; x[4] = v.Y; x[5] = v.Z;

    ModelAttitude(vehicle, frame, out double qw, out double qx, out double qy, out double qz);
    x[6] = qw; x[7] = qx; x[8] = qy; x[9] = qz;

    // Body rates into MODEL body axes. KSA reports them in its own body frame, so they need the same axis swap as the attitude - a rate about KSA's long axis is a roll rate about model Z.
    BodyAxes(vehicle, out double3 mx, out double3 my, out double3 mz);
    double3 w = vehicle.BodyRates;
    x[10] = double3.Dot(w, mx);
    x[11] = double3.Dot(w, my);
    x[12] = double3.Dot(w, mz);

    x[13] = vehicle.TotalMass;
    return x;
}
```

5. **Convert the matrix to a quaternion** by branching on the largest diagonal and pin `qw >= 0` so successive
   conversions do not flip sign; `QuatToMatrix` goes back for overlays (see [AdvancedFlightComputer/Features/Guidance/Adapters/KsaFrameBridge.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/KsaFrameBridge.cs#L148-L200)).
6. **Keep two frames apart.** The G-FOLD adapter's site frame is X-up while the 6-DOF bridge is Z-up; mixing them is a silent error.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Adapters/KsaFrameBridge.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/KsaFrameBridge.cs) | site frame, body axes, quaternion conversion, model state |
| [AdvancedFlightComputer/Features/Guidance/Adapters/Gfold/KsaGfold.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Gfold/KsaGfold.cs#L45-L52) | the X-up frame of the G-FOLD adapter |
| [AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs#L1016-L1025) | per-step conversion |
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/QuaternionBranchCheck.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/QuaternionBranchCheck.cs) | reproduces a quaternion sign flip from a flight log |

## Variations and gotchas

- **The roll reference is arbitrary but must be stable**: choose it from a fixed KSA axis so it does not wander
  as the vehicle rotates.
- **Never assume a quaternion convention**; the bridge's author calls a sign error here "looks like an unstable
  controller, not like a bug".
- A vehicle with no gimballed engine falls back to +X as thrust axis; a different layout needs a different fallback.
- The site frame is only locally flat. Rebuild it at the site's current CCI position each step (the site rotates
  with the body) as `SixDof` does.

## See also

- [Overview](overview.md); [ENU concatenation approach](read-flight-state-in-a-local-horizon-frame-1.md)
- [Convert a vehicle's state into a landing-site frame through the game's own transforms](../../../../mods/ksa-advancedflightcomputer/features/vehicle-state-to-site-frame.md)
- [Build a local site frame and compute a launch window into a target plane](../../transfers/build-a-site-frame-and-compute-a-launch-window/overview.md)

[^ksa-advancedflightcomputer-ksaframebridge]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/Guidance/Adapters/KsaFrameBridge.cs
[^ksa-flightcomputernavigation]: KSA.FlightComputerNavigation (decompiled, build 2026.10.10.5554)
