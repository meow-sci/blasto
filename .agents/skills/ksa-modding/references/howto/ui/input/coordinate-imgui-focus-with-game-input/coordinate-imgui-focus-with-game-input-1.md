---
type: How-To
title: "Share input with the game: gate on WantTextInput and WantCaptureMouse in the GUI hook"
description: Read keys and mouse through ImGui from a StarMap GUI hook, gated so a text field or a window under the cursor wins, accepting that the tool stops when the HUD is hidden
category: ui
group: input
topic: coordinate-imgui-focus-with-game-input
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/minigolf-course-builder]
techniques: [imgui-window, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:input]
game_types: [Brutal.ImGuiApi.ImGuiIOPtr, KSA.Program, KSA.ImGuiBackendGlfwImpl, KSA.GameSettings]
tags: [input, imgui, focus, keyboard, mouse, wantcapture, hotkeys]
---
The simplest way for a mod tool to use the keyboard and mouse is to poll ImGui, not GLFW, and let ImGui say whether the input is
the tool's. KSAGolf's course builder reads its hotkeys with `ImGui.IsKeyPressed` and its mouse with `ImGui.IsMouseClicked`, gated on
`WantTextInput` and `WantCaptureMouse`. The price: it runs in the GUI hook, so it stops when the HUD is hidden. Use this for tools that
belong to the UI; use [approach 2](coordinate-imgui-focus-with-game-input-2.md) when the tool must keep working with the HUD hidden.
Demonstrated by KSAGolf.

## Background: how the game does it

- ImGui exposes who owns input through `ImGuiIOPtr`: `WantCaptureMouse` (an ImGui window is hovered or a drag started in one),
  `WantCaptureKeyboard` (a widget has keyboard focus or navigation owns keys) and `WantTextInput` (a text field is active).
  [current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs#L140-L144)[^brutal-imguiapi-imguiioptr] They are refreshed by `ImGui.NewFrame`, which `Program.PrepareImGui` calls after input polling.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2133-L2139)[^ksa-program]
- The game already honours them for its own handlers. `Program.OnKey` ends with `!ImGui.GetIO().WantCaptureKeyboard` before dispatching, and
  `GameSettings.OnKeyAll` swallows keys while `IsUserTyping` is set (stock text fields only). `Program.OnMouseButton` returns when
  `ImGui.GetIO().WantCaptureMouse && InputViewport.IsMain()`.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1754)[^ksa-program][current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2003-L2022)[^ksa-program][current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L3422-L3435)[^ksa-gamesettings]
- Events reach the game in two ways: `Program` registers `OnKey`/`OnMouseButton` on the main window, and the ImGui backend registers its own
  callbacks after (they also forward events from other windows when `ImGuiBackend.InputFallthrough` is set).[current/decomp/KSA/ImGuiBackendGlfwImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendGlfwImpl.cs#L431-L452)[^ksa-imguibackendglfwimpl]
  Because events are polled in `frame:input`, before `PrepareImGui`, the flags the game reads are the previous frame's.

## Lifecycle and timing

`KSAGolfMod.OnAfterGui` (`[StarMapAfterGui]`, `frame:draw-ui`) draws the panel first and then calls the builder's `Input`, so a click on a window is
the window's. The reads happen while ImGui's frame is open; after `ImGui.Render` the queries are meaningless, which is why `Input` is only called
from this hook and not from a step that may run from the frame postfix. Because StarMap's GUI hooks are skipped when `Program.DrawUI` is false,
the tool stops with the HUD hidden, which is the right behaviour for an editor. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Call your input method from the GUI hook, after drawing your own windows:

```csharp
// src/KSAGolf/Ksa/KSAGolfMod.cs L165-L189
public void OnAfterGui(double dt)
{
    if (_disabled || _ui is null) return;

    try
    {
        // First, so the drawing below reads the state this frame produced.
        StepOnce(dt);

        _ui.Draw();
        _golf?.Keys();
        if (KsaWorld.InFlight) _builder?.Input(dt);

        // After the panel, so a click on a window is not also a click on the world behind it.
        if (KsaWorld.InFlight)
        {
            _mover.Update(_config);
            _mover.Draw(_config);
        }
    }
    catch (Exception e)
    {
        Fault("gui", e);
    }
}
```

2. Poll through ImGui and gate each device on its own flag. Hotkeys fire only when no text field is active; mouse reads only when ImGui does not own
   the pointer. Use `repeat: false` for one event per press:

```csharp
// src/KSAGolf/Ksa/CourseBuilder.cs L222-L229,L235-L238,L242-L243,L252-L254,L267-L268,L273-L275
    if (!Active || _body is null) return;

    ImGuiIOPtr io = ImGui.GetIO();
    bool keys = !io.WantTextInput;
    bool mouse = !io.WantCaptureMouse;

    if (keys)
    {
// ...
        if (Pressed(ImGuiKey.Q)) _view.Turn(1);
        if (Pressed(ImGuiKey.E)) _view.Turn(-1);
        if (Pressed(ImGuiKey.R)) Rotate();
        if (Pressed(ImGuiKey.M)) Mirror();
// ...
        if (io.KeyCtrl && Pressed(ImGuiKey.Z)) Undo();
        if (io.KeyCtrl && Pressed(ImGuiKey.Y)) Redo();
// ...
    if (mouse)
    {
        if (io.MouseWheel != 0f) _view.Zoom(io.MouseWheel);
// ...
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left, repeat: false)) Click();
    }
// ...
private static bool Down(ImGuiKey key) => ImGui.IsKeyDown(key);

private static bool Pressed(ImGuiKey key) => ImGui.IsKeyPressed(key, repeat: false);
```

3. Keep per-event actions behind `IsKeyPressed(key, repeat: false)` or `IsMouseClicked(button, repeat: false)`, and held actions (pan, rotate)
   behind `IsKeyDown` and `IsMouseDown`.
4. If the game also reacts to the same keys (the kitten walks on WASD), take them away from the game as well; see
   [approach 5](coordinate-imgui-focus-with-game-input-5.md).

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/CourseBuilder.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseBuilder.cs#L214-L275) | `Input`, `Down`, `Pressed` |
| [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L165-L189) | The GUI hook that calls it |

## Variations and gotchas

- **ImGui key polling sees everything the game sees.** Gating on `WantTextInput` stops your hotkeys while the player types into any ImGui text field,
  including other mods'. It does not stop stock KSA bindings; for that see [block game hotkeys while typing](../block-game-hotkeys-while-typing/overview.md).
- **No HUD, no tool.** Under F2 the GUI hook does not run; use [approach 2](coordinate-imgui-focus-with-game-input-2.md)'s pattern if that is unacceptable.
- **Key names.** `ImGuiKey` is not `GlfwKey`; see [rebindable hotkeys](../rebindable-hotkeys/overview.md).
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of coordinate-imgui-focus-with-game-input](overview.md)
- [Approach 2: ignore clicks ImGui owns, keep working with the HUD hidden](coordinate-imgui-focus-with-game-input-2.md)
- [Approach 5: FixedController subclass swallows input](coordinate-imgui-focus-with-game-input-5.md)
- [Block game hotkeys while typing](../block-game-hotkeys-while-typing/overview.md)
- Mod feature: [Top-down minigolf course builder](../../../../mods/ksagolf/features/minigolf-course-builder.md)

[^brutal-imguiapi-imguiioptr]: Brutal.ImGuiApi.ImGuiIOPtr (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-imguibackendglfwimpl]: KSA.ImGuiBackendGlfwImpl (decompiled, build 2026.10.10.5554)
