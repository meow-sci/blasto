# Game Feature

* [Import GLB models and their materials at runtime](overview.md) - How a mod turns a player-chosen GLB into a CPU-only MeshReference without registering it, converts its materials into private bindless textures, and enumerates the game's loaded asset registries for pickers

# How-To

* [Convert glTF materials on the CPU into game maps and upload them as private bindless textures](import-glb-models-at-runtime-2.md) - Bake glTF base colour, metallic-roughness, occlusion, normal and alpha into the game's diffuse, packed AO-roughness-metal, normal and opacity images, decode PNG and JPEG with bounded dimensions, and upload them through a private SimpleVkTexture and a reflected bindless handle outside ImGui
* [Enumerate the game's loaded meshes, textures and glTFs for pickers and id checks](import-glb-models-at-runtime-3.md) - Read ModLibrary.AllMeshes, AllFiles and AllGltfs by reflection once, filter to meshes with retained CPU geometry and textures with a live bindless handle, list the named meshes of each glTF from its JSON, and refresh until the registry is populated
* [Parse a GLB with a bounded reader into unregistered CPU meshes with path-plus-hash ids](import-glb-models-at-runtime-1.md) - Validate a self-contained GLB 2.0 container with hard limits, bake scene node and UV transforms into vertices, set HostPrimitives and PrimitiveMaterialIds on a MeshReference by reflection and freeze library choices to an id made from the absolute path and a SHA-256 of the contents
