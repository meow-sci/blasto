# Game Feature

* [Draw full-screen click-through overlays and choose the right ImGui draw list](overview.md) - How mods paint over the game view with ImGui - input-less windows, the background and foreground draw lists, click-through lock mode and per-viewport lists - and what each choice means for z-order, input and secondary viewports

# How-To

* [Click-through lock mode: a window that ignores the mouse until a hot zone refocuses it](draw-a-fullscreen-click-through-overlay-4.md) - Let a normal ImGui window become a pure overlay that clicks pass through while unfocused, with a small hot-zone window to take focus back and focus visuals on the foreground list
* [Full-screen overlay: a NoInputs, NoBackground window sized to the main viewport](draw-a-fullscreen-click-through-overlay-1.md) - Paint a HUD, markers or sky lines over the whole game view with an input-less transparent ImGui window, and compare the flag sets five mods use
* [Overlays on secondary and torn-off viewports: per-surface draw list and per-viewport ego transforms](draw-a-fullscreen-click-through-overlay-5.md) - Resolve per frame which ImGui draw list reaches a camera picture, which camera projects into it, and re-base camera-relative positions between viewports
* [Overlays without a window: ImGui.GetForegroundDrawList anchored to WorkPos and WorkSize](draw-a-fullscreen-click-through-overlay-3.md) - Draw an always-on-top broadcast-style overlay into the foreground draw list with an anchored panel layout and a menu-bar inset read from the game's own window
* [Overlays without a window: ImGuiHelper.GetOverlayDrawList](draw-a-fullscreen-click-through-overlay-2.md) - Draw 3D-projected overlay graphics straight into the main viewport's background draw list with the stock helper, keeping them under every window, with NaN culling and a once-per-type error guard
