---
type: Game Feature
title: Hide a kitten's body from the main view only, keeping its shadow
description: How a mod hides a kitten body from the main view only by folding its skeleton per viewport and restoring the full pose in the shadow pool, and what the engine HideHead switch does instead
category: kittens
group: appearance
topic: hide-a-kitten-body-from-one-viewport
demonstrated_by:
  - mods/ksastructures/features/first-person-body-hiding-and-shadow
  - mods/ksagolf/features/first-person-kitten-view
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:render, render:prepass, render:shadows]
game_types:
  - KSA.KittenEva
  - KSA.KittenRenderable
  - KSA.AnimatedRenderable
  - KSA.IAnimProcessor
  - KSA.Program
  - RenderCore.Animation.Skeleton
  - KSA.SuperMeshRenderSystem
  - KSA.ShadowRenderable
  - KSA.InstanceData
  - KSA.StaticMeshRenderable
  - KSA.MeshRenderTechnique
  - KSA.IVASeat
tags: [kitten, first-person, shadow, skinning, viewport]
---
A first-person camera sits inside the flown kitten's head, so the kitten has to disappear from that view while staying visible in camera windows and crew portraits, and while still casting its shadow. KSA draws a kitten once per viewport and rebuilds its skeleton for each, which is the seam both mods use: mark the view being drawn, fold the skeleton for that view alone, and put the whole-body pose back into the shadow pool. The engine also has a simpler per-view switch, `KittenRenderable.HideHead`, which an IVA seat sets and which hides only the head meshes and fur.

## Engine background

| Piece | Where | What it does | Source |
|---|---|---|---|
| Per-view draw | `Program.RenderGame`, `RenderViewport`, `KittenEva.UpdateRenderData` | Secondary viewports first, main view last; each clears buckets (and the shadow pool) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4672-L4683), [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L1062-L1078) |
| Per-view skeleton | `AnimatedRenderable.UpdateAnimation`, `IAnimProcessor.UpdateSkeleton` | World transforms rebuilt and processors rerun for every call; skin is `InverseBindPose * WorldTransform` | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L173-L185) |
| Shadow entries | `AnimatedRenderable.Draw`, `SuperMeshRenderSystem.ShadowRenderablePool`, `ShadowRenderable` | One entry per shadow-casting mesh using the view's skin | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L209-L266), [current/decomp/KSA/ShadowRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShadowRenderable.cs#L6-L15) |
| Attachments | `KittenRenderable.UpdateRenderData`, `AnimatedRenderable.GetBoneTransform` | Helmet and visor matrices derive from the bone world transform | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L353-L370) |
| Engine head switch | `KittenRenderable.HideHead`, `IVASeat.UpdateSeatedRenderable` | Masked head meshes and fur skipped in colour and shadow | [current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L103), [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L357-L361) |

See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for where these render calls sit in the frame.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. Fold all bones onto the head with zero-scale matrices, then give the shadow pass the full-body pose](hide-a-kitten-body-from-one-viewport-1.md) | ksastructures (and ksagolf) | `anim-processor` | `startup:all-mods-loaded`, `frame:draw-ui`, `frame:render` | Whole body gone from one view, full shadow kept; four patches, reflection for the bind pose, and a second skinning per frame |
| [2. Fold onto the wrist and keep the club-holding paw; or `HideHead` per view](hide-a-kitten-body-from-one-viewport-2.md) | ksagolf | `anim-processor` | `startup:all-mods-loaded`, `frame:render` | Keeps a viewmodel paw; the `HideHead` variant is two small patches but hides only head and fur |

## See also

- [First-person camera for a kitten](../../../camera/special-views/first-person-camera-for-a-kitten/overview.md), [Steer a walking kitten](../../eva/steer-a-walking-kitten/overview.md)
- [Pose kitten bones with an anim processor](../../animation/pose-kitten-bones-with-an-anim-processor/overview.md), [Reach a kitten's avatar and skeleton](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md)
- [Attach a prop to a kitten bone](../attach-a-prop-to-a-kitten-bone/overview.md), [Tint and restyle kitten appearance](../tint-and-restyle-kitten-appearance/overview.md), [Animate and draw seated kitten crew](../../animation/animate-and-draw-seated-kitten-crew/overview.md)
- [Add invisible shadow casters](../../../rendering/meshes/add-invisible-shadow-casters/overview.md), [Hide parts or subparts from rendering](../../../rendering/meshes/hide-parts-or-subparts-from-rendering/overview.md)
- Mod features: [First-person body hiding and shadow](../../../../mods/ksastructures/features/first-person-body-hiding-and-shadow.md), [First-person kitten view](../../../../mods/ksagolf/features/first-person-kitten-view.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Let the IVA camera leave the seat and fly around the cabin](../../../camera/special-views/unlock-the-iva-camera/overview.md) - related topic
- [Replace a game texture or model texture with a user PNG at runtime](../../../rendering/textures-and-decals/replace-a-game-texture-at-runtime/overview.md) - related topic

