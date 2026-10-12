---
type: Game Feature
title: Scaling a vessel or its parts at runtime
description: How Part.Scale, IRescale and the vehicle editor's scale limits work, and four ways to resize a vessel, a part, an avatar or the editor's limits
category: vehicles-and-parts
group: vehicles
topic: scale-a-vessel-or-its-parts-at-runtime
demonstrated_by:
  - mods/unscience/features/godzilla-vessel-scaling
  - mods/unscience/features/godzilla-visual-scaling
  - mods/gatos/features/scale-and-force-render-cheats
  - mods/unscience/features/garrys-torch-per-axis-part-scaling
  - mods/unscience/features/dont-stifle-me-editor-free-scaling
lifecycle:
  - frame:prepare
  - frame:draw-ui
  - frame:render
  - editor:on-frame
  - frame:pre-render
  - frame:input
  - frame:viewports
game_types:
  - KSA.Part
  - KSA.IRescale
  - KSA.Vehicle
  - KSA.PartTree
  - KSA.VehicleEditor
  - KSA.PartTreeRenderData
  - KSA.ScaleFactors
  - KSA.KittenEva
tags: [scale, part-scale, rescale, editor, render, kitten, colliders]
---
KSA's own scale support is narrow: the vehicle editor scales a top-level part uniformly between 0.5x and 2x in
quarter-metre steps, and a save stores the result. A mod that wants a giant vessel, a flattened part or an
unclamped editor has to pick which of several things should change: what is drawn, what is simulated, or both.

## Engine background

- **`Part.Scale`** is a `double3`. Its setter stores the value and calls `ResetCachedPosMatrixValues()`,
  which invalidates that part's own cached matrices and the tree's render transforms, and nothing else
  [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L819-L830)[^ksa-part] [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1241-L1249). Descendant sub-part caches are not reset.
- **`Part.ScaleTotal`** multiplies `Scale` up the `PartParent` chain for a sub-part
  [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L806-L817); the matrices compose `CreateScale(Scale)` per level, so scaling sub-parts as well
  as the full part applies the factor once per nesting level.
- **`Part.RefreshScale()`** is what makes a scale physical: it builds `new ScaleFactors(ScaleTotal)` and pushes
  it into every `IRescale` module (mass, tanks, colliders, nozzles), every connector and every sub-part
  [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1674-L1693) [current/decomp/KSA/IRescale.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IRescale.cs#L1-L6)[^ksa-irescale]. `ScaleFactors(double3)` collapses the three axes to the
  largest one [current/decomp/KSA/ScaleFactors.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScaleFactors.cs#L6-L21)[^ksa-scalefactors], so physics never sees non-uniform scale.
- **The editor** clamps and snaps in two private statics: `ScaleBoundsFor` returns `(0.5, 2.0)` for top-level
  parts and `(1e-6, +inf)` for sub-parts, and `QuantizeScale` snaps to 0.25 m of the part's largest diameter
  [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L3975-L4052)[^ksa-vehicleeditor]. The drag writes `new double3(s, s, s)` and calls
  `RefreshScaleAndReposition()`.
- **Rendering:** `Vehicle.UpdateRenderData` culls a vehicle whose `MeanRadius` is under one pixel
  (`IsLargeEnoughToRender`), then calls `PartTree.UpdateRenderData(in matrixAsmb2Ego, ...)`; `GetWorldMatrix`
  (used by `KittenEva`) does its own cull [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3694-L3726)[^ksa-vehicle] [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1158)[^ksa-parttree].
  `MeanRadius` is `BoundingSphereRadiusBody`, shared with physics bubble sizing and camera distance
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L535).

## Approaches

| Article | Mod | Technique | What changes |
|---|---|---|---|
| [1: whole-vessel scaling, three channels](scale-a-vessel-or-its-parts-at-runtime-1.md) | unscience (Godzilla) | `harmony-transpiler` | render, physics and colliders independently; non-cumulative |
| [2: recursive `Part.Scale` write](scale-a-vessel-or-its-parts-at-runtime-2.md) | gatOS | `harmony-prefix` | the simplest visual-only route, plus the avatar scalar |
| [3: per-axis part scale with baselines](scale-a-vessel-or-its-parts-at-runtime-3.md) | unscience (Garry's Torch) | `harmony-postfix` | full-part non-uniform scale, exact restore, kitten XYZ |
| [4: lift the editor limits](scale-a-vessel-or-its-parts-at-runtime-4.md) | unscience (Don't Stifle Me) | `harmony-prefix` | the editor's clamp, snap and per-axis drag |

Pick by what you want the game to believe: draw-only (articles 1 and 2 with physics off), physical (article 1),
per-axis visuals (article 3), or author-time (article 4).

## See also

- [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) for the settled window physical scaling needs
- [weld-one-vessel-to-another](../../../physics/kinematics/weld-one-vessel-to-another/overview.md), [Customise parachutes: swap canopy materials and lift the diameter limits](../../modules-and-resources/customize-parachute-materials-and-limits/overview.md), `keep-vessels-rendered-at-any-distance`, [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../change-vehicles-without-racing-the-solver/overview.md)
- Mod features: [unscience/godzilla-vessel-scaling](../../../../mods/unscience/features/godzilla-vessel-scaling.md), [unscience/godzilla-visual-scaling](../../../../mods/unscience/features/godzilla-visual-scaling.md), [gatos/scale-and-force-render-cheats](../../../../mods/gatos/features/scale-and-force-render-cheats.md), [unscience/garrys-torch-per-axis-part-scaling](../../../../mods/unscience/features/garrys-torch-per-axis-part-scaling.md), [unscience/dont-stifle-me-editor-free-scaling](../../../../mods/unscience/features/dont-stifle-me-editor-free-scaling.md)
- [Keep a vessel or kitten rendered at any distance](../../../rendering/meshes/keep-vessels-rendered-at-any-distance/overview.md) - related topic

[^ksa-irescale]: current/decomp/KSA/IRescale.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: current/decomp/KSA/VehicleEditor.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: current/decomp/KSA/PartTree.cs (decompiled, build 2026.10.10.5554)
[^ksa-scalefactors]: current/decomp/KSA/ScaleFactors.cs (decompiled, build 2026.10.10.5554)
