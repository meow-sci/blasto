# Game Feature

* [Lease a secondary viewport for a mod camera and show it in a window](overview.md) - KSA seals its viewport registry at boot and offers four spare secondary camera windows; how mods claim, configure, track and release one and show its picture in KSA's window or their own

# How-To

* [Claim a viewport per camera with an IViewportOwner, track the lease every update and keep KSA's window](borrow-a-secondary-viewport-2.md) - Hot Pursuit gives each camera its own owner token for TryClaimSecondaryViewport, re-applies name, size, field of view, follow and visibility after every claim, polls TryGetOwned and shows the picture in KSA's resizable window
* [Clear HasUi and RenderOrbitLines through the reflected OptionFlags setter and draw the viewport texture in your own window](borrow-a-secondary-viewport-3.md) - KSArmory takes KSA's window off a leased viewport by clearing its HasUi and RenderOrbitLines flags with the protected OptionFlags setter, then draws ViewportBase.ImGuiTexture in an ImGui window of its own with controls over the picture
* [Open a secondary viewport, make it visible and re-resolve its index by reference every frame](borrow-a-secondary-viewport-1.md) - KSArmory and KSAStructures lease a spare camera window with ViewportRegistry.TryOpenSecondaryViewport, find it again by reference in GameViews each frame, disable the control when AvailableSecondaryCount is zero and hand it back with ReleaseSecondaryViewport
