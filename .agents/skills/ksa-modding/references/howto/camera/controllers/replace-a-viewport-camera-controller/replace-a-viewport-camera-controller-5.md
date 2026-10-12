---
type: How-To
title: Route orbit drag, club swing and builder input through the controller and keep ImGui-captured releases from sticking a drag
description: KSAGolf's FixedController subclass distinguishes a looked-around view, a swing that owns the mouse and a builder that takes the whole keyboard and mouse, and shows where a release is lost to ImGui
category: camera
group: controllers
topic: replace-a-viewport-camera-controller
approach: 5
difficulty: advanced
prerequisites: [starmap, reflection]
demonstrated_by: [mods/ksagolf/features/level-horizon-camera-controller]
techniques: [controller-subclass]
hooks:
  - subclass KSA.Controller.OnKey
  - subclass KSA.Controller.OnMouseButton
  - subclass KSA.Controller.OnCursorPos
  - subclass KSA.Controller.OnScroll
lifecycle: [frame:input, frame:controller, frame:viewports]
game_types:
  - KSA.FixedController
  - KSA.Controller
  - KSA.Program
  - KSA.Vehicle
  - Brutal.GlfwApi.GlfwCursorMode
  - Brutal.GlfwApi.GlfwMouseButton
tags: [camera, input, mouse, keyboard, controller, imgui, swing]
---
A replacement controller receives every camera input callback, which makes it the right place to decide
who gets the mouse while a mod holds the view. KSAGolf's subclass handles three cases through the same
class: a view the player looks around with the right button, a **swing** that takes the mouse whole to
move a club, and a course builder that takes the keyboard and mouse from the game altogether. This article is the "Take input through the controller" half of the topic; the install is in
[approach 1](replace-a-viewport-camera-controller-1.md).

## Background: how the game does it

The dispatch order is in [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1775) (keys) and [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2003-L2096) (mouse).
Keys reach the controller only after `GameSettings.OnKeyAll`, the popup, the console and ImGui
(`WantCaptureKeyboard`) had their say, and after `Camera.OnKey` and the mode key; mouse buttons are
refused first by ImGui when `WantCaptureMouse` is set on the main view. The cursor mode is the active
controller's `GetCursorMode()`, applied each frame by `Program.OnFrameController` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2520-L2552)).

## Lifecycle and timing

All callbacks are `frame:input`; the cursor-mode read is `frame:controller`. The swing and builder
modes are switched on by the borrower's `IViewPose` (`Swing`, `TakesInput`, `Orbit`), set from the step in the GUI
hook (`frame:draw-ui`) and read by the controller each callback ([src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs#L295-L346)).

## Take input through the controller

1. **Ask the view what it wants.** Three properties are queried from the pose source rather than
   stored, so only a borrower that needs input takes it from the engine:

```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L74-L84
// Asked of the pose source rather than held here, so only a borrower that lets its view be
// looked around takes the right button and the wheel from the engine.
private IMouseDrag? Orbit => Pose?.Orbit;

// A view with a swing takes the mouse whole: it moves the club, and a click would reach through
// to whatever part is under the hidden cursor.
private IMouseSwing? Swinging => Pose?.Swing;

// A view that takes the input is read through ImGui, which sees every key and button before the engine
// does, so swallowing here costs it nothing and keeps the kitten from walking off.
private bool TakesInput => Pose?.TakesInput == true;
```

2. **Keys.** While a view takes input the controller swallows letters, digits, Space and the editing keys so the
   kitten does not walk off; Escape and the function keys still reach the game:

```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L86-L97
public override bool OnKey(GlfwKeyEvent keyEvent)
{
    if (TakesInput && !keyEvent.IsMouse && keyEvent.Action != GlfwKeyAction.Release && Swallowed(keyEvent.Key)) return true;

    return base.OnKey(keyEvent);
}

// Letters, digits, the arrows and the editing keys: what walks a kitten and what the builder uses.
// Escape and the function keys still reach the game.
private static bool Swallowed(GlfwKey key)
    => key is >= GlfwKey.A and <= GlfwKey.Z or >= GlfwKey.Number0 and <= GlfwKey.Number9 or GlfwKey.Space
           or >= GlfwKey.Insert and <= GlfwKey.PageDown;
```

3. **Buttons, cursor, wheel.** Builder mode swallows every button; swing mode swallows the left button and
   forwards cursor positions; a looked-around view arms the drag on the right button as the orbit camera does:

```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L99-L138
public override bool OnMouseButton(GlfwWindow window, GlfwMouseButton button, GlfwButtonAction action,
                                   GlfwModifier mods)
{
    if (TakesInput) return true;
    if (Swinging is not null && button == GlfwMouseButton.Number1) return true;

    if (Orbit is not { } orbit || button != GlfwMouseButton.Number2)
    {
        return base.OnMouseButton(window, button, action, mods);
    }

    // The modifiers KSA's orbit camera starts a drag on, so a chord it leaves alone stays alone.
    if (action == GlfwButtonAction.Press && (mods == 0 || (mods & GlfwModifier.Shift) != 0)) orbit.Press();
    else if (action == GlfwButtonAction.Release) orbit.Release();

    return true;
}

public override bool OnCursorPos(GlfwWindow window, double2 pos)
{
    if (Swinging is { } swing)
    {
        swing.Cursor(pos.X, pos.Y);
        return true;
    }

    if (Orbit is not { } orbit) return base.OnCursorPos(window, pos);

    orbit.Move(pos.X, pos.Y);
    return orbit.Dragging;
}

public override bool OnScroll(GlfwWindow window, double2 offset)
{
    if (TakesInput) return true;
    if (Orbit is not { } orbit) return base.OnScroll(window, offset);

    orbit.Scroll(offset.Y);
    return true;
}
```

4. **Report the state the engine asks about.** `IsMouseDrag` keeps a part window from opening after a drag
   or while the builder holds input; `GetCursorMode` returns `Disabled` for a swing or a drag:

```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L140-L147
// Load-bearing: the engine asks the active controller this before a right-release opens the
// window of whatever part is under the cursor, so a drag ending over a craft opens nothing.
public override bool IsMouseDrag() => TakesInput || Orbit?.Dragging == true || base.IsMouseDrag();

// Hidden and unbounded while dragging, as the orbit camera's is, so a drag does not stop at the
// edge of the screen.
public override GlfwCursorMode GetCursorMode()
    => Swinging is not null || Orbit?.Dragging == true ? GlfwCursorMode.Disabled : base.GetCursorMode();
```

## Complete example

[src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs#L39-L191) is the controller; [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs) implements the swing
(`IMouseSwing`) and [src/KSAGolf/Ksa/CourseBuilder.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseBuilder.cs) implements `TakesInput`.

## Variations and gotchas

- **ImGui-captured releases.** `Program.OnMouseButton` returns before any controller while ImGui wants the
  mouse on the main view, so a release over a panel is dropped and a drag would stick. The mod's fix is
  structural: a drag must be endable from the controller's own frame by checking whether the button is still
  held (`ChaseOrbit.Advance(seconds, buttonHeld)`, [approach 6](replace-a-viewport-camera-controller-6.md)). At the pinned
  ref KSAGolf's `ChaseOrbit` has no in-game caller and its `HeadLook` has no such check ([Level-horizon camera controller swapped into the main viewport](../../../../mods/ksagolf/features/level-horizon-camera-controller.md)); KSArmory's chase wires it:
  `_orbit.Advance(dtViewing, ImGui.IsMouseDown(ImGuiMouseButton.Right))` ([src/KSArmory/Ksa/ChaseCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ChaseCamera.cs#L335-L342)).
- **Swing owns the pointer.** While a swing is held the left button is swallowed so a click does not reach whatever part is under the hidden cursor.
- **Keys are read elsewhere.** A view that takes input reads it through ImGui, which sees every key before the engine does,
  so swallowing in the controller costs it nothing.
- **Do not swallow Escape.** The player needs a way out; the mod leaves Escape and the function keys alone.

## See also

- [Topic overview](overview.md); [approach 1](replace-a-viewport-camera-controller-1.md), [4](replace-a-viewport-camera-controller-4.md), [6](replace-a-viewport-camera-controller-6.md)
- [Level-horizon camera controller swapped into the main viewport](../../../../mods/ksagolf/features/level-horizon-camera-controller.md)
- [Share the keyboard and mouse between ImGui, mod tools and the game](../../../ui/input/coordinate-imgui-focus-with-game-input/overview.md), [Block game hotkeys while typing into an ImGui field](../../../ui/input/block-game-hotkeys-while-typing/overview.md)
