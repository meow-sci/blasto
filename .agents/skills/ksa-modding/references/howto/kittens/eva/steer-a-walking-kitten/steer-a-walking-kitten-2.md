---
type: How-To
title: Capture the cursor and drive a kitten action with raw mouse deltas
description: KSAGolf's borrowed camera controller takes the mouse whole while a swing is possible, hides and unbinds the cursor, ignores the first jump after capture, converts horizontal pixels to a club angle with a clamp and strikes only when the leading face crosses the ball
category: kittens
group: eva
topic: steer-a-walking-kitten
approach: 2
difficulty: advanced
prerequisites: [starmap, imgui]
demonstrated_by: [mods/ksagolf/features/putting-session, mods/ksagolf/features/level-horizon-camera-controller]
techniques: [mouse-capture]
hooks: [starmap StarMapAfterGui, controller-subclass KSA.FixedController.OnCursorPos]
lifecycle: [frame:input, frame:viewports, frame:draw-ui, frame:render, frame:after-on-frame]
game_types:
  - KSA.FixedController
  - KSA.Program
  - KSA.KittenEva
  - Brutal.ImGuiApi.ImGui
  - Brutal.GlfwApi.GlfwWindowAttribute
tags: [kitten, mouse, capture, swing, putter, input]
---
KSAGolf lets the player swing a putter with the mouse: move it away from the target to take the club back, then towards it to strike. The pattern is more general: **a mod that wants raw mouse deltas for a kitten action takes the mouse through a camera controller it borrowed, hides and unbinds the cursor, accumulates pixels in the input phase, and applies the action in a later step**, so the input handler never touches the simulation. This article walks through that machinery and the pixel-to-action conversion. Prerequisites: a borrowed main-view controller (see [replace a viewport camera controller](../../../camera/controllers/replace-a-viewport-camera-controller/overview.md)) and ImGui key reads ([coordinate ImGui focus](../../../ui/input/coordinate-imgui-focus-with-game-input/overview.md)).

## Background: how the game does it

- GLFW callbacks fire during `Glfw.PollEvents` in `Program.PrepareFrame` and route to the **active controller** of the input viewport; a `FixedController` subclass overrides `OnKey`, `OnMouseButton`, `OnCursorPos`, `OnScroll`, `IsMouseDrag` and `GetCursorMode` ([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1756-L1800)). `GetCursorMode` returning `GlfwCursorMode.Disabled` hides the cursor and gives unbounded movement.
- `Program.OnFrameController` syncs the GLFW cursor mode and the ImGui `NoMouse` flag with the active controller after the UI ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2290-L2330)).
- `Program.IsWindowOpen` is a static that KSA windows set true while they draw in the UI pass and `OnDrawUiConsole` resets to false at the end of every frame's UI ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L318), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3128)).
- Keys read through ImGui (`ImGui.IsKeyPressed`) are seen only between `NewFrame` and `Render`, in the GUI hooks.

## Lifecycle and timing

| Work | Hook | Lifecycle |
|---|---|---|
| G and V read with `ImGui.IsKeyPressed` | `Golf.Keys` from `[StarMapAfterGui]` | `frame:draw-ui` |
| Mouse position into `_dx` | `LevelHorizonController.OnCursorPos` -> `IMouseSwing.Cursor`, during `Glfw.PollEvents` | `frame:input` |
| The view is posed | `LevelHorizonController.OnFrame` in `OnFrameViewports` | `frame:viewports` |
| Angle and strike | `Golf.Swinging(dtPlayer)` from the mod's step | `frame:draw-ui` |
| Club drawn | pose processor `UpdateSkeleton` | `frame:render` |
| Bridge-scripted swing | `Nudge` once per frame | `frame:after-on-frame` |

The mouse arrives earlier in the frame than the UI pass and is only **accumulated** there; the angle and the strike are applied in the step, so the input handler never touches the ball or the pose. The stroke runs on **player** time (it is the player's hand); the ball it launches runs on **simulated** time ([game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#input-timing)).

## Steps

1. **Read the toggle keys with ImGui, skipping text input.** G steps up to the ball or away, V toggles first person:

```csharp
// src/KSAGolf/Ksa/Golf.cs L233-L239
    /// <summary>The keys, from the GUI pass: ImGui is what sees them.</summary>
    public void Keys()
    {
        if (!_config.PlayGolf || _suspended || ImGui.GetIO().WantTextInput) return;
        if (ImGui.IsKeyPressed(ImGuiKey.G, repeat: false)) AddressOrStepAway();
        if (ImGui.IsKeyPressed(ImGuiKey.V, repeat: false)) _config.FirstPerson = !_config.FirstPerson;
    }
```

2. **Offer the mouse only when it can be used.** `Swing` is non-null while the view is borrowed, the game window is focused, and the player is addressing the ball (or looking in first person with no Alt/window open). The controller asks the pose source:

```csharp
// src/KSAGolf/Ksa/Golf.cs L97-L104
    public IMouseDrag? Orbit => _borrowed && _phase == Phase.Carrying && _config.FirstPerson && !CursorWanted ? _look : null;

    // The mouse is taken to swing at address, and to look in first person -- never while the game is not
    // the window in front, and in first person not while Alt is held or a KSA window is open, which give
    // the cursor back so the panel can still be reached.
    public IMouseSwing? Swing => _borrowed && Focused && (Addressing || (_config.FirstPerson && !CursorWanted)) ? this : null;

    private static bool CursorWanted => ImGui.GetIO().KeyAlt || Program.IsWindowOpen;
```

3. **Take the mouse whole in the controller:** swallow the left button so a click does not reach a part under the hidden cursor, hand every cursor position to the swing, and ask for `Disabled` cursor mode so it can move without bound:

```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L99-L129, L146-L147
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
// ...
    public override GlfwCursorMode GetCursorMode()
        => Swinging is not null || Orbit?.Dragging == true ? GlfwCursorMode.Disabled : base.GetCursorMode();
```

4. **Ignore the first jump after capture.** When the cursor is captured, the first position reported jumps to wherever the capture put it; deltas over 400 px are discarded, and the pixel delta is stored as the accumulating `_dx` (or, in first person, turns the look):

```csharp
// src/KSAGolf/Ksa/Golf.cs L428-L439
    public void Cursor(double x, double y)
    {
        // The first position after the cursor is captured jumps to wherever the capture put it.
        if (_lastX is { } lastX && _lastY is { } lastY && Math.Abs(x - lastX) < 400.0 && Math.Abs(y - lastY) < 400.0)
        {
            if (Addressing) _dx += x - lastX;
            else _look.Turn(x - lastX, y - lastY);
        }

        _lastX = x;
        _lastY = y;
    }
```

5. **Convert pixels to an angle, with a clamp and a smoothed rate.** 0.0035 rad per pixel, clamped to +-1.3 rad, and the sign is flipped according to which way the target lies on screen, so away-then-towards is always back-then-through. The rate is averaged over a 60 ms window because mouse input arrives in bursts, and clamped to +-40 rad/s (anything faster is a mouse jump, not a stroke):

```csharp
// src/KSAGolf/Sim/PuttingStroke.cs L52-L68
    public bool Update(double dxPixels, double dtPlayer, double towardsTarget, PutterRig rig, out double launchSpeed)
    {
        launchSpeed = 0.0;
        double before = Theta;
        Theta = Math.Clamp(Theta + (dxPixels * RadiansPerPixel * Math.Sign(towardsTarget)), -MaxTheta, MaxTheta);
        Rate = Smoothed(Math.Max(dtPlayer, 0.0), Theta - before);
        if (Struck || Theta <= before) return false;
        double contact = rig.Ball.X - PutterRig.BallRadiusCm;
        if (rig.LeadingFaceX(before) >= contact || rig.LeadingFaceX(Theta) < contact) return false;
        if (rig.SoleY(Theta) > rig.Ball.Y) return false;
        Struck = true;
        launchSpeed = Transfer * Rate * rig.SwingRadiusCm / 100.0;
        return launchSpeed > 0.0;
    }
```

   A strike needs all of: the club moving forward, the leading face crossing `ball.X - radius` this update, and the sole below the ball's centre. The launch speed is `1.4 * rate * swing radius`; 1.4 is `(1 + e) * M / (M + m)` for a rubber face with e of about 0.6 and a head eight times the ball's mass.

6. **Apply it in the step, after a settle time.** Wait 0.5 s after stepping up (the hand pressing G is still moving), skip when paused, and launch the ball with the stroke's speed:

```csharp
// src/KSAGolf/Ksa/Golf.cs L635-L646
        _sinceAddress += dtPlayer;
        if (KsaWorld.IsPaused || _sinceAddress < SettleSeconds) return;

        if (_stroke.Update(dx, dtPlayer, _towards, _rig, out double speed))
        {
            _struckFromCcf = ball.PositionCcf;
            ball.Strike(_targetCcf * speed);
            GolfSounds.Play(StrikeSound.ForStrike(speed), kitten, BallEcl());
            _sinceStrike = 0.0;
            Status = $"Struck at {speed:F2} m/s.";
            Log.Info($"struck the ball at {speed:F2} m/s (swing {double.RadiansToDegrees(_stroke.Rate):F0} deg/s)");
        }
```

7. **Script swings for tests.** The bridge `golf` command takes a list of pixel movements and feeds one per frame through `Nudge` as if the mouse had moved, which makes the swing testable without a hand on the mouse (see [drive a running game through a file bridge](../../../testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/overview.md) when that topic is present, otherwise the feature page).

8. **Check the result.** The cursor disappears at address; moving the mouse away swings the club back; moving it back through the ball strikes and the status line reports `Struck at N m/s.` with a log entry.

## Complete example

- [src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs#L60-L160) - the controller that takes the mouse.
- [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L97-L146) - `Orbit`, `Swing`, `CursorWanted`, `Step`.
- [src/KSAGolf/Sim/PuttingStroke.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/PuttingStroke.cs#L1-L90) - mouse to club angle, smoothed rate, strike test (headless-tested).
- Feature pages: [Putting session](../../../../mods/ksagolf/features/putting-session.md), [Level-horizon camera controller](../../../../mods/ksagolf/features/level-horizon-camera-controller.md).

## Variations and gotchas

- **The "KSA window open" release never fires.** `Golf.Swing` and `Golf.Orbit` test `Program.IsWindowOpen`, a static that KSA windows set true while they draw in the UI pass and that `OnDrawUiConsole` resets at the end of every frame's UI. The controller asks for `Swing`/`Orbit` from GLFW callbacks during `PrepareFrame` and from `GetCursorMode` in `OnFrameController`, both outside the UI pass, where the flag is always false. In practice only Alt and window focus give the cursor back. A reliable alternative is to ask `ImGui.GetIO().WantCaptureMouse` or track your own panel's open state.
- **G and V need the HUD.** `Keys()` runs only from `[StarMapAfterGui]`, which KSA skips when the HUD is hidden; only the bridge can address the ball then.
- **Not rebindable.** ImGui key reads do not appear in KSA's controls settings ([rebindable hotkeys](../../../ui/input/rebindable-hotkeys/overview.md)).
- **Swallow what walks the kitten.** While input is taken, the controller swallows letters, digits, arrows and the editing keys (so WASD do not walk the kitten off); Escape and the function keys still reach the game.
- **Use player time for the hand, simulated time for the ball.** Pausing the game must not stop the stroke from being measured in the player's time, but the strike is refused while paused (`KsaWorld.IsPaused`).
- **Mouse jump guard.** The 400 px first-jump rule and the +-40 rad/s rate clamp are the two defences against a captured-cursor jump registering as a stroke.

## See also

- [Make a walking kitten move and face along the look direction, and drive it with the mouse](overview.md), [approach 1: heading patch](steer-a-walking-kitten-1.md)
- [Put the camera in an EVA kitten's eyes](../../../camera/special-views/first-person-camera-for-a-kitten/overview.md)
- [Attach a prop to a kitten's hand or another bone](../../appearance/attach-a-prop-to-a-kitten-bone/overview.md), [Share the keyboard and mouse between ImGui, mod tools and the game](../../../ui/input/coordinate-imgui-focus-with-game-input/overview.md)
