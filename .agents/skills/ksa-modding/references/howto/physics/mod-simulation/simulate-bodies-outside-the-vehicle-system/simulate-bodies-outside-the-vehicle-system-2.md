---
type: How-To
title: Ball integrated at up to 1/240 s in the body-fixed frame
description: Integrate a free body as a few doubles in the planet-fixed Ccf frame over the applied simulation step, with flight, bounce, rolling resistance, rest and slope handling against the height field, and convert to the ecliptic only to draw
category: physics
group: mod-simulation
topic: simulate-bodies-outside-the-vehicle-system
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksagolf/features/golf-ball-physics]
techniques: [sub-stepping, custom-physics]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Universe, KSA.Celestial, KSA.SimStep, KSA.Rendering.Water.Data.OceanReference, KSA.IParentBody]
tags: [physics, custom-physics, sub-stepping, ccf, body-fixed, bounce, rolling, terrain, golf, height-field]
---

Outcome: a ball (or any free body) that drops onto the ground, bounces lower each time, rolls out and slows, runs
down a slope it cannot hold, settles on one it can, and is drawn correctly on a rotating, orbiting planet.
KSAGolf's ball is not a KSA vehicle and has no Bepu body: it is position and velocity in the body-fixed frame of
the celestial it lies on, integrated by the mod. Prerequisites: StarMap and Harmony. This single-object variant
is the simplest complete example of mod-owned physics.

## Background: how the game does it

The engine offers three things this recipe uses: the applied simulation step ([approach 3](simulate-bodies-outside-the-vehicle-system-3.md)),
the height field along a body-fixed direction, and the body's rotation to the ecliptic frame.

- `Celestial.GetTerrainHeightFromDirCcf(dir, accurate)` [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789-L797) returns the terrain height above
  `MeanRadius` along a body-fixed direction. It answers with the seabed under the ocean.
- `Celestial.GetCcf2Cce()` and `GetPositionEcl()` [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L530-L540) are cached per frame by
  `Celestial.UpdatePerFrameData` [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L591-L603), which `Universe.ApplyVehicleSolvers` runs for every body.
- `IParentBody.Mu` [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L15-L16) is a default interface member (`Mass * 6.6743E-11`), so reading it from a
  `Celestial` needs a cast to `IParentBody`.

## Lifecycle and timing

The step runs once per frame from `[StarMapAfterGui]`, the `OnFrameCelestials` prefix on hidden-HUD frames, or
`[StarMapAfterOnFrame]` as a fallback (a latch lets only one claim the frame). It is safe because all three run
after `PrepareFrame` has waited on the vehicle solver and applied it: the ball reads only celestial data (template
height field, atmosphere and ocean references, `Mu`, `GetPositionEcl`, `GetCcf2Cce`) which no worker writes, and
touches no vehicle state, so it does not race the vehicle step running on the workers.

`dtSim` is the simulated span between consecutive `GetLastSimStep().NextTime` boundaries, so a pause gives 0 and warp
gives the real span. `StepBall` caps it at 2 s, so heavy warp makes the ball lag rather than integrate thousands of
sub-steps.

### Integrate between `GetLastSimStep` boundaries

KSAGolf's consume function is the single-object version of [approach 3](simulate-bodies-outside-the-vehicle-system-3.md): it
reads the last step, remembers the `NextTime` it integrated through, and returns the span in nanosecond arithmetic.

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L80-L100
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

    private static double Span(UniverseTime from, UniverseTime to)
        => (double)(to.Nanoseconds - from.Nanoseconds) / 1e9;
```

When paused the span is 0 and the ball does not move. KSAGolf passes 0 for a paused world (`KsaWorld.InFlightScene &&
!KsaWorld.IsPaused ? _lastSimStep : 0.0`); gating on the consumed step alone also integrates the one real step that
is applied on the frame the speed drops to zero.

## Steps

1. Keep the state in Ccf: a position, a velocity, a spin. The ball carries none of the planet's orbital or spin
   motion, so its state has no epoch; only drawing needs the body's pose, read at the instant it is drawn. Convert to
   the ecliptic only to draw, play sounds and read the density altitude:

```csharp
// src/KSAGolf/Ksa/Golf.cs L735-L741
    private double3 BallEcl() => _ballBody!.GetPositionEcl() + double3.Transform(_ball!.PositionCcf, _ballBody.GetCcf2Cce());

    private static double3 ToCcf(Celestial body, double3 ecl) => double3.Transform(ecl - body.GetPositionEcl(), body.GetCce2Ccf());

    private static double3 ToCcfDirection(Celestial body, double3 ecl) => double3.Transform(ecl, body.GetCce2Ccf());

    private static double3 ToEclDirection(Celestial body, double3 ccf) => double3.Transform(ccf, body.GetCcf2Cce());
```

2. Split the frame's time into sub-steps of at most 1/240 s (clamped to 1..2000) and run one sub-step until the
   ball rests. Each sub-step asks the ground for the surface radius along the ball's up direction, integrates
   gravity (radial, `-mu / r^2`) minus drag when airborne, and otherwise takes the contact path. Bounce reflects the
   into-ground speed above 0.25 m/s with restitution 0.35; below that the ball rolls:

```csharp
// src/KSAGolf/Sim/GolfBall.cs L111-L146
    private void SubStep(double h, IBodyGround ground, double mu, double densityRatio)
    {
        double r = Vec.Len(PositionCcf);
        if (!(r > 0.0)) return;

        double3 up = PositionCcf / r;
        double3 gravity = up * (-mu / (r * r));

        bool known = ground.TrySurfaceRadius(up, out double surface);
        if (!known || r - (surface + RadiusM) > ContactSlop)
        {
            OnGround = false;
            VelocityCcf += (gravity - Medium.Drag(VelocityCcf, DragK, densityRatio)) * h;
            if (!known || !Walled(h, ground, up, surface)) Advance(h, up);
            return;
        }

        OnGround = true;
        double3 normal = SurfaceNormal(ground, up, surface);
        if (r < surface + RadiusM) PositionCcf = up * (surface + RadiusM);

        double into = Vec.Dot(VelocityCcf, normal);
        if (into < -AudibleImpact)
        {
            Impacts++;
            LastImpactSpeed = -into;
        }

        if (into < -BounceBelow)
        {
            VelocityCcf -= normal * ((1.0 + Restitution) * into);
            Advance(h, normal);
            return;
        }

        if (into < 0.0) VelocityCcf -= normal * into;
```

3. Roll. The tangential part of gravity drives the ball downhill; rolling resistance (0.08 g) removes speed but
   never reverses it within a sub-step; below 0.01 m/s on a slope whose tangent is at most 0.08 the ball stops and
   `Step` becomes a no-op until struck:

```csharp
// src/KSAGolf/Sim/GolfBall.cs L148-L167
        double pressing = -Vec.Dot(gravity, normal);
        double3 downhill = gravity + (normal * pressing);
        double speed = Vec.Len(VelocityCcf);

        if (speed < RestSpeed && Vec.Len(downhill) <= RestSlope * pressing)
        {
            VelocityCcf = Vec.Zero;
            Resting = true;
            return;
        }

        double3 velocity = VelocityCcf + ((downhill - Medium.Drag(VelocityCcf, DragK, densityRatio)) * h);

        // Rolling resistance can stop the ball within a sub-step but never send it backwards.
        double rolling = speed > 1e-9 ? RollingResistance * pressing * h : 0.0;
        double moving = Vec.Len(velocity);
        velocity = moving > rolling ? velocity * ((moving - rolling) / moving) : Vec.Zero;

        VelocityCcf = velocity;
        if (!Walled(h, ground, up, surface)) Advance(h, normal);
```

4. Make the height field a ground, with the sea treated separately. The height field returns the seabed, so a
   ball over the ocean would fall to the bottom unseen. KSAGolf's `BodyGround` raises the surface to the waterline
   (`GroundSurface.Height`) and to a launch-site top where one stands:

```csharp
// src/KSAGolf/Ksa/Golf.cs L747-L765
        public bool TrySurfaceRadius(double3 dirCcf, out double radius)
        {
            radius = 0.0;
            try
            {
                double height = body.GetTerrainHeightFromDirCcf(dirCcf, accurate: true);
                bool hasSea = body.GetOceanReference() is { } sea && sea.Density > 0.0;
                double seaLevel = hasSea ? body.GetOceanReference()!.Level : 0.0;

                radius = body.MeanRadius + GroundSurface.Height(height, seaLevel, hasSea);
                if (PadSurface.TryRadius(body, dirCcf, radius, out double top)) radius = Math.Max(radius, top);

                return double.IsFinite(radius) && radius > 0.0;
            }
            catch
            {
                return false;
            }
        }
```

5. Estimate the normal as a finite difference across a 5 cm baseline, leaving out a side that rises like a wall, and
   treat a rise more than 1 cm above the clearance and steeper than 1:1 over the step as a wall (so a pad edge is
   not climbed in one step):

```csharp
// src/KSAGolf/Sim/GolfBall.cs L253-L268
    // The slope one way across a point. A side that rises or falls like a wall is left out, so the edge of
    // a kerb is not read as a ramp that throws the ball over it.
    private static bool TrySlope(IBodyGround ground, double3 up, double surface, double3 across, double angle, out double slope)
    {
        slope = 0.0;
        double3 ahead = Vec.Unit((up * Math.Cos(angle)) + (across * Math.Sin(angle)));
        double3 behind = Vec.Unit((up * Math.Cos(angle)) - (across * Math.Sin(angle)));
        if (!ground.TrySurfaceRadius(ahead, out double high) || !ground.TrySurfaceRadius(behind, out double low)) return false;

        double forward = (high - surface) / NormalProbe;
        double backward = (surface - low) / NormalProbe;
        bool wallAhead = Math.Abs(forward) > WallSlope, wallBehind = Math.Abs(backward) > WallSlope;

        slope = wallAhead && wallBehind ? 0.0 : wallAhead ? backward : wallBehind ? forward : (forward + backward) / 2.0;
        return double.IsFinite(slope);
    }
```

6. Call the integrator from the frame step with the gravitational parameter (cast to `IParentBody`) and the
   medium density at the ball (see [drag and gravity](../../mod-simulation/apply-drag-and-gravity-to-a-mod-simulated-body/overview.md)):

```csharp
// src/KSAGolf/Ksa/Golf.cs L716-L725
    private void StepBall(double dtSim)
    {
        if (_ball is not { } ball || _ballBody is not { } body || !(dtSim > 0.0)) return;

        // Beyond this a warped step is not worth integrating faithfully: the ball simply lags.
        double dt = Math.Min(dtSim, 2.0);
        int impacts = ball.Impacts;
        bool resting = ball.Resting;
        CourseSurface? course = CourseOn(body);
        ball.Step(dt, GroundOn(body), ((IParentBody)body).Mu, KsaWorld.MediumDensityRatioAt(body, BallEcl()), course);
```

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Sim/GolfBall.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/GolfBall.cs#L100-L268) | The ball: constants, sub-stepping, contact, bounce, rolling, rest, kerbs, spin, normal estimate |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L716-L766) | `StepBall` and `BodyGround` |
| [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L113-L134) | `StepOnce`, reached from the GUI hook, the pre-render prefix or the frame postfix |
| [tests/KSAGolf.Tests/GolfBallTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/GolfBallTests.cs) | Headless tests: settling, bounce height, putt distance, slope rest, kerb bounce |

## Variations and gotchas

- Why Ccf: the rotating frame removes the need to chase the ground; the ball needs no epoch, and every
  frame-and-epoch problem that the ecliptic-frame mods fight (29.8 km/s near Earth) disappears.
- Rotating-frame terms (Coriolis, centrifugal) are omitted. Fine for a putt; a long, high shot would miss the
  Coriolis drift a real ball has.
- The ball is not a physics body: kittens and craft do not collide with it. The swept hull test in
  [swept hull contact](../../mod-simulation/prevent-tunnelling-of-fast-bodies/prevent-tunnelling-of-fast-bodies-2.md) is not wired into `GolfBall` at the pinned commit.
- Never gate drag on `IsValid()` of the atmosphere or ocean references: `DistanceReference.IsValid()` is
  `|value| > 100000`, so Earth's 8 km scale height and the sea's 0 m level read as invalid.
- The behaviour is backed by the headless suite; the repo's own note lists only the drawn ball as seen in game.

## See also

- [Overview and comparison](overview.md)
- [Approach 3: the applied step](simulate-bodies-outside-the-vehicle-system-3.md), [approach 6: headless tests](simulate-bodies-outside-the-vehicle-system-6.md)
- Mod feature: [KSAGolf golf ball physics](../../../../mods/ksagolf/features/golf-ball-physics.md)
- [Apply drag and gravity to a mod-simulated body](../../mod-simulation/apply-drag-and-gravity-to-a-mod-simulated-body/overview.md)
