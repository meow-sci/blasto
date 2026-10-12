---
type: How-To
title: Predict where a released store lands by flying a throwaway round in a ground-carried frame
description: Fly a throwaway copy of the real round through the real gravity, air and terrain in a frame that rides with the ground, rate-limit the solve on wall clock, and store the answer relative to the platform
category: flight-and-orbits
group: guidance
topic: predict-impact-point-and-build-a-ballistic-computer
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/guided-bomb-tail-kit-and-sight]
techniques: [ground-carried-frame, rate-limited-solve, flown-prediction]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials]
lifecycle: [frame:draw-ui, frame:celestials]
game_types: [KSA.Vehicle, KSA.Celestial, KSA.IParentBody, KSA.Program, KSA.Universe]
tags: [impact-point, bomb-sight, pipper, rotating-body, ground-frame, rate-limit, terrain]
---
Outcome: the point on the ground where a store released this instant would land, and the arc down to it, recomputed a few times a second and held steady in between. The prediction is made by stepping the same simulated round the real store will be, so the sight cannot disagree with the flight by construction. KSArmory ships it as the bomb sight; the tail-kit steering in [Steer by predicted miss](../steer-by-zero-effort-miss/overview.md) uses the same machinery.[Guided bomb tail kit and bomb sight](../../../../mods/ksarmory/features/guided-bomb-tail-kit-and-sight.md)

Prerequisites: StarMap to load the mod. The round itself (`Slug`, `MunitionProfile`) is KSArmory code; to reuse the technique, supply your own stepper with a gravity function, a density function and a ground test.

## Background: how the game does it

The game offers the parts but no predictor: the body's centre and velocity in the ecliptic frame, its spin, the terrain height and the gravity parameter.

| Need | Game member |
|---|---|
| Body centre and velocity in Ecl | `Astronomical.GetPositionEcl()`, `Celestial.GetVelocityEcl()`[^ksa-celestial] |
| Spin vector, matching the ecliptic offsets | `IParentBody.GetAngularVelocityCce()` (the CCI spin `(0, 0, w)` tilted into CCE)[^ksa-iparentbody] |
| Terrain under a point | `Celestial.GetTerrainHeightFromDirCce`[^ksa-celestial] |
| Which body the craft is over | `Vehicle.Parent`, `Universe.CurrentSystem`[^ksa-vehicle] [^ksa-universe] |

The trap is the ecliptic frame. A round integrated in Ecl velocities moves about 1.5 km per step (the planet's 29.8 km/s), while the ground lookup resolves each predicted position against the planet where it is now. One step in, the round reads as underground. The fix is to work in a frame that rides with the ground under the release.

## Lifecycle and timing

| Value | Where | What runs |
|---|---|---|
| `frame:draw-ui` | `[StarMapAfterGui]` in [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L509-L510) calls `StepOnce`, then the sight update, then the draw | Solve and draw in the same hook, so the offsets and the camera anchor share an epoch |
| `frame:celestials` | A prefix on `Program.OnFrameCelestials` ([src/KSArmory/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/PreRenderHook.cs)) runs the same step on frames that draw no UI | Solving continues, nothing is drawn; if the patch cannot apply, `[StarMapAfterOnFrame]` runs the step instead |

- **Wall clock, not sim time.** One solve flies up to 2048 steps with a ground lookup each, per weapon system. A limiter built on simulated time stops limiting under time warp, because a simulated interval passes every frame.
- **Why this is safe.** Everything read (platform pose, body state) is main-thread state committed by the last `ApplyVehicleSolvers`; nothing is written. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Rate-limit the solve on a stopwatch.** Hold the last good answer; back off to 2 s when the store can never reach the ground inside the horizon (a warhead released minutes up):

```csharp
// src/KSArmory/Ksa/BombSightOverlay.cs L78-L96
    private const double SolveIntervalSeconds = 0.2;

    // How long to wait before asking again once the store has been found unsightable. Long, but
    // not never: an aircraft that climbs out of range comes back down.
    private const double UnreachableIntervalSeconds = 2.0;

    private readonly System.Diagnostics.Stopwatch _sinceSolve = System.Diagnostics.Stopwatch.StartNew();
    private bool _unreachable;

    public void Update(WeaponSystem system, double dtSim)
    {
        if (system.Platform is not { } platform || system.Launcher is null) return;

        // Held between solves, so what was drawn last time keeps being drawn.
        double wanted = _unreachable ? UnreachableIntervalSeconds : SolveIntervalSeconds;

        if (_sinceSolve.Elapsed.TotalSeconds < wanted) return;

        _sinceSolve.Restart();
```

2. **Take the release velocity over the ground.** Subtract the ground's velocity at the platform so the carrier's 29.8 km/s disappears:

```csharp
// src/KSArmory/Ksa/BombSightOverlay.cs L200-L215
        velocityOverGround = velocityEcl - KsaWorld.GroundVelocityAt(platform, system.PlatformEcl);
        return true;
    }

    private bool Fly(WeaponSystem system, Vehicle platform, double3 releaseEcl,
                     double3 velocityOverGround, List<double3> path, out double3 impactEcl)
        => BombSight.TryPredict(releaseEcl, velocityOverGround,
                                KsaWorld.GroundVelocityAt(platform, system.PlatformEcl),
                                KsaWorld.GroundAccelerationAt(platform, system.PlatformEcl),
                                KsaWorld.BodyVelocityAt(platform),
                                at => KsaWorld.GroundVelocityAt(platform, at),
                                system.Munition,
                                at => KsaWorld.GravityAt(platform, at),
                                at => KsaWorld.MediumDensityRatioAt(platform, at),
                                Ground(), BombSight.StepFor(system.Munition, IntegrationStep), path,
                                out impactEcl);
```

3. **Compute the ground's velocity and acceleration from the spin.** Velocity is the body's velocity plus `spin x r`; acceleration is `spin x (spin x r)`. Use CCE spin for CCE offsets: mixing in CCI spin invents up to 190 m/s on Earth from the axial tilt.

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L2045-L2061
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
```

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L2020-L2036
    public static double3 GroundAccelerationAt(Vehicle platform, double3 positionEcl)
    {
        try
        {
            if (platform.Parent is not Celestial body) return Vec.Zero;

            double3 spin = ((IParentBody)body).GetAngularVelocityCce();
            double3 fromCentre = positionEcl - body.GetPositionEcl();
            if (!Vec.IsFinite(spin) || !Vec.IsFinite(fromCentre)) return Vec.Zero;

            return Vec.Cross(spin, Vec.Cross(spin, fromCentre));
        }
        catch
        {
            return Vec.Zero;
        }
    }
```

4. **Fly the throwaway round in the carried frame.** The frame drifts as `spin t + A t^2 / 2`; gravity and density are read where the round is relative to the drifted centre, and the frame's own acceleration is taken off gravity:

```csharp
// src/KSArmory/Sim/BombSight.cs L84-L116
        var frame = new CarriedFrame(groundVelocityEcl - bodyVelocityEcl, groundAccelerationEcl);
        var carried = new CarriedGround(ground, frame);

        // A throwaway round, flown exactly as the real one will be. The tube number is arbitrary:
        // nothing here reaches a magazine. Its own lookups are back-dated into the step, so they are
        // carried to the instant they belong to rather than to the step's end.
        // At the age of the store it stands in for: a canopy already open and a fuse already armed
        // stay so, where a probe starting at zero would fly the first seconds of a fresh release.
        Slug shot = new(releaseEcl, velocityOverGround, null, 0, releaseEcl, Vec.Zero)
        {
            Age = Math.Max(startAge, 0.0),
            Munition = munition,
            Ground = carried,
            GravityAt = (p, intoStep) => gravityAt(p + frame.Drift(carried.Seconds + intoStep)) - groundAccelerationEcl,
            AirDensityAt = (p, intoStep) => densityAt(p + frame.Drift(carried.Seconds + intoStep)),
        };

        pathEcl.Add(releaseEcl);

        // Fixed for the whole flight: a place on the ground does not move in a frame carried with
        // the ground.
        TargetState? aim = steerAtEcl is { } steer ? new TargetState(steer, Vec.Zero, 0.0) : null;

        for (int i = 0; i < MaxSteps && shot.State == RoundState.Flying; i++)
        {
            carried.Seconds += stepSeconds;

            double3 at = shot.PositionEcl + frame.Drift(carried.Seconds);
            shot.Update(stepSeconds, aim, gravityAt(at) - groundAccelerationEcl, Vec.Zero,
                        releaseEcl, munition, densityAt(at));

            pathEcl.Add(shot.PositionEcl);
        }
```

5. **Read terrain at the release ground, not the carried point.** `CarriedGround` shifts only the centre by the drift, so the height comes from the ground under the release; asked at the carried point instead, the height is read kilometres upwind (15 km over a half-minute fall at the equator, per the mod's comment). Skip lookups until the round is near the cached surface (`CoarseGroundTest`: 600 m of the cached surface or 250 m from the last sample).

6. **Carry the landing back onto the ground that will be there.** The ground at the landing point moves against the release's ground by the spin across the distance between them:

```csharp
// src/KSArmory/Sim/BombSight.cs L133-L138
        // Where it lands is a place in the carried frame at the moment it lands. The ground there moves
        // against the release's ground by the spin across the distance between them, and a ring drawn now
        // marks that ground where it is now.
        double seconds = carried.Seconds + shot.DetonationElapsedInFrame;
        impactEcl = arrived - ((groundVelocityAt(arrived) - groundVelocityEcl) * seconds);
        return Vec.IsFinite(impactEcl);
```

7. **Store the answer relative to the platform.** The arc and impact are kept as offsets from the platform sample they were solved against, not as ecliptic points, so a quarter-second-old answer translates with the aircraft instead of drifting by the planet's motion. Draw it with the projection described in [Draw a predicted trajectory and markers](../../map-view/draw-a-predicted-trajectory-and-map-markers/overview.md).

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/BombSight.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BombSight.cs) | `TryPredict`, `CarriedFrame`, `CarriedGround` |
| [src/KSArmory/Ksa/BombSightOverlay.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BombSightOverlay.cs) | rate limit, release state, platform-relative cache, arc drawing |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs) | ground velocity and acceleration, gravity, density |
| [src/KSArmory/Ksa/GroundTest.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/GroundTest.cs) | terrain from `GetTerrainHeightFromDirCce`, cached body |
| [src/KSArmory/Sim/CoarseGroundTest.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/CoarseGroundTest.cs) | terrain lookups only near the ground |

## Variations and gotchas

- **Powered rounds get no pipper:** a ballistic sight answers the wrong question for a round that flies under its own power.
- **A failed solve keeps the last good one** so the sight does not blank for a frame.
- **Horizon.** The sight's horizon is `MaxSteps` times the integration step (about 102 s); a store released minutes above its target never has a pipper. A parachute step is coarsened to 0.30 s.
- **Differencing.** Take both the aim and the round relative to the ground; the planet's ecliptic motion then never reaches the steering law.
- **GroundTest is queried at the frame end.** Its height lookup resolves the direction through the end-of-frame rotation, so a sub-step part-way through a frame has to hand in a point walked forward by the ground's full velocity.

## See also

- [Overview](overview.md); [Gravity plus drag with a measured table](predict-impact-point-and-build-a-ballistic-computer-1.md); [Flown impact check against a closed-form solution](predict-impact-point-and-build-a-ballistic-computer-3.md)
- [Guided bomb tail kit and bomb sight (mod feature)](../../../../mods/ksarmory/features/guided-bomb-tail-kit-and-sight.md)
- [Anchor points to a rotating body](../../map-view/anchor-and-pick-points-on-bodies/overview.md)
- [Run an expensive solve without stalling the frame](../run-an-expensive-solve-without-stalling-the-frame/overview.md)

[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
