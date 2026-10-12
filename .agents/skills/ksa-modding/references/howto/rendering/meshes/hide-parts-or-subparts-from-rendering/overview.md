---
type: Game Feature
title: Hide part meshes or subparts without removing them
description: Remove a window pane, a model or a missile from the picture while the part stays in the vessel, by filtering instance lists, scaling a subpart to nothing or moving it, and also show IVA interiors from outside and draw kitten equipment in the right order
category: rendering
group: meshes
topic: hide-parts-or-subparts-from-rendering
demonstrated_by:
  - mods/unscience/features/lib-part-render-filter
  - mods/unscience/features/kitchen-sink-capsule-glass
  - mods/ksarmory/features/rounds-as-hidden-launcher-subparts
  - mods/ksacars/features/pose-subparts-at-runtime
  - mods/unscience/features/lib-iva-force-render
  - mods/gatos/features/scale-and-force-render-cheats
  - mods/unscience/features/iron-man-equipment-render-ordering
lifecycle: [frame:render, frame:draw-ui]
game_types:
  - KSA.PartTreeRenderData
  - KSA.Part
  - KSA.PartModelModule
  - KSA.PartModelRenderer
  - KSA.Vehicle
  - KSA.KittenEva
tags: [parts, subparts, hide, scale, iva, internal, render-data]
---
Parts are simulated and drawn separately. A part can keep its mass, thrust, resources and colliders while its mesh disappears, and a subpart can be moved or shrunk without touching its parent. KSA draws part meshes through per-model instance lists, so every technique here either edits those lists, edits what feeds them (a subpart transform, a shared template flag), or changes the order in which they are filled.

## Engine background

- **Where lists are filled.** `PartTree.UpdateRenderData` [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1158-L1171) calls `PartTreeRenderData.Compose`, `ComposeDynamic` and `ComposeGlass` [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1258-L1323), which append one range per model to each model's per-viewport `InstanceList`. `PartModelRenderer.UpdateRenderData` [current/decomp/KSA/PartModelRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelRenderer.cs#L807-L827) uploads them; `ClearFrameData` empties them in `PostRender`. All of that runs inside `Program.UpdateRenderingResources` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4337-L4377)) for every visible viewport, before `RenderGame` records commands.
- **What gates an instance.** In the raster branch an instance is appended only when the model is not a shadow proxy and `!Template.Internal || viewport.Mode == CameraMode.IVA`.
- **Subparts are `Part`s.** `Part.PositionParentAsmb`, `Asmb2ParentAsmb` and `Scale` [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L756-L830) are settable; each setter resets cached matrices and invalidates the tree's render transforms. There is no visibility flag on a part.
- **Cull and ordering.** `Vehicle.UpdateRenderData` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3707-L3726) draws a vessel's parts only if it projects to at least one pixel. `KittenEva` vehicles are skipped by the early loop and composed later inside `RenderViewport` and `RenderGame`, after the upload; see RenderGame row 1a in [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: compact instance lists around Compose](hide-parts-or-subparts-from-rendering-1.md) | unscience | `harmony-prefix` and `harmony-postfix` on the three compose methods, owner predicates | `frame:render` | Hides any part or model, flips instantly, no transform change; reflection into private batch lists, ray-traced IVA not filtered |
| [2: scale a pre-declared subpart to nothing](hide-parts-or-subparts-from-rendering-2.md) | KSArmory | `scale-to-hide` plus per-frame transform writes | `startup:asset-load`, `frame:draw-ui`, `frame:celestials` | No patches; fixed number of bodies, hidden parts still exist in the save |
| [3: pose or hide subparts from StarMapAfterGui](hide-parts-or-subparts-from-rendering-3.md) | KSACars | Transform writes with an `AfterOnFrame` fallback and frame latch | `frame:draw-ui`, `frame:after-on-frame` | Simple and robust to a hidden HUD; fallback poses a frame late |
| [4: owner-counted Template.Internal requirement](hide-parts-or-subparts-from-rendering-4.md) | unscience | `harmony-postfix` ctor, `harmony-prefix` and `harmony-finalizer` on Compose | `frame:render` (flight and editor scenes) | Cabins from outside and in the editor, many owners; mutates shared templates |
| [5: dedicated Harmony instance, lazily installed](hide-parts-or-subparts-from-rendering-5.md) | gatOS | Same flip, patch only while on | `frame:render` | Zero cost when off; one owner, no editor reveal or proxy exclusion |
| [6: early kitten equipment submission](hide-parts-or-subparts-from-rendering-6.md) | unscience | Prefix on `PartModelRenderer.UpdateRenderData`, nonvirtual `Vehicle.UpdateRenderData` call | `frame:render` | Equipment drawn once per view alongside other mods' patches; needs IL and careful priority |

Pick by what you control: you own the part XML and want one switch per body (2 or 3); you want to hide stock parts or models you cannot edit (1); you want to see interiors (4 or 5); your parts are not drawn at all (6).

## See also

- [Draw your own meshes through the engine's mesh renderers](../draw-mod-meshes-with-the-engine-renderers/overview.md)
- [Keep a vessel or kitten rendered at any distance](../keep-vessels-rendered-at-any-distance/overview.md)
- Topics on defining and animating subparts (assets category) and drawing a fullscreen click-through overlay (ui category) are not yet written
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Defining articulated subparts and animating or hiding them from code](../../../vehicles-and-parts/parts/define-and-animate-subparts/overview.md) - related topic
- [Draw full-screen click-through overlays and choose the right ImGui draw list](../../../ui/overlays/draw-a-fullscreen-click-through-overlay/overview.md) - related topic

