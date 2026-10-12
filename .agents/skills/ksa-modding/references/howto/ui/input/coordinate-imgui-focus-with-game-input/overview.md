---
type: Game Feature
title: Share the keyboard and mouse between ImGui, mod tools and the game
description: How KSA decides who gets a key or click - ImGui, the controller, the vehicle or a hotkey - and five ways mods read input without fighting the game
category: ui
group: input
topic: coordinate-imgui-focus-with-game-input
demonstrated_by:
  - mods/ksagolf/features/minigolf-course-builder
  - mods/ksarmory/features/cursor-ray-world-click-tools
  - mods/ksacars/features/car-panel-and-modmenu
  - mods/ksacars/features/drive-with-flight-keys
lifecycle: [frame:input, frame:draw-ui, frame:dispatch-solvers, frame:after-on-frame, frame:viewports]
game_types:
  - Brutal.ImGuiApi.ImGuiIOPtr
  - Brutal.ImGuiApi.ImGuiWindowFlags
  - KSA.Vehicle
  - KSA.GameSettings
  - KSA.Program
  - KSA.FixedController
  - KSA.Controller
  - KSA.GameViewport
  - KSA.KittenEva
  - KSA.ImGuiBackendGlfwImpl
  - KSA.ThrusterMapFlags
  - KSA.ManualControlInputs
  - KSA.EngineFlags
  - KSA.InputAction
  - KSA.InputEvents
  - KSA.Input
tags: [input, imgui, keyboard, mouse, focus, controller, hotkeys, wantcapture]
---
Keys and clicks in KSA have several would-be owners: ImGui widgets, mod tools, the camera controller, the controlled vehicle and the global hotkey table. The engine
settles most of this itself by consulting ImGui's capture flags; a mod's job is to (1) read its own input without double-handling, (2) keep its own windows from taking input
the game needs, and (3) when it runs a modal tool, take input away from the game on purpose. Five mods show the five common moves.

## Engine background

**Key path** (all on the main thread, `Glfw.PollEvents` in `PrepareFrame`, `frame:input`):

| Step | What | File |
|---|---|---|
| 1 | `Program.OnKey` runs `GameSettings.OnKeyAll` (swallows while `IsUserTyping`), `Popup.OnKeyAll`, the console, then only if `!ImGui.GetIO().WantCaptureKeyboard`... | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1754), [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L3422-L3435) |
| 2 | `DispatchKeyEvent`: editor, camera, the **active controller's `OnKey`**, then the engine's own handling including the controlled vehicle | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1756-L1776) |
| 3 | `Vehicle.OnKey` queues `VehicleInputData` actions | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3274-L3300) |
| 4 | `InputEvents.ApplyInputEvents` runs `Vehicle.ProcessInput`, which sets held thruster/engine flags | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3536-L3606) |
| 5 | `Vehicle.PrepareWorker` clears held input when ImGui wants the keyboard or above 30x warp | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2397-L2401) |

**Mouse path:** `Program.OnMouseButton` runs popups, the `WantCaptureMouse && InputViewport.IsMain()` check, bound mouse inputs, the editor, each `Vehicle.OnMouseButton` (which uses
the controller's `IsMouseDrag()` to decide whether a right-release opens a part window), then the active controller, then orbits.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2003-L2060)[^ksa-program][current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3667-L3685)[^ksa-vehicle]
`Program.OnScroll` skips the controller while ImGui wants the mouse in the main viewport.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2116-L2121)[^ksa-program]

**ImGui flags:** `WantCaptureMouse`, `WantCaptureKeyboard` and `WantTextInput` on `ImGuiIOPtr`.[current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs#L140-L144)[^brutal-imguiapi-imguiioptr] Events are polled before `PrepareImGui`, so the engine
reads the previous `NewFrame`'s values; a mod reading them from a GUI hook reads the current frame's. `Program.PrepareImGui` runs before the `DrawUI` gate, so mouse and capture state
exist even with the HUD hidden. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Gate on WantTextInput and WantCaptureMouse in the GUI hook](coordinate-imgui-focus-with-game-input-1.md) | KSAGolf | Poll `ImGui.IsKeyPressed`/`IsMouseClicked` in `StarMapAfterGui` | `frame:draw-ui` | Simple; stops with the HUD hidden |
| 2 | [Ignore clicks ImGui owns (WantCaptureMouse)](coordinate-imgui-focus-with-game-input-2.md) | KSArmory | Same gates plus a modifier reservation, one consumer per click, step-side fallback | `frame:draw-ui`, `frame:after-on-frame` | Works with the HUD hidden; the game still sees the click |
| 3 | [NoNav and NoFocusOnAppearing windows](coordinate-imgui-focus-with-game-input-3.md) | KSACars | Window flags that keep a panel from taking keyboard focus | `frame:draw-ui` | Zero code; no text fields in that window |
| 4 | [WantCaptureKeyboard drops key events; OnKey patches add new meanings](coordinate-imgui-focus-with-game-input-4.md) | KSACars | Read held flags, postfix `Vehicle.OnKey`/`ProcessInput`, reflection | `frame:input`, `frame:dispatch-solvers` | Needs Harmony and private fields |
| 5 | [FixedController subclass swallows input](coordinate-imgui-focus-with-game-input-5.md) | KSAGolf | Override `OnKey`/`OnMouseButton`/`OnScroll`/`IsMouseDrag` | `frame:input` | Only in `Fixed` camera mode; backing-field reflection |

## Choosing

- A tool that lives in the UI: approach 1. A world click tool: approach 2. A panel next to flight controls: approach 3.
- Teaching a vehicle a new key: approach 4. A modal mode that must silence the game: approach 5.
- Typing into a text field while stock hotkeys fire: [block game hotkeys while typing](../block-game-hotkeys-while-typing/overview.md).

## See also

- [Block game hotkeys while typing into an ImGui field](../block-game-hotkeys-while-typing/overview.md)
- [Implement rebindable hotkeys with ImGui key state](../rebindable-hotkeys/overview.md)
- [Read typed text, key chords and the clipboard from ImGui](../read-typed-text-and-key-chords-from-imgui/overview.md)
- [Turn the mouse cursor into a world ray](../cursor-to-world-ray/overview.md)
- [Replace a viewport's camera controller and take input through it](../../../camera/controllers/replace-a-viewport-camera-controller/overview.md)
- [Add to, drive and hide the stock cockpit gauges and navball](../../menus/work-with-stock-cockpit-gauges/overview.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imguiioptr]: Brutal.ImGuiApi.ImGuiIOPtr (decompiled, build 2026.10.10.5554)
