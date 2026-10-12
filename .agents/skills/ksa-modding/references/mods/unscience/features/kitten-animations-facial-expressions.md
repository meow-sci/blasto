---
type: Mod Feature
title: Facial expressions through a mod-owned CatExpressionAnim, plus anim-processor strength overrides
description: Kitten Animations plays facial expressions through its own CatExpressionAnim appended to AnimProcessors with an eased or latched envelope, and re-applies ear, eye and mood-face knobs from the pose prefix
mod: unscience
submod: kitten-animations
category: kittens
techniques: [harmony-prefix, reflection, imgui-window]
hooks:
  - harmony-prefix KSA.AnimatedRenderable.UpdateAnimation(double) (processor knobs re-applied in KittenAnimationDriver.ApplyBeforePose)
  - starmap StarMapBeforeGui (KittenExpressionController.Update advances the envelope)
  - starmap StarMapAfterGui (Expressions and Animation Strength sections)
  - reflection KSA.CatExpressionAnim._expressionPose (private, cached FieldInfo)
  - reflection KSA.KittenRenderable._catPersonalityExpressionAnim, _catExpressionAnim, _catEyeAnim, _catEarAnim (private)
lifecycle: [frame:draw-ui, frame:render]
game_types:
  - KSA.CatExpressionAnim
  - KSA.CatPostAnim
  - KSA.CatEyeAnim
  - KSA.CatEarAnim
  - KSA.AnimatedRenderable
  - KSA.KittenRenderable
  - KSA.CharacterAvatar
  - KSA.AnimationAssetRef
tags: [kittens, animation, expressions, anim-processor, harmony, reflection, imgui]
---

Kitten Animations can make the selected EVA kitten pull one of the five authored faces (Angry, Awe,
Happy, Sad, Scared), a specific variant or a random one, with a strength, a quadratic ease-in, a
hold and a linear ease-out, or latched until cleared. A second panel takes individual animation
processor values off the game: ear motion weight, eye look-at angle, eye pitch offset, the weight of
the permanent personality face, and a cap on the face the game pulls under acceleration.[^ka-readme]

Mechanism in one line: instead of writing to the game's expression processor, which
`KittenRenderable.UpdateRenderData` overwrites from vehicle acceleration every frame right before the
pose is sampled, the mod appends its own `CatExpressionAnim` to the body model's `AnimProcessors`,
where it runs last and keeps the weight the mod gives it.[^ka-expressions][^ksa-kittenrenderable]

## How it works

**Why the old approach showed "always the same face."** The previous controller found the
expression processor with `AnimProcessors.OfType<CatExpressionAnim>().LastOrDefault()`, which is the
reactive `_catExpressionAnim`. Its weight is reset every render by
`_catExpressionAnim.ExpressionWeight = AnimationUtils.DampingExact(..., accelTarget, 0.2f, dt)`
(L345) before `UpdateAnimation` (L355), so the mod's value never reached the pose and only the
permanent personality face (`_catPersonalityExpressionAnim`, weight 1) was visible.[^scope-character][^ksa-kittenrenderable]

**The mod-owned processor** ([kitten-animations.lib/KittenExpressionController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenExpressionController.cs)).

1. `Attach(avatar)` creates `new CatExpressionAnim { CharacterAvatar = avatar, ExpressionAnim = null,
   ExpressionWeight = 0, Priority = 1 }` (`CharacterAvatar` is a `required` member of `CatPostAnim`)
   and adds it to `avatar.Core.CharacterModel.AnimProcessors`. It is attached on bind and removed on
   unbind or target change.[^ksa-catpostanim]
2. `Trigger(avatar, type, variantIndex, random)` takes the list from `CharacterAvatar.Expressions`
   (`Angry`, `Awe`, `Happy`, `Sad`, `Scared`), picks a random index or clamps the requested one,
   sets `ExpressionAnim`, nulls the private `_expressionPose` cache, sets the weight to 0 and restarts
   the clock.[^ksa-characteravatar]
3. `Update(dt)` advances the clock and writes `ExpressionWeight = PeakWeight * t^2` during ease-in,
   `PeakWeight` during hold (or forever when latched), then a linear fade; a finished unlatched
   expression is cleared.
4. Why the cache bust: `CatExpressionAnim.UpdateLocalPose` samples `ExpressionAnim` once into
   `_expressionPose` and reuses it while `CanCacheAnimation` is true (always, in `CatPostAnim`), so
   swapping the clip without nulling the cache keeps replaying the first face.[^ksa-catexpressionanim]
5. Order matters: `AnimatedRenderable.UpdateAnimation` runs `AnimProcessors` in list order (L160-L166)
   and the mod's processor was added after the game's personality, reactive, eye and ear processors,
   so its `MixPoseLocal` blends over all of them.[^ksa-animatedrenderable]

**Strength knobs** ([kitten-animations.lib/KittenAnimationDriver.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationDriver.cs#L179-L233)).
They are applied from the same `UpdateAnimation` prefix as [forced clip playback](./kitten-animations-forced-clip-playback.md),
on the processors resolved by the [clip catalog](./kitten-animations-clip-catalog.md):[^ka-driver][^ka-processors]

| Knob | Processor field | Game behaviour | Mod behaviour |
|---|---|---|---|
| Ear motion | `CatEarAnim.ExpressionWeight` | Set once at construction: 0 seated, 1 otherwise | Hold a value; restore the bind-time value when turned off |
| Eye look angle | `CatEyeAnim.MaxLookAtAngle` | 30 at construction | Hold a value; restore when off |
| Eye pitch offset | `CatEyeAnim.LookPitchOffsetDeg` | Rewritten every frame (ladder angle, swim tilt, else 0) | Re-applied every frame while on |
| Personality face | personality `CatExpressionAnim.ExpressionWeight` | 1, only for non-Neutral kittens | Hold a value; restore when off |
| Reactive face cap | reactive `CatExpressionAnim.ExpressionWeight` | Damped toward an acceleration target every frame | `Math.Min(current, cap)` after the game's write |

`BindTarget` records the original ear, look-angle and personality values; `UnbindTarget` restores
them on the kitten being left, so switching targets never leaves a kitten modified.[^ka-driver]

**UI.** `ExpressionSection` offers a variant combo (Random or Variant N, clamped per expression), one
button per expression (disabled when the character has no clips for it), Clear, a latch checkbox and
`DragFloat` fields for strength, hold, ease-in and ease-out. The timers are drag widgets on purpose:
ctrl-click or double-click typing is not clamped, so a two-minute hold is possible, while
`SliderFloat` would clamp typed input. `StrengthSection` pairs a checkbox with a clamped slider per
knob and shows the live weights of ears, personality, reactive and mod expression.[^ka-expression-section][^ka-strength-section]

## Lifecycle and timing

| Step | Where | Phase |
|---|---|---|
| Envelope advance (`_expressions.Update(dt)`) | `KittenAnimationsSubmod.Update` from `[StarMapBeforeGui]` | `frame:draw-ui` |
| Trigger, clear, knob edits | `RenderContent` from `[StarMapAfterGui]` | `frame:draw-ui` |
| Game writes reactive weight (L345) and eye pitch (L456) | `KittenRenderable.UpdateRenderData` | `frame:render` |
| Knobs re-applied, reactive capped | `UpdateAnimation` prefix | `frame:render`, after the game's writes |
| Processors mix poses, mod processor last | `UpdateAnimation` body, first call per frame | `frame:render` |

The envelope is advanced with the `dt` that `OnDrawUiFrame` receives (`dtPlayer`, `Program.cs`
L2267), so expressions run on wall-clock time and keep playing while the simulation is paused.[^ksa-program] The
processors run even when the body clip is frozen, because only `RuntimeAnim.Update` is gated by
`FreezeAnimation` (L150-L153).[^ksa-animatedrenderable]

## Key code

| Path | Role |
|---|---|
| [kitten-animations.lib/KittenExpressionController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenExpressionController.cs#L65-L134) | Attach, detach, trigger, clear |
| [kitten-animations.lib/KittenExpressionController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenExpressionController.cs#L137-L194) | Envelope, variants, cache bust |
| [kitten-animations.lib/KittenAnimationDriver.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationDriver.cs#L110-L139) | Snapshot and restore of persistent processor values |
| [kitten-animations.lib/KittenAnimationDriver.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationDriver.cs#L179-L233) | `ApplyProcessorOverrides` |
| [kitten-animations.lib/Ui/ExpressionSection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/Ui/ExpressionSection.cs) | Variant, triggers, envelope fields, latch, status |
| [kitten-animations.lib/Ui/StrengthSection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/Ui/StrengthSection.cs) | Knob rows and live weights |
| [kitten-animations.lib/KittenAnimationsSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationsSubmod.cs#L35-L140) | Attach on bind, per-frame envelope, detach on unbind |

## Snippets

Appending the mod's own processor:

```csharp
// kitten-animations.lib/KittenExpressionController.cs L65-L82
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
```

The envelope:

```csharp
// kitten-animations.lib/KittenExpressionController.cs L137-L168
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

Knobs re-applied after the game's per-frame writes:

```csharp
// kitten-animations.lib/KittenAnimationDriver.cs L198-L232
        if (processors.Eye != null)
        {
            if (OverrideEyeLookAngle)
            {
                processors.Eye.MaxLookAtAngle = EyeLookAngleDeg;
                _eyeLookOverrideApplied = true;
            }
            else if (_eyeLookOverrideApplied && _originalEyeLookAngle.HasValue)
            {
                processors.Eye.MaxLookAtAngle = _originalEyeLookAngle.Value;
                _eyeLookOverrideApplied = false;
            }

            // The game rewrites LookPitchOffsetDeg every frame (0 unless on a ladder or swimming).
            if (OverrideEyePitch)
                processors.Eye.LookPitchOffsetDeg = EyePitchDeg;
        }

        if (processors.Personality != null)
        {
            if (OverridePersonalityWeight)
            {
                processors.Personality.ExpressionWeight = PersonalityWeight;
                _personalityOverrideApplied = true;
            }
            else if (_personalityOverrideApplied && _originalPersonalityWeight.HasValue)
            {
                processors.Personality.ExpressionWeight = _originalPersonalityWeight.Value;
                _personalityOverrideApplied = false;
            }
        }

        // The reactive face is driven from vehicle acceleration; we can only cap it after the fact.
        if (LimitReactiveExpression && processors.Reactive != null)
            processors.Reactive.ExpressionWeight = Math.Min(processors.Reactive.ExpressionWeight, ReactiveExpressionMax);
```

The game's own write that defeats a direct approach (build 5554):

```csharp
// current/decomp/KSA/KittenRenderable.cs L342-L355
		float num2 = float.Clamp((float)acceleration.Length() * 1000f, 0f, 1f);
		num2 += float.Clamp((float)angularAcceleration.Length() * 100f, 0f, 1f);
		num2 /= 2f;
		_catExpressionAnim.ExpressionWeight = AnimationUtils.DampingExact(_catExpressionAnim.ExpressionWeight, num2, 0.2f, (float)dt);
		float4x4 float4x5 = ModelToBodyMatrix();
		if (locomotionState.Mode == LocomotionMode.Ladder)
		{
			KittenLocomotionTuning current = KittenLocomotionTuning.Current;
			float x = float.Max(0f, current.LadderStandoff - current.LadderVisualStandoff);
			float4x5 *= float4x4.CreateTranslation(new float3(x, 0f, 0f));
		}
		_characterAvatar.Core.CharacterModel.Transform = float4x5 * transform;
		double dt2 = dt * Universe.GetSimulationSpeed() * Universe.GetAchivedSpeedFraction() * (double)_groundAnimPlaybackRate;
		_characterAvatar.Core.CharacterModel.UpdateAnimation(dt2);
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `CatExpressionAnim.ExpressionWeight`, `ExpressionAnim`, `_expressionPose`, `UpdateLocalPose` | [current/decomp/KSA/CatExpressionAnim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatExpressionAnim.cs#L8-L29) | The processor type the mod instantiates; cached pose |
| `CatPostAnim.CharacterAvatar` (required), `Priority`, `CanCacheAnimation` | [current/decomp/KSA/CatPostAnim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatPostAnim.cs#L8-L26) | Base class members |
| `CatEyeAnim.MaxLookAtAngle`, `LookPitchOffsetDeg` | [current/decomp/KSA/CatEyeAnim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatEyeAnim.cs#L22-L24) | Eye knobs |
| `CatEarAnim.ExpressionWeight` | [current/decomp/KSA/CatEarAnim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatEarAnim.cs#L13) | Ear knob |
| `AnimatedRenderable.AnimProcessors`, processor loop | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L147-L179) | List the mod appends to; in-order execution |
| `KittenRenderable` processor setup and per-frame writes | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L202-L237) | Personality, reactive, eye and ear processors; writes at L345 and L456 |
| `CharacterAvatar.Expressions`, `Personality` | [current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L188-L221) | Variant lists; personality decides whether a mood processor exists |

## Pitfalls and version notes

- **Never write the game's reactive processor and expect it to hold.** It can only be capped, and only
  from a point after `UpdateRenderData`'s write, such as the `UpdateAnimation` prefix.[^scope-character]
- **Bust `_expressionPose` whenever `ExpressionAnim` changes**, or the first sampled face sticks. The
  field is private; if it is renamed the controller logs a message and the variant switch silently
  stops changing the pose.[^ka-expressions]
- **Neutral kittens have no personality processor**, so that row is disabled for them. Personality
  itself is picked at random when the `CharacterAvatar` is constructed (`GetRandomEnumValue`), so the
  same character can have different moods across spawns and loads.[^ksa-characteravatar]
- **Unbind detaches the processor.** Switching target, the kitten boarding, or a load clears the
  current expression; only latched expressions are restored from a scene save (see
  [locomotion tuning and saves](./kitten-animations-locomotion-tuning.md)).
- The 5348 rework fixed the long-standing "kitten animations always the same" issue by this design; an
  on-screen confirmation pass was still listed as pending by the authors.[^scope-character]
- The 5278 per-frame pose gate (`_lastPoseUpdateFrameNumber`) is benign: the cache bust happens on
  trigger and the next frame re-samples.[^scope-character]

## See also

- [Kitten Animations forced clip playback](./kitten-animations-forced-clip-playback.md) (the shared prefix)
- [Kitten Animations clip catalog](./kitten-animations-clip-catalog.md) (processor handles)
- [Kitten Animations locomotion tuning and saves](./kitten-animations-locomotion-tuning.md)
- [Kitten Animations sub-mod](../submods/kitten-animations.md) and the [mod overview](../overview.md)
- [Own a CatExpressionAnim appended after the game's four, and bust its pose cache on every swap](../../../howto/kittens/animation/make-a-kitten-pull-a-facial-expression/make-a-kitten-pull-a-facial-expression-1.md) - how-to article demonstrating this feature
- [Re-apply eye, ear and personality weights each frame, cap the reactive face with Math.Min, and restore the originals on unbind](../../../howto/kittens/animation/make-a-kitten-pull-a-facial-expression/make-a-kitten-pull-a-facial-expression-2.md) - how-to article demonstrating this feature

[^ka-expressions]: kitten-animations.lib/KittenExpressionController.cs
[^ka-driver]: kitten-animations.lib/KittenAnimationDriver.cs
[^ka-expression-section]: kitten-animations.lib/Ui/ExpressionSection.cs
[^ka-strength-section]: kitten-animations.lib/Ui/StrengthSection.cs
[^ka-processors]: kitten-animations.lib/KittenAnimProcessors.cs
[^ka-readme]: kitten-animations/README.md
[^scope-character]: scope/character-and-materials.md
[^ksa-catexpressionanim]: KSA.CatExpressionAnim (decompiled, build 2026.10.10.5554)
[^ksa-catpostanim]: KSA.CatPostAnim (decompiled, build 2026.10.10.5554)
[^ksa-animatedrenderable]: KSA.AnimatedRenderable (decompiled, build 2026.10.10.5554)
[^ksa-kittenrenderable]: KSA.KittenRenderable (decompiled, build 2026.10.10.5554)
[^ksa-characteravatar]: KSA.CharacterAvatar (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
