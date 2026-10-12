---
type: Mod Feature
title: Force any animation clip on an EVA kitten from a prefix on AnimatedRenderable.UpdateAnimation
description: Kitten Animations holds a chosen clip on an EVA kitten against the game's per-frame clip choice from a Harmony prefix on AnimatedRenderable.UpdateAnimation, with blend, rate, freeze, restart and saved phase
mod: unscience
submod: kitten-animations
category: kittens
techniques: [harmony-prefix, reflection, imgui-window]
hooks:
  - harmony-prefix KSA.AnimatedRenderable.UpdateAnimation(double) (__instance, ref double dt)
  - starmap StarMapBeforeGui (KittenAnimationsSubmod.Update resolves and binds the target)
  - starmap StarMapAfterGui (Target and Playback sections in ISubmod.RenderContent)
  - reflection KSA.KittenRenderable._characterAvatar (private)
  - reflection KSA.AnimatedRenderable.RuntimeAnim, KSA.BoneAnimRuntime.TimeSinceTransition and CurrentAnimation (protected; saved phase only)
lifecycle: [frame:draw-ui, frame:render]
game_types:
  - KSA.AnimatedRenderable
  - KSA.BoneAnimRuntime
  - KSA.KittenRenderable
  - KSA.KittenEva
  - KSA.CharacterAvatar
  - KSA.IAnimation
  - KSA.LocomotionState
  - KSA.Program
tags: [kittens, animation, harmony, prefix, skeletal-animation, eva, playback, imgui]
---

Kitten Animations lets you pick any clip the game has loaded for an EVA kitten (walk, run, swim,
ladder, MMU loops, blend samplers, even the single-pose overlays) and keep it playing whatever the
kitten is actually doing. A checkbox hands the body back to the game, Restart replays from the first
frame, Freeze holds the current pose, Blend Time sets the cross-fade and Playback Rate multiplies
animation time on top of the game's own rate. The target follows the controlled kitten or is pinned
to any live EVA kitten by vehicle id, without taking control of it.[^ka-readme]

Mechanism in one line: `KittenRenderable.UpdateRenderData` chooses a clip from the locomotion state
and then immediately calls `AnimatedRenderable.UpdateAnimation` on the body model, so a prefix on
`UpdateAnimation` that re-asserts the forced clip (`SetAnimation` is a no-op when it is already
current) runs after every game decision and before the pose is sampled.[^ka-patches][^ksa-kittenrenderable]

## How it works

1. **Target** ([kitten-animations.lib/KittenAnimationsSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationsSubmod.cs#L35-L155)).
   Every frame `Update` resolves the target: the controlled vehicle if it is a `KittenEva`, or the
   pinned id through `VehicleProvider.FindVehicle` (unambiguous match, not disposed, must be a
   `KittenEva`). It reads the public `KittenEva.Renderable` and the private
   `KittenRenderable._characterAvatar`. When the kitten or avatar object changes it rebinds: rebuilds
   the [clip catalog](./kitten-animations-clip-catalog.md), re-reads the anim processors, attaches the
   mod's [expression processor](./kitten-animations-facial-expressions.md), clears the clip and calls
   `KittenAnimationDriver.BindTarget(avatar.Core.CharacterModel, processors)`. A pinned target that
   boards or despawns stays selected and shows as unavailable; nothing silently retargets.[^ka-submod][^ka-avatar-accessor][^ka-target-section]
2. **Override state** ([kitten-animations.lib/KittenAnimationDriver.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationDriver.cs#L36-L107)).
   `Play(entry)` stores the clip and label, turns the override on and requests a restart; `Release`
   hands control back but keeps the selection; `ClearClip` forgets it. `BlendTime` defaults to 0.15 s
   and `PlaybackRateScale` to 1.[^ka-driver]
3. **The prefix** ([kitten-animations.lib/KittenAnimationPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationPatches.cs#L29-L59)).
   `UpdateAnimationPrefix(AnimatedRenderable __instance, ref double dt)` forwards to the static
   `Driver`. `ApplyBeforePose` returns at once unless `__instance` is the bound body model by
   reference, then re-applies processor knobs and the clip override inside a `try` that resets the
   driver on any exception.[^ka-patches][^ka-driver]
4. **Clip override.** On a restart request it calls `PlayAnimation(clip, BlendTime)` (blend 0 and a
   clock restore when resuming a saved phase); otherwise `SetAnimation(clip, BlendTime)`. Then
   `FreezeAnimation = Paused` and, when the multiplier is not 1, `dt *= PlaybackRateScale`. When the
   override is switched off, `FreezeAnimation` is cleared once and the game's own choice takes over on
   the next frame.[^ka-driver]
5. **UI** ([kitten-animations.lib/Ui/PlaybackSection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/Ui/PlaybackSection.cs)).
   A live readout (`LocomotionState.Mode`, `KittenEva.ControlMode`, ground speed, gravity, jump-chain
   stage and countdown, `AnimPlaybackRate`, personality), the override checkbox, Restart, Freeze,
   Clear, and drag fields for blend time (0 to 2 s) and rate (0 to 5x). Clips are chosen in the
   Animations section, which calls `Driver.Play`.[^ka-playback-section]
6. **Saved phase** ([kitten-animations.lib/KittenPlaybackPhase.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenPlaybackPhase.cs)).
   For scene saves only the clip clock is stored: `TimeSinceTransition` of the protected
   `RuntimeAnim`, and only when its `CurrentAnimation` is the forced clip. Restoring writes the clock
   back and calls `SampleCurrentAnimation()` explicitly, because a frozen model skips
   `RuntimeAnim.Update`. The save record itself is described in
   [locomotion tuning and saves](./kitten-animations-locomotion-tuning.md).[^ka-playback-phase]

## Lifecycle and timing

Where the game picks and samples the clip in build 5554:[^ksa-program][^ksa-kittenrenderable][^ksa-animatedrenderable]

| Step | Code | Phase |
|---|---|---|
| Target resolve and bind, expression envelope | `KittenAnimationsSubmod.Update` from `[StarMapBeforeGui]` | `frame:draw-ui` |
| UI edits to the driver | `RenderContent` from `[StarMapAfterGui]` | `frame:draw-ui` |
| Kitten render data per viewport | `Program.RenderGame` (L4646) and `RenderViewport` (L4468) call `KittenEva.UpdateRenderData` for kittens in `VehiclesInFrame` | `frame:render` |
| Game clip choice | `KittenRenderable.UpdateRenderData` (L311) -> `UpdateLocomotionAnimationState` (L432): `SetAnimation`/`PlayAnimation` per locomotion mode and `FreezeAnimation` writes (L440, L506, L654) | `frame:render` |
| **Mod prefix** | `CharacterModel.UpdateAnimation(dt2)` at L355, `dt2 = dt * simSpeed * achievedSpeedFraction * groundAnimPlaybackRate` | `frame:render` |
| Pose advance | `UpdateAnimation`: `RuntimeAnim.Update(dt)` only on the first call per `Program.FrameNumber` and only if not frozen (L147-L153), then anim processors | `frame:render` |

Consequences:

- The prefix runs once per visible viewport, but `KittenRenderable` zeroes `dt` after the first call
  in a frame (L313-L317) and `UpdateAnimation` advances the pose only on the first call, so the rate
  multiplier effectively applies once per frame. Secondary viewports render before the main one.
- Because `dt2` already includes simulation speed, a forced clip pauses with the game and speeds up
  with warp, and the multiplier stacks with the game's speed-matched rate (`AnimPlaybackRate`).
- Main-thread only; no worker thread touches `AnimatedRenderable`. The UI changes made in
  `frame:draw-ui` land in the same frame's render.
- Kittens outside `Program.VehiclesInFrame` are not updated at all, so a forced clip on an off-screen
  target simply does not advance.

## Key code

| Path | Role |
|---|---|
| [kitten-animations.lib/KittenAnimationPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationPatches.cs) | `Apply`/`Remove` and the prefix |
| [kitten-animations.lib/KittenAnimationDriver.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationDriver.cs#L110-L177) | Bind, unbind, reset, model filter |
| [kitten-animations.lib/KittenAnimationDriver.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationDriver.cs#L235-L268) | `ApplyClipOverride` |
| [kitten-animations.lib/KittenAnimationsSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationsSubmod.cs) | Target resolution, bind and unbind |
| [kitten-animations.lib/KittenAvatarAccessor.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAvatarAccessor.cs) | Live kittens, stable-id lookup, avatar reflection |
| [kitten-animations.lib/KittenPlaybackPhase.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenPlaybackPhase.cs) | Clip clock capture and restore |
| [kitten-animations.lib/Ui/PlaybackSection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/Ui/PlaybackSection.cs) | Override, restart, freeze, blend and rate controls |
| [kitten-animations.lib/Ui/TargetSection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/Ui/TargetSection.cs) | Follow-controlled or pinned target combo |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L94) | Supermod applies the prefix |
| [kitten-animations/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations/Patcher.cs#L15-L30) | Standalone host applies the same class |

## Snippets

The whole Harmony surface:

```csharp
// kitten-animations.lib/KittenAnimationPatches.cs L29-L59
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

    public static void Remove(Harmony harmony)
    {
        if (_updateAnimation != null && _prefix != null)
            harmony.Unpatch(_updateAnimation, _prefix);

        _updateAnimation = null;
        _prefix = null;
        Driver = null;

        Console.WriteLine("kitten-animations.lib: patches removed");
    }

    private static void UpdateAnimationPrefix(AnimatedRenderable __instance, ref double dt)
    {
        Driver?.ApplyBeforePose(__instance, ref dt);
    }
```

Re-asserting the clip, freezing and scaling time:

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

The patched method in build 5554: the pose advances once per frame, after the prefix:

```csharp
// current/decomp/KSA/AnimatedRenderable.cs L134-L153
	public void UpdateAnimation(double dt)
	{
		if (!ShouldUpdate)
		{
			return;
		}
		if (SkinningPoseIsViewportInvariant && _lastSkinningFrameNumber == Program.FrameNumber)
		{
			AnimationIndex = _cachedAnimationIndex;
			return;
		}
		int boneCount = Skeleton.BoneCount;
		Span<float4x4> span = MeshMainRenderer.AllocateSkinningMatrices(boneCount, out AnimationIndex);
		if (Program.FrameNumber != _lastPoseUpdateFrameNumber)
		{
			_lastPoseUpdateFrameNumber = Program.FrameNumber;
			if (!FreezeAnimation)
			{
				RuntimeAnim.Update((float)dt);
			}
```

Saving and restoring only the clip clock:

```csharp
// kitten-animations.lib/KittenPlaybackPhase.cs L8-L34
internal static class KittenPlaybackPhase
{
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
}
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `AnimatedRenderable.UpdateAnimation(double)` | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L134-L179) | Prefix target; single overload |
| `AnimatedRenderable.SetAnimation`, `PlayAnimation`, `FreezeAnimation`, `RuntimeAnim` | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L34-L132) | Forcing, freezing, phase access |
| `BoneAnimRuntime.SetAnimation`, `PlayAnimation`, `SampleCurrentAnimation`, `Update`, `TimeSinceTransition`, `CurrentAnimation` | [current/decomp/KSA/BoneAnimRuntime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BoneAnimRuntime.cs#L21-L121) | `SetAnimation` is a no-op for the current clip (L92-L98); loop-period wrap |
| `KittenRenderable.UpdateRenderData`, `UpdateLocomotionAnimationState` | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L311-L358) | The per-frame clip choice the prefix overrides |
| `KittenEva.Renderable`, `UpdateRenderData`, `LocomotionState`, `ControlMode`, `AnimPlaybackRate`, `AnimJumpChainStage` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L51-L67) | Target access and readout (`UpdateRenderData` at L1062-L1078) |
| `Program.RenderGame`, `RenderViewport` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4646-L4700) | Where kitten render data is updated per viewport |
| `ChuteRenderable` (`SkinningPoseIsViewportInvariant = true`) | [current/decomp/KSA/ChuteRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChuteRenderable.cs#L30) | The only user of the early-out, so kittens are unaffected |

## Pitfalls and version notes

- **Hot path.** The prefix runs for every animated renderable in every visible viewport: kitten
  bodies, MMU meshes (an `AnimatedRenderable` since 5269, updated at L395), seated crew, parachute
  canopies. Keep it a reference compare plus return, and never throw out of it.[^scope-character]
- **Writing from a StarMap callback does not work.** Grounded, airborne, tumbling, ladder and swim
  modes call `SetAnimation` unconditionally every render, so a clip set at `frame:draw-ui` is gone
  before it is sampled; this is why the hook is a prefix on the sampling call.[^ka-readme]
- **Single overload.** `AccessTools.Method` without a parameter list relies on
  `UpdateAnimation(double)` being the only overload; a rename throws `MissingMethodException` in
  `Apply`, which the supermod's `TryApply` logs and skips.[^unscience-patcher]
- **The game also writes `FreezeAnimation`** (tumbling without a flail clip, the end of a jump-land).
  The driver overrides it while active and clears it once on release; the game sets it again on its
  next frame if it wants it.
- **5402 skinning cache.** `SkinningPoseIsViewportInvariant` short-circuits `UpdateAnimation` after
  the first viewport, but only `ChuteRenderable` sets it in 5554.[^ksa-chuterenderable]
- **5348:** `KittenEva.Renderable` became public, replacing the `_renderable` reflection here;
  `_characterAvatar` is still private.[^scope-character]
- **One static driver.** `KittenAnimationPatches.Driver` is static; the README warns that with both
  the standalone mod and unscience installed, whichever initialises last owns it. Only unscience
  ships.[^ka-readme]
- Seated IVA crew are separate renderables built with `seated: true` and are not targetable.
- In-game acceptance of forced clips has been pending since the 5348 rework (static and build checks
  only).[^scope-character]

## See also

- [Kitten Animations clip catalog](./kitten-animations-clip-catalog.md) (what can be forced)
- [Kitten Animations facial expressions and strength knobs](./kitten-animations-facial-expressions.md) (the other half of the same prefix)
- [Kitten Animations locomotion tuning and saves](./kitten-animations-locomotion-tuning.md)
- [Iron Man kitten vehicle editing](./iron-man-kitten-vehicle-editing.md) (drives the avatar with `dt = 0`; this prefix still runs)
- [Consolidated patcher](./unscience-consolidated-patcher.md)
- [Kitten Animations sub-mod](../submods/kitten-animations.md) and the [mod overview](../overview.md)
- [Force a clip from a prefix on AnimatedRenderable.UpdateAnimation, with freeze, restart, blend time and rate](../../../howto/kittens/animation/play-a-specific-kitten-animation-clip/play-a-specific-kitten-animation-clip.md) - how-to article demonstrating this feature
- [Know where and when a kitten is animated and drawn, per viewport](../../../howto/kittens/characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-4.md) - how-to article demonstrating this feature
- [Save the forced clip with its label, source and phase, plus the walking tuning, and rebind them to the restored kitten](../../../howto/kittens/characters/restore-kitten-customizations-after-a-load/restore-kitten-customizations-after-a-load-3.md) - how-to article demonstrating this feature

[^ka-patches]: kitten-animations.lib/KittenAnimationPatches.cs
[^ka-driver]: kitten-animations.lib/KittenAnimationDriver.cs
[^ka-submod]: kitten-animations.lib/KittenAnimationsSubmod.cs
[^ka-avatar-accessor]: kitten-animations.lib/KittenAvatarAccessor.cs
[^ka-playback-phase]: kitten-animations.lib/KittenPlaybackPhase.cs
[^ka-playback-section]: kitten-animations.lib/Ui/PlaybackSection.cs
[^ka-target-section]: kitten-animations.lib/Ui/TargetSection.cs
[^ka-readme]: kitten-animations/README.md
[^unscience-patcher]: unscience/Patcher.cs
[^scope-character]: scope/character-and-materials.md
[^ksa-animatedrenderable]: KSA.AnimatedRenderable (decompiled, build 2026.10.10.5554)
[^ksa-kittenrenderable]: KSA.KittenRenderable (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-chuterenderable]: KSA.ChuteRenderable (decompiled, build 2026.10.10.5554)
