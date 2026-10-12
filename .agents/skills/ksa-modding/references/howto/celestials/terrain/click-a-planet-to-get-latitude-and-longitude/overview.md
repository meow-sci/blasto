---
type: Game Feature
title: Convert a click on a planet to latitude and longitude, measure between points and draw the great-circle arc
description: Ray-test the cursor against each body's mean sphere, convert the hit to latitude and longitude through the body-fixed frame, measure great-circle distance and bearing, and draw the arc only on the visible hemisphere
category: celestials
group: terrain
topic: click-a-planet-to-get-latitude-and-longitude
demonstrated_by: [mods/ksa-measuretools/features/surface-great-circle-tool]
lifecycle: [frame:input, frame:draw-ui]
game_types: [KSA.Celestial, KSA.Camera, KSA.Ray, KSA.BoundingSphere3D]
tags: [latitude, longitude, great-circle, cursor-ray, haversine, bearing, imgui-drawlist]
---
KSA gives a mod the cursor ray (`Camera.ScreenToEgoRay`), each body's mean radius and the transforms between ecliptic, body-centred and body-fixed frames, but no "what latitude and longitude is under the cursor" call. This topic assembles it: a ray against each body's mean sphere, the hit lifted to the ecliptic, converted to latitude and longitude, stored as a pin that turns with the planet, measured with the haversine formula and an initial bearing, and drawn as a great-circle arc that breaks at the limb.

## Engine background

- Ray: `Camera.ScreenToEgoRay(float2)` returns a `Ray` in camera-relative (ego) space ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L700-L710)); `Ray.Raycast(BoundingSphere3D, out t, out inside)` tests a sphere ([current/decomp/KSA/Ray.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Ray.cs#L38)); `Camera.GetPositionEgo(body)` places the body sphere ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236)) and `Camera.EgoToEcl` lifts a point to the ecliptic ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L223)).
- Frames: `Astronomical.GetPositionCceFromEcl` subtracts the body's position ([current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L375-L378)); `Celestial.GetLatitudeFromCce` and `GetLongitudeFromCce` rotate into the body-fixed frame with `GetCce2Ccf` and take `asin(z)` and `atan2(y, x)` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L691-L740)).
- Back again: `Celestial.GetPositionEclFromLatLon(lat, lon)` builds the body-fixed direction (`GetDirCcfFromLatLon`) and rotates it with `GetCcf2Cce()` onto the mean-radius sphere ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L667-L682)), so a stored pin turns with the planet when re-resolved each frame.
- Pins sit on the mean sphere, not on terrain; for the height under a point use [the height-query topic](../query-terrain-height-and-line-of-sight/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: sphere pick, GetLatitudeFromCce/GetLongitudeFromCce, haversine, chord and bearing](click-a-planet-to-get-latitude-and-longitude-1.md) | ksa-measuretools | `screen-to-world-ray` | `frame:input`, `frame:draw-ui` | Exact on the mean sphere; ignores terrain relief and oblateness |
| [2: 48 Rodrigues samples with a back-face test and midpoint label](click-a-planet-to-get-latitude-and-longitude-2.md) | ksa-measuretools | `imgui-drawlist` | `frame:draw-ui` | Arc that breaks at the limb; coincident or antipodal pins have no unique circle |

## See also

- [Convert the cursor to a world ray](../../../ui/input/cursor-to-world-ray/overview.md), [Pick objects under the cursor](../../../ui/input/pick-objects-under-the-cursor/overview.md), Anchor and pick points on bodies
- [Understand KSA coordinate frames and epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md), [Project world positions to screen pixels](../../../rendering/overlays/project-world-positions-to-screen/overview.md)
- [Block game hotkeys while typing](../../../ui/input/block-game-hotkeys-while-typing/overview.md), [Draw markers and annotations on world objects](../../../rendering/overlays/draw-markers-and-annotations-on-world-objects/overview.md)
- [Query the terrain height field and test line of sight](../../../celestials/terrain/query-terrain-height-and-line-of-sight/overview.md)
- [Anchor points to a rotating body and pick bodies and orbit lines under the cursor](../../../flight-and-orbits/map-view/anchor-and-pick-points-on-bodies/overview.md) - related topic

