---
type: How-To
title: Height of the terrain directly below a craft for hover and touchdown
description: Rotate the craft's inertial position into the body-fixed frame and read Celestial.GetTerrainHeightFromDirCcf for a hover and touchdown height, with a finite check and the Ccf versus Cce choice explained
category: celestials
group: terrain
topic: query-terrain-height-and-line-of-sight
approach: 4
difficulty: beginner
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/terminal-hover-touchdown]
techniques: [harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Celestial, KSA.IParentBody, KSA.Vehicle, KSA.Orbit]
tags: [terrain, height, hover, touchdown, landing, ccf]
---
Outcome: the height of a craft above the ground directly below it, usable as the input of a hover or touchdown controller. Demonstrated by KSA-AdvancedFlightComputer's terminal hover ([Terminal hover and touchdown](../../../../mods/ksa-advancedflightcomputer/features/terminal-hover-touchdown.md)). Prerequisite: the controller already runs where it can read the vehicle state (here a Harmony prefix on `Vehicle.PrepareWorker`).

## Background: how the game does it

The ground height is one query, `Celestial.GetTerrainHeightFromDirCcf(direction, accurate = true)` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789-L795)). A craft's state is held as `Orbit.StateVectors.PositionCci` (body-centred inertial), so the direction needs one rotation into the body-fixed frame (`IParentBody.GetCci2Ccf`, [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L41)) and no ecliptic detour. The `Ccf` overload applies no rotation of its own; the `Cce` overload would apply `GetCcf2Cce().Inverse()` for an ecliptic direction ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L781)). The default `accurate: true` uses bicubic height and evaluates every modifier, so the answer agrees with the surface the legs touch, which matters in the last metres. A fast `accurate: false` call would drop launch-pad levelling decals. See [approach 1](query-terrain-height-and-line-of-sight-1.md) for the full table.

## Lifecycle and timing

The hover step runs in the `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`) as part of the landing state machine. That is the last settled main-thread window before the vehicle snapshot goes to the worker, so the state vectors and the body's rotation are consistent. Terrain data is read-only. Touchdown itself is decided by the engine's contact flag (`vehicle.Situation.HasAnyContact()`), not by this height. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Take the craft's position relative to the parent body** (`orbit.StateVectors.PositionCci`) and its parent as `IParentBody`.
2. **Normalise and rotate it** with `GetCci2Ccf()`.
3. **Cast the parent to `Celestial`.** A star is a `StellarBody` and has no terrain; the cast fails and the height defaults to 0.
4. **Guard non-finite results** and fall back to zero, then subtract `bodyRadius + terrain` and the vehicle's height so zero means legs on the ground:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs L91-L99
// Height of the touchdown plane above the terrain DIRECTLY BELOW the vehicle (not the landing site - a hover can drift anywhere), minus the vehicle height so zero means legs on the ground.
private static double TerminalHeight(Orbit orbit, IParentBody parent, double bodyRadius)
{
    double3 r = orbit.StateVectors.PositionCci;
    double3 dirCcf = double3.Normalize(r).Transform(parent.GetCci2Ccf());
    double terrain = (parent as Celestial)?.GetTerrainHeightFromDirCcf(dirCcf) ?? 0.0;
    if (!double.IsFinite(terrain))
        terrain = 0.0;
    return r.Length() - (bodyRadius + terrain) - _s.VehicleHeightM;
```

The mod measures this height under the craft, not under the landing site, because a hover can drift anywhere.

## Contrast: the other entry points and the engine's own radar altitude

Which overload to call is decided by the frame of the direction already in hand; all three share one height field.

| Direction you hold | Call | Example |
|---|---|---|
| Body-centred inertial (`PositionCci`) | `GetTerrainHeightFromDirCci(dir)`, or rotate with `GetCci2Ccf()` and use `Ccf` as above | The engine's own `Vehicle.GetRadarAltitude` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3060-L3088)) |
| Ecliptic position and body centre | `GetTerrainHeightFromDirCce(unit(positionEcl - centreEcl), accurate: true)` | KSACars snapping a point onto the ground ([src/KSACars/Ksa/KsaWorld.Pointing.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.Pointing.cs#L428-L434)) |
| Body-fixed (latitude and longitude) | `GetTerrainHeightFromDirCcf(dir, accurate: true)` | KSACars' `ground` bridge command along a lat/lon line ([src/KSACars/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Bridge.cs#L331)) |

The engine already offers `Vehicle.GetRadarAltitude()`, which does the `Cci` query and also raises the terrain to the ocean's `GetOceanHeightAtPositionCcf` where the body has an ocean. It measures from the vehicle's origin; the AFC hover prefers its own `TerrainHeight` so it can subtract the craft's height and ignore waves. If you only need "altitude above the ground or sea", read the engine's value rather than re-deriving it.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs) | Hover profile, PID, `TerminalHeight` |
| [AdvancedFlightComputer/Features/Guidance/Ui/Gauges/HoverGauge.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Gauges/HoverGauge.cs) | Gauge and keypad nudges |

## Variations and gotchas

- **Use the terrain under the vehicle, not under the site.**
- **Do not detect touchdown from the height.** The contact flag is what the physics uses.
- **Non-finite means no data.** The mod falls back to zero; a safer variant skips the control step instead (see the no-claim rule in approach 1).
- **A hover can be refused** if the engine's minimum thrust exceeds weight; that is a flight-computer matter, not a terrain one.

## See also

- [Overview](overview.md); [approach 1](query-terrain-height-and-line-of-sight-1.md)
- [Terminal hover and touchdown](../../../../mods/ksa-advancedflightcomputer/features/terminal-hover-touchdown.md)
- [Sample the ground under each wheel](../../../physics/aero-and-ground/sample-the-ground-under-each-wheel/overview.md), [Understand KSA coordinate frames and epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)
