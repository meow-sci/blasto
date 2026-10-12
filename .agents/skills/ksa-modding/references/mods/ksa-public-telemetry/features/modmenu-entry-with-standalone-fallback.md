---
type: Mod Feature
title: ModMenu entry with a standalone fallback window
description: Declares its own copy of ModMenu's ModMenuEntry attribute on a static settings method so ModMenu can list it under Mods without a dependency, and draws the same settings tree as a hotkey-toggled ImGui window when ModMenu is absent, switching BeginMenu for CollapsingHeader
mod: ksa-public-telemetry
category: ui
techniques: [imgui-menu, imgui-window, inter-mod-api]
hooks: [starmap StarMapAfterGui, modmenu ModMenuEntry Telemetry Overlay]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui]
tags: [modmenu, settings, imgui, menu, optional-dependency]
---

With [ModMenu](https://github.com/MrJeranimo/ModMenu) installed, the settings appear under
**Mods > Telemetry Overlay** as nested menus. Without it, numpad `/` opens a window with the same
controls laid out as collapsible sections.[^kpt-readme] The mod has no ModMenu reference and no
`mod.toml` dependency on it.

## How it works

1. **Vendored attribute.** `ModMenuEntryAttribute(string menuName, string? isModMenuActivePropertyName)`
   is declared in the mod's own namespace ([ModMenuEntry.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/ModMenuEntry.cs#L1-L14)). ModMenu's source is not in
   this corpus; KSACars documents the same trick and states that ModMenu finds the attribute by its type
   name alone ([src/KSACars/Ksa/ModMenuEntry.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/ModMenuEntry.cs#L3-L10)), so a local copy is enough (unverified
   here).[^ksacars-modmenuentry]
2. **Entry point.** `[ModMenuEntry("Telemetry Overlay", nameof(ModMenuActive))]` on the static
   `SettingsUi.DrawSettingsMenu` ([Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs#L54-L64)). The second argument names the
   static `ModMenuActive` property; presumably ModMenu sets it to `true` when it adopts the entry (not
   verifiable without ModMenu's source). The fallback window uses it only to decide whether to show the
   "Install ModMenu" hint.
3. **One content function, two containers.** `DrawContent(config, inMenu)` builds every section through
   `Category(label, inMenu)`, which is `ImGui.BeginMenu` inside ModMenu and `ImGui.CollapsingHeader` in
   the window, and `EndCategory`, which only calls `EndMenu` for menus. Colour editors become a nested
   `BeginMenu("Colours")` in the menu and a `SeparatorText` in the window
   ([Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs#L91-L269)).
4. **Fallback window.** `DrawFallbackWindow` is a plain `ImGui.Begin("Telemetry Overlay", ref _windowOpen, AlwaysAutoResize)`
   drawn from the overlay draw (only when the game UI is visible), toggled by the settings hotkey
   ([Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs#L66-L89), [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L122-L126),
   [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L186-L189)).
5. **What is in it.** Mission name (`ImInputString`), countdown, enable and HUD-replacement toggles,
   element visibility, readout slot editors (1 to 4 per side, any `ReadoutKind`), speed reference,
   arc full scales, per-event callout mutes, scale/opacity/smoothing, intro/outro, 18 colour pickers,
   key bindings, freeze threshold, window toggles, and an Advanced section with the tuning window and
   the config path. Every change sets `changed`, which marks the config dirty; slot edits also bump
   `config.Revision` so the renderer rebuilds its panels ([Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs#L102-L269),
   [Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs#L499-L549), [Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs#L59-L64)).

## Lifecycle and timing

- **ModMenu path:** called whenever ModMenu draws its menu, inside the UI phase (`frame:draw-ui`, exact
  hook is ModMenu's and unverified here).
- **Fallback path:** drawn from `[StarMapAfterGui]` in `DrawEverything`, after the overlay, only when not
  drawing from the F2 hidden-UI postfix.
- **Writes are plain field stores** into the shared `OverlayConfig`; the sampler and renderer read them on
  their next run, and `ConfigStore.Flush` saves one second after the last change.

## Key code

| Path | Role |
|---|---|
| [ModMenuEntry.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/ModMenuEntry.cs#L1-L14) | Vendored attribute |
| [Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs#L54-L100) | Menu entry, fallback window, container switch |
| [Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs#L102-L269) | Shared settings content |

## Snippets

```csharp
// Rendering/SettingsUi.cs L54-L92
    [ModMenuEntry("Telemetry Overlay", nameof(ModMenuActive))]
    public static void DrawSettingsMenu()
    {
        if (_config is null)
        {
            ImGui.TextDisabled("Overlay is still starting up."u8);
            return;
        }

        DrawContent(_config, inMenu: true);
    }

    public static void DrawFallbackWindow()
    {
        if (_config is null || !_windowOpen)
        {
            return;
        }

        ImGui.SetNextWindowSize(new float2(360f, 0f), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Telemetry Overlay"u8, ref _windowOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!ModMenuActive)
            {
                ImGui.TextDisabled("Install ModMenu to move these settings into a nice dedicated Mods menu."u8);
                ImGui.Separator();
            }

            DrawContent(_config, inMenu: false);
        }

        ImGui.End();
    }

    public static void ToggleWindow() => _windowOpen = !_windowOpen;

    private static bool Category(ImString label, bool inMenu)
        => inMenu ? ImGui.BeginMenu(label) : ImGui.CollapsingHeader(label);
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ImGui.BeginMenu` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7429) | Section as a submenu inside ModMenu |
| `ImGui.CollapsingHeader` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7260) | Section in the fallback window |
| `ImGui.Begin(name, ref open, flags)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5584) | Fallback window with a close button |
| `ImGui.ColorEdit3` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7110) | Palette pickers |

## Pitfalls and version notes

- **ModMenu behaviour is outside the corpus.** Discovery by attribute name and the meaning of
  `IsModMenuActivePropertyName` come from other mods' comments and naming, not from ModMenu's code.
- **Menus close on click-away.** Inside ModMenu the sections are nested `BeginMenu` popups, so a click
  outside closes the whole tree; the window variant (numpad `/`) stays available with ModMenu
  installed and is the more comfortable place for long edits.
- **Both can be open** at once; they edit the same config object, so there is no sync problem.

## See also

- [Rebindable numpad hotkeys](rebindable-numpad-hotkeys.md)
- [JSON config in the KSA documents folder](json-config-in-documents.md)
- [Live tuning window and palette by reflection](live-tuning-and-palette-reflection.md)
- Other mods: [AircraftHUD vendored ModMenu attributes](../../ksa-aircrafthud/features/modmenu-entry-vendored-attributes.md),
  [KSACars car panel and ModMenu](../../ksacars/features/car-panel-and-modmenu.md)
- [Render one settings tree as a ModMenu menu or as a hotkey-toggled window](../../../howto/ui/menus/add-a-menu-with-modmenu-fallback/add-a-menu-with-modmenu-fallback-3.md) - how-to article demonstrating this feature
- [Copy the ModMenuEntry attribute into your mod so there is no dependency and no extra DLL](../../../howto/ui/menus/list-a-mod-in-modmenu/list-a-mod-in-modmenu-3.md) - how-to article demonstrating this feature

[^kpt-readme]: README.md
[^ksacars-modmenuentry]: KSACars src/KSACars/Ksa/ModMenuEntry.cs
