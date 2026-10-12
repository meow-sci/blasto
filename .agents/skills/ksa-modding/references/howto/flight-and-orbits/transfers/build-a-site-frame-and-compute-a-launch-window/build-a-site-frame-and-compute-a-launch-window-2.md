---
type: How-To
title: Build a flat site frame from CCI and take surface-relative velocity as v minus omega cross r
description: Construct a local frame at a landing site with up radial from the body's CCI position, convert state and attitude into it for a flat-ground solver, and subtract the body's rotation from the velocity
category: flight-and-orbits
group: transfers
topic: build-a-site-frame-and-compute-a-launch-window
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/vehicle-state-to-site-frame]
techniques: [coordinate-bridge]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types:
  - KSA.Vehicle
  - KSA.IParentBody
  - KSA.Orbit
  - KSA.StateVectors
  - KSA.FlightComputerNavigation
  - KSA.Double3Ex
tags: [site-frame, surface-velocity, coordinate-bridge, guidance, flat-ground, body-rotation]
---

Outcome: position, surface-relative velocity, attitude, body rates and mass in a local frame at a landing site, so a solver written against flat ground with gravity along -Z can be fed from a live vehicle and its output converted back.
The mechanics (body axes from thrust, quaternion conversion) are in [Read roll, pitch, heading, angle of attack and sideslip in the local horizon frame](../../flight-state/read-flight-state-in-a-local-horizon-frame/overview.md); this article is about the site frame and the velocity.[Convert a vehicle's state into a landing-site frame through the game's own transforms](../../../../mods/ksa-advancedflightcomputer/features/vehicle-state-to-site-frame.md) Prerequisite: StarMap.

## Background: how the game does it

The flat-ground assumption is locally true in a frame whose origin is the site and whose +Z points radially up. The body rotates about CCI +Z, so the velocity relative to the surface is `Orbit.StateVectors.VelocityCci - cross(parent.GetAngularVelocityCci(), PositionCci)` (the game's `GetSurfaceVelocityCci`). The site is a fixed point on the body, so its CCI position changes with the body's rotation and the frame must be rebuilt at the site's
current CCI position every step.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2954-L2958) [^ksa-iparentbody] [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L84-L90) The body-to-CCI rotation is `Concatenate(vehicle.Body2Cce, parent.GetCce2Cci())`, composed the same way as `FlightComputerNavigation.CreateFromAnalyticStates`.[^ksa-flightcomputernavigation] [current/decomp/KSA/FlightComputerNavigation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputerNavigation.cs#L29-L37)

## Lifecycle and timing

The state reads happen on the main thread in the guidance step, a `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`), after `ApplyVehicleSolvers` copied the finished step back, so the orbit state vectors, body rotation and mass are one committed state. The output is plain `double[]`, which lets the solve run on its own thread. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Build the frame at the site's CCI position.** Up is the radial direction, X and Y come from a fixed reference axis (CCI Z, or X near the poles); the rows are the axes so CCI-to-site is three dot products:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/KsaFrameBridge.cs L18-L50
/// <summary>
/// Local landing frame: origin at the site, +Z radially up, X/Y horizontal.
/// Rows of the rotation are Ex/Ey/Ez, so CCI -> site is a dot product each.
/// </summary>
public readonly struct SiteFrame(double3 origin, double3 ex, double3 ey, double3 ez)
{
    public readonly double3 Origin = origin, Ex = ex, Ey = ey, Ez = ez;

    public double3 PosToLocal(double3 cci)
    {
        double3 d = cci - Origin;
        return new double3(double3.Dot(d, Ex), double3.Dot(d, Ey), double3.Dot(d, Ez));
    }

    public double3 VecToLocal(double3 cci) =>
        new(double3.Dot(cci, Ex), double3.Dot(cci, Ey), double3.Dot(cci, Ez));

    public double3 VecToCci(double3 local) =>
        local.X * Ex + local.Y * Ey + local.Z * Ez;

    public double3 PosToCci(double3 local) => Origin + VecToCci(local);
}

/// <summary>Site frame with +Z up (Ez radially out), unlike Gfold's X-up frame.</summary>
public static SiteFrame BuildSiteFrame(double3 siteCci)
{
    double3 up = double3.Normalize(siteCci);
    double3 reference = Math.Abs(up.Z) < 0.99 ? new double3(0, 0, 1) : new double3(1, 0, 0);
    double3 ex = double3.Normalize(double3.Cross(reference, up));
    double3 ey = double3.Cross(up, ex);
    return new SiteFrame(siteCci, ex, ey, up);
}

```

2. **Subtract the rotation from the velocity** before converting, because the flat-ground frame rotates with the body (`v - omega x r`), then convert position and velocity:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/KsaFrameBridge.cs L97-L114
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

```

3. **Convert attitude and body rates** as in [Read roll, pitch, heading, angle of attack and sideslip in the local horizon frame](../../flight-state/read-flight-state-in-a-local-horizon-frame/overview.md) (basis vectors transformed with the game's own quaternion type), and take mass from `Vehicle.TotalMass`.
4. **Convert the solver's output back** with `VecToCci` / `PosToCci` and the inverse attitude conversion.
5. **Keep two frames apart.** The G-FOLD adapter's site frame is X-up while this bridge is Z-up; the code comments warn not to cross them.
6. **Rebuild each step** at `site` rotated with the body: the site's CCI position at the step time is `siteCcf.Transform(GetCcf2Cci())` (see [Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](../../flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)).

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Adapters/KsaFrameBridge.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/KsaFrameBridge.cs) | site frame, body axes, quaternion conversion, model state |
| [AdvancedFlightComputer/Features/Guidance/Adapters/Gfold/KsaGfold.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Gfold/KsaGfold.cs#L45-L52) | the X-up frame of the G-FOLD adapter |
| [AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs#L1016-L1025) | per-step conversion |
| [AdvancedFlightComputer/Features/Guidance/Ui/Overlays/SixDofOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Overlays/SixDofOverlay.cs#L30-L40) | drawing planned attitudes in the site frame |

## Variations and gotchas

- **Flat only locally.** Gravity along -Z is correct near the site; a long trajectory needs the round-body model.
- **The reference axis at the poles.** When `|up.Z| >= 0.99` the frame falls back to +X, so east is not geographic east; use the ENU construction of [Read roll, pitch, heading, angle of attack and sideslip in the local horizon frame](../../flight-state/read-flight-state-in-a-local-horizon-frame/overview.md) when the heading matters.
- **Rotation sign.** `omega x r` uses `GetAngularVelocityCci()`; a body with retrograde rotation has a negative angular velocity and still works.
- A sign error here looks like an unstable controller, not like a bug; test the surface velocity of a landed craft is near zero.

## See also

- [Overview](overview.md); [launch window](build-a-site-frame-and-compute-a-launch-window-1.md); [east/north frame and terrain](build-a-site-frame-and-compute-a-launch-window-3.md)
- [Convert a vehicle's state into a landing-site frame through the game's own transforms](../../../../mods/ksa-advancedflightcomputer/features/vehicle-state-to-site-frame.md)
- [Read roll, pitch, heading, angle of attack and sideslip in the local horizon frame](../../flight-state/read-flight-state-in-a-local-horizon-frame/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputernavigation]: KSA.FlightComputerNavigation (decompiled, build 2026.10.10.5554)
