---
type: How-To
title: "Stock window base class: a lazily created ImGuiWindow drawn from StarMapAfterGui"
description: Derive from KSA.ImGuiWindow, create it lazily inside an ImGui frame from a menu hook, draw it every frame from the after-GUI hook and balance the ImGui stacks if drawing throws
category: ui
group: windows
topic: subclass-the-stock-imguiwindow
approach: 1
difficulty: beginner
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-deltavmap/features/map-window-and-menu-entry]
techniques: [imgui-window, harmony-postfix, starmap-lifecycle]
hooks:
  - starmap StarMapAllModsLoaded
  - starmap StarMapAfterGui
  - starmap StarMapUnload
  - harmony-postfix KSA.Program.DrawProgramMenusHook
lifecycle: [frame:draw-ui, startup:all-mods-loaded, event:starmap-unload]
game_types:
  - KSA.ImGuiWindow
  - KSA.IViewport
  - KSA.IGameViewport
  - KSA.VersionInfo
  - KSA.Program
  - Brutal.ImGuiApi.ImGui
tags: [window, imguiwindow, menu, lazy, drawcontent, version-check]
---
The least code that gives you a window that looks and behaves like the game's own: subclass `KSA.ImGuiWindow`, implement `DrawContent`, and call the inherited `OnDrawUi` each frame while it is shown. You get the title bar, collapse and close handling, the
stock **View** menu (always on top, detach, autohide) and a menu bar for free. KSA-DeltaVMap builds its whole mod on this skeleton: a top-level **Delta-V Map** menu (a postfix on the game's empty `Program.DrawProgramMenusHook`) opens a
`MapWindow` that is created on first use and drawn from `[StarMapAfterGui]`. Demonstrated by KSA-DeltaVMap. Prerequisites: StarMap and Harmony.

## Background: how the game does it

`ImGuiWindow` is a public abstract class. Its members that matter to a mod:[current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L10-L60)[^ksa-imguiwindow]

- **Constructor** `protected ImGuiWindow(float2 initialSize, bool lockAspectRatio = false, bool show = true)` computes the initial position from `ImGui.GetMainViewport().GetCenter()` and the debug name; it therefore needs an active ImGui context. A subclass that implements
  `IStaticWindow` is added to a static list (approach 3).[current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L99-L126)[^ksa-imguiwindow]
- **`OnDrawUi(IGameViewport)`** is public and non-virtual. For the default chrome it builds `NoCollapse | NoSavedSettings` plus a `MenuBar`, applies `SetNextWindowPos(_initialPosition, ImGuiCond.Once)` and `SetNextWindowSize(_initialSize, ImGuiCond.Once)`, pins the window to the main viewport
  unless detached, calls `ImGui.Begin(_windowTitle, ref _show, flags)`, draws the menu bar through `DrawMenuBar()`, then `DrawContent(viewport)`, then `ImGui.End()`. When `Begin` returns false it sets `_show = false`.[current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L181-L287)[^ksa-imguiwindow]
- **`DrawContent(IViewport)`** is the abstract member you implement. `DrawMenuBar()` and `DrawViewMenu()` are virtual and draw the **View** menu; override `DrawMenuBar` and call `base.DrawMenuBar()` to add menus.[current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L416-L458)[^ksa-imguiwindow]
- **State:** `IsShown`, protected `_show`, `SetShown`, `Toggle`, `SetWindowTitle(name)`, protected `_scrollbar`, `_noMove`, `_noResize`, `_noChrome`, `_initialPosition`, `_initialSize`.
- **Title and id.** The ImGui window name is `"Window###" + _debugName`, and `SetWindowTitle(name)` makes it `name + "###" + _debugName`, where `_debugName` is the subclass's full type name plus an underscore (no per-instance part); the id part after `###` is what ImGui identifies the window by.[current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L89-L97)[^ksa-imguiwindow][current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L140-L144)[^ksa-imguiwindow]
- **The extension point for the menu.** `Program.DrawProgramMenusHook()` is an empty public instance method that `Program.DrawMenuBar` calls after all stock menus and before the version text; a Harmony postfix there appends a top-level menu without depending on the stock menu layout.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4005)[^ksa-program][current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4018-L4020)[^ksa-program]
  `DrawMenuBar` runs for the main bar from `OnDrawUiThreadSafe` (only while the main viewport is hovered) and for a hovered secondary viewport's bar from `GameViewport.DrawImGui`; it returns before the hook while the bar is auto-hidden.

## Lifecycle and timing

| Hook | Where it fires | Work |
|---|---|---|
| `[StarMapAllModsLoaded]` | Harmony postfix on `ModLibrary.LoadAll` (`startup:all-mods-loaded`) | Version check; apply the menu patch |
| Postfix on `Program.DrawProgramMenusHook` | Inside `DrawMenuBar` (`frame:draw-ui`, in `OnDrawUiThreadSafe`, after AfterGui) | Draw the menu; lazily create the window |
| `[StarMapAfterGui]` | Postfix on `Program.OnDrawUiViewports` (`frame:draw-ui`) | Draw the window |
| `[StarMapUnload]` | `ModLoader.Dispose` at shutdown, after StarMap's id-less `UnpatchAll()` | Drop static state without touching ImGui |

The main menu bar is drawn after the AfterGui hook in the same frame, so clicking "Show Map" there sets `_show` and the window first appears on the next frame; a click in a secondary viewport's menu bar lands before AfterGui and shows it the same frame. Everything runs on the
main thread inside a live ImGui frame; with the HUD hidden the window disappears. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and
[keep UI running when the HUD is hidden](../../overlays/draw-ui-while-the-hud-is-hidden/overview.md).

## Steps

1. **Warn when the game build differs from the one you tested.** `VersionInfo.Current.VersionString` is `v{Major}.{Minor}.{Build}.{Revision}{Suffix}`; apply only the menu patch, inside a try/catch, so a missing hook leaves the mod loaded:

```csharp
// DeltaVMap/Mod.cs L20-L44
private const string TestedGameVersion = "v2026.10.10.5554";

[StarMapAllModsLoaded]
public void OnFullyLoaded()
{
    string gameVersion = VersionInfo.Current.VersionString;
    DefaultCategory.Log.Info($"[DvMap] Game version: {gameVersion}");
    if (gameVersion != TestedGameVersion)
        DefaultCategory.Log.Warning(
            $"[DvMap] Tested against {TestedGameVersion}, current is {gameVersion}. " +
            "Some features may not work correctly.");

    _harmony = new Harmony("com.maxi.deltavmap");
    try
    {
        _harmony.CreateClassProcessor(typeof(Patch_MenuBar)).Patch();
    }
    catch (Exception ex)
    {
        // A missing menu hook leaves the map unreachable but must not unload the mod.
        LogHelper.ErrorOnce("patch-" + nameof(Patch_MenuBar), $"[DvMap] Failed to apply the menu bar patch: {ex}");
    }

    DefaultCategory.Log.Info("[DvMap] Loaded.");
}
```

2. **Add the menu with a postfix on the empty hook.** Opening and closing go through the window's `Open()`/`Close()`:

```csharp
// DeltaVMap/Patches/Patch_MenuBar.cs L13-L32
[HarmonyPatch(typeof(Program), nameof(Program.DrawProgramMenusHook))]
internal static class Patch_MenuBar
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        if (!ImGui.BeginMenu("Delta-V Map"u8))
            return;

        bool shown = MapWindow.Instance.IsShown;
        if (ImGui.MenuItem("Show Map"u8, default(ImString), shown))
        {
            if (shown)
                MapWindow.Instance.Close();
            else
                MapWindow.Instance.Open();
        }
        ImGui.EndMenu();
    }
}
```

3. **Make the window a lazy singleton, constructed only from a draw path** (the base constructor reads ImGui), initially hidden, with a size derived from the main viewport. `DrawActive` never creates the instance:

```csharp
// DeltaVMap/Render/MapWindow.cs L229-L239,L260-L266
public static MapWindow Instance => _instance ??= new MapWindow();

// Never creates the instance: the draw hook must not touch ImGui before the user opens the map.
public static void DrawActive(IGameViewport viewport)
{
    if (_instance != null && _instance.IsShown)
    {
        _instance.EnsureTitle();
        _instance.OnDrawUi(viewport);
    }
}
// ...
private MapWindow()
    : base(ComputeInitialSize(), lockAspectRatio: false, show: false)
{
    SetWindowTitle("Delta-V Map");
    // The canvas handles its own panning, so suppress the window scrollbar.
    _scrollbar = false;
}
```

4. **Re-apply the title right before the first draw** if the constructor ran nested inside another window's draw: the author found the constructor's `SetWindowTitle` did not take effect when the instance was created inside the menu draw, so `EnsureTitle` repeats it once at top level:

```csharp
// DeltaVMap/Render/MapWindow.cs L241-L251
// Re-assert the window title at top level just before the first draw. The
// constructor (the conventional place, and where TargetTrackWindow does it) runs
// nested inside the game's View-menu draw, where the assignment did not take effect
// in-game; setting it here, right before the base class calls ImGui.Begin, does.
private void EnsureTitle()
{
    if (_titleApplied)
        return;
    SetWindowTitle("Delta-V Map");
    _titleApplied = true;
}
```

5. **Draw from `[StarMapAfterGui]`**, and drop the singleton at unload without touching ImGui:

```csharp
// DeltaVMap/Mod.cs L50-L54
[StarMapAfterGui]
public void Draw(double dt)
{
    MapWindow.DrawActive(Program.MainViewport);

```
```csharp
// DeltaVMap/Mod.cs L74-L81
[StarMapUnload]
public void Unload()
{
    _harmony?.UnpatchAll(_harmony.Id);
    _harmony = null;
    _validationDumped = false;

    MapWindow.ResetStatic();
```

6. **Guard `DrawContent`.** The stock shell calls it with no `finally` around it, so an exception escaping `DrawContent` skips the base class's `ImGui.End` and corrupts the window, clip and child stacks for every later frame. Wrap the body in try/catch, every `BeginChild` in try/finally,
   and log through a deduplicating helper so a per-frame failure logs once:

```csharp
// DeltaVMap/Render/MapWindow.cs L403-L454
public override void DrawContent(IViewport viewport)
{
    // A throw escaping here skips the base class's ImGui.End and corrupts the window,
    // clip and child stacks for every later frame. Hence the catch and the finally pairs.
    try
    {
        if (!EnsureBuilt())
        {
            ImGui.TextDisabled("Delta-V map unavailable (no system loaded)."u8);
            return;
        }

        float2 avail = ImGui.GetContentRegionAvail();
        // Wide enough that the longest breakdown line ("Capture at <body> (ellipse):
        // ~X,XXX m/s") does not clip on the right.
        float panelWidth = (float)Math.Clamp(avail.X * 0.30, 320.0, 470.0);
        float canvasWidth = avail.X - panelWidth - 8f;

        // Too narrow to split; canvas alone.
        if (canvasWidth < 220f)
        {
            DrawCanvas(viewport);
            return;
        }

        ImGui.BeginChild("##dvmap_canvas_col"u8, new float2?(new float2(canvasWidth, 0f)));
        try
        {
            DrawCanvas(viewport);
        }
        finally
        {
            ImGui.EndChild();
        }

        ImGui.SameLine();

        ImGui.BeginChild("##dvmap_panel_col"u8, new float2?(new float2(panelWidth, 0f)));
        try
        {
            DrawPanel();
        }
        finally
        {
            ImGui.EndChild();
        }
    }
    catch (Exception ex)
    {
        LogHelper.ErrorOnce("map-draw", $"[DvMap] Map draw failed: {ex}");
    }
}
```

7. **Add menus by overriding `DrawMenuBar`** and calling the base so the View menu stays.

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Mod.cs) | StarMap entry: version check, Harmony setup, AfterGui draw, unload |
| [DeltaVMap/Patches/Patch_MenuBar.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Patches/Patch_MenuBar.cs) | Postfix on `Program.DrawProgramMenusHook` |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs) | Singleton, `DrawActive`, constructor, guarded `DrawContent` |
| [DeltaVMap/Core/LogHelper.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/LogHelper.cs) | `WarnOnce` / `ErrorOnce` keyed log dedupe |

## Variations and gotchas

- **Constructing outside a frame.** Creating the window from `[StarMapAllModsLoaded]` or a static initializer runs outside an ImGui frame and fails; create it from a draw path.
- **`NoSavedSettings` always.** The base `OnDrawUi` begins the window with `NoSavedSettings`, so position and size never reach `imgui.ini`; every option here is session-only. For persistence see [approach 3](subclass-the-stock-imguiwindow-3.md).
- **Not an `IStaticWindow`.** `ImGuiWindow.DrawAllStaticWindows` (called from `OnDrawUiFrame`) never draws it; the mod draws it itself.
- **Hook can disappear.** If a future build removes or stops calling `DrawProgramMenusHook`, applying the patch throws; the mod catches that and logs once, leaving the map unreachable but the game running.
- **Version.** The version check is a warning only. Checked against 2026.10.10.5554.

## See also

- [Overview of subclass-the-stock-imguiwindow](overview.md)
- [Approach 2: guarded DrawContent with title-bar close detection](subclass-the-stock-imguiwindow-2.md)
- [Approach 3: IStaticWindow auto-registration with persisted layout](subclass-the-stock-imguiwindow-3.md)
- [Make mod UI look like the game](../match-the-stock-console-ui-style/overview.md)
- [Add a menu to the game's top menu bar](../../menus/add-items-to-the-game-menu-bar/overview.md)
- [Persist ImGui window state and layouts](../../../persistence/user-data/persist-imgui-window-state/overview.md)
- Mod feature: [Map window and menu entry](../../../../mods/ksa-deltavmap/features/map-window-and-menu-entry.md)

[^ksa-imguiwindow]: KSA.ImGuiWindow (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
