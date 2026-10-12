# Game Feature

* [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](overview.md) - How a mod defines a C# struct for a GLSL std140 uniform block, registers it as an XML element without referencing ShaderExtensions, and writes it each frame

# How-To

* [Feed a shader with uniform buffers: Pack=1 structs with manual std140 padding, one block per effect](feed-uniform-buffers-to-a-shaderextensions-shader-1.md) - Define an effect's parameters as a Pack=1 C# struct, register it by attribute name with no ShaderExtensions reference, and write it from StarMapAfterGui
* [Feed a shader with uniform buffers: share one buffer across passes (Size owns it, Id references it)](feed-uniform-buffers-to-a-shaderextensions-shader-2.md) - Let a pre-ImGui and a post-ImGui pass read one persistently mapped uniform buffer so the mod writes once, and see how the host allocates and maps that buffer
* [Feed a shader with uniform buffers: Span, pointer and BufferEx lookups beside push constants in a HUD mod](feed-uniform-buffers-to-a-shaderextensions-shader-3.md) - Pick the right lookup form for a ShaderExtensions uniform buffer and integrate by attribute name with no use of any ShaderExtensions type, as KSA_AircraftHUD does
