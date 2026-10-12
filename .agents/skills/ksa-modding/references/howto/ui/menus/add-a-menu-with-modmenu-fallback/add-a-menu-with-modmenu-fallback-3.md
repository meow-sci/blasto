---
type: How-To
title: Render one settings tree as a ModMenu menu or as a hotkey-toggled window
description: Draw the same settings content either under ModMenu's entry with BeginMenu or in a standalone window with CollapsingHeader by switching one flag, so both can coexist without a patch on the game's menu bar
category: ui
group: menus
topic: add-a-menu-with-modmenu-fallback
approach: 3
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/modmenu-entry-with-standalone-fallback]
techniques: [imgui-menu, imgui-window, inter-mod-api]
hooks: [modmenu ModMenuEntry, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImString]
tags: [modmenu, fallback, settings, beginmenu, collapsingheader, hotkey-window]
---
KSA-Public-Telemetry has a long settings tree: mission name, element toggles, readout slots, 18 colour pickers, key bindings and more. With ModMenu installed it appears as nested menus under **Mods > Telemetry Overlay**; without ModMenu the numpad `/` hotkey opens a window with the same controls as collapsible sections. There is no patch on the game's menu bar at all; the fallback is simply a window the player can always open. One content function serves both containers: a flag decides whether a "category" is `BeginMenu` or `CollapsingHeader`. Prerequisites: a StarMap mod and a per-frame hook (`[StarMapAfterGui]`). Demonstrated by [ModMenu entry with a standalone fallback](../../../../mods/ksa-public-telemetry/features/modmenu-entry-with-standalone-fallback.md).

## Background: how the game does it

Nothing engine-specific beyond Dear ImGui as exposed by the game: `ImGui.BeginMenu` / `ImGui.EndMenu` ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7429-L7437)),[^ksa-brutal-imguiapi-imgui] `ImGui.CollapsingHeader` and `ImGui.Begin(name, ref open, flags)`. The one wrinkle is that `EndMenu` is required only after a `BeginMenu` that returned true, while `CollapsingHeader` has no end call, which the `EndCategory` helper hides. ModMenu's behaviour (it calls your tagged method inside its own menu build) is mod-reported and unverified.

The mod does not reference `ModMenu.Attributes`: it declares its own public `ModMenuEntryAttribute` with the two-argument constructor, relying on ModMenu matching by type name (mod claim, unverified):

```csharp
// ModMenuEntry.cs L1-L14
namespace KSATelemetryOverlay;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ModMenuEntryAttribute : Attribute
{
    public string MenuName { get; }
    public string? IsModMenuActivePropertyName { get; }

    public ModMenuEntryAttribute(string menuName, string? isModMenuActivePropertyName = null)
    {
        MenuName = menuName;
        IsModMenuActivePropertyName = isModMenuActivePropertyName;
    }
}
```

## Lifecycle and timing

- **Menu path (`frame:draw-ui`).** `DrawSettingsMenu` is called by ModMenu whenever it draws its menu (exact hook unverified).
- **Window path (`frame:draw-ui`).** `DrawFallbackWindow` is called from the mod's `[StarMapAfterGui]` draw, only when the game UI is visible. The hotkey is handled in the same hook with `ImGui.IsKeyPressed`, guarded by `WantTextInput` and a key-capture flag so typing in a field does not toggle it.
- **Writes** are plain field stores into one shared `OverlayConfig`; the renderer reads them on its next run and the config is flushed shortly after the last change. Both containers can be open at once without a sync problem since they edit the same object.
- See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Decide the container with a flag, not with two code paths.** `Category` returns whether a section is open, as a menu or as a collapsing header; `EndCategory` closes menus only:

```csharp
// Rendering/SettingsUi.cs L91-L100
    private static bool Category(ImString label, bool inMenu)
        => inMenu ? ImGui.BeginMenu(label) : ImGui.CollapsingHeader(label);

    private static void EndCategory(bool inMenu)
    {
        if (inMenu)
        {
            ImGui.EndMenu();
        }
    }
```

2. **Write the content once**, passing `inMenu` down. Every section is `if (Category("Name"u8, inMenu)) { ...widgets...; EndCategory(inMenu); }` and every widget sets `changed`:

```csharp
// Rendering/SettingsUi.cs L134-L147
        if (Category("Elements"u8, inMenu))
        {
            changed |= ImGui.Checkbox("Readouts"u8, ref config.ShowTelemetryBar);
            changed |= ImGui.Checkbox("Engine diagram"u8, ref config.ShowEngineDiagram);
            ImGui.SetNextItemWidth(160f);
            changed |= ImGui.SliderFloat("Diagram rotation"u8, ref config.EngineDiagramRotation,
                OverlayConfig.MinEngineRotation, OverlayConfig.MaxEngineRotation, "%.0f deg"u8);
            changed |= ImGui.Checkbox("Propellant arc"u8, ref config.ShowPropellants);
            changed |= ImGui.Checkbox("Mission clock"u8, ref config.ShowMissionClock);
            changed |= ImGui.Checkbox("Timeline"u8, ref config.ShowTimeline);
            changed |= ImGui.Checkbox("Event Notifications"u8, ref config.ShowNotifications);
            changed |= ImGui.Checkbox("Backdrop band"u8, ref config.ShowBackdrop);
            EndCategory(inMenu);
        }
```

3. **Two entry points over the same content.** The ModMenu one passes `inMenu: true`; the window one begins a normal ImGui window with an open flag (so the title-bar X closes it) and passes `inMenu: false`. The window also shows a hint when ModMenu is not driving the entry, using the active-flag property from approach 2:

```csharp
// Rendering/SettingsUi.cs L54-L89
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
```

4. **Toggle the window from a rebindable hotkey** inside the mod's per-frame hook. Ignore the press while ImGui wants text input:

```csharp
// TelemetryOverlayMod.cs L173-L189
    private static void HandleHotkeys(OverlayConfig config)
    {
        if (ImGui.GetIO().WantTextInput || SettingsUi.IsCapturingKey)
        {
            return;
        }

        if (ImGui.IsKeyPressed(config.ToggleKey, repeat: false))
        {
            config.Enabled = !config.Enabled;
            ConfigStore.MarkDirty();
        }

        if (ImGui.IsKeyPressed(config.SettingsKey, repeat: false))
        {
            SettingsUi.ToggleWindow();
        }
// ...
```

5. **Call the window draw every frame** from `[StarMapAfterGui]`, after the overlay, only when the game UI is visible:

```csharp
// TelemetryOverlayMod.cs L122-L126
            if (!gameUiHidden)
            {
                SettingsUi.DrawFallbackWindow();
                TuningUi.Draw();
            }
```

6. **Test both ways.** Press the settings key: the window opens with collapsible sections. With ModMenu installed, the same sections appear as nested menus. Edit a value in one and see it in the other.

## Complete example

| File | Role |
|---|---|
| [ModMenuEntry.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/ModMenuEntry.cs#L1-L14) | Attribute copy |
| [Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs#L54-L100) | Both entry points and the container switch |
| [Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs#L102-L269) | Shared content |
| [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L122-L126) | Window draw call and [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L173-L189) |

## Variations and gotchas

- **Menus close on click-away.** Inside ModMenu the sections are nested `BeginMenu` popups, so a click outside closes the whole tree; for long edits the window is the more comfortable place, and it stays available with ModMenu installed.
- **Only while the UI is visible.** The settings window is drawn only on visible-HUD frames (`DrawEverything(dt, gameUiHidden: false)` from `[StarMapAfterGui]`); on hidden-HUD frames the mod redraws through its `DrawFps` postfix with `gameUiHidden: true`, which skips the window, so the settings hotkey still toggles the flag (`HandleHotkeys` runs on both paths) but nothing is shown until F2 brings the HUD back.
- **No patch to maintain.** Unlike approaches 1 and 2 this survives a game update that removes `DrawProgramMenusHook`, at the cost of not having a menu in the game's bar when ModMenu is absent. Combine with the postfix from [approach 1](add-a-menu-with-modmenu-fallback-1.md) if you want both.
- **Per-frame cost.** Content functions build large trees every frame; keep allocations out (`"..."u8` literals, no string formatting per frame).
- **Typed text** in the window (the mission name) needs an `ImInputString` buffer kept between frames; see [Read typed text and key chords from ImGui](../../input/read-typed-text-and-key-chords-from-imgui/overview.md).

## See also

- [Overview](overview.md)
- [Approach 1: decide once with ModLibrary.Find](add-a-menu-with-modmenu-fallback-1.md)
- [Approach 2: ModMenuEntry with an active-flag property name](add-a-menu-with-modmenu-fallback-2.md)
- [Rebindable hotkeys](../../input/rebindable-hotkeys/overview.md)
- [Build a mod settings window (and developer panel)](../../windows/build-a-settings-window/overview.md)
- Mod feature: [ModMenu entry with a standalone fallback](../../../../mods/ksa-public-telemetry/features/modmenu-entry-with-standalone-fallback.md)

[^ksa-brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
