---
type: Mod Feature
title: Block game hotkeys while an ImGui text field has focus
description: A Harmony prefix on GameSettings.OnKeyAll reports the key event as handled whenever ImGui wants text input, so typing into any mod text box never triggers game key bindings
mod: unscience
category: ui
techniques: [harmony-prefix, imgui-window]
hooks: [harmony-prefix KSA.GameSettings.OnKeyAll]
lifecycle: [frame:input]
game_types: [KSA.GameSettings, KSA.Program, Brutal.ImGuiApi.Abstractions.ConsoleWindow]
tags: [input, hotkeys, imgui, harmony, text-input]
---

Mods that add ImGui text boxes (combo filters, preset names, XML paste areas) need the game to
stop treating keystrokes as hotkeys while the player types; otherwise typing "m" opens the map.
`HotkeyGuard` is a two-method helper every unscience host must apply: one prefix on the first
function the game's key callback consults.[^hotkey-guard][^unscience-agents]

Mechanism in one line: a prefix on `GameSettings.OnKeyAll(GlfwKeyEvent)` sets `__result = true`
and skips the original when `ImGui.GetIO().WantTextInput` is set and the game's own console is not
open, which makes `Program.OnKey` treat the key as consumed.

## How it works

`Program.OnKey` (the GLFW key callback) short-circuits on the first handler that returns true:[^ksa-program]

1. `GameSettings.OnKeyAll(keyEvent)` (the settings window's own key handling and its `IsUserTyping` flag)[^ksa-gamesettings]
2. `Popup.OnKeyAll(keyEvent)`
3. `ConsoleWindow.OnKey(...)`, then `!ConsoleWindow.IsOpen`
4. `!ImGui.GetIO().WantCaptureKeyboard`
5. only then `DispatchKeyEvent` (editor keys, camera keys, `InputAction` bindings such as F2)

Because the prefix makes step 1 return true, nothing after it sees the key. The guard uses ImGui's
global `WantTextInput` flag, so every `InputText`, `InputTextWithHint` and filtered combo in every
mod is covered without per-widget work. It steps aside while the developer console is open so the
console keeps receiving keys through its own handler.

Every top-level project's `Patcher.cs` calls `HotkeyGuard.Patch(_harmony)` after creating its
Harmony instance and `HotkeyGuard.Unpatch(_harmony)` on unload; the supermod applies it first and
exactly once. The template host shows the minimal form.[^fixme-patcher][^unscience-patcher]

## Lifecycle and timing

Key callbacks fire while `Program.PrepareFrame` calls `Glfw.PollEvents()` (L2179 in build 5554),
which is `frame:input`, before the ImGui frame for this frame starts. `WantTextInput` therefore
reflects the ImGui state at the end of the previous frame, which is the frame in which the text field
had focus; that is the correct value for keystrokes typed into it.

## Key code

| Path | Role |
|---|---|
| [ksa-abstractions.lib/HotkeyGuard.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/HotkeyGuard.cs) | Patch, unpatch and the prefix |
| [fixme-mod-name/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/fixme-mod-name/Patcher.cs#L14-L39) | Canonical per-host usage |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L56) | Applied first in the supermod |

## Snippets

```csharp
// ksa-abstractions.lib/HotkeyGuard.cs L19-L44
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
```

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `GameSettings.OnKeyAll(GlfwKeyEvent)` | [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L3422-L3434) | Prefix target; first handler in the key chain |
| `Program.OnKey` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1754) | GLFW key callback whose chain the prefix short-circuits |
| `Program.ConsoleWindow.IsOpen` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L285) | Let the console keep its keys |

## Pitfalls and version notes

- Since KSA 5482, `Program.OnKey` itself also refuses to dispatch while
  `ImGui.GetIO().WantCaptureKeyboard` is true (the check is absent in the 5438 decomp), so for game
  bindings the guard is now partly redundant. It still stops `Popup.OnKeyAll` and `ConsoleWindow.OnKey`
  from seeing the keys, and it protects builds before 5482.
- Mouse-button bindings bypass it: `Program.OnMouseButton` dispatches bound mouse buttons through
  `DispatchKeyEvent` directly and only checks `WantCaptureMouse` on the main viewport (build 5554 L2020).
- Returning `true` from `OnKeyAll` also skips the settings window's own Cancel handling while typing.

## See also

- [Standalone mod template](./fixme-mod-name-standalone-mod-template.md)
- [Consolidated patcher](./unscience-consolidated-patcher.md)
- [Shared file browser](./lib-shared-file-library.md) (an ImGui text filter the guard protects)
- [Mod overview](../overview.md)
- [Hook any frame point with Harmony patches on Program and Universe](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md) - how-to article demonstrating this feature
- [Idempotent shared installs with a registration API](../../../howto/patching/harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-3.md) - how-to article demonstrating this feature
- [Block game hotkeys while typing: prefix on GameSettings.OnKeyAll when ImGui wants text input](../../../howto/ui/input/block-game-hotkeys-while-typing/block-game-hotkeys-while-typing-1.md) - how-to article demonstrating this feature

[^hotkey-guard]: ksa-abstractions.lib/HotkeyGuard.cs
[^unscience-agents]: AGENTS.md (repository rule requiring the guard)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^fixme-patcher]: fixme-mod-name/Patcher.cs
[^unscience-patcher]: unscience/Patcher.cs
