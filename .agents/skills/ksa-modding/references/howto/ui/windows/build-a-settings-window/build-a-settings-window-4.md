---
type: How-To
title: "Settings window: a menu entry, a small auto-sized window and wrapped tooltips"
description: Offer a few settings in a small AlwaysAutoResize window with a version-proof id, wrapped hover tooltips, a keyboard-safe flag set and expensive settings applied only when the slider is released
category: ui
group: windows
topic: build-a-settings-window
approach: 4
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/ksastructures/features/settings-menu
  - mods/ksacars/features/car-panel-and-modmenu
  - mods/ksagolf/features/developer-panel-and-menu-bar
techniques: [imgui-window, starmap-lifecycle]
hooks: [starmap StarMapBeforeGui, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImGuiWindowFlags]
tags: [settings, tooltip, auto-resize, no-nav, stable-id, slider, modmenu, ksastructures, ksacars]
---
Three small mods converged on the same recipe for a handful of settings: the values are public fields of a `Settings` object bound to widgets by `ref`, the window is small and auto-sized, every control has a wrapped tooltip, and anything expensive is applied when the player lets go of a slider. KSAStructures shows the plain version (menu entry plus a three-checkbox window); KSACars adds the flags that keep a panel from stealing the keyboard and the apply-on-release slider. Demonstrated by KSAStructures, KSACars and KSAGolf. Prerequisite: StarMap.

## Background: how the game does it

- **The game's menu bar is not ImGui's main menu bar.** `Program.DrawMenuBar` draws a separate window named "Menu Bar" (`NoTitleBar | NoResize | ... | MenuBar`) and fills it with `BeginMenuBar`/`BeginMenu("File")` and so on; `Program.DrawProgramMenusHook()` is an empty public method called inside the bar, which is the hook mods postfix to add a menu there.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3480-L3486)[^ksa-program] (hook: [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4018-L4020)) KSA never calls `ImGui.BeginMainMenuBar`, so a mod that calls it gets Dear ImGui's own separate strip, not a place in "File / Universe" (the mods' comments claim otherwise; this is checked against the decomp, not in game).
- **Window flags that matter.** `NoCollapse`, `NoFocusOnAppearing` and `NoNav` (= `NoNavInputs | NoNavFocus`) are the flags that keep a window from taking keyboard focus.[current/decomp/Brutal.ImGuiApi/ImGuiWindowFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiWindowFlags.cs#L14-L29)[^brutal-imguiapi-imguiwindowflags] The game drops key events while `ImGui.GetIO().WantCaptureKeyboard` is true, and `Vehicle.PrepareWorker` clears the flown craft's held keys under the same condition, so a focusable settings window freezes a car mid-turn (per the KSACars feature page; see [coordinate ImGui focus with game input](../../input/coordinate-imgui-focus-with-game-input/overview.md)).

## Lifecycle and timing

Both StarMap GUI hooks sit inside `if (DrawUI)` between `PrepareImGui` and `ImGui.Render`, so these windows hide with the HUD. KSAStructures opens its menu in `[StarMapBeforeGui]` (Harmony prefix on `Program.OnDrawUiFrame`) and its window in `[StarMapAfterGui]` (postfix on `Program.OnDrawUiViewports`), after the mod has stepped its own simulation so the window reads this frame's state. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Keep the values as public fields** of a plain settings object so ImGui binds them with `ref` (they last for the session here; persistence is a separate step).

2. **Open the window with a `###id`** so a version string can ride in the visible title without the window losing its saved place, and call `End` unconditionally:

```csharp
// src/KSAStructures/Ksa/Ui/Ui.cs L79-L109
public void Draw()
{
    Current = this;
    if (!Visible) return;

    // ###id so the version can ride in the title without the window losing its place.
    if (ImGui.Begin($"{Title} {Build.Version}###KSAStructures", ref Visible, ImGuiWindowFlags.AlwaysAutoResize))
    {
        ImGui.Checkbox("First-person kitten (V)", ref settings.FirstPerson);
        Tip("See through the eyes of a kitten on foot. Hold the right mouse button and drag to look around.");

        ImGui.Checkbox("Office garage lamps", ref settings.OfficeLamps);
        Tip("The ceiling lamps under the office tower. Off, the garage has only what daylight reaches it.");

        ImGui.Separator();
        ImGui.TextDisabled("In the lift: 1 garage, 2 lobby, 3 office.");
        ImGui.TextDisabled("In the office chair: Enter plays the computer.");
        ImGui.Separator();

        if (ImGui.Checkbox("Verbose log", ref settings.VerboseLog))
        {
            Log.Threshold = settings.VerboseLog ? Log.Level.Debug : Log.Level.Info;
            Log.Info(settings.VerboseLog ? "verbose logging on" : "verbose logging off");
        }

        Tip("More detail in the log, which is what a bug report wants.");
        ImGui.TextDisabled("  -> Logs/KSAStructures.log");
    }

    ImGui.End();
}
```

3. **Add a wrapped tooltip helper** after each control; a bare tooltip never wraps. 35 em is the width Dear ImGui's own help markers use:

```csharp
// src/KSAStructures/Ksa/Ui/Ui.cs L111-L123
// The width Dear ImGui's own help markers wrap at.
private const float TipWidthEms = 35f;

// What the control just drawn does, on hover. Wrapped, because a bare tooltip never is.
private static void Tip(string text)
{
    if (!ImGui.BeginItemTooltip()) return;

    ImGui.PushTextWrapPos(ImGui.GetFontSize() * TipWidthEms);
    ImGui.TextWrapped(text);
    ImGui.PopTextWrapPos();
    ImGui.EndTooltip();
}
```

4. **Offer a menu entry.** Draw one `MenuItem` per setting bound by `ref`, plus one that toggles the window. The mod puts this in ImGui's own main menu bar (`BeginMainMenuBar` / `BeginMenu`), with exceptions swallowed once because the call runs inside KSA's GUI pass; a mod that wants an entry inside KSA's own bar postfixes `Program.DrawProgramMenusHook`:

```csharp
// src/KSAStructures/Ksa/Ui/Ui.cs L19-L51
public void DrawMenuBarEntry()
{
    // ModMenu draws this mod's entry when it is installed; a second would list it twice.
    if (ModMenuPresence.Installed) return;

    try
    {
        if (!ImGui.BeginMainMenuBar()) return;

        if (ImGui.BeginMenu(Title))
        {
            DrawMenuContents();
            ImGui.EndMenu();
        }

        ImGui.EndMainMenuBar();
    }
    catch (Exception e)
    {
        // Inside KSA's own GUI pass; a missing menu is only cosmetic.
        if (_warnedMenuBar) return;

        _warnedMenuBar = true;
        Log.Warn($"menu bar entry failed; V and the lift keys still work: {e.Message}");
    }
}

private void DrawMenuContents()
{
    ImGui.MenuItem("First-person kitten (V)", default, ref settings.FirstPerson, true);
    ImGui.MenuItem("Office garage lamps", default, ref settings.OfficeLamps, true);
    ImGui.MenuItem("Settings window", default, ref Visible, true);
}
```

5. **For a panel that must not take the keyboard**, use `AlwaysAutoResize | NoCollapse | NoFocusOnAppearing | NoNav`, a `FirstUseEver` position taken from the main viewport's work area, and a `###id` title with a `ref Visible` close flag:

```csharp
// src/KSACars/Ksa/CarPanel.cs L13-L16
// Neither takes the keyboard: KSA drops the flown craft's held keys while an ImGui window has it,
// and those keys are the throttle and the steering.
private const ImGuiWindowFlags Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse
                                       | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;
```
```csharp
// src/KSACars/Ksa/CarPanel.cs L46-L65
public static void Draw(CraftMover mover)
{
    if (KsaWorld.ControlledVehicle is not { } craft || Buggies.Of(craft) is not { } car) return;

    ImGuiViewportPtr screen = ImGui.GetMainViewport();
    float2 at = new(screen.WorkPos.X + 16f, screen.WorkPos.Y + (screen.WorkSize.Y * 0.5f));
    ImGui.SetNextWindowPos(in at, ImGuiCond.FirstUseEver, null);

    if (!Visible)
    {
        if (ImGui.Begin("Fast & Purrious##reopen", Flags | ImGuiWindowFlags.NoTitleBar))
        {
            if (ImGui.SmallButton("F&P")) Visible = true;
        }
        ImGui.End();
        return;
    }

    if (ImGui.Begin("Fast & Purrious###Car", ref Visible, Flags))
    {
```

6. **Apply expensive settings on release.** Hold the slider value in a field while dragging and write it to the game only when the edit ends, because each change makes KSA rebuild its rocks:

```csharp
// src/KSACars/Ksa/CarPanel.cs L89-L96
// Held here while it is dragged and applied on release: every change makes KSA
// rebuild its rocks.
_rockWeight ??= (float)(KsaWorld.RockWeight * 100.0);
float weight = _rockWeight.Value;
ImGui.SliderFloat("Rock weight", ref weight, 0.01f, 100f, "%.2f%%", ImGuiSliderFlags.Logarithmic);
_rockWeight = weight;
if (ImGui.IsItemDeactivatedAfterEdit()) KsaWorld.RockWeight = weight / 100.0;
if (!ImGui.IsItemActive()) _rockWeight = (float)(KsaWorld.RockWeight * 100.0);
```

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Ui/Ui.cs#L5-L124) | Menu entry, ModMenu callback, window, tooltip |
| [src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L142-L160) | The two GUI hooks |
| [src/KSACars/Ksa/CarPanel.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CarPanel.cs#L13-L111) | Keyboard-safe panel, reopen button, release-apply slider |

## Variations and gotchas

- **Closing must not strand the player.** KSACars leaves a tiny `F&P` button when its panel is closed; see [approach 6](build-a-settings-window-6.md) for that pattern.
- **ModMenu without a dependency.** Each mod declares an identical internal `ModMenuEntryAttribute` and puts it on a static method; ModMenu matches by type name according to the mods (its source is not in this corpus; unverified). Skip your own menu entry when ModMenu is present so you are not listed twice.
- **Swallow exceptions inside menu callbacks** and log once; they run inside someone else's menu build.
- **Settings are not saved.** All three mods reset each session; see the related persistence topics for TOML or INI.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of build-a-settings-window](overview.md)
- [Approach 3: ColorButton popups and INI persistence](build-a-settings-window-3.md)
- [Approach 6: developer panel with a floating reopen button](build-a-settings-window-6.md)
- [Block game hotkeys while typing](../../input/block-game-hotkeys-while-typing/overview.md)
- [List a mod in ModMenu with the ModMenuEntry attribute](../../menus/list-a-mod-in-modmenu/overview.md)
- [Add a menu to the game's top menu bar](../../menus/add-items-to-the-game-menu-bar/overview.md)
- [Persist mod settings as TOML](../../../persistence/settings-formats/persist-mod-settings-toml/overview.md)
- Mod features: [KSAStructures settings menu](../../../../mods/ksastructures/features/settings-menu.md), [KSACars car panel and ModMenu](../../../../mods/ksacars/features/car-panel-and-modmenu.md), [KSAGolf developer panel](../../../../mods/ksagolf/features/developer-panel-and-menu-bar.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imguiwindowflags]: Brutal.ImGuiApi.ImGuiWindowFlags (decompiled, build 2026.10.10.5554)
