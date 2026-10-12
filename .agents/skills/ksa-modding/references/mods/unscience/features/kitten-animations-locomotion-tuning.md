---
type: Mod Feature
title: Live animation tuning on KittenLocomotionTuning.Current with a scoped reset and a scene-save record
description: Kitten Animations edits the 20 animation-facing fields of the global KittenLocomotionTuning.Current in place, resets only those, shows live blend weights, and saves them with clip and expression state
mod: unscience
submod: kitten-animations
category: kittens
techniques: [imgui-window, save-sidecar]
hooks:
  - starmap StarMapAfterGui (TuningSection writes ref KittenLocomotionTuning.Current fields)
  - unscience save participant kitten-animations (default order 100) via the shared native save hooks
lifecycle: [frame:draw-ui, frame:render, frame:dispatch-solvers, event:save, event:load, frame:prepare]
game_types:
  - KSA.KittenLocomotionTuning
  - KSA.KittenLocomotion
  - KSA.KittenTuningWindow
  - KSA.KittenRenderable
  - KSA.KittenEva
  - KSA.Vehicle
  - KSA.VehicleUpdateState
  - KSA.PhysicsBubble
  - KSA.LocomotionState
  - KSA.Rendering.AnimationPairBlendSampler
tags: [kittens, animation, locomotion, tuning, imgui, scene-saves, global-state]
---

The "Locomotion Anim Tuning" section exposes the part of the game's kitten tuning that decides how
animations land: clip cross-fade time, the idle threshold, playback-rate clamps, the speeds the walk,
run, ladder and tumble clips were authored at, the low-gravity moonwalk blend and playback scale, the
swim blend, the jump-land timing and the ladder eye pitch. Edits are live and global (every kitten,
not only the target). "Reset Animation Tuning" restores just these fields, leaving the physics
tuning alone, and a readout shows the current pair-blend weights next to the moonwalk and swim
weights the game would derive from the current gravity and speed.[^ka-readme][^ka-tuning-section]

Mechanism in one line: `KittenLocomotionTuning.Current` is a public static struct field, so ImGui
`DragFloat` can take `ref KittenLocomotionTuning.Current.Field` exactly as the game's own Debug >
Kitten Tuning window does; the same 20 fields, together with the panel's clip, expression and knob
state, form the `kitten-animations` scene-save record.[^ka-tuning-section][^ksa-tuning-window]

## How it works

**The 20 fields** (grouped as in the UI): blending and rate (`AnimBlendTime`,
`IdleSpeedThreshold`, `PlaybackRateMin`, `PlaybackRateMax`); nominal clip speeds
(`WalkClipNominalSpeed`, `RunClipNominalSpeed`, `LadderNominalSpeed`, `TumbleNominalSpeed`); low
gravity (`MoonwalkWalkNominalSpeed`, `MoonwalkRunNominalSpeed`, `MoonwalkStartGravity`,
`MoonwalkFullGravity`, `MoonwalkPlaybackScale`); swimming (`NominalSwimAnimSpeed`,
`SwimBlendFullSpeed`, `SwimBlendHalfLife`, `SwimEyePitchFactor`); jump and ladder
(`JumpLandDuration`, `JumpLandBounceIgnoreTime`, `LadderEyePitchDeg`).[^ka-saved-tuning][^ksa-tuning]

**Editing** ([kitten-animations.lib/Ui/TuningSection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/Ui/TuningSection.cs#L38-L116)).
A small `FieldGrid` helper lays out two label and `DragFloat` pairs per table row with a tooltip
per field, passing `ref KittenLocomotionTuning.Current.X` straight to ImGui. Ranges are drag limits,
not clamps.[^ka-tuning-section]

**Scoped reset** ([kitten-animations.lib/Ui/TuningSection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/Ui/TuningSection.cs#L135-L160)).
Copies the 20 fields one by one from the `KittenLocomotionTuning.Default` property (a fresh struct)
into `ref KittenLocomotionTuning.Current`. The game's own "Reset to Defaults" replaces the whole
struct.[^ksa-tuning-window]

**Live readout.** `AnimationPairBlendSampler.Weight` of the walk, run and swim samplers from the
[clip catalog](./kitten-animations-clip-catalog.md), plus
`KittenLocomotion.ComputeMoonwalkWeight(state.GravityMagnitude, in tuning)` and
`ResolveSwimBlend(state.GroundSpeed, in tuning)` evaluated with the current values.[^ksa-locomotion]

**Scene-save record** ([kitten-animations.lib/KittenAnimationsSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationsSubmod.Persistence.cs)).
Participant `kitten-animations` at the default restore order. `SavedAnimations` holds:[^ka-persistence][^save-participant]

| Group | Fields |
|---|---|
| Target | `SelectedKittenId` (null = follow controlled) |
| Clip | `ClipLabel`, `ClipSource` (asset id or sampler description), `ClipActive`, `ClipPaused`, `ClipPhase` (clock seconds, see [forced clip playback](./kitten-animations-forced-clip-playback.md)) |
| Expressions | `LatchedExpression`, `LatchedVariant`, `LatchedClipId`, `ExpressionVariant` selector, ease-in, hold, ease-out, peak weight, latch flag |
| Knobs | blend time, rate multiplier, the five strength overrides with their values |
| Tuning | `SavedAnimationTuning` (the 20 fields) |

- **Validate** rejects unknown enum values, negative durations and rates, weights outside 0 to 1,
  angles outside their ranges, an active clip without source and label, and `PlaybackRateMin >
  PlaybackRateMax`, before the world is reset.
- **Reset** unbinds (detaching the expression processor and restoring processor values), resets the
  driver, forgets the pinned target, reapplies the tuning captured in `Initialize` and the default
  settings.
- **Restore** applies tuning and settings, calls `Update(0)` so the natively rebuilt kitten binds at
  once, then resolves the clip by exact (source, label) match and calls `RestorePlayback`, and
  re-triggers a latched expression by its clip id at full weight (`Update(EaseInDuration)`). An
  ambiguous or missing clip, a missing variant or an unavailable pinned target becomes a warning; the
  target is never redirected. Unlatched one-shot expressions are not resumed, and an in-progress
  cross-fade resumes as the selected clip pose.[^ka-persistence][^ka-lib-readme]

## Lifecycle and timing

| Step | Where | Phase |
|---|---|---|
| Field edits, reset | `TuningSection.Render` from `[StarMapAfterGui]` | `frame:draw-ui` |
| Render-side reads | `KittenRenderable.UpdateLocomotionAnimationState` copies `Current` at L434 for each kitten render | `frame:render`, same frame |
| Worker-side copy | `KittenEva.PrepareWorker` passes `in KittenLocomotionTuning.Current` (L332) to `VehicleUpdateState.PrepareCharacterFromVehicle`, which stores a copy | `frame:dispatch-solvers`, next step |
| Worker use | `PhysicsBubble` calls `KittenLocomotion.Advance(..., in vehicleState.LocomotionTuning, ...)` (L1976) | `physics:vehicle-solver` |
| Capture | Native save capture | `event:save` |
| Reset, restore | Native load replayed at the `PrepareFrame` handoff | `event:load`, `frame:prepare` |

Because the worker gets its own copy when the step is prepared, UI edits during `frame:draw-ui` never
race the physics thread; they reach physics at the next `ExecuteNextVehicleSolvers` (`Program.cs`
L2220). The render path reads the static every frame, so animation changes are visible
immediately.[^ksa-kitteneva][^ksa-vehicleupdatestate][^ksa-physicsbubble][^ksa-program]

## Key code

| Path | Role |
|---|---|
| [kitten-animations.lib/Ui/TuningSection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/Ui/TuningSection.cs) | Grouped `ref` drag fields, scoped reset, live and derived weights |
| [kitten-animations.lib/SavedAnimationTuning.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/SavedAnimationTuning.cs) | Detached DTO: `Capture`, `Apply`, `Validate` |
| [kitten-animations.lib/KittenAnimationsSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationsSubmod.Persistence.cs#L11-L131) | Participant, validate, capture, reset, restore |
| [kitten-animations.lib/KittenAnimationsSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationsSubmod.cs#L29-L33) | Captures the tuning baseline at initialisation |
| [kitten-animations.lib/KittenPlaybackPhase.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenPlaybackPhase.cs) | Clip clock for the saved phase |
| [ksa-abstractions.lib/Persistence/ISaveParticipant.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/ISaveParticipant.cs#L26-L39) | `SaveParticipant<T>` adapter (default order 100) |

## Snippets

The scoped reset:

```csharp
// kitten-animations.lib/Ui/TuningSection.cs L135-L160
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
        current.TumbleNominalSpeed = defaults.TumbleNominalSpeed;
        current.MoonwalkWalkNominalSpeed = defaults.MoonwalkWalkNominalSpeed;
        current.MoonwalkRunNominalSpeed = defaults.MoonwalkRunNominalSpeed;
        current.MoonwalkStartGravity = defaults.MoonwalkStartGravity;
        current.MoonwalkFullGravity = defaults.MoonwalkFullGravity;
        current.MoonwalkPlaybackScale = defaults.MoonwalkPlaybackScale;
        current.NominalSwimAnimSpeed = defaults.NominalSwimAnimSpeed;
        current.SwimBlendFullSpeed = defaults.SwimBlendFullSpeed;
        current.SwimBlendHalfLife = defaults.SwimBlendHalfLife;
        current.SwimEyePitchFactor = defaults.SwimEyePitchFactor;
        current.JumpLandDuration = defaults.JumpLandDuration;
        current.JumpLandBounceIgnoreTime = defaults.JumpLandBounceIgnoreTime;
        current.LadderEyePitchDeg = defaults.LadderEyePitchDeg;
    }
```

Restoring the record onto the natively rebuilt kitten:

```csharp
// kitten-animations.lib/KittenAnimationsSubmod.Persistence.cs L69-L102
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
        if (_selectedKittenId != null && _context == null)
            context.Warn($"Animation target '{_selectedKittenId}' is unavailable.");
        // Unlatched one-shot expressions stay stopped. Forced looping clips/frozen poses
        // and latched faces are durable scene setup, so they resume on the normal pose pass.
    }
```

The game's own tuning window edits the same static the same way (build 5554):

```csharp
// current/decomp/KSA/KittenTuningWindow.cs L22-L38
	public override void DrawContent(IViewport viewport)
	{
		if (ImGui.Button("Reset to Defaults"u8, (float2?)null))
		{
			KittenLocomotionTuning.Current = KittenLocomotionTuning.Default;
		}
		ImGui.SameLine();
		if (ImGui.Button("Copy as C#"u8, (float2?)null))
		{
			CopyAsCSharp(in KittenLocomotionTuning.Current);
		}
		ImGui.Separator();
		if (ImGui.CollapsingHeader("Grounded"u8, ImGuiTreeNodeFlags.DefaultOpen))
		{
			ImGui.DragFloat("Walk Speed"u8, ref KittenLocomotionTuning.Current.WalkSpeed, 0.05f, 0f, 10f);
			Tooltip("Target ground speed (m/s) while walking."u8);
			ImGui.DragFloat("Run Speed"u8, ref KittenLocomotionTuning.Current.RunSpeed, 0.05f, 0f, 15f);
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `KittenLocomotionTuning.Current` (static field), `Default` (property) and the 20 fields | [current/decomp/KSA/KittenLocomotionTuning.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotionTuning.cs#L5-L149) | Global tuning edited in place |
| `KittenTuningWindow.DrawContent` | [current/decomp/KSA/KittenTuningWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenTuningWindow.cs#L22-L38) | The stock editor (menu bar Debug > Kitten Tuning) |
| `KittenLocomotion.ComputeMoonwalkWeight`, `ResolveSwimBlend` | [current/decomp/KSA/KittenLocomotion.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotion.cs#L24-L28) | Derived readout (`ResolveSwimBlend` at L476-L482) |
| `KittenLocomotion.DeriveMode`, `ResolveSwimTiltDeg` | [current/decomp/KSA/KittenLocomotion.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotion.cs#L144) | Worker-side readers of `IdleSpeedThreshold` (also L462-L469) |
| `KittenRenderable.UpdateLocomotionAnimationState` | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L432-L456) | Render-side reader |
| `KittenEva.PrepareWorker` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L319-L333) | Hands `Current` to the worker state |
| `VehicleUpdateState.PrepareCharacterFromVehicle`, `LocomotionTuning` | [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L308-L312) | Per-step copy |
| `PhysicsBubble` locomotion advance | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1976) | Worker reads the copy |
| `Program` Debug menu entry | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3851-L3860) | Where the stock window is opened |

## Pitfalls and version notes

- **Global, not per kitten.** Every kitten (and the stock tuning window) sees the edits; the panel's
  tooltip and README say so.[^ka-readme]
- **`IdleSpeedThreshold` is not purely visual.** In 5554 the vehicle worker also reads it in
  `KittenLocomotion.DeriveMode` (L144) to decide when a grounded kitten counts as idle, and in
  `ResolveSwimTiltDeg` (L462-L469). Changing it can change locomotion mode selection, not only which
  clip plays, despite the "physics fields are excluded" intent of `SavedAnimationTuning`.[^ksa-locomotion][^ka-saved-tuning]
- **Reset baselines differ.** The button resets to `Default`; a load without a record restores the
  values captured when the submod initialised (normally the same, unless another mod changed them
  earlier).[^ka-submod]
- **Documentation drift.** The mod README says "Nothing is persisted"; that is true for files on disk,
  but the scene-save participant does persist this record with each KSA save.[^ka-readme][^ka-lib-readme]
- **Field-by-field copies.** A new animation-facing field added to `KittenLocomotionTuning` will not
  be covered by reset, save or validation until it is added to all three lists.
- `KittenLocomotionTuning.Current` is mutable from any main-thread code; another mod (or the stock
  window) editing the same fields later wins.

## See also

- [Kitten Animations forced clip playback](./kitten-animations-forced-clip-playback.md) (clip phase in the record)
- [Kitten Animations facial expressions and strength knobs](./kitten-animations-facial-expressions.md) (latched expressions and knobs in the record)
- [Kitten Animations clip catalog](./kitten-animations-clip-catalog.md) (samplers in the readout)
- [Scene save coordinator](./lib-scene-save-coordinator.md) and [native save hooks](./lib-native-save-hooks.md)
- [Kitten Animations sub-mod](../submods/kitten-animations.md) and the [mod overview](../overview.md)
- [Save the forced clip with its label, source and phase, plus the walking tuning, and rebind them to the restored kitten](../../../howto/kittens/characters/restore-kitten-customizations-after-a-load/restore-kitten-customizations-after-a-load-3.md) - how-to article demonstrating this feature
- [Find the kitten branches in the worker and patch them from outside the solver](../../../howto/kittens/eva/kitten-locomotion-and-servo-reference/kitten-locomotion-and-servo-reference-1.md) - how-to article demonstrating this feature
- [Tune kitten locomotion and animation parameters live from an ImGui panel](../../../howto/kittens/eva/kitten-locomotion-and-servo-reference/kitten-locomotion-and-servo-reference-2.md) - how-to article demonstrating this feature
- [Persist feature state as ISaveParticipant records through a coordinator that retains what it cannot restore](../../../howto/persistence/saves/save-mod-state-with-the-scene/save-mod-state-with-the-scene-1.md) - how-to article demonstrating this feature

[^ka-tuning-section]: kitten-animations.lib/Ui/TuningSection.cs
[^ka-saved-tuning]: kitten-animations.lib/SavedAnimationTuning.cs
[^ka-persistence]: kitten-animations.lib/KittenAnimationsSubmod.Persistence.cs
[^ka-submod]: kitten-animations.lib/KittenAnimationsSubmod.cs
[^ka-readme]: kitten-animations/README.md
[^ka-lib-readme]: kitten-animations.lib/README.md
[^save-participant]: ksa-abstractions.lib/Persistence/ISaveParticipant.cs
[^ksa-tuning]: KSA.KittenLocomotionTuning (decompiled, build 2026.10.10.5554)
[^ksa-tuning-window]: KSA.KittenTuningWindow (decompiled, build 2026.10.10.5554)
[^ksa-locomotion]: KSA.KittenLocomotion (decompiled, build 2026.10.10.5554)
[^ksa-kitteneva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatestate]: KSA.VehicleUpdateState (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
