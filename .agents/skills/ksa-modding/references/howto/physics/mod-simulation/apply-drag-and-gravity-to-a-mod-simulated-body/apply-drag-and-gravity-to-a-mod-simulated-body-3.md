---
type: How-To
title: Impact prediction in a co-rotating atmosphere
description: Integrate gravity and drag on air-relative velocity with the atmosphere rotating with the body, using a differentiable integrator so a landing prediction can be steered
category: physics
group: mod-simulation
topic: apply-drag-and-gravity-to-a-mod-simulated-body
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/boostback-impact-prediction]
techniques: [custom-physics, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.Celestial, KSA.IParentBody]
tags: [physics, drag, gravity, atmosphere, co-rotating, impact-prediction, boostback, dual-numbers]
---

Outcome: a prediction of where a coasting booster will hit the ground, including air drag with the air moving with the
planet, accurate enough to steer to a site. KSA-AdvancedFlightComputer's boostback guidance integrates gravity and
drag itself rather than asking the game, because the game exposes no "predict impact" for a given drag model.
Prerequisites: StarMap and Harmony for the host hook (a `Vehicle.PrepareWorker` prefix); the numerical core is plain C#.

## Background: how the game does it

The game's own drag is applied inside the vehicle solver (`ComputeDrag` in `PhysicsStates.ComputeDerivatives`);
nothing outside the solver can read the force it will apply. A predictor must therefore carry its own model of the
same physics: Newtonian gravity from `IParentBody.Mu`,[^ksa-iparentbody] an atmosphere density
profile (the same exponential as `GetAtmosphericDensityAtAltitude`),[^ksa-physicalatmospherereference] and a
drag coefficient table. The one subtlety is the air: it moves with the planet. The velocity that drag acts on is the
vehicle's velocity relative to the co-rotating air.

## Lifecycle and timing

The booster is flown from a `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`, main thread), so it works
even when it is not the controlled vehicle. The predictor and the shooter are synchronous numerical work on the main
thread bounded by step budgets and a 2 s re-solve cadence; attitude and throttle reach the physics step through the
stock flight computer. See [the PrepareWorker window](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-1.md)
and the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. State is position and velocity in the body-centred inertial frame: `x = [rx, ry, rz, vx, vy, vz]`. Use dual
   numbers so the same integrator yields the sensitivity of the impact point to the velocity (the Jacobian steering
   uses): gravity first, `-Mu / r^3 * r`, with a floor under the square root so its derivative is finite at the
   origin:

```csharp
// AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs L80-L101
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
```

2. Air-relative velocity: subtract the rotation of the atmosphere. With the body spinning about `z` at `OmegaZ`, the
   air velocity at `r` is `omega x r = (-w*ry, w*rx, 0)`, so the relative velocity is `v - omega x r`:

```csharp
// AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs L103-L109
        // Air-relative velocity: v - omega x r, with omega = (0, 0, w) so omega x r = (-w*ry, w*rx, 0).
        Dual ax = vx + OmegaZ * ry;
        Dual ay = vy - OmegaZ * rx;
        Dual az = vz;

        Dual speed2 = ax * ax + ay * ay + az * az + 1e-18;
        Dual speed = Dual.Sqrt(speed2);
```

3. Density from altitude above the mean radius, and drag as `-(1/2) rho |v| Cd (A/m) v_air`, with one power of speed
   already in the vector. Above the atmosphere the density returns a hard zero with a zero derivative, so the drag
   term short-circuits to no drag and no drag sensitivity, which is correct, not just convenient:

```csharp
// AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs L111-L124
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

4. Step finely where drag matters and coarsely where the motion is smooth Keplerian. `ImpactOptions` carries the
   target radius (mean radius plus local terrain height if known) and the step sizes.
5. Use the prediction for guidance. The minimum-norm correction uses the velocity Jacobian (rank 2, because a
   crossing point of a sphere stays on the sphere) to decide whether a burn is worth lighting; the powered arc is then
   optimized and re-solved from the live state every 2 s (receding horizon). In the glide, the prediction assumes
   alpha 0, so a PID on the coast impact point steers the angle of attack until the impact lies on the site and the
   command returns to alpha 0 and the prediction comes true.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs) | Drag-integrated impact prediction |
| [AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactSteering.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactSteering.cs) | Minimum-norm impact correction |
| [AdvancedFlightComputer/Features/Guidance/Numerics/Flight/PoweredBurn.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Numerics/Flight/PoweredBurn.cs) | Powered-arc shooter |
| [AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs) | Boostback phases and laws |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceBoosterHandoverTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceBoosterHandoverTest.cs) | Headless hand-over test |

## Variations and gotchas

- The prediction assumes alpha 0 in the glide; a command that holds a different angle makes it wrong.
- The model must match the mod's own drag table, not necessarily the game's: it predicts where this mod's craft will
  land under this mod's commands.
- Co-rotation only matters inside the atmosphere; above it the spin term has no effect.
- Re-solving from the live state every 2 s keeps model error from accumulating.

## See also

- [Overview and comparison](overview.md)
- [Density ratio and surface gravity](apply-drag-and-gravity-to-a-mod-simulated-body-1.md), [shell drag and ballistic lead](apply-drag-and-gravity-to-a-mod-simulated-body-2.md)
- Mod feature: [Boostback impact prediction](../../../../mods/ksa-advancedflightcomputer/features/boostback-impact-prediction.md)

[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-physicalatmospherereference]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
