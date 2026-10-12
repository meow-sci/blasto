# Game Feature

* [Check GLB meshes offline: z-fighting, degenerate UVs, names and bounds](overview.md) - How mods find coplanar faces, zero-area UVs, unpaired node and mesh names and unmaterialed primitives in a GLB with no Blender and no game, in Python and in C#

# How-To

* [Compare exports by surface fingerprint and sweep articulated assemblies for collisions](find-z-fighting-and-degenerate-uvs-in-glb-2.md) - Compare two GLBs by an order-independent triangle fingerprint because the exporter permutes the index buffer, seed the box skin at generation time, and test convex primitives with SAT through every pose of an articulated mount
* [Find coplanar faces and zero-area UVs with checkmesh.py and read bounds with meshinfo.py](find-z-fighting-and-degenerate-uvs-in-glb-1.md) - Pure Python readers of the GLB binary that reduce triangles to quantised sign-canonical planes, measure UV area, check node and mesh name pairing and missing materials, run in CI, and list mesh bounds from accessor min and max
* [Parse the GLB JSON chunk in C# to validate mesh references](find-z-fighting-and-degenerate-uvs-in-glb-3.md) - Read only the JSON chunk of a GLB or glTF in C# to get mesh names without loading geometry, use them to check that every PartModel Mesh Id exists in a declared atlas, and list the meshes of registered glTF assets to pick from
