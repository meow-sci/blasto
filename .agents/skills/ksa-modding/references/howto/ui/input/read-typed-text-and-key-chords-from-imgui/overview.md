---
type: Game Feature
title: Read typed text, key chords and the clipboard from ImGui
description: How text, chords and clipboard reach a mod through ImGui's IO and the GLFW-backed platform clipboard, and two mods that use them
category: ui
group: input
topic: read-typed-text-and-key-chords-from-imgui
demonstrated_by:
  - mods/purrtty/features/imgui-keyboard-mouse-to-vt-input
  - mods/ksa-measuretools/features/measure-menu-and-tool-window
lifecycle: [frame:input, frame:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImGuiIOPtr
  - Brutal.ImGuiApi.ImGuiKey
  - KSA.ImGuiBackendGlfwImpl
  - Brutal.ImGuiApi.ImGui
  - KSA.DistanceReference
tags: [keyboard, text-input, chords, altgr, clipboard, imgui]
---
A mod that wants raw keyboard input (a terminal, a text editor, a hotkey capture) or the system clipboard does not talk to GLFW. KSA's ImGui backend already forwards every GLFW key, char and mouse event into ImGui's IO and
connects ImGui's clipboard to GLFW; the mod reads ImGui. The two things to get right are where each kind of input appears (text versus chords versus named keys) and when (the frame after the events were polled).

## Engine background

| Piece | File | Role |
|---|---|---|
| `ImGuiBackendGlfwImpl.OnKey` / `OnChar` / `OnMouseButton` / `OnScroll` | [current/decomp/KSA/ImGuiBackendGlfwImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendGlfwImpl.cs#L436-L527) | `AddKeyEvent`, `AddInputCharacter`, mouse events into ImGui IO |
| Clipboard callbacks | [current/decomp/KSA/ImGuiBackendGlfwImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendGlfwImpl.cs#L410-L424) | `Platform_GetClipboardTextFn` / `SetClipboardTextFn` over `Glfw.Clipboard` / `Glfw.SetClipboard` |
| `ImGuiIOPtr.InputQueueCharacters` | [current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs#L236) | Typed characters this frame as UTF-16 units |
| `ImGui.IsKeyPressed`, `GetClipboardText`, `SetClipboardText` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8146), [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8294-L8301) | Key edge and clipboard access |

**Frame order.** GLFW delivers events during `Glfw.PollEvents` in `PrepareFrame` (`frame:input`); `ImGui.NewFrame` in `PrepareImGui` converts them to this frame's key state and character queue, which stay valid until
`ImGui.Render`. A mod reads them in the GUI hook (`frame:draw-ui`). The game also receives the same events through `Program.OnKey`; stopping that is a separate step
([block game hotkeys while typing](../block-game-hotkeys-while-typing/overview.md)). See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Character queue versus IsKeyPressed, with paste chords](read-typed-text-and-key-chords-from-imgui-1.md) | purrTTY | Text from `InputQueueCharacters`, chords from `IsKeyPressed`, AltGr rule, clipboard through ImGui | `frame:input`, `frame:draw-ui` | Complete terminal input; lots of rules |
| 2 | [Selectable rows that build text only on click](read-typed-text-and-key-chords-from-imgui-2.md) | KSA-MeasureTools | `Selectable` plus `SetClipboardText` in a table | `frame:draw-ui` | Trivial; copy only |

## Choosing

- Everything a text surface needs: approach 1. Just "click to copy": approach 2.
- Single-key toggles: [rebindable hotkeys](../rebindable-hotkeys/overview.md).

## See also

- [Share the keyboard and mouse between ImGui, mod tools and the game](../coordinate-imgui-focus-with-game-input/overview.md)
- [Block game hotkeys while typing into an ImGui field](../block-game-hotkeys-while-typing/overview.md)
- [Implement rebindable hotkeys with ImGui key state](../rebindable-hotkeys/overview.md)

