---
type: Game Feature
title: Swapping one call inside a game method with a transpiler
description: How KSA mods make a large game method call their own code instead of one helper without copying the method, in five shapes, from a same-signature operand edit to adapters that translate arguments and results
category: patching
group: transpilers
topic: swap-one-call-inside-a-game-method-with-a-transpiler
demonstrated_by:
  - mods/ksaero/features/computedrag-call-site-transpiler
  - mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection
  - mods/compendium/features/navball-target-marker-and-tgt-frame
  - mods/shaderextensions/features/rendergame-render-pass-transpiler
  - mods/unscience/features/humble-arteest-kitten-visor-toggle
  - mods/unscience/features/godzilla-visual-scaling
  - mods/unscience/features/iron-man-surface-teleport-correction
lifecycle:
  - startup:all-mods-loaded
  - startup:immediate-load
  - startup:starmap-before-main
  - physics:vehicle-solver
  - frame:render
game_types:
  - KSA.PhysicsStates
  - KSA.Vehicle
  - KSA.NavigationTarget
  - KSA.Program
  - KSA.KittenRenderable
  - KSA.TransferPlanner
tags: [harmony, transpiler, call-site, swap, replacement, il]
---

A call-site swap changes what a big game method calls without copying the method: find the one `call` instruction, and make it call your static method instead. It
is selective (other callers of the original are untouched), it survives JIT inlining of the original, and it needs no method boundary. The cost is that the
stack contract must match, so each shape differs in what the replacement receives and returns. The five articles in this topic are the five shapes.

## Engine background

| Target call | In method | Why a swap rather than a patch on the callee |
|---|---|---|
| `PhysicsStates.ComputeDrag` (atmospheric) | `ComputeDerivatives` | Two calls; only the air call changes; callee is `AggressiveInlining` ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1002-L1017)[^ksa-physicsstates]) |
| `Burn.Create` | `TransferPlanner.DrawPlanWindow` | Other create paths exist ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L775-L779)[^ksa-transferplanner]) |
| `NavigationTarget.Create` | `Vehicle.UpdateNavBallMarkers`, `UpdateNavballData` | The extra data is the vehicle ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2797-L2803)[^ksa-vehicle]) |
| `BeginRenderPass<CommandBuffer>` | `Program.RenderGame` | No method boundary at that point ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4947-L4952)[^ksa-program]) |
| `StaticMeshRenderable.Draw`, `IsLargeEnoughToRender`, `get_MeanRadius` | `KittenRenderable.UpdateRenderData`, `Vehicle.UpdateRenderData`, `GetWorldMatrix` | The callee is shared by other callers ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3694-L3726)) |
| `Vehicle.GetInitialKinematicStateForLocation` | `Vehicle.TeleportToLocation` | Bounds exist only at this call ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4219-L4233)) |

Phases from [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md): the swaps are installed at startup and run in `physics:vehicle-solver`, `frame:draw-ui`, `frame:apply-solvers` or `frame:render` depending on the host
method.

## The four rules every swap follows

1. **Equal stack effect.** The replacement consumes exactly what the call consumed and produces what it produced (same parameter types and `in`/`ref` modifiers, same
   return type). If it needs more, insert the extra load yourself ([Wider signature with an extra ldarg.0 appended](swap-one-call-inside-a-game-method-with-a-transpiler-2.md)).
2. **Carry the call site's labels and blocks.** Edit the instruction in place, or move `labels` and `blocks` to the first instruction you emit.
3. **Match precisely and count.** Qualify the call by what it consumes (receiver, argument loads); throw unless exactly one matched.
4. **Fall through to the original** when the mod has nothing to add, so everything else behaves as before.

## Approaches

| # | Article | Mod | Shape | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Same-signature static replacement, operand-only edit](swap-one-call-inside-a-game-method-with-a-transpiler-1.md) | KSAero, KSA-AdvancedFlightComputer | Same-signature static replacement, operand-only edit | `physics:vehicle-solver`, `frame:draw-ui` | Simplest; needs identical signature |
| 2 | [Wider signature with an extra ldarg.0 appended](swap-one-call-inside-a-game-method-with-a-transpiler-2.md) | Compendium | Wider signature: extra `ldarg.0` appended | `frame:apply-solvers` | Replacement gets context; labels must move |
| 3 | [Replacement that does mod work, then calls the original](swap-one-call-inside-a-game-method-with-a-transpiler-3.md) | ShaderExtensions | Replacement does mod work, then calls the original | `frame:render` | Enables work between two instructions; one consumer of the anchor |
| 4 | [Gate helper that skips the call for the mod's own condition](swap-one-call-inside-a-game-method-with-a-transpiler-4.md) | unscience | Gate helper that skips the call, and redirects of a cull and a radius read | `frame:render` | Selective; counts guard against silent loss |
| 5 | [Adapter that fixes up arguments and converts the result back](swap-one-call-inside-a-game-method-with-a-transpiler-5.md) | unscience | Adapter that fixes up arguments and converts the result back | `frame:draw-ui`, `frame:input` | Most flexible; bookkeeping of labels and results |

## See also

- [Writing a Harmony transpiler for a KSA method](../write-a-harmony-transpiler/overview.md) for the recipe and vocabulary
- [Identifying the right call in a method that makes several similar calls](../identify-a-call-by-its-argument-il/overview.md) for picking the right call among several
- [Inserting calls into a game method at an IL anchor](../insert-calls-into-a-game-method-at-an-il-anchor/overview.md) for insertions that do not replace a call
- [Changing behaviour of a game method marked AggressiveInlining](../../internals/patch-around-aggressive-inlining/overview.md) and [Refusing a patch and rolling back when the game changes](../../safety/refuse-a-patch-and-roll-back-when-the-game-changes/overview.md)
- A generic-method call swap (`XmlLoader.Load<T>` to `XmlPatcher.Load<T>` via `MakeGenericMethod`) is covered in [Redirect game XML loading](../../../assets/xml-patching/redirect-game-xml-loading/overview.md)
- Related tasks: replace-the-stock-aerodynamic-force-model, insert-a-custom-pass-into-render-game, synthesize-a-navigation-target-for-the-flight-computer
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Replace the stock aerodynamic force model and measure the active one](../../../physics/aero-and-ground/replace-the-stock-aerodynamic-force-model/overview.md) - related topic
- [Insert a custom Vulkan pass into KSA's frame](../../../rendering/vulkan-passes/insert-a-custom-pass-into-render-game/overview.md) - related topic
- [Synthesize a NavigationTarget for an object that has no orbit](../../../flight-and-orbits/autopilot/synthesize-a-navigation-target-for-the-flight-computer/overview.md) - related topic

[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
