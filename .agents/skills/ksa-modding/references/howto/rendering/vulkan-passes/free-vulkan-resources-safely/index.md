# Game Feature

* [Free mod-owned Vulkan resources without device loss](overview.md) - The GPU may still read an image or buffer for several frames after the mod stops referencing it, so stop recording first, free after frames in flight or WaitIdle, and never touch the destroyed device at game exit

# How-To

* [Publish new GPU resources after a fence, retire old ones after WaitIdle](free-vulkan-resources-safely-3.md) - Swap buffers and images while frames are in flight by publishing only after the upload fence and destroying the old object only after Device.WaitIdle in the pre-GUI phase
* [Retire by MaxFramesInFlight + 1 frames](free-vulkan-resources-safely-2.md) - Replace a texture the game samples through a bindless slot and destroy the old image only after a counter-based retire queue has outlived every frame in flight, with no explicit idle
* [Stop recording now, free two frames later after WaitIdle; nullable images; no device work in unload](free-vulkan-resources-safely-1.md) - Detach a Vulkan resource from every render path immediately, queue it for a two-frame countdown, call Device.WaitIdle before freeing, and never touch the device from StarMapUnload
