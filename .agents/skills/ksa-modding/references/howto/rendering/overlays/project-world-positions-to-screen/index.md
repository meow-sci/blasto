# Game Feature

* [Project world and sky positions to screen pixels and point at off-screen targets](overview.md) - Turn a position in the game's frames into an ImGui screen coordinate, including behind-camera points, edge cues for off-screen targets, the viewport offset, sky directions and CPU occlusion by bodies

# How-To

* [Behind-camera NaN culling with polyline breaking](project-world-positions-to-screen-3.md) - Treat a NaN projection as a culling signal, skip points and labels behind the camera, and break sampled curves into pieces instead of drawing across the screen
* [Body-fixed or orbit-relative anchors lifted to ecliptic, then Camera.EclToScreen](project-world-positions-to-screen-1.md) - Project real world points through the viewport's own camera, adding the viewport origin, with orbit-relative and body-fixed anchors lifted to the ecliptic first
* [Clip-space edge clamp for off-screen and behind-camera targets](project-world-positions-to-screen-4.md) - Point at a target that is not on screen by testing IsPointWithinFov, then dividing the clip position by w, flipping behind the camera and scaling to the viewport rectangle
* [Edge cue from the camera basis (bearing), not the projection](project-world-positions-to-screen-5.md) - Compute the direction to an off-screen target from the camera's right and up vectors and clamp it to an inset rectangle, which stays correct when the projection is degenerate
* [Ego-space direction times a sphere radius, then Camera.EgoToScreen plus viewport origin](project-world-positions-to-screen-2.md) - Place sky directions on the same camera-centred sphere the star sprites use, project them with the main camera and add the ImGui main viewport origin
* [Per-frame ego-space sphere list and ray test per overlay point](project-world-positions-to-screen-6.md) - Hide overlay points behind planets and vessels with a CPU ray-sphere test against a list of bodies and craft rebuilt each frame from the camera's view
