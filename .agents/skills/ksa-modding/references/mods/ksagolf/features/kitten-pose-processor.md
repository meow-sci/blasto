---
type: Mod Feature
title: Posing kitten bones through the pose-processor list
description: Turn, move or stretch any bone of a live EVA kitten by adding an IAnimProcessor to its AnimatedRenderable.AnimProcessors list, reached through one reflection hop to the private avatar
mod: ksagolf
category: kittens
techniques: [reflection, anim-processor, procedural-animation, starmap-lifecycle]
hooks:
  - starmap StarMapAfterOnFrame
  - reflection KSA.KittenRenderable._characterAvatar
  - engine-callback KSA.IAnimProcessor.UpdateLocalPose
  - engine-callback KSA.IAnimProcessor.UpdateSkeleton
lifecycle: [frame:after-on-frame, frame:render]
game_types:
  - KSA.KittenEva
  - KSA.KittenRenderable
  - KSA.CharacterAvatar
  - KSA.AnimatedRenderable
  - KSA.IAnimProcessor
  - RenderCore.Animation.Skeleton
  - RenderCore.TransformTRS
  - KSA.OrbitView
  - KSA.CatPostAnim
  - KSA.CatEyeAnim
tags: [kitten, animation, skeleton, bones, ik, anim-processor, reflection, bridge]
---

KSA has no API for posing a kitten, but the engine already poses its own kittens with small
"processors": the face expression, the eyes and the ears are each an `IAnimProcessor` in the
character model's public `AnimProcessors` list.[^ksa-kittenrenderable] KSAGolf adds its own
processor to that same list. Each frame the engine hands it the bone-local pose KSA has just sampled
from its animation clip, the processor turns or moves whichever bones it likes, and the engine skins
whatever it leaves there.

`Ksa/KittenPoseProbe.cs` is the developer probe that proved this. The bridge's `kitten_pose`
command drives it with a list of bone turns. The putting pose (see
[Putting address pose](../features/putting-address-pose.md)) is built on the same seam. The only
private member it touches is the avatar field.

## How it works

1. **Reach the avatar.** `KittenEva.Renderable` is public and `KittenRenderable._characterAvatar`
   is private, so the probe reads that field once by reflection (`AvatarOf`). Every other kitten
   feature in the mod (`GolfPose`, `KittenPropProbe`, `KittenFrame`, `Golf`) goes through the same
   helper, so if the field moves they all stop together.[^golf-poseprobe]
2. **Resolve bones by name.** `avatar.Core.CharacterModel.Skeleton.BoneNames.IndexOf(name)` gives
   the index into the pose span. An unknown name goes into a `trouble` list in the reply and is not
   thrown.
3. **Attach once per model.** A private `Bend : IAnimProcessor` is added to
   `AnimatedRenderable.AnimProcessors`. A later `kitten_pose` retunes the processor that is already
   there instead of stacking a second one. `on: false` removes it again.
4. **Edit the local pose.** `UpdateLocalPose(transform, skeleton, Span<TransformTRS> localPose, dt)`
   gets the pose after KSA's own clip has been copied into the span. The probe pre-multiplies an
   axis-angle rotation onto `localPose[bone].Rotation`. `move` adds to `Translation` in the parent's
   frame and `stretch` scales `Scale`, which carries the bone's children with it. With `rest: true`
   it first replaces each named bone with its rest pose. It reads that pose once with
   `Skeleton.SetDefaultPose()` and `LocalTransforms`. Borrowing the skeleton like this is harmless,
   because the engine rewrites the local transforms from the processed pose straight
   afterwards.[^ksa-animatedrenderable]
5. **Shape it in time.** Each turn can be `Swing` (a sine either side), `Bounce` (one side) or
   `Spin` (a full rotation). It can be active only from `from` to `to` seconds, with 0.25 s fades, and
   the whole routine can repeat every `loop` seconds.
6. **Read back per view.** `UpdateSkeleton` copies the model-space world transforms of a fixed list
   of bones into a locked snapshot, and `report: true` returns that snapshot. That snapshot is how
   KITTEN-ARMS.md measured the rig: finger curl axes, arm lengths, and the skeleton-to-drawn fit.

The bridge command also sets a few things around the pose. `fly` takes control of the kitten,
because a kitten nobody is looking at is never rendered. `mmu` and `helmet` toggle
`CharacterAvatar.Attachments.Mmu.Enabled` and `Helmet.Enabled`. `orbit_az`, `orbit_el` and
`orbit_zoom` write the followed object's `OrbitView` so a capture is framed the same way each
time.[^golf-bridge]

### What KITTEN-ARMS.md records against what the code implements

`docs/KITTEN-ARMS.md` calls itself "a record". Some of what it describes is in the code, and some
is only a measurement or an idea.[^golf-kittenarms]

| Claim in KITTEN-ARMS.md | In code at this ref? |
|---|---|
| Pose through `AnimatedRenderable.AnimProcessors` / `IAnimProcessor` | Yes: `KittenPoseProbe.Bend`, `GolfPose` |
| One reflection hop to `_characterAvatar` | Yes: `KittenPoseProbe.AvatarOf` |
| One skeleton rebuilt per view, with `UpdateSkeleton` called per view (viewmodel technique) | Yes: `GolfPose.UpdateSkeleton` and `FirstPersonHook` |
| Turning arms at the shoulder to point the wrist at a target | Yes: `GolfPose.Reach` is a one-bone aim (shoulder only) |
| Two-bone IK from `Skeleton` names, parents and transforms | **No.** Recorded as possible; nothing solves the elbow |
| `CatPostAnim.MixPoseLocal` to blend a subtree ("arms from a holding pose, legs from the walk") | **No.** Not used. It is `protected` on the abstract `CatPostAnim`, so using it means subclassing that class[^ksa-catpostanim] |
| Capture the idle's rotation of the spine-to-hand chain when a hold begins, and hold it | **No.** Recorded as a measured technique. `GolfPose` poses on top of the live clip, and the probe's `rest` replaces named bones with the rest pose instead |
| Finger curl is local +y; the idle fist is 73-91 / 143-154 deg | Measurement only. `GolfPose` uses the knuckle bones to place the grip and never opens the paw |
| "Level is the world's, not the spine's" | Measurement only. Nothing levels a held object to the world |
| Skeleton space fits drawn space to 34 nm | Measurement only. The probes return both readings (`bones` and `bones_drawn`) and the fit was done outside the mod |
| Turning a whole kitten to face something | Listed under **Open**. `Golf` draws the turn on the skeleton instead (see [Putting address pose](../features/putting-address-pose.md)) |

## Lifecycle and timing

| Step | Where it runs | Lifecycle |
|---|---|---|
| `kitten_pose` read, `Apply` adds the processor or retunes it | `[StarMapAfterOnFrame] OnAfterFrame` then `Bridge.Update`, on the main thread after `Program.OnFrame` returns[^golf-mod][^starmap-programpatcher] | `frame:after-on-frame` |
| `Bend.UpdateLocalPose` | `Program.RenderGame` (main view) or `RenderViewport` (secondary views), then `KittenEva.UpdateRenderData`, `KittenRenderable.UpdateRenderData` and `AnimatedRenderable.UpdateAnimation`. Runs **once per frame**, for whichever view draws the kitten first[^ksa-program][^ksa-animatedrenderable] | `frame:render` |
| `Bend.UpdateSkeleton` | Same call, **once per view** that draws the kitten, after that view's world transforms are rebuilt | `frame:render` |

- **Why the list is mutated after the frame.** The bridge runs from the frame postfix, when this
  frame's render has finished. The engine only walks `AnimProcessors` inside the render, on the
  same main thread, so adding or removing at that point cannot collide with the walk.
- **What is safe inside the callbacks.** They run while Vulkan command buffers are being recorded
  for a view. `ClearBuckets` has already run for that view and the kitten is being drawn. The pose
  span and the skeleton are the only things meant to be written. `App.Run` calls `OnFrame` with no
  try/catch, so the probe catches every exception itself.
- **dt is simulated time.** `KittenRenderable.UpdateRenderData` scales the player delta by
  `Universe.GetSimulationSpeed() * GetAchivedSpeedFraction() * _groundAnimPlaybackRate`, and passes
  zero on the second view of a frame.[^ksa-kittenrenderable] While paused the speed is zero, so
  the probe's clock stands still.[^ksa-universe] That is why a turn that starts at `from: 0` begins
  at full weight: a paused capture still shows it.
- **Distance culling.** Before animating, `UpdateAnimationLod` sets a processor's `ShouldUpdate` to
  false beyond 45 m × its `Priority` from the camera of the view being drawn. The probe's
  `Priority` is 1, so that is 45 m.[^ksa-kittenrenderable] A kitten that no view draws is never
  animated at all.

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Ksa/KittenPoseProbe.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KittenPoseProbe.cs#L16-L23) | Reflection hop to the avatar (`AvatarOf`), shared by every kitten feature |
| [src/KSAGolf/Ksa/KittenPoseProbe.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KittenPoseProbe.cs#L42-L98) | `Apply`: walk EVA kittens, resolve bones, attach or retune the processor |
| [src/KSAGolf/Ksa/KittenPoseProbe.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KittenPoseProbe.cs#L155-L274) | `Bend : IAnimProcessor`: local-pose edits and per-view readback |
| [src/KSAGolf/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Bridge.cs#L356-L407) | `kitten_pose` parsing: inline turn or `turns: [...]`, orbit camera framing |
| [docs/KITTEN-ARMS.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/KITTEN-ARMS.md) | The record of what the rig and engine allow |

## Snippets

```csharp
// src/KSAGolf/Ksa/KittenPoseProbe.cs L16-L23
    // KittenEva.Renderable is public; the avatar inside it is not.
    private static readonly FieldInfo? AvatarField =
        typeof(KittenRenderable).GetField("_characterAvatar", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly List<(AnimatedRenderable Model, Bend Processor)> Attached = [];

    /// <summary>The avatar a kitten is drawn through, or null when the private field has moved.</summary>
    public static CharacterAvatar? AvatarOf(KittenEva kitten) => AvatarField?.GetValue(kitten.Renderable) as CharacterAvatar;
```

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

```csharp
// src/KSAGolf/Ksa/KittenPoseProbe.cs L171-L182, L229-L237
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
            // ...
                if (turn.Move)
                {
                    localPose[bone].Translation += turn.Axis * (float)amount;
                    continue;
                }

                floatQuat rotation = floatQuat.CreateFromAxisAngle(turn.Axis, float.DegreesToRadians((float)amount));
                localPose[bone].Rotation = floatQuat.Concatenate(rotation, localPose[bone].Rotation);
            }
```

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `KittenEva.Renderable` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L59) | Public getter for the kitten's `KittenRenderable` |
| `KittenRenderable._characterAvatar` (private) | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L13) | The live `CharacterAvatar`, read by reflection |
| `KittenRenderable.UpdateRenderData` / `UpdateAnimationLod` | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L311-L405) | Animates and draws the kitten per view, culls processors by distance × `Priority` |
| `CharacterAvatar.Core.CharacterModel` / `Attachments.Mmu.Enabled` / `Helmet.Enabled` | [current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L73-L137) | The skinned model and the attachment toggles |
| `AnimatedRenderable.AnimProcessors` / `UpdateAnimation` | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L134-L207) | Public processor list; local pose once per frame, `UpdateSkeleton` per call |
| `IAnimProcessor` | [current/decomp/KSA/IAnimProcessor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IAnimProcessor.cs) | `Priority`, `ShouldUpdate`, `CanCacheAnimation`, `UpdateLocalPose`, `UpdateSkeleton` |
| `Skeleton.BoneNames` / `SetDefaultPose` / `LocalTransforms` / `WorldTransforms` | [current/decomp/RenderCore.Animation/Skeleton.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Animation/Skeleton.cs) | Bone lookup, rest pose, model-space readback |
| `TransformTRS` | [current/decomp/RenderCore/TransformTRS.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/TransformTRS.cs) | The per-bone translation, rotation and scale the pose span holds |
| `OrbitView.Azimuth` / `Elevation` / `DistancePower` | [current/decomp/KSA/OrbitView.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitView.cs) | Bridge framing of the orbit camera |
| `CatEyeAnim`, `CatPostAnim.MixPoseLocal` | [current/decomp/KSA/CatEyeAnim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatEyeAnim.cs), [current/decomp/KSA/CatPostAnim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatPostAnim.cs) | Core's own processors: the look-at precedent and the subtree blend (not used by the mod)[^ksa-cateyeanim] |

## Pitfalls and version notes

- **Private field.** `_characterAvatar` is the one name with no signature behind it. The probe
  reports `KittenRenderable._characterAvatar not found` and everything kitten-related degrades
  instead of throwing. It still exists in build 2026.10.10.5554.[^ksa-kittenrenderable]
- **`CanCacheAnimation` has no engine effect here.** `AnimatedRenderable` never reads it. Only
  Core's `CatExpressionAnim` and `CatEarAnim` read their own flag, and a separate
  `SkinningPoseIsViewportInvariant` flag decides per-view caching. That flag is set for parachutes,
  not kittens. The probe returns `true` and `GolfPose` returns `false`, and neither changes anything.
- **Model distance cutoff.** KITTEN-ARMS.md says the model stops animating "at the same distance"
  as a processor. In the decomp the model's own cutoff is 45 m × `AnimatedRenderable.Priority`,
  whose default is 5, so 225 m, unless something lowers it.[^ksa-animatedrenderable]
- **Pose on top of the clip, or from rest.** By default a turn is added to whatever KSA animated,
  so the idle sway shows through. `rest: true` drops the clip for the named bones only, so walking
  shows the pose's own gait.
- **Built against 2026.9.22.5482.** `IAnimProcessor`, `AnimProcessors`, the call order in
  `UpdateAnimation` and the `KittenRenderable` line numbers that KITTEN-ARMS.md cites (`:211-237`,
  `:355`, `:399-404`) are unchanged in 2026.10.10.5554. `AnimatedRenderable` gained pose-bounds
  tracking for shadow culling, which matters to
  [First-person kitten view](../features/first-person-kitten-view.md) but not to this probe.
- The probe touches **every** EVA kitten in the world, not just the flown one.

## See also

- [Putting address pose drawn on the kitten skeleton](../features/putting-address-pose.md)
- [Mod meshes held by a kitten as cosmetic attachments](../features/kitten-held-props.md)
- [First-person kitten view that keeps the shadow](../features/first-person-kitten-view.md)
- [File command bridge and MCP server](../features/file-command-bridge.md)
- [Visual testing captures](../features/visual-testing-captures.md)
- [Edit the local pose in UpdateLocalPose, on a simulated-time clock, with the distance cull in mind](../../../howto/kittens/animation/pose-kitten-bones-with-an-anim-processor/pose-kitten-bones-with-an-anim-processor-1.md) - how-to article demonstrating this feature
- [Aim an arm at a point with a one-bone shoulder turn](../../../howto/kittens/animation/pose-kitten-bones-with-an-anim-processor/pose-kitten-bones-with-an-anim-processor-3.md) - how-to article demonstrating this feature
- [Hide the helmet, visor and MMU per avatar by clearing Helmet.Enabled and Mmu.Enabled](../../../howto/kittens/appearance/tint-and-restyle-kitten-appearance/tint-and-restyle-kitten-appearance-5.md) - how-to article demonstrating this feature
- [Reach the avatar with one reflection hop and resolve bones by name](../../../howto/kittens/characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-1.md) - how-to article demonstrating this feature

[^golf-poseprobe]: src/KSAGolf/Ksa/KittenPoseProbe.cs
[^golf-bridge]: src/KSAGolf/Ksa/Bridge.cs
[^golf-mod]: src/KSAGolf/Ksa/KSAGolfMod.cs
[^golf-kittenarms]: docs/KITTEN-ARMS.md
[^ksa-kittenrenderable]: KSA.KittenRenderable (decompiled, build 2026.10.10.5554)
[^ksa-animatedrenderable]: KSA.AnimatedRenderable (decompiled, build 2026.10.10.5554)
[^ksa-catpostanim]: KSA.CatPostAnim (decompiled, build 2026.10.10.5554)
[^ksa-cateyeanim]: KSA.CatEyeAnim (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
