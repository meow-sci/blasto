---
type: Game Feature
title: Inserting calls into a game method at an IL anchor
description: How KSA mods add draw, filter or snapshot calls inside a stock method by inserting instructions before or after an anchor, and how labels and exception-block markers decide whether jumps reach the insertion
category: patching
group: transpilers
topic: insert-calls-into-a-game-method-at-an-il-anchor
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection
  - mods/ksa-advancedflightcomputer/features/mods-settings-page-sections
  - mods/unscience/features/jplrepo-menu-bar-animation
  - mods/shaderextensions/features/rendergame-render-pass-transpiler
lifecycle: [startup:all-mods-loaded, startup:starmap-before-main, frame:draw-ui, frame:render]
game_types: [KSA.TransferPlanner, KSA.GameSettings, KSA.Program, KSA.ConsoleWidgets, KSA.ConsoleStyle]
tags: [harmony, transpiler, il, insert, anchor, labels, exception-blocks]
---

A prefix or postfix wraps a method; an insertion puts your call in the middle of it. Mods use it to add UI inside a stock window or menu (the call must run between `Begin` and `End`), to filter a value
left on the stack, to snapshot state at one precise moment, and to run work between two stock phases. The question that decides whether an insertion is correct is not where the new
instruction goes but **who owns the anchor's labels and exception-block markers**: labels are the jump targets that land on the anchor, and begin-try markers decide whether the new call is inside
the region.

## What decides the behaviour

| Anchor carries | Insert before it, and... | Result |
|---|---|---|
| A label (a branch jumps to it) | leave the label on the anchor | jumps skip the inserted call |
| A label | move the label to the inserted instruction | every path that reached the anchor runs the inserted call |
| A begin-try marker | leave it on the anchor | the inserted call is *outside* the try, so its `finally` does not run if it throws |
| A begin-try marker | move it to the inserted instruction | the inserted call is inside the try |
| An end-try marker | leave it on the anchor | the inserted call stays inside the region |

Local tests against Lib.Harmony 2.4.2 observed each row; see [Move labels and begin-exception markers onto the inserted instruction](insert-calls-into-a-game-method-at-an-il-anchor-1.md) for the output.

## Engine background

| Method | Anchor | Why there |
|---|---|---|
| `TransferPlanner.DrawPlanWindow` | `DrawCorrectionTransfer` call; branch-target `ConsoleStyle.PopWidgetStyle` call | Draw inside the window body ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L616-L626)[^ksa-transferplanner], [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L730-L733)) |
| `GameSettings.OnDrawUi` | branch-target `ConsoleStyle.PopWidgetStyle` | Inside the Mods tab body, skipped on other tabs ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L2247-L2253)[^ksa-gamesettings]) |
| `Program.DrawMenuBar` | first `ImGui.SetCursorPosY` call | Stack-neutral snapshot point ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4005-L4011)[^ksa-program]) |
| `Program.RenderGame` | after the last `EndRenderPass` | Outside the swapchain render pass ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4948-L4953)) |

Phases from [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md): insertions run where the host method runs: `frame:draw-ui` for the three UI methods, `frame:render` for `RenderGame`.

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Move labels and begin-exception markers onto the inserted instruction](insert-calls-into-a-game-method-at-an-il-anchor-1.md) | KSA-AdvancedFlightComputer | Move labels and begin-exception markers onto the inserted instruction; filter a stack value | `startup:all-mods-loaded`, `frame:draw-ui` | Reaches every path; needs a helper |
| 2 | [Leave labels on the anchor so a jump skips the addition](insert-calls-into-a-game-method-at-an-il-anchor-2.md) | KSA-AdvancedFlightComputer | Leave labels on the anchor so a jump skips the addition | `startup:all-mods-loaded`, `frame:draw-ui` | Skips on the jump path by design |
| 3 | [Snapshot state before a stock call, restore it in a paired prefix](insert-calls-into-a-game-method-at-an-il-anchor-3.md) | unscience | Snapshot state before a stock call and restore it in a paired prefix | `frame:draw-ui` | Split between IL and a prefix; snapshot timing |
| 4 | [Insert before the first and after the last instance of a pass boundary](insert-calls-into-a-game-method-at-an-il-anchor-4.md) | ShaderExtensions | Insert after the last instance of a pass boundary with hard-coded locals | `frame:render` | Simple; local indices are the weak point |

## See also

- [Writing a Harmony transpiler for a KSA method](../write-a-harmony-transpiler/overview.md), [Swapping one call inside a game method with a transpiler](../swap-one-call-inside-a-game-method-with-a-transpiler/overview.md) and [Identifying the right call in a method that makes several similar calls](../identify-a-call-by-its-argument-il/overview.md)
- [Coexisting with other mods that patch the same method](../../harmony/coexist-with-other-mods-patching-the-same-method/overview.md) for what other transpilers do to your anchor
- Related tasks: inject-ui-into-a-stock-window, add-plan-type-to-the-transfer-planner, add-items-to-the-game-menu-bar, insert-a-custom-pass-into-render-game
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Inject UI into the game's own windows and menus, including the orbit right-click menu](../../../ui/menus/inject-ui-into-a-stock-window/overview.md) - related topic
- [Add plan types to the stock Transfer Planner](../../../flight-and-orbits/transfers/add-plan-type-to-the-transfer-planner/overview.md) - related topic
- [Add a menu to the game's top menu bar](../../../ui/menus/add-items-to-the-game-menu-bar/overview.md) - related topic
- [Insert a custom Vulkan pass into KSA's frame](../../../rendering/vulkan-passes/insert-a-custom-pass-into-render-game/overview.md) - related topic

[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
