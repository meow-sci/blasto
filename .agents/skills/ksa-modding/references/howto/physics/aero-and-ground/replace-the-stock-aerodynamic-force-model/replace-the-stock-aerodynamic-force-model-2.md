---
type: How-To
title: Speed of sound from the atmosphere reference for a measurement tool
description: Measure the drag and lift of whatever aerodynamic model is active by calling ComputeDerivatives on a copied vehicle state at chosen Mach numbers and angles of attack, with Mach derived from the game atmosphere on the main thread
category: physics
group: aero-and-ground
topic: replace-the-stock-aerodynamic-force-model
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/drag-table-measurement
  - mods/ksa-advancedflightcomputer/features/aerodynamic-glide-impact-steering
techniques: [custom-physics, reflection]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker, harmony-postfix KSA.Program.DrawProgramMenusHook]
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types:
  - KSA.PhysicsStates
  - KSA.PhysicsEnvironment
  - KSA.PhysicalAtmosphereReference
  - KSA.AtmosphereReference
  - KSA.Vehicle
  - KSA.VehicleProperties
tags: [physics, aerodynamics, drag, lift, mach, speed-of-sound, measurement, atmosphere]
---

Outcome: a table of drag coefficient and lift coefficient over Mach number and angle of attack for the craft the player
is flying, measured from the force the game actually applies, so the numbers follow stock aerodynamics, a transpiled
model such as [KSAero](replace-the-stock-aerodynamic-force-model-1.md), or any other patch inside `ComputeDerivatives`
without the measuring code knowing which. Prerequisites: StarMap and Harmony (for the optional `PrepareWorker`
hook). Demonstrated by KSA-AdvancedFlightComputer, whose boostback and ascent planners need `Cd(Mach, alpha)`, and whose
glide phase steers with the measured lift curve.

## Background: how the game does it

The force model is a pure static function. `PhysicsStates.ComputeDerivatives` takes the vehicle's bubble origin, kinematic
state, properties and environment by `in`, the substep timing and the thrusters and chutes, and returns a `Disturbances`
value; it does not write to its arguments.[^ksa-physicsstates]

```csharp
// current/decomp/KSA/PhysicsStates.cs L956
	public static Disturbances ComputeDerivatives(in BubbleOrigin origin, in KinematicStates kinematic, in VehicleProperties props, in PhysicsEnvironment environment, double baseIntraStepTime, double dt, double3 positionOriginBub, double3 velocityOriginBub, ReadOnlySpan<ActiveNozzle> thrusters, ReadOnlySpan<ActiveChute> chutes)
```

Everything it reads comes from the vehicle structs, which the main thread can expose by reference and copy by value:

```csharp
// current/decomp/KSA/Vehicle.cs L537-L543
	public ref readonly BubbleOrigin BubbleOrigin => ref _bubbleOrigin;

	public ref readonly KinematicStates KinematicStates => ref _kinematicStates;

	public ref readonly VehicleProperties Props => ref _props;

	public ref readonly PhysicsEnvironment PhysicsEnvironment => ref _environment;
```

The airspeed the drag sees is built from the bubble velocity minus the rotation of the parent body in the CCI frame; the
game exposes that as `PhysicsStates.ComputeAirVelocityBody`.[^ksa-vehicle]

```csharp
// current/decomp/KSA/PhysicsStates.cs L181-L190
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static float3 ComputeAirVelocityBody(in BubbleOrigin origin, in KinematicStates kinematic, in PhysicsEnvironment environment)
	{
		double3 velocityBub = GetVelocityBub(in origin, in kinematic);
		if (origin.BubFrame.IsCci())
		{
			velocityBub -= double3.Cross(origin.Parent.GetAngularVelocityCci(), GetPositionBub(in origin, in kinematic));
		}
		return float3.Pack(velocityBub.Transform(kinematic.Body2Phys.Inverse()));
	}
```

The environment carries the air: `PhysicsEnvironment.AtmosphericPressure` and `AtmosphericDensity` are `float`, and the
body's atmosphere is `SeaLevelPressure * exp(-h / H)` and `SeaLevelDensity * exp(-h / H)`.[^ksa-physicsenvironment][^ksa-physicalatmospherereference]
Pressure over density is therefore constant on a body, and so is the speed of sound.

## Lifecycle and timing

- **Where the sweep runs: the main thread, on demand.** `KsaAeroSweep.TryBuild` is called from the guidance step (the
  `Vehicle.PrepareWorker` prefix, `frame:dispatch-solvers`) and from the boostback panel's resample
  (`frame:draw-ui`).[^afc-guidancefeature][^afc-boostbackgauge] `Universe.ExecuteNextVehicleSolvers` calls
  `PrepareWorker` per vehicle on the main thread inside `Program.PrepareFrame`, after the previous step has been applied
  and before the next one is queued, so the copied structs are settled.[^ksa-universe][^ksa-program]
- **Why calling the force function is safe while workers run.** The probe copies the four structs once and then only
  calls the static function on the copies. `ComputeDerivatives` writes nothing; it reads the parent body's `Mu` and angular
  velocity, which the vehicle step does not change. The main-thread vehicle structs change only inside
  `ApplyVehicleSolvers` (see the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-physics-step)),
  so copying them during the draw is equally safe.
- **Cost.** A sweep is Mach points times alpha points times 24 roll samples force evaluations, so it runs on engagement or
  resample, never every frame. The result records mass and bounding box so a stale table is recognisable.
- **The speed of sound is read on the main thread** from the body's `AtmosphereReference`, because the sweep chooses the
  airspeed for each Mach point itself; the solver's own substeps are untouched.

## Steps

1. **Mirror the atmosphere and derive the speed of sound.** Read `AtmosphereReference.Physical` from the vehicle's parent
   body and build a small immutable atmosphere from `SeaLevelDensity`, `SeaLevelPressure` and `ScaleHeight.InMeters()`.
   Reject non-positive terms (do not trust the game's `IsValid`, which tests an astronomical 100 km bar) and treat a body with
   no atmosphere as "use a nominal one": the table is still meaningful. AFC's `BuildAtmosphere`:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs L344-L360
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
```

   The atmosphere type computes the speed of sound once, because `p0 / rho0` is the same at every altitude. For KSA's Earth
   that is `sqrt(1.4 * 101325 / 1.225)`, 340.3 m/s; gamma is the caller's choice because the game models no composition:

```csharp
// AdvancedFlightComputer/Features/Guidance/Numerics/Flight/Atmosphere.cs L96
        SpeedOfSound = Math.Sqrt(gamma * seaLevelPressure / seaLevelDensity);
```

   Mach for any speed is then `speed / SpeedOfSound` (`ExponentialAtmosphere.Mach`, same file L120). The mirror is checked
   against the game's `GetAtmosphericDensityAtAltitude` at 201 altitudes on every resample (`AtmosphereMirrorError`), so a
   unit change or a mod that overrides the atmosphere shows up as a number instead of a plausible but wrong plan.

2. **Copy the structs and set up free air.** Copy the origin, kinematic state, properties and environment into a probe
   object, then on the copy: force `InPhysicsRadius`, set the mirrored sea-level density and pressure, zero the ocean
   volume and surface area and the spin. Ask the game's own `ComputeAirVelocityBody` for the airspeed at zero physics
   velocity (the frame's share) and verify that a test velocity round-trips to within 0.01 m/s before trusting the probe:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs L290-L326
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

   Because the probe's pressure and density are the mirrored sea-level pair, a model that derives Mach as
   `sqrt(gamma p / rho)` (KSAero does) sees exactly the Mach the sweep asked for.

3. **Call the force function with `dt = 0`.** Set the probe's physics velocity from the wanted body-axis air velocity and
   call the static function with no thrusters and no chutes. `dt = 0` turns off stock's semi-implicit drag limiter, which is a
   property of the integrator and not of the air:

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

   (A limiter that depends on `dt` is also why KSAero's replacement reproduces it; measuring with `dt = 0` compares the model
   itself.)

4. **Remove what is not aerodynamics.** Buoyancy and anything else independent of airspeed comes back in `ForceBody`. For
   each direction on the grid, first record the force at a crawl (0.01 m/s: low enough that its drag is a part in 10^7 of
   the slowest real sample, and not zero so the game still takes its atmospheric branch) and subtract it from every sample
   in that direction.

5. **Sweep Mach, alpha and roll.** Directions are measured from tail-first (alpha 0 means the wind comes at the tail, so
   the air velocity in body axes points along -x), with 24 roll azimuths per alpha: 15 degree steps land on both extremes of
   a box model, so the roll spread is exact and the mean within half a percent. The speed for each Mach point is
   `Mach * SpeedOfSound`, floored at Mach 0.05 because Mach 0 is a limit and not a speed. The force along the airspeed
   becomes drag and the signed cross-flow component along the engine-end tilt becomes lift:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs L187-L217
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

   The reference area is the bounding-box frontal area `pi/4 dy dz` (the same area stock's X faces use), so the Cd values are
   comparable with stock's box model.

6. **Sanity-check, then hand off immutably.** Reject a table whose lowest Mach row has no drag at either end ("the game
   applied no drag to the probe": the probe missed the atmosphere branch), report the largest Cd change across Mach
   (`MachSpread`; zero means the active model has no compressibility) and the roll spread, and build the result entirely
   from copies so it can go to solver threads. Fit the tables with whatever interpolation your planner needs (AFC uses an
   `AeroTable` over the default Mach grid, which is fine through 0.85 to 1.30 because a cubic fit through a coarse
   transonic rise overshoots the peak).

7. **Use the table with Mach computed the same way.** The glide phase turns a wanted lift acceleration into an angle of
   attack by walking the measured `CL(Mach, alpha)` in small steps. Its Mach comes from the same mirrored atmosphere:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs L195-L197
        double mach = aero.Atmosphere?.Mach(speed) ?? 0.0;
        double clMaxAoa = aero.LiftAt(mach, maxAoaRad);
        double cdMaxAoa = aero.Table.Cd(mach, maxAoaRad);
```

   and its dynamic pressure from the vehicle's settled environment:
   `0.5 * vehicle.PhysicsEnvironment.AtmosphericDensity * speed * speed`. The sign of `CL / CD` at the maximum angle of
   attack tells the glide which way the airframe lifts: a model with no lift (stock) holds alpha 0.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs) | Atmosphere mirror, probe, sweep, fit, CSV export |
| [AdvancedFlightComputer/Features/Guidance/Numerics/Flight/Atmosphere.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Numerics/Flight/Atmosphere.cs#L90-L123) | Exponential atmosphere, `SpeedOfSound`, `Mach` |
| [AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs#L182-L330) | Steering with the measured lift curve |
| [AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs#L24-L27) | The `Vehicle.PrepareWorker` prefix that calls the guidance step |
| [AdvancedFlightComputer/Features/Guidance/Ui/Gauges/BoostbackGauge.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Gauges/BoostbackGauge.cs#L294-L308) | Resample from the panel |

## Variations and gotchas

- **Pass `dt = 0`.** A step-dependent limiter distorts the measured force.
- **Only what happens inside `ComputeDerivatives` is measured.** A patch on the call site inside it (the
  [KSAero transpiler](replace-the-stock-aerodynamic-force-model-1.md)) is seen; a force added elsewhere in the
  integrator is not, and neither are thrusters, chutes and the ocean, which the probe switches off.
- **The air is sea level of the parent body.** A model whose coefficients depend on density or pressure other than through
  `q` and Mach is measured at that one condition.
- **No roll input.** The tables store the azimuthal mean; for stock's box a square-section stack rolled 45 degrees presents
  about sqrt(2) times the flank, around 25 percent for a slender stack, which the sweep reports as roll spread. An
  axisymmetric model (KSAero) reports none.
- **Main thread only.** The probe copies `Vehicle.Props`, which the solver rewrites; call the sweep from the draw or
  `PrepareWorker` windows, not from a worker.
- **Check an atmosphere override.** If another mod replaces the density and pressure functions (see the topic
  override-atmosphere-and-ocean-in-the-physics-environment) the mirrored exponential and the game can disagree;
  `AtmosphereMirrorError` is how AFC finds out.
- **A sweep has a cost.** The grid is 24 Mach points times 20 alpha points times 24 rolls in AFC's defaults, hence on demand.

## See also

- [Overview and comparison](overview.md)
- [Call-site transpiler on ComputeDerivatives](replace-the-stock-aerodynamic-force-model-1.md): the model this sweep measures when KSAero is installed
- Mod features: [Drag table measurement](../../../../mods/ksa-advancedflightcomputer/features/drag-table-measurement.md), [Aerodynamic glide impact steering](../../../../mods/ksa-advancedflightcomputer/features/aerodynamic-glide-impact-steering.md)
- [Apply air drag, gravity and aiming to a mod-simulated body](../../mod-simulation/apply-drag-and-gravity-to-a-mod-simulated-body/overview.md): approach 3 predicts impacts in a co-rotating atmosphere with a drag table of this kind
- [Write worker-thread-safe solver patches](../../solver/write-worker-thread-safe-solver-patches/overview.md)

[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
[^ksa-physicalatmospherereference]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^afc-guidancefeature]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs
[^afc-boostbackgauge]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/Guidance/Ui/Gauges/BoostbackGauge.cs
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
