# Game Feature

* [Draw lines and labels on the sky](overview.md) - Draw constellation lines, boundary polylines, star names and labelled RA/Dec grids on KSA's sky from catalog data in a click-through ImGui overlay that tracks the camera, with J2000 conversion to the ecliptic frame the game draws in

# How-To

* [ImGui window draw list from StarMapAfterGui with ego-space sky directions](draw-lines-on-the-sky-1.md) - Draw constellation lines, boundaries and names from catalog RA/Dec by converting to the game's ecliptic frame, placing the directions around the camera and stroking straight screen segments into a full-screen window draw list
* [One-degree arcs, clip-space frustum cull and CalcTextSize label rectangles](draw-lines-on-the-sky-2.md) - Draw a labelled RA/Dec coordinate grid from lazily built one-degree arcs, a 15 percent inflated clip-space cull and per-frame label rectangle rejection
