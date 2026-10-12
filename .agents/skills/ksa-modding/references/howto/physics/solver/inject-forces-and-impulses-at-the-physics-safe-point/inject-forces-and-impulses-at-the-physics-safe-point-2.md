---
type: How-To
title: Summed per-part drag-style shove with torque from a blast front
description: Turn a blast front into one linear and one angular impulse per craft by summing a per-part push and its torque about the centre of mass, saturate it at the wind speed, and write it from the PrepareWorker prefix
category: physics
group: solver
topic: inject-forces-and-impulses-at-the-physics-safe-point
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksarmory/features/blast-shove-impulse]
techniques: [impulse-write, harmony-prefix, custom-physics]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:draw-ui, frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.PhysicsStates, KSA.KinematicStates, KSA.FlightPlan, KSA.Part]
tags: [physics, vehicle, impulse, torque, blast, saturation, overpressure, wind, rails, orbit]
---

Outcome: a nuclear burst's wind throws craft that survive it: a rocket on the pad tips away from the burst, a
drone in the air is kicked sideways and set spinning. KSArmory adds no physics to the engine; it computes an
impulse and an angular impulse per craft and adds them to the vehicle's own physics state, using the same
sequence `Vehicle.Split` uses to push two halves apart. Prerequisites: StarMap and Harmony.

## Background: how the game does it

The engine integrates whatever velocity and spin the worker snapshot carries. A push is therefore a change of
`Kinematic.VelocityPhys` and `AngularVelocityPhys` made in the window before the snapshot (see
[approach 1](inject-forces-and-impulses-at-the-physics-safe-point-1.md) and the
[PrepareWorker window](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-1.md)).
`Vehicle.Split` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1527-L1660) is the engine's own example: it brings a railed craft up to date with
`UpdateFromAnalytic`, writes velocity and position, calls `Props.SetOnRails(false)`, and the craft's flight plan
is refreshed. Mass and inertia come from `Vehicle.TotalMass` and `TotalMassPropsBody` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L559-L570).

## Lifecycle and timing

| Phase | What runs | Why |
|---|---|---|
| `frame:draw-ui` | `BlastArrivals.Update` near the end of KSArmory's simulation step, stepped on simulated time so a pause holds the front and warp hurries it | Main-thread state is the step just applied; parts and bounding boxes are stable |
| `frame:dispatch-solvers` (next frame) | `Vehicle.PrepareWorker` prefix: take the queued shove, `TryShove` | A write just before the snapshot is integrated by the next step; a write after it is overwritten at the next `ApplyVehicleSolvers` |
| `frame:draw-ui` (two steps later) | `AttitudeHook.CheckShoves` reads the velocity change back | The worker that integrated the shove has had its results applied |

The wave is evaluated on the main thread against the craft as it is now: each pending part load is
re-measured every step against the front's current radius, because the craft moves between flash and arrival.

## Steps

1. Model the wind, not the overpressure. KSArmory's `BlastWave` uses Kinney and Graham's TNT fit for peak
   overpressure and positive-phase duration, then the Rankine-Hugoniot relations for the wind behind the front.
   The overpressure wraps round a closed body and mostly cancels, so only the wind pushes; the push over the
   whole positive phase is the peak wind pressure times the phase times a decay share `1 - 2/e`:

```csharp
// src/KSArmory/Sim/BlastWave.cs L67-L103
    /// <summary>
    /// Peak dynamic pressure, ½ρu² of the wind behind a front of <paramref name="overpressurePascals"/>
    /// (Rankine–Hugoniot for air). This is what pushes a body along; the overpressure itself wraps
    /// round a closed shape and mostly cancels.
    /// </summary>
    public static double PeakWindPascals(double overpressurePascals, double ambientPascals = SeaLevelPascals)
    {
        if (!(overpressurePascals > 0.0) || !(ambientPascals > 0.0)) return 0.0;

        return 2.5 * overpressurePascals * overpressurePascals / ((7.0 * ambientPascals) + overpressurePascals);
    }

    /// <summary>
    /// How fast the air behind a front of <paramref name="overpressurePascals"/> is moving
    /// (Rankine–Hugoniot for air): the fastest the wind can carry anything, since a body it has
    /// brought up to its own speed feels no more of it.
    /// </summary>
    public static double WindSpeed(double overpressurePascals, double ambientPascals = SeaLevelPascals)
    {
        if (!(overpressurePascals > 0.0) || !(ambientPascals > 0.0)) return 0.0;

        double ratio = overpressurePascals / ambientPascals;
        return SoundMetresPerSecond * (5.0 / 7.0) * ratio / Math.Sqrt(1.0 + (6.0 / 7.0 * ratio));
    }

    /// <summary>
    /// The wind's push over the whole positive phase, in pascal-seconds: times a drag coefficient
    /// and an area it is the impulse a body takes along the blast.
    /// </summary>
    public static double WindImpulse(double chargeKg, double distanceMetres, double ambientPascals = SeaLevelPascals,
                                     double reflection = SurfaceReflection)
    {
        double peak = PeakWindPascals(PeakOverpressurePascals(chargeKg, distanceMetres, ambientPascals, reflection),
                                      ambientPascals);

        return peak * PositivePhaseSeconds(chargeKg, distanceMetres, reflection) * WindImpulseShare;
    }
```

2. Push each part the front reaches, with a lever arm. The linear impulse is a drag-style `Cd * area * windImpulse`
   along the blast direction (Cd 1.2, a cylinder side-on); the angular impulse is the cross product of the part's
   offset from the centre of mass with that push. A tall rocket hit side-on tips because the widest, nearest part
   is offset from the centre of mass. Both are summed per craft, with the highest wind speed kept:

```csharp
// src/KSArmory/Sim/BlastShove.cs L48-L55
    public static (double3 Linear, double3 Angular) OnPart(double3 partCentre, double3 direction, double areaM2,
                                                           double windImpulse, double3 centreOfMass)
    {
        if (!(areaM2 > 0.0) || !(windImpulse > 0.0)) return (Vec.Zero, Vec.Zero);

        double3 linear = Vec.Unit(direction) * (DragCoefficient * areaM2 * windImpulse);
        return (linear, Vec.Cross(partCentre - centreOfMass, linear));
    }
```

```csharp
// src/KSArmory/Ksa/BlastArrivals.cs L365-L379
    private static void Push(Load load, double3 pushAsmb, double3 centreAsmb, double facingM2)
    {
        if (!KsaWorld.TryCentreOfMassAsmb(load.Craft, out double3 com)) return;

        double ambient = BlastWave.SeaLevelPascals * load.AirRatio;
        double wind = BlastWave.WindImpulse(load.ChargeKg, load.Gap, ambient, load.Reflection);
        double speed = BlastWave.WindSpeed(BlastWave.PeakOverpressurePascals(load.ChargeKg, load.Gap, ambient,
                                                                             load.Reflection), ambient);
        (double3 linear, double3 angular) = BlastShove.OnPart(centreAsmb, pushAsmb, facingM2, wind, com);

        double3 away = KsaWorld.VehicleAsmbDirectionToEcl(load.Craft, com - load.PushFromAsmb);

        _pushed.TryGetValue(load.Craft, out (double3 Linear, double3 Angular, double Wind, double3 AwayEcl) so);
        _pushed[load.Craft] = (so.Linear + linear, so.Angular + angular, Math.Max(so.Wind, speed), away);
    }
```

3. Queue one shove per craft per step. `AttitudeHook.Shove` accumulates what is owed and drops the request
   if the hook is not installed, because written anywhere else it would be overwritten:

```csharp
// src/KSArmory/Ksa/AttitudeHook.cs L263-L272
    public static void Shove(Vehicle craft, double3 impulseAsmb, double3 angularImpulseAsmb, double windSpeed,
                             double3 awayFromBurstEcl)
    {
        if (!Installed || !KsaWorld.IsAlive(craft)) return;
        if (!Vec.IsFinite(impulseAsmb) || !Vec.IsFinite(angularImpulseAsmb)) return;

        Shoves.TryGetValue(craft, out (double3 Linear, double3 Angular, double Wind, double3 AwayEcl) owed);
        Shoves[craft] = (owed.Linear + impulseAsmb, owed.Angular + angularImpulseAsmb, Math.Max(owed.Wind, windSpeed),
                         awayFromBurstEcl);
    }
```

4. Apply it in the `PrepareWorker` prefix (`Shoves.Remove(__instance, out shove)` then `TryShove`), and in
   `TryShove` write velocity and spin, take the craft off rails and rebuild its flight plan:

```csharp
// src/KSArmory/Ksa/VehicleCommand.cs L192-L202
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

   Guards before it: a dead craft, one with no physics bubble (`Vehicle.HasPhysicsBubble`, too far off to be
   integrated), a non-positive mass, and non-finite `dv`/`dw` all return `false`. `dv = impulse / TotalMass`; `dw`
   is the angular impulse through `Symmetric3x3.Invert(TotalMassPropsBody.Inertia)`.

5. Read the result back. The prefix records the pre-shove velocity in a probe; two steps later `CheckShoves`
   logs `shove read back on <craft>: velocity changed by X m/s` against what was written and traces ground
   speed, climb angle, height and spin for five seconds. A wrong frame conversion shows as a craft thrown the
   wrong way, and the logged angle from "straight away from the burst" catches it.

## Keep a pushed velocity bounded (warp-stable forces)

A push computed as though the body stayed put overestimates, because the body moves with the wind and feels
less of it. KSArmory clamps the induced velocity to the local wind speed rather than integrating an unbounded
drag, with a saturating law that leaves small pushes unchanged and never passes the limit:

```csharp
// src/KSArmory/Sim/BlastShove.cs L25-L32
    public static double Saturate(double naive, double limit)
    {
        if (!(naive > 0.0)) return 0.0;
        if (!(limit > 0.0)) return naive;

        double x = naive / limit;
        return limit * x / (1.0 + x);
    }
```

`TryShove` uses it twice: the speed change is saturated at the wind speed and the spin is cut by the same share,
because both are the same wind falling off as the craft is carried with it; then the spin is saturated again so
no rim of the craft outruns the wind:

```csharp
// src/KSArmory/Ksa/VehicleCommand.cs L179-L190
        // The wind a craft feels falls as it is carried along, for the turn as much as for the push,
        // so both are cut by the same share; and no rim outruns the wind.
        double naive = Vec.Len(dv);
        double carried = BlastShove.Saturate(naive, windSpeed);
        double share = naive > 0.0 ? carried / naive : 1.0;
        dv *= share;
        dw *= share;

        double rim = KsaWorld.MeanRadius(craft);
        if (rim > 0.0) dw = Vec.Unit(dw) * BlastShove.Saturate(Vec.Len(dw), windSpeed / rim);
        if (!Vec.IsFinite(dv)) dv = Vec.Zero;
        if (!Vec.IsFinite(dw)) dw = Vec.Zero;
```

Because the bound depends on a physical limit and not on the step length, a shove stays sane at any warp.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/BlastShove.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BlastShove.cs) | Saturation, projected area, per-part impulse (game-free, tested headlessly) |
| [src/KSArmory/Sim/BlastWave.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BlastWave.cs) | Overpressure, positive phase, wind speed and wind impulse |
| [src/KSArmory/Ksa/BlastArrivals.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BlastArrivals.cs#L142-L205) | Front following, per-craft summation, hand-off to the hook |
| [src/KSArmory/Ksa/VehicleCommand.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/VehicleCommand.cs#L148-L207) | `TryShove`: the physics-state write |
| [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs#L263-L377) | Queue, prefix application, read-back probe |

## Variations and gotchas

- Never write the velocity from a StarMap hook; only the `PrepareWorker` prefix is integrated.
- A railed craft needs `UpdateFromAnalytic` first, or the impulse is added to a stale integrated state.
- No physics bubble, no shove: a craft too far away to be integrated is skipped.
- The naive impulse is wrong for big pushes; without saturation a craft is thrown faster than the wind that
  threw it.
- Burst scaling uses ambient pressure (Sachs scaling): a burst in thin air pushes less and none in vacuum
  pushes nothing.
- The orbit lines follow the new state only because `SetFlightPlan` is called with a rebuilt orbit.

## See also

- [Overview and comparison](overview.md)
- [Approach 1: velocity deltas in the prefix](inject-forces-and-impulses-at-the-physics-safe-point-1.md), [approach 4: exponential shares and per-step caps](inject-forces-and-impulses-at-the-physics-safe-point-4.md)
- Mod feature: [KSArmory blast shove impulse](../../../../mods/ksarmory/features/blast-shove-impulse.md)
- [Mutate vehicles at a physics-safe point](../../solver/mutate-vehicles-at-a-physics-safe-point/overview.md)
