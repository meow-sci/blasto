---
type: Game Feature
title: Read a body's atmosphere, ocean, ring, rotation, tilt, tidal-lock and orbit-element data
description: Where each per-body number lives in KSA (IParentBody, the body template, transforms and the orbit), how to read atmosphere and ocean data that may be null, sample density and pressure by altitude, and the corrections moons need for tilt and inclination
category: celestials
group: systems-and-stars
topic: read-body-properties-and-orbit-elements
demonstrated_by:
  - mods/gatos/features/celestial-body-catalog-reads
  - mods/ksa-deltavmap/features/body-state-ladder-and-you-are-here
  - mods/compendium/features/atmosphere-profile-graph
  - mods/compendium/features/derived-body-properties
lifecycle: [frame:draw-ui]
game_types:
  - KSA.IParentBody
  - KSA.AtmosphereReference
  - KSA.PhysicalAtmosphereReference
  - KSA.Rendering.Water.Data.OceanReference
  - KSA.AstronomicalTemplate
  - KSA.PlanetaryRingsReference
  - KSA.Celestial
  - KSA.RotationTemplate
  - KSA.Orbit
tags: [body-data, atmosphere, ocean, rings, rotation, tilt, inclination, orbit]
---
Mass, radius, atmosphere height, sea-level pressure and density, scale height, ocean density and level, ring presence, spin rate, sidereal period, axial tilt, tidal-lock flags and orbit inclination are scattered over `IParentBody`, the body template and transforms. None needs private state, but several can be null (no atmosphere, no ocean, no rings) and a few need a correction for moons.

## Engine background

| Datum | Where | Notes |
|---|---|---|
| Mass, mu | `Celestial.Mass`; `IParentBody.Mu` (`Mass * 6.6743E-11`) | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L15), [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L89) |
| Mean radius | `IRadius.MeanRadius` (`Celestial._meanRadius` is a public field) | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L27), [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L87) |
| Sphere of influence | `IParentBody.SphereOfInfluence` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L23) |
| Atmosphere | `IParentBody.GetAtmosphereReference()` or `BodyTemplate.AtmosphereReference`; `Physical.Height`, `SeaLevelPressure`, `SeaLevelDensity`, `ScaleHeight`; `GetAtmosphericDensityAtAltitude` / `GetAtmosphericPressureAtAltitude` | null when the body has none ([current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80-L88)) |
| Ocean | `IParentBody.GetOceanReference()`; `OceanReference.Level`, `Density` | null when none ([current/decomp/KSA.Rendering.Water.Data/OceanReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Water.Data/OceanReference.cs#L9-L13)) |
| Rings | `BodyTemplate.RingsReference` | null when none ([current/decomp/KSA/AstronomicalTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AstronomicalTemplate.cs#L66)) |
| Spin | `IParentBody.GetAngularVelocity()` (rad/s) | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L78) |
| Rotation data | `CelestialTemplate.Rotation` (`RotationTemplate`: `Tilt`, `Azimuth`, `SiderealPeriod`, `IsTidallyLocked`, `IsRetrograde`) | [current/decomp/KSA/RotationTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RotationTemplate.cs#L7-L25) |
| Orbit | `Celestial.Orbit`; `Inclination`, `Eccentricity`, `SemiMajorAxis`, `Period`, `Periapsis`, `Apoapsis` | inclination is measured against the parent's equator (CCI +Z) ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L97-L123)) |
| Frames | `GetCci2Orb()`, `GetOrb2Cci()` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L47-L49) |
| Near-surface radius | `IParentBody.GetNearSurfaceRadius()` | `Astronomical`: `MaxTerrainRadius + 1`; `AtmosphericBody`: mean radius plus atmosphere height plus 1 m ([current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L384), [current/decomp/KSA/AtmosphericBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphericBody.cs#L20-L28)) |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: interface reads projected into a snapshot](read-body-properties-and-orbit-elements-1.md) | gatos | `immutable-snapshot` | `frame:draw-ui` | One place to fix on a rename; nulls become nulls |
| [2: atmosphere, density and ring templates feeding classification, stationary radius](read-body-properties-and-orbit-elements-2.md) | ksa-deltavmap | `system-graph` | `frame:draw-ui` | Gives thresholds for gas giant, low orbit and synchronous radius |
| [3: sample the live density and pressure model by altitude](read-body-properties-and-orbit-elements-3.md) | compendium | `imgui-drawlist` | `frame:draw-ui` | Follows mods that replace the profile; plots only what the model returns |
| [4: template and transform reads for tilt, periods and inclination](read-body-properties-and-orbit-elements-4.md) | compendium | `reflection` | `frame:draw-ui` | Moons need corrected tilt and inclination; one property read by reflection |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Enumerate star systems and bodies](../../../celestials/systems-and-stars/enumerate-star-systems-and-bodies/overview.md), [Cache and key per-body data](../../../celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/overview.md)
- [Override atmosphere and ocean in the physics environment](../../../physics/aero-and-ground/override-atmosphere-and-ocean-in-the-physics-environment/overview.md), [Define atmospheres and cloud layers in system XML](../../../celestials/atmospheres-and-rings/define-atmospheres-and-cloud-layers-in-system-xml/overview.md)
- [Understand KSA coordinate frames and epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md), [Read orbit elements and timing](../../../flight-and-orbits/flight-state/read-orbit-elements-and-timing/overview.md), [Build a site frame and compute a launch window](../../../flight-and-orbits/transfers/build-a-site-frame-and-compute-a-launch-window/overview.md)
- [Draw a chart with ImDrawList](../../../ui/draw-lists/draw-a-chart-with-imdrawlist/overview.md), [Plan multi-leg and interstellar delta-v routes](../../../flight-and-orbits/transfers/plan-multi-leg-and-interstellar-delta-v-routes/overview.md)
- [Define atmospheres, scattering and cloud layers for a body in system XML](../../atmospheres-and-rings/define-atmospheres-and-cloud-layers-in-system-xml/overview.md) - related topic

