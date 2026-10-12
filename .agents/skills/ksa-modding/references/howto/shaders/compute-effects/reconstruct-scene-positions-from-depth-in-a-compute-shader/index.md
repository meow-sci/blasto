# Game Feature

* [Reconstruct scene positions from the depth buffer and paint on them](overview.md) - How a compute pass rebuilds each pixel's camera-relative world point from reverse-Z depth to draw exact rings, hull holes and depth-tested streaks in the HDR scene without any geometry

# How-To

* [Draw thousands of depth-tested streaks: project on the CPU, gather per tile, add before bloom](reconstruct-scene-positions-from-depth-in-a-compute-shader-3.md) - Project line segments to screen pixels with camera-relative doubles, gather the ones reaching each 16x16 tile in shared memory, depth-test them against the scene in NDC and add their light to the HDR image before bloom
* [Paint an exact ring on the ground: depth reconstruction, one dispatch per ring](reconstruct-scene-positions-from-depth-in-a-compute-shader-1.md) - Draw circles and ellipses that lie on terrain at any distance by rebuilding each pixel's world point from the depth buffer, measuring it against a body-anchored centre, and dispatching only over the ring's screen patch
* [Paint holes that stay on a moving hull: sub-part frames and a tile bitmask gather](reconstruct-scene-positions-from-depth-in-a-compute-shader-2.md) - Store a mark in the struck sub-part's own frame, bring it to each camera through the matrix the game draws that part with, and paint up to 400 procedural holes from the depth buffer in one dispatch
