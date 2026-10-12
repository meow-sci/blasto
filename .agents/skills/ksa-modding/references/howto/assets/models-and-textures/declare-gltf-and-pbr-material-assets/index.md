# Game Feature

* [Declare glTF meshes and PBR materials in asset XML and author their texture sets](overview.md) - How GltfFile and PbrMaterial assets register meshes and looks without parts, the three-channel rule, and four ways to author the Diffuse, Normal and AoRoughMetal textures

# How-To

* [Bake diffuse, normal, ORM and emissive maps from Blender and dilate the atlas](declare-gltf-and-pbr-material-assets-2.md) - Unwrap a part into one atlas, bake lighting-free diffuse, normal, roughness and AO, route metalness through emission, pack ORM, bake over a key colour and dilate the background so lower mips do not bleed
* [Declare bare GltfFile and PbrMaterial assets and pair them by id in code](declare-gltf-and-pbr-material-assets-1.md) - Register a mesh and a look with GltfFile and PbrMaterial only, no Part or StaticObject, with all three texture channels, unit conventions, a shared atlas and see-through tint materials, then pair them by id at runtime
* [Emit flat-colour textures from the same script that generates the geometry](declare-gltf-and-pbr-material-assets-4.md) - Let one numpy and Pillow script write the mesh, its UVs and the swatch textures so they cannot drift, and give each material its own texture files because a shared path is bound for only the first material
* [Generate a flat-swatch palette atlas with a JSON swatch map](declare-gltf-and-pbr-material-assets-3.md) - Write Diffuse, PBR and Normal PNGs as a grid of flat 128 px swatches plus a JSON of UV centres, then box-project every face into a patch on a swatch so hard-surface parts get a full PBR set with no unwrapping or baking
