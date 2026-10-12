---
type: Game Feature
title: Customising parachutes, canopy materials and diameter limits
description: How a KSA parachute stores tuning, draws its canopy and clamps its diameter, and two patches that swap the canopy material and lift the editor's diameter slider bounds
category: vehicles-and-parts
group: modules-and-resources
topic: customize-parachute-materials-and-limits
demonstrated_by:
  - mods/unscience/features/free-fallin-canopy-material-substitution
  - mods/unscience/features/dont-stifle-me-parachute-diameter-unclamp
lifecycle:
  - startup:all-mods-loaded
  - frame:draw-ui
  - event:load
  - render:prepass
  - editor:draw-ui
  - event:save
  - event:unload
game_types:
  - KSA.ChuteRenderable
  - KSA.MaterialData
  - KSA.Parachute
  - KSA.ChuteTuning
  - KSA.VehicleEditor
  - KSA.PartMenu
  - KSA.AnimatedRenderable
tags: [parachute, canopy, material, gpu, editor, diameter, clamp]
---
A parachute is a `Parachute` module on a part, with a mutable `ChuteTuning` struct per module, a cloth simulation and a
`ChuteRenderable` per live canopy. Two things players want to change are not exposed: the canopy's look beyond the
authored materials, and the diameter range the editor will allow.

## Engine background

- **Tuning.** `Parachute.TuningFromTemplate` fills `Tuning` from the template: `MinDiameterM`/`MaxDiameterM` ordered,
  `DiameterM` clamped into them [current/decomp/KSA/Parachute.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Parachute.cs#L635-L648)[^ksa-parachute]. `ChuteTuning.ClampDiameter` is
  `float.Clamp(d, Min, Max)` [current/decomp/KSA/ChuteTuning.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChuteTuning.cs#L61-L64)[^ksa-chutetuning].
- **Editor.** `VehicleEditor.DrawParachuteSection` draws the "Diameter" slider with `Tuning.MinDiameterM` /
  `MaxDiameterM` as its range, and for each chute and symmetry counterpart calls `SetDiameter`
  [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L1933-L2062)[^ksa-vehicleeditor]. `Parachute.SetDiameter` clamps again for every `Parachute` module on the part
  [current/decomp/KSA/Parachute.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Parachute.cs#L395-L410). Bounds are not saved; on load `ApplySaveData` re-clamps `DiameterM` against the
  template bounds [current/decomp/KSA/Parachute.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Parachute.cs#L1364-L1412).
- **Stock canopy colour.** The editor offers authored `PbrMaterialReference`s through a "Canopy Color" combo and
  `Parachute.SetCanopyMaterial` [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L1982-L1995); anything beyond them is the mod's job.
- **Drawing.** `ChuteRenderable` wraps a private `AnimatedRenderable` built from `ParachuteCanopyGlb` with slot 0 of its
  material array replaced by the parachute's selected material; `Draw(ViewHandle, ...)` updates the cloth pose and calls
  `_renderable.Draw(view)` [current/decomp/KSA/ChuteRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChuteRenderable.cs#L8-L50)[^ksa-chuterenderable]. `AnimatedRenderable.Draw` writes `MaterialIndices[i]` into
  every instance it submits to the main-opaque, prepass and shadow buckets [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L209-L258)[^ksa-animatedrenderable].

## Approaches

| Article | Subject | Technique |
|---|---|---|
| [1: prefix `ChuteRenderable.Draw`, edit `MaterialIndices`](customize-parachute-materials-and-limits-1.md) | a custom GPU material on every canopy | `harmony-prefix` |
| [2: widen the slider bounds and setter clamp](customize-parachute-materials-and-limits-2.md) | diameter limits lifted reversibly | `harmony-prefix` |

## See also

- [Scale a vessel or its parts at runtime and lift the editor scale limits](../../vehicles/scale-a-vessel-or-its-parts-at-runtime/overview.md) (the editor's other limits)
- [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- Mod features: [unscience/free-fallin-canopy-material-substitution](../../../../mods/unscience/features/free-fallin-canopy-material-substitution.md), [unscience/dont-stifle-me-parachute-diameter-unclamp](../../../../mods/unscience/features/dont-stifle-me-parachute-diameter-unclamp.md)

[^ksa-vehicleeditor]: current/decomp/KSA/VehicleEditor.cs (decompiled, build 2026.10.10.5554)
[^ksa-animatedrenderable]: current/decomp/KSA/AnimatedRenderable.cs (decompiled, build 2026.10.10.5554)
[^ksa-parachute]: current/decomp/KSA/Parachute.cs (decompiled, build 2026.10.10.5554)
[^ksa-chuterenderable]: current/decomp/KSA/ChuteRenderable.cs (decompiled, build 2026.10.10.5554)
[^ksa-chutetuning]: current/decomp/KSA/ChuteTuning.cs (decompiled, build 2026.10.10.5554)
