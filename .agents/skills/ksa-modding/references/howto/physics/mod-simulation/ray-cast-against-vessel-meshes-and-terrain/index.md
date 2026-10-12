# Game Feature

* [Ray cast against part meshes and terrain, from the cursor or any origin](overview.md) - How a mod casts a ray or path segment against KSA's part meshes, kittens, cloth and the terrain height field, from the mouse cursor or from an arbitrary origin, to hit-test, place, launch or measure

# How-To

* [Cursor ray against every vehicle, plus the stock hover picker](ray-cast-against-vessel-meshes-and-terrain-2.md) - Cast Cursor.GetEgoRay against every vehicle's parts with Part.RayCastEgo to get the subpart, the hit point and the vertex normal in the subpart's local frame, gated by projected size, with an optional reuse of ViewportPartPicker
* [Frame-budgeted sweeps of the craft's own geometry for actuator travel limits](ray-cast-against-vessel-meshes-and-terrain-6.md) - Measure a turret mount's clear traverse arc and elevation band by ray-casting its barrel and line of fire against the rest of the craft at a grid of poses, coarse to fine, under a per-frame ray and millisecond budget
* [Layered cast over cloth, part meshes, kitten sphere and a terrain march](ray-cast-against-vessel-meshes-and-terrain-5.md) - Anchor a decal to whatever the cursor ray meets first, testing deployed parachute cloth, part meshes, kitten bounding spheres and then a CPU march of the terrain, each stored in a frame that survives the next frame
* [Per-triangle cast in a mover-centred frame with no camera](ray-cast-against-vessel-meshes-and-terrain-3.md) - The narrow phase for a small mover hitting a craft, using the engine's mesh cast with the mover-to-craft separation as the frame origin and the segment imposed on the unbounded ray
* [Round-centred ego origin and sphere broad phase](ray-cast-against-vessel-meshes-and-terrain-1.md) - Cast a path segment against a craft's part meshes with no camera by passing the mover-to-craft separation as the ego origin, after a bounding-sphere broad phase
* [Walk the ray over the height field in bounded steps](ray-cast-against-vessel-meshes-and-terrain-4.md) - Find where the mouse ray meets the ground by converting the cursor to camera framebuffer pixels, building the ray in Ego and stepping along it by its height above the terrain, then refining the crossing
