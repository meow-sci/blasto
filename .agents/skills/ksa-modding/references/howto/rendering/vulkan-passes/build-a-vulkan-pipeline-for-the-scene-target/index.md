# Game Feature

* [Build a Vulkan pipeline compatible with KSA's scene render target](overview.md) - A pipeline recorded inside KSA's scene pass must match its colour and depth formats, sample count and dynamic-rendering setup, and offscreen work of the mod's own needs a standalone render-pass-less pipeline

# How-To

* [Hand-built dynamic-rendering pipeline reusing UnlitMeshVert with premultiplied alpha](build-a-vulkan-pipeline-for-the-scene-target-2.md) - Build the pipeline by hand with a chained VkPipelineRenderingCreateInfo, the stock unlit vertex shader, a runtime-compiled premultiplied-alpha fragment shader and depth test against the scene
* [Private colour and depth targets with a standalone dynamic-rendering pipeline](build-a-vulkan-pipeline-for-the-scene-target-3.md) - Build a render-pass-less pipeline for images the mod owns instead of the scene target, compiling GLSL from embedded resources at runtime
* [Program.OffscreenTarget.SetupGraphicsPipeline, created lazily](build-a-vulkan-pipeline-for-the-scene-target-1.md) - Build a graphics pipeline that matches the scene target's dynamic-rendering formats and sample count by letting RenderTarget.SetupGraphicsPipeline stamp them, created on first use and rebuilt after renderer rebuilds
