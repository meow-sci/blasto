---
type: How-To
title: Mouse prefix that pairs presses with releases and repairs private latches
description: Take left clicks away from Program.OnMouseButton while a tool owns them, mirror the stock gate order, consume releases only for presses you consumed, and repair the private latches that the skipped finally block would have reset
category: patching
group: harmony
topic: skip-the-original-method-with-a-prefix
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-measuretools/features/click-capture-mouse-prefix]
techniques: [harmony-prefix, reflection]
hooks:
  - harmony-prefix KSA.Program.OnMouseButton
  - reflection KSA.Program._heldMouseButtons
  - reflection KSA.Program._inputViewport
  - reflection KSA.BurnContextMenu._rightClickValid
lifecycle: [frame:input]
game_types: [KSA.Program, KSA.BurnContextMenu, KSA.MapController, KSA.VehicleEditor, KSA.Input]
tags: [harmony, prefix, skip-original, input, mouse, latch, finally]
---

While the KSA-MeasureTools window is open and a tool is armed, a left click places a measurement point
instead of focusing a body, setting a target, creating a burn or picking an editor part. The mod does
this with one prefix on `Program.OnMouseButton` that returns `false` for the clicks it handles. The
interesting part is everything a skipped original *fails to do*: it never reaches its `finally`, so
the private state that the original would have reset has to be reset by the prefix, in the same
order, or it leaks for the rest of the session.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by KSA-MeasureTools in
[Own placement clicks with a guarded Program.OnMouseButton prefix](../../../../mods/ksa-measuretools/features/click-capture-mouse-prefix.md).

## Background: how the game does it

`Program.OnMouseButton` is the GLFW callback registered in the `Program` constructor
(`_window.OnMouseButton += OnMouseButton`, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L785-L793)[^ksa-program]). Its shape matters:

```csharp
// current/decomp/KSA/Program.cs L2009-L2031, L2086-L2096
int num = 1 << (int)button;
if (action == GlfwButtonAction.Press)
{
	if (_heldMouseButtons == 0)
	{
		_inputViewport = HoveredViewport;
	}
	_heldMouseButtons |= num;
}
try
{
	if (Popup.OnMouseAll(window, button, action, mods) || (ImGui.GetIO().WantCaptureMouse && InputViewport.IsMain()) || (Input.MouseBound(button) && DispatchKeyEvent(new GlfwKeyEvent(window, action, button, mods))))
	{
		return;
	}
	if (button == GlfwMouseButton.Number2 && action == GlfwButtonAction.Press)
	{
		BurnContextMenu.OnRightClick();
	}
	if (!((!(Editor?.OnMouseButton(window, button, action, mods))) ?? true))
	{
		return;
	}
// ...
finally
{
	if (action == GlfwButtonAction.Release)
	{
		_heldMouseButtons &= ~num;
		if (_heldMouseButtons == 0)
		{
			_inputViewport = null;
		}
	}
}
```

Source: [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2009-L2096)

In order: on a press it latches `_inputViewport = HoveredViewport` when no button is held and sets the
button bit in the private static `_heldMouseButtons`; then, inside a `try`, it offers the click to
popups, to ImGui (`WantCaptureMouse` on the main viewport) and to bound input actions
(`Input.MouseBound(button) && DispatchKeyEvent(...)`); a right press arms
`BurnContextMenu.OnRightClick()`; then come the editor, every vehicle, `BurnContextMenu.TryOpen`, the
active controller and the orbiters ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2018-L2085)). A `finally` clears the button bit on
release and drops `_inputViewport` when no button is left. The latch fields are declared at
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L269-L275); the burn-menu flag `_rightClickValid` is set by `OnRightClick` and
cleared by `TryOpen` ([current/decomp/KSA/BurnContextMenu.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnContextMenu.cs#L72-L76)[^ksa-burncontextmenu]).

## Lifecycle and timing

`Glfw.PollEvents()` runs inside `Program.PrepareFrame`, after the `Universe.Apply*Solvers` calls
and before `Universe.ExecuteNext*Solvers` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2163-L2181), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2216-L2221)). The
callback therefore fires in `frame:input`, on the main thread, in the settled window where vehicle and
orbit state is up to date and no worker is stepping the simulation yet. The prefix sees:

| State read by the prefix | Value |
|---|---|
| Vehicle, orbit, part transforms | just applied from the finished physics step |
| `ImGui.GetIO().WantCaptureMouse` | last frame's value (stock tests the same value) |
| Camera matrices | last frame's; `Camera.OnFrame` rebuilds them later this frame |
| `ViewportPartPicker.Part` | last frame's UI pass, so the "pause over a part" test can be one frame stale |

See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the full order of `PrepareFrame`. The point was chosen because it is the only place a
mod sees a click before the editor, the part menu, the burn menu and the controllers act on it.

## Steps

1. **Target the callback.** `Program.OnMouseButton` is public, so the class-level attribute names it
   with `nameof`. The prefix parameters repeat the original's names:

```csharp
// MeasureTools/Patches/Patch_MouseButton.cs L20-L30
[HarmonyPatch(typeof(Program), nameof(Program.OnMouseButton))]
internal static class Patch_MouseButton
{
    // A right press and release closer than this counts as a click (cancel/pause);
    // anything farther is a drag (camera rotate) and is left to the game.
    private const float RightClickDragThresholdPx = 4f;

    private static bool _rightPressPending;
    private static float2 _rightPressPos;

    private static bool _leftPressConsumed;
```

Source: [MeasureTools/Patches/Patch_MouseButton.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MouseButton.cs#L20-L30)[^ksa-measuretools-patch-mousebutton]

2. **Pair every consumed press with its release, first.** The gates below (armed, ImGui capture,
   editor drag) can change while the button is down. Deciding the release first, from one boolean,
   guarantees the game never gets a release for a press it did not see; an unpaired release would make
   the editor grab the highlighted part. A press always opens a fresh pair:

```csharp
// MeasureTools/Patches/Patch_MouseButton.cs L94-L109
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
```

Source: [MeasureTools/Patches/Patch_MouseButton.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MouseButton.cs#L94-L109)

3. **Return `true` for everything stock owns, in stock's order.** If the tool is disarmed, or the click
   belongs to another viewport, an open popup, ImGui, or a user-bound mouse button, let the original
   run. The bound-button test is an approximation: stock calls `DispatchKeyEvent` and acts only if a
   handler took it, which the prefix cannot do without side effects, so it uses `Input.MouseBound` plus
   `Input.Contains` over every `InputAction`. `Contains` is the widest match stock applies, so a press
   with extra modifiers still counts as bound:

```csharp
// MeasureTools/Patches/Patch_MouseButton.cs L62-L82
private static bool EditorHoldsDrag()
{
    VehicleEditor? editor = Program.Editor;
    return editor != null && (editor.GizmoGrabbed || (editor.Highlighted?.Grabbed ?? false));
}

// The original dispatches a bound mouse button as a key event before any world
// click. Contains is the widest test a stock handler applies, so a press with
// extra modifiers still counts as bound.
private static bool IsBoundInput(GlfwWindow window, GlfwMouseButton button, GlfwButtonAction action, GlfwModifier mods)
{
    if (!Input.MouseBound(button))
        return false;
    var keyEvent = new GlfwKeyEvent(window, action, button, mods);
    foreach (InputAction inputAction in EnumCollections.InputActions.Values)
    {
        if (Input.Contains(in keyEvent, inputAction))
            return true;
    }
    return false;
}
```

Source: [MeasureTools/Patches/Patch_MouseButton.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MouseButton.cs#L62-L82)

```csharp
// MeasureTools/Patches/Patch_MouseButton.cs L111-L134
if (!MeasureState.IsArmed)
{
    // Drop a half-tracked right click so its state cannot leak across a
    // disarm/re-arm cycle and cancel a point unexpectedly.
    _rightPressPending = false;
    return true;
}
// InputViewport is what the original tests: a press latches its viewport
// for the whole sequence, so a release still belongs to where the drag
// started. Not ours means another viewport (see MeasureViewport), or a
// click the original serves before any world click: an open popup, ImGui
// holding the mouse, or a user binding on this button. The tool consumes a
// release only after it handled the press, so a bound action always gets
// its release.
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

Source: [MeasureTools/Patches/Patch_MouseButton.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MouseButton.cs#L111-L134)

4. **Consume the click and remember it.** The left press places the point and returns `false`, after
   setting `_leftPressConsumed` so its release is swallowed in step 2. The press is consumed even when
   nothing resolved, so an armed tool never leaks a stray world click:

```csharp
// MeasureTools/Patches/Patch_MouseButton.cs L205-L218
Anchor? anchor = MapPicker.Pick(inputViewport, mouseViewport, eclipticFree);
if (anchor != null)
{
    MeasureState.AddPoint(anchor);
}
else if (DebugConfig.Measure)
{
    DefaultCategory.Log.Debug(
        $"[MeasureTools] Placement click at {mouseViewport} resolved no anchor (mode {MeasureState.Mode}), click consumed.");
}
// Consumed even when nothing resolved: while armed, unmodified left
// clicks belong to the tool.
_leftPressConsumed = true;
return false;
```

Source: [MeasureTools/Patches/Patch_MouseButton.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MouseButton.cs#L205-L218)

5. **Repair what the skipped original would have done.** A consumed *left* press needs no repair: the
   original never ran, so the bit was never set. A consumed *right release* does. Its press went through
   stock, which set the `_heldMouseButtons` bit, latched `_inputViewport` and armed
   `_rightClickValid`; the skipped `finally` will never undo that. The prefix does it by hand,
   and also ends the map-rotate drag that would otherwise never see its release:

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

Source: [MeasureTools/Patches/Patch_MouseButton.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MouseButton.cs#L46-L57)

   The latch handles are cached `AccessTools.Field` lookups, and the code mirrors the stock `finally`
   line by line:

```csharp
// MeasureTools/Core/GameReflection.cs L15-L44
private static readonly FieldInfo? Program_heldMouseButtons =
    AccessTools.Field(typeof(Program), "_heldMouseButtons");

private static readonly FieldInfo? Program_inputViewport =
    AccessTools.Field(typeof(Program), "_inputViewport");

// Armed by BurnContextMenu.OnRightClick on the press, cleared only by TryOpen on
// the release, so a consumed release leaves it armed over stale coordinates.
private static readonly FieldInfo? BurnContextMenu_rightClickValid =
    AccessTools.Field(typeof(BurnContextMenu), "_rightClickValid");

// Both fields or neither: half-releasing the latch is worse than not consuming.
// Types checked so drift reads as drift instead of throwing mid-click.
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

Source: [MeasureTools/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Core/GameReflection.cs#L15-L44)[^ksa-measuretools-gamereflection]

6. **Decline to consume when the repair is impossible.** `CanReleaseMouseLatch` checks the *types*
   (`int` and `IGameViewport`), not just presence. If either field is missing or has changed type,
   `TryConsumeRightRelease` returns `false` and the click goes to stock: the player gets a stock menu on
   top of the cancel, which is better than a stuck latch pinning `Program.InputViewport` for the
   session. The mod logs the drift once at startup (`LogDriftIfUnavailable`).

7. **Never throw out of the prefix.** The whole body sits in a `try`; any exception is logged once and
   the click passes through:

```csharp
// MeasureTools/Patches/Patch_MouseButton.cs L220-L224
catch (Exception ex)
{
    LogHelper.ErrorOnce("mouse-prefix-" + ex.GetType().Name, $"[MeasureTools] Mouse intercept failed, passing click through: {ex}");
    return true;
}
```

Source: [MeasureTools/Patches/Patch_MouseButton.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MouseButton.cs#L220-L224)

## Complete example

| File | Role |
|---|---|
| [MeasureTools/Patches/Patch_MouseButton.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MouseButton.cs) | The prefix, pairing, gates, right-click cancel and pause |
| [MeasureTools/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Core/GameReflection.cs) | Cached private-field handles, latch release, drift warning |
| [MeasureTools/Features/Measure/MeasureState.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureState.cs#L53-L94)[^ksa-measuretools-measurestate] | `IsArmed` and the supported camera modes |

## Variations and gotchas

- **A skipped original skips its `finally`.** Any click you consume *after* stock has already seen
  its press needs the bookkeeping redone by hand, and the field names are private.
- **Consume releases only in pairs.** Eating a lone release, or letting a release through after eating its
  press, hands the editor a release it never saw a press for.
- **Do not arm a left-click tool in left-drag camera modes.** The mod stays disarmed in Free, IVA and
  Fixed views because the free-fly controller steers with the left button.
- **Only this callback is patched.** Scroll, key and cursor callbacks are untouched; for keys see
  [Keyboard and mouse-binding gates with a held-key model](skip-the-original-method-with-a-prefix-5.md).
- **Stale ImGui capture.** `WantCaptureMouse` is one frame old in this callback; matching stock's
  own value is the safe choice.

## See also

- [Reproduce the original body for marked objects and return false](skip-the-original-method-with-a-prefix-1.md), [Rewrite by-ref arguments and skip when a limit would throw](skip-the-original-method-with-a-prefix-2.md), [Re-inject a private field before a rebuild so nothing can change it back](skip-the-original-method-with-a-prefix-3.md), [Keyboard and mouse-binding gates with a held-key model](skip-the-original-method-with-a-prefix-5.md)
- [Skipping Harmony prefixes](overview.md)
- [Own placement clicks with a guarded Program.OnMouseButton prefix](../../../../mods/ksa-measuretools/features/click-capture-mouse-prefix.md)
- The topics block-game-hotkeys-while-typing and coordinate-imgui-focus-with-game-input build tasks on this technique
- [Isolating patch failures and keeping patch bodies from throwing](../../safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md) for the once-only logging pattern used in step 7

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-burncontextmenu]: KSA.BurnContextMenu (decompiled, build 2026.10.10.5554)
[^ksa-measuretools-patch-mousebutton]: KSA-MeasureTools/MeasureTools/Patches/Patch_MouseButton.cs
[^ksa-measuretools-gamereflection]: KSA-MeasureTools/MeasureTools/Core/GameReflection.cs
[^ksa-measuretools-measurestate]: KSA-MeasureTools/MeasureTools/Features/Measure/MeasureState.cs
