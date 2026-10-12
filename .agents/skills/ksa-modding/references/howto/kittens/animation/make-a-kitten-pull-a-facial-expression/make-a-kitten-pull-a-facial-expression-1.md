---
type: How-To
title: Own a CatExpressionAnim appended after the game's four, and bust its pose cache on every swap
description: Kitten Animations adds a CatExpressionAnim of its own to the model's AnimProcessors so it mixes last over every game pose, sets its ExpressionAnim and weight from an ease-in, hold and ease-out envelope, and nulls the private _expressionPose when the clip changes
category: kittens
group: animation
topic: make-a-kitten-pull-a-facial-expression
approach: 1
difficulty: intermediate
prerequisites: [starmap, reflection]
demonstrated_by: [mods/unscience/features/kitten-animations-facial-expressions]
techniques: [anim-processor, reflection]
hooks: [starmap StarMapBeforeGui, reflection KSA.CatExpressionAnim._expressionPose]
lifecycle: [frame:draw-ui, frame:render]
game_types:
  - KSA.CatExpressionAnim
  - KSA.CatPostAnim
  - KSA.CharacterAvatar
  - KSA.KittenRenderable
  - KSA.AnimatedRenderable
tags: [kitten, expression, anim-processor, envelope, latch, reflection]
---
After this article a mod can make a chosen kitten pull any of the five authored faces: angry, awe, happy, sad or scared, with a chosen variant, strength, ease-in, hold and ease-out, or held until cleared. The reason it takes a processor of its own: the game's reactive expression processor has its weight overwritten every frame, so a value written to it never shows. A processor you add last in the list is never rewritten, and its `MixPoseLocal` blends over all the game's. Prerequisites: StarMap, reflection for one private field, and the avatar ([reach the avatar](../../characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-1.md)).

## Background: how the game does it

- `CatExpressionAnim.UpdateLocalPose`: if `ExpressionAnim` is set and `ExpressionWeight > 0`, it samples the clip once into the private `_expressionPose` (`SampleStepped(1f, ...)`) and reuses that array while `CanCacheAnimation` is true (always, in `CatPostAnim`), then calls `MixPoseLocal(skeleton, localPose, pose, Expressions.ExpressionRootIndex, ExpressionWeight)`, a lerp of every bone in the subtree below the root ([current/decomp/KSA/CatExpressionAnim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatExpressionAnim.cs#L10-L28), [current/decomp/KSA/CatPostAnim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatPostAnim.cs#L24-L41)).
- The cache means swapping `ExpressionAnim` alone replays the first face sampled.
- The game's reactive processor `_catExpressionAnim` has its weight set each render: `ExpressionWeight = DampingExact(ExpressionWeight, accelTarget, 0.2, dt)` ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L342-L345)). The permanent personality face is `_catPersonalityExpressionAnim` at weight 1 (non-Neutral kittens only).
- `AnimatedRenderable.UpdateAnimation` runs the processors in list order ([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L160-L166)); an added processor goes last.
- `CatPostAnim.CharacterAvatar` is a `required` member, `Priority` is settable.

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Envelope advance (`Update(dt)`), attach on bind | `KittenAnimationsSubmod.Update` from `[StarMapBeforeGui]`, with the `dtPlayer` the UI pass receives | `frame:draw-ui` |
| Trigger, clear, knob edits | the panel from `[StarMapAfterGui]` | `frame:draw-ui` |
| The processor mixes the pose | `UpdateAnimation`, first call per frame | `frame:render` |

The envelope runs on player (wall-clock) time, so expressions keep playing while the simulation is paused. The processors run even when the body clip is frozen, because only `RuntimeAnim.Update` is gated by `FreezeAnimation`. The processor list is edited from the UI pass, outside the render ([game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)).

## Steps

1. **Resolve the private cache field once:**

```csharp
// kitten-animations.lib/KittenExpressionController.cs L27-L29
    private static readonly FieldInfo? ExpressionPoseField = typeof(CatExpressionAnim).GetField(
        "_expressionPose",
        BindingFlags.Instance | BindingFlags.NonPublic);
```

2. **Attach your own processor, once per model,** with no clip and zero weight, appended to the list so it runs after the game's four:

```csharp
// kitten-animations.lib/KittenExpressionController.cs L64-L95
    /// <summary>Installs the mod's expression processor on the avatar, replacing any earlier one.</summary>
    public void Attach(CharacterAvatar avatar)
    {
        var model = avatar.Core.CharacterModel;
        if (ReferenceEquals(model, _model) && _processor != null) return;

        Detach();

        _processor = new CatExpressionAnim
        {
            CharacterAvatar = avatar,
            ExpressionAnim = null,
            ExpressionWeight = 0f,
            Priority = 1f,
        };

        model.AnimProcessors.Add(_processor);
        _model = model;
    }

    /// <summary>Removes the mod's processor from the model it was installed on.</summary>
    public void Detach()
    {
        if (_model != null && _processor != null)
            _model.AnimProcessors.Remove(_processor);

        _processor = null;
        _model = null;
        Current = ExpressionType.None;
        CurrentVariant = string.Empty;
        _elapsed = 0f;
    }
```

3. **Trigger an expression.** Take the list from `CharacterAvatar.Expressions`, pick a random index or clamp the requested one, set the clip, **null the cache**, zero the weight and restart the clock:

```csharp
// kitten-animations.lib/KittenExpressionController.cs L97-L122
    /// <summary>Starts an expression. <paramref name="variantIndex"/> below zero picks a random variant.</summary>
    public void Trigger(CharacterAvatar avatar, ExpressionType type, int variantIndex, Random random)
    {
        Attach(avatar);
        if (_processor == null) return;

        var variants = GetVariants(avatar, type);
        if (variants == null || variants.Count == 0)
        {
            Console.WriteLine($"kitten-animations: No {type} expression clips on this character");
            return;
        }

        int index = variantIndex < 0 ? random.Next(variants.Count) : Math.Clamp(variantIndex, 0, variants.Count - 1);
        var animation = variants[index];
        if (animation == null) return;

        _processor.ExpressionAnim = animation;
        ClearPoseCache(_processor);
        _processor.ExpressionWeight = 0f;

        Current = type;
        CurrentVariantIndex = index;
        CurrentVariant = $"{type} {index + 1}/{variants.Count} ({animation.Id})";
        _elapsed = 0f;
    }
```

```csharp
// kitten-animations.lib/KittenExpressionController.cs L181-L194
    /// <summary>
    /// CatExpressionAnim samples its pose once and caches it in a private field. Swapping ExpressionAnim
    /// without busting that cache replays whichever expression was sampled first.
    /// </summary>
    private static void ClearPoseCache(CatExpressionAnim processor)
    {
        if (ExpressionPoseField == null)
        {
            Console.WriteLine("kitten-animations: CatExpressionAnim._expressionPose field not found");
            return;
        }

        ExpressionPoseField.SetValue(processor, null);
    }
```

4. **Drive the weight with an envelope** once per frame: quadratic ease-in to `PeakWeight`, hold (forever when latched), linear fade; an unlatched expression is cleared when its time is up:

```csharp
// kitten-animations.lib/KittenExpressionController.cs L136-L168
    /// <summary>Advances the expression envelope. Call once per frame.</summary>
    public void Update(double dt)
    {
        if (_processor == null || Current == ExpressionType.None) return;

        _elapsed += (float)dt;

        if (!Latch && _elapsed >= TotalDuration)
        {
            Clear();
            return;
        }

        _processor.ExpressionWeight = ComputeWeight();
    }

    private float ComputeWeight()
    {
        if (EaseInDuration > 0f && _elapsed < EaseInDuration)
        {
            float progress = _elapsed / EaseInDuration;
            return PeakWeight * progress * progress; // quadratic ease-in
        }

        if (Latch) return PeakWeight;

        float easeOutStart = EaseInDuration + HoldDuration;
        if (_elapsed <= easeOutStart || EaseOutDuration <= 0f)
            return PeakWeight;

        float fade = (_elapsed - easeOutStart) / EaseOutDuration;
        return PeakWeight * Math.Max(0f, 1f - fade);
    }
```

5. **Map the five types to the avatar's lists:**

```csharp
// kitten-animations.lib/KittenExpressionController.cs L170-L179
    /// <summary>Returns the authored clips for an expression, or null if the character has none.</summary>
    public static List<AnimationAssetRef>? GetVariants(CharacterAvatar avatar, ExpressionType type) => type switch
    {
        ExpressionType.Angry => avatar.Expressions.Angry,
        ExpressionType.Awe => avatar.Expressions.Awe,
        ExpressionType.Happy => avatar.Expressions.Happy,
        ExpressionType.Sad => avatar.Expressions.Sad,
        ExpressionType.Scared => avatar.Expressions.Scared,
        _ => null,
    };
```

6. **UI note.** The strength, hold and ease timers are `ImGui.DragFloat` widgets on purpose: drag widgets accept typed values past their nominal range (ctrl-click), so a two-minute hold is possible, while `SliderFloat` would clamp ([kitten-animations.lib/Ui/ExpressionSection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/Ui/ExpressionSection.cs#L140-L160)).

7. **Check the result.** Trigger Angry on the flown kitten: the face changes within the ease-in time; `Clear` returns it. With the old approach (writing the reactive processor) the personality face was the only visible one.

## Complete example

- [kitten-animations.lib/KittenExpressionController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenExpressionController.cs#L17-L195) - the controller.
- [kitten-animations.lib/Ui/ExpressionSection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/Ui/ExpressionSection.cs#L1-L190) - variant combo, one button per expression, latch, timers.
- Feature page: [Kitten animation facial expressions](../../../../mods/unscience/features/kitten-animations-facial-expressions.md).

## Variations and gotchas

- **Never write the game's reactive processor and expect it to hold.** It can only be capped after the game's write ([approach 2](make-a-kitten-pull-a-facial-expression-2.md)).
- **Bust `_expressionPose` whenever `ExpressionAnim` changes.** The field is private; if it is renamed the controller logs a message and a variant switch silently stops changing the pose.
- **Neutral kittens have no personality processor.** Personality is picked at random when the avatar is constructed, so the same character can have different moods across spawns and loads.
- **Unbind detaches the processor.** Switching target, boarding or a load clears the current expression; only latched expressions are restored from a scene save ([restore after a load](../../characters/restore-kitten-customizations-after-a-load/restore-kitten-customizations-after-a-load-3.md)).
- **List order is execution order.** Blending over the game's pose is unaffected by what the game writes to its own processor.
- **Seated kittens** (`IVASeat` renderables) are built separately; this controller targets EVA kittens.
- The authors list an on-screen confirmation pass of the 5348 rework as pending.

## See also

- [Make a kitten pull a facial expression](overview.md), [approach 2: weights and caps](make-a-kitten-pull-a-facial-expression-2.md)
- [Force a kitten to play a chosen clip](../play-a-specific-kitten-animation-clip/overview.md)
- [Reach a kitten's avatar, skeleton, bones and animation catalog](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md)
