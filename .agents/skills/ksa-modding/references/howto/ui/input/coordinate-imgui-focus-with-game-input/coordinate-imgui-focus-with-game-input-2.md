---
type: How-To
title: "Share input with the game: ignore clicks ImGui owns and keep working with the HUD hidden"
description: Make world click tools skip any click ImGui wants, reserve modifier gestures, scope each click to one consumer, and run the same handler from the frame step when the UI pass is skipped
category: ui
group: input
topic: coordinate-imgui-focus-with-game-input
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/cursor-ray-world-click-tools]
techniques: [imgui-window, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame, frame:input]
game_types: [Brutal.ImGuiApi.ImGuiIOPtr, KSA.Program]
tags: [input, click-tools, wantcapturemouse, hud-hidden, modifiers, gestures]
---
KSArmory has four tools that act on a click in the 3D view: fire at the ground, lock onto a craft, detonate a test burst, pick up a craft. They
share a click discipline, and this article is that discipline: skip any click an ImGui window owns, reserve a modifier for one tool,
accept only a fresh press, let only one consumer read each click, and keep all of it working when the HUD is hidden. For the
GUI-hook-only variant see [approach 1](coordinate-imgui-focus-with-game-input-1.md). Demonstrated by KSArmory.

## Background: how the game does it

- `Program.OnFrame` calls `PrepareImGui` (which starts the ImGui frame and reads the mouse) **before** the `if (DrawUI)` gate, so
  `GetMousePos`, `IsMouseClicked` and `GetIO()` answer for this frame even when no UI is drawn; with the UI hidden the stock windows are not drawn, so only the developer console or a window a mod
  draws on the hidden-HUD path can still want the mouse.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2260-L2275)[^ksa-program]
- The engine does not know about mod tools. `Program.OnMouseButton` returns early only when `ImGui.GetIO().WantCaptureMouse` is true in the main
  viewport, a popup takes the click or a bound input consumes it; otherwise it goes on to editor, vehicles, controller and orbit handling.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2003-L2022)[^ksa-program]
  A mod tool that reads the same click through ImGui does not stop the game seeing it.

## Lifecycle and timing

`UpdateWorldClicks` is called from two places: `OnAfterGui` (`[StarMapAfterGui]`, `frame:draw-ui`) when the UI pass runs, after the panel has drawn so
a click on a window is the window's; and from the end of the mod's simulation step (a prefix on `Program.OnFrameCelestials` or `[StarMapAfterOnFrame]`)
when `Program.DrawUI` is false. The markers and rings that show the result are drawn only in the GUI pass. See
[keeping per-frame work running with the HUD hidden](../../overlays/draw-ui-while-the-hud-is-hidden/overview.md) and
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Start every click tool with the same three gates. A panel click is not a world click; a modifier is reserved for another tool; one event per press:

```csharp
// src/KSArmory/Ksa/BurstTool.cs L27-L33
// A click on the panel is not a click on the world behind it.
if (ImGui.GetIO().WantCaptureMouse) return;
// Shift is the designate gesture, so a shift-click is not a click on the world.
// Without this, locking a target while this tool is on also sends a round at it.
if (ImGui.GetIO().KeyShift) return;

if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left, repeat: false)) return;
```

2. The tool that owns the modifier gesture is the mirror image: it requires Shift and the left button and still refuses clicks the panel owns, otherwise
   every click on the panel behind the sight would also redesignate:

```csharp
// src/KSArmory/Ksa/TargetLock.cs L212-L217
// Shift and the left button, and not while a panel window wants the mouse -- otherwise every
// click on the panel behind the sight also redesignates.
private static bool Requested()
    => !ImGui.GetIO().WantCaptureMouse
       && ImGui.IsMouseClicked(ImGuiMouseButton.Left, repeat: false)
       && ImGui.GetIO().KeyShift;
```

3. Route the click to exactly one consumer. Several crewed systems reading one cursor would fire every launcher in the world at a single click, so only the
   system the panel is showing reads it:

```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L407-L431
// Every tool that acts on a click in the world. Called from the UI pass when it runs and from the
// step when it does not, because a hidden UI stops only the pass: ImGui's frame, and with it the
// mouse, is read before KSA's DrawUI gate every frame. Its markers are drawn only with the UI.
private void UpdateWorldClicks()
{
    if (_roster is null || _ui is null) return;

    _mover.Update(_config);
    _bursts.Update(_config);

    // Only the system the panel is showing. Every crewed system reading the same cursor would
    // fire every launcher in the world at one click.
    if (_roster.For(_ui.Focused) is { } aimed) _designator.Update(aimed.Weapon, aimed.Policy);

    // Same rule, and the same reason: every crewed computer reading one cursor would re-aim every
    // missile in the world at a single click.
    if (_icbms?.For(_ui.Focused) is { } aimedSite) _sites.Update(aimedSite);

    // Shift-click locks the installation the panel is showing onto whatever is under the cursor
    // -- its turret, and its director if it has one -- scoped to the shown system for the same
    // reason the designator is: every crewed system reading one cursor would lock all of them.
    WeaponSystems.Entry? weapon = _roster.For(_ui.Focused);
    TargetLock.Update(weapon?.Weapon, _heads?.Driving(_ui.Focused)?.Head,
                      weapon is null ? null : (at, what) => _roster.DesignateWeapon(weapon, at, what));
}
```

4. Call the dispatcher from both hooks. The GUI pass calls it after the panel; the step calls it only when the UI was not drawn:

```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L351-L355
    using (_budget.Measure("cameras")) DriveCameras(dtPlayer);

    // The UI pass reads clicks when it runs; with the UI hidden it does not run at all.
    if (!KsaWorld.UiDrawn && KsaWorld.InFlight) UpdateWorldClicks();
}
```

   `KsaWorld.UiDrawn` is simply `Program.DrawUI` ([src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L151)).

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/BurstTool.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BurstTool.cs#L23-L35) | Plain-click gate |
| [src/KSArmory/Ksa/TargetLock.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TargetLock.cs#L212-L217) | Shift-click gate |
| [src/KSArmory/Ksa/Designator.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Designator.cs#L29-L40) | Plain-click gate with the shift reservation |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L407-L431) | One consumer per click |

## Variations and gotchas

- **Keep the reservation symmetrical.** If one tool owns Shift+click, every plain-click tool must return on `KeyShift`, or locking a target also fires a round.
- **Read the flag at the click.** Every tool calls `ImGui.GetIO().WantCaptureMouse` in the same statement block as `IsMouseClicked`; none caches it.
- **The game still sees the click.** KSArmory does not consume the mouse button; whether KSA's own selection and hover also react was not checked. To swallow it
  see [approach 5](coordinate-imgui-focus-with-game-input-5.md).
- **Do not require the HUD to act.** Hiding it (F2) would silently disable every click tool without the step-side call.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of coordinate-imgui-focus-with-game-input](overview.md)
- [Approach 1: gate on WantTextInput and WantCaptureMouse in the GUI hook](coordinate-imgui-focus-with-game-input-1.md)
- [Cursor to world ray, approach 1](../cursor-to-world-ray/cursor-to-world-ray-1.md)
- Mod feature: [Cursor ray world click tools](../../../../mods/ksarmory/features/cursor-ray-world-click-tools.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
