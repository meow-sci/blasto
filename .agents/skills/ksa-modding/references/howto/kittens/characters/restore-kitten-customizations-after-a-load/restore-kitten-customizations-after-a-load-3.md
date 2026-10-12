---
type: How-To
title: Save the forced clip with its label, source and phase, plus the walking tuning, and rebind them to the restored kitten
description: Kitten Animations captures twenty tuning fields, the forced clip by exact source and label, its playback clock and any latched expression, validates them before the reset, then re-matches the clip after the load and samples the restored phase on the next render update
category: kittens
group: characters
topic: restore-kitten-customizations-after-a-load
approach: 3
difficulty: advanced
prerequisites: [starmap, reflection, scene-save-participant]
demonstrated_by:
  - mods/unscience/features/kitten-animations-locomotion-tuning
  - mods/unscience/features/kitten-animations-forced-clip-playback
techniques: [save-sidecar, reflection]
hooks:
  - save-participant kitten-animations
  - reflection KSA.BoneAnimRuntime.TimeSinceTransition
  - reflection KSA.AnimatedRenderable.RuntimeAnim
lifecycle: [event:save, event:load, frame:prepare, frame:render]
game_types: [KSA.KittenEva, KSA.KittenLocomotionTuning, KSA.BoneAnimRuntime, KSA.AnimatedRenderable]
tags: [kitten, save, load, animation, tuning, clip, phase]
---
A forced clip, a frozen pose, a latched facial expression and a retuned walk are all scene setup the player expects to find again after loading. Three different kinds of state are involved: global tuning that the load does not touch, a per-kitten clip binding that the load destroys, and a playback clock that lives in a protected field of a game class. This article shows how Kitten Animations saves all three, matches the clip again by exact identity, and applies the phase without waiting for the animation system to advance. Prerequisites: a save participant, reflection, and the forced-clip driver ([force a clip](../../animation/play-a-specific-kitten-animation-clip/overview.md)).

## Background: how the game does it

- `KittenLocomotionTuning` is a struct and `KittenLocomotionTuning.Current` a public static field initialised from `Default` ([current/decomp/KSA/KittenLocomotionTuning.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotionTuning.cs#L147-L149)). It is read on the main thread by `KittenRenderable` and `KittenEva.PrepareWorker`. A native load does not reset it, so changes persist in the process until something writes them back.
- The playing clip is `BoneAnimRuntime.CurrentAnimation` and its clock `BoneAnimRuntime.TimeSinceTransition`, both **protected**; `BoneAnimRuntime.SampleCurrentAnimation` is public and samples the current clip at `TimeSinceTransition` modulo `LoopPeriod` ([current/decomp/KSA/BoneAnimRuntime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BoneAnimRuntime.cs#L9-L40), [current/decomp/KSA/BoneAnimRuntime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BoneAnimRuntime.cs#L100-L108)). The runtime itself is the protected field `AnimatedRenderable.RuntimeAnim` ([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L34)).
- A paused (frozen) model skips `RuntimeAnim.Update` entirely (`if (!FreezeAnimation) RuntimeAnim.Update(dt)`), so after a restore the pose must be sampled explicitly or a frozen kitten would show the previous pose ([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L145-L153)).
- The load recreates the kitten and its `KittenRenderable`; the clip fields and `AnimationAssetRef`s are reloaded from the character data, so a clip is the same asset id but a different object ([clip catalog](../reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-2.md)).

## Lifecycle and timing

| Work | Where | Lifecycle |
|---|---|---|
| `CaptureAnimations` | when the native save is populated | `event:save` |
| `ValidateAnimations` | during load preparation, before anything is destroyed | `event:load` |
| `ResetAnimations` | the host's `Resetting` callback, before the world is destroyed | `event:load`, `frame:prepare` |
| `RestoreAnimations` | the host's `Restoring` callback, after the world is rebuilt | `event:load`, `frame:prepare` |
| Applying the saved phase | the forced-clip prefix on `AnimatedRenderable.UpdateAnimation` the next time the kitten is drawn | `frame:render` |

The restore itself rebinds data; the pose changes only when the kitten is next drawn, because kittens are animated inside the render ([draw path](../reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-4.md)). The comment at the end of `RestoreAnimations` records the policy: unlatched one-shot expressions stay stopped, forced looping clips, frozen poses and latched faces are durable scene setup.

## Steps

1. **Define a plain record.** All fields are primitives and ids; no references to game objects. The selected kitten is its vehicle id; the clip is its `Source` (the glTF asset id) and `Label`; the phase is a nullable float:

```csharp
// kitten-animations.lib/KittenAnimationsSubmod.Persistence.cs L29-L47
    private SavedAnimations CaptureAnimations() => new()
    {
        SelectedKittenId = _selectedKittenId, ExpressionVariant = _context?.ExpressionVariant ?? -1,
        ClipLabel = _driver.ForcedLabel,
        ClipActive = _driver.OverrideActive, ClipPaused = _driver.Paused,
        ClipPhase = KittenPlaybackPhase.Capture(_driver.TargetModel, _driver.ForcedAnimation),
        LatchedExpression = _expressions.Latch ? _expressions.Current : KittenExpressionController.ExpressionType.None,
        LatchedVariant = _expressions.CurrentVariantIndex,
        LatchedClipId = CaptureLatchedClipId(),
        ClipSource = _context?.Catalog.Groups.SelectMany(g => g.Entries)
            .FirstOrDefault(e => ReferenceEquals(e.Animation, _driver.ForcedAnimation) && e.Label == _driver.ForcedLabel)?.Source,
        Tuning = SavedAnimationTuning.Capture(),
        BlendTime = _driver.BlendTime,
        PlaybackRateScale = _driver.PlaybackRateScale,
        OverrideEarWeight = _driver.OverrideEarWeight,
        EarWeight = _driver.EarWeight,
        OverrideEyeLookAngle = _driver.OverrideEyeLookAngle,
        EyeLookAngleDeg = _driver.EyeLookAngleDeg,
        OverrideEyePitch = _driver.OverrideEyePitch,
```

2. **Read the clip clock with reflection, and only if it belongs to the forced clip.** The three protected members are resolved once; a missing one throws a clear error and `IsAvailable` gates restore. Capture compares the runtime's `CurrentAnimation` to the expected clip by reference so a clock from another clip is never saved:

```csharp
// kitten-animations.lib/KittenPlaybackPhase.cs L10-L22
    private static readonly FieldInfo? Runtime = typeof(AnimatedRenderable).GetField("RuntimeAnim", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? Clock = typeof(BoneAnimRuntime).GetField("TimeSinceTransition", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? Animation = typeof(BoneAnimRuntime).GetField("CurrentAnimation", BindingFlags.Instance | BindingFlags.NonPublic);
    public static bool IsAvailable => Runtime != null && Clock != null && Animation != null;

    public static float? Capture(AnimatedRenderable? model, IAnimation? expected)
    {
        if (model == null || expected == null) return null;
        if (!IsAvailable) throw new InvalidOperationException("Kitten animation playback fields are unavailable in this game build.");
        var runtime = Runtime?.GetValue(model) as BoneAnimRuntime;
        if (runtime == null || !ReferenceEquals(Animation?.GetValue(runtime), expected)) return null;
        return Clock?.GetValue(runtime) is float time && float.IsFinite(time) ? time : null;
    }
```

3. **Validate before the reset.** Reject negative durations, weights out of range, an unknown expression enum, a clip flagged active with no source or label, then the tuning record itself (non-negative rates and durations, `PlaybackRateMin <= PlaybackRateMax`, ladder eye angle within +-90):

```csharp
// kitten-animations.lib/KittenAnimationsSubmod.Persistence.cs L16-L27
    private static void ValidateAnimations(SavedAnimations saved)
    {
        if (!Enum.IsDefined(saved.LatchedExpression) || saved.ExpressionVariant < -1 || saved.LatchedVariant < -1
            || saved.ClipPhase < 0 || saved.BlendTime < 0 || saved.PlaybackRateScale < 0
            || saved.EaseInDuration < 0 || saved.HoldDuration < 0 || saved.EaseOutDuration < 0
            || saved.PeakWeight is < 0 or > 1 || saved.EarWeight is < 0 or > 1
            || saved.PersonalityWeight is < 0 or > 1 || saved.ReactiveExpressionMax is < 0 or > 1
            || saved.EyePitchDeg is < -90 or > 90 || saved.EyeLookAngleDeg is < 0 or > 90
            || (saved.ClipActive && (string.IsNullOrWhiteSpace(saved.ClipSource) || string.IsNullOrWhiteSpace(saved.ClipLabel))))
            throw new InvalidOperationException("Invalid saved kitten animation configuration.");
        saved.Tuning?.Validate();
    }
```

4. **Copy the tuning field by field, never the whole struct.** `Capture` copies twenty animation-facing fields from `KittenLocomotionTuning.Current`; `Apply` writes them back through `ref KittenLocomotionTuning.Current`. Physics-facing fields are deliberately left alone, so a restore (or the reset to the originals captured at start-up) cannot disturb the simulation:

```csharp
// kitten-animations.lib/SavedAnimationTuning.cs L69-L75
    public void Apply()
    {
        ref var target = ref KittenLocomotionTuning.Current;
        target.AnimBlendTime = AnimBlendTime;
        target.IdleSpeedThreshold = IdleSpeedThreshold;
        target.PlaybackRateMin = PlaybackRateMin;
        target.PlaybackRateMax = PlaybackRateMax;
```

   (See the [locomotion tuning feature](../../../../mods/unscience/features/kitten-animations-locomotion-tuning.md) for the field list and the live-tuning panel, and the [tuning article](../../eva/kitten-locomotion-and-servo-reference/overview.md) for what each field does.)

5. **Reset puts the process state back.** Reset unbinds the kitten, clears the forced clip, restores the tuning captured when the sub-mod started (global state survives the load) and resets the driver settings to defaults:

```csharp
// kitten-animations.lib/KittenAnimationsSubmod.Persistence.cs L60-L67
    private void ResetAnimations()
    {
        Unbind();
        _driver.Reset();
        _selectedKittenId = null;
        _originalTuning?.Apply();
        ApplyAnimationSettings(new SavedAnimations());
    }
```

6. **Restore: apply, rebind, match exactly, warn on ambiguity.** Apply tuning and settings, call `Update(0)` so the selected kitten (by id) is bound to the new objects, find the saved clip in the rebuilt catalog by exact source *and* label, and only restore when exactly one entry matches. Never redirect to a different target or clip:

```csharp
// kitten-animations.lib/KittenAnimationsSubmod.Persistence.cs L69-L84
    private void RestoreAnimations(SavedAnimations saved, SaveRestoreContext context)
    {
        context.Require(!saved.ClipPhase.HasValue || (saved.ClipPhase.Value >= 0 && KittenPlaybackPhase.IsAvailable),
            "Saved animation phase or native playback fields are unavailable.");
        _selectedKittenId = saved.SelectedKittenId;
        saved.Tuning?.Apply();
        ApplyAnimationSettings(saved);
        Update(0);
        if (_context != null) _context.ExpressionVariant = saved.ExpressionVariant;
        if (_context != null && saved.ClipSource != null)
        {
            var matches = _context.Catalog.Groups.SelectMany(g => g.Entries)
                .Where(e => e.Source == saved.ClipSource && e.Label == saved.ClipLabel).ToArray();
            if (matches.Length == 1) _driver.RestorePlayback(matches[0], saved.ClipActive, saved.ClipPaused, saved.ClipPhase);
            else context.Warn($"Saved animation clip unavailable or ambiguous: {saved.ClipLabel}.");
        }
```

7. **Re-trigger a latched expression by clip id, and warn if it is gone:**

```csharp
// kitten-animations.lib/KittenAnimationsSubmod.Persistence.cs L85-L97
        if (_context != null && saved.LatchedExpression != KittenExpressionController.ExpressionType.None && saved.Latch)
        {
            var variants = KittenExpressionController.GetVariants(_context.Avatar, saved.LatchedExpression);
            int variantIndex = saved.LatchedClipId == null ? -1
                : variants?.FindIndex(v => v != null && v.Id.ToString() == saved.LatchedClipId) ?? -1;
            if (variants == null || variantIndex < 0)
                context.Warn("Saved latched expression variant is unavailable.");
            else
            {
                _expressions.Trigger(_context.Avatar, saved.LatchedExpression, variantIndex, _random);
                _expressions.Update(_expressions.EaseInDuration);
            }
        }
```

8. **Apply the phase at the next render update, and sample it explicitly.** `RestorePlayback` stores the phase; when the forced-clip prefix next runs for the bound kitten it plays the clip with no blend, writes the clock and samples the pose, because a frozen model skips the update that would otherwise consume the clock:

```csharp
// kitten-animations.lib/KittenAnimationDriver.cs L247-L256
        if (_restartRequested)
        {
            model.PlayAnimation(ForcedAnimation, _savedPlaybackPhase.HasValue ? 0 : BlendTime);
            if (_savedPlaybackPhase.HasValue)
            {
                KittenPlaybackPhase.Restore(model, _savedPlaybackPhase.Value);
                _savedPlaybackPhase = null;
            }
            _restartRequested = false;
        }
```

```csharp
// kitten-animations.lib/KittenPlaybackPhase.cs L24-L33
    public static void Restore(AnimatedRenderable model, float time)
    {
        if (!float.IsFinite(time) || time < 0) throw new InvalidOperationException("Invalid saved kitten animation phase.");
        var runtime = Runtime?.GetValue(model) as BoneAnimRuntime;
        if (runtime == null || Clock == null) throw new InvalidOperationException("Kitten animation playback clock is unavailable in this game build.");
        Clock.SetValue(runtime, time);
        // FreezeAnimation can suppress Update entirely, so explicitly sample the restored
        // pose before the normal skinning pass consumes it. No GPU resources are allocated.
        runtime.SampleCurrentAnimation();
    }
```

9. **Check the result.** Save with a clip playing on a named kitten, reload: the clip resumes on that kitten (by vehicle id) at roughly the saved phase; if the kitten or clip is missing the sidecar reports `Saved animation clip unavailable or ambiguous` and nothing is redirected.

## Complete example

- [kitten-animations.lib/KittenAnimationsSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationsSubmod.Persistence.cs#L8-L164) - participant, validation, capture, reset, restore and the record class.
- [kitten-animations.lib/KittenPlaybackPhase.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenPlaybackPhase.cs#L8-L34) - protected clock read and write.
- [kitten-animations.lib/SavedAnimationTuning.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/SavedAnimationTuning.cs#L1-L60) - the tuning record, validation and capture.
- [kitten-animations.lib/KittenAnimationDriver.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationDriver.cs#L83-L90) - `RestorePlayback`.
- Feature pages: [locomotion tuning and saves](../../../../mods/unscience/features/kitten-animations-locomotion-tuning.md), [forced clip playback](../../../../mods/unscience/features/kitten-animations-forced-clip-playback.md).

## Variations and gotchas

- **Identity is source plus label, not index.** An index into the catalog changes when a mod adds a clip; an ambiguous match becomes a warning and the target is never redirected.
- **Protected fields are the fragile part.** `RuntimeAnim`, `TimeSinceTransition` and `CurrentAnimation` are read by name; `IsAvailable` makes the restore fail loudly (`context.Require`) rather than silently skipping the phase.
- **Clocks of other clips are never saved.** Capture checks the runtime is still playing the forced clip.
- **Global tuning survives the load.** That is why `Reset` re-applies the originals; without it one save's tuning leaks into the next load.
- **Unlatched one-shot expressions are not restored,** by design.
- **Warnings keep the record.** The coordinator retains a record that restored with warnings so a later build can retry it.

## See also

- [Restore kitten colours, clips and tuning after a save load](overview.md), [approach 1](restore-kitten-customizations-after-a-load-1.md), [approach 2](restore-kitten-customizations-after-a-load-2.md)
- [Force a kitten to play a chosen clip, freeze it or change its speed](../../animation/play-a-specific-kitten-animation-clip/overview.md), [Make a kitten pull a facial expression](../../animation/make-a-kitten-pull-a-facial-expression/overview.md)
- [Kitten locomotion, modes and the servo, from the decompiled game](../../eva/kitten-locomotion-and-servo-reference/overview.md)
