---
type: Mod Feature
title: Menu-bar entry and stock ImGuiWindow subclass drawn from AfterGui
description: A Harmony postfix on the empty Program.DrawProgramMenusHook adds a top-level menu, and a KSA.ImGuiWindow subclass created lazily from that menu is drawn every frame from StarMapAfterGui
mod: ksa-deltavmap
category: ui
techniques: [harmony-postfix, starmap-lifecycle, imgui-window, imgui-menu]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-postfix KSA.Program.DrawProgramMenusHook
  - starmap StarMapAfterGui
  - starmap StarMapUnload
lifecycle: [startup:all-mods-loaded, frame:draw-ui, event:starmap-unload]
game_types: [KSA.Program, KSA.ImGuiWindow, KSA.VersionInfo, KSA.IGameViewport, KSA.GameViewport]
tags: [ui, imgui, menu-bar, harmony, starmap, window, lifecycle]
---

The whole mod is one ImGui window, "Delta-V Map", opened from its own top-level **Delta-V Map**
menu in the main menu bar, in flight and in the vehicle editor. Mechanism: a Harmony postfix on
the game's deliberately empty `Program.DrawProgramMenusHook` draws the menu, the window class
extends the stock `KSA.ImGuiWindow`, and the `[StarMapAfterGui]` hook draws the window every frame
while it is shown.[^dvmap-patch-menubar][^dvmap-mapwindow]

This is the skeleton every other feature of the mod hangs off: the graph build, layout, routing,
staged delta-v read, transfer windows and the 3D map overlay all run from inside
`MapWindow.DrawContent`, so they cost nothing until the player opens the window.

## How it works

1. `[StarMapAllModsLoaded] Mod.OnFullyLoaded` compares `VersionInfo.Current.VersionString` with
   the hardcoded `"v2026.10.10.5554"` and logs a warning on a mismatch, then creates
   `new Harmony("com.maxi.deltavmap")` and applies only `Patch_MenuBar` with
   `CreateClassProcessor(...).Patch()`, inside a try/catch so a failed menu hook leaves the mod
   loaded.[^dvmap-mod]
2. `Patch_MenuBar` is `[HarmonyPatch(typeof(Program), nameof(Program.DrawProgramMenusHook))]`
   with a `[HarmonyPostfix]`. In the decompiled game `DrawProgramMenusHook()` is an empty public
   instance method that `Program.DrawMenuBar` calls after all the stock menus (flight and editor
   branches alike) and before the version text, so a postfix there appends a top-level menu without
   depending on the stock menu layout.[^ksa-program] `DrawMenuBar` runs for the main menu bar from
   `Program.OnDrawUiThreadSafe` (only while the main viewport is the hovered one) and for a hovered
   secondary game viewport from `GameViewport.DrawImGui`, so the menu appears in both bars; it
   returns before the hook while the auto-hide setting keeps the bar hidden.[^ksa-gameviewport] The postfix calls `ImGui.BeginMenu("Delta-V Map"u8)` and one
   `ImGui.MenuItem("Show Map"u8, default(ImString), shown)` that toggles the window.
3. `MapWindow.Instance` is a lazy singleton (`_instance ??= new MapWindow()`). The first access
   happens inside the menu postfix, which matters because the `ImGuiWindow` base constructor
   calls `ImGui.GetMainViewport().GetCenter()` to compute the initial position, so it must run
   inside an ImGui frame.[^ksa-imguiwindow] The constructor passes
   `show: false`, an initial size of 75% of `ImGui.GetMainViewport().Size` (fallback 1100x760),
   and sets `_scrollbar = false` because the canvas pans itself.
4. `[StarMapAfterGui] Mod.Draw(double dt)` calls `MapWindow.DrawActive(Program.MainViewport)`.
   `DrawActive` never creates the instance; it only draws when `_instance != null &&
   _instance.IsShown`, calling the inherited `ImGuiWindow.OnDrawUi(viewport)`. The base class
   does `ImGui.Begin`, the menu bar (`DrawMenuBar`, overridden here to add Zoom, Fit, Reset Root,
   System and Options menus), then the abstract `DrawContent(IViewport)`, then `ImGui.End`.
5. `EnsureTitle` calls `SetWindowTitle("Delta-V Map")` once more right before the first draw,
   because the author found the constructor assignment did not take effect when the instance
   was created nested inside the menu draw.
6. `DrawContent` wraps everything in try/catch and every `BeginChild` in try/finally, because a
   throw escaping `DrawContent` would skip the base class's `ImGui.End` and corrupt the window,
   clip and child stacks for every later frame. Errors go through `LogHelper.ErrorOnce(key, ...)`,
   a HashSet-keyed dedupe so a per-frame failure logs once.[^dvmap-loghelper]
7. `[StarMapUnload] Mod.Unload` calls `_harmony.UnpatchAll(_harmony.Id)`,
   `MapWindow.ResetStatic()` (drops the singleton without touching ImGui, since it can run
   outside a frame), `StagedDv.Reset()` and `LogHelper.Reset()`. The unpatch finds nothing to
   remove: StarMap's `StarMapCore.DeInit` calls the id-less `Harmony.UnpatchAll()` before
   `ModLoader.Dispose` invokes the unload methods.[^starmap-core][^starmap-modloader]

### Settings persistence: none

Every option (layout mode, full ladder, center-on-root, route toggles, visibility toggles,
piloting margin, overlay sizes, the transfer-window toggles) is a field of the `MapWindow`
singleton or of its `ViewOptions` / `RouteOptions` objects, and `ViewOptions.cs` states "All
session-only (not persisted yet)".[^dvmap-viewoptions] The stock `ImGuiWindow.OnDrawUi` also
begins the window with `ImGuiWindowFlags.NoSavedSettings`, so not even the window position and
size reach `imgui.ini`.[^ksa-imguiwindow] Everything resets on game restart and on
`[StarMapUnload]`.

## Lifecycle and timing

| Hook | Where it fires | What the mod does there |
|---|---|---|
| `[StarMapAllModsLoaded]` | Harmony postfix StarMap puts on `ModLibrary.LoadAll`[^starmap-modlibrarypatches] | Version check, apply `Patch_MenuBar` |
| Harmony postfix `Program.DrawProgramMenusHook` | Inside `Program.DrawMenuBar`: from `Program.OnDrawUiThreadSafe` when `HoveredViewport.IsMain()` (after AfterGui), or from `GameViewport.DrawImGui` for a hovered secondary viewport (inside `OnDrawUiViewports`, before AfterGui); skipped while the bar is auto-hidden | Draw the menu, lazily create the window |
| `[StarMapAfterGui]` | Harmony postfix StarMap puts on `Program.OnDrawUiViewports`[^starmap-programpatcher] | Draw the window (and everything inside it) |
| `[StarMapUnload]` | `ModLoader.Dispose`, run from `StarMapCore.DeInit` at shutdown, after StarMap's id-less `UnpatchAll()`[^starmap-modloader][^starmap-core] | Unpatch (a no-op by then), drop static state |

In `Program.OnFrame` (build 2026.10.10.5554) the order is `PrepareFrame` (physics results applied,
next solver batch dispatched) -> `PrepareImGui` -> `OnFrameEditor` -> `OnFrameViewports` ->
`OnDrawUiFrame` -> `OnDrawUiViewports` (AfterGui fires here) -> `OnDrawUiThreadSafe` (menu bar,
`TransferPlanner.DrawPlanWindow`, `Universe.OnDrawUi`, `Editor.OnDrawUi`) -> `ImGui.Render`. The
first two `OnDrawUi*` calls and `OnDrawUiThreadSafe` are all skipped when `Program.DrawUI` is
false (HUD hidden).[^ksa-program] Consequences:

- Everything this mod does runs on the main thread inside a live ImGui frame, so ImGui calls,
  `ImGui.CalcTextSize` text measurement and draw-list access are legal. Camera state for the
  frame was already computed in `OnFrameViewports`.
- The main menu bar is drawn after the AfterGui hook in the same frame. Clicking "Show Map" there
  sets `_show`, and the window first appears on the next frame's AfterGui. A click in a secondary
  viewport's menu bar lands before AfterGui and shows the window in the same frame.
- With the HUD hidden the window, its overlays and the 3D map-mode overlay all disappear.
- The next vehicle-solver batch is already running on worker threads when AfterGui fires, so the
  mod only reads simulated state (orbits, controlled vehicle parent, staged delta-v through a
  private analyzer) and never writes it.

## Key code

| Path | Role |
|---|---|
| [DeltaVMap/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Mod.cs) | StarMap entry: version check, Harmony setup, AfterGui draw, unload |
| [DeltaVMap/Patches/Patch_MenuBar.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Patches/Patch_MenuBar.cs) | Postfix on `Program.DrawProgramMenusHook` adding the top-level menu |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L229-L454) | Singleton, `DrawActive`, constructor, menu bar, guarded `DrawContent` |
| [DeltaVMap/Core/LogHelper.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/LogHelper.cs) | `WarnOnce` / `ErrorOnce` keyed log dedupe |
| [DeltaVMap/Render/ViewOptions.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/ViewOptions.cs) | Session-only display settings |

## Snippets

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

```csharp
// DeltaVMap/Render/MapWindow.cs L229-L266
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
        // ...
        ImGui.BeginChild("##dvmap_canvas_col"u8, new float2?(new float2(canvasWidth, 0f)));
        try
        {
            DrawCanvas(viewport);
        }
        finally
        {
            ImGui.EndChild();
        }
        // ...
    }
    catch (Exception ex)
    {
        LogHelper.ErrorOnce("map-draw", $"[DvMap] Map draw failed: {ex}");
    }
}
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Program.DrawProgramMenusHook` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4005-L4020) | Empty extension hook called by `Program.DrawMenuBar` after the stock menus; postfix target |
| `Program.DrawMenuBar` callers | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3186-L3189), [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L252-L257) | Main menu bar (`OnDrawUiThreadSafe`) and hovered secondary viewport bar (`GameViewport.DrawImGui`) |
| `Program.MainViewport` | same | Viewport passed to the window draw |
| `Program.DrawUI` | same | Gate that skips all UI hooks when the HUD is hidden |
| `ImGuiWindow` (ctor, `OnDrawUi`, `DrawMenuBar`, `DrawContent`, `SetWindowTitle`, `IsShown`, `_show`, `_scrollbar`) | [current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs) | Stock window chrome, View menu (pin, always on top, autohide), Begin/End |
| `VersionInfo.Current.VersionString` | [current/decomp/KSA/VersionInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionInfo.cs) | Tested-build warning (`v{Major}.{Minor}.{Build}.{Revision}{Suffix}`) |

## Pitfalls and version notes

- `ImGuiWindow`'s constructor reads `ImGui.GetMainViewport()`; constructing a window from
  `[StarMapAllModsLoaded]` or from a static initializer runs outside an ImGui frame. Create it
  lazily from a draw path, as this mod does from the menu postfix.
- `MapWindow` does not implement `IStaticWindow`, so `ImGuiWindow.DrawAllStaticWindows` (called
  from `OnDrawUiFrame`) never draws it; the mod draws it itself from AfterGui.
- `DrawProgramMenusHook` is an empty method by design. If a future build removes it or stops
  calling it, the patch throws on apply; the mod catches that and logs once, leaving the map
  unreachable but the game running.
- The version check is a warning only; nothing is disabled on a mismatch.
- No settings survive a restart (see above). A mod that wants persistence has to add its own
  file; the stock window base will not help because of `NoSavedSettings`.

## See also

- [Overview](../overview.md)
- [Live celestial system graph](./live-celestial-system-graph.md)
- [Subway-map canvas rendering](./subway-map-canvas-rendering.md)
- [Map-mode transfer overlay](./map-mode-transfer-overlay.md)
- [In-game validation dumps and perf scopes](./in-game-validation-dumps-and-perf.md)
- [Add a menu with a Harmony postfix on Program.DrawProgramMenusHook](../../../howto/ui/menus/add-items-to-the-game-menu-bar/add-items-to-the-game-menu-bar-1.md) - how-to article demonstrating this feature
- [Stock window base class: a lazily created ImGuiWindow drawn from StarMapAfterGui](../../../howto/ui/windows/subclass-the-stock-imguiwindow/subclass-the-stock-imguiwindow-1.md) - how-to article demonstrating this feature

[^dvmap-mod]: DeltaVMap/Mod.cs
[^dvmap-patch-menubar]: DeltaVMap/Patches/Patch_MenuBar.cs
[^dvmap-mapwindow]: DeltaVMap/Render/MapWindow.cs
[^dvmap-viewoptions]: DeltaVMap/Render/ViewOptions.cs
[^dvmap-loghelper]: DeltaVMap/Core/LogHelper.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-imguiwindow]: KSA.ImGuiWindow (decompiled, build 2026.10.10.5554)
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^starmap-modlibrarypatches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^starmap-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^starmap-core]: StarMap.Core/StarMapCore.cs
