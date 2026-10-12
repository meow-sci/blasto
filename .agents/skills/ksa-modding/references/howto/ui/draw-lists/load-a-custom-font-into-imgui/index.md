# Game Feature

* [Load a custom TTF font into KSA's ImGui atlas and keep it across font rebuilds](overview.md) - How to add a bundled font to the shared ImGui atlas at the right moment, draw it at any size, avoid KSA auto-loading it, and survive FontManager.RegenerateFonts clearing the atlas

# How-To

* [Add a bundled TTF to the ImGui atlas at StarMapImmediateLoad and draw it at any size](load-a-custom-font-into-imgui-1.md) - Locate a font next to the mod DLL, call AddFontFromFileTTF once during immediate load, and use ImGui.PushFont(font, size) with balanced pops
* [Add a font family at StarMapAllModsLoaded under a renamed extension so KSA does not auto-load it](load-a-custom-font-into-imgui-3.md) - Ship TTFs as .iamttf so FontManager's recursive Content scan ignores them, load them explicitly after all mods are loaded, key them by name in FontManager.Fonts, and size each draw with PushFont
* [Add fonts lazily on the first UI frame with the game's own ImFontConfig fields](load-a-custom-font-into-imgui-2.md) - Resolve the font files from Mod.DirectoryPath, add them on the first overlay draw with the same rasterizer settings KSA.FontManager uses, fall back to the game font, and keep digits from jittering
* [Keep mod fonts alive when the game rebuilds its font atlas](load-a-custom-font-into-imgui-4.md) - Why FontManager.RegenerateFonts invalidates every cached ImFontPtr and how to guard against it by looking the font up in FontManager.Fonts each frame and re-adding it when missing, an adapted and untested design
