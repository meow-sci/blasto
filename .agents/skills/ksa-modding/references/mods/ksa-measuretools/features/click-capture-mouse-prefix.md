---
type: Mod Feature
title: Own placement clicks with a guarded Program.OnMouseButton prefix
description: A Harmony prefix on Program.OnMouseButton takes unmodified left clicks while armed, pairs each consumed press with its release, defers to popups, ImGui, bindings and editor drags, and restores private mouse latches via reflection
mod: ksa-measuretools
category: patching
techniques: [harmony-prefix, reflection]
hooks:
  - harmony-prefix KSA.Program.OnMouseButton
  - reflection KSA.Program._heldMouseButtons
  - reflection KSA.Program._inputViewport
  - reflection KSA.BurnContextMenu._rightClickValid
lifecycle: [frame:input]
game_types:
  - KSA.Program
  - KSA.BurnContextMenu
  - KSA.MapController
  - KSA.Controller
  - KSA.VehicleEditor
  - KSA.Input
  - KSA.Popup
  - KSA.Cursor
  - KSA.ViewportPartPicker
  - KSA.CameraMode
tags: [input, mouse, harmony, prefix, reflection, map-view, editor, click-capture]
---

While the Measure window is open and a tool is selected, a left click in the map view or the default
flight view places a measurement point instead of focusing a body, setting a target, creating a burn
or picking an editor part. Camera control is untouched: the map and orbit cameras rotate and pan with
the right and middle buttons, a short right click cancels the pending point or pauses the tool, and a
right drag still rotates.[^mt-readme]

Mechanism: one Harmony prefix on the GLFW mouse callback `Program.OnMouseButton`. It decides first
whether the click belongs to the tool at all, returns `false` (skip the original) only for clicks it
handled, and repairs the two pieces of private state the skipped original would have reset.[^mt-patch-mouse]

## How it works

The stock handler, in order (build 2026.10.10.5554): on a press it latches `_inputViewport =
HoveredViewport` when no button is held and sets the button bit in `_heldMouseButtons`; inside a
`try` it gives the click to `Popup.OnMouseAll`, to ImGui (`WantCaptureMouse` on the main viewport)
and to bound input actions; a right press arms `BurnContextMenu.OnRightClick()`; then
`Editor.OnMouseButton`, every vehicle's `OnMouseButton` (the stock part menu),
`BurnContextMenu.TryOpen`, the active controller, `IOrbiter.OnMouseButtonOrbit`, and finally
left-press burn creation at `CelestialSystem.NearestOrbitPoint`. A `finally` clears the button bit on
release and drops `_inputViewport` when no button is left.[^ksa-program]

The prefix mirrors that order before claiming anything:

1. **Press/release pairing first.** A left press clears `_leftPressConsumed`; a left release that
   follows a consumed press is swallowed. This runs before every other gate because the gates (armed
   state, ImGui capture, editor drag) can change while the button is down, and an unpaired release
   makes the editor grab the highlighted part.
2. **Disarmed means pass-through.** `MeasureState.IsArmed` requires the window open, the tool not
   paused, a loaded system, and the main viewport in `CameraMode.Map` or `CameraMode.Orbit`. Free
   (FlyController looks with left drag), IVA and Fixed views are never armed.[^mt-state]
3. **Stock-owned clicks pass.** Clicks on another viewport (`MeasureViewport.IsHost(Program.InputViewport)`
   must be the main one[^mt-viewport]), any open popup (`Popup.AnyOpen`), ImGui capture
   (`ImGui.GetIO().WantCaptureMouse`) and mouse buttons bound in the control settings pass. Stock
   routes a bound button through `Input.MouseBound(button) && DispatchKeyEvent(...)` and returns only
   when a handler took it; the prefix cannot dispatch without side effects, so it approximates that
   with `Input.MouseBound(button)` plus `Input.Contains(in keyEvent, action)` over every
   `EnumCollections.InputActions` value. `Contains` is the widest match a stock handler uses, so a
   press with extra modifiers still counts as bound, and the test can pass a click that stock would
   have dispatched to no handler.[^ksa-input][^ksa-program]
4. **Editor drags pass.** If `Program.Editor.GizmoGrabbed` or the highlighted part is `Grabbed`, the
   left click goes to the editor, because `VehicleEditor.OnMouseButton` drops a grabbed part and ends a
   gizmo drag on the left release.[^ksa-vehicleeditor]
5. **Right button.** The press is never consumed (the camera may start a rotate drag). The prefix
   records `Cursor.DesktopPosition`; a release closer than 4 px is a click: cancel the pending point,
   or pause the tool when nothing is pending and the cursor is not over a part (`Editor.Highlighted`
   in the editor, `viewport.PartPicker.Part` in flight; over a part the stock part menu wins).
6. **Left press.** Shift (target) and Alt (focus, editor duplicate) pass to stock. Ctrl requests a
   free point on the ecliptic plane. The cursor goes through `Cursor.GetPosition(viewport)` to
   `MapPicker.PickCircle` (Circle mode) or `MapPicker.Pick`, the anchor is added, and the press is
   consumed even when nothing resolved, so an armed tool never leaks a stray world click.
7. Any exception is logged once and the click passes through.

### Repairing what the skipped original would have done

A consumed left press needs no repair: the prefix skips the whole original, including the latch code,
so the bit is never set. A consumed right release does: its press already went through stock, which
set the `_heldMouseButtons` bit, latched `_inputViewport` and armed `BurnContextMenu._rightClickValid`.
`TryConsumeRightRelease` therefore:[^mt-gamereflection]

- calls `controller.CancelMouseDrag()` and, for a `MapController`, sets `RotateMouseDragging = false`
  itself. `MapController` clears that flag only in its own `OnMouseButton` (on the release this
  prefix just ate) and in `OnSwitchOff`, and does not override the empty base `CancelMouseDrag`, so
  without this the map keeps rotating with the cursor captured.[^ksa-mapcontroller][^ksa-controller]
- clears the button bit and nulls `_inputViewport` exactly like the stock `finally`, through cached
  `AccessTools.Field` handles. A stuck latch would pin `Program.InputViewport` for the session.
- sets `_rightClickValid = false`, standing in for the skipped `BurnContextMenu.TryOpen`, which would
  otherwise open the burn menu on stale coordinates at the next opportunity.[^ksa-burncontextmenu]

If either latch field is missing or changed type (`CanReleaseMouseLatch` checks `int` and
`IGameViewport`), the tool declines to consume the right release: the user gets a stock menu on top
of the cancel, which is better than a leaked latch. `Mod.OnFullyLoaded` logs the drift once at
startup.[^mt-mod]

## Lifecycle and timing

`Program.OnMouseButton` is registered on the GLFW window in the `Program` constructor and fires inside
`Glfw.PollEvents()` in `Program.PrepareFrame`, after `Universe.Apply*Solvers` and before
`Universe.ExecuteNext*Solvers`.[^ksa-program] That is `frame:input`, the settled window:

| State | Value seen by the prefix | Consequence |
|---|---|---|
| Vehicle, orbit, part transforms | Just applied from the finished physics step | Safe to read; the mesh raycast in the pick is exact for this step |
| Camera | View and projection matrices are last frame's (`Camera.OnFrame` rebuilds them later this frame, in `OnFrameViewports`); `Camera.PositionEcl` of a following camera is computed live from the followed body, so it already includes the just-applied step | The ray matches the image the player clicked on, re-centred on the followed body |
| `Cursor.DesktopPosition` / `Cursor.GetPosition` | The ImGui mouse position copied in last frame's `PrepareImGui` (`Cursor.SetDesktopPosition`); the GLFW callback does not update it | Click position and camera come from the same frame; the 4 px right-click test compares two such samples |
| `ImGui.GetIO().WantCaptureMouse` | Last frame's value | Same value stock tests |
| `ViewportPartPicker.Part`, `Astronomical.CursorHoverEligible` | Last frame's UI pass | The pause-over-part test can be one frame stale; the click pick does a full scan instead of reusing the stock hover |
| `Program.Editor` | Still alive even if the editor is closing; disposal runs right after `PollEvents` | Editor anchors placed on this click are re-homed by the next `Prune` |

The point was chosen because it is the only place a mod can see a click before the editor, the part
menu, the burn menu and the controllers act on it, and because the simulation is not being stepped
on worker threads at that instant.

## Key code

| Path | Role |
|---|---|
| [MeasureTools/Patches/Patch_MouseButton.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MouseButton.cs#L84-L225) | The prefix: pairing, gates, right-click cancel/pause, placement |
| [MeasureTools/Patches/Patch_MouseButton.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MouseButton.cs#L46-L82) | `TryConsumeRightRelease`, `EditorHoldsDrag`, `IsBoundInput` |
| [MeasureTools/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Core/GameReflection.cs) | Cached private-field handles, latch release, drift warning |
| [MeasureTools/Features/Measure/MeasureState.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureState.cs#L53-L94) | `IsArmed`, supported camera modes, pause |
| [MeasureTools/Features/Measure/MeasureViewport.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureViewport.cs) | Main-viewport-only host test |

## Snippets

```csharp
// MeasureTools/Patches/Patch_MouseButton.cs L84-L134
[HarmonyPrefix]
private static bool Prefix(GlfwWindow window, GlfwMouseButton button, GlfwButtonAction action, GlfwModifier mods)
{
    try
    {
        // ...
        if (button == GlfwMouseButton.Number1)
        {
            if (action == GlfwButtonAction.Press)
            {
                _leftPressConsumed = false;
            }
            else if (action == GlfwButtonAction.Release && _leftPressConsumed)
            {
                _leftPressConsumed = false;
                return false;
            }
        }
        else if (button == GlfwMouseButton.Number2 && action == GlfwButtonAction.Press)
        {
            _rightPressPending = false;
        }

        if (!MeasureState.IsArmed)
        {
            // ...
            _rightPressPending = false;
            return true;
        }
        // ...
        IGameViewport inputViewport = Program.InputViewport;
        if (!MeasureViewport.IsHost(inputViewport)
            || Popup.AnyOpen
            || ImGui.GetIO().WantCaptureMouse
            || IsBoundInput(window, button, action, mods))
        {
            return true;
        }
        if (button == GlfwMouseButton.Number1 && EditorHoldsDrag())
            return true;
```

```csharp
// MeasureTools/Patches/Patch_MouseButton.cs L46-L57
private static bool TryConsumeRightRelease()
{
    if (!GameReflection.CanReleaseMouseLatch)
        return false;
    Controller controller = Program.InputViewport.GetActiveController();
    controller.CancelMouseDrag();
    if (controller is MapController map)
        map.RotateMouseDragging = false;
    GameReflection.ReleaseMouseLatch(GlfwMouseButton.Number2);
    GameReflection.ClearBurnRightClickLatch();
    return true;
}
```

```csharp
// MeasureTools/Core/GameReflection.cs L15-L44
private static readonly FieldInfo? Program_heldMouseButtons =
    AccessTools.Field(typeof(Program), "_heldMouseButtons");

private static readonly FieldInfo? Program_inputViewport =
    AccessTools.Field(typeof(Program), "_inputViewport");
// ...
public static bool CanReleaseMouseLatch =>
    Program_heldMouseButtons?.FieldType == typeof(int)
    && Program_inputViewport != null
    && Program_inputViewport.FieldType.IsAssignableFrom(typeof(IGameViewport));

// Mirrors the finally in Program.OnMouseButton.
public static void ReleaseMouseLatch(GlfwMouseButton button)
{
    if (!CanReleaseMouseLatch)
        return;

    int held = (int)Program_heldMouseButtons!.GetValue(null)!;
    held &= ~(1 << (int)button);
    Program_heldMouseButtons.SetValue(null, held);
    if (held == 0)
        Program_inputViewport!.SetValue(null, null);
}
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Program.OnMouseButton` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2003-L2097) | Prefix target; stock click routing and latch `finally` |
| `Program._heldMouseButtons`, `Program._inputViewport` (private static) | same, L271-L273 | Latch state repaired after a consumed right release |
| `Program.InputViewport`, `Program.Editor` | same | Viewport the press latched to; editor presence |
| `BurnContextMenu._rightClickValid`, `OnRightClick`, `TryOpen` | [current/decomp/KSA/BurnContextMenu.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnContextMenu.cs#L38-L76) | Burn-menu latch armed on right press, cleared by the mod |
| `MapController.RotateMouseDragging` | [current/decomp/KSA/MapController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MapController.cs#L533-L620) | Ends the right-drag rotate that never sees its release (cleared only in `OnMouseButton` and `OnSwitchOff`) |
| `Controller.CancelMouseDrag` | [current/decomp/KSA/Controller.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Controller.cs) | Generic drag cancel (empty in the base class) |
| `VehicleEditor.GizmoGrabbed`, `Highlighted`, `Part.Grabbed`, `OnMouseButton` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L3485-L3639) | Editor acts on left release; drags must pass |
| `Input.MouseBound`, `Input.Contains` | [current/decomp/KSA/Input.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Input.cs) | Leave user-bound mouse buttons to the game |
| `Popup.AnyOpen` | [current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs) | Popups own clicks |
| `Cursor.DesktopPosition`, `Cursor.GetPosition` | [current/decomp/KSA/Cursor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Cursor.cs) | Click-vs-drag threshold; viewport-local cursor (set once per frame in `Program.PrepareImGui`) |
| `IGameViewport.PartPicker` (`ViewportPartPicker.Part`) | [current/decomp/KSA/ViewportPartPicker.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportPartPicker.cs) | "Over a part" test for the pause gesture |

## Pitfalls and version notes

- A prefix that returns `false` also skips the original's `finally`. Any click you consume after stock
  already saw its press needs the latch bookkeeping redone by hand, and the field names are private
  and can change. Check field types, not only presence, so drift degrades instead of throwing
  mid-click.
- Consume releases only in pairs with presses you consumed. Eating a lone release (or letting a lone
  release through after eating the press) hands the editor a release it never saw a press for.
- `WantCaptureMouse` is one frame old in this callback; stock uses the same value, so matching stock
  is the safe choice.
- Do not arm a left-click tool in camera modes that steer with the left button (`FlyController`
  checks `GlfwMouseButton.Number1`).
- The mod patches only `Program.OnMouseButton`; scroll, key and cursor callbacks are untouched.

## See also

- [Overview](../overview.md)
- [Cursor unprojection and the construction plane](./cursor-unprojection-and-construction-plane.md)
- [Body and limb snapping](./body-and-limb-snapping.md)
- [Editor measuring](./editor-measuring.md)
- [Game loop and lifecycle reference](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Mouse prefix that pairs presses with releases and repairs private latches](../../../howto/patching/harmony/skip-the-original-method-with-a-prefix/skip-the-original-method-with-a-prefix-4.md) - how-to article demonstrating this feature

[^mt-patch-mouse]: MeasureTools/Patches/Patch_MouseButton.cs
[^mt-gamereflection]: MeasureTools/Core/GameReflection.cs
[^mt-state]: MeasureTools/Features/Measure/MeasureState.cs
[^mt-viewport]: MeasureTools/Features/Measure/MeasureViewport.cs
[^mt-mod]: MeasureTools/Mod.cs
[^mt-readme]: KSA-MeasureTools README.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-burncontextmenu]: KSA.BurnContextMenu (decompiled, build 2026.10.10.5554)
[^ksa-mapcontroller]: KSA.MapController (decompiled, build 2026.10.10.5554)
[^ksa-controller]: KSA.Controller (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
[^ksa-input]: KSA.Input (decompiled, build 2026.10.10.5554)
