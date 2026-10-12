---
type: Game Feature
title: Skipping Harmony prefixes
description: When and how to return false from a Harmony prefix in KSA to replace, veto, rewrite or consume a call, what a skipped original still owes the game, and when a postfix is safer
category: patching
group: harmony
topic: skip-the-original-method-with-a-prefix
demonstrated_by:
  - mods/gatos/features/scale-and-force-render-cheats
  - mods/ohmystars/features/star-instance-restyle-patch
  - mods/unscience/features/glass-fov-override
  - mods/ksa-measuretools/features/click-capture-mouse-prefix
  - mods/purrtty/features/gate-game-input-while-terminal-focused
lifecycle: [frame:render, startup:starmap-before-main, frame:viewports, frame:input, frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.InstancedStarTechnique
  - KSA.Camera
  - KSA.GameSettings
  - KSA.Program
  - KSA.Input
  - KSA.BurnContextMenu
  - KSA.MapController
  - KSA.VehicleEditor
tags: [harmony, prefix, skip-original, replace-method, input, veto, patching]
---

A Harmony prefix runs before the original method. If it returns `false` the original does not run.
That one bit lets a mod replace a stock method for some callers, veto an action, rewrite an argument
and avoid an exception, or take keyboard and mouse input away from the game while a mod tool owns it.
The cost is that everything the original would have done is now the prefix's job, including work in
its `finally` block, and that anything else patching the same method may be skipped too.

This topic collects five KSA mods that skip an original for different reasons. All five apply to
build 2026.10.10.5554 and all target public or private instance and static methods on `Vehicle`,
`Camera`, `InstancedStarTechnique`, `Program` and `GameSettings`.

## Engine background

| Type | Member | Why a prefix is used there |
|---|---|---|
| `Vehicle` | `GetWorldMatrix(Camera)`, `IsLargeEnoughToRender`, `UpdateRenderData(IViewport, int)` | Sub-pixel cull decided inside the method ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3694-L3726)[^ksa-vehicle]) |
| `InstancedStarTechnique` | `AddInstance(IViewport, float3, byte, byte4, float)` | Per-star size and colour; throws via the base class when full ([current/decomp/KSA/InstancedStarTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedStarTechnique.cs#L68-L84)[^ksa-instancedstartechnique], [current/decomp/KSA/InstancedRenderTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedRenderTechnique.cs#L86-L96)[^ksa-instancedrendertechnique]) |
| `Camera` | `SetFieldOfView`, `ChangeFieldOfView`, `UpdateProjection`, private `_fovRadians` | One private field read by every projection rebuild ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L417-L485)[^ksa-camera]) |
| `Program` | `OnMouseButton`, `OnKey`, private `DispatchKeyEvent`, private static `_heldMouseButtons` and `_inputViewport` | GLFW callbacks in `frame:input`; latches reset in a `finally` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1830)[^ksa-program], [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2003-L2097)) |
| `GameSettings` | `OnKeyAll(GlfwKeyEvent)` | First consumer inside `Program.OnKey` ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L3422-L3435)[^ksa-gamesettings]) |
| `BurnContextMenu` | private static `_rightClickValid`, `OnRightClick`, `TryOpen` | State armed on press and cleared on release ([current/decomp/KSA/BurnContextMenu.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnContextMenu.cs#L36-L76)[^ksa-burncontextmenu]) |

The phases involved, from [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md):

- `frame:input`: `Glfw.PollEvents()` runs inside `Program.PrepareFrame`, between the `Apply*Solvers`
  and `ExecuteNext*Solvers` calls, and fires `Program.OnKey` and `Program.OnMouseButton`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2163-L2181)).
- `frame:viewports` and `frame:draw-ui`: where camera projections are rebuilt and where a mod's UI writes
  the state its prefixes read.
- `frame:render`: `Program.Render` calls `UpdateRenderingResources`, which calls
  `Vehicle.UpdateRenderData`; EVA kittens are updated inside `RenderViewport` and `RenderGame`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4337-L4358), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4483-L4495)).
- `startup:starmap-before-main`: the only point before `Program` builds its star technique.

## What a skipping prefix actually does

Harmony 2.4.2 is the version StarMap references ([StarMap.Core/StarMap.Core.csproj](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMap.Core.csproj#L11)[^starmap-starmap-core]). A throwaway
console test against that version, with three patches on one method, showed:

```text
// observed output of a local test against Lib.Harmony 2.4.2 (not part of any mod repository)
Run(1):
  prefixA (returns false)          <- Priority.First prefix, returns false
  postfix result=100               <- postfix still ran
  finalizer after skipped original; exception=null
=> 100
```

A second `bool` prefix at `Priority.Last` did not run once the first one returned `false` (a `void` prefix at `Priority.Last` did, with or without a `bool __runOriginal` parameter, which then read `false`), the postfix and the
finalizer did, and the value the skipping prefix wrote to `__result` was what the caller got. A void
finalizer does not swallow an exception from the original; a finalizer that returns `null` does, and
then no postfix runs for that call. In short:

| Question | Answer in 2.4.2 |
|---|---|
| Does the original run after `return false`? | No |
| Do later prefixes run? | Prefixes that return `bool`: no. `void` prefixes: yes |
| Do postfixes run? | Yes |
| Does the original's `finally` run? | No, the original never started |
| Who supplies the return value? | The prefix, through `ref T __result` |

Consequences used by the five approaches:

1. **Reproduce the original faithfully** (approach 1). Copy the stock body minus the part you want to
   change, keep the copy short, and fall through (`return true`) on any fault.
2. **Pay the skipped `finally`** (approach 4). If the original latched state before its `try`, a skipped
   original skips the unlatching; the prefix must undo it, and must decline to skip when it cannot.
3. **Pair what you consume** (approaches 4 and 5). If you swallow a press, swallow its release; if you
   forward a press, forward its release.
4. **Edit rather than replace** (approaches 2 and 3). A `ref` parameter or a pre-write of a private field
   changes what the original sees and needs no `return false` except for the one case that must not
   run.

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Reproduce the original body for marked objects and return false](skip-the-original-method-with-a-prefix-1.md) | gatOS | Prefix reproduces the stock body for marked vessels and returns false | `frame:render` | Maximum control; the copied body goes stale when the game changes |
| 2 | [Rewrite by-ref arguments and skip when a limit would throw](skip-the-original-method-with-a-prefix-2.md) | OhMyStars | `ref` arguments rewritten, `return false` when a limit would throw, positional `ref int __4` on a constructor | `startup:starmap-before-main`, `frame:draw-ui` | Original still runs; any other caller of the method is affected too |
| 3 | [Re-inject a private field before a rebuild so nothing can change it back](skip-the-original-method-with-a-prefix-3.md) | unscience | Skip the FOV keys, inject a private field before every projection rebuild | `frame:input`, `frame:draw-ui` | One choke point covers all callers; string-named private field |
| 4 | [Mouse prefix that pairs presses with releases and repairs private latches](skip-the-original-method-with-a-prefix-4.md) | KSA-MeasureTools | Mouse prefix pairs presses with releases and repairs private latches | `frame:input` | Must mirror stock gate order and repair state by reflection |
| 5 | [Keyboard and mouse-binding gates with a held-key model](skip-the-original-method-with-a-prefix-5.md) | purrTTY | Key and mouse-binding gates with a held-key model | `frame:input` | Needs a flag that is cleared on every path; swallowed input is hidden from others |

## Postfix or prefix

Prefer a postfix that edits `__result` when the original is cheap and has no side effect you need to
avoid: it composes with every other mod's patches because nothing is skipped, and it needs no copy of
the stock body. Approach 1's `GetWorldMatrix` could be done that way because stock returns `null`
exactly when the cull fires (a sketch is in that article). Prefer a skipping prefix when the original can
throw on the data you are about to give it (approach 2), when it has side effects you must not run
(approaches 4 and 5), or when its cost is what you are avoiding.

## See also

- Other topics: patch-a-private-game-method-by-name ([Patching private and overloaded game methods by name](../../internals/patch-a-private-game-method-by-name/overview.md)), run-cleanup-or-recover-from-exceptions-with-a-finalizer ([Harmony finalizers, cleanup and scoped overrides](../run-cleanup-or-recover-from-exceptions-with-a-finalizer/overview.md)) and coexist-with-other-mods-patching-the-same-method ([Coexisting with other mods that patch the same method](../coexist-with-other-mods-patching-the-same-method/overview.md))
- Tasks built on this technique: override-the-camera-field-of-view, keep-vessels-rendered-at-any-distance, grow-game-gpu-buffers-for-runtime-content, block-game-hotkeys-while-typing, coordinate-imgui-focus-with-game-input, rebindable-hotkeys
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Override the camera field of view and zoom past the stock clamp](../../../camera/main-view/override-the-camera-field-of-view/overview.md) - related topic
- [Keep a vessel or kitten rendered at any distance](../../../rendering/meshes/keep-vessels-rendered-at-any-distance/overview.md) - related topic
- [Reserve or grow game GPU buffers for content added at runtime](../../../rendering/meshes/grow-game-gpu-buffers-for-runtime-content/overview.md) - related topic
- [Block game hotkeys while typing into an ImGui field](../../../ui/input/block-game-hotkeys-while-typing/overview.md) - related topic
- [Share the keyboard and mouse between ImGui, mod tools and the game](../../../ui/input/coordinate-imgui-focus-with-game-input/overview.md) - related topic
- [Implement rebindable hotkeys with ImGui key state](../../../ui/input/rebindable-hotkeys/overview.md) - related topic

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-instancedstartechnique]: KSA.InstancedStarTechnique (decompiled, build 2026.10.10.5554)
[^ksa-instancedrendertechnique]: KSA.InstancedRenderTechnique (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-burncontextmenu]: KSA.BurnContextMenu (decompiled, build 2026.10.10.5554)
[^starmap-starmap-core]: StarMap/StarMap.Core/StarMap.Core.csproj
