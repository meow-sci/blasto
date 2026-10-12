---
type: How-To
title: Steer a mod-simulated round by true proportional navigation on a back-dated target sample
description: Null the line-of-sight rotation between a round and a KSA vehicle with true proportional navigation, with the target sample moved back to the round's own epoch, gravity compensation, a lateral-only command and an airframe g limit
category: flight-and-orbits
group: guidance
topic: steer-by-zero-effort-miss
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksarmory/features/proportional-navigation-guidance]
techniques: [sim-boundary, proportional-navigation, closed-loop-guidance]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Vehicle, KSA.Celestial, KSA.IParentBody, KSA.Universe, KSA.Program]
tags:
  - proportional-navigation
  - homing
  - line-of-sight
  - missile
  - guidance
  - epoch
  - back-dating
  - frame-velocity
---
Outcome: a projectile your mod integrates itself (not a KSA `Vehicle`) homes on a KSA craft by true proportional navigation (PN): it turns so the line of sight to the target stops rotating, which flies a collision triangle and leads a crossing target instead of chasing its tail. KSArmory steers every guided round this way; the law is six lines and the work is in what goes into it.[^armory-interceptor] [Proportional navigation guidance](../../../../mods/ksarmory/features/proportional-navigation-guidance.md)

Prerequisites: StarMap for the frame hooks; Harmony only if you also want guidance to keep running while the HUD is hidden. You need your own round state (position and velocity in the ecliptic frame) and a per-frame target sample (`Vehicle.GetPositionEcl()`, `GetVelocityEcl()`).

## Background: how the game does it

KSA has no homing guidance; this is all mod code. What the game provides is the inputs and their epoch:

- **Target samples.** `Vehicle.GetPositionEcl()` and `GetVelocityEcl()` return values cached by `UpdatePerFrameData` when `Universe.ApplyVehicleSolvers` installs the finished step, so they are end-of-step values.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L954-L963)
- **The step.** `Universe.GetLastSimStep()` returns the applied `SimStep` (`DeltaTime`, `NextTime`), and `Universe.GetElapsedTime()` is that step's `NextTime`.[^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2381-L2401)
- **The ground under the round.** `Celestial.GetVelocityEcl()` plus `IParentBody.GetAngularVelocityCce()` crossed with the offset from the body centre is the velocity of the air mass the round flies through; `GetAngularVelocityCce` is the CCI spin `(0, 0, GetAngularVelocity())` rotated into CCE, so the spin axis is the parent pole (CCI +Z).[^ksa-iparentbody] [^ksa-celestial]

Ecliptic positions near a planet carry about 29.8 km/s of orbital motion. That cancels in a difference of two samples taken at the same instant, and nowhere else.

## Lifecycle and timing

| Where | Lifecycle | Why |
|---|---|---|
| `[StarMapAfterGui]` (postfix on `Program.OnDrawUiViewports`) runs `StepOnce` | `frame:draw-ui` | Preferred: rounds are stepped and drawn against the same anchor in one pass |
| Harmony prefix on `Program.OnFrameCelestials` | `frame:celestials` | Fallback when `DrawUI` is false and the GUI hooks do not fire |
| `[StarMapAfterOnFrame]` | `frame:after-on-frame` | Last resort; a frame-claim flag makes sure only one of the three steps per frame |

All three run after `PrepareFrame` applied the vehicle step and dispatched the next one, so the target samples are settled end-of-step values and the worker is busy with the following step. Guidance only reads cached samples and writes nothing back to KSA, so it needs no lock. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Consume the applied step exactly once per frame.** Integrate on the step the engine applied, not on the player delta StarMap passes the hook; a paused game reports zero and a warped one the real span:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L98-L115
    public static double ConsumeSimStep()
    {
        SimStep step = Universe.GetLastSimStep();

        if (_consumedThrough is { } from && !step.NextTime.Equals(from))
        {
            LastLongerOfStepAndSpan = Math.Max(step.DeltaTime, Span(from, step.NextTime));
        }

        _consumedThrough = step.NextTime;

        // The span, not just the step: a frame in which this mod's hook never ran still advanced
        // the world, and GetLastSimStep reports only the most recent one. Taking a screenshot is
        // exactly that case -- ScreenshotCapture sets Program.DrawUI false and KSA guards the call
        // this mod postfixes with it, so the hook is not called at all. Differenced in Int128
        // nanoseconds because absolute universe time is far too large to subtract as double.
        return _stepGate.Consume(step.NextTime, step.DeltaTime, Span);
    }
```

2. **Compute the frame velocity, the ground under the round.** Every direction the airframe cares about (thrust axis, seeker cone, drag, the plane the command is projected onto) uses `localVelocity = VelocityEcl - frameVelocityEcl`, never the absolute ecliptic velocity:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L2045-L2066
    public static double3 GroundVelocityAt(Celestial body, double3 positionEcl)
    {
        try
        {
            // Cce, not the Cci that GetBodyRates answers with: the separation below is a Cce
            // vector, and the two frames differ by the body's axial tilt -- 23.4 degrees on Earth,
            // which at 465 m/s of surface speed invents up to 190 m/s of velocity out of nothing.
            double3 spin = ((IParentBody)body).GetAngularVelocityCce();
            double3 fromCentre = positionEcl - body.GetPositionEcl();

            // The body's own motion without the spin term, which is the honest answer when the
            // spin cannot be read: wrong by at most the surface speed, where guessing zero would
            // be wrong by the whole orbital velocity.
            if (!Vec.IsFinite(spin) || !Vec.IsFinite(fromCentre)) return body.GetVelocityEcl();

            return body.GetVelocityEcl() + Vec.Cross(spin, fromCentre);
        }
        catch
        {
            return Vec.Zero;
        }
    }
```

3. **Back-date the target to the round's epoch, then difference.** The target sample is end-of-step while the round's pre-step state is start-of-step. Each sub-step places the target at `t.PositionEcl + t.VelocityEcl * (elapsedInFrame - frameSeconds)`; without it the line of sight carries a one-frame offset and PN flies a clean intercept on a ghost hundreds of metres away. The seeker gate and the separation delay sit here too:

```csharp
// src/KSArmory/Sim/Interceptor.cs L456-L540
        if (target is { } t)
        {
            // Back-date the target sample to the round's own epoch, then extrapolate to this
            // sub-step.
            // ...
            double3 targetPos = t.PositionEcl + t.VelocityEcl * (elapsedInFrame - frameSeconds);
            double3 targetVel = t.VelocityEcl;
            // ...
            double3 r = targetPos - PositionEcl;
            double3 v = targetVel - VelocityEcl;

            ClosestApproach = Math.Min(ClosestApproach, Vec.Len(r));
            // ...
            bool blind = munition.Guidance == GuidanceMode.AntiRadiation
                         && !OperatorHeld && !t.Emitting && !homingOnMemory;

            SeekerInView = !blind
                           && (munition.Guidance == GuidanceMode.CommandLink
                               || OperatorHeld
                               || Vec.AngleBetween(r, localVelocity) <= munition.SeekerFovRad);

            // Nothing steers until it is clear of what launched it. A rail-launched round leaves
            // along its rail and turns after separation; guiding from the first sub-step turns it
            // into the craft carrying it.
            SteeringCommandEcl = Vec.Zero;
            if (SeekerInView && Age >= munition.SeparationSeconds)
            {
                SteeringCommandEcl = GuidanceAccel(r, v, localVelocity, gravity, munition);
                accel += SteeringCommandEcl;
            }
```

4. **Apply the law.** Line-of-sight rate `omega = (r x v) / |r|^2`, command `N * (omega x Vc)` with closing velocity `Vc = -v`; subtract gravity times a compensation factor so guidance does not fight the fall; keep only the part square to the airflow (an airframe pulls lateral g, it does not add thrust); clamp to the airframe limit (`MaxLateralG * 9.80665`):

```csharp
// src/KSArmory/Sim/Interceptor.cs L618-L632
    internal static double3 GuidanceAccel(
        double3 r, double3 v, double3 missileVelocity, double3 gravity, MunitionProfile munition)
    {
        double rangeSq = Vec.Len2(r);
        if (rangeSq < 1e-6) return Vec.Zero;

        double3 omega = Vec.Cross(r, v) / rangeSq;
        double3 closingVelocity = -v;
        double3 command = Vec.Cross(omega, closingVelocity) * munition.NavConstant;

        command -= gravity * munition.GravityCompensation;
        command = Vec.RejectFrom(command, missileVelocity);

        return Vec.ClampLength(command, munition.MaxLateralAccel);
    }
```

5. **Thrust along the velocity gained since release.** A rail round inherits the launcher's whole velocity; thrusting along its total velocity drives it along the craft's track. Push along `localVelocity - LaunchFrameVelocityLocal` instead, falling back to the release heading:

```csharp
// src/KSArmory/Sim/Interceptor.cs L443-L454
        if (Age <= munition.TotalBoostSeconds)
        {
            double3 axis = Vec.Unit(localVelocity - LaunchFrameVelocityLocal);

            // Nothing gained yet, which is a round its rail imparts no speed to at all. The
            // heading it left along is the only thing that says which way it is pointing.
            if (axis.Equals(Vec.Zero)) axis = Vec.Unit(ReleaseHeadingEcl);

            if (!axis.Equals(Vec.Zero)) accel += axis * munition.BoostAccelAt(Age);
        }

        accel -= Medium.Drag(localVelocity, munition, mediumDensityRatio);
```

6. **Sub-step.** `Interceptor.Step` is called once per 5 ms sub-step inside the frame's step, with symplectic Euler (`VelocityEcl += accel * h`, then `PositionEcl += VelocityEcl * h`). Expect a clean intercept log from the fuse; a miss of hundreds of metres with a small reported fuse distance means the back-dating is missing.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/Interceptor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Interceptor.cs#L407-L632) | `Step` (motor, drag, back-dating, seeker gate, steering) and `GuidanceAccel` |
| [src/KSArmory/Sim/MunitionProfile.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/MunitionProfile.cs#L333-L406) | `NavConstant` (4 on the shipped missiles), `MaxLateralG`, `GravityCompensation`, `SeparationSeconds` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L98-L115) | `ConsumeSimStep` |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L509-L520) | `[StarMapAfterGui]` entry that steps first, then draws |
| [src/KSArmory/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/PreRenderHook.cs) | `OnFrameCelestials` prefix for the hidden-HUD frame |

## Variations and gotchas

- **Never feed an absolute velocity as a direction.** The mod's own test notes the symptom of using `VelocityEcl` as the flight path: drag at Mach 87 and a seeker that loses lock on the first step.
- **Frame-invariant signatures.** Pass absolute positions into the law's entry point and let it difference both ends itself; a test that adds 29.8 km/s to round, target and frame and expects the same miss pins it.
- **Gravity is compensated only laterally.** The bias is subtracted before the projection, so the along-path part still slows a climbing round.
- **No mass and no lift model.** Turn rate is limited only by `MaxLateralG`, independent of speed and air density.
- **Separation is a hard gate.** Below `SeparationSeconds` nothing steers even with the target in view.
- **When the target is not a craft but a place**, a line-of-sight law is the wrong tool for a store released from a climb; see [approach 3](steer-by-zero-effort-miss-3.md).

## See also

- [Overview](overview.md); [ZEM on a predicted impact point](steer-by-zero-effort-miss-2.md); [Seekers, raycast occlusion and uplink loss](steer-by-zero-effort-miss-4.md)
- [Proportional navigation guidance (mod feature)](../../../../mods/ksarmory/features/proportional-navigation-guidance.md)
- [Find the closest approach to a target along a trajectory](../../map-view/find-closest-approach-along-the-flight-plan/overview.md)
- [Understand KSA coordinate frames and epochs](../../flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)

[^armory-interceptor]: KSArmory: src/KSArmory/Sim/Interceptor.cs
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
