---
type: Mod Feature
title: True proportional navigation for mod-flown missiles
description: Guided rounds steer by true proportional navigation on a back-dated target sample, with gravity biased out, the command projected square to the airflow and clipped to an airframe g limit, and boost or sustain stages pushing along the velocity gained since release
mod: ksarmory
category: flight-and-orbits
techniques: [custom-physics, sim-boundary, frame-invariant-signatures, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Vehicle, KSA.Celestial, KSA.IParentBody, KSA.Universe, KSA.Program]
tags:
  - guidance
  - proportional-navigation
  - missiles
  - motors
  - boost
  - sustain
  - ecliptic-frame
  - back-dating
  - projectiles
---

Every guided round in KSArmory (the Pantsir's 57E6, the AIM-9J, the AIM-120C, the AGM-88) is an
`Interceptor` the mod integrates itself, and it steers by textbook **true proportional navigation**:
null the rotation of the line of sight and the round flies a collision triangle, so it leads a
crossing target instead of chasing its tail. The law itself is six lines. Most of the work is in
what goes into it: a target sample moved back to the round's own instant, velocities measured
against the ground the round flies over, a gravity bias, an airframe g limit, and motors that push
along the round rather than along whatever it inherited from the launching craft.[^ksarmory-readme]

The law and the motors live in `Sim/`, which references no KSA type. The game side only passes in
the target sample, the round's gravity, the frame velocity and the platform position each frame,
so the whole guidance loop runs headless in the test project.[^ksarmory-interceptor]

## How it works

**The law** (`Interceptor.GuidanceAccel`, called once per 5 ms sub-step):[^ksarmory-interceptor]

| Step | Expression | Why |
|---|---|---|
| Line-of-sight rate | `omega = (r x v) / (r . r)` | `r` = target minus round, `v` = target velocity minus round velocity |
| PN command | `a = N * (omega x Vc)`, `Vc = -v` | `N` is `MunitionProfile.NavConstant` (4 on every shipped missile) |
| Gravity bias | `a -= gravity * GravityCompensation` | 1.0 means guidance does not fight the fall; lower values give lobbed shots |
| Lateral only | `a = RejectFrom(a, localVelocity)` | An airframe pulls lateral g; it does not add axial thrust |
| Airframe limit | `ClampLength(a, MaxLateralG * 9.80665)` | 35 g default; AIM-9J 30, AIM-120C 40, AGM-88 20 |

Below `r . r < 1e-6` the command is zero. The command is stored as `SteeringCommandEcl` so the
renderer can show it on the fins.[^ksarmory-munition][^ksarmory-arsenal]

**What goes into `r` and `v`.** Both are ecliptic differences, so the ~29.8 km/s the planet adds
to every position and velocity cancels inside the subtraction. The one absolute velocity the law
needs, the round's own flight direction, is `localVelocity = VelocityEcl - frameVelocityEcl`, where
the frame velocity is the ground under the round (body velocity plus spin at that radius, see
[point gravity and atmosphere](./point-gravity-and-atmosphere-model.md)): `KsaWorld.GroundVelocityAt`
returns `Celestial.GetVelocityEcl() + Cross(GetAngularVelocityCce(), r)`. Feeding `VelocityEcl` there
would make every round's "flight path" the planet's orbit.[^ksarmory-frames][^ksarmory-ksaworld][^ksa-celestial][^ksa-iparentbody]

**Back-dating the target.** The target sample is end-of-step (see Lifecycle), while the round's
pre-step position is start-of-step. Each sub-step therefore places the target at
`t.PositionEcl + t.VelocityEcl * (elapsedInFrame - frameSeconds)` before differencing. Without it
the line of sight carries a constant one-frame offset and PN flies a clean intercept on a ghost
450 to 680 m away, which the fuse's own miss distance cannot reveal.[^ksarmory-interceptor]

**One sub-step of `Interceptor.Step`, in order:**

1. `Age += h`; expire past `MaxFlightSeconds`.
2. `accel = Medium.Buoyancy(gravity, ...)` (gravity, scaled only for rounds with a neutral density).
3. Motor: while `Age <= TotalBoostSeconds`, add `BoostAccelAt(Age)` along the thrust axis (below).
4. `accel -= Medium.Drag(localVelocity, ...)`, quadratic in airspeed and scaled by local density.
5. Back-date the target; for `AntiRadiation`, substitute the remembered emission if the set is silent
   (see [seekers](./seekers-uplink-and-laser-homing.md)).
6. Seeker gate: `SeekerInView` is true for `CommandLink`, for an operator-held ground aimpoint, or
   when the angle between `r` and `localVelocity` is within `SeekerFovDeg`. Recomputed every sub-step,
   so a seeker that loses its target off-axis can regain it.
7. Steering only once `Age >= SeparationSeconds` (0.4 s on rail rounds, so they clear the craft first).
8. Proximity fuse, independent of the seeker (see [analytic fuses](./analytic-subframe-fuses.md)).
9. Symplectic Euler: `VelocityEcl += accel * h`, then `PositionEcl += VelocityEcl * h`, then the
   ground crossing test.[^ksarmory-interceptor][^ksarmory-medium]

**Motors.** A round burns `BoostSeconds` at `BoostAccel`, then each `BoostStage(Seconds, Accel)` in
`Stages[]` in order; `BoostAccelAt(age)` returns the acceleration for an age and zero after
burnout. There is no mass model, so each stage is a constant acceleration.[^ksarmory-munition]

| Round | Guidance | Boost | Later stages | Source |
|---|---|---|---|---|
| 57E6 | `CommandLink` | 2.4 s at 520 m/s^2 (profile defaults) | none | Weapons.xml L25-L27, MunitionProfile defaults |
| AIM-9J | `Seeker` (infrared) | 2.2 s at 370 | none | Arsenal.cs L29-L80 |
| AIM-120C-7 | `Seeker` (radar) | 5.5 s at 235 | none (boost-only, long coast) | Arsenal.cs L99-L143 |
| AGM-88 | `AntiRadiation` | 5.0 s at 210 | sustainer 18 s at 26 | Arsenal.cs L162-L210 |

[^ksarmory-weapons-xml][^ksarmory-arsenal]

**Thrust axis = velocity gained since release.** A rail round inherits the whole craft velocity
and adds only 25 m/s of its own, so thrusting along its total velocity would drive it along the
craft's track whatever the rail pointed at. The launcher stores
`LaunchFrameVelocityLocal = platformVelocity - frameVelocity` at release, and the motor pushes
along `Unit(localVelocity - LaunchFrameVelocityLocal)`, falling back to `ReleaseHeadingEcl` while
nothing has been gained. PN's lateral term lands in that gained velocity too, so the round still
curves onto the target. For a launcher standing still the gain is the whole velocity and nothing
changes.[^ksarmory-interceptor][^ksarmory-weaponsystem]

**Frame-invariant signatures.** `Interceptor.Update(dt, target, gravity, frameVelocityEcl,
platformEcl, ...)` takes the absolute platform position and computes `OffsetFromPlatform` itself,
and `GuidanceAccel` takes both ends of every difference. The repo rule is that a `Sim/` entry point
never accepts a difference computed in `Ksa/`, and the matching test adds Earth's 29.8 km/s to
the round, the target and the declared frame and asserts the miss distance moves by less than a
metre (`EngagementIsUnchanged_WhenCarriedByAFastMovingFrame`).[^ksarmory-frames][^ksarmory-interceptortests]

**Launch gate.** A seeker or anti-radiation round fired from a fixed rail more than `SeekerFovDeg`
off its aimpoint can never steer back (no steering means no turn), so
`FireGate.CanGuideOntoAimpoint` refuses that shot and returns the tube.[^ksarmory-firegate]

**Presentation only.** The flight model has no fins and no attitude. `FinMixer.DeflectionRad` turns
`SteeringCommandEcl` (rotated into the body frame) into a per-blade deflection, normalised by the
round's own `MaxLateralAccel`; `FinTest` produces a pitch-then-yaw sweep command for a store on the
rack and feeds it through the same mixer; `BodyAttitude.Turn` carries the drawn attitude onto the
airflow at a rate set by dynamic pressure (`density * speed^2`, full authority above 40 m/s at sea
level). All three are read only by `WeaponSystem.SyncRoundBodies`, never by guidance.[^ksarmory-finmixer][^ksarmory-fintest][^ksarmory-bodyattitude]

**Not round guidance.** `BoosterPerformance` (thrust, mass flow, Tsiolkovsky, burn-time inversion)
and `CoastQuiet` (a latch that stops commanding a coasting bus's attitude so KSA can keep it on
rails) are used only by the ICBM computer; see
[ICBM ballistic computer](./icbm-ballistic-computer.md).[^ksarmory-boosterperformance][^ksarmory-coastquiet]

## Lifecycle and timing

| Where | What happens | Decomp |
|---|---|---|
| `Program.PrepareFrame` | `Universe.ApplyVehicleSolvers()` (Program.cs L2167) sets `_lastSimStep = _nextSimStep` and calls `CurrentSystem.UpdatePerFrameData()` (Universe.cs L1948, L1950), which writes every `Vehicle._positionEcl/_velocityEcl` (Vehicle.cs L2645-L2651) | [^ksa-program][^ksa-universe][^ksa-vehicle] |
| `Program.PrepareFrame` | `Universe.ExecuteNextVehicleSolvers` (Program.cs L2220) queues the next vehicle step on worker threads (Universe.cs L2126-L2137) | [^ksa-universe] |
| `Program.OnFrame` L2265-L2268 | `if (DrawUI) { OnDrawUiFrame; OnDrawUiViewports }`: `[StarMapAfterGui]` runs `StepOnce`, the normal home of guidance | [^ksa-program] |
| `Program.OnFrame` L2320 | `OnFrameCelestials`: the `PreRenderHook` prefix steps instead when the UI pass did not run (`Program.DrawUI` false, L526) | [^ksa-program] |
| after `FrameNumber++` L2332 | `[StarMapAfterOnFrame]`: last-resort step | [^ksa-program] |

- Inside `StepOnce`, `StepSimulation` consumes `Universe.GetLastSimStep()` (Universe.cs L2381) once,
  clamps it to the faithful step, then runs `WeaponSystem.Update` for every system
  (KSArmoryMod.cs L776). `Update` steps rounds before fire control (WeaponSystem.cs L835-L837), so a
  round launched this frame is first guided next frame.[^ksarmory-mod][^ksarmory-weaponsystem]
- **Epoch.** Every target sample (`Vehicle.GetPositionEcl` L954 returns the cached `_positionEcl`)
  is stamped at `Universe.GetElapsedTime() => _lastSimStep.NextTime` (Universe.cs L2399-L2401), the
  end of the step the round is about to cross. That is the reason for the `- frameSeconds`
  back-dating in every sub-step.[^ksa-universe][^ksa-vehicle]
- **Concurrency.** The next vehicle step is already running on workers for the whole hook.
  Guidance only reads the main-thread cached samples and writes nothing to KSA, so it needs no
  lock. Drawing (`SyncRoundBodies`, fins, attitude) runs in the same pass outside the clock gate
  (KSArmoryMod.cs L816-L819), so bodies follow the platform on frames that advanced no time.[^ksarmory-mod]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Sim/Interceptor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Interceptor.cs#L407-L604) | `Step`: motor, drag, back-dating, seeker gate, steering, fuse, integration |
| [src/KSArmory/Sim/Interceptor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Interceptor.cs#L606-L632) | `GuidanceAccel`: the PN law |
| [src/KSArmory/Sim/MunitionProfile.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/MunitionProfile.cs#L178-L230) | Launch speed, boost, `Stages`, `BoostAccelAt` |
| [src/KSArmory/Sim/MunitionProfile.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/MunitionProfile.cs#L333-L406) | `NavConstant`, `MaxLateralG`, `Guidance`, seeker fields, `SeparationSeconds`, `GravityCompensation` |
| [src/KSArmory/Sim/Arsenal.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Arsenal.cs#L29-L210) | Shipped AIM-9J, AIM-120C and AGM-88 profiles |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L2477-L2532) | `Commit`: seeker launch gate, `LaunchFrameVelocityLocal`, round construction |
| [src/KSArmory/Sim/FireGate.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/FireGate.cs#L73-L107) | `CanGuideOntoAimpoint` |
| [src/KSArmory/Sim/FinMixer.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/FinMixer.cs) | Steering command to per-blade deflection (drawn only) |
| [src/KSArmory/Sim/BodyAttitude.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BodyAttitude.cs) | Drawn attitude weathervaned by dynamic pressure |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L1992-L2046) | `SyncRoundBodies`: attitude and fins applied to the round's body |
| [tests/KSArmory.Tests/InterceptorTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/InterceptorTests.cs#L142-L180) | Frame-invariance test |

## Snippets

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

```csharp
// src/KSArmory/Sim/MunitionProfile.cs L218-L230
    public float BoostAccelAt(double age)
    {
        if (age <= BoostSeconds) return age < 0.0 ? 0f : BoostAccel;

        double from = BoostSeconds;
        for (int i = 0; i < Stages.Length; i++)
        {
            from += Stages[i].Seconds;
            if (age <= from) return Stages[i].Accel;
        }

        return 0f;
    }
```

## Game APIs touched

Guidance itself calls no KSA API; these are what the `Ksa/` side reads to feed it.

| API | Decompiled file | Used for |
|---|---|---|
| `Vehicle.GetPositionEcl()` / `GetVelocityEcl()` (L954, L960), via `KsaWorld.PositionEcl/VelocityEcl` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs) | Target sample and platform anchor, end-of-step values |
| `Vehicle.UpdatePerFrameData()` (L2645) | same | Where those cached values are written each frame |
| `Celestial.GetVelocityEcl()` (L403), `IParentBody.GetAngularVelocityCce()` (L81) | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs), [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs) | Frame velocity (`KsaWorld.GroundVelocityAt`) that defines `localVelocity` |
| `IParentBody.Mu` (L15) | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs) | Gravity vector that the bias subtracts |
| `Universe.GetLastSimStep()` (L2381), `GetElapsedTime()` (L2399) | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs) | The `dt` guidance integrates and the epoch of every sample |
| `Vehicle.Asmb2Ego` (L502) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs) | Rotating the steering command into the body frame for the fins |

## Pitfalls and version notes

- **`MissDistance` is not a miss distance.** It is a fuse threshold crossing with a one-sub-step
  horizon, so it is bounded by the fuse radius whatever the round does; the docs measured 10 to
  15 m reported against 450 to 680 m real misses before the back-dating fix.[^ksarmory-frames]
- **Absolute velocity as a heading.** Anywhere `VelocityEcl` is used as a direction (thrust axis,
  seeker cone, drag, rejection plane) the round points along the planet's orbit. The test comment
  records the symptom: drag at Mach 87, the seeker lock broken on step one, 84 km of straight coast.[^ksarmory-interceptortests]
- **Gravity bias with a fixed vector.** `gravity` arrives once per frame (aimed at the body's
  mid-frame position) and is held across sub-steps for an `Interceptor`; only density is re-read
  per sub-step. That is fine for a short, steering-dominated flight and is not how a `Slug` does it
  (see [point gravity](./point-gravity-and-atmosphere-model.md)).[^ksarmory-weaponsystem]
- **No mass, no lift.** Stage accelerations are averages (the AIM-9J comment says the real motor's
  acceleration climbs as propellant burns), and turn rate is limited only by `MaxLateralG`,
  independent of speed or air density.[^ksarmory-arsenal]
- **Separation is a hard gate.** Below `SeparationSeconds` nothing steers even with the target in
  view, so very short-range shots need it small.
- **Doc drift.** `docs/FRAMES-AND-EPOCHS.md` cites `Universe.cs:1681-1683`, `Program.cs:2012/2096`
  and `Universe.cs:2060-2062` from an older corpus (`current/src`); in build 5554 decomp those are
  Universe.cs L1947-L1950, Program.cs L2167/L2268 and Universe.cs L2399-L2401. The Interceptor
  comment says KSA writes vehicle state "at the top of OnFrame"; precisely, it is in
  `PrepareFrame` via `ApplyVehicleSolvers`, which `OnFrame` calls first (Program.cs L2243).[^ksa-program]
- **Gravity is cancelled only laterally.** The bias is subtracted before the projection, so only
  the part of gravity square to the flight path is cancelled; the along-path part still slows a
  climbing round and speeds a diving one.[^ksarmory-interceptor]

## See also

- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Seekers, command uplink and laser designation](./seekers-uplink-and-laser-homing.md)
- [Analytic sub-frame fuses](./analytic-subframe-fuses.md)
- [Point gravity and atmosphere model](./point-gravity-and-atmosphere-model.md)
- [Ecliptic frames and epochs](./ecliptic-frames-and-epochs.md)
- [Round body draw hooks](./round-body-draw-hooks.md)
- [Fire control and target allocation](./fire-control-and-target-allocation.md)
- [Sim boundary and headless tests](./sim-boundary-headless-tests.md)
- [Steer a mod-simulated round by true proportional navigation on a back-dated target sample](../../../howto/flight-and-orbits/guidance/steer-by-zero-effort-miss/steer-by-zero-effort-miss-1.md) - how-to article demonstrating this feature

[^ksarmory-readme]: README.md, How it works and Tuning that actually matters
[^ksarmory-interceptor]: src/KSArmory/Sim/Interceptor.cs
[^ksarmory-munition]: src/KSArmory/Sim/MunitionProfile.cs
[^ksarmory-arsenal]: src/KSArmory/Sim/Arsenal.cs
[^ksarmory-weapons-xml]: src/KSArmory/KSArmory/Weapons.xml
[^ksarmory-medium]: src/KSArmory/Sim/Medium.cs
[^ksarmory-firegate]: src/KSArmory/Sim/FireGate.cs
[^ksarmory-finmixer]: src/KSArmory/Sim/FinMixer.cs
[^ksarmory-fintest]: src/KSArmory/Sim/FinTest.cs
[^ksarmory-bodyattitude]: src/KSArmory/Sim/BodyAttitude.cs
[^ksarmory-boosterperformance]: src/KSArmory/Sim/BoosterPerformance.cs
[^ksarmory-coastquiet]: src/KSArmory/Sim/CoastQuiet.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-interceptortests]: tests/KSArmory.Tests/InterceptorTests.cs
[^ksarmory-frames]: docs/FRAMES-AND-EPOCHS.md (author notes)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
