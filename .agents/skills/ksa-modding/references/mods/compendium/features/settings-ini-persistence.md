---
type: Mod Feature
title: settings.ini persistence
description: A hand-rolled key=value INI next to the DLL stores whether the window is open and the pointer options, loaded first at immediate load and rewritten whenever a value changes, including a per-frame comparison that catches both the ModMenu toggle and the window's close button
mod: compendium
category: persistence
techniques: [ini-config, starmap-lifecycle]
hooks: [starmap StarMapImmediateLoad, starmap StarMapAfterGui]
lifecycle: [startup:immediate-load, frame:draw-ui]
game_types: []
tags: [settings, ini, persistence, config, imgui]
---

Compendium remembers across launches whether its window was open, and the pointer options (on or off,
one or two lines, rainbow, color). ImGui's own `imgui.ini` already keeps window position and size but
not whether a window is open, which is the gap this file fills.[^cmp-cdmsettings]

## How it works

1. **Location.** `settings.ini` sits in the mod folder, next to the DLL (`dllDir` from
   `Assembly.GetExecutingAssembly().Location`) ([src/CdmSettings.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmSettings.cs#L8-L16),
   [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1240-L1242)).[^cmp-compendiummain]
2. **Format.** Section headers are written for readability but ignored on read: every line with an `=`
   becomes a case-insensitive key and value ([src/settings.ini](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/settings.ini#L1-L9)).[^cmp-settings-ini]
3. **Load.** A missing file is created with the current defaults. Booleans use `bool.TryParse`, colors
   `float.TryParse` with the invariant culture and a clamp to 0..1, and each missing or malformed value
   keeps its default; a read error is logged and ignored ([src/CdmSettings.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmSettings.cs#L18-L59),
   [src/CdmSettings.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmSettings.cs#L100-L110)).
4. **Save.** `SaveSettings` rewrites the whole file with invariant-culture formatting and records the
   saved window state ([src/CdmSettings.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmSettings.cs#L61-L89)).
5. **Catch every way the flag changes.** The window flag is flipped by the ModMenu entry and by the
   window's own close button (`ImGui.Begin("Compendium", ref CompendiumWindow)`).
   `PersistWindowOpenState` runs at the top of every draw call and saves only when the flag differs from
   the last saved value ([src/CdmSettings.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmSettings.cs#L91-L98), [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L56-L60)). Pointer
   options call `SaveSettings` directly when changed ([src/CdmBodyPointer.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmBodyPointer.cs#L56-L125)).[^cmp-cdmbodypointer]
6. **Shipping defaults.** The csproj copies `settings.ini` to the output on every build and then to the
   mods folder, with a comment that each build resets the test copy so published files carry the
   defaults ([src/Compendium.csproj](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium.csproj#L31-L37), [src/Compendium.csproj](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium.csproj#L81-L84)).[^cmp-compendium-csproj]

## Lifecycle and timing

- **Load at `startup:immediate-load`.** `LoadSettings` is the first thing `[StarMapImmediateLoad]` does,
  before patching and before any UI, so the first frame already knows whether to draw the window
  ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1235-L1245)). No game state is needed.
- **Save at `frame:draw-ui`.** Writes happen on the main thread during the UI pass, synchronously, and
  only on change, so the per-frame cost is one boolean comparison.

## Key code

| Path | Role |
|---|---|
| [src/CdmSettings.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmSettings.cs#L18-L59) | Parse with defaults and clamping |
| [src/CdmSettings.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmSettings.cs#L61-L98) | Write and change detection |
| [src/settings.ini](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/settings.ini#L1-L9) | Shipped defaults |

## Snippets

```csharp
// src/CdmSettings.cs L18-L51
private static void LoadSettings()
{
    string? settingsPath = GetSettingsFilePath();
    if (settingsPath == null)
    {
        return;
    }

    if (!File.Exists(settingsPath))
    {
        SaveSettings();
        return;
    }

    try
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadAllLines(settingsPath))
        {
            int equalsIndex = line.IndexOf('=');
            if (equalsIndex > 0)
            {
                values[line[..equalsIndex].Trim()] = line[(equalsIndex + 1)..].Trim();
            }
        }

        CompendiumWindow = ReadBool(values, "ShowWindow", CompendiumWindow);
        showBodyPointer = ReadBool(values, "ShowPointer", showBodyPointer);
        pointerDualLines = ReadBool(values, "PointerDualLines", pointerDualLines);
        pointerRainbow = ReadBool(values, "PointerRainbow", pointerRainbow);
        pointerColor = new float3(
            ReadFloat(values, "PointerColorR", pointerColor.X),
            ReadFloat(values, "PointerColorG", pointerColor.Y),
            ReadFloat(values, "PointerColorB", pointerColor.Z));
```

```csharp
// src/CdmSettings.cs L91-L98
// Called every frame so both the mod menu toggle and the window's close (X) button get persisted.
private static void PersistWindowOpenState()
{
    if (CompendiumWindow != savedCompendiumWindow)
    {
        SaveSettings();
    }
}
```

## Game APIs touched

None. The feature uses only .NET file IO and the window flag passed to `ImGui.Begin`.

## Pitfalls and version notes

- **Updates can overwrite user settings.** The file lives in the mod folder and is part of the release,
  so installing a new version over the old one replaces the player's saved values with the defaults.
- **Install location may be read-only.** StarMap also loads mods from the game's `Content` folder; if the
  game is installed somewhere the user cannot write, saves fail and are only logged.
- **Most UI state is session-only.** Font face and size, opacity, selected system, orbit-line modes and
  colors, and root targets are not stored.
- **Synchronous IO in the frame.** Each change writes the file from the UI thread; fine for rare
  toggles, not for slider drags (the color picker saves on every change while dragging).

## See also

- [ModMenu entry that toggles the window](modmenu-window-toggle.md)
- [Pointer lines to the selected body](body-pointer-overlay.md)
- [Celestial compendium window](celestial-info-window.md)
- [Load settings first at StarMapImmediateLoad and rewrite on change](../../../howto/persistence/settings-formats/persist-mod-settings-ini/persist-mod-settings-ini-2.md) - how-to article demonstrating this feature
- [Keep settings beside the mod DLL, resolved from the loaded assembly](../../../howto/persistence/user-data/choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-1.md) - how-to article demonstrating this feature
- [Let imgui.ini restore window geometry with a plain ImGui.Begin and persist the open flag yourself](../../../howto/persistence/user-data/persist-imgui-window-state/persist-imgui-window-state-1.md) - how-to article demonstrating this feature

[^cmp-cdmsettings]: Compendium: src/CdmSettings.cs
[^cmp-compendiummain]: Compendium: src/CompendiumMain.cs
[^cmp-settings-ini]: Compendium: src/settings.ini
[^cmp-cdmbodypointer]: Compendium: src/CdmBodyPointer.cs
[^cmp-compendium-csproj]: Compendium: src/Compendium.csproj
