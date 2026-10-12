# Game Feature

* [Bend the picture with CRT curvature and chromatic aberration](overview.md) - Remap UVs for barrel distortion and offset colour channels by a luminance-masked amount, applied to one tagged ImGui window or to a whole canopy-glass composite, plus how the host replays a tagged window offscreen

# How-To

* [Bend the picture: barrel curve within UvRect and channel offsets in an ImGuiShader](bend-the-picture-with-crt-curvature-and-chromatic-aberration-1.md) - Run a custom fragment shader over one ImGui window by tagging its draw list with a no-op marker callback and declaring an ImGuiShader asset, with barrel distortion and luminance-masked chromatic aberration inside the window's UV rectangle
* [Bend the picture: chromatic aberration and radial edge blur in a glass composite pass](bend-the-picture-with-crt-curvature-and-chromatic-aberration-2.md) - Curve the scene UV with a barrel distortion and fringe red and blue by a radius-squared amount in the final pass of a three-pass canopy-glass chain
* [Bend the picture: how the host replays a tagged ImGui window offscreen and rewrites it to one image](bend-the-picture-with-crt-curvature-and-chromatic-aberration-3.md) - Follow ShaderExtensions' replay of a tagged ImGui draw list through private-member reflection, an offscreen base pass and a shader pass, and use it to debug a window that does not show
