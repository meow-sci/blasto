# Game Feature

* [Query the terrain height field and test line of sight across terrain](overview.md) - Ask KSA how high the ground is along a direction from a body centre, choose between the Ccf and Cce entry points and between fast and accurate sampling, and build sea clamps, ground-contact tests, hover height and line of sight on that one call

# How-To

* [Answer as a body centre and surface radius, pick the body by depth, mask radar by horizon](query-terrain-height-and-line-of-sight-2.md) - Wrap the height query so a mod-simulated round tests its height every sub-step by subtraction, back-date the body's motion, and mask line of sight with non-accurate samples of the real horizon
* [Cache decorator that reuses a height sample until near the ground or moved](query-terrain-height-and-line-of-sight-3.md) - Keep trajectory previews affordable by wrapping the ground test in a decorator that re-samples only within 600 m of the cached surface or 250 m from where it last sampled
* [Ccf versus Cce entry points, sea clamp, no-claim results and a fixed-budget line-of-sight test](query-terrain-height-and-line-of-sight-1.md) - Read the engine height field with the right entry point, clamp to the sea, treat unreadable samples as no claim, decide when accurate sampling is worth it and build a sphere-then-band line-of-sight test on top
* [Height of the terrain directly below a craft for hover and touchdown](query-terrain-height-and-line-of-sight-4.md) - Rotate the craft's inertial position into the body-fixed frame and read Celestial.GetTerrainHeightFromDirCcf for a hover and touchdown height, with a finite check and the Ccf versus Cce choice explained
