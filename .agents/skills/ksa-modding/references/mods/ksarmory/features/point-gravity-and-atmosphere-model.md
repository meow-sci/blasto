---
type: Mod Feature
title: Point gravity, body fall and one-reference air for mod-integrated bodies
description: Rounds the mod integrates itself feel mu over r squared toward their body back-dated to the sub-step, plus the body's own fall toward its primary, and are dragged by air and water read from KSA's atmosphere and ocean against one fixed reference density, avoiding KSA's IsValid traps
mod: ksarmory
category: physics
techniques: [custom-physics, sim-boundary, epoch-back-dating, frame-conversion, quadratic-drag]
hooks: [starmap StarMapAfterGui (via StepOnce)]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.IParentBody
  - KSA.Celestial
  - KSA.Astronomical
  - KSA.AtmosphereReference
  - KSA.PhysicalAtmosphereReference
  - KSA.Rendering.Water.Data.OceanReference
  - KSA.DistanceReference
  - KSA.StateVectors
tags: [gravity, atmosphere, drag, ocean, buoyancy, frames, epochs]
---

KSArmory integrates its rounds, decoys and predicted warhead arcs itself, so it needs its own model
of the two forces KSA would otherwise apply: gravity and the medium (air, or water). The model is
deliberately simple (a point mass and a quadratic drag) but it is careful about **frames and
epochs**, because each shortcut it avoids was measured to cost hundreds of metres. The formulas live
in game-free `Sim/` classes; `KsaWorld` reads the engine.[^point-gravity][^medium][^ksaworld]

## How it works

### Gravity

| Term | Code | Why |
|---|---|---|
| `mu / r^2` toward the body | `PointGravity.Toward(centreEcl, mu, positionEcl)`, `mu = IParentBody.Mu` (`Mass * 6.6743e-11`) | Zero inside a metre of the centre, where the direction is noise[^point-gravity][^ksa-iparentbody] |
| Back-dated centre | `PointGravity.OnRound(mu, bodySample, bodyVelocity, bodyFall, position, secondsIntoFrame)` aims at `bodySample + bodyVelocity * secondsIntoFrame` | KSA samples celestials once per frame, at the step's end; a round read part-way through the step would otherwise be pulled toward a body a step of its ~30 km/s orbital speed away (513 m at 17 ms)[^point-gravity][^weapon-system] |
| The body's own fall | `KsaWorld.BodyFallEcl(body)` = `PointGravity.Toward(primary.GetPositionEcl(), primary.Mu, body.GetPositionEcl())` | A round integrated against its own body in the ecliptic frame is left behind by the acceleration the ground shares (measured 428 m over a 375 s coast on KSA's Earth). Computed rather than read, because KSA's celestials move on analytic Kepler rails and `StateVectors.AccelerationCci` is set to zero by most constructors[^ksaworld][^ksa-statevectors] |

`Celestial.Parent` is typed as the interface `IParentBody`; testing `is Celestial` instead
silently returns zero for every body, which the mod's comment warns about.[^ksaworld][^ksa-celestial]
The fall is one link deep (Earth to Sol is exact; a lunar shot loses Earth's own fall toward Sol).[^ksaworld]

### The medium

| Piece | Code | Detail |
|---|---|---|
| Density ratio | `KsaWorld.MediumDensityRatioAt(body, positionEcl, withOcean)` | Altitude over `MeanRadius`; below `OceanReference.Level` (when `withOcean`) the medium is water at `Density / 1.225`; otherwise `air.GetAtmosphericDensityAtAltitude(alt) / 1.225`, and 0 above `air.Height`[^ksaworld][^ksa-ocean][^ksa-atmosphere] |
| One reference density | `Medium.ReferenceDensityKgPerM3 = 1.225` | Every body's air is measured against Earth's sea level as a unit, so a round is dragged by the air actually present (a fiftieth over a Mars-like surface)[^medium] |
| Airless is knowledge | No `AtmosphereReference.Physical` returns 0.0, not the 1.0 fallback | Reading "no reference" as unknown put Earth's sea-level air on the Moon: a bomb released 7 m over lunar ground reached a 113 m/s terminal speed[^ksaworld] |
| Air-only variant | `AirDensityRatioAt` (no ocean) | A gun lay targeting a point on the sea would otherwise stop the shell dead in its last step at the waterline[^ksaworld] |
| Drag | `Medium.Drag(localVelocity, munition, ratio) = v * k * |v| * ratio` | Quadratic in airspeed against the ground-relative velocity, zero in vacuum, at rest, or with no drag constant[^medium] |
| Drag constant from shape | `Medium.DragK(mass, calibre, Cd) = 0.5 * 1.225 * Cd * pi * r^2 / m` | `MunitionProfile.AppliedDragK` uses it when mass, calibre and coefficient are all given, else the hand-set `DragK`[^medium][^munition-profile] |
| Buoyancy | `Medium.Buoyancy(g, munition, ratio) = g * (1 - ratio / NeutralDensityRatio)` | A torpedo swims at its neutral density; zero disables the term[^medium] |
| Parachute | `Medium.ChuteDrag` | Sized from a sink rate: `k = g0 / sink^2`, ramped while the canopy fills[^medium] |
| Faithful step in air | `Medium.FaithfulStepInAir = 0.05` s | Density falls off on a kilometres scale height; measured about 1.7 m of impact shift per millisecond of frame on a deorbit[^medium] |
| Ground velocity | `KsaWorld.GroundVelocityAt(body, position) = body.GetVelocityEcl() + cross(GetAngularVelocityCce(), r)` | Spin in Cce, not Cci: the frames differ by the axial tilt, which invents up to 190 m/s at Earth's surface speed[^ksaworld][^ksa-iparentbody] |
| Ambient air record | `AmbientAir(DensityRatio, PressureRatio, Known)` | Speed of sound `sqrt(gamma * P / rho)` for blast and sound effects, and a flag for "could not read, assumed sea level"[^ambient-air] |

### Two KSA `IsValid` traps

- `PhysicalAtmosphereReference.IsValid()` requires `ScaleHeight.IsValid()`, and
  `DistanceReference.IsValid()` is `|value| > 100000` (metres). Earth's 8 km scale height fails it, so
  **every realistic atmosphere reports invalid**; the mod checks `SeaLevelDensity > 0` and
  `ScaleHeight > 0` instead.[^ksaworld][^ksa-atmosphere][^ksa-distancereference]
- The ocean's level (0 m) fails the same distance check, so the mod uses "no ocean reference" as
  the discriminator and `Density > 0`.[^ksaworld][^ksa-ocean]

## Lifecycle and timing

- **`frame:draw-ui`**, inside `StepSimulation` (normally the `[StarMapAfterGui]` hook via
  `StepOnce`): the system samples `_bodyVelocityEcl` once per frame beside the platform, then every
  sub-step asks `GravityIntoFrame`/`AirDensityIntoFrame` with a negative `secondsIntoFrame`, so
  body and round are compared at the same instant.[^weapon-system]
- Celestial positions read here are those of the step KSA just applied in `PrepareFrame`; that
  is the "sample at the step's end" every back-dating term corrects for.[^frames-doc]
- All `KsaWorld` readers catch exceptions and return documented fallbacks (zero gravity, density
  1.0 for an unreadable atmosphere), because they run inside the frame hook.[^ksaworld]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Sim/PointGravity.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PointGravity.cs) | Inverse square, back-dated, plus body fall |
| [src/KSArmory/Sim/Medium.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Medium.cs) | Drag, buoyancy, chute, drag constant, step bound |
| [src/KSArmory/Sim/AmbientAir.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/AmbientAir.cs) | Density and pressure ratios, speed of sound |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L2158-L2232) | `GravityAt`, `PullOnRound`, `BodyFallEcl` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L2493-L2541) | `MediumDensityRatioAt`: ocean, air, traps |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L3249-L3320) | Per-round gravity and density callbacks |

## Snippets

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

```csharp
// current/decomp/KSA/DistanceReference.cs L162-L165
	public override bool IsValid()
	{
		return !double.IsNaN(_value) && Math.Abs(_value) > 100000.0;
	}
```

## Game APIs touched

| Member | Use |
|---|---|
| `IParentBody.Mu`, `GetAngularVelocityCce()`, `GetOceanReference()` ([current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L15)) | Gravitational parameter, spin, ocean |
| `Celestial.Parent` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L69)), `GetPositionEcl()`, `GetVelocityEcl()`, `MeanRadius`, `GetAtmosphereReference()` | Primary, sample, radius, air |
| `PhysicalAtmosphereReference.SeaLevelDensity`, `ScaleHeight`, `Height`, `GetAtmosphericDensityAtAltitude`, `IsValid` ([current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L25)) | Air density profile; the validity trap |
| `DistanceReference.IsValid` ([current/decomp/KSA/DistanceReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DistanceReference.cs#L162)) | The 100 km check behind the trap |
| `OceanReference.Level`, `Density` ([current/decomp/KSA.Rendering.Water.Data/OceanReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Water.Data/OceanReference.cs)) | Water as a medium |
| `StateVectors.AccelerationCci` ([current/decomp/KSA/StateVectors.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StateVectors.cs#L16)) | Not used: zero from most constructors |

## Pitfalls and version notes

- **Back-date the body, not the round**: the celestial sample is at the end of the step just
  applied; anything evaluated mid-step must move the body back by its velocity times the offset.[^point-gravity][^frames-doc]
- **Do not trust `IsValid()` on atmosphere or ocean data** in this build; check the terms you divide by.[^ksa-distancereference]
- **Distinguish "no air" from "could not read"**: 0.0 vs 1.0, or a vacuum round gets Earth's drag.[^ksaworld]
- **Spin in the right frame**: `GetAngularVelocityCce` for a Cce offset; `GetAngularVelocityCci`
  differs by the axial tilt.[^ksaworld][^ksa-iparentbody]
- The body-fall term is one level deep; moons inherit an error from their planet's own fall.[^ksaworld]

## See also

- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Ecliptic frames and epochs](./ecliptic-frames-and-epochs.md)
- [Terrain ground-impact test](./terrain-ground-impact-test.md)
- [Proportional navigation guidance](./proportional-navigation-guidance.md)
- [Countermeasure dispensers](./countermeasure-dispensers.md)
- [Unguided ballistic lead](./unguided-ballistic-lead.md)
- [High-altitude burst sky](./high-altitude-burst-sky.md)
- [Shell and missile drag with the same density read](../../../howto/physics/mod-simulation/apply-drag-and-gravity-to-a-mod-simulated-body/apply-drag-and-gravity-to-a-mod-simulated-body-2.md) - how-to article demonstrating this feature

[^point-gravity]: src/KSArmory/Sim/PointGravity.cs
[^medium]: src/KSArmory/Sim/Medium.cs
[^ambient-air]: src/KSArmory/Sim/AmbientAir.cs
[^munition-profile]: src/KSArmory/Sim/MunitionProfile.cs
[^ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^weapon-system]: src/KSArmory/Ksa/WeaponSystem.cs
[^frames-doc]: docs/FRAMES-AND-EPOCHS.md
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-atmosphere]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-distancereference]: KSA.DistanceReference (decompiled, build 2026.10.10.5554)
[^ksa-ocean]: KSA.Rendering.Water.Data.OceanReference (decompiled, build 2026.10.10.5554)
[^ksa-statevectors]: KSA.StateVectors (decompiled, build 2026.10.10.5554)
