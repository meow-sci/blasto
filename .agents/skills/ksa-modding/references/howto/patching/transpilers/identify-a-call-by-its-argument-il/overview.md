---
type: Game Feature
title: Identifying the right call in a method that makes several similar calls
description: "How KSA transpilers pick one instruction among many similar ones by what surrounds it: the loads that feed its arguments, a unique neighbouring call, a UI label literal, or the instruction that consumes its result"
category: patching
group: transpilers
topic: identify-a-call-by-its-argument-il
demonstrated_by:
  - mods/ksaero/features/computedrag-call-site-transpiler
  - mods/shaderextensions/features/rendergame-render-pass-transpiler
  - mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection
  - mods/ksa-advancedflightcomputer/features/orbit-context-menu-shortcuts
  - mods/compendium/features/navball-target-marker-and-tgt-frame
lifecycle:
  - startup:all-mods-loaded
  - startup:starmap-before-main
  - startup:immediate-load
  - frame:draw-ui
  - frame:apply-solvers
game_types:
  - KSA.PhysicsStates
  - KSA.PhysicsEnvironment
  - KSA.Program
  - KSA.TransferPlanner
  - KSA.BurnContextMenu
  - KSA.Vehicle
  - KSA.GaugeCanvas
  - KSA.GaugeButtonNavBallMode
  - KSA.NavigationTarget
tags: [harmony, transpiler, il, anchor, discriminate, patching]
---

A big game method often calls the same helper more than once: ocean versus air drag, one `Burn.Create` among several, one `BeginMenu` among four, one `get_Target` that feeds a null
test among others that pass the value on. `CodeInstruction.Calls(method)` matches all of them. A transpiler has to pick the right instruction by what surrounds it. The four approaches here
use four different surroundings, each with a bounded search and a count check.

## Engine background

| Method | The similar calls | What distinguishes the target |
|---|---|---|
| `PhysicsStates.ComputeDerivatives` | Two `ComputeDrag` calls | The density field loaded as an argument ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1002-L1017)[^ksa-physicsstates]) |
| `Program.RenderGame` | `BeginRenderPass`, `EndRenderPass`, `RenderDrawData` | A unique neighbouring call ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4947-L4952)[^ksa-program]) |
| `TransferPlanner.DrawPlanWindow` | `PrimaryButton`, `Burn.Create` | Distance from a unique call ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L745-L779)[^ksa-transferplanner]) |
| `BurnContextMenu.Draw` | Four `BeginMenu` calls, six `EndPopup` calls | The UTF-8 label literal; the last `EndPopup` ([current/decomp/KSA/BurnContextMenu.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnContextMenu.cs#L296-L330)[^ksa-burncontextmenu]) |
| `Vehicle.UpdateNavballData` and three gauge methods | `get_Target` | The instruction that consumes the result ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2760-L2777)[^ksa-vehicle]) |

Phases from [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md): the searches run at patch time; the patched code runs in `physics:vehicle-solver`, `frame:render`, `frame:draw-ui` and `frame:apply-solvers`.

## Principles shared by the four

1. **Describe the instruction, not its index.** Indices shift with every compiler tweak; a field load, a neighbouring call, a literal or a consumer is a property of the code.
2. **Bound the search.** A window of instructions, or a stop at the previous call, keeps a match from drifting into another call's arguments.
3. **Require exactly one.** Count matches and throw (or leave the code unchanged and warn once) on any other number.
4. **Name what failed.** The message says which method, which anchor and how many matches.
5. **Expect other transpilers.** Earlier transpilers' output is your input ([Anchors that one mod can consume (and what the next transpiler then sees)](../../harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-2.md)).

## Approaches

| # | Article | Mod | Surrounding used | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Scan backwards through the argument loads to the previous call](identify-a-call-by-its-argument-il-1.md) | KSAero | The argument loads (backward scan to the previous call) | `startup:all-mods-loaded`, `physics:vehicle-solver` | Precise; window size matters |
| 2 | [Anchor on a unique neighbouring call, then walk to the target](identify-a-call-by-its-argument-il-2.md) | ShaderExtensions, KSA-AdvancedFlightComputer | A unique neighbouring call, then a bounded walk | `startup:starmap-before-main`, `frame:render`, `frame:draw-ui` | Fragile if the neighbour is not really unique |
| 3 | [Anchor on a UTF-8 label literal in the method](identify-a-call-by-its-argument-il-3.md) | KSA-AdvancedFlightComputer | A UTF-8 label literal | `startup:all-mods-loaded`, `frame:draw-ui` | Works when no call is distinctive; depends on compiler output for `u8` |
| 4 | [Match the instruction that consumes the result](identify-a-call-by-its-argument-il-4.md) | Compendium | The instruction that consumes the result | `startup:immediate-load`, `frame:apply-solvers` | Selective; depends on how the compiler emits null tests |

## See also

- [Writing a Harmony transpiler for a KSA method](../write-a-harmony-transpiler/overview.md) for the recipe
- [Swapping one call inside a game method with a transpiler](../swap-one-call-inside-a-game-method-with-a-transpiler/overview.md) for what to do once the call is found
- [Inserting calls into a game method at an IL anchor](../insert-calls-into-a-game-method-at-an-il-anchor/overview.md) for insertion at an anchor
- Related tasks: set-and-clear-the-vehicle-target, synthesize-a-navigation-target-for-the-flight-computer, patch-a-private-game-method-by-name
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Coexisting with other mods that patch the same method](../../harmony/coexist-with-other-mods-patching-the-same-method/overview.md) - related topic
- [Set or clear a vehicle's target, including bodies the game cannot target](../../../flight-and-orbits/autopilot/set-and-clear-the-vehicle-target/overview.md) - related topic
- [Synthesize a NavigationTarget for an object that has no orbit](../../../flight-and-orbits/autopilot/synthesize-a-navigation-target-for-the-flight-computer/overview.md) - related topic
- [Patching private and overloaded game methods by name](../../internals/patch-a-private-game-method-by-name/overview.md) - related topic

[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-burncontextmenu]: KSA.BurnContextMenu (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
