---
type: Mod Feature
title: Custom fonts in the ImGui atlas
description: Every .ttf and .otf in the mod's Fonts folder is added to the shared ImGui font atlas at immediate load, and the window pushes the chosen face at a user-set base size times a per-section scale with ImGui 1.92's sized PushFont, balancing pushes with a counter
mod: compendium
category: ui
techniques: [imgui-font, starmap-lifecycle]
hooks: [starmap StarMapImmediateLoad, starmap StarMapAfterGui]
lifecycle: [startup:immediate-load, frame:draw-ui, frame:prepare]
game_types:
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImFontAtlasExtensions
  - Brutal.ImGuiApi.ImGuiIOPtr
  - KSA.FontManager
  - KSA.Program
  - KSA.GameSettings
  - KSA.ImGuiBackendVulkanImpl
tags: [imgui, fonts, atlas, ui, starmap]
---

Compendium ships the five weights of the Oxanium typeface in `Fonts/` and renders its window text in
one of them. The font-settings arrow opens a combo to pick the face, a size slider (16 to 46 px) and an
opacity slider; headings, body text and category buttons are drawn at different multiples of the
chosen size. Any `.ttf` or `.otf` dropped into the folder appears in the combo.

## How it works

1. **Find files.** `LoadFonts(dllDir, size)` lists `*.ttf` and `*.otf` in `<mod folder>/Fonts`
   ([src/CdmLoadFonts.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadFonts.cs#L13-L24)).[^cmp-cdmloadfonts]
2. **Add to the atlas.** In an `unsafe` block it takes `ImGuiIO* io = ImGui.GetIO()` (Brutal's
   `ImGuiIOPtr` converts implicitly to the raw pointer), reads `io->Fonts`, and calls
   `atlas.AddFontFromFileTTF(new ImString(path), 30f)` for each file, keeping the returned `ImFontPtr`
   by file name. A face whose name ends in "Regular" becomes the default selection
   ([src/CdmLoadFonts.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadFonts.cs#L28-L60), [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5438),
   [current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs#L243-L251), [current/decomp/Brutal.ImGuiApi/ImGuiIO.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiIO.cs#L39),
   [current/decomp/Brutal.ImGuiApi/ImFontAtlasExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImFontAtlasExtensions.cs#L18-L27)).[^ksa-brutal-imguiapi-imgui]
   [^ksa-brutal-imguiapi-imfontatlasextensions]
3. **Push at any size.** `PushTheFont(scale)` calls the two-argument `ImGui.PushFont(font, fontSizeCurrent * scale)`
   and increments a counter; `PopTheFont()` pops only while the counter is positive, so a missing font
   degrades to ImGui's default without unbalanced pops ([src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L716-L735),
   [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5872-L5880)).[^cmp-cdmhelpers] The window uses scales from 0.75 (category
   buttons) to 2.0 (the title) ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L309-L320)).[^cmp-compendiummain]
4. **Why one atlas entry serves every size.** The game's Vulkan ImGui backend sets
   `ImGuiBackendFlags.RendererHasTextures` ([current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L650-L660)), the ImGui 1.92
   dynamic-atlas mode in which glyphs are rasterized on demand at the requested size, so changing the
   slider takes effect on the next frame without reloading anything.[^ksa-imguibackendvulkanimpl]
5. **Settings UI.** The combo is built from a `\0`-separated `ImString`, followed by the size and opacity
   sliders and the pointer settings ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L274-L301)).

## Lifecycle and timing

- **Load at `startup:immediate-load`.** `[StarMapImmediateLoad]` runs from StarMap's prefix on
  `Mod.PrepareSystems`, inside `ModLibrary.PrepareAll` in the `Program` constructor. By then the
  constructor has created the ImGui context, initialized the ImGui backend and run
  `FontManager.Initialize`, which builds the game's own fonts
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L842-L899)).[^ksa-program] The atlas therefore exists and already holds the game
  fonts; mod fonts are appended after them ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1240-L1248)).
- **Use at `frame:draw-ui`.** `PushFont`/`PopFont` happen inside the `[StarMapAfterGui]` window,
  between ImGui `NewFrame` and `Render`.
- **Font rebuilds at `frame:prepare`.** When the player changes the interface font settings,
  `GameSettings` calls `Program.ScheduleFontRebuild`, and the next `PrepareFrame` runs
  `FontManager.RegenerateFonts`, which starts with `ImGui.GetIO().Fonts.Clear()`, then ends the frame
  early ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1403-L1415), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2228-L2233),
  [current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L55-L59)).[^ksa-gamesettings] [^ksa-fontmanager]

## Key code

| Path | Role |
|---|---|
| [src/CdmLoadFonts.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadFonts.cs#L13-L72) | Folder scan and atlas registration |
| [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L716-L735) | Sized push with a balancing counter |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L274-L301) | Face combo and size slider |
| [src/Compendium.csproj](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium.csproj#L39-L43) | Copy `Fonts/` into the mod folder |

## Snippets

```csharp
// src/CdmLoadFonts.cs L28-L60
unsafe
{
    ImGuiIO* io = ImGui.GetIO();
    if (io != null)
    {
        ImFontAtlasPtr atlas = io->Fonts;
        fontNames = new string[fontFiles.Length];

        for (int i = 0; i < fontFiles.Length; i++)
        {
            string fontPath = fontFiles[i];
            string fontName = Path.GetFileNameWithoutExtension(fontPath);
            fontNames[i] = fontName;

            if (File.Exists(fontPath))
            {
                ImString fontPathStr = new ImString(fontPath);
                ImFontPtr font = atlas.AddFontFromFileTTF(fontPathStr, fontSizeCurrent);
                loadedFonts[fontName] = font;
            }
        }
        // Now writes to console a single string with all loaded font names
        Console.WriteLine($"Compendium: {fontNames.Length} Loaded fonts - " + string.Join(", ", fontNames));

        // Now if the fontnames array has 'Regular' style, set that as default selected font index
        for (int i = 0; i < fontNames.Length; i++)
        {
            if (fontNames[i].EndsWith("Regular", StringComparison.OrdinalIgnoreCase))
            {
                selectedFontIndex = i;
                break;
            }
        }
```

```csharp
// src/CdmHelpers.cs L716-L735
private static int fontPushCount = 0;

private void PushTheFont(float scale)
{
    if (fontNames.Length > 0 && selectedFontIndex < fontNames.Length && loadedFonts.TryGetValue(fontNames[selectedFontIndex], out ImFontPtr value))
    {
        ImGui.PushFont(value, fontSizeCurrent * scale);
        fontPushCount++;
    }
    // If no custom font is available, ImGui will use the default font automatically
}

private void PopTheFont()
{
    if (fontPushCount > 0)
    {
        ImGui.PopFont();
        fontPushCount--;
    }
}
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ImGui.GetIO`, `ImGuiIOPtr` to `ImGuiIO*`, `ImGuiIO.Fonts` | [current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs#L243-L251) | Reach the shared atlas |
| `ImFontAtlasPtr.AddFontFromFileTTF` (extension) | [current/decomp/Brutal.ImGuiApi/ImFontAtlasExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImFontAtlasExtensions.cs#L18-L27) | Add a face |
| `ImGui.PushFont(ImFontPtr, float)`, `PopFont` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5872-L5880) | Sized font push |
| `FontManager.Initialize`, `RegenerateFonts` | [current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L27-L59) | Game font setup and the atlas clear |
| `Program.ScheduleFontRebuild` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5273-L5276) | Trigger of the rebuild |
| `ImGuiBackendVulkanImpl` backend flags | [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L650-L660) | Dynamic atlas support |

## Pitfalls and version notes

- **A game font rebuild drops mod fonts.** `RegenerateFonts` clears the whole atlas before re-adding the
  game's fonts. Compendium never re-adds its own, so after the player changes the interface font
  settings its cached `ImFontPtr` values refer to fonts that are no longer in the atlas (and, if native
  ImGui frees font objects on `Clear` as upstream ImGui does, they dangle; not verifiable in the decomp).
  A robust mod re-adds its fonts after a rebuild instead of caching pointers for the session, for
  example from a postfix on `FontManager.RegenerateFonts`.
- **Font choice is not persisted.** Face, size and opacity reset to their defaults on every launch; only
  window visibility and the pointer options go to `settings.ini`.
- **Exceptions skip pops.** The draw method's catch block ends the child and window but does not pop
  fonts pushed before the throw, so the push counter and ImGui's font stack get out of step.
- **Fonts load only if patching succeeded.** `LoadFonts` comes after `Patcher.Patch` in
  `OnImmediateLoad`; a patch failure skips it (see
  [the navball feature](navball-target-marker-and-tgt-frame.md)).

## See also

- [Celestial compendium window](celestial-info-window.md)
- [Custom TTF font in the ImGui atlas (KSA_AircraftHUD)](../../ksa-aircrafthud/features/custom-hud-font.md)
- [Bundled Nerd Fonts loaded into the ImGui atlas (purrtty)](../../purrtty/features/nerd-font-loading-into-imgui-atlas.md)
- [Add a bundled TTF to the ImGui atlas at StarMapImmediateLoad and draw it at any size](../../../howto/ui/draw-lists/load-a-custom-font-into-imgui/load-a-custom-font-into-imgui-1.md) - how-to article demonstrating this feature
- [Keep mod fonts alive when the game rebuilds its font atlas](../../../howto/ui/draw-lists/load-a-custom-font-into-imgui/load-a-custom-font-into-imgui-4.md) - how-to article demonstrating this feature

[^cmp-cdmloadfonts]: Compendium: src/CdmLoadFonts.cs
[^ksa-brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-brutal-imguiapi-imfontatlasextensions]: Brutal.ImGuiApi.ImFontAtlasExtensions (decompiled, build 2026.10.10.5554)
[^cmp-cdmhelpers]: Compendium: src/CdmHelpers.cs
[^cmp-compendiummain]: Compendium: src/CompendiumMain.cs
[^ksa-imguibackendvulkanimpl]: KSA.ImGuiBackendVulkanImpl (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-fontmanager]: KSA.FontManager (decompiled, build 2026.10.10.5554)
