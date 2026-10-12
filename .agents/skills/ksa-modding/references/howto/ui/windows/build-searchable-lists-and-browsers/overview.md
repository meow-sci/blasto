---
type: Game Feature
title: Build searchable lists, two-pane browsers and file pickers in ImGui
description: How Brutal's ImInputString behaves in filter boxes, and six ways mods build searchable combos, ranked search lists, two-pane browsers and file pickers
category: ui
group: windows
topic: build-searchable-lists-and-browsers
demonstrated_by:
  - mods/purrtty/features/imgui-filter-combo-and-form-widgets
  - mods/compendium/features/celestial-info-window
  - mods/ksa-deltavmap/features/search-focus-isolate-and-aggregation
  - mods/unscience/features/lib-xkcd-color-catalog
  - mods/unscience/features/lib-shared-file-library
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImInputString
  - Brutal.ImGuiApi.ImGui
  - KSA.Universe
  - KSA.Camera
  - KSA.KSAColor
  - Brutal.ImGuiApi.ImGuiChildFlags
  - KSA.Program
  - KSA.ConsoleStyle
  - Brutal.ImGuiApi.ImGuiSelectableFlags
tags: [search, filter, combo, browser, splitter, file-picker, iminputstring]
---
Long lists are the norm in a modded game: parts, vehicles, hundreds of colours, thousands of asteroids, every file on disk. KSA provides no list widgets for mods; every mod builds them from `Brutal.ImGuiApi` primitives, and the same few details decide whether they work: how a text filter reads its buffer, when the filter box takes focus, how a long result list is capped, and when a pick may mutate the model being drawn.

## Engine background

| Piece | File | Role |
|---|---|---|
| `ImInputString` | [current/decomp/Brutal.ImGuiApi/ImInputString.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImInputString.cs#L8-L92) | Fixed-capacity UTF-8 text buffer; `Length` is cached, `EvaluateLength()` refreshes it |
| `ImGui.InputText(label, ImInputString, ...)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8487-L8501) | Writes into the buffer and refreshes the length only when the native call returned true |
| `ImGui.InputTextWithHint(...)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8522-L8538) | Same, with a placeholder hint |
| `ImGui.SetKeyboardFocusHere`, `SetItemDefaultFocus` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7923-L7930) | Focus a filter box, scroll a combo to the current item |
| `Universe.MoveCameraTo` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2527-L2532) | Follow a body from a "go to" button (releases vehicle control for non-vehicles) |

**The quirk.** The binding refreshes a buffer's cached length only when the native `InputText` returned true. For default flags that is every edit, so a plain `ToString()` after the call works; with `ImGuiInputTextFlags.EnterReturnsTrue` it is true only on Enter, so the cache is stale while typing. purrTTY sidesteps the question by calling `EvaluateLength()` every frame. **Frame order.** All of it is `frame:draw-ui`. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Live filter inside a combo, EvaluateLength every frame](build-searchable-lists-and-browsers-1.md) | purrTTY | `BeginCombo` plus filter, unique ids | `frame:draw-ui` | Robust against the length cache; reusable helper |
| 2 | [SetKeyboardFocusHere and SetItemDefaultFocus](build-searchable-lists-and-browsers-2.md) | Compendium | Searchable star-system combo | `frame:draw-ui` | Smallest; relies on default-flag edit semantics |
| 3 | [InputTextWithHint with ranked results and reveal-on-pick](build-searchable-lists-and-browsers-3.md) | KSA-DeltaVMap | Edit-triggered search, ranked, capped, bordered list | `frame:draw-ui` | Scales to thousands of bodies; act after the child closes |
| 4 | [Filterable colour catalogue list](build-searchable-lists-and-browsers-4.md) | unscience (Zippo) | Cached array, clearing filter | `frame:draw-ui` | Easy picker; list rebuilt silently empty if the game changes |
| 5 | [Splitter, universe guard and go-to button](build-searchable-lists-and-browsers-5.md) | Compendium | Button splitter, `WorldSun` guard, `MoveCameraTo` | `startup:all-mods-loaded`, `frame:draw-ui` | Full browser; many pieces |
| 6 | [File browser copying into a flat catalogue](build-searchable-lists-and-browsers-6.md) | unscience | `System.IO` listing, cached; copy-on-import | `frame:draw-ui` | Robust references; large copies block one frame |

## Choosing

- A short searchable choice: approach 1 or 2. A choice among hundreds with swatches: approach 4.
- Search over a huge, structured set with an action on pick: approach 3.
- A browser with a resizable detail pane: approach 5. Files from disk: approach 6.

## See also

- [Build a mod settings window](../build-a-settings-window/overview.md)
- [Build a tabbed ImGui window](../build-a-tabbed-imgui-window/overview.md)
- [Lay out a tree or graph diagram and keep it usable at scale](../../draw-lists/lay-out-a-tree-or-graph-diagram/overview.md)
- [Persist mod settings as TOML](../../../persistence/settings-formats/persist-mod-settings-toml/overview.md)
- [Build a mod window on the game's ImGuiWindow base class, with stable ids and saved placement](../subclass-the-stock-imguiwindow/overview.md) - related topic

