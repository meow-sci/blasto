---
type: Mod Feature
title: Ballistic lead for unguided shells, from a fixed-point solve to flying the shell
description: A gun is laid by iterating time of flight to a tolerance against the target's relative motion and acceleration, then refined by flying simulated shells through drag, gravity and a turning ground with a Broyden-updated aim correction, with a halving search for targets beyond reach
mod: ksarmory
category: physics
techniques:
  - custom-physics
  - sim-boundary
  - fixed-point-iteration
  - shooting-method
  - broyden-update
  - frame-invariant-signatures
hooks: [starmap StarMapAfterGui (via StepOnce)]
lifecycle: [frame:draw-ui]
game_types: [KSA.Vehicle, KSA.KinematicMeasurements, KSA.IParentBody, KSA.Celestial]
tags: [ballistics, gunnery, lead, fire-control, drag, broyden, timed-fuse]
---

A missile steers, so pointing it at the target is enough; a shell cannot, so the barrel must
already contain the target's motion during the flight and the shell's drop over the same time.
At cannon ranges neither is small: a 300 m/s target crosses about 1.4 km during a 4 km shot and the
round falls about 100 m.[^ballistic-lead] KSArmory solves this in three tiers, all in the game-free
`Sim/BallisticLead`, so every refusal and edge case is unit-tested without the game.[^ballistic-lead][^lead-tests][^flown-lead-tests]

## How it works

### Tier 1: fixed-point solve in vacuum

`TrySolve(shooterPos, shooterVel, targetPos, targetVel, targetAccel, muzzleSpeed, gravity, ...)`:

1. Relative velocity `targetVel - shooterVel` (the round leaves with the shooter's velocity).
2. Start with `t = range / muzzleSpeed`; repeat `predicted = target + vRel * t + 0.5 * aTarget * t^2`,
   `t = |predicted - shooter| / muzzleSpeed` until `t` moves less than 1e-7 s (at most 32 passes).
   A fixed pass count was not enough: the iteration contracts by roughly target speed over muzzle
   speed, and against a 576 m/s missile four passes left about 13% of the error.[^ballistic-lead]
3. Out of passes means the target is outrunning the round: return no solution rather than the
   last iterate.
4. Aim above the intercept by the drop: `aim = intercept - 0.5 * gravity * t^2`.[^ballistic-lead]

The solve takes **both** velocities and subtracts them itself, the convention for all of `Sim/`:
both carry the planet's ~29.8 km/s around its star, and leading on the common part throws the aim
point a hundred kilometres wide.[^ballistic-lead][^frames-doc]

### Tier 2: fly the shell (`TrySolveFlown`)

Distance over muzzle speed underestimates a dragged shell's flight time (a five-inch shell 15.7 km
out at 80 degrees flies 43.4 s, not 19.4 s), and the lead, the drop and a timed fuse all hang off
that number.[^ballistic-lead]

| Step | Detail |
|---|---|
| Frame | Everything relative to the ground the round flies over: mount, target and air velocities and the ground's own acceleration (spin) are differenced, so motion common to all of them cannot reach the answer[^ballistic-lead] |
| Seed | The previous solve's direction, else the vacuum solve, else the line of sight |
| Pass | `Flight.TryClosestApproach(direction * muzzleSpeed)` integrates one shell (gravity field and air density supplied as callbacks) and the target (its acceleration split into gravity, drag along airspeed and the rest) to their closest approach[^ballistic-lead] |
| Correction | A 3x3 "how a barrel turn moves the miss" matrix, seeded as the flat-fire answer (`-reach` on the diagonal) and corrected after each pass by **Broyden's update**; the turn square to the barrel that best removes the miss comes from a 2x2 least-squares solve. The flat answer alone fails on lobbed shots, where it vanished before 45 degrees of elevation[^ballistic-lead] |
| Stop | Converged when the closest approach is within 0.1 m; abandoned when the miss has not shrunk by a fifth in four passes (usually out of reach)[^ballistic-lead] |

### Tier 3: beyond reach (`TrySolveFlownOrReach`)

For a target the shell cannot reach, the gun is laid to throw as far as it can along the way,
over the ground when the target is on it (`AlongTheGround`), by halving the fraction of the range.
The mount reuses that answer for half a second unless the target moves 0.2% of the range, because
each search flies shells for most of their life (76 ms on Earth, 215 ms from Mars for the first
search, including JIT).[^ballistic-lead][^weapon-system][^lay-cost-doc]

### Inputs read from the game

| Input | Source |
|---|---|
| Target acceleration | `KsaWorld.AccelerationEcl`: `Vehicle.AccelerationBody` (an accelerometer reading, gravity excluded) rotated by `Body2Cce`, plus gravity, checked by `AccelerationEstimate` against a 0.5 s median of velocity changes and overruled when they disagree by more than 3 m/s^2 (an impulse or a tumbling fragment)[^ksaworld][^acceleration-estimate][^ksa-vehicle] |
| Ground velocity and acceleration | `GroundVelocityAt` and `GroundAccelerationAt = cross(w, cross(w, r))` with `w = GetAngularVelocityCce()`[^ksaworld][^ksa-iparentbody] |
| Gravity and air | Per-position callbacks `LeadGravityAt` / `LeadDensityAt` (air only, never the ocean)[^weapon-system] |

`LeadError.Resolve` splits a burst's miss along the target's track, up and right, and
`SteadyTargetMiss` predicts the miss an unled acceleration causes, so logs show whether a miss was
the lead or the aim.[^lead-error]

## Lifecycle and timing

- **`frame:draw-ui`.** `WeaponSystem.TryGunLay` runs in the system's step inside
  `StepSimulation` (from `[StarMapAfterGui]` via `StepOnce`), after `SampleWorld` read this frame's
  platform and target states from the step KSA applied in `PrepareFrame`.[^weapon-system]
- The time of flight the solve returns is handed to the timed fuse directly, never recomputed,
  so the burst happens where the gun aimed.[^ballistic-lead]
- Flown solves are the mod's most expensive per-frame work; the reuse window and the JIT warm-up
  at startup both exist to keep them off the frame budget.[^lay-cost-doc]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Sim/BallisticLead.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BallisticLead.cs#L80-L127) | Fixed-point vacuum solve |
| [src/KSArmory/Sim/BallisticLead.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BallisticLead.cs#L187-L352) | Flown solve with Broyden correction |
| [src/KSArmory/Sim/AccelerationEstimate.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/AccelerationEstimate.cs) | Accelerometer vs history check |
| [src/KSArmory/Sim/LeadError.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/LeadError.cs) | Miss decomposition for logs |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L1151-L1215) | `TryGunLay`: inputs, reuse window, call |
| [tests/KSArmory.Tests/FlownLeadTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/FlownLeadTests.cs) | Headless tests of the flown solve |

## Snippets

```csharp
// src/KSArmory/Sim/BallisticLead.cs L95-L128
        double3 targetVelocityRelative = targetVelocity - shooterVelocity;

        double flightTime = Vec.Len(targetPos - shooterPos) / muzzleSpeed;

        bool converged = false;

        for (int i = 0; i < MaxPasses; i++)
        {
            double3 predicted = targetPos + targetVelocityRelative * flightTime
                                + targetAccelerationEcl * (0.5 * flightTime * flightTime);
            double range = Vec.Len(predicted - shooterPos);
            double next = range / muzzleSpeed;

            if (!double.IsFinite(next)) return false;

            double moved = Math.Abs(next - flightTime);
            flightTime = next;

            if (moved <= ToleranceSeconds) { converged = true; break; }
        }

        // A solve that ran out of passes has not found an intercept, it has been walking away from
        // one: the target is outrunning the round. Reporting the last iterate would be an aim point
        // presented with the same confidence as a real one.
        if (!converged) return false;

        // Aim above the intercept by exactly what the round will fall, so the two cancel on the
        // way. Sign matters: gravity points down, so subtracting raises the aim.
        double3 intercept = targetPos + targetVelocityRelative * flightTime
                            + targetAccelerationEcl * (0.5 * flightTime * flightTime);
        aimPoint = intercept - gravityEcl * (0.5 * flightTime * flightTime);
        flightTimeSeconds = flightTime;

        return Vec.IsFinite(aimPoint);
```

```csharp
// src/KSArmory/Sim/BallisticLead.cs L284-L315
            if (i == 0)
            {
                byX = new double3(-reach, 0, 0);
                byY = new double3(0, -reach, 0);
                byZ = new double3(0, 0, -reach);
            }
            else if (Vec.Len2(lastTurn) > 0.0)
            {
                double3 surprise = (miss - lastMiss - Moves(byX, byY, byZ, lastTurn)) / Vec.Len2(lastTurn);
                byX += surprise * lastTurn.X;
                byY += surprise * lastTurn.Y;
                byZ += surprise * lastTurn.Z;
            }

            // The turn square to the barrel that best takes out the miss, by least squares.
            double3 up = Vec.AnyPerpendicular(direction);
            double3 side = Vec.Cross(direction, up);
            double3 byUp = Moves(byX, byY, byZ, up);
            double3 bySide = Moves(byX, byY, byZ, side);
            double uu = Vec.Dot(byUp, byUp), us = Vec.Dot(byUp, bySide), ss = Vec.Dot(bySide, bySide);
            double towardsUp = -Vec.Dot(byUp, miss), towardsSide = -Vec.Dot(bySide, miss);
            double det = (uu * ss) - (us * us);

            double3 turn = det > 1e-9 * uu * ss
                ? (up * (((ss * towardsUp) - (us * towardsSide)) / det)) + (side * (((uu * towardsSide) - (us * towardsUp)) / det))
                : Vec.RejectFrom(miss, direction) / reach;
            if (!Vec.IsFinite(turn)) turn = Vec.RejectFrom(miss, direction) / reach;
            turn = Vec.ClampLength(turn, Math.Max(MaxTurnRadians, 2.0 * missBy / reach));

            lastMiss = miss;
            lastTurn = turn;
            direction = Vec.Unit(direction + turn);
```

## Game APIs touched

| Member | Use |
|---|---|
| `Vehicle.AccelerationBody` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L573)), `Body2Cce` | Target's measured acceleration (from `KinematicMeasurements`) |
| `IParentBody.GetAngularVelocityCce()` ([current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L81)) | Ground velocity and centripetal acceleration |
| `Celestial` position, velocity, `Mu`, atmosphere | Through the gravity and density callbacks (see the point gravity feature) |

## Pitfalls and version notes

- **Never pass a pre-subtracted relative velocity across the Sim boundary**; subtract inside the
  tested code, or the planet's orbital velocity leaks into the lead.[^ballistic-lead]
- **Iterate to a tolerance, not a count**, and treat non-convergence as "no solution".[^ballistic-lead]
- **`AccelerationBody` excludes gravity** and spikes on impulses; add gravity back and sanity-check
  it against measured velocity changes before leading with it.[^ksaworld][^acceleration-estimate]
- **Read air only** for a lay; a target on the sea sits on the waterline and water would stop the
  simulated shell in its last step.[^ballistic-lead]
- The flown solve's cost is dominated by steps proving a target out of reach; the levers measured
  for it are a plan, not merged code, at this ref.[^lay-cost-doc]

## See also

- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Point gravity and atmosphere model](./point-gravity-and-atmosphere-model.md)
- [Analytic sub-frame fuses](./analytic-subframe-fuses.md)
- [Fire control and target allocation](./fire-control-and-target-allocation.md)
- [CPA threat classification](./cpa-threat-classification.md)
- [Ground target designation and reach](./ground-target-designation-and-reach.md)
- [JIT warm-up of the kill path](./jit-warmup-kill-path.md)
- [Shell and missile drag with the same density read](../../../howto/physics/mod-simulation/apply-drag-and-gravity-to-a-mod-simulated-body/apply-drag-and-gravity-to-a-mod-simulated-body-2.md) - how-to article demonstrating this feature

[^ballistic-lead]: src/KSArmory/Sim/BallisticLead.cs
[^lead-error]: src/KSArmory/Sim/LeadError.cs
[^acceleration-estimate]: src/KSArmory/Sim/AccelerationEstimate.cs
[^weapon-system]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^lay-cost-doc]: docs/LAY-COST.md
[^frames-doc]: docs/FRAMES-AND-EPOCHS.md
[^lead-tests]: tests/KSArmory.Tests/BallisticLeadTests.cs
[^flown-lead-tests]: tests/KSArmory.Tests/FlownLeadTests.cs
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
