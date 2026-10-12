---
type: Game Feature
title: Force a kitten to play a chosen clip, freeze it or change its speed, and hold values the game rewrites
description: Why a clip set from a StarMap callback is overwritten every frame, why a Harmony prefix on AnimatedRenderable.UpdateAnimation is the point where a forced clip, a freeze or a rate multiplier still lands, and the patch-the-consumer pattern for any per-frame rewrite
category: kittens
group: animation
topic: play-a-specific-kitten-animation-clip
demonstrated_by: [mods/unscience/features/kitten-animations-forced-clip-playback]
lifecycle: [frame:draw-ui, frame:render]
game_types:
  - KSA.AnimatedRenderable
  - KSA.BoneAnimRuntime
  - KSA.KittenRenderable
  - KSA.KittenEva
  - KSA.IAnimation
  - KSA.LocomotionState
  - KSA.CatEyeAnim
  - KSA.CatEarAnim
  - KSA.CatExpressionAnim
tags: [kitten, animation, clip, harmony-prefix, freeze, playback-rate, per-frame-write]
---
The game chooses a kitten's body clip itself, every frame, from its locomotion state: idle, walk, run, jump, tumble, ladder, swim or the MMU blend. A mod that calls `AnimatedRenderable.SetAnimation` from a StarMap callback has its clip replaced before it is ever sampled. The same is true of other values the game rewrites per frame (the reactive expression weight, the eye pitch offset, `FreezeAnimation`). Unscience's Kitten Animations solves it by moving the write to the one moment after the game's choice and before the pose is evaluated: a Harmony prefix on `AnimatedRenderable.UpdateAnimation`. This topic explains the engine order, the prefix, and the generalisation: when the game rewrites a value every frame, patch the consumer, not the producer.

## Engine background

| Piece | Role | Source |
|---|---|---|
| `KittenRenderable.UpdateRenderData` | Per viewport: zero `dt` after the first call in a frame, call `UpdateLocomotionAnimationState`, then `CharacterModel.UpdateAnimation(dt2)` | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L311-L355) |
| `KittenRenderable.UpdateLocomotionAnimationState` | Chooses the clip by locomotion mode with `SetAnimation` / `PlayAnimation` and writes `FreezeAnimation` and the playback rate | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L432-L535) |
| `AnimatedRenderable.SetAnimation`, `PlayAnimation`, `UpdateAnimation`, `FreezeAnimation` | `SetAnimation` is a no-op when the clip is already current; `PlayAnimation` restarts it; a frozen model skips `RuntimeAnim.Update` | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L124-L152) |
| `BoneAnimRuntime` | Holds the current clip, cross-fade time and clock; `IsBlending`, `SampleCurrentAnimation` | [current/decomp/KSA/BoneAnimRuntime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BoneAnimRuntime.cs) |
| `CatEyeAnim`, `CatEarAnim`, `CatExpressionAnim` | Processors whose weights the game also writes each frame | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L342-L345) |

The order is fixed: the game picks the clip (step 3 in the [draw path](../../characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-4.md)), then calls `UpdateAnimation(dt2)` with `dt2 = dt * simulationSpeed * achievedSpeedFraction * groundAnimPlaybackRate`. A prefix on `UpdateAnimation` therefore sees the game's choice and can replace it; the `ref double dt` parameter lets it change the playback rate.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. Prefix on `UpdateAnimation` compares the instance by reference and re-applies](play-a-specific-kitten-animation-clip.md) | unscience | `harmony-prefix` | `frame:draw-ui`, `frame:render` | The last point in the frame where an override lands; runs for every animated renderable, so it must be a reference compare and return |

## See also

- [Reach a kitten's avatar, skeleton, bones and animation catalog](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md), [approach 2: the clip catalog](../../characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-2.md)
- [Restore kitten colours, clips and tuning after a save load](../../characters/restore-kitten-customizations-after-a-load/overview.md)
- [Make a kitten pull a facial expression](../make-a-kitten-pull-a-facial-expression/overview.md)
- [Isolate patch failures and fault-proof patch bodies](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
