---
type: How-To
title: Density ratio to one reference air, ocean density below the waterline
description: Drag a body-fixed ball through the game's air and sea as a ratio to Earth sea-level air, with surface gravity from IParentBody.Mu and no gating on KSA's IsValid
category: physics
group: mod-simulation
topic: apply-drag-and-gravity-to-a-mod-simulated-body
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/golf-ball-physics]
techniques: [custom-physics, quadratic-drag]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types:
  - KSA.Celestial
  - KSA.IParentBody
  - KSA.PhysicalAtmosphereReference
  - KSA.AtmosphereReference
  - KSA.Rendering.Water.Data.OceanReference
tags: [physics, drag, gravity, atmosphere, ocean, density-ratio, body-fixed, golf]
---

Outcome: a ball that is dragged by the air actually present at its altitude (a fiftieth of Earth's over a Mars-like
surface, nothing on an airless moon), by water when it is under the waterline, and pulled by the body it lies on.
Prerequisites: StarMap. KSAGolf's ball is a free body in the planet-fixed frame
([simulate-bodies approach 2](../simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-2.md)),
so gravity is the radial `-mu / r^2` at the ball's position and the integrator needs no extra terms.

## Background: how the game does it

The atmosphere is an exponential: `PhysicalAtmosphereReference.GetAtmosphericDensityAtAltitude(h)` is
`SeaLevelDensity * exp(-h / ScaleHeight)`, with no cut-off.[^ksa-physicalatmospherereference]

```csharp
// current/decomp/KSA/PhysicalAtmosphereReference.cs L85-L88
	public double GetAtmosphericDensityAtAltitude(double altitudeInMeters)
	{
		return (double)SeaLevelDensity * Math.Exp((0.0 - altitudeInMeters) / ScaleHeight.InMeters());
	}
```

The ocean has a `Level` and a `Density`:[^ksa-oceanreference]

```csharp
// current/decomp/KSA.Rendering.Water.Data/OceanReference.cs L8-L12
	[XmlElement("Level")]
	public DistanceReference Level = new DistanceReference(0.0, DistanceUnit.Kilometers);

	[XmlElement("Density")]
	public DensityReference Density = new DensityReference(1028.13);
```

Both reference types use `DistanceReference.IsValid()`, which is an astronomical sanity check:

```csharp
// current/decomp/KSA/DistanceReference.cs L162-L165
	public override bool IsValid()
	{
		return !double.IsNaN(_value) && Math.Abs(_value) > 100000.0;
	}
```

so `air.IsValid()` is false for every realistic atmosphere and the sea's level (0 m) fails too. Never gate on
`IsValid`.[^ksa-distancereference] `IParentBody.Mu` is a default interface member, so reading it from a
`Celestial` needs a cast:

```csharp
// current/decomp/KSA/IParentBody.cs L15
	double Mu => Mass * 6.6743E-11;
```

## Lifecycle and timing

The density read and `Mu` come from celestial data that no worker writes, so they are safe on the main thread in the
mod's once-per-frame step (`StarMapAfterGui`, the `OnFrameCelestials` prefix, or `StarMapAfterOnFrame`). The result
feeds the sub-stepped integrator ([approach 2 of the simulation topic](../simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-2.md)).
See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Measure every body's air against one reference: Earth's sea-level density, 1.225 kg/m3. The drag formula is then a
   constant times a ratio. KSAGolf's `Medium.Drag` is zero in a vacuum, at rest or with no constant declared (guarded
   so you cannot divide out a zero speed):

```csharp
// src/KSAGolf/Sim/Medium.cs L65-L71
    public static double3 Drag(double3 localVelocity, double dragK, double densityRatio)
    {
        double airspeed = Vec.Len(localVelocity);
        if (dragK <= 0.0 || airspeed <= 1e-6 || densityRatio <= 0.0) return Vec.Zero;

        return localVelocity * (dragK * airspeed * densityRatio);
    }
```

   The constant for a sphere is half the reference density, times its drag coefficient and frontal area, over its mass:

```csharp
// src/KSAGolf/Sim/Medium.cs L41-L47
    public static double DragK(double massKg, double diameterMm, double dragCoefficient)
    {
        if (!(massKg > 0.0) || !(diameterMm > 0.0) || !(dragCoefficient > 0.0)) return 0.0;

        double radius = diameterMm / 2000.0;
        return 0.5 * ReferenceDensityKgPerM3 * dragCoefficient * Math.PI * radius * radius / massKg;
    }
```

2. Compute the ratio at the ball. Altitude is the distance from the body's centre less `MeanRadius`, the measure the
   game's own physics uses. Ask the water question first: a body can have an ocean and no atmosphere. Below the
   waterline the medium is the ocean, about 840 times sea-level air, so the ratio is not bounded above by 1. A body
   with no ocean returns `null` from `GetOceanReference()`:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L934-L954
    private static double MediumDensityRatioAt(Celestial body, double3 positionEcl, bool withOcean)
    {
        try
        {
            // Altitude above the mean surface, the same measure KSA's own physics uses. Asked
            // before the air is, because whether a point is under water is its own question: a
            // body can have an ocean and no atmosphere, and resolving the air first returned
            // vacuum for a point at the bottom of one.
            double altitude = Vec.Len(positionEcl - body.GetPositionEcl()) - body.MeanRadius;

            // Below the waterline the medium is the ocean, which is ~840x sea-level air. The
            // ratio is therefore not bounded above by 1.
            // The ocean's IsValid() tests its level (0 m) and transparency depth (100 m) against
            // an astronomical 100 km bar, so it is false wherever there is water. A body with no
            // ocean hands back null, which is the discriminator that means it.
            OceanReference? ocean = body.GetOceanReference();
            if (withOcean && ocean is { } sea && sea.Density > 0.0 && altitude < sea.Level)
            {
                double water = sea.Density / Medium.ReferenceDensityKgPerM3;
                return double.IsFinite(water) && water > 0.0 ? water : 1.0;
            }
```

3. Then the air. No reference, or one with nothing physical in it, is the model saying there is no air, which is
   knowledge and not a failed read: return 0, never the 1.0 fallback. Check the terms you divide by (sea-level
   density and scale height) instead of `IsValid`, clamp altitude to zero, and cut off at `air.Height`:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L956-L978
            // Never gate on KSA's own IsValid(). DistanceReference.IsValid requires a distance
            // over 100 km — an astronomical-scale sanity check — and the atmosphere's applies it
            // to the scale height, 8 km on Earth. So air.IsValid() is false for every realistic
            // atmosphere, and trusting it reports vacuum at ground level. Check the terms this
            // actually divides by instead.
            //
            // No reference, or one with nothing physical in it, is the model saying there is no
            // air — which is KNOWLEDGE, not a failed read, and every airless body in the game
            // answers this way. Read as the reference density it would put Earth's sea-level air on
            // the Moon, and everything flying there would be dragged by air that is not there.
            AtmosphereReference? atmosphere = body.GetAtmosphereReference();
            if (atmosphere?.Physical is not { } air) return 0.0;

            double seaLevel = air.SeaLevelDensity;
            double scaleHeight = air.ScaleHeight.InMeters();
            if (!(seaLevel > 0.0) || !(scaleHeight > 0.0)) return 0.0;

            if (altitude < 0.0) altitude = 0.0;
            if (altitude >= air.Height) return 0.0;

            double ratio = air.GetAtmosphericDensityAtAltitude(altitude) / Medium.ReferenceDensityKgPerM3;
            return double.IsFinite(ratio) && ratio >= 0.0 ? ratio : 1.0;
        }
```

4. Gravity. In the body-fixed frame it is radial: `-mu / r^2` along the ball's up direction, with `mu` from
   `((IParentBody)body).Mu`:

```csharp
// src/KSAGolf/Sim/GolfBall.cs L111-L126
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
```

5. Keep the read cheap and bounded: it runs once per sub-step batch, wrapped in `try/catch` that falls back to
   `1.0` (unreadable) because it runs inside the frame hook.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Sim/Medium.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/Medium.cs) | Drag, drag constant, reference density |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L898-L978) | `MediumDensityRatioAt`: ocean, air, traps |
| [src/KSAGolf/Sim/GolfBall.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/GolfBall.cs#L111-L146) | Gravity and drag in one sub-step |
| [tests/KSAGolf.Tests/MediumForcesTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/MediumForcesTests.cs) | Headless tests of the medium model |

## Variations and gotchas

- Never gate on `IsValid()` of the atmosphere or ocean reference.
- Airless is `0.0`, unreadable is `1.0`; conflating them puts Earth's air on the Moon.
- An air-only variant (no ocean) is needed when something aims at a point on the sea: otherwise a shell stops dead at
  the waterline in its last step.
- Rotating-frame terms (Coriolis, centrifugal) are omitted in the body-fixed integrator; fine for a putt, not for a
  long high shot.
- The height field returns the seabed, so sea level as ground is handled separately (see the simulation topic).

## See also

- [Overview and comparison](overview.md)
- [Shell and missile drag with the same density read](apply-drag-and-gravity-to-a-mod-simulated-body-2.md)
- Mod feature: [KSAGolf golf ball physics](../../../../mods/ksagolf/features/golf-ball-physics.md)
- The topic override-atmosphere-and-ocean-in-the-physics-environment (other batch) covers the game's own physics environment

[^ksa-physicalatmospherereference]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-oceanreference]: KSA.Rendering.Water.Data.OceanReference (decompiled, build 2026.10.10.5554)
[^ksa-distancereference]: KSA.DistanceReference (decompiled, build 2026.10.10.5554)
