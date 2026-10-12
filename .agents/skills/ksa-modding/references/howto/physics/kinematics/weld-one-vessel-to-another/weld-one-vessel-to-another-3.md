---
type: How-To
title: Per-weld eased animation queues in player time
description: Animate a weld's offset, rotation and scale with per-weld FIFO queues advanced at the physics handoff on player time, and store reusable named weld presets in a TOML file with legacy-key migration
category: physics
group: kinematics
topic: weld-one-vessel-to-another
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/garrys-torch-weld-animation-and-presets]
techniques: [custom-physics, toml-config]
hooks: [harmony-transpiler KSA.Program.PrepareFrame, starmap StarMapAfterGui]
lifecycle: [frame:prepare, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.Universe]
tags: [physics, kinematics, weld, animation, easing, presets, toml, tomlyn, unscience]
---

Outcome: a weld that moves smoothly to a new offset, rotation and size over a duration with linear or power-curve easing, step
after step, plus named presets so a favourite attachment can be reapplied to any pair of vessels. Both are conveniences on
top of the [handoff weld](weld-one-vessel-to-another-1.md); animations are an API feature other code drives through
`AnimateWeld`, presets are a TOML file in the user's KSA documents folder. Prerequisites: StarMap, the handoff weld, and Tomlyn
for the file.

## Background: how the game does it

The game has nothing to animate here: a weld is a pose the mod recomputes every frame, so an animation is simply a function of
time that edits the weld's `Position`, `Rotation` and `Scale` before the teleport. The only engine fact is the clock: the player frame
delta that `PrepareFrame` passes to `GetJobSimStep` is wall-clock, not simulated time, so animations keep their duration under time warp
and keep running while the game is paused (the weld teleport still runs every frame).[^ksa-universe]

## Lifecycle and timing

| Work | Where | Why there |
|---|---|---|
| `WeldAnimationManager.Update(dt)` | `UpdateBeforeVehicleSolvers`, called from `BeforePhysics` inside `Program.PrepareFrame` (`frame:prepare`) | Runs right before the weld teleports in the same handoff, so the interpolated pose is used for this frame's teleport |
| Disposed-weld cleanup | Same callback, before `Update` | Applying the previous step's results can destroy a vessel; animations must not write scale into disposed parts |
| `AnimateWeld` / preset load and save | Caller's thread; the UI runs in `StarMapAfterGui` (`frame:draw-ui`) | Only edits managed lists and a file; nothing touches game state until the next handoff |

The handoff fires with the HUD hidden, so animations do not stall under F2. Scene saves store each weld's active and queued
animation recipes with their elapsed time, and replay restores them. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Model an animation as start values (the weld's current `Position`, `Rotation`, `Scale`), target values, a duration, an easing
   type (`Linear`, `EaseIn` = `t^p`, `EaseOut` = `1 - (1-t)^p`, `EaseInOut` piecewise) and two exponents. Advance it with the
   handoff `dt`; on completion snap to the exact targets; interpolate each component linearly otherwise. Scale changes go through the
   same captured-baseline path as UI edits so animated scaling stays non-cumulative:

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

2. Keep one active animation and a FIFO queue per weld. `Enqueue` starts it if none is active; when one completes, promote the next
   and rebuild it with start values re-captured from the weld's current state, so a queued step never jumps back to stale start values:

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

   Iterate a copy of the active keys; `CancelAll` and `Clear` drop a weld's or all animations; every unweld path cancels them first.
3. Expose it through the API only: `AnimateWeld(sourceId, targetPosition, targetRotation, targetScale, duration, easing, powerStart = 3,
   powerEnd = 3)` validates the weld, a positive duration and a valid scale and returns an error string or null.
4. Presets. Store the create-form values (offset, rotation, XYZ scale, rotation lock, collisions) under a name in
   `<MyDocuments>/My Games/Kitten Space Agency/.unscience/garrys-torch-presets.toml`. Load with Tomlyn's model API; `lock_rotation`
   defaults to true and `collisions` to false when absent:

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

5. Migrate legacy files without editing them: a scalar `scale` key becomes the default for each missing `scale_*` axis; new saves
   never write it:

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

6. Save by rebuilding a `TomlTable` from the in-memory dictionary and writing the file whole on every save or delete; refuse invalid
   names or scales. Cache sorted names for the UI.

## Complete example

| File | Role |
|---|---|
| [garrys-torch.lib/WeldAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldAnimation.cs) | One animation: values, easing, `Update` |
| [garrys-torch.lib/WeldAnimationManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldAnimationManager.cs) | Active and queued animations per weld |
| [garrys-torch.lib/PresetManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/PresetManager.cs) | TOML presets with legacy-key migration |
| [ksa-abstractions.lib/EasingHelper.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/EasingHelper.cs) | Easing functions |

## Variations and gotchas

- Rotation is interpolated per Euler component, not by quaternion slerp, so large multi-axis rotations do not follow the shortest arc.
- Animations are paced by player time: a 10 s animation takes 10 s at 1000x warp and also progresses while paused.
- `AnimateWeld` captures start values when called; queued steps are re-based on promotion, but the first step starts from whatever
  the weld held at call time, including a value a UI edit later changes.
- Presets are written synchronously on the main thread on each change; a failed write is only logged.

## See also

- [Overview and comparison](overview.md)
- [Handoff callback](weld-one-vessel-to-another-1.md)
- Mod feature: [Garry's Torch weld animation and presets](../../../../mods/unscience/features/garrys-torch-weld-animation-and-presets.md)
- The topic store-named-presets-in-toml-with-tomlyn is merged here; persistence topics in other batches cover settings files

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
