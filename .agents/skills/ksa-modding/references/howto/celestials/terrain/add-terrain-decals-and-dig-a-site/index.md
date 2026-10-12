# Game Feature

* [Add terrain decals from code and flatten or dig the ground under a custom site](overview.md) - Add DecalModifierReference terrain modifiers to a body at load so the ground is drawn, collided with, queried and cluttered consistently, and compose a levelling decal with a height-map pit that meets the stock launch pad

# How-To

* [Deserialize DecalModifierReference XML and add it to both modifier lists before the renderer is built](add-terrain-decals-and-dig-a-site-1.md) - Write a Decal modifier as XML in a C# string, deserialize it with the engine's own type, run OnDataLoad with your mod, and add it to a body's ProceduralModifiers and TerrainModifiers at StarMapAllModsLoaded
* [Levelling decal plus additive pit height map, ordered after the stock pad decal](add-terrain-decals-and-dig-a-site-2.md) - Compose an absolute-level decal with an additive height-map decal so a dug site meets the stock launch pad without a step, and route cover meshes to the terrain draw bucket
