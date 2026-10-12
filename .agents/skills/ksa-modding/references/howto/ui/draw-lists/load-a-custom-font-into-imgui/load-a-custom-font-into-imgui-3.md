---
type: How-To
title: Add a font family at StarMapAllModsLoaded under a renamed extension so KSA does not auto-load it
description: Ship TTFs as .iamttf so FontManager's recursive Content scan ignores them, load them explicitly after all mods are loaded, key them by name in FontManager.Fonts, and size each draw with PushFont
category: ui
group: draw-lists
topic: load-a-custom-font-into-imgui
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/nerd-font-loading-into-imgui-atlas]
techniques: [imgui-font, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImFontAtlasPtr
  - Brutal.ImGuiApi.ImGui
  - KSA.FontManager
  - KSA.ImGuiBackendVulkanImpl
  - KSA.Program
tags: [font, nerd-font, iamttf, family, all-mods-loaded, fontmanager]
---
purrTTY ships 15 Nerd Font files (Hack, JetBrains Mono and Space Mono in four styles; Pro Font, Proggy Clean and Shure Tech Mono regular only) so prompts, powerline glyphs and TUI icons render in-game. They are loaded into the same atlas the game uses and selected per window by family name; each draw call passes its own pixel size, so one loaded font serves every size from 4 to 72 px. The trick that makes this clean: the files are renamed from `.ttf` to `.iamttf`, so KSA's own font scanner never picks them up, and purrTTY loads them explicitly. Demonstrated by [Bundled Nerd Fonts loaded into the ImGui atlas](../../../../mods/purrtty/features/nerd-font-loading-into-imgui-atlas.md).

## Background: how the game does it

At startup `FontManager.Initialize` collects every `*.ttf` under the game's `Content` folder recursively, and `RegenerateFonts` adds each one to the atlas and to the `FontManager.Fonts` dictionary (the game's font lookup by name):

```csharp
// current/decomp/KSA/FontManager.cs L27-L32
public static void Initialize(Device device)
{
	_device = device;
	_fontFiles = Directory.GetFiles(Path.Combine("Content"), "*.ttf", SearchOption.AllDirectories);
	RegenerateFonts();
}
```

Mods can live under `Content/` as well as in the Documents mods folder, so a mod shipping `.ttf` files there would have them auto-loaded and listed. Renaming to `.iamttf` opts out; the purrTTY notes document the convention ([docs/BRUTAL_IMGUI_NOTES.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/docs/BRUTAL_IMGUI_NOTES.md#L7)). `AddFontFromFileTTF` does not care about the extension ([current/decomp/Brutal.ImGuiApi/ImFontAtlasExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImFontAtlasExtensions.cs#L18-L27)[^brutal-imguiapi-imfontatlasextensions]).

## Lifecycle and timing

| Step | Hook | Why there |
|---|---|---|
| `LoadFonts` | `startup:all-mods-loaded` via `[StarMapAllModsLoaded]`, then `InitializeTerminal` | The ImGui context and atlas already exist: `Program` calls `ImGui.CreateContext()` and `FontManager.Initialize` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L842-L853)[^ksa-program]) before `ModLibrary.PrepareAll` and `LoadAll` |
| Resolve, measure, draw | `frame:draw-ui` via `[StarMapAfterGui]` | `PushFont` and `CalcTextSize` need an active ImGui frame |

Loading is idempotent, main-thread only, and never repeated. A game font-settings change at `frame:prepare` clears the atlas; purrTTY has no hook to reload (see [approach 4](load-a-custom-font-into-imgui-4.md)). See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Rename and deploy.** Rename each `.ttf` to `.iamttf` and copy the folder beside the DLL on build:

```xml
<!-- purrTTY.GameMod/purrTTY.GameMod.csproj L65-L70 -->
<!-- Copy Fonts folder to the build dir-->
<ItemGroup>
    <FontFiles Include="$(TargetDir)TerminalFonts\**\*"/>
</ItemGroup>
<MakeDir Directories="$(DistDir)TerminalFonts"/>
<Copy SourceFiles="@(FontFiles)" DestinationFolder="$(DistDir)TerminalFonts\%(RecursiveDir)"/>
```

2. **Load from the post-load hook.** `[StarMapAllModsLoaded]` fires after `ModLibrary.LoadAll`; purrTTY initializes the terminal there and loads fonts first:

```csharp
// purrTTY.GameMod/TerminalMod.cs L263-L281
private void InitializeTerminal()
{
    if (_isInitialized || _isDisposed)
    {
        return;
    }

    ModLog.Log.Debug("purrTTY GameMod: Initializing terminal...");

    try
    {
        SetProductionConfigPath();
        LoadToggleHotkeyFromConfiguration();

        // Load fonts first
        PurrTTYFontManager.LoadFonts();

        // Game-console shells are launchable from the menus at any time.
        GhosttySessionManagerFactory.EnsureGameShellsDiscovered();
```

3. **Scan, add at a generous base size, key by name.** Each file is added once at 32 px; the returned `ImFontPtr` is stored by file name and also inserted into the game's `FontManager.Fonts` so other code can look it up by name. The flag makes the load idempotent:

```csharp
// purrTTY.Display/Rendering/PurrTTYFontManager.cs L34-L47
public static void LoadFonts()
{
    if (s_fontsLoaded)
    {
        return;
    }

    try
    {
        string? dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrEmpty(dllDir))
        {
            return;
        }
```

```csharp
// purrTTY.Display/Rendering/PurrTTYFontManager.cs L56-L82
string[] fontFiles = Directory.GetFiles(fontsDir, "*.iamttf");
if (fontFiles.Length == 0)
{
    ModLog.Log.Error($"PurrTTYFontManager: no font files found in {fontsDir}");
    return;
}

ImFontAtlasPtr atlas = ImGui.GetIO().Fonts;
foreach (string fontPath in fontFiles)
{
    string fontName = Path.GetFileNameWithoutExtension(fontPath);
    ImFontPtr font = atlas.AddFontFromFileTTF(new ImString(fontPath), 32.0f);
    s_loadedFonts[fontName] = font;

    try
    {
        FontManager.Fonts[fontName] = font;
    }
    catch (Exception ex)
    {
        ModLog.Log.Error($"PurrTTYFontManager: could not add font '{fontName}' to FontManager: {ex.Message}");
    }
}

ModLog.Log.Info(
    $"PurrTTYFontManager: loaded {s_loadedFonts.Count} fonts - {string.Join(", ", s_loadedFonts.Keys)}");
s_fontsLoaded = true;
```

4. **Register families.** A static registry maps display names to a base file name and the variants that exist; missing Bold, Italic and BoldItalic fall back to Regular:

```csharp
// purrTTY.Display/Rendering/PurrTTYFontManager.cs L97-L124
    // Families with all 4 variants.
    RegisterFontFamily("Jet Brains Mono", "JetBrainsMonoNerdFontMono",
        hasBold: true, hasItalic: true, hasBoldItalic: true);
    RegisterFontFamily("Space Mono", "SpaceMonoNerdFontMono",
        hasBold: true, hasItalic: true, hasBoldItalic: true);
    RegisterFontFamily("Hack", "HackNerdFontMono",
        hasBold: true, hasItalic: true, hasBoldItalic: true);

    // Regular-only families (bold/italic fall back to regular).
    RegisterFontFamily("Pro Font", "ProFontWindowsNerdFontMono");
    RegisterFontFamily("Proggy Clean", "ProggyCleanNerdFontMono");
    RegisterFontFamily("Shure Tech Mono", "ShureTechMonoNerdFontMono");
    RegisterFontFamily("Departure Mono", "DepartureMonoNerdFont");

    s_registryInitialized = true;
}

private static void RegisterFontFamily(
    string displayName,
    string fontBaseName,
    bool hasBold = false,
    bool hasItalic = false,
    bool hasBoldItalic = false)
{
    s_fontRegistry[displayName] = new FontFamilyDefinition
    {
        DisplayName = displayName,
        FontBaseName = fontBaseName,
```

5. **Size per draw, not per load.** Windows resolve four `ImFontPtr`s (regular, bold, italic, bold-italic) and pass `fontSize` to `ImDrawListPtr.AddText(font, size, ...)` and to `ImGui.PushFont(font, size)` when measuring. This works because KSA's backend declares `RendererHasTextures` ([current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L656-L658)[^ksa-imguibackendvulkanimpl]). Re-resolve and re-measure when the family, the size or the loaded-font count changes:

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.Fonts.cs L15-L46
private bool _hasCachedFonts;
private string? _cachedFontFamily;
private float _cachedFontSize;
private int _cachedLoadedFontCount;

private FrameFonts ResolveFontsCached(float fontSize)
{
    int loadedCount = PurrTTYFontManager.LoadedFonts.Count;
    if (_hasCachedFonts
        && _cachedFontFamily == Settings.FontFamily
        && _cachedFontSize == fontSize
        && _cachedLoadedFontCount == loadedCount)
    {
        return _cachedFonts;
    }

    var fonts = ResolveFonts();
    ComputeCellMetrics(fonts.Regular, fontSize);
    _glyphBatchCache.Clear();
    _cachedFonts = new FrameFonts(
        fonts.Regular, fonts.Bold, fonts.Italic, fonts.BoldItalic,
        IsAsciiMonospace(fonts.Regular, fontSize, _cellWidth),
        IsAsciiMonospace(fonts.Bold, fontSize, _cellWidth),
        IsAsciiMonospace(fonts.Italic, fontSize, _cellWidth),
        IsAsciiMonospace(fonts.BoldItalic, fontSize, _cellWidth),
        _glyphBatchCache);
    _hasCachedFonts = true;
    _cachedFontFamily = Settings.FontFamily;
    _cachedFontSize = fontSize;
    _cachedLoadedFontCount = loadedCount;
    return _cachedFonts;
}
```

## Complete example

| File | Role |
|---|---|
| [purrTTY.Display/Rendering/PurrTTYFontManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Rendering/PurrTTYFontManager.cs#L34-L88) | `.iamttf` discovery and `AddFontFromFileTTF` |
| [purrTTY.Display/Rendering/PurrTTYFontManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Rendering/PurrTTYFontManager.cs#L90-L159) | Family registry and variant fallback |
| [purrTTY.Display/Ghostty/TerminalWindow.Fonts.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.Fonts.cs#L20-L97) | Per-window resolution, cell metrics, monospace validation |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L263-L281) | Calls `LoadFonts` during init |
| [purrTTY.GameMod/purrTTY.GameMod.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/purrTTY.GameMod.csproj#L65-L70) | Copies `TerminalFonts/` into the deployed mod folder |

## Variations and gotchas

- **Game font changes clear the atlas.** `Program.ScheduleFontRebuild` leads to `FontManager.RegenerateFonts`, which starts with `ImGui.GetIO().Fonts.Clear()` and `Fonts.Clear()`. purrTTY keeps the old `ImFontPtr` values in its own dictionary with no hook to reload. From the code, terminals would then hold pointers to fonts the atlas no longer owns (runtime effect not verified in game). Its loaded-font count check also does not notice, because the mod's own dictionary does not shrink.
- **The `.otf` is never loaded.** `TerminalFonts/` ships `DepartureMonoNerdFont-Regular.otf` and the registry lists "Departure Mono", but `LoadFonts` only globs `*.iamttf`, so that family resolves to no font and resolution falls back to `ImGui.GetFont()`.
- **Load at a generous base size** (32 px here) and size per draw; do not add one font per size.
- **Do not call `PushFont` outside an ImGui frame**; keep measurement in the render path.
- **No braille in Nerd Fonts.** Where a glyph range is missing, draw it as shapes (see [Draw per-frame text efficiently: monospace grids and allocation-free labels](../../../../howto/ui/draw-lists/draw-a-monospace-text-grid/overview.md)).
- **Version.** `FontManager.Fonts` is public static in 2026.10.10.5554; check it after updates.

## See also

- [Overview of custom fonts](overview.md)
- [Approach 1: add at immediate load](load-a-custom-font-into-imgui-1.md)
- [Approach 4: surviving a font rebuild](load-a-custom-font-into-imgui-4.md)
- [Draw per-frame text efficiently: monospace grids and allocation-free labels](../../../../howto/ui/draw-lists/draw-a-monospace-text-grid/overview.md)
- Mod feature: [Bundled Nerd Fonts loaded into the ImGui atlas](../../../../mods/purrtty/features/nerd-font-loading-into-imgui-atlas.md)

[^brutal-imguiapi-imfontatlasextensions]: Brutal.ImGuiApi.ImFontAtlasExtensions (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-imguibackendvulkanimpl]: KSA.ImGuiBackendVulkanImpl (decompiled, build 2026.10.10.5554)
