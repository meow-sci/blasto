# Game Feature

* [Export or generate a GLB that KSA loads correctly](overview.md) - The glTF export contract KSA reads (Y-up off, part space, transforms applied, node name equal to mesh name, a _VM twin, pivot-centred moving bodies) and four ways mods meet it

# How-To

* [Export a part-space GLB with pivot-centred subparts and preview it headless](export-glb-from-blender-for-ksa-1.md) - Follow the export contract (Y-up off, part space, transforms applied, node name equal to mesh name, a _VM twin, each moving body recentred on its pivot) and render the exported file in headless Blender before declaring it
* [Reframe hand-modelled assets and generate hard-surface parts with headless Blender](export-glb-from-blender-for-ksa-3.md) - Use a script to bake node transforms, change frame, recentre pivots and add _VM twins on an authored GLB, and another to generate parts from boxes, cylinders and fins with swatch UVs, seeded skin and a muzzles.json report
* [Verify the export contract against Core and keep a human sign-off before unwrapping](export-glb-from-blender-for-ksa-2.md) - Author in a loop that stops before UV unwrapping for a human yes, state the contract table with Core's evidence, choose props in centimetres and parts in metres, purge orphan meshes and never export a primitive without a material
* [Write the GLB, textures, collider XML and C# table from one Python script (no Blender)](export-glb-from-blender-for-ksa-4.md) - Generate a mesh atlas with a hand-rolled glTF binary writer in numpy, swatch UVs, box colliders approximating a slope as steps, XML spliced between markers and a generated C# table so mesh, colliders and code come from one layout
