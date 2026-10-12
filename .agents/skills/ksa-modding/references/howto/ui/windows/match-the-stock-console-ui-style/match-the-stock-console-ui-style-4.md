---
type: How-To
title: Re-theme every KSA and mod window through the global ImGuiStyle (capture, restore and TOML presets)
description: Change the look of the whole game UI by writing the global ImGuiStyle, capturing the original at startup, restoring it at unload and storing themes as TOML
category: ui
group: windows
topic: match-the-stock-console-ui-style
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/skittles-imgui-theme-manager]
techniques: [toml-config, imgui-window]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterGui, starmap StarMapUnload]
lifecycle: [startup:all-mods-loaded, frame:draw-ui, event:save, event:load, frame:prepare]
game_types:
  - Brutal.ImGuiApi.ImGuiStyle
  - Brutal.ImGuiApi.ImGuiCol
  - KSA.ConsoleStyle
  - KSA.FontManager
  - KSA.KSAColor
tags: [theme, imgui-style, global-style, toml, presets, style-editor, skittles]
---
Instead of matching the game's look per window, unscience's Skittles submod changes the look of the **whole UI at once**: every KSA window, menu and control, and every mod window. Dear ImGui keeps one global `ImGuiStyle` that every widget reads at submission time, and Brutal exposes it through `ImGui.GetStyle()` as an `ImGuiStylePtr` with `ref` properties, so assigning its fields (including the
60-entry `Colors` array) re-themes everything without a Harmony patch. Pick a theme from a filterable list, tweak colours and sizes live with the Dear ImGui style editor, save under a name, and the last theme is re-applied at the next start; unloading the mod puts the original style back. Demonstrated by unscience (Skittles). Prerequisite: StarMap.

## Background: how the game does it

- The managed game code **never calls `StyleColors*` and never assigns `ImGuiStyle` colours**; the only global style write found is `FontManager.LoadFont` setting `FontScaleMain = 1`.[current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L102)[^ksa-fontmanager] "Game Default" is therefore Dear ImGui's context default as created in the `Program` constructor (`ImGui.CreateContext()` runs before mods load). The game's distinctive looks come from **per-widget overrides**: `PushStyleColor` appears on 137 lines
  across 32 files of the `KSA` namespace (`ConsoleStyle.PushWidgetStyle` pushes 48 colours alone), and those pushes win over any global theme for the widgets they wrap.[current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L575-L650)[^ksa-consolestyle]
- `ImGui.GetStyle()` returns an `ImGuiStylePtr`; `ImGui.StyleColorsDark()`, `StyleColorsLight()` and `StyleColorsClassic()` set colours only; `ImGui.ShowStyleEditor()` with no reference style edits the live global style.
- A style change made from `[StarMapAfterGui]` affects widgets submitted after it in the same ImGui frame (the game's `OnDrawUiThreadSafe` windows); windows drawn earlier pick it up on the next frame.

## Lifecycle and timing

| Step | Hook | Phase |
|---|---|---|
| Capture "Game Default", apply the startup theme | `ISubmod.Initialize` from `[StarMapAllModsLoaded]` | `startup:all-mods-loaded` |
| Apply, save, delete, style editor | `RenderContent` / `RenderFloatingWindows` from `[StarMapAfterGui]` | `frame:draw-ui` |
| Capture / reset and restore | Native save; native load at the `PrepareFrame` handoff | `event:save`, `event:load`, `frame:prepare` |
| Restore the captured default | `ISubmod.Dispose` from `[StarMapUnload]` | unload |

`ImGui.CreateContext()` runs in the `Program` constructor before mods are loaded, so the style exists when `Initialize` captures it. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Capture the original style before touching anything.** Snapshot it into a plain class (60 `float[4]` colours indexed by `(int)ImGuiCol`, 35 float vars, 15 `float2` vars, 3 anti-aliasing bools); `CaptureFromImGui()` copies out of `ImGui.GetStyle()` and `ApplyToImGui()` writes back field by field:

```csharp
// skittles.lib/ThemeManager.cs L38-L72
public void Initialize()
{
    // 1. Capture game default style BEFORE any theming
    DefaultTheme = ThemeDefinition.CaptureFromImGui();
    DefaultTheme.Name = "Game Default";
    DefaultTheme.Description = "The game's original default style";

    // 2. Ensure directories exist
    Directory.CreateDirectory(_themesDirectory);

    // 3. Ship the Inanimate Carbon Rod preset if not present
    string icr_path = Path.Combine(_themesDirectory, "inanimate-carbon-rod.toml");
    if (!File.Exists(icr_path))
    {
        var icr = BuiltInThemes.CarbonRod();
        ThemeSerializer.SaveToFile(icr, icr_path);
        Console.WriteLine("skittles: Shipped Inanimate Carbon Rod preset");
    }

    // 4. Load config
    Config = ModConfigSerializer.LoadFromFile(_configFilePath);

    // 5. Discover themes
    RefreshThemeList();

    // 6. Apply startup theme
    if (!string.IsNullOrEmpty(Config.ActiveThemeName))
    {
        bool found = AvailableThemes.Any(t => t.Name == Config.ActiveThemeName);
        if (found)
        {
            ApplyTheme(Config.ActiveThemeName);
        }
    }
}
```
```csharp
// skittles.lib/ThemeDefinition.cs L170-L183
public void ApplyToImGui()
{
    ImGuiStylePtr style = ImGui.GetStyle();

    // Colors
    for (int i = 0; i < 60; i++)
    {
        float[] c = Colors[i];
        style.Colors[i] = new float4(c[0], c[1], c[2], c[3]);
    }

    // Float vars
    style.Alpha = Alpha;
    style.DisabledAlpha = DisabledAlpha;
```

2. **Apply a theme by name.** "Game Default" re-applies the snapshot; the three built-ins call `StyleColorsDark/Light/Classic()` then re-apply the snapshot's non-colour style vars (those methods only set colours); anything else loads a TOML file. Persist the choice every time:

```csharp
// skittles.lib/ThemeManager.cs L83-L119
if (themeName == "Game Default")
{
    DefaultTheme?.ApplyToImGui();
}
else if (themeName == "Dark")
{
    ImGui.StyleColorsDark();
    ApplyDefaultStyleVars();
}
else if (themeName == "Light")
{
    ImGui.StyleColorsLight();
    ApplyDefaultStyleVars();
}
else if (themeName == "Classic")
{
    ImGui.StyleColorsClassic();
    ApplyDefaultStyleVars();
}
else
{
    var entry = AvailableThemes.FirstOrDefault(t => t.Name == themeName);
    if (entry?.FilePath != null)
    {
        var theme = ThemeSerializer.LoadFromFile(entry.FilePath);
        theme?.ApplyToImGui();
    }
    else
    {
        Console.WriteLine($"skittles: Theme '{themeName}' not found");
        return;
    }
}

ActiveThemeName = themeName;
Config.ActiveThemeName = themeName;
ModConfigSerializer.SaveToFile(Config, _configFilePath);
```

3. **Store themes as TOML** (Tomlyn): `[meta]` (name, description), `[colors]` (60 keys named after the `ImGuiCol` members, each a four-number array rounded to two decimals) and `[style]`. Read leniently: unknown colour names are skipped, missing keys keep defaults, numbers parse with the invariant culture, and a parse failure logs the diagnostics and returns null.
4. **Offer a picker and the embedded editor.** A filterable combo applies on selection; the editor is a floating window with Save, Save as New and `ImGui.ShowStyleEditor()` with no reference style, so it edits the live global style.
5. **Remember the exact style in saves.** The scene-save participant captures a full `ThemeDefinition` of the live style, so unsaved editor tweaks are kept, and validates it on load (a colour array that is not 60 by 4 finite numbers, a non-finite float, an `Alpha` outside 0.05 to 1 or non-positive tessellation values are rejected) so a corrupt record cannot make the UI invisible.
6. **Restore at unload.** `Dispose` puts the captured default back.

## Complete example

| File | Role |
|---|---|
| [skittles.lib/ThemeDefinition.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/skittles.lib/ThemeDefinition.cs) | Capture from and apply to `ImGui.GetStyle()` |
| [skittles.lib/ThemeManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/skittles.lib/ThemeManager.cs) | Paths, initialisation, apply, save, delete, list, restore |
| [skittles.lib/ThemeSerializer.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/skittles.lib/ThemeSerializer.cs) | TOML mapping with named colour keys |
| [skittles.lib/SkittlesSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/skittles.lib/SkittlesSubmod.cs) | Picker, delete modal, editor window, `Dispose` |
| [skittles.lib/SkittlesSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/skittles.lib/SkittlesSubmod.Saves.cs) | Scene-save participant and validation |

## Variations and gotchas

- **Per-widget colours still win.** Widgets that push their own colours (everything under `ConsoleStyle.PushWidgetStyle`) ignore the global theme; for per-window colours see the widget-kit topic.
- **Capture first, theme second.** Any theming before the capture loses "Game Default".
- **Built-ins only set colours**, so re-apply the style vars (rounding, padding, spacing) or switching between themes accumulates sizes.
- **Validate stored styles.** A zero-alpha `Alpha` or non-finite float would blank the UI.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of match-the-stock-console-ui-style](overview.md)
- [Approach 1: ConsoleWidgets rows inside an ImGuiWindow subclass](match-the-stock-console-ui-style-1.md)
- [Build custom-drawn ImGui widgets, icons, trees and alert tiles on the draw list](../../draw-lists/build-custom-drawn-imgui-widgets/overview.md)
- [Persist mod settings as TOML](../../../persistence/settings-formats/persist-mod-settings-toml/overview.md)
- Mod feature: [Skittles ImGui theme manager](../../../../mods/unscience/features/skittles-imgui-theme-manager.md)

[^ksa-fontmanager]: KSA.FontManager (decompiled, build 2026.10.10.5554)
[^ksa-consolestyle]: KSA.ConsoleStyle (decompiled, build 2026.10.10.5554)
