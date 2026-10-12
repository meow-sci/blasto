# Game Feature

* [Sample terrain and pad surfaces under each wheel](overview.md) - How wheel or ground-contact code gets the true surface under a point, combining the accurate terrain height with the launch-pad and static-object surfaces the height field omits

# How-To

* [Copy convex children under the shapes lock in an ExecuteNextVehicleSolvers prefix](sample-the-ground-under-each-wheel-2.md) - Copy a launch site's convex collider children out of KSA's shared Bepu shape registry at the one main-thread moment it can be read, then ray-test the copy and the site's drawn ground triangles from anywhere to get the surface height under a point
* [GetTerrainHeightFromDirCce with accurate true in the PrepareWorker prefix](sample-the-ground-under-each-wheel-1.md) - Read the planet-fixed physics state of a craft in a Vehicle.PrepareWorker prefix and sample the accurate terrain height along each wheel hub's direction from the body centre to get the hub height above the ground
* [Rebuild box and cylinder colliders from template data and drop a ray per wheel](sample-the-ground-under-each-wheel-3.md) - Pose a launch pad's box and cylinder colliders from static-object template data exactly as ConstraintSim does, without touching the shape registry lock, and drop a line from each wheel hub onto them to get the hub height over the pad
