---
type: Mod Feature
title: Convert a vehicle's state into a landing-site frame through the game's own transforms
description: Build a local site frame from CCI, derive the model's body axes from the measured engine thrust direction, and convert attitude by transforming basis vectors with KSA's own quaternion so no quaternion convention is ever assumed
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [frame-conversion, coordinate-bridge]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker, starmap StarMapAfterGui]
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.IParentBody
  - KSA.Orbit
  - KSA.StateVectors
  - KSA.GimbalController
  - KSA.GimbalControllerData
  - KSA.FlightComputerNavigation
  - KSA.Double3Ex
  - Brutal.Numerics.doubleQuat
tags: [reference-frames, cci, cce, quaternion, attitude, guidance, coordinate-conversion]
---

A guidance model written against textbook conventions (flat ground, gravity along -Z, thrust along body +Z,
scalar-first Hamilton quaternions) does not match KSA, which works in a body-centred inertial frame (CCI)
about a round, rotating body, uses body +X as a rocket's long axis and stores quaternions scalar-last.
`KsaFrameBridge` converts a live vehicle into the 14-element state of the 6-DOF SCvx model and back. Its
author's point, stated in the file header, is that all three mismatches fail silently: a sign error looks
like an unstable controller, not like a bug ([AdvancedFlightComputer/Features/Guidance/Adapters/KsaFrameBridge.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/KsaFrameBridge.cs#L9-L16)).

## How it works

| Mismatch | KSA side | Bridge |
|---|---|---|
| Inertial frame | CCI position and velocity from `Orbit.StateVectors` | `BuildSiteFrame`: origin at the site, `Ez` radially up, `Ex`/`Ey` horizontal from a fixed reference axis; CCI to site is three dot products |
| Body axes | Long axis is body +X; engines report `GimbalControllerData.ThrustDirVehicleAsmb` | `BodyAxes`: model +Z is the thrust direction of the highest-`MaximumThrust` gimbal (activated engines first, all gimbals if none), falling back to +X; model +X is taken from a fixed KSA axis so roll does not wander |
| Quaternion convention | `doubleQuat` with `Identity = (0, 0, 0, 1)`, scalar last | Never reinterpret components: transform the three basis vectors with KSA's own `Transform`, build a rotation matrix, convert that with the same formula the Python reference uses |
| Surface-relative velocity | `IParentBody.GetAngularVelocityCci()` | `v_model = v_cci - omega x r_cci`, because the flat-ground frame rotates with the body |

The body-to-CCI rotation is composed exactly like the game's own navigation does it,
`doubleQuat.Concatenate(vehicle.Body2Cce, parent.GetCce2Cci())`
([current/decomp/KSA/FlightComputerNavigation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputerNavigation.cs#L31-L36)).[^ksa-flightcomputernavigation]
`ToModelState` then fills `[r(3) v(3) q(4) w(3) m(1)]`: site-frame position and surface-relative
velocity, the model attitude (model body to site, composed as site<-CCI * CCI<-KSA body * KSA body<-model
body), `Vehicle.BodyRates` projected onto the model body axes, and `Vehicle.TotalMass`
([AdvancedFlightComputer/Features/Guidance/Adapters/KsaFrameBridge.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/KsaFrameBridge.cs#L94-L142)).

`MatrixToQuat` branches on the largest diagonal so it stays conditioned near `qw = 0`, and pins `qw >= 0`
so successive conversions do not flip sign. `QuatToMatrix` goes the other way for overlays and gauges.

### Two solvers, two frames

The G-FOLD adapter builds its own site frame with **X up** (`KsaGfold.BuildFrame`), while the 6-DOF
bridge is **Z up**; the code comments warn not to cross them
([AdvancedFlightComputer/Features/Guidance/Adapters/Gfold/KsaGfold.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Gfold/KsaGfold.cs#L45-L52),
[AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs#L1016-L1025)).

### Who calls it

| Caller | Use |
|---|---|
| `Modes/SixDof.cs` | Engage and every guidance step: `BuildSiteFrame` at the site's current CCI position, then `ToModelState` |
| `Ksa6DofSetup.cs` | `BodyAxes` to express inertia and engine geometry in model axes ([AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSetup.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSetup.cs#L40-L50)) |
| `SixDofOverlay.cs`, `GfoldGauge.cs` | `BuildSiteFrame` / `QuatToMatrix` to draw planned attitudes ([AdvancedFlightComputer/Features/Guidance/Ui/Overlays/SixDofOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Overlays/SixDofOverlay.cs#L30-L40)) |
| `Boostback.cs`, `Autopilot.cs` | `BodyToCci` to read the vehicle's body axes in CCI |

## Lifecycle and timing

The state reads happen on the main thread. The guidance step runs in the `Vehicle.PrepareWorker` prefix
(`frame:dispatch-solvers`), after `Universe.ApplyVehicleSolvers` has copied the finished physics step
back, so `Body2Cce`, `BodyRates`, `Orbit.StateVectors` and `TotalMass` describe one consistent
committed state (see [guidance vehicle step loop](guidance-vehicle-step-loop.md)). The resulting
`double[]` is plain data, which is what lets the 6-DOF solve run on its own thread without touching game
objects. Overlay and gauge conversions run in `StarMapAfterGui` (`frame:draw-ui`) on the same
committed state.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Adapters/KsaFrameBridge.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/KsaFrameBridge.cs) | Site frame, body axes, quaternion conversion, model state |
| [AdvancedFlightComputer/Features/Guidance/Adapters/Gfold/KsaGfold.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Gfold/KsaGfold.cs) | The X-up frame of the G-FOLD adapter |
| [AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs) | Per-step conversion at engage and during flight |
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/QuaternionBranchCheck.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/QuaternionBranchCheck.cs) | Reproduces a quaternion sign flip from a flight log |

## Snippets

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

## Game APIs touched

| Member | Use |
|---|---|
| `Vehicle.Body2Cce`, `BodyRates` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L476-L518)), `TotalMass` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L567)) | Attitude (body to CCE), body-frame rates, mass |
| `IParentBody.GetCce2Cci()`, `GetAngularVelocityCci()` ([current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L53-L90)) | CCE to CCI rotation; body spin, `(0, 0, GetAngularVelocity())` about CCI +Z, for surface-relative velocity |
| `Orbit.StateVectors` -> `StateVectors.PositionCci`, `VelocityCci` ([current/decomp/KSA/StateVectors.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StateVectors.cs#L10-L12)) | Inertial position and velocity |
| `FlightComputerNavigation.CreateFromAnalyticStates` ([current/decomp/KSA/FlightComputerNavigation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputerNavigation.cs#L31-L36)) | Reference for the body-to-CCI composition |
| `GimbalControllerData.ThrustDirVehicleAsmb`, `MaximumThrust` ([current/decomp/KSA/GimbalControllerData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GimbalControllerData.cs#L15-L19)) | Measured thrust axis that defines model +Z |
| `doubleQuat.Identity`, `Concatenate` ([current/decomp/Brutal.Numerics/doubleQuat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/doubleQuat.cs#L61)), `Double3Ex.Transform(double3, doubleQuat)` ([current/decomp/KSA/Double3Ex.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Double3Ex.cs#L117)) | Scalar-last quaternion; rotating basis vectors |

## Pitfalls and version notes

- Pinning `qw >= 0` does not stop sign flips for a vehicle rotating through `qw = 0`: the author's flight
  log shows consecutive frames on opposite branches turning a 0.2 m defect into 3548 m and fifteen refused
  re-solves. The fix is to align the plan onto the measurement's branch;
  `QuaternionBranchCheck` reproduces it ([tests/AdvancedFlightComputer.Guidance.Tests/Scvx/QuaternionBranchCheck.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/QuaternionBranchCheck.cs#L3-L28)).
- Deriving model +Z from the thrust direction means a vehicle with no gimballed engine falls back to KSA +X;
  a vehicle whose biggest gimbal is off-axis gets that axis.
- Velocity must be surface-relative for a frame fixed to the ground; using `VelocityCci` directly adds
  roughly 465 m/s of eastward error at an Earth-like equator.
- Keep the G-FOLD (X-up) and 6-DOF (Z-up) frames apart; passing one solver's state to the other is silent.

## See also

- [6-DOF SCvx landing MPC](scvx-6dof-landing-mpc.md)
- [G-FOLD powered descent](gfold-powered-descent.md)
- [Guidance attitude and gimbal actuation](guidance-attitude-and-gimbal-actuation.md)
- [Guidance panel and overlays](guidance-panel-and-overlays.md)
- [Transform basis vectors with the game's own quaternion type to build a site-frame attitude](../../../howto/flight-and-orbits/flight-state/read-flight-state-in-a-local-horizon-frame/read-flight-state-in-a-local-horizon-frame-2.md) - how-to article demonstrating this feature
- [Build a flat site frame from CCI and take surface-relative velocity as v minus omega cross r](../../../howto/flight-and-orbits/transfers/build-a-site-frame-and-compute-a-launch-window/build-a-site-frame-and-compute-a-launch-window-2.md) - how-to article demonstrating this feature

[^ksa-flightcomputernavigation]: KSA.FlightComputerNavigation (decompiled, build 2026.10.10.5554)
