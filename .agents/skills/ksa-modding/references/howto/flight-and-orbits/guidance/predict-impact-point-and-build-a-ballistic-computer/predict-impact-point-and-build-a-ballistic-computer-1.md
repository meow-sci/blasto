---
type: How-To
title: Predict an impact point with gravity, drag and a measured drag table
description: Integrate gravity and drag on the air-relative velocity to the ground, with a drag table measured from the game's own force function, and measure lift under the active aero model
category: flight-and-orbits
group: guidance
topic: predict-impact-point-and-build-a-ballistic-computer
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/boostback-impact-prediction
  - mods/ksa-advancedflightcomputer/features/drag-table-measurement
techniques: [custom-physics, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker, starmap StarMapAfterGui]
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.PhysicsStates
  - KSA.PhysicsEnvironment
  - KSA.IParentBody
  - KSA.Celestial
  - KSA.CelestialFrameMath
tags:
  - impact-point
  - drag
  - aero-measurement
  - lift
  - co-rotating-atmosphere
  - terrain
  - automatic-differentiation
---
Outcome: from a vehicle's current state, the place and time at which it hits the ground if nothing is done, through the atmosphere the game actually simulates and with the drag of the craft actually flying. A second, smaller outcome is a lift coefficient table, which tells a guidance law which way tilting the craft moves the landing. KSA-AdvancedFlightComputer (AFC) is the mod that demonstrates it.[Boostback impact prediction](../../../../mods/ksa-advancedflightcomputer/features/boostback-impact-prediction.md) [Drag table measurement](../../../../mods/ksa-advancedflightcomputer/features/drag-table-measurement.md)

Prerequisites: StarMap to load the mod and Harmony to hook `Vehicle.PrepareWorker` (only needed if guidance flies on the prediction; a readout needs neither). The integrator in AFC is generic ODE code over a two-double dual number; a plain `double` RK4 is enough if you do not need a Jacobian.

## Background: how the game does it

KSA has no impact predictor. What it has is a force function and a few body constants:

| Piece | Game member | Why it matters |
|---|---|---|
| Air-relative velocity | `PhysicsStates.ComputeAirVelocityBody` | In a CCI bubble frame it subtracts `cross(parent.GetAngularVelocityCci(), position)`, so the air co-rotates with the planet[^ksa-physicsstates] |
| Force on the craft | `PhysicsStates.ComputeDerivatives(in BubbleOrigin, in KinematicStates, in VehicleProperties, in PhysicsEnvironment, double, double, double3, double3, ReadOnlySpan<ActiveNozzle>, ReadOnlySpan<ActiveChute>)` | Static, returns `Disturbances`; callable on copies of the state[^ksa-physicsstates] |
| State to copy | `Vehicle.BubbleOrigin`, `KinematicStates`, `Props`, `PhysicsEnvironment` | `ref readonly` properties, so assigning one to a local copies the struct[^ksa-vehicle] |
| Spin | `IParentBody.GetAngularVelocity`, `GetAngularVelocityCci` | The pole is CCI +Z, so the vector is `(0, 0, w)`[^ksa-iparentbody] |
| Terrain | `Celestial.GetTerrainHeightFromDirCcf` | Height above `MeanRadius` under a body-fixed direction[^ksa-celestial] |

An exponential atmosphere is enough to integrate, but the constants must come from the body: `IParentBody.GetAtmosphereReference()` gives an `AtmosphereReference` whose `Physical` part has `SeaLevelDensity`, `SeaLevelPressure`, `ScaleHeight`, `Height` and `GetAtmosphericDensityAtAltitude`.[^ksa-physicalatmospherereference]

[current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L182-L190) shows the co-rotation term, and [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L956) the force function signature.

## Lifecycle and timing

- **Where it runs.** AFC's boostback mode predicts inside its `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`, main thread), so a booster is predicted even when it is not the controlled vehicle. The readout overlay and the aero sweep run from the panel draw (`frame:draw-ui`, via `StarMapAfterGui` in [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L217-L218)).
- **Why this is safe.** Both read main-thread vehicle state committed by the last `ApplyVehicleSolvers`. `ComputeDerivatives` writes nothing; it returns a value computed from its arguments and the parent's `Mu` and spin, which the vehicle step does not change. Copying the structs for the probe is safe because main-thread vehicle structs change only in `ApplyVehicleSolvers`.
- **Cost.** One prediction is about a millisecond, so the overlay recomputes every 60 ms ([AdvancedFlightComputer/Features/Guidance/Ui/Overlays/BoostbackOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Overlays/BoostbackOverlay.cs#L35)) and the sweep runs on demand, never per frame. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Copy the vehicle state into a probe.** Copy the four structs and rewrite only what the sweep controls: free air at the chosen density and pressure, no ocean, no spin.

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs L290-L306
        public static bool TryCreate(Vehicle vehicle, double density, double pressure, out ForceProbe probe, out string error)
        {
            probe = new ForceProbe
            {
                _origin = vehicle.BubbleOrigin,
                _kinematic = vehicle.KinematicStates,
                _props = vehicle.Props,
                _environment = vehicle.PhysicsEnvironment,
            };

            // Air, no water, no spin: a probe in free air at this body's sea level. Anything this leaves set, like gravity, lands in AccelPhys or in the buoyancy the baseline removes.
            probe._environment.InPhysicsRadius = true;
            probe._environment.AtmosphericDensity = (float)density;
            probe._environment.AtmosphericPressure = (float)pressure;
            probe._environment.OceanVolume = 0.0;
            probe._environment.OceanSurfaceArea = 0.0;
            probe._kinematic.AngularVelocityPhys = double3.Zero;
```

2. **Set airspeed the game's way, then check it.** Ask `ComputeAirVelocityBody` for the frame's share at zero physics velocity, set every probe velocity relative to it, and verify a test velocity round-trips to 1 cm/s.

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs L308-L326
            // The game's own definition of airspeed, asked at zero physics velocity, gives the frame's share; every probe velocity is set relative to it.
            probe._kinematic.VelocityPhys = double3.Zero;
            float3 frameAir = PhysicsStates.ComputeAirVelocityBody(in probe._origin, in probe._kinematic, in probe._environment);
            probe._frameAirPhys = double3.Unpack(in frameAir).Transform(probe._kinematic.Body2Phys);

            // Check the airspeed lands where it was put, against the same game function. A frame this gets wrong would measure the drag of the wrong direction.
            var check = new double3(-100.0, 30.0, 10.0);
            probe._kinematic.VelocityPhys = check.Transform(probe._kinematic.Body2Phys) - probe._frameAirPhys;
            float3 got = PhysicsStates.ComputeAirVelocityBody(in probe._origin, in probe._kinematic, in probe._environment);
            double miss = (double3.Unpack(in got) - check).Length();
            if (!(miss < 0.01))
            {
                error = $"could not set the probe's airspeed (missed by {miss:G3} m/s)";
                return false;
            }

            error = "";
            return true;
        }
```

3. **Read the force with `dt = 0`.** Passing `dt = 0` switches off stock's semi-implicit drag limiter, which belongs to the integrator and not to the air. No thrusters, no chutes.

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs L328-L337
        /// <summary>The body-axis force the game applies at this air velocity (body axes), buoyancy included.</summary>
        public double3 ForceBody(double3 airVelocityBody)
        {
            _kinematic.VelocityPhys = airVelocityBody.Transform(_kinematic.Body2Phys) - _frameAirPhys;

            // dt = 0 turns off the semi-implicit limiter stock applies to its drag, which is a property of the integrator rather than of the air. No thrusters, no chutes.
            Disturbances d = PhysicsStates.ComputeDerivatives(in _origin, in _kinematic, in _props, in _environment,
                0.0, 0.0, _origin.PositionBub, _origin.VelocityBub, default, default);
            return d.ForceBody;
        }
```

4. **Sweep Mach and angle of attack.** Subtract a near-zero-speed baseline (removes buoyancy), average 24 roll azimuths per alpha, and turn force along the airflow into `Cd` and force across it into a signed `CL`:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs L187-L216
        for (int i = 0; i < nm; i++)
        {
            double speed = Math.Max(machGrid[i], MinProbeMach) * air.SpeedOfSound;
            double qa = 0.5 * air.SeaLevelDensity * speed * speed * refArea;

            for (int j = 0; j < na; j++)
            {
                double dragSum = 0.0, liftSum = 0.0, signedLiftSum = 0.0, lo = double.MaxValue, hi = double.MinValue;
                for (int k = 0; k < RollSamples; k++)
                {
                    double3 u = dirs[j, k];
                    double3 force = probe.ForceBody(speed * u) - baseline[j, k];
                    double drag = -double3.Dot(force, u);
                    double3 across = force + drag * u;
                    dragSum += drag;
                    liftSum += across.Length();
                    signedLiftSum += double3.Dot(across, tilts[j, k]);
                    lo = Math.Min(lo, drag);
                    hi = Math.Max(hi, drag);
                }

                double cd = dragSum / RollSamples / qa;
                double cl = signedLiftSum / RollSamples / qa;
                if (!double.IsFinite(cd) || !double.IsFinite(cl))
                {
                    error = $"the game's force function gave a non-finite force at Mach {machGrid[i]:0.##}, alpha {alphaDeg[j]:0}";
                    return false;
                }
                cdTable[i * na + j] = cd;
                clTable[i * na + j] = cl;
```

5. **Mirror the atmosphere and check the mirror.** Build an exponential from the body's sea-level density, pressure and scale height, then compare it with `GetAtmosphericDensityAtAltitude` over the column (the game zeroes density above `Height`) and keep the worst disagreement:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs L344-L380
    private static void BuildAtmosphere(IParentBody parent, Result res)
    {
        AtmosphereReference reference = parent?.GetAtmosphereReference();
        if (reference == null)
            return;

        PhysicalAtmosphereReference phys = reference.Physical;
        double rho0 = phys.SeaLevelDensity;
        double p0 = phys.SeaLevelPressure;
        double h = phys.ScaleHeight.InMeters();

        if (!(rho0 > 0.0) || !(p0 > 0.0) || !(h > 0.0))
            return;

        res.Atmosphere = new ExponentialAtmosphere(rho0, p0, h);
        // IParentBody does not carry a name; Astronomical does, and every body a vehicle can orbit is one.
        res.BodyName = (parent as Astronomical)?.Id ?? "";

        // Sample the whole column, including above the cutoff, so the boundary height is checked too and not just the exponential.
        double worst = 0.0;
        double top = res.Atmosphere.TopAltitude;
        double gameTop = phys.Height;
        for (int i = 0; i <= 200; i++)
        {
            double alt = top * 1.05 * i / 200.0;
            double ours = res.Atmosphere.Density(alt);
            double theirs = phys.GetAtmosphericDensityAtAltitude(alt);

            // KSA zeroes density outside the boundary in PhysicsEnvironment rather than inside GetAtmosphericDensityAtAltitude, which keeps returning the exponential. Compare against the game's EFFECTIVE density, which is what a vehicle experiences.
            if (alt >= gameTop)
                theirs = 0.0;

            double scale = Math.Max(Math.Abs(theirs), 1e-12);
            worst = Math.Max(worst, Math.Abs(ours - theirs) / scale);
        }
        res.AtmosphereMirrorError = worst;
    }
```

6. **Integrate to the ground.** The dynamics are gravity plus drag on `v - omega x r`; alpha is a held assumption (0, engine into the wind) and not a state:

```csharp
// AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs L80-L124
    public readonly void Derivative(Dual t, ReadOnlySpan<Dual> x, Span<Dual> dx)
    {
        Dual rx = x[0], ry = x[1], rz = x[2];
        Dual vx = x[3], vy = x[4], vz = x[5];

        // rdot = v
        dx[0] = vx;
        dx[1] = vy;
        dx[2] = vz;

        // Newtonian gravity.
        // The floor keeps Sqrt's derivative finite at the origin, which the integrator should never visit but a diverged iterate might.
        Dual r2 = rx * rx + ry * ry + rz * rz + 1e-12;
        Dual rlen = Dual.Sqrt(r2);
        Dual gk = -Mu / (r2 * rlen);

        dx[3] = gk * rx;
        dx[4] = gk * ry;
        dx[5] = gk * rz;

        if (!HasDrag)
            return;

        // Air-relative velocity: v - omega x r, with omega = (0, 0, w) so omega x r = (-w*ry, w*rx, 0).
        Dual ax = vx + OmegaZ * ry;
        Dual ay = vy - OmegaZ * rx;
        Dual az = vz;

        Dual speed2 = ax * ax + ay * ay + az * az + 1e-18;
        Dual speed = Dual.Sqrt(speed2);

        Dual rho = Atmosphere.Density(rlen - MeanRadius);
        // Above the atmosphere Density returns a hard zero with a zero derivative, so this short-circuits to no drag AND no drag sensitivity - which is correct, not merely convenient: there is no air there to be sensitive to.
        if (rho.V == 0.0)
            return;

        Dual mach = speed / Atmosphere.SpeedOfSound;
        Dual cd = Table.Cd(mach, new Dual(Alpha));

        // a = -(1/2) rho |v| Cd (A/m) * v_air   -- one power of speed is in the vector
        Dual k = 0.5 * rho * speed * cd * AreaOverMass;
        dx[3] -= k * ax;
        dx[4] -= k * ay;
        dx[5] -= k * az;
    }
```

7. **Call it with two passes.** The first pass is coarse and finds roughly where the craft lands so the terrain height there can be sampled with `GetTerrainHeightFromDirCcf`; the second pass uses that height, low-passed, as the target radius:

```csharp
// AdvancedFlightComputer/Features/Guidance/Ui/Overlays/BoostbackOverlay.cs L118-L143
        var sys = new DragCoastSystem
        {
            Mu = parent.Mu,
            // The body spins about CCI +z (IParentBody.GetAngularVelocityCci is (0,0,w) and the game relies on it), which is exactly the frame DragCoastSystem is written in, so this is a scalar and not a vector.
            OmegaZ = parent.GetAngularVelocity(),
            MeanRadius = parent.MeanRadius,
            AreaOverMass = aero.ReferenceArea / mass,
            // Retrograde throughout: the coast assumption. See DragCoastSystem.
            Alpha = 0.0,
            Table = aero.Table,
            Atmosphere = aero.Atmosphere,
        };

        double3 r0 = orbit.StateVectors.PositionCci;
        double3 v0 = orbit.StateVectors.VelocityCci;

        Span<Dual> x0 = stackalloc Dual[ImpactPredictor.N];
        x0[0] = new Dual(r0.X); x0[1] = new Dual(r0.Y); x0[2] = new Dual(r0.Z);
        x0[3] = new Dual(v0.X); x0[4] = new Dual(v0.Y); x0[5] = new Dual(v0.Z);

        // FIRST PASS, deliberately coarse and with no path recorded. Its only job is to find out roughly WHERE the vehicle lands so the terrain height there can be sampled; a kilometre of error in that lookup point costs a few metres of terrain height, which the second pass then absorbs. Running it at the accurate step sizes would double the cost of the whole overlay to refine a number that is about to be low-passed anyway.
        var scout = ImpactOptions.Default(parent.MeanRadius);
        scout.MaxTime = ImpactHorizonMinutes * 60.0;
        scout.StepAir = scout.StepVacuum;
        scout.PathStride = 0;
        ImpactPrediction p = ImpactPredictor.Predict(sys, x0, scout, _s.ImpactScratch, default);
```

The second pass reads the terrain height at the scouted landing point, low-passes it, and predicts again to that radius:

```csharp
// AdvancedFlightComputer/Features/Guidance/Ui/Overlays/BoostbackOverlay.cs L145-L166
        var opt = ImpactOptions.Default(parent.MeanRadius);
        opt.MaxTime = ImpactHorizonMinutes * 60.0;

        if (p.Hit && parent is Celestial celestial)
        {
            double3 scoutCcf = GroundCcf(parent, in p);
            double h = celestial.GetTerrainHeightFromDirCcf(scoutCcf.NormalizeOrZero());
            if (double.IsFinite(h))
            {
                // Low-passed, not used raw. The sample point moves every recompute, and over rough ground the height under it can change by hundreds of metres - which a shallow descent turns into kilometres of lateral impact movement. See the header.
                double a = Blend(ImpactTerrainTau, ImpactIntervalMs * 0.001);
                _s.ImpactTerrainH = _s.ImpactTerrainValid
                    ? _s.ImpactTerrainH + (h - _s.ImpactTerrainH) * a
                    : h;
                _s.ImpactTerrainValid = true;
            }
        }
        if (_s.ImpactTerrainValid)
            opt.TargetRadius = parent.MeanRadius + _s.ImpactTerrainH;

        p = ImpactPredictor.Predict(sys, x0, opt, _s.ImpactScratch, _s.ImpactPath);
```

8. **Make the answer ground-fixed before comparing it with a site.** `ImpactPrediction.Fx/Fy/Fz` is the inertial impact point carried back by `omega * time of flight`; each path sample is carried back by the rotation that will have happened by then, once, at prediction time:

```csharp
// AdvancedFlightComputer/Features/Guidance/Ui/Overlays/BoostbackOverlay.cs L174-L184
        // Convert the whole path to body-fixed coordinates ONCE, here. Each sample carries its own time, so each is carried back by the rotation that will have happened by the moment the vehicle reaches it.
        double omega = parent.GetAngularVelocity();
        doubleQuat cci2Ccf = parent.GetCci2Ccf();
        int n = Math.Min(p.PathPoints, ImpactPathCapacity);
        for (int i = 0; i < n; i++)
        {
            int b = i * ImpactPredictor.PathStrideDoubles;
            double3 cci = new double3(_s.ImpactPath[b], _s.ImpactPath[b + 1], _s.ImpactPath[b + 2]);
            _s.ImpactPathCcf[i] = RotZ(cci, -omega * _s.ImpactPath[b + 3]).Transform(cci2Ccf);
        }
        _s.ImpactPathCount = n;
```

## Measuring lift and drag under the active aero model

The sweep in step 4 is also the "measure lift and drag" recipe. What to take from it:

- **Sign convention.** Alpha is retrograde-first: 0 is tail-first with the engine into the wind, 180 is nose-first. A positive `CL` means lift towards the side the engine end is tilted off the airflow; a negative one, away from it. A slender body leading with its engine lifts the positive way, a blunt one leading with a heat shield the negative way, and stock's box model makes none.
- **What the numbers say.** `LiftToDragMax`, `RollSpread`, `MachSpread` and `PointingSensitivity` are reported so the approximations are visible: the Cd table has no roll input, so a square-section stack rolled 45 degrees presents about sqrt(2) times its flank (roughly 25 percent for a slender stack).
- **Reference area.** Cd is referenced to `pi/4 * dy * dz` off `Vehicle.BoundingBoxHalfExtentsAsmb`, and the predictor multiplies it back as `AreaOverMass = ReferenceArea / TotalMass`.
- **Immutable result.** The `Result` holds only copies, so it can go to a solver thread; it records mass and bounding box so a stale sweep is recognisable.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs) | probe, sweep, Cd and CL fit, atmosphere mirror |
| [AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs) | `DragCoastSystem`, `ImpactOptions`, `ImpactPredictor.Predict` |
| [AdvancedFlightComputer/Features/Guidance/Numerics/Flight/Aero.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Numerics/Flight/Aero.cs) | `AeroTable` surrogate with alpha convention |
| [AdvancedFlightComputer/Features/Guidance/Numerics/Flight/Atmosphere.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Numerics/Flight/Atmosphere.cs) | `ExponentialAtmosphere` |
| [AdvancedFlightComputer/Features/Guidance/Ui/Overlays/BoostbackOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Overlays/BoostbackOverlay.cs) | throttled recompute, terrain low-pass, ground-fixed path |
| [AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs) | the prediction used by the boostback mode |

## Variations and gotchas

- **Coarse step in vacuum, fine step in air.** The switch is at eight scale heights, not at the atmosphere's top: the game derives `Height` from `MIN_DENSITY` (1e-9 kg/m^3; the mod's comment gives 167 km on Earth),[^ksa-physicalatmospherereference] and switching there made every suborbital flight use the fine step all the way.
- **Altitude datum.** The atmosphere is measured from `MeanRadius` and never from terrain, so keep `MeanRadius` for the atmosphere even when the impact `TargetRadius` includes terrain height.
- **Targeting versus drawing.** Draw to the terrain under the impact point, but target at the site's own radius: otherwise the miss contains a difference of terrain heights, which at a steep impact is as large as the miss itself.
- **Terrain feedback.** A moving impact point resamples a moving terrain height; low-pass it (AFC uses 0.6 s) and snap instead of blending if the point moves more than 20 km in one recompute.
- **Match the game's drag.** The measurement sees an aero mod only if it patches `ComputeDerivatives` or something it calls; a transpiled call site inside the integrator is not visible.
- **Drag is long, not short.** On a boostback-shaped coast the drag landing point lands long of the vacuum one.
- **Only single-craft ascent has been flown in the game by the author;** the boostback paths are exercised by harness tests and console checks.

## See also

- [Overview](overview.md); [Throwaway copy flown in a ground-carried frame](predict-impact-point-and-build-a-ballistic-computer-2.md); [Flown impact check against a closed-form solution](predict-impact-point-and-build-a-ballistic-computer-3.md)
- [Boostback impact prediction (mod feature)](../../../../mods/ksa-advancedflightcomputer/features/boostback-impact-prediction.md) and [Drag table measurement (mod feature)](../../../../mods/ksa-advancedflightcomputer/features/drag-table-measurement.md)
- [Fly a boostback burn and glide a booster to its landing site](../fly-a-boostback-and-glide-to-a-landing-site/overview.md)
- [Read flight state in a local horizon frame](../../flight-state/read-flight-state-in-a-local-horizon-frame/overview.md)

[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-physicalatmospherereference]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
