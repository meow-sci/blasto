# Game Feature

* [Draw world-space lines and gizmos with the game's own renderers](overview.md) - Use the game's gizmo renderer for debug-style depth-tested world overlays that need no Vulkan code, and preview extra predicted orbits through the game's own line instances

# How-To

* [FlightPlan.AddLineInstances per plan with dimmed colours, postfix hook and quantized cache keys](draw-world-space-lines-and-gizmos-3.md) - Preview extra predicted orbits through the game's own line instances so they look and clip like stock orbits, adding them from a TransferPlanner.OnPreRender postfix
* [Gizmos next to a moving craft using a frame-epoch anchor](draw-world-space-lines-and-gizmos-2.md) - Submit gizmos between GizmosRenderer.ResetInstances and the render, anchored with the craft's rendered Ego position of this frame so overlays do not slide
* [Submit spheres and lines from the StarMap GUI postfix between reset and render](draw-world-space-lines-and-gizmos-1.md) - Draw radar cones, track markers, trails and placement markers with GizmosRenderer.DrawSphere and DrawLine from StarMapAfterGui, anchored to the engine's render position of the platform
