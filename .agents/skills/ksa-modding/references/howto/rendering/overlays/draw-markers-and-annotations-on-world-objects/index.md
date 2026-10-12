# Game Feature

* [Draw markers, brackets and measurement annotations on world objects](overview.md) - Put a bracket on a craft, a lock cue on a contact, a measurement line with a plated label on a hull, a transfer-window gizmo in the map view or a pointer to a selected body using projected points on an ImGui draw list rather than Vulkan

# How-To

* [Constant-pixel-size brackets, cues and bars projected from craft and contact positions](draw-markers-and-annotations-on-world-objects-1.md) - Draw brackets, lock cues and health bars at a constant pixel size over craft and contacts using one projection pattern with edge arrows and occlusion marks
* [Lines, curves, rings and plated labels for measurements](draw-markers-and-annotations-on-world-objects-2.md) - Draw measurement lines, 3D construction rings, great-circle arcs and plated labels as projected polylines on the background draw list, throttling expensive picks
* [Map-mode markers and gizmos for future positions](draw-markers-and-annotations-on-world-objects-3.md) - Draw destination markers on a real orbit, a phase angle and an ejection-angle gizmo in the map view by lifting orbit positions to the ecliptic and projecting them on the viewport overlay list
* [Previous-frame window rectangle plus edge-clamped projection (body target)](draw-markers-and-annotations-on-world-objects-4.md) - Draw a pointer line from a mod window to a selected body, anchored to the window rectangle captured the previous frame and clamped to the screen edge when the body is off screen
* [Same technique for a catalog star on the sky sphere](draw-markers-and-annotations-on-world-objects-5.md) - Point from a mod window to a catalog star by using its direction times 19.5 as the target, with the camera-behind w less than 0 handling
