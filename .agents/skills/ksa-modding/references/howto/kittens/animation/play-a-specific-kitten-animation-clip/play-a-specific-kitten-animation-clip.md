---
type: How-To
title: Force a clip from a prefix on AnimatedRenderable.UpdateAnimation, with freeze, restart, blend time and rate
description: Kitten Animations keeps a driver that a Harmony prefix on AnimatedRenderable.UpdateAnimation feeds, compares the model to the bound kitten by reference, re-applies the forced clip after the game's choice, and also holds processor weights the game rewrites
category: kittens
group: animation
topic: play-a-specific-kitten-animation-clip
difficulty: advanced
prerequisites: [starmap, harmony, reflection]
demonstrated_by: [mods/unscience/features/kitten-animations-forced-clip-playback]
techniques: [harmony-prefix, reflection]
hooks:
  - starmap StarMapBeforeGui
  - starmap StarMapAfterGui
  - harmony-prefix KSA.AnimatedRenderable.UpdateAnimation
lifecycle: [frame:draw-ui, frame:render]
game_types:
  - KSA.AnimatedRenderable
  - KSA.BoneAnimRuntime
  - KSA.KittenRenderable
  - KSA.KittenEva
  - KSA.IAnimation
  - KSA.CatEyeAnim
  - KSA.CatEarAnim
  - KSA.CatExpressionAnim
tags: [kitten, animation, clip, harmony-prefix, freeze, rate, per-frame-write]
---
After this article a kitten of your choice plays any loaded clip (or a blend sampler) whatever it is doing, can be frozen on a frame, restarted, cross-faded and sped up or slowed down, and a pinned target kitten can be forced without taking control of it. The same prefix also holds values that the game rewrites every frame. This is the Kitten Animations sub-mod of unscience. Prerequisites: StarMap, Harmony, and the clip catalog ([approach 2 of the avatar topic](../../characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-2.md)) for the `IAnimation` objects to force.

## Background: how the game does it

- `KittenRenderable.UpdateRenderData` is called for the kitten in every viewport it is drawn in. It calls `UpdateLocomotionAnimationState`, which for grounded, airborne, tumbling, ladder and swimming modes calls `SetAnimation` (or `PlayAnimation` for jump stages) **unconditionally every render**, and sometimes writes `FreezeAnimation`; then it calls `CharacterModel.UpdateAnimation(dt2)` ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L432-L535), [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L353-L355)).
- `SetAnimation(animation, blend)` is a no-op when the clip is already current (so calling it every frame is cheap and does not restart); `PlayAnimation` restarts. `AnimatedRenderable.FreezeAnimation` skips `RuntimeAnim.Update` entirely ([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L124-L152)).
- `dt` is zero after the first call in a frame (`Program.FrameNumber != _lastAnimAdvanceFrameNumber`), and `UpdateAnimation` advances the pose only on the first call per frame, so a rate multiplier applies once per frame in effect. Secondary viewports record before the main one, so the prefix runs for the camera windows first ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L311-L317), [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L145-L153)).
- `dt2` already includes simulation speed, so a forced clip pauses with the game and speeds up with warp, and a rate multiplier stacks on the game's speed-matched rate.
- Writing from `[StarMapBeforeGui]` or `[StarMapAfterGui]` does not work: the clip is gone before it is sampled.

## Lifecycle and timing

| Step | Code | Lifecycle |
|---|---|---|
| Resolve and bind the target kitten; expression envelope | `KittenAnimationsSubmod.Update` from `[StarMapBeforeGui]` | `frame:draw-ui` |
| UI edits to the driver (play, pause, rate) | `RenderContent` from `[StarMapAfterGui]` | `frame:draw-ui` |
| Game chooses the clip | `KittenRenderable.UpdateLocomotionAnimationState` | `frame:render` |
| **Prefix re-applies the forced clip** | `AnimatedRenderable.UpdateAnimation` prefix | `frame:render` |
| Pose advance | `RuntimeAnim.Update` once per frame if not frozen, then processors | `frame:render` |

The prefix runs for **every** `AnimatedRenderable` in every visible viewport (kitten bodies, MMU meshes, seated crew, parachute canopies), so it must be a reference compare and return. It runs on the main thread, inside the render ([game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence)). Kittens outside `Program.VehiclesInFrame` are not updated at all, so a forced clip on an off-screen target simply does not advance.

## Steps

1. **Patch the single overload and fail loudly when it moves.** `AccessTools.Method` without a parameter list relies on `UpdateAnimation(double)` being the only overload; the exception is caught by the host's per-feature isolation:

```csharp
// kitten-animations.lib/KittenAnimationPatches.cs L29-L42
    public static void Apply(Harmony harmony)
    {
        _updateAnimation = AccessTools.Method(typeof(AnimatedRenderable), nameof(AnimatedRenderable.UpdateAnimation));
        if (_updateAnimation == null)
            throw new MissingMethodException(typeof(AnimatedRenderable).FullName, nameof(AnimatedRenderable.UpdateAnimation));

        _prefix = AccessTools.Method(typeof(KittenAnimationPatches), nameof(UpdateAnimationPrefix));
        if (_prefix == null)
            throw new MissingMethodException(typeof(KittenAnimationPatches).FullName, nameof(UpdateAnimationPrefix));

        harmony.Patch(_updateAnimation, prefix: new HarmonyMethod(_prefix));

        Console.WriteLine("kitten-animations.lib: patches applied");
    }
```

2. **Keep the prefix trivial.** It takes `ref double dt` so the driver can scale it:

```csharp
// kitten-animations.lib/KittenAnimationPatches.cs L56-L59
    private static void UpdateAnimationPrefix(AnimatedRenderable __instance, ref double dt)
    {
        Driver?.ApplyBeforePose(__instance, ref dt);
    }
```

3. **Filter by reference to the bound kitten model and never throw.** Anything else returns immediately; an exception resets the driver so a fault cannot repeat every frame:

```csharp
// kitten-animations.lib/KittenAnimationDriver.cs L162-L177
    public void ApplyBeforePose(AnimatedRenderable model, ref double dt)
    {
        var target = TargetModel;
        if (target == null || !ReferenceEquals(model, target)) return;

        try
        {
            ApplyProcessorOverrides();
            ApplyClipOverride(model, ref dt);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"kitten-animations: Error applying animation override: {ex.Message}");
            Reset();
        }
    }
```

4. **Re-assert the clip, restart on request, freeze, scale the rate.** `PlayAnimation` on the first frame (or when restart is requested), `SetAnimation` on later frames (a no-op when already current), `FreezeAnimation = Paused`, and `dt *= PlaybackRateScale`. The same lines restore a saved phase (see [restore after a load](../../characters/restore-kitten-customizations-after-a-load/restore-kitten-customizations-after-a-load-3.md)):

```csharp
// kitten-animations.lib/KittenAnimationDriver.cs L235-L268
    private void ApplyClipOverride(AnimatedRenderable model, ref double dt)
    {
        if (!OverrideActive || ForcedAnimation == null)
        {
            if (_wasOverriding)
            {
                model.FreezeAnimation = false;
                _wasOverriding = false;
            }
            return;
        }

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
        else
        {
            // No-op when the clip is already current, so this is safe to call every frame.
            model.SetAnimation(ForcedAnimation, BlendTime);
        }

        model.FreezeAnimation = Paused;
        _wasOverriding = true;

        if (PlaybackRateScale != 1f)
            dt *= PlaybackRateScale;
    }
```

   The game also writes `FreezeAnimation` (tumbling without a flail clip, a stationary ladder climb, the end of a jump-land); the driver overrides it while active, clears it once on release, and the game sets it again on its next frame if it wants it.

5. **Choose the target without taking control.** An unselected target is the controlled kitten; a pinned target is found by vehicle id, so a camera-window kitten can be forced while the player flies another vessel:

```csharp
// kitten-animations.lib/KittenAnimationsSubmod.cs L142-L147
    private KittenEva? ResolveTargetKitten()
    {
        return _selectedKittenId == null
            ? KittenAvatarAccessor.GetControlledKitten()
            : KittenAvatarAccessor.FindKitten(_selectedKittenId);
    }
```

6. **Hold values the game rewrites, the same way.** The processors' weights are fields the game overwrites each render (`_catExpressionAnim.ExpressionWeight` is damped toward acceleration; `CatEyeAnim.LookPitchOffsetDeg` is rewritten every frame, 0 unless on a ladder or swimming), so the same prefix re-applies the override each frame, restores the bind-time original on unbind, and caps (rather than sets) the reactive face after the game's write:

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

   For a value that must be set (not capped) on the reactive face see [the expression topic](../make-a-kitten-pull-a-facial-expression/overview.md), which adds a processor that runs after the game's.

7. **Check the log.** `kitten-animations.lib: patches applied` on load; on any exception inside the prefix the driver logs `Error applying animation override` and resets itself. In game the chosen clip keeps playing while the kitten walks.

## Complete example

- [kitten-animations.lib/KittenAnimationPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationPatches.cs#L21-L60) - the prefix and its installation.
- [kitten-animations.lib/KittenAnimationDriver.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationDriver.cs#L16-L284) - the driver: clip override, processor overrides, phase restore.
- [kitten-animations/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations/Patcher.cs#L10-L50) - the Harmony instance and unload.
- Feature page: [Kitten animation forced clip playback](../../../../mods/unscience/features/kitten-animations-forced-clip-playback.md).

## Variations and gotchas

- **Hot path.** The prefix runs for every animated renderable; do a `ReferenceEquals` and return, nothing allocating.
- **A frozen model does not sample.** `FreezeAnimation` skips `RuntimeAnim.Update`, so writing a playback clock into a frozen model shows nothing until the pose is sampled; a restored phase therefore calls `BoneAnimRuntime.SampleCurrentAnimation` explicitly ([restore after a load, approach 3](../../characters/restore-kitten-customizations-after-a-load/restore-kitten-customizations-after-a-load-3.md)).
- **One static driver.** `KittenAnimationPatches.Driver` is static, so two copies of the mod fight over it; only unscience ships it.
- **Seated crew are not targetable.** They are separate renderables built with `seated: true`.
- **5402 skinning cache.** `SkinningPoseIsViewportInvariant` would short-circuit `UpdateAnimation` after the first viewport, but only parachutes set it in 5554.
- **5348:** `KittenEva.Renderable` became public, replacing a `_renderable` reflection; `_characterAvatar` is still private.
- **Build status.** The feature notes say in-game acceptance of forced clips has been pending since the 5348 rework (static and build checks only); treat it as verified by reading, not by play.
- **Iron Man editing.** The vehicle-editor feature draws the avatar with `dt = 0`; this prefix still runs.

## See also

- [Force a kitten to play a chosen clip, freeze it or change its speed, and hold values the game rewrites](overview.md)
- [Reach a kitten's avatar, skeleton, bones and animation catalog](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md)
- [Make a kitten pull a facial expression](../make-a-kitten-pull-a-facial-expression/overview.md), [Restore kitten colours, clips and tuning after a save load](../../characters/restore-kitten-customizations-after-a-load/overview.md)
- [Isolate patch failures and fault-proof patch bodies](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md)
