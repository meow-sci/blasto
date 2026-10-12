# Game Feature

* [Build a mod window on the game's ImGuiWindow base class, with stable ids and saved placement](overview.md) - How KSA.ImGuiWindow gives mod windows stock chrome, menu integration and static registration, what it does not persist, and when a plain ImGui.Begin window is the better choice

# How-To

* [Chrome-less plain-ImGui windows: alpha-0 title bar, InvisibleButton drag guard and resize snapped to a character grid](subclass-the-stock-imguiwindow-5.md) - Take over a window's chrome entirely with a plain ImGui.Begin - hide it without changing layout, stop body drags moving the window, and snap resizing to whole cells
* [Stable window ids: Label###stable-id for renameable windows and a fixed id for plain windows](subclass-the-stock-imguiwindow-4.md) - Keep an ImGui window's identity (and the position and size ImGui remembers for it) constant while its visible title changes, using the id part after the title in the Begin name
* [Stock window base class: a lazily created ImGuiWindow drawn from StarMapAfterGui](subclass-the-stock-imguiwindow-1.md) - Derive from KSA.ImGuiWindow, create it lazily inside an ImGui frame from a menu hook, draw it every frame from the after-GUI hook and balance the ImGui stacks if drawing throws
* [Stock window base class: guarded DrawContent with title-bar close detection](subclass-the-stock-imguiwindow-2.md) - Detect the title-bar close button on an ImGuiWindow subclass, share one teardown with the menu close path, guard DrawContent and unload cleanly on StarMapUnload
* [Stock window base class: IStaticWindow auto-registration with a mod-owned layout persisted to config](subclass-the-stock-imguiwindow-3.md) - Let the game draw your ImGuiWindow subclass by implementing IStaticWindow, and persist position and size yourself because the base class uses NoSavedSettings
