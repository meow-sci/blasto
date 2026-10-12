---
type: How-To
title: Reach the avatar with one reflection hop and resolve bones by name
description: KSAGolf reads the private KittenRenderable._characterAvatar field once through a single helper, resolves bone names to pose-span indices with Skeleton.BoneNames.IndexOf and reports unknown names instead of throwing
category: kittens
group: characters
topic: reach-a-kittens-avatar-and-skeleton
approach: 1
difficulty: intermediate
prerequisites: [starmap, reflection]
demonstrated_by:
  - mods/ksagolf/features/kitten-pose-processor
  - mods/ksastructures/features/first-person-body-hiding-and-shadow
techniques: [reflection, starmap-lifecycle]
hooks: [starmap StarMapAfterOnFrame, reflection KSA.KittenRenderable._characterAvatar]
lifecycle: [frame:after-on-frame, frame:render]
game_types:
  - KSA.KittenEva
  - KSA.KittenRenderable
  - KSA.CharacterAvatar
  - KSA.AnimatedRenderable
  - RenderCore.Animation.Skeleton
  - KSA.IAnimProcessor
tags: [kitten, avatar, skeleton, bones, reflection]
---
This article gets you from a `KittenEva` to its `CharacterAvatar`, its `AnimatedRenderable` model, its `Skeleton` and a bone index for a name such as `Head_M`, with the smallest reflection surface that works and a failure mode that degrades instead of throwing. It is the pattern behind every kitten feature in KSAGolf (`KittenPoseProbe`, `GolfPose`, held props) and KSAStructures (`KittenFrame.AvatarOf`, first-person body hiding). Prerequisites: a StarMap mod with a per-frame hook ([create a minimal StarMap mod](../../../getting-started/loader/create-a-minimal-starmap-mod/overview.md)). The mod that demonstrates it is KSAGolf; KSAStructures repeats the same helper.

## Background: how the game does it

- `KittenEva.Renderable` is a public getter for the private `_renderable` field, so the `KittenRenderable` is reachable without reflection ([current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L59)).
- The `CharacterAvatar` inside it is `KittenRenderable._characterAvatar`, a private field with no public accessor ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L13)).
- From the avatar everything else is public: `avatar.Core.CharacterModel` is the `AnimatedRenderable`, its `Skeleton.BoneNames` is a `List<string>?`, and the attachment toggles are `avatar.Attachments.Helmet.Enabled` and `avatar.Attachments.Mmu.Enabled` ([current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L30-L51), [current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L103-L141)).
- The engine's own bone lookup, `KittenRenderable.TryFindBoneIndex`, is public and case-insensitive; it is the cheapest route when you only need an index and do not need the avatar ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L117-L134)).
- The model skeleton (a clone of the `KittenGlb` rig) holds the bone names the XML uses: `Head_M`, `Chest_M`, `EyeJoint_L`, `EyeJoint_R`, `EyeLidJoints_M`, `ear1_R` and so on ([character asset XML](../define-a-kitten-character-in-asset-xml/overview.md)). The mods in this repository also use `Root_M`, `Spine1_M`, `Shoulder_L/R`, `Elbow_L/R`, `Wrist_L/R`, the finger joints and `NoseJoint_M`.

## Lifecycle and timing

| Step | Where it runs | Lifecycle |
|---|---|---|
| Enumerate kittens, read the avatar, resolve bones, add or retune a processor | `[StarMapAfterOnFrame]` handler, main thread after `Program.OnFrame` returned | `frame:after-on-frame` |
| `IAnimProcessor.UpdateLocalPose` / `UpdateSkeleton` on the processor you attached | `Program.RenderGame` or `RenderViewport` -> `KittenEva.UpdateRenderData` -> `KittenRenderable.UpdateRenderData` -> `AnimatedRenderable.UpdateAnimation` | `frame:render` |

The avatar and the skeleton are plain main-thread objects; read them in the per-frame hook and only mutate the `AnimProcessors` list there, never from inside a processor callback, because the engine iterates the list during the render. The after-on-frame point is after the render, so adding or removing a processor cannot collide with that iteration ([game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)). Bone names resolve to the same index for the life of a skeleton, so resolve once per attach.

## Steps

1. **Add one `AvatarOf` helper and use it everywhere.** The field is resolved once (a static readonly `FieldInfo`); a null `FieldInfo` means the field has moved, and every feature that goes through the helper degrades together instead of each failing in its own way:

```csharp
// src/KSAGolf/Ksa/KittenPoseProbe.cs L16-L23
    // KittenEva.Renderable is public; the avatar inside it is not.
    private static readonly FieldInfo? AvatarField =
        typeof(KittenRenderable).GetField("_characterAvatar", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly List<(AnimatedRenderable Model, Bend Processor)> Attached = [];

    /// <summary>The avatar a kitten is drawn through, or null when the private field has moved.</summary>
    public static CharacterAvatar? AvatarOf(KittenEva kitten) => AvatarField?.GetValue(kitten.Renderable) as CharacterAvatar;
```

2. **Enumerate the kittens from the system's own list.** KSAGolf's `KsaWorld.CollectVehicles` reads `Universe.CurrentSystem.All` and not `Program.VehiclesInFrame`, because the per-frame buffer reads back empty from a postfix on `OnFrame`:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L274-L292
    public static void CollectVehicles(List<Vehicle> into)
    {
        into.Clear();

        try
        {
            if (Universe.CurrentSystem is { } system)
            {
                ReadOnlySpan<Astronomical> all = system.All.AsSpan();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] is Vehicle { IsDisposed: false } v) into.Add(v);
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"vehicle enumeration failed: {e.Message}");
        }
```

3. **Resolve the avatar, report a missing field, and resolve bones by name.** `BoneNames.IndexOf` gives the index into the pose span and the world-transform span; an unknown name goes into a `trouble` list that the caller returns instead of throwing:

```csharp
// src/KSAGolf/Ksa/KittenPoseProbe.cs L50-L77
        if (AvatarField is null) trouble.Add("KittenRenderable._characterAvatar not found");

        foreach (Vehicle vehicle in KsaWorld.Vehicles)
        {
            if (vehicle is not KittenEva kitten) continue;
            kittens++;

            // A kitten nobody is looking at is never rendered, so its processors are never called.
            if (fly && flying is null && KsaWorld.GoTo(kitten)) flying = KsaWorld.DisplayName(kitten);
            if (AvatarField?.GetValue(kitten.Renderable) is not CharacterAvatar avatar) continue;

            // The MMU's hand controllers ride the forearms and hide the hands.
            if (mmu is { } showMmu) avatar.Attachments.Mmu.Enabled = showMmu;
            if (helmet is { } showHelmet) avatar.Attachments.Helmet.Enabled = showHelmet;

            AnimatedRenderable model = avatar.Core.CharacterModel;
            List<(int Bone, Turn Turn)> resolved = [];
            foreach (Turn turn in turns)
            {
                int index = model.Skeleton.BoneNames?.IndexOf(turn.Bone) ?? -1;
                if (index < 0)
                {
                    trouble.Add($"no bone '{turn.Bone}' on {KsaWorld.DisplayName(vehicle)}");
                    continue;
                }

                resolved.Add((index, turn.Move || turn.Stretch ? turn : turn with { Axis = float3.Normalize(turn.Axis) }));
            }
```

4. **Attach a processor once per model** and keep a list of `(model, processor)` pairs so a second call retunes instead of stacking. The engine will now call the processor for every view that draws the kitten:

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

5. **Read bones in the processor with the same names.** `UpdateSkeleton` receives the model-space world transforms after the engine rebuilt them; the probe resolves a watch list by name every call and copies the matrices into a locked snapshot for the bridge ([src/KSAGolf/Ksa/KittenPoseProbe.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KittenPoseProbe.cs#L233-L274)). For the pose-editing half see [Pose a kitten's bones](../../animation/pose-kitten-bones-with-an-anim-processor/overview.md).

6. **Check the log.** With the field present nothing is logged; with it moved, `trouble` carries `KittenRenderable._characterAvatar not found` and the kitten features stop quietly. Test that path once by renaming the string and confirming the mod reports it.

## Complete example

- [src/KSAGolf/Ksa/KittenPoseProbe.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KittenPoseProbe.cs) - `AvatarOf`, `Apply`, the `Bend` processor and the per-view readback.
- [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L274-L320) - the vehicle census used to find kittens.
- [src/KSAStructures/Ksa/KittenFrame.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KittenFrame.cs#L16-L23) - the same helper in KSAStructures, shared by its first-person features.

## Variations and gotchas

- **One name, one place.** `_characterAvatar` is the only private name this approach depends on. KSAStructures has the same single-helper layout, so a game update that renames the field breaks the mods' kitten features together and visibly.
- **A kitten nobody draws is never animated.** `UpdateRenderData` runs only for kittens in `VehiclesInFrame` and the model is culled beyond 45 m times its `Priority` (default 5 on the model, 1 on the probe) from the drawing camera, so a processor on a faraway or off-screen kitten is not called ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L704-L726)). Fly the kitten, or have a view draw it, before expecting callbacks.
- **`CanCacheAnimation` has no effect here.** `AnimatedRenderable` never reads it; only Core's own `CatExpressionAnim` and `CatEarAnim` read their own flag.
- **The probe touches every kitten.** Filter on the flown kitten (`Program.ControlledVehicle as KittenEva`) if you only want one.
- **Struct reads are copies.** `Core`, `Attachments` and friends are structs; a reflected read of them returns a boxed copy. Prefer compiled access (`avatar.Core.CharacterModel`) and reflection only for the private fields.
- **Kitten seated crew are different kittens.** `KittenEva.Renderable` covers EVA kittens only. Kittens drawn in a seat are built by `IVASeat` ([seated crew](../../animation/animate-and-draw-seated-kitten-crew/overview.md)).

## See also

- [Reach a kitten's avatar, skeleton, bones and animation catalog](overview.md)
- [Approach 2: private clip and processor fields](reach-a-kittens-avatar-and-skeleton-2.md), [approach 3: live material arrays](reach-a-kittens-avatar-and-skeleton-3.md), [approach 4: draw path and animation order](reach-a-kittens-avatar-and-skeleton-4.md)
- Mod features: [Posing kitten bones through the pose-processor list](../../../../mods/ksagolf/features/kitten-pose-processor.md), [First-person body hiding and shadow](../../../../mods/ksastructures/features/first-person-body-hiding-and-shadow.md)
- [Pose a kitten's bones and aim its limbs with an IAnimProcessor](../../animation/pose-kitten-bones-with-an-anim-processor/overview.md), [Contain exceptions in StarMap hooks](../../../getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md)
