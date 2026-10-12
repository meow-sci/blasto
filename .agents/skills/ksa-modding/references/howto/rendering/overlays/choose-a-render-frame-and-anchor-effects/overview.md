---
type: Game Feature
title: Choose a render frame and anchor effects to bodies, parts and terrain
description: Ecl positions keep the 29.8 km/s orbital carrier while ego positions are camera-relative translations, so picking the right frame and sample instant keeps overlays, effects and decals on the craft, planet or part they belong to
category: rendering
group: overlays
topic: choose-a-render-frame-and-anchor-effects
demonstrated_by:
  - mods/ksagolf/features/frame-epoch-draw-anchor
  - mods/ksarmory/features/gizmo-world-overlays
  - mods/ksarmory/features/rounds-as-hidden-launcher-subparts
  - mods/ksarmory/features/ksa-explosions-from-a-point
  - mods/ksarmory/features/pooled-particle-emitters
  - mods/ksagolf/features/mod-mesh-draw-hook
  - mods/unscience/features/graffiti-decal-anchoring
lifecycle: [frame:draw-ui, frame:apply-solvers, frame:render, frame:celestials, frame:pre-render]
game_types:
  - KSA.Camera
  - KSA.Vehicle
  - KSA.GizmosRenderer
  - KSA.PartModel
  - KSA.Celestial
  - KSA.ExplosionSystem
  - KSA.ParticleEmitter
  - KSA.StaticMeshRenderable
  - KSA.Part
  - KSA.PartTree
tags: [ecl, ego, ccf, anchor, reference-frames, epoch, overlays, particles]
---

Every KSA position near Earth includes about 29.8 km/s of ecliptic (`Ecl`) motion, about 500 m per frame at 60 fps. `Ego` positions are camera-relative translations of `Ecl`. An overlay, effect or decal that mixes samples from different instants, or anchors in the wrong frame, lags,
jitters or drifts away from the craft, planet or part it should hug. The approaches differ in what the anchor is: the craft's rendered position, a body-fixed frame, a part-local frame.

## Engine background

- Frames: `Camera.EclToEgo` / `EgoToEcl` and `GetPositionEgo(IPosition)` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L223-L250); `GetPositionEgo` is how vehicle meshes are placed (followed object: `-PositionCce`; same physics bubble: physics positions; otherwise `Ecl - camera.PositionEcl`).
- `Vehicle.GetMatrixAsmb2Ego(camera)` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270); body-fixed frame `Celestial.GetCce2Ccf()` / `GetCcf2Cce()` [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L532-L538).
- Gizmos: `GizmosRenderer.DrawSphere` / `DrawLine` take Ego and are cleared by `ResetInstances` at the top of each frame [current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs#L471), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2260).
- Effects: `ExplosionSystem.SpawnPreset` with a `BubbleOrigin` [current/decomp/KSA/ExplosionSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ExplosionSystem.cs#L217); pooled `ParticleEmitter.Origin` [current/decomp/KSA.Rendering.Particles/ParticleEmitter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ParticleEmitter.cs#L113); `StaticMeshRenderable.Draw` after `ClearBuckets` [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L546).
- Timing: positions are applied in `PrepareFrame` (`frame:apply-solvers`), cameras posed in `frame:viewports`, overlays drawn in `frame:draw-ui`, particles spawned in `frame:pre-render` and recorded in `frame:render`. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: two-instant draw anchor](choose-a-render-frame-and-anchor-effects-1.md) | ksagolf | `Ego` from `GetPositionEgo` paired with same-instant `Ecl` | `frame:draw-ui` | Exact overlays beside a craft; every caller must follow the sampling rules |
| [2: anchor to the engine's render position, add travel](choose-a-render-frame-and-anchor-effects-2.md) | ksarmory | Ego anchor for gizmos; subpart placement from platform-relative travel | `frame:draw-ui`, `frame:render` | Matches how the craft is drawn; fixed body count, culling with the launcher |
| [3: explosion presets with a body-frame origin](choose-a-render-frame-and-anchor-effects-3.md) | ksarmory | `BubbleOrigin` in `Ccf` | `frame:draw-ui` | Stock effects anywhere; main thread only |
| [4: pooled emitters re-anchored per frame](choose-a-render-frame-and-anchor-effects-4.md) | ksarmory | Origin rewritten every frame, `Kill` before return | `frame:draw-ui`, `frame:pre-render` | Follows mod-simulated objects; shared pool, one-frame dt glitch |
| [5: body-fixed mesh at draw time](choose-a-render-frame-and-anchor-effects-5.md) | ksagolf | `ClearBuckets` postfix, `Ccf2Cce` rotation | `frame:render` | Lit and shadowed mesh on a rotating planet; per-viewport ego |
| [6: part, cloth or geodetic anchors in double](choose-a-render-frame-and-anchor-effects-6.md) | unscience | Stable ids re-resolved, double composition | `frame:draw-ui` | Survives staging, docking and floating origin; recomposed every frame |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Draw world-space lines and gizmos with the game's own renderers](../draw-world-space-lines-and-gizmos/overview.md), [Spawn particle effects and explosions from a mod](../../lights-and-particles/spawn-particle-effects/overview.md), [Draw your own meshes through the engine's mesh renderers](../../meshes/draw-mod-meshes-with-the-engine-renderers/overview.md), [Project decals onto vessels, terrain and clutter](../../textures-and-decals/project-decals-onto-scene-geometry/overview.md), [Draw a textured quad attached to a part or the camera](../../textures-and-decals/draw-a-textured-quad-in-the-world/overview.md), [Pick the vessel, part, world quad or terrain point under the cursor](../../../ui/input/pick-objects-under-the-cursor/overview.md), [Ray cast against part meshes and terrain, from the cursor or any origin](../../../physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md)
- [Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)
