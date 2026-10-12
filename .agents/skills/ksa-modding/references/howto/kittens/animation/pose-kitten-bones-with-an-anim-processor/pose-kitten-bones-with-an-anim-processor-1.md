---
type: How-To
title: Edit the local pose in UpdateLocalPose, on a simulated-time clock, with the distance cull in mind
description: KSAGolf's pose probe adds a Bend IAnimProcessor to the kitten's AnimProcessors list from the after-frame hook and, once per frame, pre-multiplies rotations, adds translations and scales bones in the sampled local pose with swing, bounce and spin shapes
category: kittens
group: animation
topic: pose-kitten-bones-with-an-anim-processor
approach: 1
difficulty: intermediate
prerequisites: [starmap, reflection]
demonstrated_by: [mods/ksagolf/features/kitten-pose-processor]
techniques: [anim-processor, reflection, starmap-lifecycle]
hooks:
  - starmap StarMapAfterOnFrame
  - reflection KSA.KittenRenderable._characterAvatar
  - engine-callback KSA.IAnimProcessor.UpdateLocalPose
lifecycle: [frame:after-on-frame, frame:render]
game_types:
  - KSA.IAnimProcessor
  - KSA.AnimatedRenderable
  - KSA.CharacterAvatar
  - KSA.KittenRenderable
  - RenderCore.Animation.Skeleton
  - RenderCore.TransformTRS
  - KSA.KittenEva
tags: [kitten, animation, anim-processor, local-pose, bones, procedural]
---
You can turn, move or stretch any bone of a live EVA kitten by adding an `IAnimProcessor` to its model's `AnimProcessors` list. This article builds the simplest useful one: the engine hands the processor the bone-local pose it has just sampled from the current clip, the processor edits that pose, and the engine skins the result. It is KSAGolf's developer pose probe (`Bend`), and it is the cheapest way to pose a kitten because the edit happens once per frame and is shared by every viewport. Prerequisites: a StarMap mod with an after-frame hook and the reflection hop to the avatar ([reach the avatar](../../characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-1.md)). The mod that demonstrates it is KSAGolf.

## Background: how the game does it

- `AnimatedRenderable.UpdateAnimation` samples the clip into `RuntimeAnim.Transforms`, copies it into a `TransformTRS[]` span, and calls `UpdateLocalPose(Transform, Skeleton, span, dt)` on every processor whose `ShouldUpdate` is true, **only when `Program.FrameNumber` differs from the last pose update**. The span is then converted to local matrices, the world transforms are rebuilt and the pose is skinned ([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L145-L172)).
- `TransformTRS` has `Translation`, `Rotation` (`floatQuat`) and `Scale`. A rotation in the bone's parent frame is pre-multiplied (`floatQuat.Concatenate(delta, current)`), a translation is added in the parent's frame and a scale scales the bone and everything below it ([current/decomp/RenderCore/TransformTRS.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/TransformTRS.cs)).
- The kitten calls `UpdateAnimationLod` before animating: it disables the model beyond 45 m times its `Priority` (default 5, so 225 m) and each processor beyond 45 m times that processor's own `Priority` from the camera of the view being drawn. The stock processors use 0.8 (eyes), 1 and 1 (the two expressions) and 2 (ears) ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L202-L237), [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L704-L726)).
- `dt` is simulated time: `KittenRenderable.UpdateRenderData` scales the player delta by the simulation speed and achieved speed fraction and by the ground playback rate, and passes 0 on the second and later views in a frame. While paused the speed is zero ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L311-L355)).
- A kitten no view draws is never animated, so its processors are never called.

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Add or retune the processor | `[StarMapAfterOnFrame]` handler, after `Program.OnFrame` returned | `frame:after-on-frame` |
| `UpdateLocalPose` | `Program.RenderGame` or `RenderViewport` -> `KittenEva.UpdateRenderData` -> `KittenRenderable.UpdateRenderData` -> `AnimatedRenderable.UpdateAnimation`, once per frame, for whichever view draws the kitten first | `frame:render` |

Why the list is changed after the frame: the engine walks `AnimProcessors` only inside the render, on the main thread, so adding or removing between frames cannot collide with that walk. Inside the callback the Vulkan command buffers are being recorded and the only things meant to be written are the pose span and the skeleton; `App.Run` calls `OnFrame` with no try/catch, so the processor catches everything itself ([game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence), [contain exceptions](../../../getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md)).

## Steps

1. **Get the avatar and model** with the single reflection hop; resolve bone names with `Skeleton.BoneNames.IndexOf` and report unknown names without throwing ([approach 1 of the avatar topic](../../characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-1.md)).

2. **Attach one processor per model** and retune it on later calls instead of stacking another:

```csharp
// src/KSAGolf/Ksa/KittenPoseProbe.cs L79-L90
            Bend? existing = Attached.Find(a => ReferenceEquals(a.Model, model)).Processor;
            Bend processor = existing ?? new Bend();
            processor.Turns = resolved.ToArray();
            processor.LoopSeconds = loopSeconds;
            processor.Rate = rate;
            processor.FromRest = fromRest;
            processor.Restart();
            if (existing is null)
            {
                model.AnimProcessors.Add(processor);
                Attached.Add((model, processor));
            }
```

3. **Implement the interface.** `Priority` is how far away the processor stops running (45 m times it); `CanCacheAnimation` has no effect on a kitten's `AnimatedRenderable` (only the stock `CatExpressionAnim` and `CatEarAnim` read their own flag). `UpdateLocalPose` receives the pose after the clip was copied in; advance a clock by `dt` so a paused game freezes the pose:

```csharp
// src/KSAGolf/Ksa/KittenPoseProbe.cs L171-L182
        public float Priority => 1f;
        public bool ShouldUpdate { get; set; } = true;
        public bool CanCacheAnimation => true;

        public void UpdateLocalPose(float4x4 transform, Skeleton skeleton, Span<TransformTRS> localPose, float dt)
        {
            // Inside the engine's render: nothing here may throw.
            Calls++;
            _clock += dt * Rate;

            // KSA's own clip is dropped for every bone the pose names, so walking plays the pose's gait
            // rather than the pose added to a two-legged walk.
```

4. **Optionally replace the clip for named bones with the rest pose.** `Skeleton.SetDefaultPose` copies the default local pose into the skeleton's local transforms; borrowing them to read the rest pose once leaves nothing behind because the engine rewrites the local transforms from the processed pose straight afterwards:

```csharp
// src/KSAGolf/Ksa/KittenPoseProbe.cs L183-L200
            if (FromRest)
            {
                // The engine rewrites the local transforms from the pose once processors are done, so
                // borrowing them to read the rest pose once leaves nothing behind.
                if (_rest is null)
                {
                    skeleton.SetDefaultPose();
                    Span<float4x4> rest = skeleton.LocalTransforms;
                    _rest = new TransformTRS[rest.Length];
                    for (int i = 0; i < rest.Length; i++) _rest[i] = TransformTRS.FromMatrix(rest[i]);
                }

                foreach ((int bone, Turn _) in Turns)
                {
                    if ((uint)bone < (uint)localPose.Length && bone < _rest.Length) localPose[bone] = _rest[bone];
                }
            }
            double t = LoopSeconds > 0f ? _clock % LoopSeconds : _clock;
```

5. **Apply the shapes.** Each turn is a `Swing` (a sine either side), `Bounce` (one side only) or `Spin` (round and round), active between `from` and `to` seconds with 0.25 s fades, repeating every `loop` seconds. `move` adds to `Translation` in the parent's frame, `stretch` scales along an axis (children follow), otherwise the rotation is pre-multiplied:

```csharp
// src/KSAGolf/Ksa/KittenPoseProbe.cs L202-L237
            foreach ((int bone, Turn turn) in Turns)
            {
                if ((uint)bone >= (uint)localPose.Length) continue;

                // A move from the top of the routine starts at full weight, so a paused world still shows it.
                double rise = turn.From > 0f ? (t - turn.From) / FadeSeconds : 1.0;
                double weight = Math.Clamp(Math.Min(rise, (turn.To - t) / FadeSeconds), 0.0, 1.0);

                // Faded, a spin would unwind the whole of it in a quarter second; whole turns join seamlessly.
                if (turn.Shape == Shape.Spin) weight = t >= turn.From && t < turn.To ? 1.0 : 0.0;
                if (weight <= 0.0) continue;

                double phase = (2.0 * Math.PI * turn.Hz * t) + double.DegreesToRadians(turn.PhaseDeg);
                double amount = turn.Shape switch
                {
                    Shape.Bounce => turn.Degrees + (turn.Swing * Math.Abs(Math.Sin(phase))),
                    Shape.Spin => turn.Degrees + (360.0 * ((turn.Hz * (t - turn.From)) % 1.0)),
                    _ => turn.Degrees + (turn.Swing * Math.Sin(phase)),
                };
                amount *= weight;

                if (turn.Stretch)
                {
                    localPose[bone].Scale *= float3.One + (turn.Axis * (float)amount);
                    continue;
                }

                if (turn.Move)
                {
                    localPose[bone].Translation += turn.Axis * (float)amount;
                    continue;
                }

                floatQuat rotation = floatQuat.CreateFromAxisAngle(turn.Axis, float.DegreesToRadians((float)amount));
                localPose[bone].Rotation = floatQuat.Concatenate(rotation, localPose[bone].Rotation);
            }
```

6. **Make sure a view draws the kitten.** A kitten nobody is looking at is never animated. The probe's `fly` option takes control of the kitten so the camera follows it; a kitten far away is also culled: at `Priority` 1 the processor stops 45 m from the drawing camera.

7. **Check the log or return value.** The probe returns how many times `UpdateLocalPose` was called (`Calls`), whether `ShouldUpdate` is true, whether the model is updating, and how many processors the model holds. `calls` rising while the kitten is on screen confirms the processor runs.

## Complete example

- [src/KSAGolf/Ksa/KittenPoseProbe.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KittenPoseProbe.cs#L14-L274) - the whole probe: `AvatarOf`, `Apply`, `Report`, the `Bend` processor.
- [src/KSAGolf/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Bridge.cs#L356-L407) - the `kitten_pose` command parser that drives it.
- Feature page: [Posing kitten bones through the pose-processor list](../../../../mods/ksagolf/features/kitten-pose-processor.md).

## Variations and gotchas

- **The pose is on top of the clip.** By default a turn is added to whatever KSA animated, so the idle sway shows through. `rest` drops the clip for the named bones only.
- **Distance cull.** `KITTEN-ARMS.md` says the model stops animating "at the same distance" as a processor; in the decompile the model's own cutoff is 45 m times `AnimatedRenderable.Priority`, whose default is 5, so 225 m unless something lowers it. A processor with `Priority` 1 stops at 45 m. Use a larger `Priority` for a processor that must keep running at distance (KSACars' `DriverHands` uses 5).
- **`CanCacheAnimation` is inert here.** The engine never reads it for the kitten model.
- **dt is zero on later views.** A processor that integrates `dt` advances once per frame, not once per view, which is what you want; one that depends on `dt` being non-zero will see zero for the second view.
- **The probe touches every EVA kitten,** not just the flown one.
- **Mutate `AnimProcessors` outside the render,** from a StarMap hook, never from inside a processor callback.
- **Built against 2026.9.22.5482;** `IAnimProcessor`, `AnimProcessors` and the call order in `UpdateAnimation` are unchanged in 2026.10.10.5554. `AnimatedRenderable` gained pose-bounds tracking for shadow culling.

## See also

- [Pose a kitten's bones and aim its limbs with an IAnimProcessor](overview.md), [approach 2: world transforms](pose-kitten-bones-with-an-anim-processor-2.md), [approach 3: shoulder aim](pose-kitten-bones-with-an-anim-processor-3.md)
- [Reach a kitten's avatar, skeleton, bones and animation catalog](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md)
- [Contain exceptions in StarMap hooks](../../../getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md)
