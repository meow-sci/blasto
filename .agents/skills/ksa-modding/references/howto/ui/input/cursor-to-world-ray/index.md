# Game Feature

* [Turn the mouse cursor into a world ray and pick points on a construction plane](overview.md) - How ImGui's cursor becomes an ecliptic ray through the camera of the viewport it is over, the camera ego frame the ray lives in, and how five mods verify and use it

# How-To

* [Cursor to world ray: convert to camera framebuffer pixels before ScreenToEgoRay](cursor-to-world-ray-3.md) - Why the cursor must be offset by the viewport origin and scaled into the camera framebuffer before unprojection, with a headless-tested conversion and a pick-up-and-place tool built on it
* [Cursor to world ray: Cursor.GetEgoRay and Moller-Trumbore tests against in-world quads](cursor-to-world-ray-4.md) - Use the game's own Cursor.GetEgoRay helper and Ray.RaycastMollerTrumbore to find which camera-relative quad the mouse is over
* [Cursor to world ray: ScreenToEgoRay with viewport offset and framebuffer scale](cursor-to-world-ray-1.md) - Turn the ImGui cursor into an ecliptic ray through the visible viewport under it, ignoring clicks ImGui owns, and keep the tool working with the HUD hidden
* [Cursor to world ray: the ego frame, EgoToEcl and picking points on a construction plane](cursor-to-world-ray-5.md) - Lift an ego-space cursor ray into ecliptic space, intersect it with a camera-facing or ecliptic plane and store the hit relative to a body
* [Cursor to world ray: viewport lookup, EgoToEcl and a projected round trip](cursor-to-world-ray-2.md) - Find the viewport under the cursor, build an ecliptic ray from its camera and verify it by projecting the hit back and measuring the pixel error
