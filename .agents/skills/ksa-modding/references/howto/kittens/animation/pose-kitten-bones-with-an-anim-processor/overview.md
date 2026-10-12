---
type: Game Feature
title: Pose a kitten's bones and aim its limbs with an IAnimProcessor
description: How a mod edits a kitten's sampled local pose or its world transforms before skinning with an IAnimProcessor, aims an arm at a point with one-bone or two-bone solves, converts between model space and the world, and draws a stepped or turned kitten without moving its body
category: kittens
group: animation
topic: pose-kitten-bones-with-an-anim-processor
demonstrated_by:
  - mods/ksagolf/features/kitten-pose-processor
  - mods/ksagolf/features/putting-address-pose
  - mods/ksacars/features/driver-hands-steering-ik
  - mods/ksastructures/features/first-person-body-hiding-and-shadow
  - mods/ksastructures/features/first-person-kitten-view
lifecycle: [frame:after-on-frame, frame:draw-ui, frame:render]
game_types:
  - KSA.IAnimProcessor
  - KSA.AnimatedRenderable
  - KSA.CharacterAvatar
  - KSA.KittenRenderable
  - RenderCore.Animation.Skeleton
  - RenderCore.TransformTRS
  - KSA.KittenEva
  - KSA.IVASeat
  - KSA.KittenLocomotionTuning
  - RenderCore.TransformNodeInfo
  - KSA.Vehicle
tags: [kitten, animation, skeleton, anim-processor, ik, model-space, pose]
---
KSA animates a kitten by sampling a clip into a bone-local pose and then letting a list of `IAnimProcessor`s adjust it. The game's own eyes, ears and facial expressions are processors in that list, and it is public, so a mod can add its own to bend a spine, swing a tail, put a paw on a steering wheel or carry a whole skeleton to a different spot, all without touching the physics body. The processor has two callbacks with different costs and different guarantees, which is the main thing to choose between. This topic covers that choice, three ways to aim an arm (a local-pose edit, a one-bone shoulder turn and a two-bone solve), the model-space-to-world conversion every aim needs, and drawing a kitten stepped, turned or leaning while its body stays put. It also covers when a processor is culled by distance.

## Engine background

| Piece | What it is | Source |
|---|---|---|
| `IAnimProcessor` | `Priority`, `ShouldUpdate`, `CanCacheAnimation`, `UpdateLocalPose(transform, skeleton, Span<TransformTRS> localPose, dt)`, `UpdateSkeleton(transform, skeleton, dt)` | [current/decomp/KSA/IAnimProcessor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IAnimProcessor.cs) |
| `AnimatedRenderable.UpdateAnimation` | Samples the clip into `RuntimeAnim.Transforms`, copies that into a processed-pose span, calls every enabled processor's `UpdateLocalPose` **once per frame**, rebuilds the skeleton's local and world transforms, calls every enabled processor's `UpdateSkeleton` **on every call (once per view)**, then writes skinning matrices | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L134-L207) |
| `KittenRenderable.UpdateAnimationLod` | Sets `ShouldUpdate` on the model and each processor from distance times `Priority` | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L704-L726) |
| `Skeleton` | `BoneNames`, `NodeInfo` (parent index, child range), `LocalTransforms`, `WorldTransforms`, `InverseBindPose`, `SetDefaultPose` | [current/decomp/RenderCore.Animation/Skeleton.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Animation/Skeleton.cs) |
| `SetWorldTransform` | Extension on the transform hierarchy: writes a bone's world matrix, derives its local matrix from the parent and updates the descendants. The game's own `CatEyeAnim` uses it from `UpdateSkeleton` | [current/decomp/RenderCore/PackedTransformExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/PackedTransformExtensions.cs#L135-L150), [current/decomp/KSA/CatEyeAnim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatEyeAnim.cs#L238-L246) |
| `KittenRenderable.ModelToBodyMatrix` | Scale, then -90 degrees about X and about Z: the model-to-body chain | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L107-L110) |

Two facts decide most designs. The local pose is computed once per frame, so an edit there is shared by every viewport; the world transforms and skinning are recomputed per view, so only `UpdateSkeleton` can differ between views ([draw path](../../characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-4.md)). And the model is drawn in its own space, centimetres with +Y up, +Z the way the kitten faces and +X its left; every aim target must be expressed there.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. Edit the local TRS pose in `UpdateLocalPose`, with the distance cull in mind](pose-kitten-bones-with-an-anim-processor-1.md) | KSAGolf | `anim-processor` | `frame:after-on-frame`, `frame:render` | Cheapest, shared by all views; pose on top of the clip, simulated-time clock, culled by distance |
| [2. Overwrite world transforms in `UpdateSkeleton` once per view](pose-kitten-bones-with-an-anim-processor-2.md) | KSACars, KSAStructures | `anim-processor` | `frame:draw-ui`, `frame:after-on-frame`, `frame:render` | Edits go straight into the skin and can differ per view; exceptions switch the processor off once |
| [3. One-bone shoulder aim](pose-kitten-bones-with-an-anim-processor-3.md) | KSAGolf | `anim-processor` | `frame:render` | No elbow solve; good enough when the target is near the reach |
| [4. Two-bone IK with a clamped reach and an outward-and-down elbow](pose-kitten-bones-with-an-anim-processor-4.md) | KSACars | `two-bone-ik` | `frame:render` | Wrist lands on the grip; pure maths in a KSA-free class with tests |
| [5. Convert a standing kitten's model space to the ecliptic or a camera ego frame](pose-kitten-bones-with-an-anim-processor-5.md) | KSAGolf, KSAStructures | `reflection` | `frame:render`, `frame:draw-ui` | A helper repeats the engine chain; no drawn matrix is read |
| [6. Stance matrix on the roots plus a spine lean, eased on player time](pose-kitten-bones-with-an-anim-processor-6.md) | KSAGolf | `anim-processor` | `frame:draw-ui`, `frame:render` | Kitten stepped and turned while the body stays put; teleporting would throw it |

## See also

- [Reach a kitten's avatar, skeleton, bones and animation catalog](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md)
- [Animate seated kittens, draw them on unflown craft and fly another craft from a chair](../animate-and-draw-seated-kitten-crew/overview.md), [Attach a prop to a kitten's hand or another bone at runtime](../../appearance/attach-a-prop-to-a-kitten-bone/overview.md), [Steer a walking kitten](../../eva/steer-a-walking-kitten/overview.md)
- [Compute mission elapsed time and detect liftoff](../../../flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/overview.md), [Understand KSA coordinate frames and epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md), [Choose a render frame and anchor effects](../../../rendering/overlays/choose-a-render-frame-and-anchor-effects/overview.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Keep a vessel or kitten rendered at any distance](../../../rendering/meshes/keep-vessels-rendered-at-any-distance/overview.md) - related topic

