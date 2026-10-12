---
type: How-To
title: Re-apply eye, ear and personality weights each frame, cap the reactive face with Math.Min, and restore the originals on unbind
description: The same UpdateAnimation prefix that forces clips writes the ear weight, eye look angle, eye pitch offset and personality weight after the game's own writes, caps the acceleration-driven face with Math.Min and puts the bind-time values back when the kitten is released
category: kittens
group: animation
topic: make-a-kitten-pull-a-facial-expression
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony, reflection]
demonstrated_by: [mods/unscience/features/kitten-animations-facial-expressions]
techniques: [harmony-prefix]
hooks: [harmony-prefix KSA.AnimatedRenderable.UpdateAnimation, reflection KSA.KittenRenderable._catEyeAnim]
lifecycle: [frame:render, frame:draw-ui]
game_types: [KSA.CatEyeAnim, KSA.CatEarAnim, KSA.CatExpressionAnim, KSA.KittenRenderable, KSA.AnimatedRenderable]
tags: [kitten, expression, eyes, ears, personality, per-frame-write]
---
Faces are only part of what the processors expose. The ear processor has a weight, the eye processor a look-at cone and a pitch offset, the personality face a weight, and the reactive face is driven by acceleration. This article covers the second half of Kitten Animations' expression support: sliders for those knobs that survive the game's own per-frame writes, with the original values put back when the kitten is released. It reuses the `UpdateAnimation` prefix from [forced clip playback](../play-a-specific-kitten-animation-clip/play-a-specific-kitten-animation-clip.md). Prerequisites: that prefix, and typed handles to the four processors ([clip catalog](../../characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-2.md)).

## Background: how the game does it

| Knob | Processor field | What the game does | What the mod does |
|---|---|---|---|
| Ear motion | `CatEarAnim.ExpressionWeight` | Set once at construction: 0 seated, 1 otherwise | Hold a value; restore the bind-time value when turned off |
| Eye look angle | `CatEyeAnim.MaxLookAtAngle` | 30 at construction | Hold a value; restore when off |
| Eye pitch offset | `CatEyeAnim.LookPitchOffsetDeg` | Rewritten every frame (ladder angle, swim tilt, else 0) | Re-applied every frame while on |
| Personality face | personality `CatExpressionAnim.ExpressionWeight` | 1, only for non-Neutral kittens | Hold a value; restore when off |
| Reactive face cap | reactive `CatExpressionAnim.ExpressionWeight` | Damped toward an acceleration target every frame | `Math.Min(current, cap)` after the game's write |

([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L213-L237), [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L342-L346), [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L442-L457), [current/decomp/KSA/CatEarAnim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatEarAnim.cs#L8-L16), [current/decomp/KSA/CatEyeAnim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatEyeAnim.cs#L16-L26).)

The rule: a value the game sets **once** can be set once and restored; a value the game sets **every frame** must be re-applied after that write; and a value the game sets every frame to track a physical quantity (the reactive face) can only be capped, because setting it would fight the damping.

## Lifecycle and timing

The game writes these in `KittenRenderable.UpdateRenderData` (steps 3 and 4 of the [draw path](../../characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-4.md)), then calls `UpdateAnimation`; the prefix on `UpdateAnimation` therefore runs after the game's writes and before the processors read the fields (`frame:render`). UI edits to the knobs happen in the UI pass (`frame:draw-ui`) and take effect in the same frame's render.

## Steps

1. **Record the originals when binding to a kitten,** and restore them when it is released, so switching targets never leaves a kitten modified:

```csharp
// kitten-animations.lib/KittenAnimationDriver.cs L109-L139
    /// <summary>Binds the driver to one kitten and records the persistent values it may override.</summary>
    public void BindTarget(AnimatedRenderable model, KittenAnimProcessors processors)
    {
        if (ReferenceEquals(TargetModel, model) && ReferenceEquals(Processors, processors)) return;

        UnbindTarget();
        TargetModel = model;
        Processors = processors;
        _originalEarWeight = processors.Ear?.ExpressionWeight;
        _originalEyeLookAngle = processors.Eye?.MaxLookAtAngle;
        _originalPersonalityWeight = processors.Personality?.ExpressionWeight;
    }

    /// <summary>Releases the current kitten and restores persistent processor values owned by the mod.</summary>
    public void UnbindTarget()
    {
        RestorePersistentProcessorOverrides();

        if (_wasOverriding && TargetModel != null)
            TargetModel.FreezeAnimation = false;

        TargetModel = null;
        Processors = null;
        _originalEarWeight = null;
        _originalEyeLookAngle = null;
        _originalPersonalityWeight = null;
        _earOverrideApplied = false;
        _eyeLookOverrideApplied = false;
        _personalityOverrideApplied = false;
        _wasOverriding = false;
    }
```

2. **Hold the once-written values:** while the override is on, write it every frame; when it is switched off, write the original back once:

```csharp
// kitten-animations.lib/KittenAnimationDriver.cs L179-L196
    private void ApplyProcessorOverrides()
    {
        var processors = Processors;
        if (processors == null) return;

        if (processors.Ear != null)
        {
            if (OverrideEarWeight)
            {
                processors.Ear.ExpressionWeight = EarWeight;
                _earOverrideApplied = true;
            }
            else if (_earOverrideApplied && _originalEarWeight.HasValue)
            {
                processors.Ear.ExpressionWeight = _originalEarWeight.Value;
                _earOverrideApplied = false;
            }
        }
```

3. **Re-apply the every-frame value, and cap the reactive face.** The eye pitch offset is simply rewritten each frame while on; the reactive face is capped with `Math.Min` after the game's write:

```csharp
// kitten-animations.lib/KittenAnimationDriver.cs L211-L213, L230-L232
            // The game rewrites LookPitchOffsetDeg every frame (0 unless on a ladder or swimming).
            if (OverrideEyePitch)
                processors.Eye.LookPitchOffsetDeg = EyePitchDeg;
// ...
        // The reactive face is driven from vehicle acceleration; we can only cap it after the fact.
        if (LimitReactiveExpression && processors.Reactive != null)
            processors.Reactive.ExpressionWeight = Math.Min(processors.Reactive.ExpressionWeight, ReactiveExpressionMax);
```

4. **Get the processor handles by role** (personality and reactive are both `CatExpressionAnim`; see the clip catalog article), and let UI sliders edit the driver's fields; `ApplyBeforePose` does the rest each frame ([kitten-animations.lib/KittenAnimationDriver.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationDriver.cs#L162-L177)).

5. **Check the result.** Turn the ear weight to 0: the ears stop following the helmet-mask clip; turn it off: they return to the bind-time value. Cap the reactive face to 0 and hard-accelerate the kitten: the scared face no longer appears.

## Complete example

- [kitten-animations.lib/KittenAnimationDriver.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationDriver.cs#L16-L284) - all knobs, bind, unbind and the prefix target.
- [kitten-animations.lib/Ui/StrengthSection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/Ui/StrengthSection.cs#L1-L118) - checkbox plus slider per knob and the live weight readout.
- Feature page: [Kitten animation facial expressions](../../../../mods/unscience/features/kitten-animations-facial-expressions.md).

## Variations and gotchas

- **Cap, do not set, the reactive face.** It is rewritten every render, so the prefix can only reduce it.
- **Restore on unbind or the kitten keeps the override** (switching target, boarding, unload).
- **Seated ears start at 0.** The ear processor's weight is 0 for seated renderables; the bind-time value is what is restored, not 1.
- **Neutral kittens** have no personality processor; that knob is disabled for them.
- **Single point of failure.** The processor handles are read by private field name; a rename yields null handles and the rows read "Not present on this character" ([catalog gotchas](../../characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-2.md)).
- **One static driver:** with two copies of the mod, whichever initialises last owns it.

## See also

- [Make a kitten pull a facial expression](overview.md), [approach 1: own processor](make-a-kitten-pull-a-facial-expression-1.md)
- [Force a kitten to play a chosen clip, freeze it or change its speed, and hold values the game rewrites](../play-a-specific-kitten-animation-clip/overview.md)
