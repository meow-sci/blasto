---
type: Game Feature
title: Reach a kitten's avatar, skeleton, bones and animation catalog
description: How a mod gets from a KittenEva to its CharacterAvatar, Skeleton, named bones, private clip fields and materials, and where in the frame the kitten is animated and drawn
category: kittens
group: characters
topic: reach-a-kittens-avatar-and-skeleton
demonstrated_by:
  - mods/ksagolf/features/kitten-pose-processor
  - mods/ksastructures/features/first-person-body-hiding-and-shadow
  - mods/unscience/features/kitten-animations-clip-catalog
  - mods/unscience/features/doh-runtime-gpu-materials
  - mods/unscience/features/humble-arteest-kitten-visor-toggle
  - mods/ksacars/features/seated-crew-of-unflown-craft
  - mods/unscience/features/kitten-animations-forced-clip-playback
lifecycle: [frame:draw-ui, frame:after-on-frame, frame:render, render:prepass, render:shadows]
game_types:
  - KSA.KittenEva
  - KSA.KittenRenderable
  - KSA.CharacterAvatar
  - KSA.AnimatedRenderable
  - RenderCore.Animation.Skeleton
  - KSA.Program
  - KSA.IAnimProcessor
  - KSA.IVASeat
  - KSA.Vehicle
  - KSA.AnimationAssetRef
  - KSA.IAnimation
  - KSA.CatExpressionAnim
  - KSA.CatEyeAnim
  - KSA.CatEarAnim
  - KSA.BoneAnimRuntime
tags: [kitten, avatar, skeleton, bones, reflection, animation, render-order]
---
Almost every kitten mod starts with the same question: given a `KittenEva` that the game owns, how do I get at the animated model, its bones, its clips and its materials? The game exposes only a thin slice publicly. The rest sits in private fields of `KittenRenderable` and in struct fields of `CharacterAvatar`, so the mods in the reference set all use a small amount of reflection, and they differ mainly in how visibly they fail when a game update renames something. This topic compares those approaches and explains, as an engine background article, where in the frame a kitten is animated and drawn so you know when your code sees what.

## Engine background

The object chain, from the vehicle to the bones:

| Step | Member | Visibility | Source |
|---|---|---|---|
| `KittenEva` to its renderable | `KittenEva.Renderable` (field `_renderable`) | public getter; field private | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L59) |
| renderable to avatar | `KittenRenderable._characterAvatar` | **private** | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L13) |
| avatar to the animated model | `CharacterAvatar.Core.CharacterModel` (an `AnimatedRenderable`) | public field of a public struct field | [current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L30-L51) |
| model to bones | `AnimatedRenderable.Skeleton` (`Skeleton.BoneNames`, `WorldTransforms`, `LocalTransforms`, `InverseBindPose`) | public | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L54) |
| model to processors | `AnimatedRenderable.AnimProcessors` (`List<IAnimProcessor>`) | public | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L56) |
| avatar to attachments | `CharacterAvatar.Attachments` (`Helmet`, `Mmu`, `CosmeticAttachments`) | public structs | [current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L73-L141) |
| renderable to ground clips, samplers, processors | `_groundIdleAnim`, `_walkPairSampler`, `_catEyeAnim` and so on | **private** | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L31-L73) |

- `CharacterAvatar` is a class whose parts (`Core`, `Fur`, `Attachments`, `Expressions`, `Animations`) are **structs held in public fields**. Reading `avatar.Core.CharacterModel` in compiled code is fine and the `CharacterModel` it returns is the live shared object. A reflected read of a struct field boxes a copy, so reflection that walks `Core`, `Fur` or `Attachments` is reading copies of structs whose class-typed members still point at the live renderables (this is what [approach 3](reach-a-kittens-avatar-and-skeleton-3.md) relies on). Writing into a struct field of a class instance from compiled code (`avatar.Attachments.Helmet.Enabled = false`) writes through to the avatar.
- `KittenRenderable` has two public helpers that do the bone lookup for you: `TryFindBoneIndex(string, out int)` (case-insensitive over `Skeleton.BoneNames`) and `GetBoneAsmbOffset(int)` (the bone position in the vehicle's assembly frame via `ModelToBodyMatrix`) ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L107-L134)). They avoid the avatar hop for the simplest bone queries, but give no access to the pose span or the processor list.
- Every `KittenEva` constructor builds a non-seated `KittenRenderable(Character.Id)`; only an `IVASeat` builds a `seated: true` one, which loads the seated clips, disables the MMU and helmet attachments and uses a different animation path ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L136-L244), [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L66-L110)).
- The constructor installs four processors on `CharacterModel.AnimProcessors`: an optional personality `CatExpressionAnim` (priority 1, absent for Neutral), the reactive `CatExpressionAnim` (priority 1), `CatEyeAnim` (priority 0.8) and `CatEarAnim` (priority 2). Two of them are the same type, which is why the clip-catalog approach reads them by field name ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L202-L237)).
- The ground clips are loaded from `CharacterGroundAnimationsReference` into private fields; `CharacterAvatar.Animations.WalkingAnimations` is declared but `InitalizeFromCharacterRef` assigns only `WalkingAnim` and never `RunningAnim` ([current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L181-L186), [current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L404-L410)).

## Where kittens are animated and drawn

Unlike part models, kittens are not updated by `UpdateRenderingResources`. Each viewport's `RenderGame` or `RenderViewport` clears that viewport's mesh buckets and then calls `KittenEva.UpdateRenderData(viewport, ...)` for every kitten in `VehiclesInFrame`, followed by `ControlledVehicle.UpdateSeatedCrewRenderData`. The animation, the skinning and the shadow-pool entries are all produced in that one call, [approach 4](reach-a-kittens-avatar-and-skeleton-4.md) walks through it.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. One reflection hop to the private avatar, bones resolved by name](reach-a-kittens-avatar-and-skeleton-1.md) | KSAGolf | `reflection` | `frame:after-on-frame`, `frame:render` | One helper (`AvatarOf`) so every feature breaks together; unknown bones are reported, not thrown |
| [2. Cached FieldInfo reads of the private clip and processor fields](reach-a-kittens-avatar-and-skeleton-2.md) | unscience | `reflection` | `frame:draw-ui` | Thirteen clip fields, four samplers, four processors; unresolved names are shown in the UI |
| [3. Walk renderable struct fields down to the live MaterialIndices arrays](reach-a-kittens-avatar-and-skeleton-3.md) | unscience | `reflection` | `frame:draw-ui` | Reaches protected `int[]` arrays across boxed structs; path of field names to maintain |
| [4. Background: the per-viewport draw path and animation order](reach-a-kittens-avatar-and-skeleton-4.md) | KSAStructures | `starmap-lifecycle` | `frame:render`, `render:prepass`, `render:shadows` | An explainer, not a technique: when your processor, patch or toggle is seen |

## See also

- [Pose a kitten's bones and aim its limbs with an IAnimProcessor](../../animation/pose-kitten-bones-with-an-anim-processor/overview.md)
- [Force a kitten to play a chosen clip, freeze it or change its speed](../../animation/play-a-specific-kitten-animation-clip/overview.md)
- [Make a kitten pull a facial expression](../../animation/make-a-kitten-pull-a-facial-expression/overview.md)
- [Read and extend the character asset XML](../define-a-kitten-character-in-asset-xml/overview.md)
- [Tint kitten fur, body and eyes, and hide the visor, helmet or MMU](../../appearance/tint-and-restyle-kitten-appearance/overview.md)
- [Hide a kitten's body from the main view only, keeping its shadow](../../appearance/hide-a-kitten-body-from-one-viewport/overview.md)
- [Animate seated kittens, draw them on unflown craft and fly another craft from a chair](../../animation/animate-and-draw-seated-kitten-crew/overview.md)
- [Contain exceptions in StarMap hooks](../../../getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Hide part meshes or subparts without removing them](../../../rendering/meshes/hide-parts-or-subparts-from-rendering/overview.md) - related topic

