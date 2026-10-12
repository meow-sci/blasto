---
type: How-To
title: "Share input with the game: a FixedController subclass swallows OnKey, OnMouseButton and OnScroll"
description: Take keys, clicks and the wheel away from the game while a mod tool is active by subclassing the camera controller the engine asks before the vehicle
category: ui
group: input
topic: coordinate-imgui-focus-with-game-input
approach: 5
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/minigolf-course-builder]
techniques: [reflection, imgui-window, starmap-lifecycle]
hooks: [reflection KSA.GameViewport.<FixedController>k__BackingField]
lifecycle: [frame:input, frame:viewports]
game_types: [KSA.Controller, KSA.FixedController, KSA.GameViewport, KSA.Program, KSA.Vehicle]
tags: [input, controller, swallow, camera, fixed-controller, tool-mode]
---
While KSAGolf's course builder is open, the kitten must not walk off when you press WASD, and a click must not open a part's window. The builder reads the same keys
through ImGui ([approach 1](coordinate-imgui-focus-with-game-input-1.md)), so the job here is the other half: make the game stop reacting. The mod does it without a single Harmony patch
by installing its own subclass of the camera controller the engine asks before the controlled vehicle, and returning `true` from the overrides. Demonstrated by KSAGolf.

## Background: how the game does it

- **Keys.** `Program.DispatchKeyEvent` asks the editor, then the camera, then `inputViewport.GetActiveController().OnKey(keyEvent)`, and only afterwards the rest of its own
  handling, which includes the controlled vehicle. A controller that returns `true` ends the dispatch.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1756-L1776)[^ksa-program] (`GameSettings.OnKeyAll`, `Popup.OnKeyAll` and the console
  run earlier, in `Program.OnKey`; a swallow in the controller does not beat them.)
- **Mouse buttons.** `Program.OnMouseButton` runs popups, the ImGui-capture check, bound mouse inputs, the editor, then **each `Vehicle.OnMouseButton` before the active controller**, then
  the controller, then the orbits.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2003-L2060)[^ksa-program]
- **Scroll.** `Program.OnScroll` asks the editor, then `InputViewport.GetActiveController()?.OnScroll`.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2116-L2121)[^ksa-program]
- **Part window.** `Vehicle.OnMouseButton` handles the right-button release: if `Program.InputViewport.GetActiveController().IsMouseDrag()` is true it returns false and does nothing,
  otherwise it cancels the drag and, if a part is highlighted, marks it clicked (which opens its window).[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3667-L3685)[^ksa-vehicle]
- **The base class.** `Controller` has virtual `OnKey`, `OnMouseButton`, `OnCursorPos`, `OnScroll`, `IsMouseDrag`, `CancelMouseDrag` and `GetCursorMode`; `FixedController` is public and unsealed
  with a public constructor.[current/decomp/KSA/Controller.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Controller.cs#L8-L95)[^ksa-controller][current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L5-L45)[^ksa-fixedcontroller] The viewport's active controller for `CameraMode.Fixed` is its `FixedController`,
  whose property has a protected setter.[current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L42)[^ksa-gameviewport][current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L288-L300)[^ksa-gameviewport]

## Lifecycle and timing

The overrides run during `Glfw.PollEvents` in `PrepareFrame` (`frame:input`), when `Program.OnKey`, `OnMouseButton` and `OnScroll` fire; the controller's `OnFrame` runs in
`frame:viewports`. The builder's own ImGui reads happen later, in the GUI hook (`frame:draw-ui`), so nothing is lost by swallowing: ImGui's backend gets every key and button for itself.
`TakesInput` is read live, so the swallow ends the same frame the tool does. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Subclass the controller and decide when to swallow.** The tool exposes a flag (`IViewPose.TakesInput`); the controller reads it live:

```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L82-L91
// A view that takes the input is read through ImGui, which sees every key and button before the engine
// does, so swallowing here costs it nothing and keeps the kitten from walking off.
private bool TakesInput => Pose?.TakesInput == true;

public override bool OnKey(GlfwKeyEvent keyEvent)
{
    if (TakesInput && !keyEvent.IsMouse && keyEvent.Action != GlfwKeyAction.Release && Swallowed(keyEvent.Key)) return true;

    return base.OnKey(keyEvent);
}
```

2. **Swallow keys.** Presses and repeats only, never releases, so a key held when the tool starts is still released in the game; letters, digits, Space and the editing keys
   (Insert through PageDown). Escape and the function keys still reach the game:

```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L93-L97
// Letters, digits, the arrows and the editing keys: what walks a kitten and what the builder uses.
// Escape and the function keys still reach the game.
private static bool Swallowed(GlfwKey key)
    => key is >= GlfwKey.A and <= GlfwKey.Z or >= GlfwKey.Number0 and <= GlfwKey.Number9 or GlfwKey.Space
           or >= GlfwKey.Insert and <= GlfwKey.PageDown;
```

3. **Swallow clicks and the wheel.** Return `true` for every button and for scroll while the tool is active:

```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L99-L103
public override bool OnMouseButton(GlfwWindow window, GlfwMouseButton button, GlfwButtonAction action,
                                   GlfwModifier mods)
{
    if (TakesInput) return true;
    if (Swinging is not null && button == GlfwMouseButton.Number1) return true;
```
```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L131-L137
public override bool OnScroll(GlfwWindow window, double2 offset)
{
    if (TakesInput) return true;
    if (Orbit is not { } orbit) return base.OnScroll(window, offset);

    orbit.Scroll(offset.Y);
    return true;
```

4. **Defeat the vehicle's part window.** The vehicle is asked before the controller, so returning `true` from `OnMouseButton` is too late for the right-button release. Answer
   `IsMouseDrag()` with `true`, which makes `Vehicle.OnMouseButton` ignore the release:

```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L140-L142
// Load-bearing: the engine asks the active controller this before a right-release opens the
// window of whatever part is under the cursor, so a drag ending over a craft opens nothing.
public override bool IsMouseDrag() => TakesInput || Orbit?.Dragging == true || base.IsMouseDrag();
```

5. **Hide or free the cursor if the tool needs it** by overriding `GetCursorMode()`, and clear held input when the tool starts (`kitten.ClearHeldPlayerInput()`):

```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L144-L147
// Hidden and unbounded while dragging, as the orbit camera's is, so a drag does not stop at the
// edge of the screen.
public override GlfwCursorMode GetCursorMode()
    => Swinging is not null || Orbit?.Dragging == true ? GlfwCursorMode.Disabled : base.GetCursorMode();
```

6. **Install the subclass.** `GameViewport.FixedController`'s setter is protected, so KSAGolf writes the compiler backing field `"<FixedController>k__BackingField"` by reflection, remembers the
   stock controller to put back at unload, verifies the write took, and degrades to the stock controller with a warning if it did not. The install and the camera work are
   shown in [the grid builder article](../place-pieces-on-a-grid-with-a-ghost/place-pieces-on-a-grid-with-a-ghost-1.md).

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs#L82-L147) | The swallowing overrides |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1734-L1766) | Installing the subclass (`LevelTheHorizon`) |
| [src/KSAGolf/Ksa/CourseBuilder.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseBuilder.cs#L84) | `TakesInput => Active` |

## Variations and gotchas

- **Swallowing is not total.** `GameSettings.OnKeyAll` and `Popup.OnKeyAll` see every key before the controller, and every `Vehicle.OnMouseButton` is asked before the controller's: a global binding on
  a swallowed key still fires. For typing see [block game hotkeys while typing](../block-game-hotkeys-while-typing/overview.md).
- **Release keys pass through**, deliberately: the controller swallows presses and repeats so the game's held state is not left stuck.
- **Only works in `Fixed` mode.** The controller is only asked while its mode is active (`GetActiveController()`); the tool must hold the viewport in `Fixed` and hand it back on every exit
  path, because `NextCameraMode` returns false for `Fixed` and the player's camera-mode key cannot leave it.
- **Private backing field.** Rename-sensitive; the mod warns and falls back.
- **Version.** The `Program.cs` diff to 2026.10.10.5554 does not touch `OnKey`, `DispatchKeyEvent`, `OnMouseButton` or `OnScroll`.

## See also

- [Overview of coordinate-imgui-focus-with-game-input](overview.md)
- [Approach 1: gate on WantTextInput and WantCaptureMouse in the GUI hook](coordinate-imgui-focus-with-game-input-1.md)
- [Place pieces on a grid with a snapping ghost](../place-pieces-on-a-grid-with-a-ghost/overview.md)
- [Replace a viewport's camera controller and take input through it](../../../camera/controllers/replace-a-viewport-camera-controller/overview.md)
- Mod feature: [Top-down minigolf course builder](../../../../mods/ksagolf/features/minigolf-course-builder.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-controller]: KSA.Controller (decompiled, build 2026.10.10.5554)
[^ksa-fixedcontroller]: KSA.FixedController (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
