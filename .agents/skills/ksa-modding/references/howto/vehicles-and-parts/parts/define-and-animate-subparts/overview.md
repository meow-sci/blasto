---
type: Game Feature
title: Defining articulated subparts and animating or hiding them from code
description: How a subpart is a Part with settable parent-relative transforms, and four mods' ways of posing wheels, mounts and gimbals and hiding subparts from the main-thread frame
category: vehicles-and-parts
group: parts
topic: define-and-animate-subparts
demonstrated_by:
  - mods/ksacars/features/pose-subparts-at-runtime
  - mods/ksacars/features/car-part-assets-glb-pbr
  - mods/ksarmory/features/articulated-mount-subparts
  - mods/ksarmory/features/optical-director-heads
  - mods/ksarmory/features/rounds-as-hidden-launcher-subparts
lifecycle:
  - startup:asset-load
  - frame:draw-ui
  - frame:after-on-frame
  - frame:celestials
  - frame:viewports
  - frame:render
game_types: [KSA.Part, KSA.SubPartTemplate, KSA.PartTree, KSA.PartTreeRenderData, KSA.PartInstance, KSA.Vehicle]
tags: [subpart, animation, transform, gimbal, hide, projectile, render]
---
KSA has no animation hook a mod can drive, and no module a mod can register. What it has is a tree of `Part`
objects whose subparts carry public, settable parent-relative transforms that the renderer re-reads whenever
one changed. Every moving body (a wheel, a turret, a gimbal, a round in flight) is therefore declared as its
own `<SubPart>` in the part XML and posed from C# every frame.

## Engine background

- **Subparts are Parts.** A `<SubPart Id InstanceOf>` under a `<Part>` is deserialised into
  `PartTemplate.SubPartInstances` (`[XmlElement("SubPart")]`), and the `Part` constructor builds one child `Part`
  per instance, applying its `<Transform>` [current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L19-L20)[^ksa-parttemplate] [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1517-L1525)[^ksa-part].
  `Part.SubParts` lists them [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1127).
- **The transform.** `PositionParentAsmb`, `Asmb2ParentAsmb` (rotation) and `Scale` are public properties; each
  setter calls `ResetCachedPosMatrixValues()` [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L756-L781) [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L819-L830). That method clears
  the part's cached matrices and calls `Tree?.RenderData.InvalidateTransforms()`
  [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1241-L1249), which sets `_transformsDirty` [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L318-L321)[^ksa-parttreerenderdata]; the next
  render-data build repacks every batch slot from the matrices (`RewriteTransforms`)
  [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L755). The explicit reset after a write is redundant on this build for the three
  setters, but it is needed for **descendants**: a sub-part's cached matrices derive from its parent's, and the
  parent's setter does not invalidate theirs.
- **The matrix.** A subpart's transform to its parent is `Scale * rotation * translation`, composed up the
  `PartParent` chain [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1231-L1239). Rotation is about the subpart's own origin, so meshes are
  exported recentred on their pivot.
- **`Id` at runtime.** `Part.Id` is the instance Id from the `<Part>`, falling back to the template Id only if
  the instance has none (`ResolveRuntimeId`) [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1485-L1488). Look bodies up by instance Id.
- **Hide.** There is no draw switch for a subpart; scaling to 0.001 (or parking it in the hull) hides it.
  `Vehicle.UpdateRenderData` builds only when the craft is large enough on screen
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3713-L3726)[^ksa-vehicle].

## Approaches

| Article | Mod | Technique | Subject |
|---|---|---|---|
| [1: wheels, arms and blades from `StarMapAfterGui`](define-and-animate-subparts-1.md) | KSACars | `starmap-lifecycle` | author the subparts, pose them each frame, dual-hook with a frame latch |
| [2: articulated turret mounts](define-and-animate-subparts-2.md) | KSArmory | `subpart-transform-write` | rate-limited drives, sibling chains composed in code, per-channel refusal latch |
| [3: roll-nod optical gimbals](define-and-animate-subparts-3.md) | KSArmory | `gimbal-geometry` | pointing drive without a look-up singularity, two bodies on one pivot |
| [4: scale-to-hide and parent-frame placement](define-and-animate-subparts-4.md) | KSArmory | `scale-to-hide` | hidden launcher subparts flown as projectiles |

## See also

- [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for where `StarMapAfterGui` and the fallbacks sit in the frame
- [Define a part with its own game data and GLB model](../define-a-part-with-game-data/overview.md), [Change a shipped part without breaking saves](../evolve-a-shipped-part-without-breaking-saves/overview.md), [hide-parts-or-subparts-from-rendering](../../../rendering/meshes/hide-parts-or-subparts-from-rendering/overview.md), [ray-cast-against-vessel-meshes-and-terrain](../../../physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md), [choose-a-render-frame-and-anchor-effects](../../../rendering/overlays/choose-a-render-frame-and-anchor-effects/overview.md)
- Mod features: [ksacars/pose-subparts-at-runtime](../../../../mods/ksacars/features/pose-subparts-at-runtime.md), [ksarmory/articulated-mount-subparts](../../../../mods/ksarmory/features/articulated-mount-subparts.md), [ksarmory/optical-director-heads](../../../../mods/ksarmory/features/optical-director-heads.md), [ksarmory/rounds-as-hidden-launcher-subparts](../../../../mods/ksarmory/features/rounds-as-hidden-launcher-subparts.md)

[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-parttreerenderdata]: current/decomp/KSA/PartTreeRenderData.cs (decompiled, build 2026.10.10.5554)
[^ksa-parttemplate]: current/decomp/KSA/PartTemplate.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
