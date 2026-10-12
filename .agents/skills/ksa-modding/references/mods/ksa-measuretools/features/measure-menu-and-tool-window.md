---
type: Mod Feature
title: Measure menu and a stock-styled tool window with hover sync and click-to-copy
description: A postfix on the empty Program.DrawProgramMenusHook adds a Measure menu in flight and editor, opening a KSA.ImGuiWindow subclass built from ConsoleWidgets rows whose list syncs hover with the overlay and copies rows to the clipboard
mod: ksa-measuretools
category: ui
techniques: [harmony-postfix, starmap-lifecycle, imgui-window, imgui-menu]
hooks:
  - harmony-postfix KSA.Program.DrawProgramMenusHook
  - starmap StarMapAfterGui
  - starmap StarMapAllModsLoaded
  - starmap StarMapUnload
lifecycle: [frame:draw-ui, startup:all-mods-loaded, event:starmap-unload]
game_types:
  - KSA.Program
  - KSA.ImGuiWindow
  - KSA.ConsoleWidgets
  - KSA.ConsoleStyle
  - KSA.ImGuiHelper
  - KSA.DistanceReference
  - KSA.RadianReference
  - KSA.IGameViewport
tags: [ui, imgui, menu-bar, window, console-widgets, clipboard, hover-sync, table]
---

The tool is one window, "Measure", opened from a new top-level **Measure -> Show Window** menu in the
main menu bar, in flight and in the vehicle editor. It holds a five-button mode toolbar (Ruler,
Protractor, Surface, Circle, Face angle), a status line, snapping toggles, the reference-body combo,
the measurement list, and the color settings. Hovering a row brightens that measurement in the view;
clicking a row copies it.[^mt-readme]

## How it works

### Menu entry

`Patch_MenuBar` is a `[HarmonyPostfix]` on `Program.DrawProgramMenusHook`, an empty public instance
method that `Program.DrawMenuBar` calls after the stock menus in both flight and editor branches.
The postfix draws `ImGui.BeginMenu("Measure")` with one `MenuItem("Show Window", ..., shown)` that
calls `Open()` or `Close()`.[^mt-patch-menubar][^ksa-program] Accessing `MeasureWindow.Instance`
lazily creates the window, and doing that inside the menu means it is created inside an active ImGui
frame, which the `ImGuiWindow` constructor needs (it reads `ImGui.GetMainViewport()` for the initial
position).[^ksa-imguiwindow] The constructor then moves the initial position to the upper left (320 px right and 80 px down from
the main viewport origin) to stay clear of the map area.

### Window drawing

`MeasureWindow : ImGuiWindow` is never registered as a static window; `Mod.Draw` (`[StarMapAfterGui]`)
calls `MeasureWindow.DrawActive(viewport)` for the main viewport, which only draws when the instance
exists and is shown, through the inherited `ImGuiWindow.OnDrawUi`.[^mt-mod][^mt-window]

- The base class begins the window with `NoCollapse | NoSavedSettings` and a menu bar; the override
  of `DrawMenuBar` keeps the stock View menu and adds a **Palette** menu for one-click palette
  switching.
- `DrawContent` wraps everything in a `try/catch`, because the stock shell calls it with no
  `finally`, so an escaping exception would leave `ImGui.Begin` unmatched.
- Closing by the title-bar X flips `_show` without calling `Close()`; `DrawActive` detects the
  change after drawing and runs the same teardown (`ClearAll`, drop the cached combo body, save colors
  if dirty). Measurements are ephemeral by design.
- `ConsoleUi.CheckboxRow` lays rows out with the game's `ConsoleWidgets.BeginRow` / `Checkbox(id, ref
  value, pending: false)` / `RowHovered` / `Tooltip` / `EndRow`, so the snapping section looks like a
  stock console panel without setting `UseConsoleChrome`.[^mt-consoleui][^ksa-consolewidgets]
- The mode toolbar uses `ConsoleWidgets.PrimaryButton` for the active mode and `Button` otherwise,
  sized to fit five across, or 3 + 2 rows when "Protractor" would not fit. No button is highlighted
  while the tool is paused; any button re-arms.
- Nested `ImGui.BeginDisabled` blocks grey out the part-snap sub-toggles under the master toggle and
  all snap controls in modes that ignore them (Surface, Circle, Face angle).

### Hover sync

`MeasureState.HighlightIndex` is reset to -1 at the start of `DrawMeasurementList` and set to the row
index when the row's remove button, value cell or endpoints cell is hovered. `Mod.Draw` calls the
window draw before the overlay draw in the same AfterGui call, so the overlay reads the index set this
frame and draws that measurement thicker in the highlight color.[^mt-state][^mt-overlay] When the list
header is collapsed the index is cleared.

### Click-to-copy

Value and endpoints cells are `ImGui.Selectable`s (the value right-aligned with
`ImGuiStyleVar.SelectableTextAlign`). A click builds the copy text only then, because it resolves
anchors and concatenates strings, and passes it to `ImGui.SetClipboardText`. The text is the full
data line, not just the headline: arm lengths for the protractor, chord and bearing for surface
measurements, radius and circumference for circles, axial and radial components for same-vehicle part
rulers, then the endpoint labels in parentheses. Numbers come from the game's own formatters
(`DistanceReference.ToNearest`, `RadianReference.FromRadians(...).ToStringDegrees`), so units and
precision match the stock UI.[^ksa-distancereference][^ksa-radianreference] Hovering shows a tooltip
with the full endpoint text, since the column truncates long part names.

## Lifecycle and timing

| Hook | Where it fires | Work |
|---|---|---|
| Harmony postfix `Program.DrawProgramMenusHook` | `Program.DrawMenuBar` from `OnDrawUiThreadSafe` when the main viewport is hovered (after AfterGui), and from `GameViewport.DrawImGui` inside `OnDrawUiViewports` for a hovered secondary viewport's own bar (before AfterGui) [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L252-L257); skipped while the bar is auto-hidden | Menu; lazy window creation. The menu therefore also appears in secondary viewports' bars, but the window and the tool only ever serve the main viewport |
| `[StarMapAfterGui]` | Postfix StarMap puts on `Program.OnDrawUiViewports`[^starmap-programpatcher] | `Prune`, window, overlay |
| `[StarMapUnload]` | StarMap shutdown, after the game loop has returned | `MeasureWindow.ResetStatic()` drops the instance without touching ImGui |

AfterGui runs before `OnDrawUiThreadSafe`, so in a frame the window is drawn before the menu bar. A
click on "Show Window" therefore shows the window from the next frame. All of it is inside the ImGui
frame on the main thread and is skipped when the HUD is hidden (`Program.DrawUI == false`).[^ksa-program]

## Key code

| Path | Role |
|---|---|
| [MeasureTools/Patches/Patch_MenuBar.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MenuBar.cs) | Menu postfix |
| [MeasureTools/Features/Measure/MeasureWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureWindow.cs#L13-L123) | Lazy singleton, `DrawActive`, open/close, Palette menu, guarded `DrawContent` |
| [MeasureTools/Features/Measure/MeasureWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureWindow.cs#L434-L584) | List, hover sync, copy text |
| [MeasureTools/Core/ConsoleUi.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Core/ConsoleUi.cs) | Stock console-row helpers |
| [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L57-L78) | AfterGui order: prune, window, overlay |

## Snippets

```csharp
// MeasureTools/Patches/Patch_MenuBar.cs L14-L33
[HarmonyPatch(typeof(Program), nameof(Program.DrawProgramMenusHook))]
internal static class Patch_MenuBar
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        if (!ImGui.BeginMenu("Measure"u8))
            return;

        bool shown = MeasureWindow.IsOpen;
        if (ImGui.MenuItem("Show Window"u8, default(ImString), shown))
        {
            if (shown)
                MeasureWindow.Instance.Close();
            else
                MeasureWindow.Instance.Open();
        }
        ImGui.EndMenu();
    }
}
```

```csharp
// MeasureTools/Features/Measure/MeasureWindow.cs L487-L508
// Right-aligned value cell.
ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new float2(1f, 0f));
bool copy = ImGui.Selectable(value);
ImGui.PopStyleVar();
hovered |= CopyTooltipOnHover(endpoints);

ImGui.TableNextColumn();
copy |= ImGui.Selectable(endpoints);
hovered |= CopyTooltipOnHover(endpoints);

if (copy)
{
    // Built only on the click: the full data set resolves anchors and
    // concatenates several strings, waste on the 99.9% of frames
    // where no row is clicked.
    string copyText = BuildCopyText(m, value, endpoints, buffer);
    ImGui.SetClipboardText(copyText);
    // ...
}
if (hovered)
    MeasureState.HighlightIndex = i;
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Program.DrawProgramMenusHook`, `DrawMenuBar`, `OnDrawUiThreadSafe` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3455-L4020) | Empty hook called after the stock menus (L4005); bar auto-hide early return at the top |
| `ImGuiWindow` (ctor, `OnDrawUi`, `DrawMenuBar`, `DrawContent`, `SetWindowTitle`, `IsShown`, `_show`, `_initialPosition`) | [current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L98-L215) | Window chrome and lifecycle |
| `ConsoleWidgets.BeginRow`, `Checkbox`, `RowHovered`, `Tooltip`, `EndRow`, `Button`, `PrimaryButton`, `ButtonHeight` | [current/decomp/KSA/ConsoleWidgets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleWidgets.cs) | Stock-styled rows and buttons |
| `ConsoleStyle.PushValueFont`, `TextMuted`, `PopFont` | [current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs) | Muted "Reference: edited vehicle" text |
| `DistanceReference.ToNearest`, `RadianReference.FromRadians`, `ToStringDegrees` | [current/decomp/KSA/DistanceReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DistanceReference.cs#L167-L180) | Stock unit formatting into stack buffers |

## Pitfalls and version notes

- Create `ImGuiWindow` subclasses lazily from a draw path; the constructor calls into ImGui.
- `ImGuiWindow.OnDrawUi` uses `NoSavedSettings`, so window position and size do not persist; the
  mod persists only its colors.
- Guard `DrawContent` yourself; the base class does not `End` the window on an exception.
- The title-bar close path bypasses your `Close()`; detect `IsShown` turning false after the draw.
- The README's usage line mentions **View -> Measure** for flight; at this ref the code only adds the
  top-level **Measure** menu.

## See also

- [Overview](../overview.md)
- [Overlay rendering](./overlay-drawlist-rendering.md)
- [Color palettes](./color-palettes-persistence.md)
- [Editor measuring](./editor-measuring.md)
- [KSA-DeltaVMap: the same menu and window pattern](../../ksa-deltavmap/features/map-window-and-menu-entry.md)
- [Hook any frame point with Harmony patches on Program and Universe](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md) - how-to article demonstrating this feature
- [Copy to the clipboard from ImGui: selectable rows that build text only on click](../../../howto/ui/input/read-typed-text-and-key-chords-from-imgui/read-typed-text-and-key-chords-from-imgui-2.md) - how-to article demonstrating this feature
- [Add a menu with a Harmony postfix on Program.DrawProgramMenusHook](../../../howto/ui/menus/add-items-to-the-game-menu-bar/add-items-to-the-game-menu-bar-1.md) - how-to article demonstrating this feature
- [Match the stock UI: ConsoleWidgets rows inside an ImGuiWindow subclass without console chrome](../../../howto/ui/windows/match-the-stock-console-ui-style/match-the-stock-console-ui-style-1.md) - how-to article demonstrating this feature
- [Stock window base class: guarded DrawContent with title-bar close detection](../../../howto/ui/windows/subclass-the-stock-imguiwindow/subclass-the-stock-imguiwindow-2.md) - how-to article demonstrating this feature

[^mt-patch-menubar]: MeasureTools/Patches/Patch_MenuBar.cs
[^mt-window]: MeasureTools/Features/Measure/MeasureWindow.cs
[^mt-consoleui]: MeasureTools/Core/ConsoleUi.cs
[^mt-state]: MeasureTools/Features/Measure/MeasureState.cs
[^mt-overlay]: MeasureTools/Features/Measure/MeasureOverlay.cs
[^mt-mod]: MeasureTools/Mod.cs
[^mt-readme]: KSA-MeasureTools README.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-imguiwindow]: KSA.ImGuiWindow (decompiled, build 2026.10.10.5554)
[^ksa-consolewidgets]: KSA.ConsoleWidgets (decompiled, build 2026.10.10.5554)
[^ksa-distancereference]: KSA.DistanceReference (decompiled, build 2026.10.10.5554)
[^ksa-radianreference]: KSA.RadianReference (decompiled, build 2026.10.10.5554)
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
