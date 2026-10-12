# Game Feature

* [Override the terrain height cameras and wheels see](overview.md) - How a mod makes wheel and camera code treat a roof, ramp or floor as the ground by postfixing the camera-frame terrain height query while leaving the fixed-frame query that physics collision reads

# How-To

* [Postfix GetTerrainHeightFromDirCce keyed on who is asking](override-terrain-height-for-roofs-and-platforms.md) - Answer a roof, ramp slab, spiral level or pit floor from the camera-frame terrain height query depending on whether a vehicle being prepared or the camera is asking, without moving any collider
