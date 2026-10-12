---
type: How-To
title: Shell and missile drag with the same density read
description: Drag every round against a fixed reference air with the same density read, add point gravity back-dated to the sub-step plus the body's own fall, and solve a lead on a moving target by flying simulated shells through the identical model
category: physics
group: mod-simulation
topic: apply-drag-and-gravity-to-a-mod-simulated-body
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/ksarmory/features/point-gravity-and-atmosphere-model
  - mods/ksarmory/features/unguided-ballistic-lead
techniques: [quadratic-drag, custom-physics, root-finding]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types:
  - KSA.Celestial
  - KSA.IParentBody
  - KSA.Astronomical
  - KSA.PhysicalAtmosphereReference
  - KSA.Rendering.Water.Data.OceanReference
tags: [physics, drag, gravity, ballistics, lead, back-dating, broyden, atmosphere]
---

Outcome: shells and missiles feel the same air and water as the game's craft, are pulled by a body whose own fall is
accounted for, and a gun lays a shot that actually meets a moving target. Prerequisites: StarMap, and a body
integrated in sub-steps ([approach 1](../simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-1.md)).
The model is deliberately simple (a point mass and quadratic drag) but careful about frames and epochs, because each
shortcut it avoids was measured to cost hundreds of metres.

## Background: how the game does it

KSA samples celestial positions once per frame, at the end of the step just applied; a round read part-way through
the step would otherwise be pulled toward a body a step of its ~30 km/s away. The density curve and `Mu` are the
same data as in [approach 1](apply-drag-and-gravity-to-a-mod-simulated-body-1.md):

```csharp
// current/decomp/KSA/PhysicalAtmosphereReference.cs L85-L88
	public double GetAtmosphericDensityAtAltitude(double altitudeInMeters)
	{
		return (double)SeaLevelDensity * Math.Exp((0.0 - altitudeInMeters) / ScaleHeight.InMeters());
	}
```

`Celestial.Parent` is typed as the interface `IParentBody`; testing `is Celestial` instead compiles and silently
returns zero for every body, which the mod's comment warns about.[^ksa-celestial] `IParentBody.Mu` is a
default interface member (`Mass * 6.6743E-11`).[^ksa-iparentbody]

## Lifecycle and timing

Everything runs in `frame:draw-ui` inside the mod's once-per-frame step (normally `StarMapAfterGui`). The system
samples the body velocity once per frame beside the platform, then every sub-step asks for gravity and air density
with a negative `secondsIntoFrame`, so body and round are compared at the same instant. Celestial positions read are
those of the step KSA just applied in `PrepareFrame`. All `KsaWorld` readers catch exceptions and return documented
fallbacks (zero gravity, density 1.0 for an unreadable atmosphere) because they run inside the frame hook. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and
[epoch back-dating](../simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-4.md).

## Steps

1. Quadratic drag against ground-relative velocity, zero in a vacuum, at rest or with no drag constant. The constant
   comes from shape (`Medium.DragK(mass, calibre, Cd)`) when given, else a hand-set value:

```csharp
// src/KSArmory/Sim/Medium.cs L113-L123
    public static double3 Drag(double3 localVelocity, MunitionProfile munition, double densityRatio)
    {
        ArgumentNullException.ThrowIfNull(munition);

        double airspeed = Vec.Len(localVelocity);

        double k = munition.AppliedDragK;
        if (k <= 0.0 || airspeed <= 1e-6 || densityRatio <= 0.0) return Vec.Zero;

        return localVelocity * (k * airspeed * densityRatio);
    }
```

2. One reference density for every body (1.225 kg/m3), so a round is dragged by the air actually present. The density
   read mirrors the golf ball's, with the same airless-is-zero rule and no `IsValid` gating; it runs both with and
   without the ocean (an air-only variant for aiming at a point on the sea):

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L2526-L2537
            AtmosphereReference? atmosphere = body.GetAtmosphereReference();
            if (atmosphere?.Physical is not { } air) return 0.0;

            double seaLevel = air.SeaLevelDensity;
            double scaleHeight = air.ScaleHeight.InMeters();
            if (!(seaLevel > 0.0) || !(scaleHeight > 0.0)) return 0.0;

            if (altitude < 0.0) altitude = 0.0;
            if (altitude >= air.Height) return 0.0;

            double ratio = air.GetAtmosphericDensityAtAltitude(altitude) / Medium.ReferenceDensityKgPerM3;
            return double.IsFinite(ratio) && ratio >= 0.0 ? ratio : 1.0;
```

3. Gravity: `mu / r^2` toward the centre, zero inside a metre of it, aimed at the body's centre back-dated to the
   sub-step, plus the body's own fall toward its primary:

```csharp
// src/KSArmory/Sim/PointGravity.cs L21-L38
    public static double3 Toward(double3 centreEcl, double mu, double3 positionEcl)
    {
        if (mu <= 0.0) return Vec.Zero;

        double3 toCentre = centreEcl - positionEcl;
        double dist2 = Vec.Len2(toCentre);
        if (dist2 < 1.0) return Vec.Zero;

        return Vec.Unit(toCentre) * (mu / dist2);
    }

    /// <summary>
    /// What a round at <paramref name="positionEcl"/> falls with, <paramref name="secondsIntoFrame"/>
    /// against the body's sample — negative, since the sample is at the step's end.
    /// </summary>
    public static double3 OnRound(double mu, double3 bodySampleEcl, double3 bodyVelocityEcl,
                                  double3 bodyFallEcl, double3 positionEcl, double secondsIntoFrame)
        => Toward(bodySampleEcl + (bodyVelocityEcl * secondsIntoFrame), mu, positionEcl) + bodyFallEcl;
```

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L2220-L2235
    public static double3 BodyFallEcl(Celestial body)
    {
        try
        {
            // IParentBody, not Celestial: Celestial.Parent is declared as the interface, and a
            // planet's primary need not be one of the concrete type. Testing for Celestial compiles,
            // reads as a null check, and silently answers zero for every body in the game.
            if (body.Parent is not { } primary) return Vec.Zero;

            return PointGravity.Toward(primary.GetPositionEcl(), primary.Mu, body.GetPositionEcl());
        }
        catch
        {
            return Vec.Zero;
        }
    }
```

   The fall is one link deep (Earth to Sol is exact; a lunar shot loses Earth's own fall toward Sol).
4. Ground velocity for air-relative speed: `body.GetVelocityEcl() + cross(GetAngularVelocityCce(), r)`, with spin in
   Cce and not Cci (the frames differ by the axial tilt, which invents up to 190 m/s at Earth's surface speed).
5. Lead a moving target. Tier 1 is a fixed-point solve in vacuum. Start with `t = range / muzzleSpeed`, predict the
   target at `t`, update `t` from the new range, and stop when `t` moves less than 1e-7 s. Out of passes means the
   target is outrunning the round: return no solution rather than the last iterate. Aim above the intercept by the
   drop. Both velocities are taken in and subtracted inside, because both carry the planet's ~29.8 km/s:

```csharp
// src/KSArmory/Sim/BallisticLead.cs L80-L129
    public static bool TrySolve(double3 shooterPos, double3 shooterVelocity,
                                double3 targetPos, double3 targetVelocity, double3 targetAccelerationEcl,
                                double muzzleSpeed, double3 gravityEcl, out double3 aimPoint,
                                out double flightTimeSeconds)
    {
        aimPoint = targetPos;
        flightTimeSeconds = 0.0;
        if (!(muzzleSpeed > 0.0) || !double.IsFinite(muzzleSpeed)) return false;
        if (!Vec.IsFinite(shooterPos) || !Vec.IsFinite(targetPos)
            || !Vec.IsFinite(shooterVelocity) || !Vec.IsFinite(targetVelocity)
            || !Vec.IsFinite(targetAccelerationEcl))
        {
            return false;
        }

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
    }
```

6. Tier 2 flies the shell. Distance over muzzle speed underestimates a dragged shell's flight time (a five-inch shell
   15.7 km out at 80 degrees flies 43.4 s, not 19.4 s). Fly one shell through the same gravity and density callbacks
   to its closest approach to the target, correct the barrel direction with a 3x3 "how a barrel turn moves the miss"
   matrix updated by Broyden's rule after each pass, and stop at 0.1 m or when the miss has not shrunk by a fifth in
   four passes:

```csharp
// src/KSArmory/Sim/BallisticLead.cs L236-L318
        const double StallProgress = 0.8;
        const int StallPasses = 4;
        double bestMiss = double.MaxValue;
        int stalled = 0;

        // How a turn of the barrel moves the miss, as the three columns of a matrix, corrected along each turn by
        // what that turn did (Broyden's update) -- so no pass flies more than its one shell.
        double3 byX = Vec.Zero, byY = Vec.Zero, byZ = Vec.Zero;
        double3 lastMiss = Vec.Zero, lastTurn = Vec.Zero;

        static double3 Moves(double3 byX, double3 byY, double3 byZ, double3 turn)
            => (byX * turn.X) + (byY * turn.Y) + (byZ * turn.Z);

        for (int i = 0; i < MaxPasses; i++)
        {
            if (!flight.TryClosestApproach(direction * muzzleSpeed, out double time,
                                           out double3 round, out double3 target))
            {
                return false;
            }

            double3 miss = target - round;
            double reach = Vec.Len(round);
            if (!(reach > 1.0)) return false;

            double missBy = Vec.Len(miss);
            if (missBy <= FlownToleranceMetres)
            {
                aimPoint = shooterPos + (direction * Vec.Len(target));
                flightTimeSeconds = time;
                return Vec.IsFinite(aimPoint);
            }

            if (missBy < bestMiss * StallProgress)
            {
                bestMiss = missBy;
                stalled = 0;
            }
            else if (++stalled >= StallPasses)
            {
                return false;
            }

            // Flat, a turn moves the shell by the reach times the angle, and turning along the miss takes it out.
            // Lobbed, the shell comes down steeper than it went up, so a turn upwards moves its path mostly along
            // itself: turned along the miss the correction shrinks by the square of that cosine and is gone before
            // the barrel reaches 45 degrees -- a shell that goes 64.8 km could not be laid past 55. So the flat
            // answer is only where it starts, and what a turn really does is learnt from the turns flown.
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
        }

        // Out of passes is walking rather than converging, as in the fixed-point solve above.
```

7. Tier 3, beyond reach: lay the gun to throw as far as it can along the way, reusing the answer for half a second
   unless the target moves 0.2 percent of the range, because each search flies shells for most of their life.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/PointGravity.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PointGravity.cs) | Inverse square, back-dated, plus body fall |
| [src/KSArmory/Sim/Medium.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Medium.cs) | Drag, buoyancy, chute, drag constant, step bound |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L2158-L2232) | `GravityAt`, `PullOnRound`, `BodyFallEcl` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L2493-L2541) | `MediumDensityRatioAt`: ocean, air, traps |
| [src/KSArmory/Sim/BallisticLead.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BallisticLead.cs) | The three-tier lead solver |

## Variations and gotchas

- `PhysicalAtmosphereReference.IsValid()` needs `ScaleHeight.IsValid()`, and `DistanceReference.IsValid()` is
  `|value| > 100000`; every realistic atmosphere reports invalid, so check `SeaLevelDensity > 0` and `ScaleHeight > 0`.
- Density falls off on a kilometres scale height; the faithful step in air is 0.05 s (about 1.7 m of impact shift per
  millisecond of frame on a deorbit).
- Flown solves are the mod's most expensive per-frame work: cache them and bound the passes.
- Buoyancy (`g * (1 - ratio / neutralDensityRatio)`) lets a torpedo swim at its neutral density.
- Lunar bomb: reading "no reference" as 1.0 put Earth's sea-level air on the Moon and a bomb released 7 m over the
  ground reached 113 m/s.

## See also

- [Overview and comparison](overview.md)
- [Density ratio and surface gravity (KSAGolf)](apply-drag-and-gravity-to-a-mod-simulated-body-1.md)
- Mod features: [KSArmory point gravity and atmosphere model](../../../../mods/ksarmory/features/point-gravity-and-atmosphere-model.md), [unguided ballistic lead](../../../../mods/ksarmory/features/unguided-ballistic-lead.md)
- [Analytic closest approach](../prevent-tunnelling-of-fast-bodies/prevent-tunnelling-of-fast-bodies-1.md) for the hit test of the same rounds

[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
