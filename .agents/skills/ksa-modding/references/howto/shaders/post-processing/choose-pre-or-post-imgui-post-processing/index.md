# Game Feature

* [Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing](overview.md) - What a PostProcessingShader and a GlobalPostShader each see, where they run relative to the tonemap composite, CMAA2 and the UI, and how to pick the stage for an effect

# How-To

* [Choose the stage: a post-ImGui chain on the presented image via swapchain copies](choose-pre-or-post-imgui-post-processing-2.md) - Run an effect after the UI so it covers the whole frame in display colour, and understand the copies into and out of the swapchain image that make it work
* [Choose the stage: a pre-ImGui chain on the RGBA16F scene, copied back before the tonemap](choose-pre-or-post-imgui-post-processing-1.md) - Put an effect before the UI so it works on the linear HDR scene, and know why it vanishes with CMAA2 and what layout the offscreen image is left in
