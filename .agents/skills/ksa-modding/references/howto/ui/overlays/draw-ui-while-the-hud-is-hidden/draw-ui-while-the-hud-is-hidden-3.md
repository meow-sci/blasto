---
type: How-To
title: "Keep per-frame work running when the HUD is hidden: replay StarMap GUI work from a prefix on OnDrawUiConsole"
description: Re-run the work of your StarMapBeforeGui method on hidden-HUD frames from a Harmony prefix on Program.OnDrawUiConsole, deliberately leaving window drawing out
category: ui
group: overlays
topic: draw-ui-while-the-hud-is-hidden
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/lib-hidden-ui-frame-hook, mods/unscience/features/pebbles-workshop-editor]
techniques: [harmony-prefix, starmap-lifecycle]
hooks: [harmony-prefix KSA.Program.OnDrawUiConsole, starmap StarMapBeforeGui, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, startup:all-mods-loaded]
game_types: [KSA.Program]
tags: [hud-hidden, f2, harmony, ondrawuiconsole, replay, update-loop]
---
StarMap's two per-frame GUI attributes are the natural place for a mod's "every frame" logic, but the game only calls their target methods while the HUD is drawn. Press F2 and every `[StarMapBeforeGui]` and `[StarMapAfterGui]` method silently
stops: timers freeze, animations stall, pending work never runs. unscience's `HiddenUiFrameHook` keeps that work alive by replaying host-registered callbacks from a game method that runs every frame regardless of HUD state. It is
the "update loop" counterpart of the draw-oriented approaches 1 and 2: unscience replays only its non-UI `Update` work, and leaves window drawing hidden with the HUD. Demonstrated by unscience (the supermod's `HiddenUiFrameHook`, used for
its submods including the Pebbles workshop editor's preview refresh). Prerequisites: StarMap and Harmony.

## Background: how the game does it

- StarMap dispatches `[StarMapBeforeGui]` from a Harmony prefix on `Program.OnDrawUiFrame` and `[StarMapAfterGui]` from a postfix on `Program.OnDrawUiViewports`; both are inside `if (DrawUI)` in `Program.OnFrame`.
- `OnFrame` then calls `DrawFps()` and `OnDrawUiConsole(dtPlayer)` unconditionally, and `ImGui.Render` follows:

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

- `OnDrawUiConsole` is a `private void` that draws the developer console window on the hovered viewport.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3119-L3129)[^ksa-program]
- `DrawUI` flips only from key handling in `PrepareFrame` (F2 is `InputAction.ToggleUi`, handled in `DispatchKeyEvent`), from `ScreenshotCapture` for its capture frames (it sets it false and restores it after rendering); nothing else assigns it, and
  the menu bar does not. So it is stable across the UI phase: a frame runs either StarMap's hooks or the replay, never both and never neither.

## Lifecycle and timing

The prefix sits in the same frame phase as the skipped hooks: after `PrepareFrame` (physics already dispatched), after `PrepareImGui` (`NewFrame` done), after `OnFrameViewports` (cameras computed), and before `ImGui.Render` and `OnPreRender`.

| Moment | What happens |
|---|---|
| `Glfw.PollEvents` inside `PrepareFrame` | F2 flips `DrawUI` via `Program.OnKey` -> `DispatchKeyEvent` |
| `ScreenshotCapture.OnPrepareFrame` at the top of `PrepareFrame` | A capture saves `DrawUI` and sets it false; `OnPostRender` restores it |
| `if (DrawUI) { OnDrawUiFrame; OnDrawUiViewports }` | StarMap BeforeGui/AfterGui fire only when visible |
| `OnDrawUiConsole(dtPlayer)` | **Prefix runs; replays only when `DrawUI` is false** |
| `ImGui.Render()` | ImGui frame closes |

What is safe is what is safe in `[StarMapBeforeGui]`: main-thread reads of game state, ImGui, queueing work. Writes to simulated vessel state race the physics step that `PrepareFrame` already dispatched, so those go through a settled-window handoff (see
[Game loop and mod lifecycle, approach 3](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md)). ImGui calls are legal in the replay but would show with the HUD hidden, which is why unscience leaves `AfterGui` empty.

## Steps

1. **Write the helper once**, in a library shared by your mods. The prefix returns at once while the HUD is drawn, otherwise invokes the registered callbacks, each isolated so one exception cannot stop the game's console draw. Patch by string name because the target
   is private, and throw `MissingMethodException` so the caller can isolate the failure:

```csharp
// ksa-abstractions.lib/HiddenUiFrameHook.cs L26-L37,L42-L50,L62-L71
public static class HiddenUiFrameHook
{
    private const string TargetMethodName = "OnDrawUiConsole";

    private static MethodInfo? _original;
    private static MethodInfo? _prefix;

    /// <summary>Work normally done in <c>[StarMapBeforeGui]</c>. Invoked only while the HUD is hidden.</summary>
    public static Action<double>? BeforeGui { get; set; }

    /// <summary>Work normally done in <c>[StarMapAfterGui]</c>. Invoked only while the HUD is hidden, after <see cref="BeforeGui"/>.</summary>
    public static Action<double>? AfterGui { get; set; }
// ...
    public static void Patch(Harmony harmony)
    {
        _original = AccessTools.Method(typeof(Program), TargetMethodName)
            ?? throw new MissingMethodException(typeof(Program).FullName, TargetMethodName);
        _prefix = typeof(HiddenUiFrameHook).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static)!;
        harmony.Patch(_original, prefix: new HarmonyMethod(_prefix));
        Console.WriteLine("ksa-abstractions: HiddenUiFrameHook patch applied");
    }

// ...
    private static void Prefix(double dt)
    {
        if (Program.DrawUI) return; // HUD visible: StarMap already fired the real hooks this frame

        try { BeforeGui?.Invoke(dt); }
        catch (Exception ex) { Console.WriteLine($"ksa-abstractions: HiddenUiFrameHook BeforeGui error: {ex.Message}"); }

        try { AfterGui?.Invoke(dt); }
        catch (Exception ex) { Console.WriteLine($"ksa-abstractions: HiddenUiFrameHook AfterGui error: {ex.Message}"); }
    }
```

2. **Register only the work that must survive F2.** unscience points `BeforeGui` at the same `UpdateSubmods` method its `[StarMapBeforeGui]` handler calls, which runs `ISubmod.Update(dt)` on every sub-module, even hidden ones:

```csharp
// unscience/Mod.cs L132-L136
// While the game HUD is hidden (F2) StarMap's [StarMapBeforeGui]/[StarMapAfterGui]
// never fire (their game targets are skipped), so the non-UI per-frame work is
// replayed from HiddenUiFrameHook at the same frame phase. ImGui rendering is
// deliberately left out so mod windows honour the hidden HUD too.
HiddenUiFrameHook.BeforeGui = UpdateSubmods;
```
```csharp
// unscience/Mod.cs L157-L167
private void UpdateSubmods(double dt)
{
    if (!_isInitialized || _isDisposed) return;

    // Update ALL submods every frame, even hidden ones
    foreach (var submod in _submods)
    {
        try { submod.Update(dt); }
        catch (Exception ex) { Console.WriteLine($"unscience/{submod.Name}: Update error: {ex.Message}"); }
    }
}
```

   `AfterGui` is left unset, so the unscience window, floating windows and the F11 toggle stay hidden with the HUD. The Pebbles workshop editor uses the same path: its preview refresh and offscreen render run in `Update` (so they continue with the HUD
   hidden), while the editor window itself is drawn only from `[StarMapAfterGui]`.
3. **Apply it first and isolated** from your patcher so a missing method only means "mods freeze on F2 again", and remove it on unload:

```csharp
// unscience/Patcher.cs L56-L60
TryApply("hotkey-guard", () => HotkeyGuard.Patch(_harmony!));
// Replays Mod.UpdateSubmods while the HUD is hidden (F2), since StarMap's
// BeforeGui/AfterGui targets are skipped by the game in that state. Callbacks are
// registered in Mod.OnFullyLoaded before Patch() runs.
TryApply("hidden-ui-frame-hook", () => HiddenUiFrameHook.Patch(_harmony!));
```
```csharp
// unscience/Patcher.cs L105-L115
private static void TryApply(string feature, Action apply)
{
    try
    {
        apply();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"unscience: Failed to apply {feature} patches: {ex.Message}");
    }
}
```

## Complete example

| File | Role |
|---|---|
| [ksa-abstractions.lib/HiddenUiFrameHook.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/HiddenUiFrameHook.cs) | The prefix, the two replay delegates and patch/unpatch |
| [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L132-L136) | Registers `UpdateSubmods` as `BeforeGui` |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L56-L60) | Applies the prefix right after `HotkeyGuard` |

## Variations and gotchas

- **Phase contract.** The target must remain a method the game calls every frame after the `if (DrawUI)` block and before `ImGui.Render()`; `DrawFps()` ([approach 2](draw-ui-while-the-hud-is-hidden-2.md)) is the fallback anchor if `OnDrawUiConsole` moves. A miss throws at `Patch()`, is logged
  and skipped by `TryApply`, and the symptom is mods freezing on F2 again.
- **Replaying draws is a choice.** If you do replay `AfterGui`, anything drawn shows with the HUD hidden.
- **Physics-sensitive features do not depend on this hook** in unscience: they run from the `PrepareFrame` handoff, which is HUD-independent.
- **`Exit` frames** (window close, font rebuild): `OnFrame` returns before this section, so the prefix does not run.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of draw-ui-while-the-hud-is-hidden](overview.md)
- [Approach 2: reflection-installed postfix on Program.DrawFps](draw-ui-while-the-hud-is-hidden-2.md)
- [Approach 4: frame-latched step claimed by whichever hook fires first](draw-ui-while-the-hud-is-hidden-4.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Patching private and overloaded game methods by name](../../../patching/internals/patch-a-private-game-method-by-name/overview.md)
- [Isolating patch failures and keeping patch bodies from throwing](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md)
- [Choose and use the right StarMap lifecycle hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md)
- Mod features: [Hidden-UI frame hook](../../../../mods/unscience/features/lib-hidden-ui-frame-hook.md),
  [Pebbles workshop editor](../../../../mods/unscience/features/pebbles-workshop-editor.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
