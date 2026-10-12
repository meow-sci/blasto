---
type: How-To
title: Take right-drag look through the controller with orbit-camera constants and IsMouseDrag to keep part windows shut
description: KSAStructures' FixedController subclass takes the right button only while its pose source exposes a look, copies KSA's orbit drag rules and answers IsMouseDrag and GetCursorMode so a look-drag never opens the part window under the cursor
category: camera
group: controllers
topic: replace-a-viewport-camera-controller
approach: 4
difficulty: intermediate
prerequisites: [starmap, reflection]
demonstrated_by: [mods/ksastructures/features/level-horizon-camera-controller]
techniques: [controller-subclass]
hooks:
  - subclass KSA.Controller.OnMouseButton
  - subclass KSA.Controller.OnCursorPos
  - subclass KSA.Controller.OnScroll
  - subclass KSA.Controller.IsMouseDrag
  - subclass KSA.Controller.GetCursorMode
lifecycle: [frame:input, frame:controller, frame:viewports]
game_types:
  - KSA.FixedController
  - KSA.Controller
  - KSA.OrbitController
  - KSA.Vehicle
  - KSA.Program
  - Brutal.GlfwApi.GlfwCursorMode
  - Brutal.GlfwApi.GlfwMouseButton
tags: [camera, input, mouse, orbit, controller, part-window]
---
The stock `FixedController` reads no input, so the player cannot look around a borrowed view. A
subclass can. KSAStructures lets the player hold the right mouse button to look around the first-person
kitten's view, copying the rules of KSA's orbit camera so it feels the same, and answers `IsMouseDrag`
so a drag that ends over a craft does not open that craft's part window. This article is the "Take input through the controller" half of the topic; the controller is installed as in
[approach 1](replace-a-viewport-camera-controller-1.md).

## Background: how the game does it

Mouse input goes to the active controller of the input viewport after the vehicles had a chance:

1. `Program.OnMouseButton` latches the hovered viewport as the input viewport on a press and returns
   early while ImGui wants the mouse on the main view ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2003-L2022)).
2. Each `Vehicle.OnMouseButton` handles a **right-button release**: if the active controller says
   `IsMouseDrag()` it returns false; otherwise it calls `CancelMouseDrag()` and opens the highlighted
   part's window ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3667-L3681)).
3. Only then does `InputViewport.GetActiveController().OnMouseButton` run ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2045)).

`OrbitController` is what the player already knows: a right press with no modifier or Shift arms a drag;
the drag starts once the squared distance from the press exceeds 2; each pixel turns the view by 0.003
rad; `GetCursorMode` returns `Disabled` while dragging ([current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L157-L188),
[current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L354-L398), [current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L702-L705)).

## Lifecycle and timing

`OnMouseButton`, `OnCursorPos` and `OnScroll` run in `frame:input`, from the GLFW callbacks during
`Glfw.PollEvents` in `PrepareFrame`. `GetCursorMode` is read by `Program.OnFrameController`
(`frame:controller`) to set the GLFW cursor mode. The look it feeds is consumed in `frame:viewports` when
the controller's `OnFrame` asks the pose source ([Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../write-the-camera-pose-in-phase-with-the-frame/overview.md)).

## Take input through the controller

1. **Own the right button only when someone is looking.** The controller asks its pose source for an
   `IMouseDrag` and handles the button only if there is one; otherwise it defers to `base`, so a
   borrowed view with no look leaves the engine's input alone:

```csharp
// src/KSAStructures/Ksa/LevelHorizonController.cs L74-L106
// looked around takes the right button and the wheel from the engine.
private IMouseDrag? Orbit => Pose?.Orbit;

public override bool OnMouseButton(GlfwWindow window, GlfwMouseButton button, GlfwButtonAction action,
                                   GlfwModifier mods)
{
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
    if (Orbit is not { } orbit) return base.OnCursorPos(window, pos);

    orbit.Move(pos.X, pos.Y);
    return orbit.Dragging;
}

public override bool OnScroll(GlfwWindow window, double2 offset)
{
    if (Orbit is not { } orbit) return base.OnScroll(window, offset);

    orbit.Scroll(offset.Y);
    return true;
}
```

2. **Mirror the orbit camera's chord.** Press arms the drag on no modifier or Shift (the orbit controller's
   rule), release ends it, and the method returns true so the right-click is consumed.

3. **Answer `IsMouseDrag` and `GetCursorMode`.** This is the load-bearing pair:

```csharp
// src/KSAStructures/Ksa/LevelHorizonController.cs L108-L115
// Load-bearing: the engine asks the active controller this before a right-release opens the
// window of whatever part is under the cursor, so a drag ending over a craft opens nothing.
public override bool IsMouseDrag() => Orbit?.Dragging == true || base.IsMouseDrag();

// Hidden and unbounded while dragging, as the orbit camera's is, so a drag does not stop at the
// edge of the screen.
public override GlfwCursorMode GetCursorMode()
    => Orbit?.Dragging == true ? GlfwCursorMode.Disabled : base.GetCursorMode();
```

4. **Copy the numbers.** `HeadLook` uses KSA's own constants so the look moves like the camera the player
   knows, and clamps pitch to 85 degrees:

```csharp
// src/KSAStructures/Sim/HeadLook.cs L11-L18
{
    /// <summary>What KSA's orbit camera turns per pixel dragged.</summary>
    public const double RadiansPerPixel = 0.003;

    /// <summary>A press becomes a drag past this squared distance, in pixels: KSA's own threshold.</summary>
    public const double DragStartsPastSquared = 2.0;
    public static readonly double MaxPitchRad = double.DegreesToRadians(85.0);
```

   The drag threshold compares the squared distance from the press (a press becomes a drag past 2, so
   about 1.4 px), the same test as `OrbitController` does at line 374.

## Complete example

- [src/KSAStructures/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/LevelHorizonController.cs): the controller
- [src/KSAStructures/Sim/HeadLook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/HeadLook.cs) and [src/KSAStructures/Sim/IMouseDrag.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/IMouseDrag.cs): the look and its contract
- [src/KSAStructures/Ksa/FirstPerson.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPerson.cs): the `IViewPose` that exposes the look

## Variations and gotchas

- **Releases over a panel are lost.** `Program.OnMouseButton` returns before any controller while ImGui
  wants the mouse on the main view, so a release made over a window never arrives and the drag sticks.
  End the drag from the controller's frame by also testing whether the button is still down; KSAGolf's
  `ChaseOrbit.Advance(seconds, buttonHeld)` does that ([approach 6](replace-a-viewport-camera-controller-6.md)); `HeadLook` has no such check.
- **Wheel zoom.** `OnScroll` returns true to consume it; the orbit camera steps its distance power by 1.1 per
  notch.
- **No part window after a drag.** If `IsMouseDrag()` is not overridden, a right-drag that ends over a craft
  opens that part's window.
- **Cursor mode.** Without `GetCursorMode` returning `Disabled` the cursor leaves the window mid-drag.

## See also

- [Topic overview](overview.md); [approach 1](replace-a-viewport-camera-controller-1.md), [5](replace-a-viewport-camera-controller-5.md), [6](replace-a-viewport-camera-controller-6.md)
- [A replacement fixed camera controller with an up vector and an in-phase pose](../../../../mods/ksastructures/features/level-horizon-camera-controller.md), [First-person view for a kitten on foot](../../../../mods/ksastructures/features/first-person-kitten-view.md)
- [Put the camera in an EVA kitten's eyes, with mouse look and look-driven walking](../../special-views/first-person-camera-for-a-kitten/overview.md), [Share the keyboard and mouse between ImGui, mod tools and the game](../../../ui/input/coordinate-imgui-focus-with-game-input/overview.md)
