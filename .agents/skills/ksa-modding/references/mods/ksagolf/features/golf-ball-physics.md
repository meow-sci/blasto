---
type: Mod Feature
title: Golf ball physics in the body-fixed frame
description: A mod-simulated golf ball integrated in the rotating body-fixed frame of the world under it, with flight, bounce, rolling resistance, slope rest and quadratic drag scaled by the engine's own air and sea density
mod: ksagolf
category: physics
techniques: [custom-physics, starmap-lifecycle, harmony-prefix, sub-stepping, headless-tests]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types:
  - KSA.Celestial
  - KSA.Astronomical
  - KSA.IParentBody
  - KSA.AtmosphereReference
  - KSA.PhysicalAtmosphereReference
  - KSA.Rendering.Water.Data.OceanReference
  - KSA.DistanceReference
  - KSA.Universe
  - KSA.SimStep
  - KSA.PhysicsEnvironment
tags: [ball, custom-physics, body-fixed, ccf, drag, atmosphere, ocean, rolling, bounce, sim-time]
---

The golf ball is not a KSA vehicle and has no Bepu body. It is a few doubles the mod integrates
itself: position and velocity in the **body-fixed (`Ccf`) frame** of the celestial it lies on. The
player sees a ball that drops onto the ground, bounces lower each time, rolls out and slows like a
slowish green, runs down a slope it cannot hold, settles on one it can, sounds when it lands hard,
and drops into a minigolf cup.

Living in `Ccf` is the point. The ball carries none of the planet's orbital or spin motion, so its
state has no epoch: only drawing it needs the body's pose, and that pose is read at the instant it
is drawn. Every frame-and-epoch problem that the mod's other bodies fight (29.8 km/s of ecliptic
carrier near Earth) disappears for the ball.[^golf-claude][^golf-frames]

## How it works

`GolfBall.Step(dt, ground, mu, densityRatio, walls)` splits the frame's simulated time into
sub-steps of at most 1/240 s (clamped to 1..2000 of them) and runs `SubStep` on each until the ball
rests.[^golf-golfball]

| Term | What the code does |
|---|---|
| Gravity | Point mass, radial in `Ccf`: `-mu / r^2` along `PositionCcf / r`. `mu` is `IParentBody.Mu` (`Mass * 6.6743E-11`), a default interface member, hence the cast `((IParentBody)body).Mu`.[^ksa-iparentbody] Centrifugal and Coriolis terms of the rotating frame are left out on purpose (0.3% of g on Earth at golf speeds, by the mod's own estimate). |
| Air or water drag | `Medium.Drag(v, k, ratio)` is `v` times `k`, the speed and the density ratio, subtracted from the acceleration. `k = 0.5 * 1.225 * Cd * pi * r^2 / m` (`Medium.DragK`), with Cd 0.25, 42.67 mm and 45.93 g, about 0.0048 per metre. The velocity used is `VelocityCcf`, which is already the velocity against co-rotating air.[^golf-medium] |
| Airborne | When the ground is unknown or more than 2 mm below the ball's bottom: integrate gravity minus drag, then `Walled` and `Advance`. |
| Contact | Snap onto the surface, take the normal (exact from `IBodyGround.TrySurfaceNormal`, else a finite difference across a 5 cm baseline that ignores a side rising like a wall), count an audible impact above 0.3 m/s. |
| Bounce | Speed into the ground above 0.25 m/s reflects with restitution 0.35; below it the into-ground component is removed and the ball rolls. |
| Rolling and slopes | The tangential part of gravity drives it downhill; rolling resistance of 0.08 g removes speed but never reverses it within a sub-step. |
| Rest | Below 0.01 m/s on a slope whose tangent is at most 0.08 the ball stops and `Step` becomes a no-op until struck. |
| Kerbs | `Walled` samples the ground at the next position; a rise more than 1 cm above the clearance and steeper than 1:1 over the step is treated as a wall and reflected, so a pad edge is not climbed in one step. |
| Rails | `Advance` sweeps the step against an optional `IWalls` (the minigolf course) up to four bounces, with wall restitution 0.7 and 95% grip along the wall. |
| Spin | Rolling without slipping turns `Spin` (a `doubleQuat`) about `normal x velocity`, so the drawn ball rolls. |

The ground is an `IBodyGround` (`TrySurfaceRadius(dirCcf)`, the distance from the body centre to
the surface along a direction). Outside a course it is `Golf.BodyGround`: the engine height field
via `Celestial.GetTerrainHeightFromDirCcf(dir, accurate: true)`, raised to the waterline where the
body has a sea (`GroundSurface.Height`), and raised again to a launch site's top where
`PadSurface` finds one. When a course is laid on the same body the ball's ground is the
`CourseSurface` instead.[^golf-golf][^golf-igroundtest][^ksa-celestial]

The density ratio comes from `KsaWorld.MediumDensityRatioAt(body, ballEcl)`, as a multiple of
Earth's sea-level air (1.225 kg/m^3) for every body, so the ball is dragged by the air that is
really there:[^golf-ksaworld]

- below the ocean's `Level`: `OceanReference.Density / 1.225` (about 840 on Earth);
- in an atmosphere: `PhysicalAtmosphereReference.GetAtmosphericDensityAtAltitude(alt) / 1.225`,
  with altitude above `MeanRadius`, the same measure `PhysicsEnvironment.RecomputePositionalValues`
  uses;[^ksa-physenv]
- no atmosphere reference, or above `Height`: 0 (vacuum is an answer, not a failure);
- an exception: 1.0, so a fault keeps tuned drag rather than silently dropping it.

`Medium.Buoyancy` and `Medium.Coasting` exist for other bodies (carts, later shots), but nothing in
the game code calls them; the ball calls only `Medium.Drag`, so it has drag and no buoyancy. The
water branch of the density is also not reached in normal play: `BodyGround` raises the ground to the
waterline, so the ball rests on the sea surface and its centre stays above `OceanReference.Level`,
where the density is the air's.[^golf-medium][^golf-golf]

## Lifecycle and timing

| Where | Thread | What happens |
|---|---|---|
| `[StarMapAfterGui]` (postfix on `Program.OnDrawUiViewports`), `frame:draw-ui` | main | Preferred: `KSAGolfMod.OnAfterGui` calls `StepOnce` first, then draws the panel[^golf-mod] |
| Harmony prefix on private `Program.OnFrameCelestials`, `frame:celestials` | main | The same `StepOnce` on frames where F2 hid the UI, so the step still lands before the render[^ksa-program] |
| `[StarMapAfterOnFrame]` (postfix on `Program.OnFrame`), `frame:after-on-frame` | main | Fallback if neither ran; `StepOnce` is latched so only one hook steps a frame |

`StepOnce` calls `Golf.Step(dtSim, dtPlayer)`, which calls `StepBall(dtSim)`. `dtSim` is
`KsaWorld.ConsumeSimStep()`: the span between consecutive `Universe.GetLastSimStep().NextTime`
boundaries, so a pause gives 0, warp gives the real span, and a frame on which no hook ran is
integrated on the next instead of lost. `StepBall` caps it at 2 s, so heavy warp makes the ball lag
rather than integrate thousands of sub-steps.[^golf-golf][^golf-ksaworld][^ksa-universe]

Why this point is safe: every hook above runs after `PrepareFrame` has waited on the vehicle
solver, run `Universe.ApplyVehicleSolvers` (which ends with `CurrentSystem.UpdatePerFrameData()`,
refreshing each celestial's position and `Ccf`-to-`Cce` rotation) and queued the next step. The
ball reads only celestial data (template height field, atmosphere and ocean references, `Mu`,
`GetPositionEcl`, `GetCcf2Cce`) which no worker writes, and touches no vehicle state, so it does
not race the vehicle step that is running on the workers at the same time.[^ksa-program][^ksa-universe]
Using simulated rather than player time is a hard rule of the repo: StarMap's frame hook hands
over a player-time delta that keeps running while paused and ignores warp.[^golf-claude]

`Golf.Step` also calls `PadSurface.Want(world)` each frame, which arms the launch-site copy taken
on the next frame's `Universe.ExecuteNextVehicleSolvers` prefix (see
[Launch-site surface raycast](launch-site-surface-raycast.md)).

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Sim/GolfBall.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/GolfBall.cs#L100-L268) | The ball: constants, sub-stepping, contact, bounce, rolling, rest, kerbs, swept rails, spin, normal estimate |
| [src/KSAGolf/Sim/Medium.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/Medium.cs#L35-L71) | Reference air, sphere drag constant, quadratic drag (the uncalled `Buoyancy` and `Coasting` are at L14-L55) |
| [src/KSAGolf/Sim/IGroundTest.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/IGroundTest.cs#L36-L56) | `GroundSurface.Height`: the higher of terrain and waterline |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L716-L766) | `StepBall` (the per-frame call) and `BodyGround` (height field, sea, launch site) |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L898-L983) | `MediumDensityRatioAt`: atmosphere and ocean density as a ratio of 1.225 kg/m^3 |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L80-L97) | `ConsumeSimStep`: the simulated-time clock |
| [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L113-L134) | `StepOnce`, reached from the GUI hook, the pre-render prefix or the frame postfix |
| [tests/KSAGolf.Tests/GolfBallTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/GolfBallTests.cs) | Headless tests: settling, bounce height, putt distance, slope rest, kerb bounce, step-up |
| [tests/KSAGolf.Tests/MediumForcesTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/MediumForcesTests.cs) | Drag is quadratic, along the airspeed, scaled by density, zero where it cannot apply |

## Snippets

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

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L942, L949-L954, L966-L977
            double altitude = Vec.Len(positionEcl - body.GetPositionEcl()) - body.MeanRadius;
            // ...
            OceanReference? ocean = body.GetOceanReference();
            if (withOcean && ocean is { } sea && sea.Density > 0.0 && altitude < sea.Level)
            {
                double water = sea.Density / Medium.ReferenceDensityKgPerM3;
                return double.IsFinite(water) && water > 0.0 ? water : 1.0;
            }
            // ...
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

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `Celestial.GetTerrainHeightFromDirCcf(dir, accurate)` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789-L797) | Terrain height above `MeanRadius` along a body-fixed direction; the ball asks with `accurate: true` |
| `Celestial.GetCcf2Cce()` / `GetCce2Ccf()` / `GetPositionEcl()` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L530-L540) | Per-frame cached rotation and position, used only to convert the ball to `Ecl` for drawing, sound and the density altitude |
| `Celestial.UpdatePerFrameData()` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L591-L603) | Where `_ccf2Cce` and `_positionEcl` are refreshed, from `Universe.ApplyVehicleSolvers` |
| `Astronomical.GetOceanReference()` / `GetAtmosphereReference()` | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L387-L395) | Return the template's references; null means no sea or no air |
| `IParentBody.Mu` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs) | Gravitational parameter, a default interface member (`Mass * 6.6743E-11`) |
| `PhysicalAtmosphereReference.GetAtmosphericDensityAtAltitude` / `SeaLevelDensity` / `ScaleHeight` / `Height` | [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs) | Air density at altitude; `SeaLevelDensity` defaults to 1.225, the mod's reference |
| `OceanReference.Level` / `Density` | [current/decomp/KSA.Rendering.Water.Data/OceanReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Water.Data/OceanReference.cs) | Waterline and water density (default 1028.13 kg/m^3) |
| `DistanceReference.IsValid()` | [current/decomp/KSA/DistanceReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DistanceReference.cs#L162-L165) | Deliberately not used: it requires a distance above 100 km |
| `PhysicsEnvironment.RecomputePositionalValues` | [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L83-L117) | The engine's own density and terrain read for vehicles; the mod matches its altitude convention |
| `Universe.GetLastSimStep()` / `ApplyVehicleSolvers()` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952) | The simulated step the ball integrates over, and where the celestial poses it reads are settled |
| `Program.PrepareFrame` / `OnFrame` / `OnFrameCelestials` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2140-L2330) | Frame order that places the three stepping hooks after the solver apply and before the render |

## Pitfalls and version notes

- **Never gate on `IsValid()`.** `DistanceReference.IsValid()` is `Math.Abs(_value) > 100000.0`, so
  `PhysicalAtmosphereReference.IsValid()` (via an 8 km `ScaleHeight`) and `OceanReference.IsValid()`
  (via a 0 m `Level`) are false on Earth. Gating drag on them gives vacuum at sea level and a ball
  that falls through the sea. Still true in build 2026.10.10.5554.[^ksa-distref][^golf-blocked]
- **Airless is 0, unreadable is 1.** Every airless body returns no atmosphere reference; treating
  that as "use the default" would put Earth's air on the Moon.[^golf-ksaworld]
- **The height field answers with the seabed.** Without the waterline clamp a ball over the ocean
  falls to the bottom unseen. The engine's own vehicles float on `OceanRenderer.GetOceanHeightAtPositionCcf`
  (the displaced wave surface), so the ball and a floating craft disagree by the wave height.[^ksa-physenv]
- **Rotating-frame terms are omitted.** Fine for a putt; a long, high shot in `Ccf` would miss the
  Coriolis drift a real ball has.[^golf-golfball]
- **Not a physics body.** Kittens and craft do not collide with the ball and it does not collide with
  them; the swept hull test in [Swept hull contact](swept-hull-contact.md) is not wired into
  `GolfBall` at this commit.
- **Drag barely matters on a putt.** At 2 m/s it is about 0.02 m/s^2 against 0.78 m/s^2 of rolling
  resistance; it is there for longer shots.
- **Unverified in game.** The repo's own testing note lists what has been seen in game since the
  cut (the ball drawn on a kitten, not the ball's physics); the behaviour above is backed by the
  headless suite only.[^golf-claude][^golf-golfball-tests]
- **Version.** Built against KSA 2026.9.22.5482. Between that build and 2026.10.10.5554 the
  `Celestial` diff touches orbit creation and `UpdateTask` only; the height, rotation, ocean,
  atmosphere and `Mu` members used here are unchanged, and the `Program.PrepareFrame` order of
  wait, apply, input, dispatch is the same.

## See also

- [Putting session with a mouse-swung club](putting-session.md)
- [Launch-site surface by raycast into a copied collider](launch-site-surface-raycast.md)
- [Course surface from exact shapes](course-surface-exact-shapes.md)
- [Terrain height queries off the engine's height field](terrain-height-queries.md)
- [Swept contact against a craft's real hull](swept-hull-contact.md)
- [Dual-hook frame stepping on simulated time](dual-hook-frame-stepping.md)
- [Frame and epoch draw anchor](frame-epoch-draw-anchor.md)
- [Drawing a mod's static mesh into every viewport](mod-mesh-draw-hook.md)
- [World-positioned sounds](world-positioned-sounds.md)
- [Headless simulation tests behind a Sim/Ksa boundary](headless-sim-tests.md)
- [Density ratio to one reference air, ocean density below the waterline](../../../howto/physics/mod-simulation/apply-drag-and-gravity-to-a-mod-simulated-body/apply-drag-and-gravity-to-a-mod-simulated-body-1.md) - how-to article demonstrating this feature
- [Ball integrated at up to 1/240 s in the body-fixed frame](../../../howto/physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-2.md) - how-to article demonstrating this feature

[^golf-golfball]: src/KSAGolf/Sim/GolfBall.cs
[^golf-medium]: src/KSAGolf/Sim/Medium.cs
[^golf-golf]: src/KSAGolf/Ksa/Golf.cs
[^golf-ksaworld]: src/KSAGolf/Ksa/KsaWorld.cs
[^golf-mod]: src/KSAGolf/Ksa/KSAGolfMod.cs
[^golf-igroundtest]: src/KSAGolf/Sim/IGroundTest.cs
[^golf-golfball-tests]: tests/KSAGolf.Tests/GolfBallTests.cs
[^golf-claude]: CLAUDE.md (layout, design decisions, testing status)
[^golf-blocked]: docs/BLOCKED-ON-KSA.md
[^golf-frames]: docs/FRAMES-AND-EPOCHS.md
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-physenv]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
[^ksa-distref]: KSA.DistanceReference (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
