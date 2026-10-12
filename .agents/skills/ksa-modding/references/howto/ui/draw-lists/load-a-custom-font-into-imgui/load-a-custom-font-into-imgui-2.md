---
type: How-To
title: Add fonts lazily on the first UI frame with the game's own ImFontConfig fields
description: Resolve the font files from Mod.DirectoryPath, add them on the first overlay draw with the same rasterizer settings KSA.FontManager uses, fall back to the game font, and keep digits from jittering
category: ui
group: draw-lists
topic: load-a-custom-font-into-imgui
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/montserrat-fonts-in-imgui-atlas]
techniques: [imgui-font, starmap-lifecycle]
hooks: [starmap StarMapImmediateLoad, starmap StarMapAfterGui]
lifecycle: [startup:immediate-load, frame:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImFontAtlasPtr
  - Brutal.ImGuiApi.ImFontConfig
  - Brutal.ImGuiApi.ImGui
  - KSA.FontManager
  - KSA.Mod
  - KSA.ImGuiBackendVulkanImpl
tags: [font, lazy, fallback, imfontconfig, tabular-digits, pushfont]
---
KSA-Public-Telemetry's broadcast look depends on Montserrat Bold for numbers and labels and Montserrat Regular for body text, shipped in a `montserrat/` folder with the SIL Open Font License. The fonts go into the same atlas the game uses, so any ImGui call or draw list can use them, and if the files are missing the overlay quietly falls back to the game font. The mod loads them on the first overlay draw instead of at startup, with the same `ImFontConfig` fields the game's own loader sets. Demonstrated by [Montserrat fonts in the ImGui atlas](../../../../mods/ksa-public-telemetry/features/montserrat-fonts-in-imgui-atlas.md). Prerequisite: a StarMap mod with an overlay that draws every frame.

## Background: how the game does it

`FontManager.LoadFont` is what the game uses for its own fonts. It sets `OversampleH = 3`, `OversampleV = 2`, `PixelSnapH` and `RasterizerDensity` from settings, `GlyphMaxAdvanceX = float.MaxValue`, `RasterizerMultiply = 1` and `SizePixels`, then calls `AddFontFromFileTTF(filename, sizePixels, &config)`:

```csharp
// current/decomp/KSA/FontManager.cs L100-L114
public unsafe static ImFontPtr LoadFont(string filename, ImFontAtlasPtr atlasPtr, float sizePixels, bool pixelSnapH, float fontDensity)
{
	ImGui.GetStyle().FontScaleMain = 1f;
	ImFontConfig imFontConfig = new ImFontConfig
	{
		OversampleH = 3,
		OversampleV = 2,
		PixelSnapH = pixelSnapH,
		GlyphMaxAdvanceX = float.MaxValue,
		RasterizerMultiply = 1f,
		RasterizerDensity = fontDensity,
		SizePixels = sizePixels
	};
	return atlasPtr.AddFontFromFileTTF(filename, sizePixels, &imFontConfig);
}
```

Copying those fields gives mod fonts the same rasterization as the interface font. `Mod.DirectoryPath` is the folder of the mod's `mod.toml` ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L91-L109)[^ksa-mod]), which is where the font folder lives.

## Lifecycle and timing

- **Directory at `startup:immediate-load`.** `[StarMapImmediateLoad]` receives the game's `Mod` and stores `mod.DirectoryPath`. Only the path is taken here.
- **Fonts at the first overlay draw (`frame:draw-ui`).** The game creates the ImGui context and runs `FontManager.Initialize` in the `Program` constructor before `ModLibrary.LoadAll` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L842-L858)[^ksa-program]), so `[StarMapAllModsLoaded]` would also work; the mod simply loads on the first draw, once it knows it will draw (`[StarMapAfterGui]`, or the `DrawFps` postfix if the game starts with the HUD hidden; see [Keep mod UI and per-frame work running when the HUD is hidden (F2)](../../../../howto/ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md)).
- **Mid-frame is legal.** The backend sets `RendererHasTextures` ([current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L656-L658)[^ksa-imguibackendvulkanimpl]), ImGui 1.92's dynamic atlas mode, in which fonts can be added after `NewFrame` and glyphs are rasterized on demand at the size `PushFont` asks for.
- **Rebuild at `frame:prepare`.** A game font-settings change clears the atlas; the lazy design can recover only if it re-adds (see [approach 4](load-a-custom-font-into-imgui-4.md)). As written the mod's `_attempted` flag prevents a reload.

See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Store the mod directory in the immediate-load hook.**

```csharp
// TelemetryOverlayMod.cs L32-L37
[StarMapImmediateLoad]
public void OnImmediateLoad(Mod mod)
{
    _modDirectory = mod.DirectoryPath;
    Console.WriteLine(LogPrefix + $"attached to KSA mod '{mod.Id}'");
}
```

2. **Resolve files in three places.** `<mod>/montserrat/<file>`, then `<mod>/<file>`, then a path relative to the working directory (the game folder under StarMap):

```csharp
// Rendering/OverlayFonts.cs L80-L99
private static string? Resolve(string? modDirectory, string fileName)
{
    if (!string.IsNullOrEmpty(modDirectory))
    {
        string beside = Path.Combine(modDirectory, FontFolder, fileName);
        if (File.Exists(beside))
        {
            return beside;
        }

        string flat = Path.Combine(modDirectory, fileName);
        if (File.Exists(flat))
        {
            return flat;
        }
    }

    string local = Path.Combine(FontFolder, fileName);
    return File.Exists(local) ? local : null;
}
```

3. **Load once, on the first draw, and degrade quietly.** `TryLoad` is the first call in every overlay draw and returns after the first attempt, loading three `ImFontPtr`s (Numeric from Bold, Label from Bold, Body from Regular). If no file is found, nothing is loaded and the overlay uses the game font:

```csharp
// TelemetryOverlayMod.cs L104-L108

try
{
    OverlayFonts.TryLoad(_modDirectory);

```

```csharp
// Rendering/OverlayFonts.cs L21-L62
public static void TryLoad(string? modDirectory)
{
    if (_attempted)
    {
        return;
    }

    _attempted = true;

    try
    {
        string? regular = Resolve(modDirectory, RegularFile);
        string? bold = Resolve(modDirectory, BoldFile);

        if (regular is null && bold is null)
        {
            Console.WriteLine(
                "[KSATelemetryOverlay] Montserrat not found; using the game font.");
            return;
        }

        string numericSource = bold ?? regular!;
        string labelSource = bold ?? regular!;
        string bodySource = regular ?? bold!;

        ImFontAtlasPtr atlas = ImGui.GetIO().Fonts;

        Numeric = Load(atlas, numericSource, ClockSize);
        Label = Load(atlas, labelSource, LabelSize);
        Body = Load(atlas, bodySource, BodySize);

        Console.WriteLine("[KSATelemetryOverlay] loaded Montserrat overlay fonts.");
    }
    catch (Exception ex)
    {
        Numeric = null;
        Label = null;
        Body = null;
        Console.WriteLine(
            $"[KSATelemetryOverlay] font load failed, using the game font: {ex.Message}");
    }
}
```

4. **Use the game's rasterizer settings.** `Load` fills an `ImFontConfig` with the fields from `FontManager.LoadFont` (the mod uses density 1 and no pixel snap where the game reads both from settings):

```csharp
// Rendering/OverlayFonts.cs L64-L78
private static unsafe ImFontPtr Load(ImFontAtlasPtr atlas, string path, float sizePixels)
{

    ImFontConfig config = default;

    config.OversampleH = 3;
    config.OversampleV = 2;
    config.PixelSnapH = false;
    config.GlyphMaxAdvanceX = float.MaxValue;
    config.RasterizerMultiply = 1f;
    config.RasterizerDensity = 1f;
    config.SizePixels = sizePixels;

    return atlas.AddFontFromFileTTF(path, sizePixels, &config, default);
}
```

5. **Push at the requested size, tolerate a missing font.** A null font pushes `default`, which keeps the current font at the requested size:

```csharp
// Rendering/Gfx.cs L312-L323
public static void PushFont(ImFontPtr? font, float sizePixels)
    => ImGui.PushFont(font ?? default, sizePixels);

public static void PopFont() => ImGui.PopFont();

public static float2 MeasureWithFont(ImFontPtr? font, float sizePixels, ReadOnlySpan<char> text)
{
    PushFont(font, sizePixels);
    float2 size = ImGui.CalcTextSize(text);
    PopFont();
    return size;
}
```

6. **Fix proportional figures for clocks.** Montserrat's digits are proportional, so a running clock jitters. `TextTabular` measures the widest digit and centres every digit in a cell of that width; every text call also draws a 1 px offset shadow first:

```csharp
// Rendering/Gfx.cs L355-L367
private static float DigitCellWidth()
{
    float widest = 0f;
    Span<char> digit = stackalloc char[1];

    for (char c = '0'; c <= '9'; c++)
    {
        digit[0] = c;
        widest = MathF.Max(widest, ImGui.CalcTextSize(digit).X);
    }

    return widest;
}
```

```csharp
// Rendering/Gfx.cs L387-L419
public static void TextTabular(
    ImDrawListPtr drawList,
    ImFontPtr? font,
    float sizePixels,
    float2 pos,
    uint color,
    ReadOnlySpan<char> text,
    float opacity)
{
    PushFont(font, sizePixels);

    float cell = DigitCellWidth();
    float x = pos.X;

    for (int i = 0; i < text.Length; i++)
    {
        ReadOnlySpan<char> glyph = text.Slice(i, 1);
        float advance = ImGui.CalcTextSize(glyph).X;

        if (char.IsAsciiDigit(text[i]))
        {
            Text(drawList, new float2(x + (cell - advance) * 0.5f, pos.Y), color, glyph, opacity);
            x += cell;
        }
        else
        {
            Text(drawList, new float2(x, pos.Y), color, glyph, opacity);
            x += advance;
        }
    }

    PopFont();
}
```

## Complete example

| File | Role |
|---|---|
| [Rendering/OverlayFonts.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayFonts.cs#L21-L99) | Resolve, load once, fall back |
| [Rendering/Gfx.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Gfx.cs#L312-L419) | `PushFont(font, size)` helpers, measuring, tabular digits |
| [KSATelemetryOverlay.csproj](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/KSATelemetryOverlay.csproj#L15-L29) | Ship the TTFs and the OFL with the DLL |
| [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L32-L37) | Capture `mod.DirectoryPath` |

## Variations and gotchas

- **Font rebuilds invalidate the pointers (inferred from the decomp, not tested in game).** The next `PrepareFrame` after a font-settings change runs `FontManager.RegenerateFonts`, which clears the atlas and rebuilds only the game's fonts, then returns `Exit`. `OverlayFonts.Numeric/Label/Body` would then point at fonts the atlas no longer owns and `_attempted` blocks a reload. A safer copy remembers a game font it saw at load (for example `FontManager.ConsoleFont`, which `RegenerateFonts` reassigns) or checks `FontManager.Fonts` each frame as in [approach 4](load-a-custom-font-into-imgui-4.md).
- **The manifest `fonts` key is not an alternative.** It only registers a name in the interface-font combo (`FontManager.FontFiles`); `RegenerateFonts` loads the `Content/*.ttf` list captured by `FontManager.Initialize`.
- **Working-directory fallback.** The last `Resolve` candidate is relative to the process working directory, which under StarMap is the game folder, not the mod folder.
- **Atlas growth.** Three fonts are added once; never call `AddFontFromFileTTF` per frame.
- **Ship the license.** The csproj comment says the OFL must travel with the font.

## See also

- [Overview of custom fonts](overview.md)
- [Approach 1: add at immediate load](load-a-custom-font-into-imgui-1.md)
- [Approach 4: surviving a font rebuild](load-a-custom-font-into-imgui-4.md)
- Mod feature: [Montserrat fonts in the ImGui atlas](../../../../mods/ksa-public-telemetry/features/montserrat-fonts-in-imgui-atlas.md)

[^ksa-mod]: KSA.Mod (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-imguibackendvulkanimpl]: KSA.ImGuiBackendVulkanImpl (decompiled, build 2026.10.10.5554)
