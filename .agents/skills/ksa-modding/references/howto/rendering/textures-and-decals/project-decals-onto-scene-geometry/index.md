# Game Feature

* [Project decals onto vessels, terrain and clutter](overview.md) - Draw an image, a sticker or a damage mark onto arbitrary surfaces by reconstructing the scene position from the depth buffer in the post-resolve window or a compute pass, or use the terrain's own decal modifiers

# How-To

* [Engine terrain DecalModifier deserialized from XML in code](project-decals-onto-scene-geometry-4.md) - Dig pits into a planet's terrain by deserializing Decal modifier XML into DecalModifierReference objects and adding them to the terrain while the game is still loading, so rendering, colliders and height queries all follow
* [Procedural bullet holes painted by compute from depth, stored in the struck subpart's frame](project-decals-onto-scene-geometry-3.md) - Paint procedural damage marks with a compute pass from the depth buffer along the shell's path, anchored in the struck sub-part's frame and placed with the engine's own draw matrix
* [Sticker pass after resolve with runtime GLSL including the game library](project-decals-onto-scene-geometry-2.md) - Port the grid-pass window to a sticker renderer whose patch is installed only while stickers exist, with anchors from parts or latitude and longitude
* [Unit-cube decal volumes with reverse-Z depth reconstruction](project-decals-onto-scene-geometry-1.md) - Draw each decal as a unit cube in the post-resolve window, reconstruct the scene position from the reverse-Z depth in the fragment shader and sample the texture through the bindless table
