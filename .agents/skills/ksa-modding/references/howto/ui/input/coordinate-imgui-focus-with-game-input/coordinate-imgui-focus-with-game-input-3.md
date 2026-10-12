---
type: How-To
title: "Share input with the game: NoNav and NoFocusOnAppearing windows that never take the keyboard"
description: Build a mod panel that cannot capture keyboard focus, so held flight keys keep working while the panel is open
category: ui
group: input
topic: coordinate-imgui-focus-with-game-input
approach: 3
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/car-panel-and-modmenu]
techniques: [imgui-window, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:input, frame:dispatch-solvers]
game_types: [Brutal.ImGuiApi.ImGuiWindowFlags, Brutal.ImGuiApi.ImGuiIOPtr, KSA.Program, KSA.Vehicle]
tags: [input, imgui, window-flags, nonav, focus, keyboard, panel]
---
KSACars drives a car with the player's ordinary flight keys (W/S throttle, A/D steering), and it shows a small panel with speed, gear and buttons.
That panel must never take the keyboard, because the moment an ImGui window has it KSA stops listening to the flight keys and clears the held ones, freezing the car
mid-turn. This article is the window setup that guarantees it. Demonstrated by KSACars.

## Background: how the game does it

- `Program.OnKey` drops the key event entirely when `ImGui.GetIO().WantCaptureKeyboard` is true:
  `... && !ConsoleWindow.IsOpen && !ImGui.GetIO().WantCaptureKeyboard` guards `DispatchKeyEvent`.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1754)[^ksa-program]
- `Vehicle.PrepareWorker` additionally clears the flown craft's **held** input (thruster flags, sprint, grab, engine flags) whenever the controlled vehicle is
  not active, `ImGui.GetIO().WantCaptureKeyboard` is true or the simulation speed is above 30x. So a window that grabs focus does not just miss new key presses; it
  releases the ones already down.[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2397-L2401)[^ksa-vehicle][current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6094-L6100)[^ksa-vehicle]
- The mod's rationale for its flags is that `NoFocusOnAppearing` (do not take focus when the window opens) and `NoNav` (= `NoNavInputs | NoNavFocus`, no
  keyboard or gamepad navigation) keep keyboard navigation off the window. They are plain `ImGuiWindowFlags` values.[current/decomp/Brutal.ImGuiApi/ImGuiWindowFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiWindowFlags.cs#L21-L29)[^brutal-imguiapi-imguiwindowflags]
  `WantCaptureKeyboard` itself is ImGui's own judgement (a text field active, or a window holding navigation focus).

## Lifecycle and timing

The panel is drawn from `[StarMapAfterGui]` (`frame:draw-ui`), inside the ImGui frame; `KSACarsMod.OnAfterGui` runs the frame's car step first, draws the panel, then
updates and draws the craft mover, so a click on the panel's checkbox belongs to the panel. The panel disappears with the rest of the UI when `Program.DrawUI` is false.
The key consequence runs elsewhere: key events are polled in `frame:input`, and `PrepareWorker` runs at `frame:dispatch-solvers`, both reading the flag ImGui computed
at the last `NewFrame`. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Define the window flags once, with the reason next to them:

```csharp
// src/KSACars/Ksa/CarPanel.cs L13-L16
// Neither takes the keyboard: KSA drops the flown craft's held keys while an ImGui window has it,
// and those keys are the throttle and the steering.
private const ImGuiWindowFlags Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse
                                       | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;
```

2. Pass them to every `Begin`, including the tiny reopen button. Use a stable id after `###` so the title can change without losing the window's position:

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
```csharp
// src/KSACars/Ksa/CarPanel.cs L108-L111
        if (ImGui.Button("Unflip", null)) Buggies.Right(craft);
    }
    ImGui.End();
}
```

3. Place it for the first run only (`ImGuiCond.FirstUseEver`) so the player can move it, relative to the main viewport's work area (the first lines of step 2).
4. Avoid widgets that grab focus. Buttons, radio buttons, checkboxes and sliders are fine with these flags; a text input will set `WantTextInput` and `WantCaptureKeyboard` while
   active, by design. Apply expensive slider changes on release:

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
| [src/KSACars/Ksa/CarPanel.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CarPanel.cs) | Panel, reopen button, ModMenu callback |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L88-L110) | The GUI hook: step, panel, mover |

## Variations and gotchas

- **Mouse still works.** `NoNav` removes navigation, not clicking; a click on the panel sets `WantCaptureMouse` as usual.
- **Any focused text field wins.** If a panel has to contain a text box, expect flight keys to stop while it is active and design for it.
- **A closed panel must not leave a focusable window behind.** The reopen button uses the same flags plus `NoTitleBar`.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of coordinate-imgui-focus-with-game-input](overview.md)
- [Approach 4: drive with flight keys](coordinate-imgui-focus-with-game-input-4.md)
- [Build a settings window](../../windows/build-a-settings-window/overview.md)
- [List a mod in ModMenu with the ModMenuEntry attribute](../../menus/list-a-mod-in-modmenu/overview.md)
- Mod feature: [Car panel and ModMenu entry](../../../../mods/ksacars/features/car-panel-and-modmenu.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imguiwindowflags]: Brutal.ImGuiApi.ImGuiWindowFlags (decompiled, build 2026.10.10.5554)
