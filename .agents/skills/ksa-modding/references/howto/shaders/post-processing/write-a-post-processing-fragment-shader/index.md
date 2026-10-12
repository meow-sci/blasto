# Game Feature

* [Write a fullscreen post-processing fragment shader for KSA](overview.md) - The contract a ShaderExtensions pass shader is written against, the extra bindings it can declare, and how the mod builds each pass from a RenderTechnique and a RenderTarget

# How-To

* [Write a post-processing fragment shader: a ShaderReference subclass that builds its own descriptor set](write-a-post-processing-fragment-shader-4.md) - How ShaderEx counts descriptors per type, creates a pool for one set, lays out bindings from XML children and resolves them at ModLibrary.Bind time on the thread pool
* [Write a post-processing fragment shader: extra textures and the port from subpasses to dynamic rendering](write-a-post-processing-fragment-shader-2.md) - Bind a dirt mask and a blue-noise texture to a ShaderExtensions pass, share a GLSL include, and port the v0.4.0 subpass-style shaders of KSA_AircraftHUD to the current binding layout
* [Write a post-processing fragment shader: how a pass is built from RenderTechnique and RenderTarget](write-a-post-processing-fragment-shader-3.md) - What ShaderExtensions builds around your fragment shader - a RenderTarget used as the pipeline's pass info, a descriptor set and a bind order - so a shader that does not show can be debugged
* [Write a post-processing fragment shader: the minimal identity-for-neutral pass (MPFX contract)](write-a-post-processing-fragment-shader-1.md) - Write one ShaderExtensions pass as a standalone fragment shader that returns its input at neutral parameters, plus how to read the game's camera and lighting data from set 0
