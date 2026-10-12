# Game Feature

* [Add planetary rings to a body or restyle existing rings at runtime](overview.md) - Build a PlanetaryRingsReference tree in code and assign it to a body template, or mutate an existing ring's meshes, textures and field settings, then dispose and recreate the rings renderer so the game rebuilds its baked ring data

# How-To

* [Build the ring reference tree, assign it to BodyTemplate.RingsReference, dispose and recreate the rings renderer](add-or-restyle-planetary-rings-1.md) - Give any moon or planet a ring by constructing the same reference classes the XML loader would, validating every asset first, assigning the tree to the body template and forcing the rings renderer to be recreated, with painted band textures
* [Mutate the public ring reference tree, snapshot originals and recreate the rings renderer](add-or-restyle-planetary-rings-2.md) - Swap the LOD meshes, rock material, band texture and field settings of an existing ring by writing into the public reference tree, cloning interleaved atlas meshes into simple ring-usable meshes, and forcing the rings renderer to rebuild
