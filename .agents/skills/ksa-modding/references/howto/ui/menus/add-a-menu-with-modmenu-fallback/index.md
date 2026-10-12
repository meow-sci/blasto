# Game Feature

* [Add a menu that uses ModMenu when present and the game menu bar otherwise](overview.md) - How to show one mod menu through ModMenu or through KSA's own menu bar or a standalone window depending on what is installed, without showing it twice

# How-To

* [Name an active-flag property in ModMenuEntry and skip your own menu when ModMenu sets it](add-a-menu-with-modmenu-fallback-2.md) - Pass the name of a static bool property as the second ModMenuEntry argument, check it in a postfix on Program.DrawProgramMenusHook, and keep an auto-hidden menu bar visible with the IGameViewportLifecycle cast
* [Render one settings tree as a ModMenu menu or as a hotkey-toggled window](add-a-menu-with-modmenu-fallback-3.md) - Draw the same settings content either under ModMenu's entry with BeginMenu or in a standalone window with CollapsingHeader by switching one flag, so both can coexist without a patch on the game's menu bar
* [Show the same menu through ModMenu or a Harmony postfix, chosen once with ModLibrary.Find](add-a-menu-with-modmenu-fallback-1.md) - Share one menu-content function between a ModMenuEntry method and a postfix on Program.DrawProgramMenusHook that draws only when ModMenu is not loaded, keeping the menu bar visible and the draw path allocation-light
