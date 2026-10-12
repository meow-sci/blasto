# Game Feature

* [Add a menu to the game's top menu bar](overview.md) - How KSA draws its menu bar as its own Menu Bar window, the supported DrawProgramMenusHook extension point for adding menus, and the older BeginMainMenuBar approach that does not join it

# How-To

* [Add a menu with a Harmony postfix on Program.DrawProgramMenusHook](add-items-to-the-game-menu-bar-1.md) - Patch the empty DrawProgramMenusHook so BeginMenu and MenuItem draw inside KSA's real menu bar, toggle a window from the item, create the window lazily inside the frame and keep the bar visible while the menu is open
* [Add a menu with BeginMainMenuBar from StarMapBeforeGui, and why it is not KSA's bar](add-items-to-the-game-menu-bar-2.md) - The older approach of opening ImGui's main menu bar before the game's UI pass, what it actually produces in KSA (a separate bar window), when it is still acceptable and why the DrawProgramMenusHook postfix is preferred
* [Decorate the menu bar itself with a transpiler snapshot and a hook prefix](add-items-to-the-game-menu-bar-3.md) - Capture the ImGui cursor position inside Program.DrawMenuBar with an injected call, restore it in a prefix on DrawProgramMenusHook, and draw sliding colored words as BeginMenu items over filled backgrounds
