---
type: Mod Feature
title: Eased weld animation queues and TOML weld presets
description: Garry's Torch interpolates weld offset, rotation and XYZ scale with eased per-weld animation queues advanced at the physics handoff in player time, and stores reusable weld settings as named presets in a Tomlyn TOML file with legacy-key migration
mod: unscience
submod: garrys-torch
category: physics
techniques: [toml-config, harmony-transpiler, inter-mod-api]
hooks:
  - harmony-transpiler KSA.Program.PrepareFrame (shared PhysicsFrameHook.BeforePhysics, animation tick)
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent, preset UI)
lifecycle: [frame:prepare, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.UniverseTime]
tags: [animation, easing, queue, presets, toml, weld, persistence]
---

Two conveniences on top of [vehicle welding](./garrys-torch-vehicle-welding.md). Animations move a
weld smoothly to a new offset, rotation and size over a duration with linear or power-curve easing,
one after another per weld (an API feature other code drives through `AnimateWeld`). Presets save the
create-form values (offset, rotation, XYZ scale, rotation lock, collisions) under a name in a TOML file
in the user's KSA documents folder, so a favourite attachment can be reapplied to any pair of
vessels.[^garrys-torch-lib-readme]

Mechanism in one line: `WeldAnimationManager` keeps one active `WeldAnimation` and a FIFO queue per
`WeldEntry`, advanced by the weld update at the `PhysicsFrameHook` handoff with the player frame delta,
and `PresetManager` round-trips a `[presets.<name>]` table set through Tomlyn's model API.

## How it works

**Animation** ([garrys-torch.lib/WeldAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldAnimation.cs)):

- Constructed with start values (the weld's current `Position`, `Rotation`, `Scale`), target values,
  `DurationSeconds`, an `EasingType` and two exponents.
- `Update(weld, dt)` adds `dt` to `ElapsedSeconds`. On completion it snaps the weld to the exact
  targets; otherwise `t = EasingHelper.ApplyEasing(elapsed / duration, easing, powerStart, powerEnd)`
  and position, Euler rotation and scale are linearly interpolated per component.
- Scale changes go through `WeldEngine.ApplyVehicleScale`, so animated scaling uses the same
  captured-baseline path as UI edits and stays non-cumulative.
- Easing ([ksa-abstractions.lib/EasingHelper.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/EasingHelper.cs)): `Linear`, `EaseIn` (`t^p`), `EaseOut`
  (`1 - (1-t)^p`), `EaseInOut` (piecewise), `t` clamped to `[0, 1]`.

**Queue** ([garrys-torch.lib/WeldAnimationManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldAnimationManager.cs)): `Enqueue` starts the animation
if the weld has none active, otherwise appends it. `Update` iterates a copy of the active keys; when
one completes, `PromoteNext` dequeues the next and rebuilds it with start values re-captured from the
weld's current state, so a queued step never jumps back to stale start values recorded when it was
queued. `CancelAll` and `Clear` drop a weld's or all animations; every unweld path cancels them first.

**API** ([garrys-torch.lib/GarrysTorchSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/GarrysTorchSubmod.cs#L663-L697)): `AnimateWeld(sourceId,
targetPosition, targetRotation, targetScale, duration, easing, powerStart = 3, powerEnd = 3)` validates
the weld, a positive duration and a valid scale and returns an error string or null. A uniform `float`
overload exists for older callers. There is no UI for animations.

**Presets** ([garrys-torch.lib/PresetManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/PresetManager.cs)):

- File: `<MyDocuments>/My Games/Kitten Space Agency/.unscience/garrys-torch-presets.toml`
  (`KsaPaths.UserDataDir`), created on `Initialize`.
- Load: `Toml.ToModel`, iterate the `presets` table; floats via `position_x..z`, `rotation_x..z`,
  `scale_x..z`; `lock_rotation` defaults to true and `collisions` to false when absent. A legacy scalar
  `scale` key becomes the default for each missing `scale_*` axis, so pre-XYZ presets migrate without
  editing the file.
- Save: rebuild a `TomlTable` from the in-memory dictionary and `File.WriteAllText(Toml.FromModel(root))`
  on every save or delete. Invalid names or scales are refused.
- UI: the create form's preset combo copies a preset into the pending fields; "Save settings as
  preset..." on an active weld and a delete confirmation use modal popups opened at the content-area ID
  scope (deferred `OpenPopup` flags). Names are cached, sorted case-insensitively.

## Lifecycle and timing

| Work | Where | Why there |
|---|---|---|
| `WeldAnimationManager.Update(dt)` | `GarrysTorchSubmod.UpdateBeforeVehicleSolvers`, called from `PhysicsFrameHook.BeforePhysics` inside `Program.PrepareFrame` (`frame:prepare`) | Runs right before the weld teleports in the same handoff, so the interpolated pose is used for this frame's teleport and the animated scale is in place before the next step is snapshotted |
| Disposed-weld cleanup | Same callback, before `Update` | Applying the previous step's results can destroy a vessel; animations must not write scale into disposed parts |
| `AnimateWeld` / preset load and save | Caller's thread; the UI runs in `[StarMapAfterGui]` (`frame:draw-ui`) | Only edits managed lists and a file; nothing touches game state until the next handoff |

`dt` is the player frame delta that `PrepareFrame` passes to `GetJobSimStep`, not simulated time, so
animations keep their wall-clock duration during time warp and keep running while the game is paused
(the weld teleport still runs every frame). The handoff fires with the HUD hidden, so animations do
not stall under F2.[^garrys-torch-lib-readme]

Scene saves store each weld's active and queued animation recipes with their elapsed time, and replay
restores them so playback continues where it stopped
([garrys-torch.lib/WeldAnimation.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldAnimation.Persistence.cs)).

## Key code

| Path | Role |
|---|---|
| [garrys-torch.lib/WeldAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldAnimation.cs) | One eased transition and its per-frame application |
| [garrys-torch.lib/WeldAnimationManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldAnimationManager.cs) | Active animation plus FIFO queue per weld, start re-capture on promotion |
| [garrys-torch.lib/GarrysTorchSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/GarrysTorchSubmod.cs#L74-L91) | Advances animations at the handoff before teleporting |
| [garrys-torch.lib/PresetManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/PresetManager.cs) | TOML presets with legacy `scale` migration |
| [garrys-torch.lib/WeldPreset.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldPreset.cs) | Preset record |
| [ksa-abstractions.lib/EasingHelper.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/EasingHelper.cs) | Shared easing curves |
| [garrys-torch.lib/WeldAnimation.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldAnimation.Persistence.cs) | Save and restore of animation recipes with elapsed progress |
| [garrys-torch.tests/PresetChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.tests/PresetChecks.cs) | TOML round-trip and default checks |
| [garrys-torch.tests/AnimationSaveChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.tests/AnimationSaveChecks.cs) | Save-restored playback equals uninterrupted playback |
| [garrys-torch.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/README.md) | Authors' notes: player-time pacing, saves, scale limits |

## Snippets

Advancing one animation and applying scale through the shared baseline path:

```csharp
// garrys-torch.lib/WeldAnimation.cs L62-L93
    public bool Update(WeldEntry weld, double dt)
    {
        ElapsedSeconds += dt;

        if (ElapsedSeconds >= DurationSeconds)
        {
            // Snap to exact target values on completion
            weld.Position = TargetPosition;
            weld.Rotation = TargetRotation;
            if (!WeldScale.Equals(weld.Scale, TargetScale))
            {
                weld.Scale = TargetScale;
                WeldEngine.ApplyVehicleScale(weld.Source, TargetScale);
            }
            return false;
        }

        double rawT = ElapsedSeconds / DurationSeconds;
        float t = (float)ApplyEasing(rawT, Easing, EasingPowerStart, EasingPowerEnd);

        weld.Position = Lerp(StartPosition, TargetPosition, t);
        weld.Rotation = Lerp(StartRotation, TargetRotation, t);

        float3 newScale = Lerp(StartScale, TargetScale, t);
        if (!WeldScale.Equals(weld.Scale, newScale))
        {
            weld.Scale = newScale;
            WeldEngine.ApplyVehicleScale(weld.Source, newScale);
        }

        return true;
    }
```

Promoting the next queued step from the weld's current values:

```csharp
// garrys-torch.lib/WeldAnimationManager.cs L79-L99
    private void PromoteNext(WeldEntry weld)
    {
        if (!_queues.TryGetValue(weld, out var queue) || queue.Count == 0)
        {
            _queues.Remove(weld);
            return;
        }

        var next = queue.Dequeue();
        if (queue.Count == 0)
            _queues.Remove(weld);

        // Recapture start state from current weld values
        var corrected = new WeldAnimation(
            weld.Position, weld.Rotation, weld.Scale,
            next.TargetPosition, next.TargetRotation, next.TargetScale,
            next.DurationSeconds, next.Easing,
            next.EasingPowerStart, next.EasingPowerEnd);

        _active[weld] = corrected;
    }
```

Legacy-tolerant preset loading:

```csharp
// garrys-torch.lib/PresetManager.cs L98-L118
            if (model.TryGetValue("presets", out var p) && p is TomlTable presetsTable)
            {
                foreach (var (name, value) in presetsTable)
                {
                    if (value is not TomlTable entry)
                        continue;

                    _presets[name] = new WeldPreset
                    {
                        Position = new float3(
                            GetFloat(entry, "position_x"),
                            GetFloat(entry, "position_y"),
                            GetFloat(entry, "position_z")),
                        Rotation = new float3(
                            GetFloat(entry, "rotation_x"),
                            GetFloat(entry, "rotation_y"),
                            GetFloat(entry, "rotation_z")),
                        Scale = ReadScale(entry),
                        Collisions = entry.TryGetValue("collisions", out var collisions) && collisions is true,
                        LockRotation = entry.TryGetValue("lock_rotation", out var lr) && lr is bool b ? b : true,
                    };
```

```csharp
// garrys-torch.lib/PresetManager.cs L172-L181
    private static float3 ReadScale(TomlTable table)
    {
        // Presets written before XYZ scaling used one scalar. Treat it as a uniform
        // value so existing user presets migrate without a manual edit.
        float legacyScale = GetFloat(table, "scale", 1f);
        return new float3(
            GetFloat(table, "scale_x", legacyScale),
            GetFloat(table, "scale_y", legacyScale),
            GetFloat(table, "scale_z", legacyScale));
    }
```

## Game APIs touched

No game members beyond those used by welding and scaling. The animation tick receives
`UniverseTime stateTime` and the player delta from the handoff; see
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2215-L2221) for where `dtPlayer` comes from.[^ksa-program]

## Pitfalls and version notes

- Rotation is interpolated per Euler component, not by quaternion slerp, so large multi-axis rotations
  do not follow the shortest arc.
- Animations are paced by player time: a 10 s animation takes 10 s at 1000x warp and also progresses
  while paused.
- `AnimateWeld` captures start values when called; queued steps are re-based on promotion, but the
  first step starts from whatever the weld held at call time, including a value a UI edit later changes.
- Presets are written synchronously on the main thread on each change; the file is rewritten whole and a
  failed write is only logged.
- `scale` (legacy) is read only as a fallback when `scale_x/y/z` are absent; new saves never write it.

## See also

- [Garry's Torch vehicle welding](./garrys-torch-vehicle-welding.md)
- [Per-axis part scaling](./garrys-torch-per-axis-part-scaling.md)
- [Zippo light animation queue](./zippo-light-animation-queue.md) (the same queue pattern for lights)
- [Camera keyframe sequences](./camera-controller-override-keyframe-sequences.md)
- [Shared file library](./lib-shared-file-library.md)
- [Garry's Torch sub-mod](../submods/garrys-torch.md) and [mod overview](../overview.md)
- [BeforePhysics callback at the PrepareFrame handoff](../../../howto/physics/kinematics/weld-one-vessel-to-another/weld-one-vessel-to-another-1.md) - how-to article demonstrating this feature
- [Per-weld eased animation queues in player time](../../../howto/physics/kinematics/weld-one-vessel-to-another/weld-one-vessel-to-another-3.md) - how-to article demonstrating this feature

[^garrys-torch-lib-readme]: garrys-torch.lib/README.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
