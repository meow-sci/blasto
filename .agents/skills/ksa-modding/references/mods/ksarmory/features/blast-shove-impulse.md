---
type: Mod Feature
title: Push a craft with a blast wind by writing an impulse into its physics state
description: Each part the blast front reaches adds a drag-style push and a torque about the centre of mass, saturated so nothing outruns the wind, and the summed impulse is written into the vehicle's kinematic state from the PrepareWorker prefix the way Vehicle.Split does it
mod: ksarmory
category: physics
techniques: [harmony-prefix, custom-physics, impulse-write, per-part-drag, saturation, readback-probe]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker, starmap StarMapAfterGui (via StepOnce)]
lifecycle: [frame:draw-ui, frame:dispatch-solvers]
game_types:
  - KSA.Vehicle
  - KSA.PhysicsStates
  - KSA.KinematicStates
  - KSA.VehicleProperties
  - KSA.MassProperties
  - KSA.FlightPlan
  - KSA.Part
  - BepuUtilities.Symmetric3x3
tags: [impulse, blast, kinematics, physics-state, prepareworker, torque]
---

A nuclear burst's blast wind throws craft that survive it: a rocket on the pad tips away from the
burst, a drone in the air is kicked sideways and set spinning. KSArmory does it with no new physics
in the engine: it computes an impulse and an angular impulse per craft and adds them to the
vehicle's own physics state, using the same sequence `Vehicle.Split` uses to push two halves
apart.[^vehicle-command][^ksa-vehicle] The reusable lesson is the **write window**: the impulse is
queued during the frame and applied from a Harmony prefix on `Vehicle.PrepareWorker`, because a
velocity written anywhere else is overwritten by the worker's results before physics reads it.[^attitude-hook]

## How it works

| Step | Code | Detail |
|---|---|---|
| Front reaches a part | `BlastArrivals.Update` | Each pending part load is re-measured every step against the burst's front as it is now (the craft moves between flash and arrival); when due, the part is struck[^blast-arrivals] |
| Part geometry | `KsaWorld` face/area helper | From `Part.BoundingBoxVehicleAsmb`: centre, half-extents, the face the line from the burst exits through, and the projected area along that line (`BlastShove.ProjectedArea`)[^ksaworld][^blast-shove][^ksa-part] |
| Per-part push | `BlastShove.OnPart` | `linear = unit(direction) * Cd * area * windImpulse` with `Cd = 1.2` (side-on cylinder) and `windImpulse` in Pa-s from `BlastWave.WindImpulse`; `angular = cross(partCentre - CoM, linear)`. Only the wind pushes; overpressure wraps round a closed body and mostly cancels[^blast-shove][^blast-wave] |
| Per-craft sum | `BlastArrivals.Push` | Sums linear and angular impulse per craft, keeps the highest wind speed, then calls `AttitudeHook.Shove` once per craft[^blast-arrivals] |
| Queue | `AttitudeHook.Shove` | Accumulates per craft; dropped if the hook is not installed[^attitude-hook] |
| Apply | `VehicleCommand.TryShove` from the prefix | See below[^vehicle-command] |
| Read back | `AttitudeHook.CheckShoves` | Two steps later logs the velocity change against what was written and its angle from "straight away from the burst", then traces ground speed, climb angle, height and spin for 5 s[^attitude-hook] |

### Applying the impulse

`TryShove` refuses a dead craft or one without a physics bubble (`Vehicle.HasPhysicsBubble`), then:

1. `dv = impulse / Vehicle.TotalMass`; `dw = Invert(TotalMassPropsBody.Inertia) * angularImpulse`
   (BepuUtilities `Symmetric3x3.Invert`).[^vehicle-command][^ksa-vehicle][^ksa-massproperties][^bepu-symmetric]
2. **Saturate**: a push computed as if the body stayed put overestimates, because the body moves
   with the wind. `Saturate(naive, limit) = limit * x / (1 + x)` with `x = naive / limit` caps the
   speed change at the wind speed; the spin is cut by the same share and its rim speed is capped too.[^blast-shove][^vehicle-command]
3. If the craft is on rails, `PhysicsStates.UpdateFromAnalytic(orbit, stateVectors, Body2Cce,
   BodyRates, Situation.Maneuvering)` brings the integrated state up to date first, exactly as
   `Split` does.[^vehicle-command][^ksa-physicsstates]
4. Add `dv` and `dw`, rotated by `Kinematic.Body2Phys`, to `Kinematic.VelocityPhys` and
   `AngularVelocityPhys`; `Props.SetOnRails(false)`; `SetFlightPlan(new FlightPlan(states.ComputeOrbit(...), Hash))`
   so the orbit lines follow the new state.[^vehicle-command][^ksa-kinematicstates][^ksa-vehicleproperties]

## Lifecycle and timing

| Phase | What runs | Why |
|---|---|---|
| `frame:draw-ui` | `BlastArrivals.Update(_lastSimStep)` near the end of `StepSimulation` (KSArmoryMod.cs L944), stepped on simulated time so a pause holds the front and timewarp hurries it[^entry][^blast-arrivals] | Main-thread state is the step just applied; parts and bounding boxes are stable |
| `frame:dispatch-solvers` (next frame) | `Vehicle.PrepareWorker` prefix: `Shoves.Remove(__instance)` then `TryShove`[^attitude-hook] | `PrepareWorker` snapshots the main-thread physics state into the worker; a write just before it is integrated by the next step, a write anywhere after it is overwritten at the next `ApplyVehicleSolvers` |
| `frame:draw-ui` (+2 steps) | `AttitudeHook.CheckShoves` (L947) | The worker that integrated the shove has had its results applied[^entry][^attitude-hook] |

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Sim/BlastShove.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BlastShove.cs) | Saturation, projected area, per-part impulse (game-free, tested headlessly) |
| [src/KSArmory/Ksa/BlastArrivals.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BlastArrivals.cs#L142-L205) | Front following, per-craft summation, hand-off to the hook |
| [src/KSArmory/Ksa/VehicleCommand.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/VehicleCommand.cs#L148-L206) | `TryShove`: the physics-state write |
| [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs#L263-L377) | Queue, prefix application, read-back probe |

## Snippets

```csharp
// src/KSArmory/Ksa/VehicleCommand.cs L187-L202
        double rim = KsaWorld.MeanRadius(craft);
        if (rim > 0.0) dw = Vec.Unit(dw) * BlastShove.Saturate(Vec.Len(dw), windSpeed / rim);
        if (!Vec.IsFinite(dv)) dv = Vec.Zero;
        if (!Vec.IsFinite(dw)) dw = Vec.Zero;

        PhysicsStates states = craft.GetPhysicsStatesMutable();
        if (craft.Situation.IsOnRails())
        {
            states.UpdateFromAnalytic(craft.Orbit, in craft.Orbit.StateVectors, craft.Body2Cce, craft.BodyRates,
                                      Situation.Maneuvering);
        }

        states.Kinematic.VelocityPhys += dv.Transform(states.Kinematic.Body2Phys);
        states.Kinematic.AngularVelocityPhys += dw.Transform(states.Kinematic.Body2Phys);
        states.Props.SetOnRails(isOnRails: false);
        craft.SetFlightPlan(new FlightPlan(states.ComputeOrbit(craft.OrbitColor), craft.Hash));
```

```csharp
// src/KSArmory/Sim/BlastShove.cs L25-L55
    public static double Saturate(double naive, double limit)
    {
        if (!(naive > 0.0)) return 0.0;
        if (!(limit > 0.0)) return naive;

        double x = naive / limit;
        return limit * x / (1.0 + x);
    }

    /// <summary>The face a box presents along <paramref name="direction"/>, in square metres.</summary>
    public static double ProjectedArea(double3 halfExtents, double3 direction)
    {
        double3 d = Vec.Unit(direction);
        return 4.0 * ((Math.Abs(d.X) * halfExtents.Y * halfExtents.Z)
                      + (Math.Abs(d.Y) * halfExtents.X * halfExtents.Z)
                      + (Math.Abs(d.Z) * halfExtents.X * halfExtents.Y));
    }
    // ...
    public static (double3 Linear, double3 Angular) OnPart(double3 partCentre, double3 direction, double areaM2,
                                                           double windImpulse, double3 centreOfMass)
    {
        if (!(areaM2 > 0.0) || !(windImpulse > 0.0)) return (Vec.Zero, Vec.Zero);

        double3 linear = Vec.Unit(direction) * (DragCoefficient * areaM2 * windImpulse);
        return (linear, Vec.Cross(partCentre - centreOfMass, linear));
    }
```

```csharp
// current/decomp/KSA/Vehicle.cs L1639-L1647
		PhysicsStates physicsStatesMutable = GetPhysicsStatesMutable();
		PhysicsStates physicsStatesMutable2 = vehicle.GetPhysicsStatesMutable();
		double3 double12 = splitImpulse * double5;
		physicsStatesMutable.Kinematic.VelocityPhys = velocityPhys + double10 - double12 / TotalMass;
		physicsStatesMutable2.Kinematic.VelocityPhys = velocityPhys + double11 + double12 / vehicle.TotalMass;
		physicsStatesMutable.Kinematic.PositionPhys = positionPhys + double7.Transform(body2Phys);
		physicsStatesMutable.Props.SetOnRails(isOnRails: false);
		physicsStatesMutable2.Kinematic.PositionPhys = positionPhys + double9.Transform(body2Phys);
		physicsStatesMutable2.Props.SetOnRails(isOnRails: false);
```

## Game APIs touched

| Member | Use |
|---|---|
| `Vehicle.PrepareWorker(SimStep)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396)) | Prefix target: the write window |
| `Vehicle.GetPhysicsStatesMutable()` (L922), `HasPhysicsBubble` (L381), `TotalMass` (L567), `TotalMassPropsBody` (L559), `SetFlightPlan` (L632) | State access, guards, mass and inertia, orbit refresh |
| `Vehicle.Split(...)` (L1527) | The engine's own impulse pattern copied here |
| `PhysicsStates.UpdateFromAnalytic`, `ComputeOrbit` ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L835)) | Bring a railed craft up to date; rebuild its orbit |
| `KinematicStates.VelocityPhys`, `AngularVelocityPhys`, `Body2Phys` ([current/decomp/KSA/KinematicStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KinematicStates.cs)) | The velocities written |
| `VehicleProperties.SetOnRails(bool)` ([current/decomp/KSA/VehicleProperties.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleProperties.cs#L72)) | Off rails so the change is integrated |
| `MassProperties.Inertia` ([current/decomp/KSA/MassProperties.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MassProperties.cs)), `Symmetric3x3.Invert` ([current/decomp/BepuUtilities/Symmetric3x3.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/BepuUtilities/Symmetric3x3.cs#L50)) | Angular impulse to spin change |
| `Part.BoundingBoxVehicleAsmb` ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L835)) | Per-part area and lever arm |

## Pitfalls and version notes

- **Never write velocity from a StarMap hook**: the worker's copy is applied over it at the next
  `ApplyVehicleSolvers`; only the `PrepareWorker` prefix is integrated.[^attitude-hook]
- **On-rails craft need `UpdateFromAnalytic` first**, or the impulse is added to a stale
  integrated state; the engine's `Split` does the same.[^vehicle-command][^ksa-vehicle]
- **No physics bubble, no shove**: a craft too far away to be integrated is skipped.[^vehicle-command]
- **The naive impulse is wrong for big pushes**: without the saturation a craft is thrown faster
  than the wind that threw it.[^blast-shove]
- The read-back probe exists because a frame error in the asmb-to-physics conversion shows as a
  craft thrown the wrong way; it logs the angle between the measured change and the written one.[^attitude-hook]

## See also

- [Blast damage and part health](./blast-damage-and-part-health.md)
- [PrepareWorker prefix command window](./prepareworker-prefix-command-window.md)
- [Fly another vehicle](./fly-another-vehicle.md)
- [Shock-front refraction](./shock-front-refraction.md)
- [Burst sound at blast arrival](./burst-sound-at-blast-arrival.md)
- [Safe vehicle destruction and part failure](./safe-vehicle-destruction-and-part-failure.md)
- [Summed per-part drag-style shove with torque from a blast front](../../../howto/physics/solver/inject-forces-and-impulses-at-the-physics-safe-point/inject-forces-and-impulses-at-the-physics-safe-point-2.md) - how-to article demonstrating this feature

[^blast-shove]: src/KSArmory/Sim/BlastShove.cs
[^blast-arrivals]: src/KSArmory/Ksa/BlastArrivals.cs
[^blast-wave]: src/KSArmory/Sim/BlastWave.cs
[^vehicle-command]: src/KSArmory/Ksa/VehicleCommand.cs
[^attitude-hook]: src/KSArmory/Ksa/AttitudeHook.cs
[^ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^entry]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-kinematicstates]: KSA.KinematicStates (decompiled, build 2026.10.10.5554)
[^ksa-vehicleproperties]: KSA.VehicleProperties (decompiled, build 2026.10.10.5554)
[^ksa-massproperties]: KSA.MassProperties (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^bepu-symmetric]: BepuUtilities.Symmetric3x3 (decompiled, build 2026.10.10.5554)
