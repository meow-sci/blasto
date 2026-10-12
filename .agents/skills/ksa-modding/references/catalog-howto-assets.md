# Assets

The data side of a KSA mod: the asset XML vocabulary and how ids and paths resolve, custom XML element types and shared data files, glTF models and PBR textures (authoring, export, runtime import), patching and redirecting the game's own XML (KittenExtensions XPath patches, loader redirects, Core system XML), and offline and in-game validation of assets and patches.

## XML patching

Changing the game's own XML without copying it: KittenExtensions patches, XPath recipes and loader redirects to patched copies.

### [Patch Core or another mod's XML data without copying files](howto/assets/xml-patching/patch-game-xml-data/overview.md)

How a mod changes stock or other mods' XML through KittenExtensions patch files listed under a patches key in mod.toml, and how to depend on KittenExtensions optionally

- [Patch Core or another mod's XML with a patches array in mod.toml](howto/assets/xml-patching/patch-game-xml-data/patch-game-xml-data-1.md) — intermediate
- [Ship XML patches that apply only when KittenExtensions is installed](howto/assets/xml-patching/patch-game-xml-data/patch-game-xml-data-2.md) — intermediate

### [Redirect the game to patched copies of its XML and coordinate edits between mods](howto/assets/xml-patching/redirect-game-xml-loading/overview.md)

Two ways to substitute the XML the game reads without editing the install, how to edit the copies safely, and how two mods share one patched copy

- [Swap Core's XML entries for patched copies with prefixes on the Mod loaders](howto/assets/xml-patching/redirect-game-xml-loading/redirect-game-xml-loading-1.md) — advanced
- [Transpile the Mod loaders so XmlLoader.Load calls read from an in-memory document](howto/assets/xml-patching/redirect-game-xml-loading/redirect-game-xml-loading-2.md) — advanced
- [Edit shadow copies with anchor-verified text patches and generated XML between markers](howto/assets/xml-patching/redirect-game-xml-loading/redirect-game-xml-loading-3.md) — advanced
- [Let two mods edit the same Core file with a shared shadow folder and a named mutex](howto/assets/xml-patching/redirect-game-xml-loading/redirect-game-xml-loading-4.md) — advanced

### [Write XPath patch operations for game XML](howto/assets/xml-patching/xpath-xml-patch-recipes/overview.md)

The ModuleManager-style patch operations KittenExtensions runs over XPath 1.0 paths, with worked recipes for appending a gauge button, adding missing elements to bodies and rewriting values with conditionals

- [Append a button to a stock gauge canvas with Copy and inline XML](howto/assets/xml-patching/xpath-xml-patch-recipes/xpath-xml-patch-recipes-1.md) — intermediate
- [Add missing mass and sphere-of-influence elements to a kind of body](howto/assets/xml-patching/xpath-xml-patch-recipes/xpath-xml-patch-recipes-2.md) — intermediate
- [Rewrite many nodes with With, If and SetVar, and combine trees with Merge](howto/assets/xml-patching/xpath-xml-patch-recipes/xpath-xml-patch-recipes-3.md) — advanced

## Asset XML

The asset XML vocabulary and its plumbing: element reference, load order, ids and paths, custom element types and data files shared between mods.

### [Know which asset XML elements the game accepts and what they load](howto/assets/asset-xml/asset-xml-element-reference/overview.md)

How KSA turns the XML files a mod lists under assets into registered templates and file references, with the AssetBundle element table, the registration rules and the attributes that matter

- [Read the AssetBundle element table against three real asset files](howto/assets/asset-xml/asset-xml-element-reference/asset-xml-element-reference.md) — beginner

### [Understand when and in which order KSA loads mod XML](howto/assets/asset-xml/mod-xml-load-order/overview.md)

The mod.toml keys feed separate Mod loader methods that run in a fixed order inside the Program constructor, which decides where patches, redirects, StarMap hooks and runtime registrations can see the data

- [Find out when KSA reads each mod.toml key and where hooks can see the data](howto/assets/asset-xml/mod-xml-load-order/mod-xml-load-order.md) — intermediate

### [Register a custom XML asset type or inject new child elements into game XML](howto/assets/asset-xml/register-custom-xml-asset-type/overview.md)

How KittenExtensions teaches the game's XmlSerializers new elements from attributes in a mod assembly, and three ways mods use it to ship shader assets and data profiles

- [Add an XML element with KxAsset and inject child elements with KxAssetInject](howto/assets/asset-xml/register-custom-xml-asset-type/register-custom-xml-asset-type-1.md) — advanced
- [Ship shader asset elements through KxAsset and a copied injector](howto/assets/asset-xml/register-custom-xml-asset-type/register-custom-xml-asset-type-2.md) — advanced
- [Ship a data-only profile type that loads from ordinary asset XML](howto/assets/asset-xml/register-custom-xml-asset-type/register-custom-xml-asset-type-3.md) — intermediate

### [Resolve asset ids and file paths correctly across mods](howto/assets/asset-xml/resolve-asset-ids-and-mod-relative-paths/overview.md)

How the global asset registry, first-wins Id registration and Mod.GetPath path resolution behave, with a layout for your own assets and a way to reach another mod's or Core's files

- [Lay out mod-folder-relative paths and prefixed global asset ids](howto/assets/asset-xml/resolve-asset-ids-and-mod-relative-paths/resolve-asset-ids-and-mod-relative-paths-1.md) — beginner
- [Reach another mod's or Core's files with absolute paths and Id-only references](howto/assets/asset-xml/resolve-asset-ids-and-mod-relative-paths/resolve-asset-ids-and-mod-relative-paths-2.md) — advanced

### [Ship data files with a mod and let other mods contribute more](howto/assets/asset-xml/ship-and-share-data-files-between-mods/overview.md)

How a code mod packages and finds its own data files, scans other mods for contributions, reads a convention folder inside every installed mod, and writes a new mod folder from the game

- [Ship data beside the DLL with an explicit copy list and resolve the mod root from the assembly](howto/assets/asset-xml/ship-and-share-data-files-between-mods/ship-and-share-data-files-between-mods-1.md) — beginner
- [Let other mods add data with a marker key in JSON files scanned across mod folders](howto/assets/asset-xml/ship-and-share-data-files-between-mods/ship-and-share-data-files-between-mods-2.md) — intermediate
- [Read a convention folder inside every installed mod with a strict reader](howto/assets/asset-xml/ship-and-share-data-files-between-mods/ship-and-share-data-files-between-mods-3.md) — advanced
- [Write a new content mod folder and manifest entry from inside the game](howto/assets/asset-xml/ship-and-share-data-files-between-mods/ship-and-share-data-files-between-mods-4.md) — advanced

## Models and textures

Models and textures: glTF and PBR declarations, part models, Blender export, runtime GLB import and compositing over stock textures.

### [Add a part's art with its own GLB model and PBR textures](howto/assets/models-and-textures/add-a-part-model-with-glb-and-pbr-textures/overview.md)

How a Blender export becomes a placeable part through MeshAtlas, PbrMaterial, SubPart and Part elements, with two ways to organise atlases, materials and preview twins

- [Build a part from one atlas per vehicle with subparts for moving bodies, glass and hidden doors](howto/assets/models-and-textures/add-a-part-model-with-glb-and-pbr-textures/add-a-part-model-with-glb-and-pbr-textures-1.md) — intermediate
- [Share one palette atlas across parts, add _VM preview twins and keep subpart ids apart](howto/assets/models-and-textures/add-a-part-model-with-glb-and-pbr-textures/add-a-part-model-with-glb-and-pbr-textures-2.md) — intermediate

### [Composite an image over a stock texture](howto/assets/models-and-textures/composite-png-over-stock-texture/overview.md)

Two ways to put a user image on top of a game texture, a CPU blend at run time over a re-transcoded stock source and an offline script that writes a modified copy to ship

- [Blend a PNG decal over a transcoded stock texture on the CPU and register it with GpuTextureSystem](howto/assets/models-and-textures/composite-png-over-stock-texture/composite-png-over-stock-texture-1.md) — advanced
- [Write a modified copy of a stock texture with an offline script](howto/assets/models-and-textures/composite-png-over-stock-texture/composite-png-over-stock-texture-2.md) — intermediate

### [Declare glTF meshes and PBR materials in asset XML and author their texture sets](howto/assets/models-and-textures/declare-gltf-and-pbr-material-assets/overview.md)

How GltfFile and PbrMaterial assets register meshes and looks without parts, the three-channel rule, and four ways to author the Diffuse, Normal and AoRoughMetal textures

- [Declare bare GltfFile and PbrMaterial assets and pair them by id in code](howto/assets/models-and-textures/declare-gltf-and-pbr-material-assets/declare-gltf-and-pbr-material-assets-1.md) — intermediate
- [Bake diffuse, normal, ORM and emissive maps from Blender and dilate the atlas](howto/assets/models-and-textures/declare-gltf-and-pbr-material-assets/declare-gltf-and-pbr-material-assets-2.md) — advanced
- [Generate a flat-swatch palette atlas with a JSON swatch map](howto/assets/models-and-textures/declare-gltf-and-pbr-material-assets/declare-gltf-and-pbr-material-assets-3.md) — intermediate
- [Emit flat-colour textures from the same script that generates the geometry](howto/assets/models-and-textures/declare-gltf-and-pbr-material-assets/declare-gltf-and-pbr-material-assets-4.md) — advanced

### [Export or generate a GLB that KSA loads correctly](howto/assets/models-and-textures/export-glb-from-blender-for-ksa/overview.md)

The glTF export contract KSA reads (Y-up off, part space, transforms applied, node name equal to mesh name, a _VM twin, pivot-centred moving bodies) and four ways mods meet it

- [Export a part-space GLB with pivot-centred subparts and preview it headless](howto/assets/models-and-textures/export-glb-from-blender-for-ksa/export-glb-from-blender-for-ksa-1.md) — intermediate
- [Verify the export contract against Core and keep a human sign-off before unwrapping](howto/assets/models-and-textures/export-glb-from-blender-for-ksa/export-glb-from-blender-for-ksa-2.md) — intermediate
- [Reframe hand-modelled assets and generate hard-surface parts with headless Blender](howto/assets/models-and-textures/export-glb-from-blender-for-ksa/export-glb-from-blender-for-ksa-3.md) — advanced
- [Write the GLB, textures, collider XML and C# table from one Python script (no Blender)](howto/assets/models-and-textures/export-glb-from-blender-for-ksa/export-glb-from-blender-for-ksa-4.md) — advanced

### [Import GLB models and their materials at runtime](howto/assets/models-and-textures/import-glb-models-at-runtime/overview.md)

How a mod turns a player-chosen GLB into a CPU-only MeshReference without registering it, converts its materials into private bindless textures, and enumerates the game's loaded asset registries for pickers

- [Parse a GLB with a bounded reader into unregistered CPU meshes with path-plus-hash ids](howto/assets/models-and-textures/import-glb-models-at-runtime/import-glb-models-at-runtime-1.md) — advanced
- [Convert glTF materials on the CPU into game maps and upload them as private bindless textures](howto/assets/models-and-textures/import-glb-models-at-runtime/import-glb-models-at-runtime-2.md) — advanced
- [Enumerate the game's loaded meshes, textures and glTFs for pickers and id checks](howto/assets/models-and-textures/import-glb-models-at-runtime/import-glb-models-at-runtime-3.md) — intermediate

## Validation

Catching silent asset failures before and while the game runs: mesh checks, XML reference validation and XML patch debugging.

### [Debug XML patches with the step debugger and the session log](howto/assets/validation/debug-xml-patches/overview.md)

How to find out why a KittenExtensions XML patch did nothing, using the pre-game ImGui stepper and the per-session patch log

- [Debug a failing XML patch with the KittenExtensions step debugger](howto/assets/validation/debug-xml-patches/debug-xml-patches-1.md) — intermediate
- [Audit what patches changed from KittenExtensions.log](howto/assets/validation/debug-xml-patches/debug-xml-patches-2.md) — beginner

### [Check GLB meshes offline: z-fighting, degenerate UVs, names and bounds](howto/assets/validation/find-z-fighting-and-degenerate-uvs-in-glb/overview.md)

How mods find coplanar faces, zero-area UVs, unpaired node and mesh names and unmaterialed primitives in a GLB with no Blender and no game, in Python and in C#

- [Find coplanar faces and zero-area UVs with checkmesh.py and read bounds with meshinfo.py](howto/assets/validation/find-z-fighting-and-degenerate-uvs-in-glb/find-z-fighting-and-degenerate-uvs-in-glb-1.md) — intermediate
- [Compare exports by surface fingerprint and sweep articulated assemblies for collisions](howto/assets/validation/find-z-fighting-and-degenerate-uvs-in-glb/find-z-fighting-and-degenerate-uvs-in-glb-2.md) — advanced
- [Parse the GLB JSON chunk in C# to validate mesh references](howto/assets/validation/find-z-fighting-and-degenerate-uvs-in-glb/find-z-fighting-and-degenerate-uvs-in-glb-3.md) — intermediate

### [Validate asset XML ids and paths before launching the game (offline)](howto/assets/validation/validate-asset-xml-references/overview.md)

Why bad asset XML fails silently in KSA and three levels of validator, from a well-formedness and reference script to extended generated-geometry checks to a game-serializer parse for runtime loaders

- [Check asset XML ids and paths offline with check-xml.sh and validate-parts.py](howto/assets/validation/validate-asset-xml-references/validate-asset-xml-references-1.md) — intermediate
- [Extend the validator for generated geometry, ids named in C# and shared planes](howto/assets/validation/validate-asset-xml-references/validate-asset-xml-references-2.md) — advanced
- [Parse an AssetBundle with the game serializer, without OnDataLoad, and apply pure rules](howto/assets/validation/validate-asset-xml-references/validate-asset-xml-references-3.md) — advanced
