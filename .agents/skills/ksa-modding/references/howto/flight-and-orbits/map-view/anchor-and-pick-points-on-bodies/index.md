# Game Feature

* [Anchor points to a rotating body and pick bodies and orbit lines under the cursor](overview.md) - Keep a point attached to a rotating, orbiting body so it stays put on the surface or on an orbit, and find the body, limb or orbit-line point under the mouse cursor

# How-To

* [Click a point on the terrain and keep it as a body-fixed anchor re-read into ecliptic position and velocity every frame](anchor-and-pick-points-on-bodies-1.md) - Cast the cursor ray from the camera against terrain, store the hit as an offset in the nearest body's own rotating frame, and turn it back into an ecliptic position and a ground velocity with spin every frame
* [Pick a body under the cursor by its projected centre and disc radius, and snap to its limb by the closest point on the cursor ray](anchor-and-pick-points-on-bodies-3.md) - Project every body's centre and disc radius to screen, let the smallest disc under the cursor win, snap to the limb in an edge band using the cursor ray's closest point to the centre, and gate dot-sized bodies on the game's CursorHoverEligible flag
* [Pick the point on an orbit line under the cursor with Orbit.GetNearestPosition and re-validate it on screen](anchor-and-pick-points-on-bodies-4.md) - Reuse the game's nearest-point solver and OrbitHoverCandidate tie-break on flight-plan patches, burn branches and body orbits, then re-project each candidate to close the behind-camera NaN hole and the integer aspect bug in the stock acceptance test
* [Store points as immutable anchors in CCE, orbit-parent or body-fixed frames, prune them by identity and clear them on save load](anchor-and-pick-points-on-bodies-2.md) - Keep map-view points attached to what they were placed on by storing each in its owner's frame and resolving to the ecliptic only at use time, drop anchors whose body object was replaced, and clear state from a Universe.DeserializeSave postfix
