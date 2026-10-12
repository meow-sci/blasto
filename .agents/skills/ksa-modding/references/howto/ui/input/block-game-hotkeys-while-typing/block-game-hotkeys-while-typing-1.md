---
type: How-To
title: "Block game hotkeys while typing: prefix on GameSettings.OnKeyAll when ImGui wants text input"
description: Stop KSA bindings from firing while any ImGui text box is active with a one-method Harmony prefix that reports the key as handled
category: ui
group: input
topic: block-game-hotkeys-while-typing
approach: 1
difficulty: beginner
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/lib-hotkey-guard]
techniques: [harmony-prefix]
hooks: [harmony-prefix KSA.GameSettings.OnKeyAll]
lifecycle: [frame:input, startup:all-mods-loaded]
game_types: [KSA.GameSettings, KSA.Program, KSA.InputAction, Brutal.ImGuiApi.ImGuiIOPtr]
tags: [hotkeys, input, text-input, harmony, imgui, guard]
---
A mod that adds an ImGui text box (a combo filter, a preset name, a paste area) has a problem the first time the player types an "m": the game treats it as a hotkey and opens the map.
unscience solves it once for every submod with a single helper, `HotkeyGuard`: a Harmony prefix on `GameSettings.OnKeyAll` that returns "handled" whenever `ImGui.GetIO().WantTextInput`
is set. Because it uses ImGui's global flag, every `InputText`, `InputTextWithHint` and filtered combo in every mod is covered with no per-widget work. Demonstrated by unscience.
Prerequisites: StarMap and Harmony.

## Background: how the game does it

`Program.OnKey` is the GLFW key callback. It short-circuits on the first handler that returns true:

```csharp
// current/decomp/KSA/Program.cs L1745-L1754
public void OnKey(GlfwWindow window, GlfwKey key, int scanCode, GlfwKeyAction action, GlfwModifier mods)
{
    GlfwKeyEvent keyEvent = new GlfwKeyEvent(window, action, key, mods);
    ShiftDown = EnumEx.IsSet((int)mods, 1);
    AltDown = EnumEx.IsSet((int)mods, 4);
    if (IsLoaded && !GameSettings.OnKeyAll(keyEvent) && !Popup.OnKeyAll(keyEvent) && !ConsoleWindow.OnKey(window, key, scanCode, action, mods) && !ConsoleWindow.IsOpen && !ImGui.GetIO().WantCaptureKeyboard)
    {
        DispatchKeyEvent(in keyEvent);
    }
}
```

| Order | Handler | Notes |
|---|---|---|
| 1 | `GameSettings.OnKeyAll` | Handles `Cancel` release for the settings window; returns true while `IsUserTyping`, a flag only the **stock** settings text fields set [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L3422-L3435)[^ksa-gamesettings] |
| 2 | `Popup.OnKeyAll` | Popups |
| 3 | `ConsoleWindow.OnKey`, then `!ConsoleWindow.IsOpen` | The developer console keeps its keys |
| 4 | `!ImGui.GetIO().WantCaptureKeyboard` | Present in current builds; see gotchas |
| 5 | `DispatchKeyEvent` | Editor keys, camera, controller, vehicle, `InputAction` bindings such as the HUD toggle |

The engine therefore already offers a "typing" exit at step 1, but only for its own fields. The guard reuses that exit for everyone's.

## Lifecycle and timing

Key callbacks fire while `Program.PrepareFrame` calls `Glfw.PollEvents()` (`frame:input`), before this frame's ImGui frame starts. `WantTextInput` therefore reflects the ImGui state at the
end of the previous frame, which is the frame in which the text field had focus: the correct value for keystrokes typed into it. The patch itself is applied once at startup (unscience applies it first,
from its consolidated `Patcher`, after creating its Harmony instance). See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Add a static helper to a library your mods share, with `Patch(Harmony)`, `Unpatch(Harmony)` and a private prefix. The prefix steps aside while the developer console is open so the console
   keeps receiving keys through its own handler:

```csharp
// ksa-abstractions.lib/HotkeyGuard.cs L10-L45
/// <summary>
/// Blocks game hotkeys (GameSettings.OnKeyAll) whenever an ImGui text input has keyboard focus.
/// Uses the global ImGui WantTextInput flag so every InputText / combo filter is covered automatically.
/// </summary>
public static class HotkeyGuard
{
    private static MethodInfo? _original;
    private static MethodInfo? _prefix;

    public static void Patch(Harmony harmony)
    {
        _original = AccessTools.Method(typeof(GameSettings), nameof(GameSettings.OnKeyAll));
        _prefix = typeof(HotkeyGuard).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static)!;
        harmony.Patch(_original, prefix: new HarmonyMethod(_prefix));
        Console.WriteLine("ksa-abstractions: HotkeyGuard patch applied");
    }

    public static void Unpatch(Harmony harmony)
    {
        if (_original != null && _prefix != null)
            harmony.Unpatch(_original, _prefix);
        _original = null;
        _prefix = null;
        Console.WriteLine("ksa-abstractions: HotkeyGuard patch removed");
    }

    private static bool Prefix(ref bool __result)
    {
        if (!Program.ConsoleWindow.IsOpen && ImGui.GetIO().WantTextInput)
        {
            __result = true;
            return false;
        }
        return true;
    }
}
```

2. Apply it first, from your patcher, and remove it on unload. The canonical per-host form:

```csharp
// fixme-mod-name/Patcher.cs L12-L34
private static Harmony? _harmony = new Harmony("fixme-mod-name");

public static void Patch()
{
    try
    {
        _harmony?.PatchAll(typeof(Patcher).Assembly);
        if (_harmony != null) HotkeyGuard.Patch(_harmony);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"fixme-mod-name: Error applying patches: {ex.Message}");
    }
}

public static void Unload()
{
    try
    {
        if (_harmony != null) HotkeyGuard.Unpatch(_harmony);
        _harmony?.UnpatchAll("fixme-mod-name");
        _harmony = null;
    }
```

   The supermod applies it first and exactly once, isolated so a failure does not abort the other features (`TryApply("hotkey-guard", ...)`).
3. Test it: focus any ImGui text box (a filter field), type the letter bound to a game action, and confirm the game does not react. Expected: no map, no staging, no HUD toggle.

## Complete example

| File | Role |
|---|---|
| [ksa-abstractions.lib/HotkeyGuard.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/HotkeyGuard.cs) | Patch, unpatch and the prefix |
| [fixme-mod-name/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/fixme-mod-name/Patcher.cs) | Canonical per-host usage |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L41-L56) | Applied first in the supermod |

## Variations and gotchas

- **Partly redundant in current builds.** `Program.OnKey` itself refuses to dispatch while `WantCaptureKeyboard` is true. But `GameViewport.DrawImGui` sets the next frame's
  `WantCaptureKeyboard` to false while the 3D view is hovered and no text field is active, so `WantTextInput` is the reliable "a text field is active" signal. The guard also stops
  `Popup.OnKeyAll` and `ConsoleWindow.OnKey` from seeing the keys, and protects builds before the engine check existed.[current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L238-L246)[^ksa-gameviewport]
- **Mouse bindings bypass it.** `Program.OnMouseButton` feeds a bound mouse button straight into `DispatchKeyEvent` (`Input.MouseBound(button)`) and checks only `WantCaptureMouse` on the main viewport;
  see [approach 2](block-game-hotkeys-while-typing-2.md) for gating those.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2003-L2022)[^ksa-program]
- **Side effects of skipping `OnKeyAll`.** Returning true also skips the settings window's own `Cancel` handling and its frame counter for that key.
- **Focus without a text widget.** A focused ImGui window with no active text widget does not set `WantTextInput`; that is the terminal case in approach 2.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of block-game-hotkeys-while-typing](overview.md)
- [Approach 2: gate game input while a terminal has focus](block-game-hotkeys-while-typing-2.md)
- [Share the keyboard and mouse between ImGui, mod tools and the game](../coordinate-imgui-focus-with-game-input/overview.md)
- [Implement rebindable hotkeys](../rebindable-hotkeys/overview.md)
- [Patching private and overloaded game methods by name](../../../patching/internals/patch-a-private-game-method-by-name/overview.md)
- Mod feature: [Hotkey guard](../../../../mods/unscience/features/lib-hotkey-guard.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
