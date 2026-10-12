---
type: Mod Feature
title: Terrain contact under each wheel
description: Find the ground under each wheel hub by sampling the body's height field along the hub's planet-fixed direction, using the vehicle's physics state rather than its analytic orbit position
mod: ksacars
category: physics
techniques: [custom-physics, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types:
  - KSA.Celestial
  - KSA.PhysicsStates
  - KSA.Vehicle
  - KSA.IParentBody
  - KSA.AtmosphereReference
  - KSA.TerrainPatch
  - KSA.BubbleTerrainGrid
  - KSA.TerrainImpactFinder
tags: [physics, terrain, height-field, ccf, reference-frames, wheels, ground-contact]
---

Each frame KSACars needs, for every wheel, how far its hub is above the ground and how fast it is
moving over it. The mod gets both by reading the vehicle's **physics state** in the
**planet-fixed frame (Ccf)** and sampling the body's terrain height field along each hub's
direction from the planet's centre.[^ksacars-buggies] Two choices make it work: Ccf, where the
ecliptic orbital motion and the planet's spin cancel so a hub's velocity is simply its velocity
over the ground; and the physics state, because a landed craft's analytic orbit position can be
metres away from where the physics has it, which would throw a spring measured in centimetres off
the planet.[^ksacars-claude]

## How it works

Inside `Buggies.Step` (called from the [PrepareWorker prefix](./prepareworker-impulse-injection.md)):

1. `PhysicsStates.GetStatesCcf(in states.Origin, in states.Kinematic, out positionCcf, out velocityCcf, out body2Ccf)`
   gives the centre of mass position, velocity and attitude in the body-fixed frame.[^ksa-physicsstates]
2. Velocity and spin are rotated into the car's body frame (`ccf2Body`, `Body2Phys`).
3. For each corner, the hub's rest point relative to the centre of mass
   (`Part.PositionVehicleAsmb + Part.Asmb2VehicleAsmb * corner.Hub - CenterOfMassAsmb`) is rotated
   to Ccf and added to the centre of mass, giving `atCcf`.
4. `dirCcf = atCcf / |atCcf|` is converted to Cce with `Celestial.GetCcf2Cce()` and passed to
   `TerrainHeights.TryHeight`, which calls `Celestial.GetTerrainHeightFromDirCce(dir, accurate: true)`
   and rejects non-finite results.[^ksacars-terrainheights]
5. `hubHeight = |atCcf| - (MeanRadius + terrainHeight)`; if a [launch pad](./launch-pad-surface-contact.md)
   collider is under the hub, the lesser of the two is used.
6. `WheelContact(Valid, HubHeight, GroundUp = dirCcf in body frame, HubVelocity = velocityBody + spinBody x hub)`
   goes to [`BuggyDrive.Step`](./wheel-suspension-tyre-model.md).

The ground's "up" at each wheel is the radial direction, not a terrain normal: the slope emerges
from the four wheels reading four different heights. Gravity is `mu / r^2` from the parent's
`IParentBody.Mu`, and air density comes from the parent's `AtmosphereReference.Physical`
(`GetAtmosphericDensityAtAltitude`), returning 0 on an airless body.[^ksacars-ksaworld]

`ITerrainHeights` is the seam between the two halves of the repo: `Sim/` code depends on the
interface only, so the headless tests drive the cars over flat ground with no KSA assembly
loaded.[^ksacars-iterrainheights]

### What `accurate: true` buys

`Celestial.GetTerrainHeightFromDirCce` rotates the direction to Ccf and calls
`GetTerrainHeightFromDirCcf` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789-L882), L789-L882). The flag changes
two things only. The base height is sampled bicubically (`SampleHeightBicubic`) instead of
bilinearly (`SampleHeightMapBilinear`), and inside the modifier loop `modifierReference.Evaluate`
is called only when `accurate` is set. Everything else still runs either way: the normal-map,
biome-id and biome-control samples and the per-modifier LUT weight. So `accurate: false` is not
much cheaper on a body with biome materials, and it drops every modifier's contribution.[^ksa-celestial]
Every `GetTerrainHeight*` overload defaults to `accurate = true`. The engine's own physics terrain
uses that default: `TerrainPatch.GetTerrainRadiusCcf` builds the collision patch from
`GetTerrainHeightFromDirCcf(dir)` ([current/decomp/KSA/TerrainPatch.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainPatch.cs#L518-L525), L518-L525), and so
does `BubbleTerrainGrid.SampleRadius` ([current/decomp/KSA/BubbleTerrainGrid.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BubbleTerrainGrid.cs#L255-L259), L255-L259).
Only `TerrainImpactFinder.Clearance` (impact prediction), a camera helper and
`LocationReference` pass `accurate: false`
([current/decomp/KSA/TerrainImpactFinder.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainImpactFinder.cs#L64), L64).[^ksa-terrainpatch][^ksa-bubbleterraingrid][^ksa-terrainimpactfinder]
On a detailed body like Earth most of the metre-scale relief comes from the modifiers, so a wheel
reading the non-accurate field would drive on a smoothed surface the engine's own colliders do not
match. The
mod's `docs/KSA-TERRAIN.md` measures the field: a 1.5-3.1 km base grid on Earth, 0.2985 m per
16-bit height level, and the modifier stack evaluated on a direction packed to `float3`, which
quantises the surface into roughly 0.31 m treads (the `float3.Pack` calls are in the decomp).[^ksacars-terrain-doc]

## Lifecycle and timing

Runs once per frame on the main thread inside the `Vehicle.PrepareWorker` prefix
(`frame:dispatch-solvers`). At that point `Universe.ApplyVehicleSolvers` has already copied the
previous step's result into the vehicle and refreshed per-frame data, so the physics state, the
body's attitude (`GetCcf2Cce`) and the height field all describe the same instant: the end of the
step just applied.[^ksacars-epochs] The impulse computed from them is integrated by the step that is
about to be dispatched. Reading terrain from a StarMap hook would also work for the heights, but
the forces would then be lost (see the impulse feature).

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L600-L670) | Ccf state, per-hub direction, height, pad override, `WheelContact`, gravity and air |
| [src/KSACars/Ksa/TerrainHeights.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/TerrainHeights.cs) | One body's height field via `GetTerrainHeightFromDirCce` |
| [src/KSACars/Sim/ITerrainHeights.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/ITerrainHeights.cs) | KSA-free seam; false means "no claim", never flat ground |
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L611-L669) | `GravityAt` and `AirDensityRatioAt` from the parent body |
| [docs/KSA-TERRAIN.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/docs/KSA-TERRAIN.md) | The authors' measurements of the height field |

## Snippets

```csharp
// src/KSACars/Ksa/Buggies.cs L647-L666
        for (int i = 0; i < corners.Length; i++)
        {
            double3 hub = hubs[i];

            double3 atCcf = positionCcf + hub.Transform(body2Ccf);
            double radius = Vec.Len(atCcf);
            if (!(radius > 0.0)) continue;
            double3 dirCcf = atCcf / radius;
            if (!ground.TryHeight(dirCcf.Transform(ccf2Cce), out double height)) continue;

            double hubHeight = radius - (body.MeanRadius + height);
            if (pads.TryHeightOver(atCcf, out double overPad)) hubHeight = Math.Min(hubHeight, overPad);

            e.HubHeights[i] = hubHeight;
            contacts[i] = new WheelContact(
                Valid: true,
                HubHeight: hubHeight,
                GroundUp: dirCcf.Transform(ccf2Body),
                HubVelocity: velocityBody + Vec.Cross(spinBody, hub));
        }
```

```csharp
// src/KSACars/Ksa/TerrainHeights.cs L13-L32
internal sealed class TerrainHeights(Celestial body, bool accurate = false) : ITerrainHeights
{
    private readonly Celestial _body = body;
    private readonly bool _accurate = accurate;

    public bool TryHeight(double3 dirFromCentre, out double metres)
    {
        metres = 0.0;

        try
        {
            metres = _body.GetTerrainHeightFromDirCce(dirFromCentre, accurate: _accurate);

            return double.IsFinite(metres);
        }
        catch
        {
            return false;
        }
    }
```

## Game APIs touched

| API | Purpose |
|---|---|
| `PhysicsStates.GetStatesCcf` ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L428)) | Physics-state position, velocity and attitude in the planet-fixed frame |
| `Celestial.GetTerrainHeightFromDirCce` / `GetTerrainHeightFromDirCcf` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L882)) | Height above the mean sphere along a direction; `accurate` selects bicubic + modifiers |
| `Celestial.GetCcf2Cce` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L532)) | Body-fixed to body-centred equatorial rotation |
| `Celestial.MeanRadius`, `IParentBody.Mu`, `Celestial.GetAtmosphereReference()` | Ground radius, gravity, air density |
| `Vehicle.CenterOfMassAsmb`, `Part.PositionVehicleAsmb`, `Part.Asmb2VehicleAsmb` | Hub positions relative to the centre of mass |

## Pitfalls and version notes

- **Never use the analytic position for a landed craft.** The orbit's state vectors and the
  physics state share an epoch but not a frame; on a landed craft they differ by metres. The bridge
  even reports the difference (`orbit_off_m`).[^ksacars-buggies]
- **Ecl velocities are not ground speeds.** Near Earth an ecliptic velocity carries ~29.8 km/s of
  orbital motion; in Ccf it cancels.[^ksacars-epochs]
- **The height field answers the seabed.** There is no waterline clamp, so a car driven into the sea
  is sprung against the sea floor.[^ksacars-terrain-doc]
- **A launch pad is not in the height field**; it sits on a levelled decal disc, and its deck is
  1.75 m higher. That is why [launch pad surface contact](./launch-pad-surface-contact.md) exists.
- **`TerrainHeights`' comment is wrong on both counts in this build.** It calls `accurate: false`
  "the engine's own terrain solver's choice, and the default". In build 2026.10.10.5554 every
  `GetTerrainHeight*` overload defaults to `accurate = true`, and the physics terrain
  (`TerrainPatch`, `BubbleTerrainGrid`) samples with that default; only the impact predictor uses
  `false`. Every caller in the mod (`Buggies.Step`, the craft mover's ray, the bridge's `ground`
  survey) passes `accurate: true` explicitly, so behaviour is unaffected and matches the colliders.
  The mod's own `docs/KSA-TERRAIN.md` already says the same.[^ksacars-terrainheights][^ksacars-terrain-doc]
- **`ITerrainHeights`' doc comment refers to an `IGroundTest`** that does not exist in this
  repository (the helper set was carried over from another LaurensDeV mod).
- A failed read (`TryHeight` false) leaves that wheel's contact invalid, which the model treats as
  a wheel in the air, never as ground at the mean sphere.[^ksacars-iterrainheights]

## See also

- [Inject wheel forces from a Vehicle.PrepareWorker prefix](./prepareworker-impulse-injection.md)
- [Wheel, suspension and tyre model](./wheel-suspension-tyre-model.md)
- [Launch pad surface contact](./launch-pad-surface-contact.md)
- [Craft mover](./craft-mover-mouse-pick-place.md) (walks a ray against the same height field)
- [GetTerrainHeightFromDirCce with accurate true in the PrepareWorker prefix](../../../howto/physics/aero-and-ground/sample-the-ground-under-each-wheel/sample-the-ground-under-each-wheel-1.md) - how-to article demonstrating this feature
- [Raycast-style suspension impulses in the PrepareWorker prefix](../../../howto/physics/aero-and-ground/simulate-wheels-and-suspension-without-a-wheel-module/simulate-wheels-and-suspension-without-a-wheel-module.md) - how-to article demonstrating this feature
- [Read GetStatesCcf in the PrepareWorker prefix](../../../howto/physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-5.md) - how-to article demonstrating this feature

[^ksacars-buggies]: src/KSACars/Ksa/Buggies.cs
[^ksacars-terrainheights]: src/KSACars/Ksa/TerrainHeights.cs
[^ksacars-iterrainheights]: src/KSACars/Sim/ITerrainHeights.cs
[^ksacars-ksaworld]: src/KSACars/Ksa/KsaWorld.cs
[^ksacars-terrain-doc]: docs/KSA-TERRAIN.md
[^ksacars-epochs]: docs/FRAMES-AND-EPOCHS.md
[^ksacars-claude]: CLAUDE.md
[^ksa-celestial]: KSA.Celestial (decompiled)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled)
[^ksa-terrainpatch]: KSA.TerrainPatch (decompiled)
[^ksa-bubbleterraingrid]: KSA.BubbleTerrainGrid (decompiled)
[^ksa-terrainimpactfinder]: KSA.TerrainImpactFinder (decompiled)
