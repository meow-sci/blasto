---
type: How-To
title: Add a bundled TTF to the ImGui atlas at StarMapImmediateLoad and draw it at any size
description: Locate a font next to the mod DLL, call AddFontFromFileTTF once during immediate load, and use ImGui.PushFont(font, size) with balanced pops
category: ui
group: draw-lists
topic: load-a-custom-font-into-imgui
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksa-aircrafthud/features/custom-hud-font, mods/compendium/features/custom-fonts-in-imgui]
techniques: [imgui-font, starmap-lifecycle]
hooks: [starmap StarMapImmediateLoad, harmony-postfix KSA.Program.OnFrameEditor, starmap StarMapAfterGui]
lifecycle: [startup:immediate-load, frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImFontAtlasPtr, Brutal.ImGuiApi.ImGui, KSA.FontManager, KSA.Mod, KSA.Program]
tags: [font, ttf, immediate-load, pushfont, atlas]
---
KSA_AircraftHUD draws its whole HUD in Hornet Display Bold, a stencil-like face shipped in the mod folder. The mod adds it once to ImGui's shared font atlas from `[StarMapImmediateLoad]`, keeps the returned `ImFontPtr`, and each frame pushes it at a large size for the speed and altitude boxes and a medium size for everything else. Compendium does the same for a whole `Fonts/` folder. Prerequisite: a StarMap mod and a font file that your csproj copies beside the DLL. Demonstrated by [Custom TTF font in the ImGui atlas](../../../../mods/ksa-aircrafthud/features/custom-hud-font.md) and [Custom fonts in the ImGui atlas](../../../../mods/compendium/features/custom-fonts-in-imgui.md).

## Background: how the game does it

KSA's own loader only sees fonts it scans at startup. `FontManager.Initialize` collects `*.ttf` under the game's `Content` folder and `RegenerateFonts` adds each to the atlas ([current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L27-L32)[^ksa-fontmanager]). A mod's `mod.toml` `fonts` list reaches only `FontManager.RegisterFont`, which adds the file name to the settings combo ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L287-L311)[^ksa-mod]); it does not load the file. A mod in the user mods folder that wants a private font therefore adds it to the atlas itself with the Brutal binding's `AddFontFromFileTTF` ([current/decomp/Brutal.ImGuiApi/ImFontAtlasExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImFontAtlasExtensions.cs#L18-L27)[^brutal-imguiapi-imfontatlasextensions]).

Sizing is handled by ImGui 1.92's dynamic atlas: the Vulkan backend sets `ImGuiBackendFlags.RendererHasTextures` ([current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L656-L658)[^ksa-imguibackendvulkanimpl]), glyphs are rasterized on demand at whatever size is requested, and the two-argument `ImGui.PushFont(font, size)` ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5872-L5875)[^brutal-imguiapi-imgui]) selects that size. One atlas entry therefore serves every size, and a "text scale" slider takes effect on the next frame without reloading the font.

## Lifecycle and timing

- **Add at `startup:immediate-load`.** The `Program` constructor creates the ImGui context, initializes the backend and runs `FontManager.Initialize` before `ModLibrary.PrepareAll` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L842-L900)[^ksa-program]), which triggers `[StarMapImmediateLoad]`. The atlas already holds the game fonts and accepts additions.
- **Use at `frame:draw-ui`/`frame:viewports`.** AircraftHUD pushes and pops inside a postfix on `Program.OnFrameEditor` (between `ImGui.NewFrame` and `ImGui.Render`); Compendium inside `[StarMapAfterGui]`.
- **Hazard at `frame:prepare`.** A change to interface font settings clears the atlas on the next `PrepareFrame` and your pointer no longer belongs to it; see [approach 4](load-a-custom-font-into-imgui-4.md).

See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Ship the font.** Put the TTF in a `Fonts/` folder, mark it `CopyToOutputDirectory` in the csproj, and ship its license next to it.

2. **Declare fields for the font and its path.**

```csharp
// AircraftHUDMain.cs L100-L106
// TEXT
private bool fontLoaded = false;
private ImFontPtr hudFont;
static float fontSizeSm = 20f;
static float fontSizeMed = 26f;
static float fontSizeLrg = 32f;
private string fontFile = "Fonts/HornetDisplay-Bold.ttf";
```

3. **Locate the file next to the DLL, then add it in `[StarMapImmediateLoad]`.** The signature must have exactly one `KSA.Mod` parameter or StarMap never calls it. The load is guarded by `File.Exists` and a `fontLoaded` flag:

```csharp
// AircraftHUDMain.cs L660-L662
[StarMapImmediateLoad]
unsafe public void Init(Mod definingMod)
{
```

```csharp
// AircraftHUDMain.cs L701-L713
if (!fontLoaded)
{
    ImGuiIO* io = ImGui.GetIO();
    ImFontAtlasPtr atlas = io->Fonts;

    string fontPathStr = Path.Combine(dllDir, fontFile);
    if (File.Exists(fontPathStr))
    {
        ImString fontPath = new ImString(fontPathStr);
        hudFont = atlas.AddFontFromFileTTF(fontPath, fontSizeLrg);
        fontLoaded = true;
    }
}
```

4. **Size from screen height, not from the atlas entry.** The three sizes are computed from the viewport height and a user scale, and passed to `PushFont` at draw time:

```csharp
// AircraftHUDMain.cs L688-L692
verticalScale = viewport->Size.Y / 1440f;

fontSizeSm = (float)Math.Round(20f * textScale * verticalScale);
fontSizeMed = (float)Math.Round(26f * textScale * verticalScale);
fontSizeLrg = (float)Math.Round(32f * textScale * verticalScale);
```

5. **Push, switch, pop.** Push the large size, draw, pop and push the medium size, draw, then pop once at the end. Guard every push with `fontLoaded` and every pop with the same flag:

```csharp
// AircraftHUDMain.cs L1090-L1108
// TEXT
// SWITCH TO LARGE FONT
if (fontLoaded) { ImGui.PushFont(hudFont, fontSizeLrg); }

// ALTITUDE BOX
DrawTextAligned(draw_list, new float2(localCenter.X + textPosX, localCenter.Y), color, FormatStringPadded(useRadarAlt ? altitudeRadar : altitude, 5));
DrawCenteredRect(draw_list, new float2(textBoxSizeX, textBoxSizeY), new float2(localCenter.X + textPosX, localCenter.Y), color, line_thickness);
if (useRadarAlt) { DrawTextAligned(draw_list, new float2(localCenter.X + textPosX + (textBoxSizeX * 0.7f), localCenter.Y), color, "R"); }

// SPEED BOX
DrawTextAligned(draw_list, new float2(localCenter.X - textPosX, localCenter.Y), color, FormatStringPadded(speed, 4));
DrawCenteredRect(draw_list, new float2(textBoxSizeX, textBoxSizeY), new float2(localCenter.X - textPosX, localCenter.Y), color, line_thickness);

// SWITCH TO MEDIUM FONT
if (fontLoaded)
{
    ImGui.PopFont();
    ImGui.PushFont(hudFont, fontSizeMed);
}
```

6. **Variation: a folder of fonts (Compendium).** Scan `Fonts/` for `*.ttf` and `*.otf`, add each at a base size, keep the returned `ImFontPtr` by file name, and push with a counter so a missing font degrades to ImGui's default without an unbalanced pop:

```csharp
// src/CdmLoadFonts.cs L13-L26
private static void LoadFonts(string? dllDir, float fontSizeCurrent)
{
    if (!string.IsNullOrEmpty(dllDir))
    {
        string fontsDir = Path.Combine(dllDir, "Fonts");

        if (Directory.Exists(fontsDir))
        {
            // Get all .ttf and .otf files from Fonts folder
            var ttfFiles = Directory.GetFiles(fontsDir, "*.ttf");
            var otfFiles = Directory.GetFiles(fontsDir, "*.otf");
            fontFiles = ttfFiles.Concat(otfFiles).ToArray();

            if (fontFiles.Length > 0)
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

## Complete example

| File | Role |
|---|---|
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L100-L106) | Font fields and default sizes |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L701-L713) | Atlas load |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1090-L1108) | Push and switch sizes |
| [src/CdmLoadFonts.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadFonts.cs#L13-L72) | Folder scan and atlas registration |
| [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L716-L735) | Sized push with a balancing counter |

## Variations and gotchas

- **Font settings wipe it.** Changing font options in game settings calls `Program.ScheduleFontRebuild()`; the next `PrepareFrame` runs `FontManager.RegenerateFonts()`, which starts with `ImGui.GetIO().Fonts.Clear()`, and the frame returns `Exit`, so the HUD skips it. On the next frame the cached `hudFont` still holds the old pointer while `fontLoaded` stays true. Dear ImGui's `ImFontAtlas::Clear` destroys the fonts it owns (upstream behaviour, not shown in the decomp), so `PushFont` would receive a dangling pointer. See [approach 4](load-a-custom-font-into-imgui-4.md).
- **Unbalanced pop.** AircraftHUD ends `DrawHud` with an unconditional `ImGui.PopFont()` even when the font was never loaded ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1327)); pop only when you pushed.
- **Exceptions skip pops.** Compendium's catch block ends the child and window but does not pop fonts pushed before the throw.
- **Do not add per frame.** Each `AddFontFromFileTTF` adds an atlas entry.
- **Load order in a mod.** Compendium loads fonts after `Patcher.Patch` in `OnImmediateLoad`, so a patch failure skips them.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of custom fonts](overview.md)
- [Approach 2: lazy load](load-a-custom-font-into-imgui-2.md)
- [Approach 3: renamed extension](load-a-custom-font-into-imgui-3.md)
- [Approach 4: surviving a font rebuild](load-a-custom-font-into-imgui-4.md)
- Mod feature: [Custom TTF font in the ImGui atlas](../../../../mods/ksa-aircrafthud/features/custom-hud-font.md)
- Mod feature: [Custom fonts in the ImGui atlas](../../../../mods/compendium/features/custom-fonts-in-imgui.md)

[^ksa-fontmanager]: KSA.FontManager (decompiled, build 2026.10.10.5554)
[^ksa-mod]: KSA.Mod (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imfontatlasextensions]: Brutal.ImGuiApi.ImFontAtlasExtensions (decompiled, build 2026.10.10.5554)
[^ksa-imguibackendvulkanimpl]: KSA.ImGuiBackendVulkanImpl (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
