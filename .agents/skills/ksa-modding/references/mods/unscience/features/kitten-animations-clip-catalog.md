---
type: Mod Feature
title: Catalog every animation clip, blend sampler and anim processor a live kitten has loaded
description: "Kitten Animations catalogs every playable clip of a live kitten: ground clips and blend samplers from private KittenRenderable fields, MMU and overlay clips from CharacterAvatar, and the four anim processors by name"
mod: unscience
submod: kitten-animations
category: kittens
techniques: [reflection, imgui-window]
hooks:
  - starmap StarMapBeforeGui (catalog and processors rebuilt on bind in KittenAnimationsSubmod.Update)
  - starmap StarMapAfterGui (Animations section buttons)
  - reflection KSA.KittenRenderable._groundIdleAnim ... _seatedIdleActionAnims (13 private clip fields, cached FieldInfo)
  - reflection KSA.KittenRenderable._walkPairSampler, _runPairSampler, _swimPairSampler, _blendSampler (private)
  - reflection KSA.KittenRenderable._catPersonalityExpressionAnim, _catExpressionAnim, _catEyeAnim, _catEarAnim (private)
  - reflection KSA.KittenRenderable._characterAvatar (private)
lifecycle: [frame:draw-ui]
game_types:
  - KSA.KittenRenderable
  - KSA.CharacterAvatar
  - KSA.AnimationAssetRef
  - KSA.IAnimation
  - KSA.Rendering.AnimationPairBlendSampler
  - KSA.Rendering.AnimationDirectionalBlendSampler
  - KSA.CatExpressionAnim
  - KSA.CatEyeAnim
  - KSA.CatEarAnim
  - KSA.BoneAnimRuntime
  - KSA.KittenEva
tags: [kittens, animation, reflection, catalog, blend-sampler, anim-processor, imgui]
---

Kitten Animations shows every animation a selected EVA kitten can play as buttons in four groups:
"Ground / EVA" (idle, walk, run, jump, jump land, tumble, ladder, moon walk, moon run, swim, swim
idle and, on seated renderables, seated idle and seated actions), "MMU" (idle default, idle actions,
six directional loops, arm retract), "Blends" (the live walk/moonwalk, run/moonrun, swim and MMU
directional samplers) and "Overlays" (blink, ear/helmet mask). The tooltip of each button shows the
asset id and clip length; the active one is highlighted. The same bind also produces typed handles
to the game's four anim processors, which the strength controls use.[^ka-readme][^ka-library-section]

Mechanism in one line: the ground set is not reachable through `CharacterAvatar.Animations`;
`KittenRenderable`'s constructor loads it from `CharacterGroundAnimationsReference` into private
fields, so the catalog reads those fields by name with cached `FieldInfo`s, records any name that no
longer resolves, and adds the public MMU and overlay clips from `CharacterAvatar`.[^ka-catalog][^ksa-kittenrenderable]

## How it works

1. **Build on bind** ([kitten-animations.lib/KittenAnimationsSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationsSubmod.cs#L102-L130)).
   When the target kitten or its avatar object changes, `Bind` calls
   `KittenAnimationCatalog.Build(avatar, renderable)` and `KittenAnimProcessors.Read(renderable)`
   and stores both in an `AnimationUiContext` used by every UI section. Unresolved field names are
   logged once.[^ka-submod][^ka-ui-context]
2. **Ground group.** Twelve `AnimationAssetRef?` fields (`_groundIdleAnim`, `_groundWalkAnim`,
   `_groundRunAnim`, `_jumpIntroAnim`, `_jumpLandAnim`, `_flailAnim`, `_ladderAnim`,
   `_moonWalkAnim`, `_moonRunAnim`, `_swimAnim`, `_swimIdleAnim`, `_seatedIdleAnim`) and the list
   `_seatedIdleActionAnims` are read from the `KittenRenderable` instance. A field that is missing
   from the type is added to `UnresolvedFields`; a field that exists but is null (the clip was not
   authored) is skipped silently.[^ka-catalog]
3. **MMU group.** Plain typed reads of `CharacterAvatar.Animations.MmuAnimations`: `MmuIdleDefaultAnim`,
   the `MmuIdleActionsAnim` list, the six `MmuMove*LoopAnim` fields and `MmuArmRetractAnim`.[^ksa-characteravatar]
4. **Blend group.** `_walkPairSampler`, `_runPairSampler`, `_swimPairSampler`
   (`AnimationPairBlendSampler`) and `_blendSampler` (`AnimationDirectionalBlendSampler`, seven
   candidates). Both types implement `IAnimation`, so they can be forced like any clip; their
   `Weight` is what the tuning readout shows.[^ksa-pairblendsampler][^ksa-directionalblendsampler]
5. **Overlay group.** `CharacterAvatar.Animations.BlinkAnim` and `HelmetMaskAnim`, single-pose clips
   the game normally mixes in through the eye and ear processors.
6. **Zero-length filter.** Any entry whose `LoopPeriod` is not positive is dropped:
   `BoneAnimRuntime.SampleCurrentAnimation` divides by it.[^ksa-boneanimruntime]
7. **Anim processors** ([kitten-animations.lib/KittenAnimProcessors.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimProcessors.cs)).
   `_catPersonalityExpressionAnim` and `_catExpressionAnim` are both `CatExpressionAnim`, so they are
   read by field name instead of `AnimProcessors.OfType<>()`: the first is the permanent mood face
   (absent for Neutral kittens), the second the acceleration-driven reactive face. `_catEyeAnim` and
   `_catEarAnim` complete the set.[^ka-processors][^ksa-kittenrenderable]
8. **UI** ([kitten-animations.lib/Ui/AnimationLibrarySection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/Ui/AnimationLibrarySection.cs)).
   One collapsing header per non-empty group, a three-column button table, `Driver.Play(entry)` on
   click (see [forced clip playback](./kitten-animations-forced-clip-playback.md)), and a red warning
   with the list of unresolved fields as tooltip, so a game update degrades visibly.[^ka-library-section]

What the catalog deliberately does not use: `CharacterAvatar.Animations.WalkingAnimations`. In 5554
`InitalizeFromCharacterRef` only assigns `WalkingAnim` (a duplicate of the ground walk clip) and never
`RunningAnim`, so the mod's old "Running" button was a no-op; run now comes from `_groundRunAnim`.[^ka-readme][^ksa-characteravatar]

## Lifecycle and timing

- Built in `KittenAnimationsSubmod.Update`, called from `[StarMapBeforeGui]` (or the hidden-HUD
  replay in unscience), only when the bound kitten or avatar changes: `frame:draw-ui`, main thread.
- Rendered from `RenderContent` (`[StarMapAfterGui]`), also `frame:draw-ui`.
- The objects it holds are the game's own: the same `AnimationAssetRef` and sampler instances the
  kitten's `KittenRenderable` uses. Forcing a sampler therefore plays it with whatever `Weight` the
  game wrote this frame (for example the moonwalk weight driven by gravity).
- The renderable is created with the `KittenEva` and lives as long as it, so rebinding on
  object-identity change is enough; there is no per-frame rescan.

## Key code

| Path | Role |
|---|---|
| [kitten-animations.lib/KittenAnimationCatalog.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationCatalog.cs#L54-L147) | Group builders |
| [kitten-animations.lib/KittenAnimationCatalog.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationCatalog.cs#L149-L230) | Field resolution, zero-length filter, unresolved list |
| [kitten-animations.lib/KittenAnimProcessors.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimProcessors.cs) | Typed processor handles |
| [kitten-animations.lib/Ui/AnimationLibrarySection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/Ui/AnimationLibrarySection.cs) | Buttons, highlight, unresolved warning |
| [kitten-animations.lib/Ui/AnimationUiContext.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/Ui/AnimationUiContext.cs) | Per-bind context shared by all sections |
| [ksa-abstractions.lib/ReflectionHelpers.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/ReflectionHelpers.cs#L12-L17) | Uncached field read used for the processors |

## Snippets

Groups are built from the renderable and the avatar:

```csharp
// kitten-animations.lib/KittenAnimationCatalog.cs L54-L88
    public static KittenAnimationCatalog Build(CharacterAvatar avatar, KittenRenderable renderable)
    {
        var catalog = new KittenAnimationCatalog();
        catalog.AddGroundGroup(renderable);
        catalog.AddMmuGroup(avatar);
        catalog.AddBlendGroup(renderable);
        catalog.AddOverlayGroup(avatar);
        return catalog;
    }

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

Missing fields are recorded, null clips and zero-length clips are skipped:

```csharp
// kitten-animations.lib/KittenAnimationCatalog.cs L149-L184
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

    private void AddClipList(AnimationGroup group, KittenRenderable renderable, string fieldName, string labelPrefix)
    {
        var field = ResolveField(fieldName);
        if (field == null)
        {
            UnresolvedFields.Add($"KittenRenderable.{fieldName}");
            return;
        }

        AddList(group, field.GetValue(renderable) as List<AnimationAssetRef>, labelPrefix);
    }

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

Processors by role, not by type:

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

Where the private fields come from (game, build 5554):

```csharp
// current/decomp/KSA/KittenRenderable.cs L140-L169
		CharacterReference characterReference = ModLibrary.Get<CharacterReference>(characterId);
		CharacterGroundAnimationsReference characterGroundAnimationsReference = characterReference.CharacterGroundAnimations?.Get();
		_groundIdleAnim = LoadGroundAnim(characterGroundAnimationsReference?.AnimIdle);
		_groundWalkAnim = LoadGroundAnim(characterGroundAnimationsReference?.AnimWalk);
		_groundRunAnim = LoadGroundAnim(characterGroundAnimationsReference?.AnimRun);
		_ladderAnim = LoadGroundAnim(characterGroundAnimationsReference?.AnimLadder);
		_jumpIntroAnim = LoadGroundAnim(characterGroundAnimationsReference?.AnimJump);
		_flailAnim = LoadGroundAnim(characterGroundAnimationsReference?.AnimTumble);
		_jumpLandAnim = LoadGroundAnim(characterGroundAnimationsReference?.AnimJumpLand);
		_moonWalkAnim = LoadGroundAnim(characterGroundAnimationsReference?.AnimMoonWalk);
		_moonRunAnim = LoadGroundAnim(characterGroundAnimationsReference?.AnimMoonRun);
		_swimAnim = LoadGroundAnim(characterGroundAnimationsReference?.AnimSwim);
		_swimIdleAnim = LoadGroundAnim(characterGroundAnimationsReference?.AnimSwimIdle);
		if (_swimIdleAnim != null && _swimAnim != null)
		{
			_swimPairSampler = new AnimationPairBlendSampler(_swimIdleAnim, _swimAnim);
		}
		if (_moonWalkAnim != null && _groundWalkAnim != null)
		{
			_walkPairSampler = new AnimationPairBlendSampler(_groundWalkAnim, _moonWalkAnim);
		}
		AnimationAssetRef animationAssetRef = _moonRunAnim ?? _moonWalkAnim;
		if (animationAssetRef != null && _groundRunAnim != null)
		{
			_runPairSampler = new AnimationPairBlendSampler(_groundRunAnim, animationAssetRef);
		}
		if (seated)
		{
			LoadSeatedAnims(characterGroundAnimationsReference);
		}
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `KittenRenderable` private clip, sampler and processor fields | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L13-L73) | Ground set, samplers, processors, `_characterAvatar` |
| `KittenRenderable(string, bool seated)` constructor, `LoadSeatedAnims` | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L136-L266) | How the fields are filled; processors added at L204-L237 |
| `CharacterAvatar.Animations` (`MmuAnimations`, `BlinkAnim`, `HelmetMaskAnim`, `WalkingAnimations`) | [current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L143-L186) | MMU and overlay groups |
| `AnimationAssetRef.Id`, `AnimLength`, `LoopPeriod` | [current/decomp/KSA/AnimationAssetRef.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimationAssetRef.cs#L8-L23) | Label source and filter |
| `IAnimation` (`AnimLength`, default `LoopPeriod`, `Sample`) | [current/decomp/KSA/IAnimation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IAnimation.cs#L6-L18) | Common type of clips and samplers |
| `AnimationPairBlendSampler.Weight` | [current/decomp/KSA.Rendering/AnimationPairBlendSampler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/AnimationPairBlendSampler.cs#L6-L28) | Playable pair blend and readout |
| `AnimationDirectionalBlendSampler` | [current/decomp/KSA.Rendering/AnimationDirectionalBlendSampler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/AnimationDirectionalBlendSampler.cs#L10) | MMU directional blend |
| `BoneAnimRuntime.SampleCurrentAnimation` | [current/decomp/KSA/BoneAnimRuntime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BoneAnimRuntime.cs#L100-L106) | Divides by `LoopPeriod` |
| `KittenEva.Renderable` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L59) | Public renderable access |
| `IVASeat` seated renderable | [current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L95) | The only `seated: true` construction |

## Pitfalls and version notes

- **Seated clips never appear for EVA kittens.** `LoadSeatedAnims` runs only for `seated: true`
  renderables, which only `IVASeat` builds; every `KittenEva` constructor builds a non-seated one. The
  "Seated Idle" and "Seated Action" entries are therefore always skipped on the kittens this panel can
  target.[^ksa-kittenrenderable][^ksa-ivaseat]
- **Renames degrade differently.** A renamed clip or sampler field shows in `UnresolvedFields` and as
  a red warning; a renamed processor field silently yields null through `ReflectionHelpers` and the
  matching strength row just becomes "Not present on this character".[^reflection-helpers]
- **Samplers are shared live objects.** Their `Weight` is rewritten by the game every frame; forcing a
  sampler does not freeze its blend.
- **Overlay clips on the body.** Blink and the ear/helmet mask are single-pose clips; playing them as
  the body clip works but is not how the game uses them.[^ka-catalog]
- The 5402 to 5482 checks found all 17 animation and sampler fields, the four processor fields and
  `_characterAvatar` unchanged; they are still present in 5554.[^scope-character]
- `AnimationAssetRef.Id` is a `Core.AssetName`, which needs a `Planet.Core` assembly reference in the
  library project.[^scope-character]

## See also

- [Kitten Animations forced clip playback](./kitten-animations-forced-clip-playback.md)
- [Kitten Animations facial expressions and strength knobs](./kitten-animations-facial-expressions.md) (uses the processor handles)
- [Kitten Animations locomotion tuning and saves](./kitten-animations-locomotion-tuning.md) (blend-weight readout)
- [Kitten Animations sub-mod](../submods/kitten-animations.md) and the [mod overview](../overview.md)
- [Compose a Character from the shared asset blocks](../../../howto/kittens/characters/define-a-kitten-character-in-asset-xml/define-a-kitten-character-in-asset-xml.md) - how-to article demonstrating this feature
- [Build a live clip catalog from cached FieldInfo reads with a visible unresolved list](../../../howto/kittens/characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-2.md) - how-to article demonstrating this feature

[^ka-catalog]: kitten-animations.lib/KittenAnimationCatalog.cs
[^ka-processors]: kitten-animations.lib/KittenAnimProcessors.cs
[^ka-library-section]: kitten-animations.lib/Ui/AnimationLibrarySection.cs
[^ka-submod]: kitten-animations.lib/KittenAnimationsSubmod.cs
[^ka-ui-context]: kitten-animations.lib/Ui/AnimationUiContext.cs
[^reflection-helpers]: ksa-abstractions.lib/ReflectionHelpers.cs
[^ka-readme]: kitten-animations/README.md
[^scope-character]: scope/character-and-materials.md
[^ksa-kittenrenderable]: KSA.KittenRenderable (decompiled, build 2026.10.10.5554)
[^ksa-characteravatar]: KSA.CharacterAvatar (decompiled, build 2026.10.10.5554)
[^ksa-pairblendsampler]: KSA.Rendering.AnimationPairBlendSampler (decompiled, build 2026.10.10.5554)
[^ksa-directionalblendsampler]: KSA.Rendering.AnimationDirectionalBlendSampler (decompiled, build 2026.10.10.5554)
[^ksa-boneanimruntime]: KSA.BoneAnimRuntime (decompiled, build 2026.10.10.5554)
[^ksa-ivaseat]: KSA.IVASeat (decompiled, build 2026.10.10.5554)
