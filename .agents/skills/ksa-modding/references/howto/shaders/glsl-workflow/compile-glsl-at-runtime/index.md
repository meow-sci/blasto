# Game Feature

* [Compile and write GLSL at runtime for a mod's own pipeline](overview.md) - How a mod compiles an embedded GLSL string with the game's shaderc wrapper, what debug path and includes it needs, and three mods that build their own quad, decal or preview pipeline this way

# How-To

* [Compile GLSL at runtime: a premultiplied-alpha fragment shader paired with the stock unlit vertex shader](compile-glsl-at-runtime-1.md) - Compile only the fragment stage from an embedded string and reuse the game's UnlitMesh vertex shader for an in-world quad that blends over the scene
* [Compile GLSL at runtime: a sticker fragment shader compiled with FromString and the game's includes](compile-glsl-at-runtime-2.md) - Compile both stages of a depth-aware decal pass from C# raw strings, point the NUL-terminated debug path next to a stock shader so the game's headers resolve, and reconstruct positions from reverse-Z depth
* [Compile GLSL at runtime: derivative normal mapping in an embedded-resource preview shader](compile-glsl-at-runtime-3.md) - Ship GLSL as embedded resources, compile them with FromString for a private offscreen pipeline, and normal-map meshes that have no tangents using screen-space derivatives
