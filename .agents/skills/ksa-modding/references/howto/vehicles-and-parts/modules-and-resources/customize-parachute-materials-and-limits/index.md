# Game Feature

* [Customising parachutes, canopy materials and diameter limits](overview.md) - How a KSA parachute stores tuning, draws its canopy and clamps its diameter, and two patches that swap the canopy material and lift the editor's diameter slider bounds

# How-To

* [Lift the parachute diameter limits in the editor, reversibly](customize-parachute-materials-and-limits-2.md) - Overwrite each chute's per-instance min and max diameter in prefixes on the slider draw and on SetDiameter, remember the authored pair, restore it on demand, and plan for the re-clamp on load
* [Swap parachute canopy materials by editing MaterialIndices in a ChuteRenderable.Draw prefix](customize-parachute-materials-and-limits-1.md) - Create a runtime GPU MaterialData entry and write its handle into slot 0 of each canopy renderable's material index array before the game submits the draw
