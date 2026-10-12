# Game Feature

* [Draw a textured quad attached to a part or the camera](overview.md) - Put an image, sticker, screen or pixel sprite into the 3D scene as a world-space quad with depth testing, tied to a part transform or a view-space billboard

# How-To

* [A single textured quad via the same hook with its own pipeline setup](draw-a-textured-quad-in-the-world-2.md) - Port the RenderMainPass quad to a single textured quad with its own buffer and texture plumbing, attached by the part's ego matrix and installed on demand
* [Opaque pixel quads with stock UnlitMesh shaders inside the offscreen scene pass](draw-a-textured-quad-in-the-world-1.md) - Draw a sprite as one small quad per opaque texel with the stock unlit shaders from a RenderMainPass postfix, with the ego matrix computed per frame and the pipeline built lazily
* [Premultiplied-alpha translucent quad after the translucency pass](draw-a-textured-quad-in-the-world-3.md) - Draw a translucent texture on a part-anchored or camera-pinned quad with a custom premultiplied-alpha shader after atmosphere and clouds, with click focus by ray picking
