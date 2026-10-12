# Game Feature

* [Record and submit your own Vulkan work: offscreen renders and one-shot uploads](overview.md) - One-shot uploads, offscreen renders and ImGui-to-texture need command buffers the game does not own, so pick the pool, fence and queue lock that neither stall nor auto-submit half-recorded work

# How-To

* [Mod command pool, fence ring and locked queue submit](submit-your-own-vulkan-commands-2.md) - Re-record a small offscreen render every changed frame from a mod-owned command pool with a two-slot fence ring, submitted under the engine's queue lock
* [Private images, runtime GLSL and a fenced submit from StarMapBeforeGui](submit-your-own-vulkan-commands-4.md) - Render a mesh into private colour and depth images with a free-standing pipeline and show the finished image in an ImGui window as an editor preview
* [Private one-shot command buffer and fence, with StagingPool used only for memory](submit-your-own-vulkan-commands-1.md) - Record buffer and texture uploads into a command buffer the mod owns, submit it with a fence from the main thread, and publish the result only after the fence
* [Private transient pool for readback and thumbnails](submit-your-own-vulkan-commands-3.md) - Render the game's part-thumbnail pipeline one part at a time from a private command pool, wait on a fence and read the image back, all before OnPreRender
