---
type: Mod Feature
title: INI settings file and settings window
description: Keeps HUD options in a hand-parsed hud.ini next to the mod DLL, loads it at StarMapImmediateLoad, and edits it from an ImGui settings window drawn in StarMapBeforeGui with toggles, unit menus, color pickers, scale sliders and an explicit save button
mod: ksa-aircrafthud
category: persistence
techniques: [imgui-window, imgui-menu, starmap-lifecycle, ini-config]
hooks: [starmap StarMapImmediateLoad, starmap StarMapBeforeGui]
lifecycle: [startup:immediate-load, frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui]
tags: [settings, ini, persistence, imgui, color-picker, slider, starmap]
---

Settings live in an eleven-key INI file shipped with the mod and rewritten in place when the player
presses "Save HUD Settings". There is no INI library: a line splitter, a `switch` on the key, and
`CultureInfo.InvariantCulture` for numbers and RGBA colors. The settings window exposes everything in
the file plus a few session-only layout sliders.[^ah-main] [^ah-ini]

## How it works

**File.** [hud.ini](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/hud.ini) has a `[HUD]` section with `Enabled`, `OrbitMode`,
`EffectsOutsideIVA` (0/1), `DistanceUnits` (`Metric|Imperial`), `SpeedUnits`
(`Metric|Imperial|Knots`), four colors as `r,g,b,a` floats (`HUDColor`, `HUDColor1`, `HUDColor2`,
`ShadowColor`), `TextScale` and `LineScale`. The csproj copies it to the output with
`CopyToOutputDirectory=Always`.[^ah-csproj]

**Load.** `LoadHudIni(path)` skips blank lines and section headers, splits on `=`, trims, and
assigns fields; enums go through `Enum.TryParse`, floats and colors through invariant-culture
`float.Parse` ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L557-L638)). `Init` calls it with
`<dll folder>/hud.ini`, then converts colors with `ImGui.ColorConvertFloat4ToU32` and computes
font sizes and line thickness from the loaded scales
([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L677-L699)).

**Save.** `SaveHudIni(path)` rewrites the whole file with a `StreamWriter`, same key order, invariant
culture ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L640-L658)). Only the button calls it.

**Window.** `DrawHudMenu` draws `ImGui.Begin("HUD Settings", ref settingsPageOn, MenuBar)` when
toggled from the ModMenu entry ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L769-L934)):

| Control | Effect |
|---|---|
| Buttons | Enable/disable HUD, orbit Surface/Body (subtract `MeanRadius` from Ap/Pe), show/hide effects outside IVA, show/hide symbology labels |
| `BeginMenu("Distance Units")`, `BeginMenu("Speed Units")` | Unit enums, as sub-menus in the window body |
| `ColorButton` + `OpenPopup` + `ColorPicker4` | Four colors; the packed `uint` is recomputed while the popup is open |
| `SliderFloat` Text Scale, Line Scale | Recompute font sizes or line thickness and shadow offset on change |
| `SliderFloat` Width, Pitch Ladder W1/W2 | Recompute text positions, clip rect and ladder widths (not saved) |
| Save HUD Settings | `SaveHudIni` |

The window is created with `ImGuiWindowFlags.MenuBar`, but the code never calls `BeginMenuBar`, so
the flag only reserves an empty menu-bar strip; the two `BeginMenu` calls sit in the body, where Dear
ImGui draws them as sub-menu entries (library behaviour). Nothing goes into the game's own menu bar,
which is a separate `Menu Bar` window that `Program.DrawMenuBar` fills between `BeginMenuBar` and
`EndMenuBar`
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3455-L3487)).[^ksa-program]

## Lifecycle and timing

- **`startup:immediate-load`.** `LoadHudIni` runs at the start of `Init`, from StarMap's prefix on
  `Mod.PrepareSystems` ([StarMap.Core/Patches/ModPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModPatches.cs#L11-L22)),
  before the layout and font size are derived from the values.[^starmap-modpatches]
- **`frame:draw-ui`.** `[StarMapBeforeGui]` is a Harmony prefix on `Program.OnDrawUiFrame`
  ([StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L14-L24)),
  which `Program.OnFrame` only calls under `if (DrawUI)`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2269)). The settings
  window hides with the game UI while the HUD (drawn earlier, outside that check) does not.[^starmap-programpatcher]
  [^ksa-program]
- **File I/O** happens synchronously on the main thread at load and on the button press.

## Key code

| Path | Role |
|---|---|
| [hud.ini](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/hud.ini) | Defaults shipped with the mod |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L557-L574) | `ParseColor` / `FormatColor` |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L576-L658) | `LoadHudIni` / `SaveHudIni` |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L769-L934) | Settings window |

## Snippets

```csharp
// AircraftHUDMain.cs L585-L605
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
```

```csharp
// AircraftHUDMain.cs L852-L857
                if (ImGui.BeginPopup("hudColorPicker"))
                {
                    ImGui.ColorPicker4("##hudColorPicker", ref hudColorPicker);
                    HUD.color = ImGui.ColorConvertFloat4ToU32(hudColorPicker);
                    ImGui.EndPopup();
                }
```

## Game APIs touched

Only ImGui widgets (`Begin`, `Button`, `BeginMenu`, `MenuItem`, `ColorButton`, `OpenPopup`,
`BeginPopup`, `ColorPicker4`, `SliderFloat`, `ColorConvertFloat4ToU32`) from `Brutal.ImGuiApi`. No
KSA settings APIs are used.

## Pitfalls and version notes

- **Writes into the install folder.** The file sits next to the DLL in the mods folder; a mod update
  that ships a new `hud.ini` (the csproj marks it `Always`) overwrites the player's settings. A
  per-user file outside the mod folder, or "write defaults only if missing", avoids that.
- **Parsing is fragile.** `float.Parse` throws on a malformed value, and a color line with fewer
  than four components throws `IndexOutOfRangeException`. The exception escapes `Init`; StarMap's
  `PrepareSystems` prefix invokes `Init` by reflection without a `try`/`catch`
  ([StarMap.Core/Patches/ModPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModPatches.cs#L18-L21)),
  so one bad line stops the game during mod loading. `TryParse` with a fallback is safer.
- **Unsaved state.** "Width" and the pitch ladder sliders are not written to the INI, and the
  "Show Symbology" toggle is not persisted.
- **Line scale drift.** `Init` multiplies the current `line_thickness` field (default 2) by the
  scale, while the slider recomputes from the constant `2f`; both give the same result only because
  `Init` runs once.
- The settings window title uses a fixed label without `###id`, so ImGui remembers its position under
  that name.

## See also

- [ModMenu entry with a vendored attributes DLL](modmenu-entry-vendored-attributes.md)
- [Custom TTF font in the ImGui atlas](custom-hud-font.md)
- [Surface attitude and air data from the controlled vehicle](flight-data-from-vehicle.md)
- [Save an eleven-key hud.ini only when the player presses a button](../../../howto/persistence/settings-formats/persist-mod-settings-ini/persist-mod-settings-ini-3.md) - how-to article demonstrating this feature
- [Keep settings beside the mod DLL, resolved from the loaded assembly](../../../howto/persistence/user-data/choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-1.md) - how-to article demonstrating this feature
- [Settings window: ColorButton popups and a hand-parsed INI file](../../../howto/ui/windows/build-a-settings-window/build-a-settings-window-3.md) - how-to article demonstrating this feature

[^ah-main]: KSA_AircraftHUD/AircraftHUDMain.cs
[^ah-ini]: KSA_AircraftHUD/hud.ini
[^ah-csproj]: KSA_AircraftHUD/AircraftHUD.csproj
[^starmap-programpatcher]: StarMap ProgramPatcher.cs
[^starmap-modpatches]: StarMap ModPatches.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
