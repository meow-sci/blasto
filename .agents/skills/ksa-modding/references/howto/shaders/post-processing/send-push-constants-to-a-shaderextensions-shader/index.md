# Game Feature

* [Send per-frame values and time to a shader with push constants, written at the right point in the frame](overview.md) - How a ShaderExtensions push constant becomes a Vulkan push-constant range, which clock a shader can read, how to switch an always-on pass off, and when in the frame to write

# How-To

* [Push constants for a shader: one element per pass with an enabled flag and time, written from OnFrameEditor](send-push-constants-to-a-shaderextensions-shader-1.md) - Declare a push-constant struct, give each pass its own element, and toggle an always-on post-processing pass with an enabled flag that selects an early pass-through branch
* [Push constants for a shader: per-side clock and strengths in a settings-driven mod](send-push-constants-to-a-shaderextensions-shader-2.md) - Keep a mod-owned animation clock per pass, optionally scaled by time warp, write it as a push constant every frame and write parameter buffers only when a setting changes
* [Time in a shader: the ShaderTime block at binding 1 of every ShaderEx set](send-push-constants-to-a-shaderextensions-shader-3.md) - Read frame number, frame delta, real time, simulation time and warp rate in any ShaderExtensions fragment shader without declaring anything in XML, and know which fields freeze on pause
* [Time in a stock shader: signed simulation seconds smuggled through a stock atmosphere field](send-push-constants-to-a-shaderextensions-shader-4.md) - Give a patched stock atmosphere shader a clock that freezes on pause and scales with warp by writing Universe.GetElapsedTime into an unused uniform field for the length of one call
