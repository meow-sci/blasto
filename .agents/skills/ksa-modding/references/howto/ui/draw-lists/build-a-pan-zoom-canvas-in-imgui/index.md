# Game Feature

* [Build pannable, zoomable and 3D canvases inside an ImGui window](overview.md) - A clipped canvas with a full-size InvisibleButton for pan, zoom about the cursor, hover and click, and a 3D preview canvas with wireframe gizmos, ray picking and undo

# How-To

* [Build a 3D orbit canvas with CPU-projected gizmos, ray picking and undo](build-a-pan-zoom-canvas-in-imgui-2.md) - Show an offscreen render in an ImGui window, project colliders and gizmo handles with the same view-projection matrix, pick with a ray, drag handles, and keep bounded JSON-clone undo history
* [Pan and zoom a 2D canvas: InvisibleButton, wheel key ownership, clip rect and priority label culling](build-a-pan-zoom-canvas-in-imgui-1.md) - Build a clipped canvas that pans by dragging, zooms about the cursor, hit-tests nodes by screen distance, keeps glyphs and text a fixed screen size, and culls overlapping labels greedily by priority
