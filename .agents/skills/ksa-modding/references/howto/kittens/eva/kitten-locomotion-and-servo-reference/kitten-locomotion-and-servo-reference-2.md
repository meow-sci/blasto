---
type: How-To
title: Tune kitten locomotion and animation parameters live from an ImGui panel
description: Kitten Animations passes ref KittenLocomotionTuning.Current.Field to ImGui.DragFloat for twenty animation fields in five groups, with tooltips, drag ranges that are not clamps, a reset that copies only those fields from Default, and a live readout of the blend weights
category: kittens
group: eva
topic: kitten-locomotion-and-servo-reference
approach: 2
difficulty: intermediate
prerequisites: [starmap, imgui]
demonstrated_by: [mods/unscience/features/kitten-animations-locomotion-tuning]
techniques: [imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:render]
game_types:
  - KSA.KittenLocomotionTuning
  - KSA.KittenLocomotion
  - KSA.KittenTuningWindow
  - KSA.KittenRenderable
  - KSA.Rendering.AnimationPairBlendSampler
  - KSA.KittenEva
tags: [kitten, locomotion, tuning, imgui, live-edit]
---
`KittenLocomotionTuning.Current` is a public static struct field, so an ImGui `DragFloat` can take `ref KittenLocomotionTuning.Current.Field` and edit the live value. That is exactly how the game's own **Debug > Kitten Tuning** window works. This article shows the mod version that exposes only the animation-facing slice (20 fields) with tooltips, a scoped reset, and a readout of the derived blend weights. Prerequisites: a StarMap mod with an ImGui panel (see [build a settings window](../../../ui/windows/build-a-settings-window/overview.md)). If you only need to explore all the numbers in game, the stock window already does it; write your own panel when you want a subset, persistence or a different reset.

## Background: how the game does it

- The game window: `KittenTuningWindow` is an `ImGuiWindow` with sections Grounded, Servos, Jump, Transitions, Mmu, Ladder, Animation, Seated (IVA) and Swimming, a "Reset to Defaults" button that assigns `KittenLocomotionTuning.Default` to `Current` (the whole struct) and a "Copy as C#" button. It is opened from the Debug menu ([current/decomp/KSA/KittenTuningWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenTuningWindow.cs#L16-L50), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3850-L3862)).
- `KittenLocomotionTuning` is a struct, so a field must be passed by reference (`ref KittenLocomotionTuning.Current.WalkClipNominalSpeed`) and copying `Current` to a local and editing the local does nothing ([current/decomp/KSA/KittenLocomotionTuning.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotionTuning.cs#L147-L149)).
- The animation reader `KittenRenderable.UpdateLocomotionAnimationState` copies `Current` once per render and divides ground speed by the nominal clip speeds, blends the moon walk by gravity (`ComputeMoonwalkWeight`) and the swim blend by speed (`ResolveSwimBlend`) ([current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L432-L457), [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L658-L687)).

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Field edits, reset | `TuningSection.Render` from `[StarMapAfterGui]` | `frame:draw-ui` |
| Renderer reads | `KittenRenderable.UpdateLocomotionAnimationState` copies `Current` for each kitten render | `frame:render`, the same frame |
| Worker copy | `KittenEva.PrepareWorker` stores a copy in the snapshot | `frame:dispatch-solvers`, next step |

Because the worker gets its own copy when the step is prepared, UI edits during `frame:draw-ui` never race the physics thread; physics fields reach the simulation at the next dispatch, and animation changes are visible immediately ([game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-physics-step)). The tuning is global: every kitten changes, not just the selected one.

## Steps

1. **Group the fields as the UI does:** blending and rate (`AnimBlendTime`, `IdleSpeedThreshold`, `PlaybackRateMin`, `PlaybackRateMax`); nominal clip speeds (`WalkClipNominalSpeed`, `RunClipNominalSpeed`, `LadderNominalSpeed`, `TumbleNominalSpeed`); low gravity (`MoonwalkWalkNominalSpeed`, `MoonwalkRunNominalSpeed`, `MoonwalkStartGravity`, `MoonwalkFullGravity`, `MoonwalkPlaybackScale`); swimming (`NominalSwimAnimSpeed`, `SwimBlendFullSpeed`, `SwimBlendHalfLife`, `SwimEyePitchFactor`); jump and ladder (`JumpLandDuration`, `JumpLandBounceIgnoreTime`, `LadderEyePitchDeg`).

2. **Pass the live field by reference.** A small helper lays out label and `DragFloat` pairs, with a tooltip each; the min and max are drag limits, not clamps (ctrl-click typing can exceed them):

```csharp
// kitten-animations.lib/Ui/TuningSection.cs L38-L52
    private static void RenderBlending()
    {
        ImGui.SeparatorText("Blending & Rate");

        var grid = new FieldGrid("##ka_tune_blend");
        grid.Field("Blend Time", "##ka_t_blend", ref KittenLocomotionTuning.Current.AnimBlendTime, 0.005f, 0f, 2f,
            "Cross-fade (s) the game uses whenever it switches clip.");
        grid.Field("Idle Threshold", "##ka_t_idle", ref KittenLocomotionTuning.Current.IdleSpeedThreshold, 0.005f, 0f, 2f,
            "Ground speed (m/s) below which the idle clip plays instead of walk/run.");
        grid.Field("Rate Min", "##ka_t_ratemin", ref KittenLocomotionTuning.Current.PlaybackRateMin, 0.01f, 0f, 4f,
            "Lower clamp on the speed-matched playback rate.");
        grid.Field("Rate Max", "##ka_t_ratemax", ref KittenLocomotionTuning.Current.PlaybackRateMax, 0.01f, 0f, 8f,
            "Upper clamp on the speed-matched playback rate.");
        grid.End();
    }
```

3. **Reset only your fields.** The game's reset replaces the whole struct, resetting physics tuning too. The mod's reset copies its 20 fields one by one from the `Default` property (a fresh struct) into `ref Current`, leaving physics alone:

```csharp
// kitten-animations.lib/Ui/TuningSection.cs L135-L146
    private static void ResetAnimationTuning()
    {
        var defaults = KittenLocomotionTuning.Default;
        ref var current = ref KittenLocomotionTuning.Current;
        current.AnimBlendTime = defaults.AnimBlendTime;
        current.IdleSpeedThreshold = defaults.IdleSpeedThreshold;
        current.PlaybackRateMin = defaults.PlaybackRateMin;
        current.PlaybackRateMax = defaults.PlaybackRateMax;
        current.WalkClipNominalSpeed = defaults.WalkClipNominalSpeed;
        current.RunClipNominalSpeed = defaults.RunClipNominalSpeed;
        current.LadderNominalSpeed = defaults.LadderNominalSpeed;
```

   (the remaining fields follow the same pattern, [kitten-animations.lib/Ui/TuningSection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/Ui/TuningSection.cs#L135-L162)).

4. **Show the derived values.** The live walk, run and swim blend weights come from the catalog's samplers ([clip catalog](../../characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-2.md)); the derived moonwalk weight and swim target are computed with the same public functions the game uses, on the current values:

```csharp
// kitten-animations.lib/Ui/TuningSection.cs L118-L131
    private static void RenderLiveBlendWeights(AnimationUiContext ctx)
    {
        var catalog = ctx.Catalog;
        var state = ctx.Kitten.LocomotionState;
        var tuning = KittenLocomotionTuning.Current;

        ImGui.TextDisabled(
            $"Live blends: walk {Weight(catalog.WalkPairSampler?.Weight)}   "
            + $"run {Weight(catalog.RunPairSampler?.Weight)}   "
            + $"swim {Weight(catalog.SwimPairSampler?.Weight)}");
        ImGui.TextDisabled(
            $"Derived: moonwalk {KittenLocomotion.ComputeMoonwalkWeight(state.GravityMagnitude, in tuning):F2}   "
            + $"swim target {KittenLocomotion.ResolveSwimBlend(state.GroundSpeed, in tuning):F2}");
    }
```

5. **Persist it** if the player will expect the tuning back after a load: the 20 fields form part of the scene-save record, and the baseline captured at start-up is what reset-on-load restores ([restore after a load, approach 3](../../characters/restore-kitten-customizations-after-a-load/restore-kitten-customizations-after-a-load-3.md)).

6. **Check the result.** Drag "Walk Clip" up: the walk plays slower for the same ground speed (playback rate is actual speed divided by the nominal speed); lower "Moon Walk" blend gravities to see the low-gravity gait come in at different gravities.

## Complete example

- [kitten-animations.lib/Ui/TuningSection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/Ui/TuningSection.cs#L12-L162) - the whole panel.
- [kitten-animations.lib/SavedAnimationTuning.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/SavedAnimationTuning.cs#L1-L90) - the detached record for saves.
- Feature page: [Kitten animation locomotion tuning](../../../../mods/unscience/features/kitten-animations-locomotion-tuning.md).

## Variations and gotchas

- **Ranges are drag limits.** `DragFloat` bounds do not clamp typed values; validate when you save ([approach 3 of the restore topic](../../characters/restore-kitten-customizations-after-a-load/restore-kitten-customizations-after-a-load-3.md)).
- **Global state.** Edits affect every kitten and survive loads until something writes them back.
- **A reset that copies only your fields** keeps the physics tuning a player may have set in the stock window.
- **Do not edit the physics fields from a worker or per frame.** The worker reads its copy at `PrepareWorker`; per-frame writes from the UI thread are fine, but writing from a physics-thread patch is a race.
- **Stock window duplicates this.** For one-off exploration prefer Debug > Kitten Tuning and its "Copy as C#" button.

## See also

- [Kitten locomotion, modes and the servo, from the decompiled game](overview.md), [approach 1: seams](kitten-locomotion-and-servo-reference-1.md)
- [Build a settings window](../../../ui/windows/build-a-settings-window/overview.md)
- [Restore kitten colours, clips and tuning after a save load](../../characters/restore-kitten-customizations-after-a-load/overview.md)
