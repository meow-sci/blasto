---
type: Mod Feature
title: Keep per-frame mod work running while the HUD is hidden
description: A Harmony prefix on Program.OnDrawUiConsole replays a host's StarMap BeforeGui and AfterGui work in frames where F2 or a screenshot capture has hidden the HUD and StarMap's GUI hooks are skipped
mod: unscience
category: patching
techniques: [harmony-prefix, starmap-lifecycle]
hooks:
  - harmony-prefix KSA.Program.OnDrawUiConsole
  - starmap StarMapBeforeGui (the hook being replayed)
  - starmap StarMapAfterGui (the hook being replayed)
lifecycle: [frame:draw-ui]
game_types: [KSA.Program, KSA.ScreenshotCapture, KSA.InputAction]
tags: [starmap, lifecycle, hud, imgui, harmony, timing]
---

StarMap's two per-frame GUI attributes are the natural place for a mod's "every frame" logic, but
the game only calls their target methods while the HUD is drawn. Press F2 and every
`[StarMapBeforeGui]` / `[StarMapAfterGui]` method silently stops: timers freeze, animations stall,
pending work never runs. `HiddenUiFrameHook` keeps that work alive by replaying a host-registered
callback from a game method that runs every frame regardless of HUD state.[^hidden-ui-frame-hook]

Mechanism in one line: a Harmony prefix on the private `Program.OnDrawUiConsole(double)` returns
immediately while `Program.DrawUI` is true, and otherwise invokes the static `BeforeGui` and
`AfterGui` delegates the host registered.

## How it works

- StarMap dispatches `[StarMapBeforeGui]` from a Harmony prefix on `Program.OnDrawUiFrame` and
  `[StarMapAfterGui]` from a postfix on `Program.OnDrawUiViewports`.[^starmap-program-patcher]
- `Program.OnFrame` calls both of those only inside `if (DrawUI)`, then calls `DrawFps()` and
  `OnDrawUiConsole(dtPlayer)` unconditionally (L2265-L2275).[^ksa-program]
- The prefix therefore sits in the same frame phase as the skipped hooks: after `PrepareFrame`
  (physics already dispatched), after `PrepareImGui` (`ImGui.NewFrame` done), after
  `OnFrameViewports` (cameras computed), and before `ImGui.Render()` and `OnPreRender`.
- Exceptions from each callback are caught and logged so the game's console still draws.
- `Patch(Harmony)` resolves the target by string (`"OnDrawUiConsole"`) because the method is private;
  `Unpatch` removes only this prefix and clears both delegates.

unscience registers only its non-UI work: `HiddenUiFrameHook.BeforeGui = UpdateSubmods`, the same
method its `[StarMapBeforeGui]` handler calls, which runs `ISubmod.Update(dt)` on all 31
sub-modules.[^unscience-mod] `AfterGui` is left unset, so the unscience window, floating windows and
the F11 toggle deliberately stay hidden with the HUD. Physics-sensitive features do not depend on
this hook at all: they run from the PrepareFrame handoff, which is HUD-independent.

## Lifecycle and timing

| Moment | Line (build 5554) | What happens |
|---|---|---|
| `Glfw.PollEvents` inside `PrepareFrame` | L2179 | F2 (`InputAction.ToggleUi`, bound to `GlfwKey.F2`) flips `DrawUI` through `Program.OnKey` -> `DispatchKeyEvent` (L1800-L1803)[^ksa-input] |
| `ScreenshotCapture.OnPrepareFrame` at the top of `PrepareFrame` | L2148 | A capture in progress saves `DrawUI` and sets it to `false`; `OnPostRender` restores it[^ksa-screenshot-capture] |
| `if (DrawUI) { OnDrawUiFrame; OnDrawUiViewports }` | L2265-L2269 | StarMap BeforeGui/AfterGui fire only when visible |
| `if (DrawUI) OnDrawUiThreadSafe` | L2270-L2273 | Menu bar, vehicle windows, gauges UI |
| `OnDrawUiConsole(dtPlayer)` | L2275 | **Prefix runs; replays only when `DrawUI` is false** |
| `ImGui.Render()` | L2286 | ImGui frame closes |

Because every write to `DrawUI` happens before the GUI phase (input polling and the screenshot
pre-frame step) or after rendering (screenshot restore), `DrawUI` is stable from L2265 to L2275.
A frame therefore runs either StarMap's hooks or the replay, never both and never neither. ImGui
calls are legal inside the replay (it is inside `NewFrame`...`Render`), but anything drawn there
would show with the HUD hidden, which is why unscience leaves `AfterGui` empty.

What is safe here is the same as in `[StarMapBeforeGui]`: main-thread reads of game state, ImGui,
queueing work. Writes to simulated vessel state race the physics step that `PrepareFrame` already
dispatched, so those go through the [PrepareFrame handoff](./lib-physics-frame-hook.md) instead.

## Key code

| Path | Role |
|---|---|
| [ksa-abstractions.lib/HiddenUiFrameHook.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/HiddenUiFrameHook.cs) | The prefix, the two replay delegates and patch/unpatch |
| [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L132-L167) | Registers `UpdateSubmods` as `BeforeGui`; same method backs `[StarMapBeforeGui]` |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L57-L60) | Applies the prefix right after `HotkeyGuard` |

## Snippets

```csharp
// ksa-abstractions.lib/HiddenUiFrameHook.cs L42-L71
    public static void Patch(Harmony harmony)
    {
        _original = AccessTools.Method(typeof(Program), TargetMethodName)
            ?? throw new MissingMethodException(typeof(Program).FullName, TargetMethodName);
        _prefix = typeof(HiddenUiFrameHook).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static)!;
        harmony.Patch(_original, prefix: new HarmonyMethod(_prefix));
        Console.WriteLine("ksa-abstractions: HiddenUiFrameHook patch applied");
    }

    public static void Unpatch(Harmony harmony)
    {
        if (_original != null && _prefix != null)
            harmony.Unpatch(_original, _prefix);
        _original = null;
        _prefix = null;
        BeforeGui = null;
        AfterGui = null;
        Console.WriteLine("ksa-abstractions: HiddenUiFrameHook patch removed");
    }

    private static void Prefix(double dt)
    {
        if (Program.DrawUI) return; // HUD visible: StarMap already fired the real hooks this frame

        try { BeforeGui?.Invoke(dt); }
        catch (Exception ex) { Console.WriteLine($"ksa-abstractions: HiddenUiFrameHook BeforeGui error: {ex.Message}"); }

        try { AfterGui?.Invoke(dt); }
        catch (Exception ex) { Console.WriteLine($"ksa-abstractions: HiddenUiFrameHook AfterGui error: {ex.Message}"); }
    }
```

The gated block and the unconditional console call in the game loop:

```csharp
// current/decomp/KSA/Program.cs L2265-L2275
		if (DrawUI)
		{
			OnDrawUiFrame(dtPlayer);
			OnDrawUiViewports(dtPlayer);
		}
		if (DrawUI)
		{
			OnDrawUiThreadSafe(dtPlayer);
		}
		DrawFps();
		OnDrawUiConsole(dtPlayer);
```

The host side, one method behind both paths:

```csharp
// unscience/Mod.cs L132-L151
            // While the game HUD is hidden (F2) StarMap's [StarMapBeforeGui]/[StarMapAfterGui]
            // never fire (their game targets are skipped), so the non-UI per-frame work is
            // replayed from HiddenUiFrameHook at the same frame phase. ImGui rendering is
            // deliberately left out so mod windows honour the hidden HUD too.
            HiddenUiFrameHook.BeforeGui = UpdateSubmods;

            _sceneSaves = new UnscienceSaves(_submods);
            Patcher.Patch();

            _isInitialized = true;
            Console.WriteLine($"unscience: Initialized with {_submods.Count} submods");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"unscience: Error during initialization: {ex.Message}");
        }
    }

    [StarMapBeforeGui]
    public void OnBeforeUi(double dt) => UpdateSubmods(dt);
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Program.OnDrawUiConsole(double)` (private) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3119-L3129) | Prefix target; draws the developer console every frame |
| `Program.DrawUI` (static property) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L526-L540) | HUD flag tested by the prefix |
| `Program.OnDrawUiFrame` / `OnDrawUiViewports` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131) | StarMap's targets, skipped when hidden |
| `InputAction.ToggleUi` | [current/decomp/KSA/Input.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Input.cs#L202) | Default F2 binding that toggles `DrawUI` |
| `ScreenshotCapture.OnPrepareFrame` / `OnPostRender` | [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L210) | Hide and restore the HUD around a screenshot capture |

## Pitfalls and version notes

- Target lookup is by string; a rename of `OnDrawUiConsole` makes `Patch` throw (the host logs it and
  carries on without the replay). Present and still called unconditionally in build 5554 (L2275).
- StarMap's own targets are string-named too (`"OnDrawUiFrame"`, `"OnDrawUiViewports"`); if those move,
  StarMap breaks, not this hook.
- Screenshot captures also hide the HUD, so replayed work runs during capture frames as well.
- Do not register UI drawing as `AfterGui` unless you want mod windows to stay visible with the HUD off.
- Since KSA 5402 `OnDrawUiViewports` only draws UI for secondary viewports that have `HasUi`; that does
  not change when StarMap's postfix fires.[^scope-architecture]
- `[StarMapAfterOnFrame]` (postfix on `Program.OnFrame`) is an alternative that always fires, but it runs
  after rendering, outside the ImGui frame and after the next physics step started; unscience does not use it.

## See also

- [PrepareFrame physics handoff](./lib-physics-frame-hook.md)
- [Supermod host and ISubmod driving](./unscience-supermod-host.md)
- [ISubmod contract](./lib-isubmod-contract.md)
- [Consolidated patcher](./unscience-consolidated-patcher.md)
- [Mod overview](../overview.md)
- [Hook any frame point with Harmony patches on Program and Universe](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md) - how-to article demonstrating this feature
- [Keep per-frame work running when the HUD is hidden: replay StarMap GUI work from a prefix on OnDrawUiConsole](../../../howto/ui/overlays/draw-ui-while-the-hud-is-hidden/draw-ui-while-the-hud-is-hidden-3.md) - how-to article demonstrating this feature

[^hidden-ui-frame-hook]: ksa-abstractions.lib/HiddenUiFrameHook.cs
[^starmap-program-patcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^unscience-mod]: unscience/Mod.cs
[^ksa-input]: KSA.Input (decompiled, build 2026.10.10.5554)
[^ksa-screenshot-capture]: KSA.ScreenshotCapture (decompiled, build 2026.10.10.5554)
[^scope-architecture]: scope/00-architecture-and-abstractions.md (authors' lifecycle map)
