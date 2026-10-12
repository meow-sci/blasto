# Game Feature

* [Part meshes, attach nodes and part-local points in vehicle, world and screen space](overview.md) - How KSA stores part geometry and the transform chain from a part-local point to vehicle assembly, ego, ECL and screen coordinates

# How-To

* [Analyse a part mesh: MeshView positions, weld, dihedral feature edges and Kasa circle fit](get-part-points-in-vehicle-and-world-frames-4.md) - Read the index-unrolled triangle positions of a shared MeshReference and derive welded vertices, sharp edges and fitted rim circles, cached per asset
* [Clone an interleaved part mesh into a Simple mesh for your own renderer](get-part-points-in-vehicle-and-world-frames-5.md) - Build a Simple MeshReference sharing a part mesh's HostPrimitives and bind a SimpleVkMesh so renderers that cannot draw interleaved meshes can use it
* [Derive the stack axis from connectors and split vectors into axial and radial parts](get-part-points-in-vehicle-and-world-frames-3.md) - Find a part's stack axis from its opposed connectors, mirror points across it and split an ECL delta into components along and across the vehicle's X axis
* [Locate attach nodes and part centres with Connector.PositionEgo and PositionVehicleAsmb](get-part-points-in-vehicle-and-world-frames-1.md) - Project a vehicle's connectors and part bounding-box centres to screen pixels and vehicle assembly coordinates without a render camera
* [Turn a part-local point into an ECL position through the centre of mass and Asmb2Ego](get-part-points-in-vehicle-and-world-frames-2.md) - Store a point in a part's frame and resolve it to ECL every frame by applying MatrixAsmb2VehicleAsmb, the centre-of-mass offset and the vehicle's Asmb2Ego about its ECL position
