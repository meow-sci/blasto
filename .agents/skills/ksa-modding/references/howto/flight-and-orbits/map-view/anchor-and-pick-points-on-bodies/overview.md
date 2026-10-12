---
type: Game Feature
title: Anchor points to a rotating body and pick bodies and orbit lines under the cursor
description: Keep a point attached to a rotating, orbiting body so it stays put on the surface or on an orbit, and find the body, limb or orbit-line point under the mouse cursor
category: flight-and-orbits
group: map-view
topic: anchor-and-pick-points-on-bodies
demonstrated_by:
  - mods/ksarmory/features/ground-target-designation-and-reach
  - mods/ksa-measuretools/features/live-body-relative-anchors
  - mods/ksa-measuretools/features/body-and-limb-snapping
  - mods/ksa-measuretools/features/orbit-line-snapping
lifecycle: [frame:draw-ui, frame:input, event:save-load]
game_types:
  - KSA.Celestial
  - KSA.Astronomical
  - KSA.IParentBody
  - KSA.CelestialSystem
  - KSA.Universe
  - KSA.Camera
  - KSA.Orbit
  - KSA.OrbitHoverCandidate
  - KSA.FlightPlan
  - KSA.StellarBody
tags: [anchor, picking, cursor, body-fixed, cce, limb, orbit-line, map-view, save-load]
---
A point a player clicks in KSA is only meaningful relative to something that moves: the planet orbits at tens of km/s, spins under the point, and its moons and orbit lines move with it. Written down as an ecliptic coordinate, a point is left behind the instant it is stored. Mods that let the player place or designate points therefore need two things the game does not package for them: an anchor that stores the point in its owner's frame and resolves it each frame, and a picker that finds what is under the cursor (terrain, a body's disc or limb, a point on an orbit line).

## Engine background

- **Frames.** CCE is a body-centred frame with ecliptic axes: `Astronomical.GetPositionEclFromCce(cce)` is `GetPositionEcl() + cce`. The body-fixed frame turns with the body: `Celestial.GetBodyFixed2Ecl()` returns `GetCcf2Cce()`, and the spin is `GetAngularVelocity()` about body-fixed +Z, the parent pole (CCI +Z), which is also the axis inclination and LAN are measured against.[^ksa-astronomical] [^ksa-celestial] [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L369-L378) [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L174-L188)
- **Surface pins.** `Celestial.GetPositionEclFromLatLon`, `GetLatitudeFromCce`, `GetLongitudeFromCce`.[^ksa-celestial]
- **Cursor and projection.** `Camera.ScreenToEgoRay`, `EgoToEcl`, `EclToScreen` (NaN behind the camera) and `GetObjectDiameterPixels`.[^ksa-camera] [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L333-L345)
- **Stock picking.** `Astronomical.CursorHoverEligible` (cleared in `Program.OnFrameViewports`, set in `IOrbiter.OnDrawUi`), `Orbit.GetNearestPosition` and `OrbitHoverCandidate.Wins`, used by `CelestialSystem.SetNearestOrbitPoint`.[^ksa-orbit] [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L3190) [current/decomp/KSA/OrbitHoverCandidate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitHoverCandidate.cs)
- **Identity and save loads.** `CelestialSystem.Get(id)` returns the live object; `Universe.DeserializeSave` keeps the same `CurrentSystem` object.[^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430)
- **Timing.** Clicks arrive in `Glfw.PollEvents` inside `PrepareFrame` (`frame:input`) with the previous frame's camera; previews and resolution happen in `[StarMapAfterGui]` (`frame:draw-ui`); save loads are `event:save-load`. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Body-fixed ground point re-read into ecliptic position and velocity each frame](anchor-and-pick-points-on-bodies-1.md) | KSArmory | body-fixed anchor, cursor ray against terrain | `frame:draw-ui` | Gives position and ground velocity with spin, what guidance needs; terrain ray-march is your own |
| [Immutable anchors in CCE, orbit-parent or body-fixed frames, pruned and cleared on save load](anchor-and-pick-points-on-bodies-2.md) | KSA-MeasureTools | frame-relative anchors | `frame:draw-ui`, `frame:input`, `event:save-load` | One model for every kind of point, identity-safe; positions only, session-only |
| [Projected centre and disc radius, limb snap by closest point on the ray](anchor-and-pick-points-on-bodies-3.md) | KSA-MeasureTools | screen-space picking | `frame:input`, `frame:draw-ui` | Cheap one-pass scan with limb and tolerance band; `CursorHoverEligible` is stale or cleared depending on the hook |
| [Orbit.GetNearestPosition on patches, burn branches and body orbits](anchor-and-pick-points-on-bodies-4.md) | KSA-MeasureTools | reuse stock picker | `frame:input`, `frame:draw-ui` | Same answers as the stock burn picker for any body; must re-validate on screen to close the stock NaN hole |

## See also

- [Understand KSA coordinate frames and epochs](../../flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)
- [Draw a predicted trajectory and markers in the map view](../draw-a-predicted-trajectory-and-map-markers/overview.md)
- [Steer by predicted miss and model homing sensors](../../guidance/steer-by-zero-effort-miss/overview.md) (consumes ground designations)

[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
