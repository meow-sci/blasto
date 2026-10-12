---
type: Game Feature
title: Make a kitten pull a facial expression
description: How the five authored faces are mixed over a kitten's pose by CatExpressionAnim processors, why writing the game's reactive processor never holds, and two ways to drive faces and the eye, ear and personality weights
category: kittens
group: animation
topic: make-a-kitten-pull-a-facial-expression
demonstrated_by: [mods/unscience/features/kitten-animations-facial-expressions]
lifecycle: [frame:draw-ui, frame:render]
game_types:
  - KSA.CatExpressionAnim
  - KSA.CatPostAnim
  - KSA.CatEyeAnim
  - KSA.CatEarAnim
  - KSA.CharacterAvatar
  - KSA.KittenRenderable
tags: [kitten, expression, face, anim-processor, eyes, ears, personality]
---
A kitten's face is not a separate mesh with morph targets: each expression is an authored full-pose clip (angry, awe, happy, sad, scared; two variants for happy, angry and awe) that a `CatExpressionAnim` processor blends over the head subtree with a weight. The game installs two of these on every kitten, plus an eye processor (look-at, blink, saccades) and an ear processor, and rewrites some of their fields every frame. To make a kitten pull a face on demand you either add a processor of your own, appended last so it mixes over everything, or you re-apply values after the game's write. unscience's Kitten Animations does both.

## Engine background

| Piece | Behaviour | Source |
|---|---|---|
| `CharacterAvatar.Expressions` | `Happy`, `Angry`, `Sad`, `Scared`, `Awe` lists of `AnimationAssetRef`; `ExpressionRootIndex` (the helmet socket bone, `Head_M`) and `ExpressionCenterIndex` | [current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L188-L207) |
| `CatExpressionAnim` | Samples `ExpressionAnim` once into a private `_expressionPose`, then `MixPoseLocal` blends it over the subtree below `ExpressionRootIndex` with `ExpressionWeight` | [current/decomp/KSA/CatExpressionAnim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatExpressionAnim.cs) |
| `CatPostAnim` | Abstract base: required `CharacterAvatar`, settable `Priority`, `CanCacheAnimation` always true, protected `MixPoseLocal` | [current/decomp/KSA/CatPostAnim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatPostAnim.cs) |
| `KittenRenderable` constructor | Adds the personality face (non-Neutral kittens, weight 1), the reactive face, the eye processor (priority 0.8, `MaxLookAtAngle` 30) and the ear processor (priority 2, weight 0 seated or 1) in that list order | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L185-L237) |
| `KittenRenderable.UpdateRenderData` | Rewrites the reactive `ExpressionWeight` every frame from acceleration; sets `CatEyeAnim.LookPitchOffsetDeg` every frame | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L342-L346) |
| `CatEarAnim`, `CatEyeAnim` | Ear weight mixes the helmet-mask clip on the ear bones; eye look angle, pitch offset and blink | [current/decomp/KSA/CatEarAnim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatEarAnim.cs), [current/decomp/KSA/CatEyeAnim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatEyeAnim.cs#L8-L40) |

The personality face is picked when the avatar is built (a random expression of the kitten's personality), so the same character can have different moods across spawns. Processors run in list order on the local pose, once per frame, and a later processor's `MixPoseLocal` lerps over whatever the earlier ones produced.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. Own `CatExpressionAnim` appended after the game's four, pose cache busted on every swap](make-a-kitten-pull-a-facial-expression-1.md) | unscience | `anim-processor` | `frame:draw-ui`, `frame:render` | A face that holds, with ease-in, hold, ease-out or latch; relies on a private cache field |
| [2. Re-apply each frame, cap with `Math.Min`, restore the bind-time value on unbind](make-a-kitten-pull-a-facial-expression-2.md) | unscience | `harmony-prefix` | `frame:render` | Ear, eye and personality weights and a cap on the reactive face; needs the `UpdateAnimation` prefix |

## See also

- [Force a kitten to play a chosen clip, freeze it or change its speed, and hold values the game rewrites](../play-a-specific-kitten-animation-clip/overview.md)
- [Reach a kitten's avatar, skeleton, bones and animation catalog](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md)
- [Pose a kitten's bones and aim its limbs with an IAnimProcessor](../pose-kitten-bones-with-an-anim-processor/overview.md)
- [Read and extend the character asset XML](../../characters/define-a-kitten-character-in-asset-xml/overview.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
