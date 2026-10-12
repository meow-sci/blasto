---
type: How-To
title: "Settings window: a developer panel opened from a menu, ModMenu or a floating button"
description: Build a closable control panel that always has a way back - a floating reopen button - and give it simulation-speed buttons that go below the game's own 0.1x floor through Universe.SetSimulationSpeed
category: ui
group: windows
topic: build-a-settings-window
approach: 6
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/developer-panel-and-menu-bar]
techniques: [imgui-window, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Universe, KSA.SimSpeed, Brutal.ImGuiApi.ImGui]
tags: [developer-panel, reopen-button, floating-button, simulation-speed, slow-motion, modmenu, ksagolf]
---
KSAGolf has one window that holds everything: putting controls, a course builder button, a world-clock row with slow-motion buttons, a craft mover, a log switch and a developer-only capture button. Two parts generalise to any mod panel: the "never strand the player" pattern (when the panel is closed, a tiny title-less window with one button reopens it, and ModMenu and a menu-bar entry are additional routes), and the slow-motion row, which sets the simulation speed below what the game's own control reaches. Demonstrated by KSAGolf. Prerequisite: StarMap.

## Background: how the game does it

- **Simulation speed is one static field.** `Universe.SetSimulationSpeed(SimSpeed speed)` just assigns `_simulationSpeed = speed.Value`;[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L700-L703)[^ksa-universe] there is no floor and no `MaxSpeed` check in this overload. The `simspeed` terminal command is a separate private method (`SetSimulationSpeedDirect`) that validates NaN, refuses while auto-warp is active and checks `SimSpeed.MaxSpeed`.[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L706-L725)[^ksa-universe]
- **`SimSpeed` names tiny values "paused".** Its constructor labels any `|value| < 1e-4` "paused" and any value within 1e-4 of 1.0 "realtime"; `Universe.IsPaused()` tests the speed against exactly zero, so a sub-1e-4 speed is labelled paused while the world still runs. That mismatch is why KSAGolf clamps its slowest speed to 0.001.[current/decomp/KSA/SimSpeed.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SimSpeed.cs#L25-L42)[^ksa-simspeed]
- **The menu route.** The game's own menu bar is the separate "Menu Bar" window; KSAGolf's menu-bar entry uses ImGui's own main menu bar instead (see [approach 4](build-a-settings-window-4.md)), skipped when ModMenu is installed.

## Lifecycle and timing

The panel is drawn from `[StarMapAfterGui]` (postfix on `Program.OnDrawUiViewports`, `frame:draw-ui`, inside `if (DrawUI)`), after the mod has stepped its simulation for the frame so the panel reads this frame's state. `Universe.SetSimulationSpeed` is a main-thread field write; the next physics dispatch reads it (per the mod feature page). See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Draw the panel, or the reopen button when it is closed.** `###KSAGolf` keeps the saved window position when the version in the title changes; the closed state draws a title-less auto-resize window with one button:

```csharp
// src/KSAGolf/Ksa/Ui/Ui.cs L88-L127
public void Draw()
{
    Current = this;
    if (_builder.Active) DrawBuilder();

    if (Visible)
    {
        // ###id so the version can ride in the title without the window losing its place.
        if (ImGui.Begin($"KSAGolf {Build.Version}###KSAGolf", ref Visible))
        {
            if (Build.Developer)
            {
                DrawCaptureForClaude();
                ImGui.Separator();
            }

            DrawGolf();
            ImGui.Separator();
            DrawWorldClock();
            ImGui.Separator();
            DrawCraftMover();
            DrawSendToBody();
            ImGui.Separator();
            DrawLogging();
        }

        ImGui.End();
    }
    else if (_config.FloatingPanelButton)
    {
        // Closing the panel must not strand the player: both other routes back are somebody
        // else's, and the setting that would switch this on lives inside the shut panel.
        if (ImGui.Begin("KSAGolf##reopen", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
        {
            if (ImGui.Button("KSAGolf")) Visible = true;
        }

        ImGui.End();
    }
}
```

2. **Add the other routes back.** A static `[ModMenuEntry]` method calls the instance's `DrawMenuContents`, guarded by one-time logging (ModMenu resolves instances only for a couple of hard-coded method names, per the mod's comment):

```csharp
// src/KSAGolf/Ksa/Ui/Ui.cs L65-L83
/// <summary>
/// Called by <b>ModMenu</b>, if the player has it, to fill this mod's entry in its shared menu.
/// Static because ModMenu resolves an instance only for a couple of hardcoded method names.
/// </summary>
[ModMenuEntry("KSAGolf")]
public static void DrawModMenu()
{
    try
    {
        Current?.DrawMenuContents();
    }
    catch (Exception e)
    {
        if (_warnedModMenu) return;
        _warnedModMenu = true;
        Log.Warn($"ModMenu entry failed, so its Panel item will not work: {e.Message}. "
                 + "Reopen from the floating KSAGolf button instead.");
    }
}
```

3. **Add slow-motion buttons** from a table of speeds, drawn on one line:

```csharp
// src/KSAGolf/Ksa/Ui/Ui.cs L230-L245
// KSA's own roller stops at 0.1x; these go two decades below.
private static readonly (string Label, double Speed)[] SlowMotionSpeeds =
[
    ("0.01x", 0.01), ("0.05x", 0.05), ("0.1x", 0.1), ("0.25x", 0.25), ("1x", 1.0),
];

private static void DrawWorldClock()
{
    ImGui.Text($"Sim speed: {KsaWorld.SimulationSpeed:0.###}x");

    foreach ((string label, double speed) in SlowMotionSpeeds)
    {
        ImGui.SameLine();
        if (ImGui.Button(label)) KsaWorld.SetSimulationSpeed(speed);
    }
}
```

4. **Set the speed through a guarded helper** that rejects non-finite or non-positive values and clamps to the slowest meaningful speed:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L143-L167
public const double SlowestSimSpeed = 0.001;

/// <summary>Whether KSA is running its own warp-to-a-time, which it will not be interrupted during.</summary>
public static bool IsAutoWarpActive => Universe.IsAutoWarpActive;

/// <summary>
/// Sets the world's simulation speed, including values slower than the in-game controls
/// reach. KSA's own roller works in tenths, so 0.1x is as slow as it will go; nothing in
/// the engine enforces that.
///
/// <para><c>SetSimulationSpeed</c> only rejects speeds above <c>SimSpeed.MaxSpeed</c> — there
/// is no floor — and it assigns the field directly rather than queuing an input event, so a
/// value set here holds until something else changes it.</para>
///
/// <para>Everything this mod does is already keyed to simulated time, so a slow world needs
/// no special handling: the step simply comes back smaller and everything scales with it.</para>
/// </summary>
/// <returns>False if the value was not finite or not positive; the speed is left alone.</returns>
public static bool SetSimulationSpeed(double speed)
{
    if (!double.IsFinite(speed) || speed <= 0.0) return false;

    Universe.SetSimulationSpeed(new SimSpeed(Math.Max(speed, SlowestSimSpeed)));
    return true;
}
```

5. **Pause separately.** A caller wanting to stop the world uses `SetPaused` ([src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L169-L192)), which sets exactly 0 or 1; mixing pause and slow-down into one speed argument is the trap the helper guards.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Ui/Ui.cs#L27-L127) | Menu-bar entry, ModMenu callback, panel, reopen button |
| [src/KSAGolf/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Ui/Ui.cs#L230-L245) | Slow-motion row |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L135-L167) | Simulation-speed helper |

## Variations and gotchas

- **The floating button is a config option** (`FloatingPanelButton`, default true), and the setting lives inside the shut panel; the mod keeps it on by default for that reason.
- **The speed is a global field.** Nothing resets it when your mod unloads, so restore it if your mod owns the change (general advice, not something the mod does).
- **Menu bar claim.** The mod's comments say its menu-bar entry sits beside File and Universe; per the decomp KSA never calls `BeginMainMenuBar`, so it lands in ImGui's own bar (see approach 4).
- **Version.** Checked against 2026.10.10.5554; `SimSpeed` and `Universe.SetSimulationSpeed` are public.

## See also

- [Overview of build-a-settings-window](overview.md)
- [Approach 4: small settings window with wrapped tooltips](build-a-settings-window-4.md)
- [List a mod in ModMenu with the ModMenuEntry attribute](../../menus/list-a-mod-in-modmenu/overview.md)
- [Add a menu to the game's top menu bar](../../menus/add-items-to-the-game-menu-bar/overview.md)
- Mod feature: [Developer panel and menu bar](../../../../mods/ksagolf/features/developer-panel-and-menu-bar.md)

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-simspeed]: KSA.SimSpeed (decompiled, build 2026.10.10.5554)
