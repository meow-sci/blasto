---
type: How-To
title: Build a live clip catalog from cached FieldInfo reads with a visible unresolved list
description: unscience reads the thirteen private clip fields, four blend samplers and four anim processors of KittenRenderable by name, adds the public MMU and overlay clips, and shows any name that no longer resolves in the UI so a game update degrades visibly
category: kittens
group: characters
topic: reach-a-kittens-avatar-and-skeleton
approach: 2
difficulty: intermediate
prerequisites: [starmap, reflection, imgui]
demonstrated_by: [mods/unscience/features/kitten-animations-clip-catalog]
techniques: [reflection, imgui-window]
hooks:
  - starmap StarMapBeforeGui
  - starmap StarMapAfterGui
  - reflection KSA.KittenRenderable._characterAvatar
  - reflection KSA.KittenRenderable._groundIdleAnim
  - reflection KSA.KittenRenderable._catEyeAnim
lifecycle: [frame:draw-ui]
game_types:
  - KSA.KittenEva
  - KSA.KittenRenderable
  - KSA.CharacterAvatar
  - KSA.AnimationAssetRef
  - KSA.IAnimation
  - KSA.CatExpressionAnim
  - KSA.CatEyeAnim
  - KSA.CatEarAnim
  - KSA.BoneAnimRuntime
  - KSA.IVASeat
tags: [kitten, animation, clips, catalog, reflection, processors]
---
The clips a kitten can play are not all reachable from `CharacterAvatar`. The MMU clips and the blink and ear-mask overlays are public fields; the ground set (idle, walk, run, jump, tumble, ladder, moon walk, swim, seated) lives in private fields of `KittenRenderable`, as do the blend samplers and the four anim processors. This article builds the catalog the Kitten Animations sub-mod of unscience shows as buttons, and its typed handles to the processors. Prerequisites: StarMap, reflection, an ImGui panel. The point worth copying is the failure mode: every private name is resolved through a cache, any name that no longer exists goes into a list shown in red in the UI, and nothing throws.

## Background: how the game does it

- `KittenRenderable`'s constructor loads the ground set from the character's `CharacterGroundAnimationsReference` (the `CharacterGroundAnimations` block of the [character asset XML](../define-a-kitten-character-in-asset-xml/overview.md)) into private `AnimationAssetRef?` fields, builds three `AnimationPairBlendSampler`s (swim idle/swim, walk/moon walk, run/moon run) and one `AnimationDirectionalBlendSampler` for the seven MMU candidates ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L43-L73), [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L140-L169), [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L268-L309)).
- The MMU clips are typed fields in `CharacterAvatar.Animations.MmuAnimations`; the blink and ear/helmet mask are `Animations.BlinkAnim` and `Animations.HelmetMaskAnim` ([current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L143-L180)).
- `CharacterAvatar.Animations.WalkingAnimations.WalkingAnim` is a duplicate of the ground walk clip and `RunningAnim` is never assigned in build 5554 ([current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L404-L410)), so a "run" button built on it does nothing. Run comes from the private `_groundRunAnim`.
- Four processors are installed in the constructor and kept in private fields: `_catPersonalityExpressionAnim` and `_catExpressionAnim` (both `CatExpressionAnim`), `_catEyeAnim` and `_catEarAnim` ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L202-L237)). The seated clips (`_seatedIdleAnim`, `_seatedIdleActionAnims`) are loaded only when the renderable was constructed with `seated: true`, which only an `IVASeat` does, so they are always empty on EVA kittens ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L166-L169), [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L246-L266)).
- Every clip implements `IAnimation` (`AnimLength`, default `LoopPeriod => AnimLength`); `BoneAnimRuntime.SampleCurrentAnimation` divides by `LoopPeriod`, so a zero-length entry must never be offered for playback ([current/decomp/KSA/IAnimation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IAnimation.cs), [current/decomp/KSA/BoneAnimRuntime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BoneAnimRuntime.cs#L100-L108)).

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| `Update`: resolve the target kitten, rebind when the kitten or avatar object changes | `[StarMapBeforeGui]` handler | `frame:draw-ui` |
| `RenderContent`: draw the button groups and the unresolved warning | `[StarMapAfterGui]` handler | `frame:draw-ui` |

The catalog holds the game's own `AnimationAssetRef` and sampler instances, so a button press plays the same object the kitten's `KittenRenderable` plays. The renderable is created with the `KittenEva` and lives as long as it, so rebinding on object identity is enough and there is no per-frame rescan. Everything is main thread. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence) for the draw-ui phase.

## Steps

1. **Resolve the avatar and renderable.** `KittenEva.Renderable` is typed; the avatar is the one private hop (a generic reflection helper that returns null when the field is missing):

```csharp
// kitten-animations.lib/KittenAvatarAccessor.cs L53-L58
    public static CharacterAvatar? GetAvatar(KittenRenderable? renderable)
    {
        if (renderable == null) return null;

        return ReflectionHelpers.GetFieldValue(renderable, "_characterAvatar") as CharacterAvatar;
    }
```

2. **Rebuild on bind, not per frame.** When the kitten or its avatar object differs from the bound one, build the catalog and the processor handles, and log unresolved names once:

```csharp
// kitten-animations.lib/KittenAnimationsSubmod.cs L102-L130
    private void Bind(KittenEva kitten, KittenRenderable renderable, CharacterAvatar avatar)
    {
        Unbind();

        var catalog = KittenAnimationCatalog.Build(avatar, renderable);
        var processors = KittenAnimProcessors.Read(renderable);

        _expressions.Attach(avatar);

        _driver.ClearClip();
        _driver.BindTarget(avatar.Core.CharacterModel, processors);

        _context = new AnimationUiContext
        {
            Kitten = kitten,
            Avatar = avatar,
            Catalog = catalog,
            Driver = _driver,
            Expressions = _expressions,
            Processors = processors,
            Random = _random,
        };

        _boundKitten = kitten;
        _boundAvatar = avatar;

        if (catalog.UnresolvedFields.Count > 0)
            Console.WriteLine($"kitten-animations: unresolved game fields: {string.Join(", ", catalog.UnresolvedFields)}");
    }
```

3. **Read the ground clips by field name.** The group builder lists every field name and a UI label; a missing field is added to `UnresolvedFields`, a field that exists but is null (the character does not author that clip) is skipped silently:

```csharp
// kitten-animations.lib/KittenAnimationCatalog.cs L64-L88
    private void AddGroundGroup(KittenRenderable renderable)
    {
        var group = new AnimationGroup
        {
            Name = "Ground / EVA",
            Tooltip = "Locomotion clips from CharacterGroundAnimations. KittenRenderable keeps these in "
                    + "private fields, so they are read by reflection.",
        };

        AddClip(group, renderable, "_groundIdleAnim", "Idle");
        AddClip(group, renderable, "_groundWalkAnim", "Walk");
        AddClip(group, renderable, "_groundRunAnim", "Run");
        AddClip(group, renderable, "_jumpIntroAnim", "Jump");
        AddClip(group, renderable, "_jumpLandAnim", "Jump Land");
        AddClip(group, renderable, "_flailAnim", "Tumble / Flail");
        AddClip(group, renderable, "_ladderAnim", "Ladder");
        AddClip(group, renderable, "_moonWalkAnim", "Moon Walk");
        AddClip(group, renderable, "_moonRunAnim", "Moon Run");
        AddClip(group, renderable, "_swimAnim", "Swim");
        AddClip(group, renderable, "_swimIdleAnim", "Swim Idle");
        AddClip(group, renderable, "_seatedIdleAnim", "Seated Idle");
        AddClipList(group, renderable, "_seatedIdleActionAnims", "Seated Action");
        Groups.Add(group);
    }
```

4. **Cache each `FieldInfo`, including the negative result,** and record the miss:

```csharp
// kitten-animations.lib/KittenAnimationCatalog.cs L149-L159
    private void AddClip(AnimationGroup group, KittenRenderable renderable, string fieldName, string label)
    {
        var field = ResolveField(fieldName);
        if (field == null)
        {
            UnresolvedFields.Add($"KittenRenderable.{fieldName}");
            return;
        }

        Add(group, field.GetValue(renderable) as AnimationAssetRef, label);
    }
```

```csharp
// kitten-animations.lib/KittenAnimationCatalog.cs L213-L230
    private static FieldInfo? ResolveField(string fieldName)
    {
        if (RenderableFields.TryGetValue(fieldName, out var cached))
            return cached;

        FieldInfo? field = null;
        try
        {
            field = typeof(KittenRenderable).GetField(fieldName, InstanceNonPublic);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"kitten-animations: Error resolving KittenRenderable.{fieldName}: {ex.Message}");
        }

        RenderableFields[fieldName] = field;
        return field;
    }
```

5. **Drop zero-length entries** so a forced clip cannot divide by zero inside `BoneAnimRuntime`:

```csharp
// kitten-animations.lib/KittenAnimationCatalog.cs L173-L184
    private static void Add(AnimationGroup group, AnimationAssetRef? animation, string label)
    {
        // A zero-length clip divides by its loop period inside BoneAnimRuntime, so never offer one.
        if (animation == null || animation.LoopPeriod <= 0f) return;

        group.Entries.Add(new AnimationEntry
        {
            Label = label,
            Animation = animation,
            Source = animation.Id.ToString(),
        });
    }
```

6. **Add the typed MMU and overlay clips** straight from the public struct fields (`avatar.Animations.MmuAnimations`, `BlinkAnim`, `HelmetMaskAnim`) and the four blend samplers by field name (`_walkPairSampler`, `_runPairSampler`, `_swimPairSampler`, `_blendSampler`). Both sampler types implement `IAnimation`, so they can be played like clips; their `Weight` is rewritten by the game every frame ([kitten-animations.lib/KittenAnimationCatalog.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationCatalog.cs#L90-L147)).

7. **Read the processors by role, not by type.** Two of the four are `CatExpressionAnim`, so scanning `AnimProcessors.OfType<>()` cannot tell the permanent mood face from the acceleration-driven reactive face:

```csharp
// kitten-animations.lib/KittenAnimProcessors.cs L27-L36
    public static KittenAnimProcessors Read(KittenRenderable renderable)
    {
        return new KittenAnimProcessors
        {
            Personality = ReflectionHelpers.GetFieldValue(renderable, "_catPersonalityExpressionAnim") as CatExpressionAnim,
            Reactive = ReflectionHelpers.GetFieldValue(renderable, "_catExpressionAnim") as CatExpressionAnim,
            Eye = ReflectionHelpers.GetFieldValue(renderable, "_catEyeAnim") as CatEyeAnim,
            Ear = ReflectionHelpers.GetFieldValue(renderable, "_catEarAnim") as CatEarAnim,
        };
    }
```

8. **Make breakage visible.** The UI shows a red line with a count and the names in the tooltip:

```csharp
// kitten-animations.lib/Ui/AnimationLibrarySection.cs L69-L75
    private static void RenderUnresolved(AnimationUiContext ctx)
    {
        ImGui.Spacing();
        ImGui.TextColored(new float4(1f, 0.3f, 0.3f, 1f),
            $"{ctx.Catalog.UnresolvedFields.Count} game field(s) could not be resolved — the game build may have changed.");
        ImGui.SetItemTooltip(string.Join("\n", ctx.Catalog.UnresolvedFields));
    }
```

## Complete example

- [kitten-animations.lib/KittenAnimationCatalog.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationCatalog.cs#L54-L88) - the catalog builder and `ResolveField`.
- [kitten-animations.lib/KittenAnimProcessors.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimProcessors.cs#L12-L37) - typed processor handles.
- [kitten-animations.lib/KittenAnimationsSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationsSubmod.cs#L35-L56) - `Update` and `Bind` in the sub-mod.
- [kitten-animations.lib/Ui/AnimationLibrarySection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/Ui/AnimationLibrarySection.cs#L1-L22) - the button groups.
- [kitten-animations.lib/KittenAvatarAccessor.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAvatarAccessor.cs#L10-L58) - kitten and avatar lookup.
- [ksa-abstractions.lib/ReflectionHelpers.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/ReflectionHelpers.cs#L6-L31) - uncached field read used for the processors.

## Variations and gotchas

- **Two failure modes.** A renamed clip or sampler field shows up in `UnresolvedFields` and the red warning. A renamed processor field goes through the uncached `ReflectionHelpers` and silently yields null, so the matching UI rows just read "Not present on this character". If you copy the pattern, route the processors through the same cache and unresolved list.
- **Seated entries never appear for EVA kittens** (see the background above): the catalog code adds them but the null filter drops them.
- **Samplers are shared live objects.** Forcing `_walkPairSampler` plays it with whatever `Weight` the game wrote this frame (for example the moon walk weight from gravity), and playing a sampler does not freeze its blend.
- **Overlay clips are single poses.** Blink and the ear/helmet mask play on the body but are not meant to.
- **Do not build on `WalkingAnimations`.** It is a vestigial duplicate; run is missing.
- **Names that were stable.** The 17 animation and sampler fields, the four processor fields and `_characterAvatar` were unchanged across the builds checked (5402 to 5554) according to the mod's own notes. `AnimationAssetRef.Id` is a `Core.AssetName`, so the library project needs a reference to the `Planet.Core` assembly.
- To play what you list, see [Force a kitten to play a chosen clip](../../animation/play-a-specific-kitten-animation-clip/overview.md); to drive the processors, [Make a kitten pull a facial expression](../../animation/make-a-kitten-pull-a-facial-expression/overview.md).

## See also

- [Reach a kitten's avatar, skeleton, bones and animation catalog](overview.md)
- [Approach 1: one reflection hop to the avatar](reach-a-kittens-avatar-and-skeleton-1.md), [approach 3: live material arrays](reach-a-kittens-avatar-and-skeleton-3.md), [approach 4: draw path and animation order](reach-a-kittens-avatar-and-skeleton-4.md)
- Mod feature: [Kitten animation clip catalog](../../../../mods/unscience/features/kitten-animations-clip-catalog.md)
- [Read and extend the character asset XML](../define-a-kitten-character-in-asset-xml/overview.md), [Contain exceptions in StarMap hooks](../../../getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md)
