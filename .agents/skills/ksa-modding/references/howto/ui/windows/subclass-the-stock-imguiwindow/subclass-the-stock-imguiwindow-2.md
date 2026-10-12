---
type: How-To
title: "Stock window base class: guarded DrawContent with title-bar close detection"
description: Detect the title-bar close button on an ImGuiWindow subclass, share one teardown with the menu close path, guard DrawContent and unload cleanly on StarMapUnload
category: ui
group: windows
topic: subclass-the-stock-imguiwindow
approach: 2
difficulty: beginner
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-measuretools/features/measure-menu-and-tool-window]
techniques: [imgui-window, harmony-postfix, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, starmap StarMapUnload, harmony-postfix KSA.Program.DrawProgramMenusHook]
lifecycle: [frame:draw-ui, event:starmap-unload]
game_types: [KSA.ImGuiWindow, KSA.IViewport, KSA.IGameViewport, KSA.Program]
tags: [window, imguiwindow, close, teardown, menu, guarded, measure]
---
KSA-MeasureTools uses the same base-class skeleton as [approach 1](subclass-the-stock-imguiwindow-1.md) but adds the details a window that owns state needs: the title-bar X closes the window without calling your `Close()`, so the code must notice and run the same teardown; `DrawContent`
is wrapped so an exception cannot leave `ImGui.Begin` unmatched; and the singleton is dropped at unload without touching ImGui. Demonstrated by KSA-MeasureTools. Prerequisites: StarMap and Harmony.

## Background: how the game does it

- `ImGuiWindow.OnDrawUi` calls `ImGui.Begin(_windowTitle, ref _show, flags)`: the title-bar close button writes `_show = false` directly, without going through any method you defined. If `Begin` returns false the base also sets `_show = false` and clears its hover and focus flags.[current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L248-L287)[^ksa-imguiwindow]
- `DrawContent` is called inside `Begin`/`End` with no `try`/`finally`, so a throw escapes before `ImGui.End()`.
- `DrawMenuBar` is virtual; the base draws the **View** menu (always on top, detach, autohide).[current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L416-L458)[^ksa-imguiwindow]
- The menu entry hook is the empty `Program.DrawProgramMenusHook`; see [approach 1](subclass-the-stock-imguiwindow-1.md) for how it is called.

## Lifecycle and timing

| Hook | Where it fires | Work |
|---|---|---|
| Postfix on `Program.DrawProgramMenusHook` | `Program.DrawMenuBar` from `OnDrawUiThreadSafe` (after AfterGui) or from `GameViewport.DrawImGui` for a hovered secondary viewport's bar | Menu; lazy window creation |
| `[StarMapAfterGui]` | Postfix on `Program.OnDrawUiViewports` | `Prune`, window, then the overlay |
| `[StarMapUnload]` | StarMap shutdown, after the game loop has returned | `MeasureWindow.ResetStatic()` drops the instance without touching ImGui |

AfterGui runs before `OnDrawUiThreadSafe`, so in a frame the window is drawn before the menu bar: a click on "Show Window" shows the window from the next frame. All of it is on the main thread inside the ImGui frame and skipped when the HUD is hidden. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Create the window lazily and place it.** The constructor runs only from the menu hook, so an ImGui frame is always active; move the initial position after the base constructor to keep clear of the map area:

```csharp
// MeasureTools/Features/Measure/MeasureWindow.cs L21-L33
public static bool IsOpen => _instance != null && _instance.IsShown;

private MeasureWindow()
    : base(new float2(500f, 460f), lockAspectRatio: false, show: false)
{
    SetWindowTitle("Measure");
    // Default to the upper left, right of the stock Map View panel, so the
    // window stays clear of the map area where measurements are placed. The base
    // constructor already calls into ImGui, and this only runs from the menu
    // hook, so an ImGui frame is always active here.
    ImGuiViewportPtr mainViewport = ImGui.GetMainViewport();
    _initialPosition = new float2(mainViewport.Pos.X + 320f, mainViewport.Pos.Y + 80f);
}
```

2. **Draw only when shown, then detect a title-bar close.** `DrawActive` does not create the instance. After `OnDrawUi`, if `IsShown` flipped to false the X was clicked, so run the same teardown as the menu close path:

```csharp
// MeasureTools/Features/Measure/MeasureWindow.cs L35-L50
// Draw the window if it exists and is shown. Does not create the instance, so
// the draw hook never touches ImGui state before the user first opens the tool.
public static void DrawActive(IGameViewport viewport)
{
    if (_instance == null || !_instance.IsShown)
        return;
    _instance.OnDrawUi(viewport);
    // The title bar close button flips _show without going through Close();
    // measurements are ephemeral, so treat it the same.
    if (!_instance.IsShown)
    {
        _instance.OnClosed();
        if (DebugConfig.Measure)
            DefaultCategory.Log.Debug("[MeasureTools] Tool closed via title bar, measurements cleared.");
    }
}
```

3. **Share one teardown** for `Close()` and the X button: clear measurements, drop caches that must not outlive the window, save edited settings:

```csharp
// MeasureTools/Features/Measure/MeasureWindow.cs L58-L83
public void Open()
{
    _show = true;
    // Opening always arms the tool, even if it was paused before closing.
    MeasureState.SetToolActive(true);
    if (DebugConfig.Measure)
        DefaultCategory.Log.Debug("[MeasureTools] Tool opened.");
}

public void Close()
{
    _show = false;
    OnClosed();
    if (DebugConfig.Measure)
        DefaultCategory.Log.Debug("[MeasureTools] Tool closed, measurements cleared.");
}

// Shared teardown for both close paths (Close and the title-bar button):
// measurements are ephemeral by design, the combo cache must not outlive
// the window, and edited colors persist here.
private void OnClosed()
{
    MeasureState.ClearAll();
    ResetAutoPreview();
    MeasureColors.SaveIfDirty();
}
```

4. **Add a menu by overriding `DrawMenuBar`** and calling the base first:

```csharp
// MeasureTools/Features/Measure/MeasureWindow.cs L93-L109
// Quick palette switching from the menu bar, without opening the Colors
// section.
public override void DrawMenuBar()
{
    base.DrawMenuBar();
    if (ImGui.BeginMenu("Palette"u8))
    {
        for (int i = 0; i < MeasureColors.Palettes.Count; i++)
        {
            ImGui.PushID(i);
            if (ImGui.MenuItem(MeasureColors.Palettes[i].Name, default(ImString), i == MeasureColors.ActiveIndex))
                MeasureColors.SetActive(i);
            ImGui.PopID();
        }
        ImGui.EndMenu();
    }
}
```

5. **Guard `DrawContent`** with a try/catch that logs each exception type once:

```csharp
// MeasureTools/Features/Measure/MeasureWindow.cs L111-L123
// The stock shell wraps this call in no finally, so an escaping throw would leave
// ImGui.Begin unmatched and the style stack pushed for the rest of the frame.
public override void DrawContent(IViewport viewport)
{
    try
    {
        DrawContentCore(viewport);
    }
    catch (Exception ex)
    {
        LogHelper.ErrorOnce("window-" + ex.GetType().Name, $"[MeasureTools] Window draw failed: {ex}");
    }
}
```

6. **Order the work in AfterGui**: prune stale state first, then the window, then the overlay, all behind one `try`:

```csharp
// MeasureTools/Mod.cs L57-L78
    // Prune first, so neither the window nor the overlay resolves an anchor whose
    // body is gone.
    [StarMapAfterGui]
    public void Draw(double dt)
    {
        try
        {
#if DEBUG
            using var perfScope = new PerfTracker.Scope("Mod.Draw");
#endif
            MeasureState.Prune();
            if (!MeasureViewport.TryGetActive(out IGameViewport viewport))
                return;
            MeasureWindow.DrawActive(viewport);
            MeasureOverlay.Draw(viewport);
        }
        catch (Exception ex)
        {
            // Per-frame path: first throw of each type logs a stack, then quiet.
            LogHelper.ErrorOnce("aftergui-" + ex.GetType().Name, $"[MeasureTools] Per-frame draw failed: {ex}");
        }
    }
```

## Complete example

| File | Role |
|---|---|
| [MeasureTools/Patches/Patch_MenuBar.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MenuBar.cs) | Menu postfix |
| [MeasureTools/Features/Measure/MeasureWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureWindow.cs#L21-L123) | Lazy singleton, `DrawActive`, open/close, Palette menu, guarded `DrawContent` |
| [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L57-L78) | AfterGui order |

## Variations and gotchas

- **`ImGuiWindow.OnDrawUi` uses `NoSavedSettings`**, so position and size do not persist; see [approach 3](subclass-the-stock-imguiwindow-3.md).
- **Do not run teardown from `ResetStatic`.** It can run outside a frame (from `[StarMapUnload]`) and must not touch ImGui.
- **A missing `finally`** in your own children (`BeginChild`, `BeginTable`) has the same effect as an escaping `DrawContent`; pair every `Begin*` with `End*` in a `finally`.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of subclass-the-stock-imguiwindow](overview.md)
- [Approach 1: lazily created window drawn from StarMapAfterGui](subclass-the-stock-imguiwindow-1.md)
- [Make mod UI look like the game: console widgets](../match-the-stock-console-ui-style/overview.md)
- [Show live vehicle data in a window](../show-live-vehicle-data-in-a-window/overview.md)
- Mod feature: [Measure menu and tool window](../../../../mods/ksa-measuretools/features/measure-menu-and-tool-window.md)

[^ksa-imguiwindow]: KSA.ImGuiWindow (decompiled, build 2026.10.10.5554)
