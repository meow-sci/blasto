---
type: How-To
title: "Settings window: ColorButton popups and a hand-parsed INI file"
description: Build a compact HUD settings window from buttons, sliders and swatch buttons that open ColorPicker4 popups, and load and save the values with a tiny INI reader and writer
category: ui
group: windows
topic: build-a-settings-window
approach: 3
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksa-aircrafthud/features/ini-settings-and-settings-window]
techniques: [imgui-window, starmap-lifecycle]
hooks: [starmap StarMapBeforeGui, starmap StarMapImmediateLoad]
lifecycle: [startup:immediate-load, frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.Numerics.float4]
tags: [settings, color-button, color-picker, popup, ini, slider, aircraft-hud]
---
KSA Aircraft HUD has about a dozen settings: enable switches, unit systems, four colours and two scale sliders. Its settings window is the shortest complete example of the pattern: buttons that flip booleans, a swatch button per colour that opens a `ColorPicker4` popup, sliders that recompute derived layout only when they change, and a "Save HUD Settings" button that rewrites a small INI file next to the DLL. A ModMenu entry toggles the window. Demonstrated by KSA Aircraft HUD. Prerequisite: StarMap.

## Background: how the game does it

No KSA settings API is involved; only `Brutal.ImGuiApi` widgets (`Begin`, `Button`, `ColorButton`, `OpenPopup`, `BeginPopup`, `ColorPicker4`, `SliderFloat`, `ColorConvertFloat4ToU32`). The game's own menu bar is a separate "Menu Bar" window that `Program.DrawMenuBar` fills; this mod adds nothing to it and uses a ModMenu entry instead (how ModMenu calls the attribute method is not verified in these references).

## Lifecycle and timing

`LoadHudIni` runs at the start of `Init`, which StarMap invokes from its prefix on `Mod.PrepareSystems` (`startup:immediate-load`), before layout and font sizes are derived from the loaded scales. The window is drawn by a `[StarMapBeforeGui]` method, a Harmony prefix on `Program.OnDrawUiFrame` (`frame:draw-ui`), which `OnFrame` only calls under `if (DrawUI)`,[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2271)[^ksa-program] so the window hides with the game UI. File I/O is synchronous on the main thread at load and on the Save button. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Toggle the window from a ModMenu entry**; the window checks a static flag every frame:

```csharp
// AircraftHUDMain.cs L153-L165
[ModMenuEntry("Aircraft HUD")]
public static void CreateModMenu()
{
    if (ImGui.MenuItem(HUD.enabled ? "Disable HUD" : "Enable HUD"))
    {
        HUD.enabled = !HUD.enabled;
    }

    if (ImGui.MenuItem("Settings"))
    {
        HUD.settingsPageOn = !HUD.settingsPageOn;
    }
}
```

2. **Begin the window with its close flag**, then draw boolean buttons whose labels show the current state:

```csharp
// AircraftHUDMain.cs L769-L791
[StarMapBeforeGui]
unsafe public void DrawHudMenu(double dt)
{
    ImGuiWindowFlags menuFlags = ImGuiWindowFlags.MenuBar;
    // options page
    if (HUD.settingsPageOn)
    {
        ImGui.Begin("HUD Settings", ref HUD.settingsPageOn, menuFlags);

        if (ImGui.Button(HUD.enabled ? "Disable HUD" : "Enable HUD"))
        {
            HUD.enabled = !HUD.enabled;
        }

        if (ImGui.Button(HUD.orbitMode ? "Orbit: Surface" : "Orbit: Body"))
        {
            HUD.orbitMode = !HUD.orbitMode;
        }

        if (ImGui.Button(HUD.effectsOutsideIVA ? "Hide Effects" : "Show Effects"))
        {
            HUD.effectsOutsideIVA = !HUD.effectsOutsideIVA;
        }
```

3. **Colour swatches open a popup picker.** A `ColorButton` per colour calls `OpenPopup`; each popup holds a `ColorPicker4` bound to the `float4`, and the packed `uint` used by the draw list is recomputed every frame the popup is open:

```csharp
// AircraftHUDMain.cs L829-L857
// Color Pickers
ImGui.Text("Colors");
ImGui.SameLine();
if (ImGui.ColorButton("##hudColorPickerBtn", hudColorPicker))
{
    ImGui.OpenPopup("hudColorPicker");
}
ImGui.SameLine();
if (ImGui.ColorButton("##hudColorPicker1Btn", hudColor1Picker))
{
    ImGui.OpenPopup("hudColor1Picker");
}
ImGui.SameLine();
if (ImGui.ColorButton("##hudColorPicker2Btn", hudColor2Picker))
{
    ImGui.OpenPopup("hudColor2Picker");
}
ImGui.SameLine();
if (ImGui.ColorButton("##shadowColorPickerBtn", shadowColorPicker))
{
    ImGui.OpenPopup("shadowColorPicker");
}

if (ImGui.BeginPopup("hudColorPicker"))
{
    ImGui.ColorPicker4("##hudColorPicker", ref hudColorPicker);
    HUD.color = ImGui.ColorConvertFloat4ToU32(hudColorPicker);
    ImGui.EndPopup();
}
```

4. **Recompute derived values only on change.** `SliderFloat` returns true on the frame the slider moves; the block recomputes font sizes or line thickness once:

```csharp
// AircraftHUDMain.cs L877-L888
if (ImGui.SliderFloat("Text Scale", ref HUD.textScale, 0.8f, 1.2f))
{
    fontSizeSm = (float)Math.Round(20f * HUD.textScale * verticalScale);
    fontSizeMed = (float)Math.Round(26f * HUD.textScale * verticalScale);
    fontSizeLrg = (float)Math.Round(32f * HUD.textScale * verticalScale);
}

if (ImGui.SliderFloat("Line Scale", ref HUD.lineScale, 0.5f, 2.0f))
{
    line_thickness = Math.Max((float)Math.Round(2f * HUD.lineScale * verticalScale), 1f);
    shadowOffset = new float2(0f, line_thickness);
}
```

5. **Save on demand.** The button writes the file next to the DLL; values use `CultureInfo.InvariantCulture` so locales do not break the format:

```csharp
// AircraftHUDMain.cs L924-L934
        ImGui.Dummy(new float2(20.0f, 8.0f));

        if (ImGui.Button("Save HUD Settings"))
        {
            string dllDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            SaveHudIni(Path.Combine(dllDir, "hud.ini"));
        }

        ImGui.End();
    }
}
```
```csharp
// AircraftHUDMain.cs L640-L658
static void SaveHudIni(string path)
{
    using var w = new StreamWriter(path);

    w.WriteLine("[HUD]");
    w.WriteLine($"Enabled={(HUD.enabled ? 1 : 0)}");
    w.WriteLine($"OrbitMode={(HUD.orbitMode ? 1 : 0)}");
    w.WriteLine($"EffectsOutsideIVA={(HUD.effectsOutsideIVA ? 1 : 0)}");
    w.WriteLine($"DistanceUnits={HUD.CurrentDistUnits.ToString()}");
    w.WriteLine($"SpeedUnits={HUD.CurrentSpeedUnits.ToString()}");
    w.WriteLine($"HUDColor={FormatColor(HUD.hudColorPicker)}");
    w.WriteLine($"HUDColor1={FormatColor(HUD.hudColor1Picker)}");
    w.WriteLine($"HUDColor2={FormatColor(HUD.hudColor2Picker)}");
    w.WriteLine($"ShadowColor={FormatColor(HUD.shadowColorPicker)}");
    w.WriteLine($"TextScale={HUD.textScale.ToString(CultureInfo.InvariantCulture)}");
    w.WriteLine($"LineScale={HUD.lineScale.ToString(CultureInfo.InvariantCulture)}");

    Console.WriteLine("AircraftHUD: Save Settings to " + path);
}
```

6. **Load on startup** with a line splitter and a `switch` on the key; unknown keys and malformed lines are skipped:

```csharp
// AircraftHUDMain.cs L576-L606
static void LoadHudIni(string path)
{
    if (!File.Exists(path))
    {  
        Console.WriteLine("AircraftHUD: file does not exist " + path);
        return;
    }
    Console.WriteLine("AircraftHUD: Load Settings from " + path);

    string[] lines = File.ReadAllLines(path);

    foreach (string line in lines)
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("["))
            continue;

        var parts = line.Split('=');
        if (parts.Length != 2)
            continue;

        string key = parts[0].Trim();
        string value = parts[1].Trim();

        switch (key)
        {
            case "Enabled":
                HUD.enabled = value == "1";
                break;
            case "OrbitMode":
                HUD.orbitMode = value == "1";
                break;
```

## Complete example

| File | Role |
|---|---|
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L153-L165) | ModMenu entry |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L557-L658) | Colour and INI parse and write |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L769-L934) | The settings window |
| [hud.ini](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/hud.ini#L1-L12) | Shipped defaults |

## Variations and gotchas

- **Writes into the install folder.** The file lives beside the DLL and the csproj copies it `Always`, so a mod update overwrites the player's settings (per the mod feature page); a per-user file or write-defaults-only-if-missing avoids it.
- **Parsing is fragile.** `float.Parse` throws on malformed values and a colour with fewer than four components throws; the exception escapes `Init`. Use `TryParse` with a fallback.
- **`MenuBar` flag without `BeginMenuBar`.** The window passes `ImGuiWindowFlags.MenuBar` but never calls `BeginMenuBar`; the two `BeginMenu` calls sit in the body, where ImGui draws them as sub-menu entries.
- **Unsaved state.** "Width" and the pitch-ladder sliders are not written to the INI; "Show Symbology" is not persisted either.
- **`Begin`'s return value is ignored**, with `End` always called after the widgets; widgets still run while collapsed. Prefer `if (Begin(...)) { ... } End();` in new code.
- **Fixed title.** The window title has no `###id`, so ImGui remembers its position under that name.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of build-a-settings-window](overview.md)
- [Approach 1: slider-plus-input rows and colour pickers](build-a-settings-window-1.md)
- [Approach 4: menu entry plus small settings window](build-a-settings-window-4.md)
- [Persist mod settings as TOML](../../../persistence/settings-formats/persist-mod-settings-toml/overview.md)
- [List a mod in ModMenu with the ModMenuEntry attribute](../../menus/list-a-mod-in-modmenu/overview.md)
- Mod feature: [INI settings and settings window](../../../../mods/ksa-aircrafthud/features/ini-settings-and-settings-window.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
