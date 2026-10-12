# Subdirectories

* [build-a-vulkan-pipeline-for-the-scene-target](build-a-vulkan-pipeline-for-the-scene-target/index.md) - Pipelines compatible with the scene target or free-standing for private targets
* [capture-rendered-frames](capture-rendered-frames/index.md) - Reading rendered frames and part thumbnails back without stalling the GPU
* [free-vulkan-resources-safely](free-vulkan-resources-safely/index.md) - Frames-in-flight discipline for freeing images and buffers and for game exit
* [handle-renderer-rebuilds](handle-renderer-rebuilds/index.md) - Recreating mod targets and pipelines when KSA rebuilds its renderer
* [insert-a-custom-pass-into-render-game](insert-a-custom-pass-into-render-game/index.md) - Hooking RenderGame stage boundaries to record custom Vulkan passes
* [modify-the-hdr-scene-with-a-compute-pass](modify-the-hdr-scene-with-a-compute-pass/index.md) - Compute passes shipped as Shader assets that paint into or rescale the HDR scene before bloom
* [submit-your-own-vulkan-commands](submit-your-own-vulkan-commands/index.md) - Private command buffers, fences and queue submission for uploads, offscreen renders and thumbnails
