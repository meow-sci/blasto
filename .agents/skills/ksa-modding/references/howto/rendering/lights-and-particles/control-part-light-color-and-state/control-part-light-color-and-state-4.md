---
type: How-To
title: Animate a light with a bounded queue of eased single-step transitions
description: Move a part light from one colour and intensity to another with a chosen easing curve and queue up to 25 more steps per light, ticked by UI-frame delta time and re-capturing live start values on promotion
category: rendering
group: lights-and-particles
topic: control-part-light-color-and-state
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/zippo-light-animation-queue]
techniques: [inter-mod-api, starmap-lifecycle]
hooks: [starmap StarMapBeforeGui, harmony-prefix KSA.Program.OnDrawUiConsole, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:render, event:save, event:load]
game_types: [KSA.Part, KSA.Vehicle, KSA.LightModule, KSA.Program]
tags: [lights, animation, easing, queue, transitions, inter-mod-api]
---
Outcome: light shows on a single part without scripting. A light goes from one colour and intensity to another over a set time with an easing curve, and up to 25 more steps wait behind the running one. A progress bar shows the running step and the manual controls are locked while it plays. Other mods can queue steps through a static API instance.

Demonstrated by unscience's Zippo light animation ([Per-light queues of eased color and intensity transitions](../../../../mods/unscience/features/zippo-light-animation-queue.md)). Prerequisites: StarMap, and the template writes of [approach 1](control-part-light-color-and-state-1.md). The easing helper lives in unscience's shared `ksa-abstractions.lib`, not in the game.

## Background: how the game does it

The game offers keyframe actuators for authored animations ([approach 5](control-part-light-color-and-state-5.md) shows driving them); there is no runtime hook for "fade this light". The light reads its colour and intensity from the template every frame (`LightModule.UpdateRenderData`), so the technique is: every UI frame, compute an eased interpolation and write it through approach 1's `ApplyColor` and `ApplyIntensity`. The values written this frame are read in the same frame's `Render`, so there is no lag and no physics synchronization (colour and intensity are not part of the vessel solver's state).

## Lifecycle and timing

| Host | Tick source | Phase |
|---|---|---|
| unscience | `Mod.UpdateSubmods` from `[StarMapBeforeGui]`, or from a `HiddenUiFrameHook` prefix on `Program.OnDrawUiConsole` while F2 hides the HUD | `frame:draw-ui` |
| standalone Zippo | its own `[StarMapBeforeGui]` only, so playback pauses while the HUD is hidden | `frame:draw-ui` |

StarMap's `BeforeGui` is a prefix on `Program.OnDrawUiFrame(dtPlayer)`, so `dt` is the player-time frame delta that `Program.OnFrame` receives ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2276)); the simulation step is derived from it later by multiplying with the simulation speed. Transitions therefore run in real seconds, continue while the game is paused, and do not speed up under time warp. See rows 12 to 15 of [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence); for keeping per-frame work running with the HUD hidden see that reference and [hide parts: approach 3](../../meshes/hide-parts-or-subparts-from-rendering/hide-parts-or-subparts-from-rendering-3.md).

## Steps

1. **Model one step as an immutable record plus elapsed time.** `Update(dt)` adds `dt`, returns exact end values once elapsed reaches the duration, otherwise interpolates colour and intensity with the eased `t`:

   ```csharp
// zippo.lib/LightAnimation.cs L45-L61
public (float3 Color, float Intensity) Update(double dt)
{
    ElapsedSeconds += dt;

    if (ElapsedSeconds >= DurationSeconds)
    {
        return (EndColor, EndIntensity);
    }

    double rawT = ElapsedSeconds / DurationSeconds;
    float t = (float)EasingHelper.ApplyEasing(rawT, Easing, EasingPowerStart, EasingPowerEnd);

    var color = Lerp(StartColor, EndColor, t);
    float intensity = StartIntensity + (EndIntensity - StartIntensity) * t;

    return (color, intensity);
}
```

2. **Easing helper** with power-controlled curves:

   ```csharp
// ksa-abstractions.lib/EasingHelper.cs L13-L29
public static class EasingHelper
{
    public static double ApplyEasing(double t, EasingType easingType,
        double powerStart = 3.0, double powerEnd = 3.0)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return easingType switch
        {
            EasingType.EaseIn  => Math.Pow(t, powerStart),
            EasingType.EaseOut => 1.0 - Math.Pow(1.0 - t, powerEnd),
            EasingType.EaseInOut => t < 0.5
                ? Math.Pow(2 * t, powerStart) / 2.0
                : 1.0 - Math.Pow(2 * (1 - t), powerEnd) / 2.0,
            _ => t
        };
    }
}
```

3. **Keep one active step and a bounded queue per part, keyed by the runtime-unique `Part.InstanceId`** (`"part-instance:" + InstanceId`), not by `Part.Id`, which can repeat on one vessel. `Enqueue` starts at once when the part is idle, otherwise appends, refusing beyond 25 pending steps. Tick and apply each frame:

   ```csharp
// zippo.lib/LightAnimationManager.cs L62-L88
public void Update(double dt, Func<string, Part?> partResolver)
{
    var keys = new List<string>(_active.Keys);
    foreach (var partKey in keys)
    {
        var part = partResolver(partKey);
        if (part == null)
        {
            // Part no longer available — cancel all animations for it
            _active.Remove(partKey);
            _queues.Remove(partKey);
            continue;
        }

        var anim = _active[partKey];
        var (color, intensity) = anim.Update(dt);

        LightController.ApplyColor(part, color);
        LightController.ApplyIntensity(part, intensity);

        if (anim.IsComplete)
        {
            _active.Remove(partKey);
            PromoteNext(partKey, part);
        }
    }
}
```

4. **On completion, promote the next step with its start values replaced by what the light actually shows now**, so a queued step always starts from where the light really is even if something else changed it:

   ```csharp
// zippo.lib/LightAnimationManager.cs L104-L127
private void PromoteNext(string partKey, Part part)
{
    if (!_queues.TryGetValue(partKey, out var queue) || queue.Count == 0)
    {
        _queues.Remove(partKey);
        return;
    }

    var next = queue.Dequeue();
    if (queue.Count == 0)
        _queues.Remove(partKey);

    // Re-capture current part values as start state
    var currentColor = LightController.ReadColor(part.Template);
    var currentIntensity = LightController.ReadIntensity(part.Template);

    var corrected = new LightAnimation(
        currentColor, next.EndColor,
        currentIntensity, next.EndIntensity,
        next.DurationSeconds, next.Easing,
        next.EasingPowerStart, next.EasingPowerEnd);

    _active[partKey] = corrected;
}
```

5. **Expose a small API for other mods**, returning errors as strings rather than throwing: `QueueAnimation(vehicleId, partId, LightAnimation)`, `ClearAnimationQueue`, `IsAnimating`:

   ```csharp
// zippo.lib/ZippoSubmod.cs L487-L506
/// <summary>Queues a light animation on a specific part. Returns error message or null on success.</summary>
public string? QueueAnimation(string vehicleId, string partId, LightAnimation animation)
{
    var part = ResolvePartInVehicle(vehicleId, partId);
    if (part == null) return $"Part '{partId}' not found on vehicle '{vehicleId}'.";

    StopDisco(part);
    if (!_animationManager.Enqueue(PartKey(part), animation))
        return $"Animation queue is full for part '{partId}' (max {LightAnimationManager.MaxQueueDepth}).";
    return null;
}

/// <summary>Clears the animation queue for a specific part. Returns error message or null on success.</summary>
public string? ClearAnimationQueue(string vehicleId, string partId)
{
    // No error if part doesn't exist — clear is idempotent.
    var part = ResolvePartInVehicle(vehicleId, partId);
    if (part != null) _animationManager.CancelAll(PartKey(part));
    return null;
}
```

6. **Persist** each step including its elapsed time with a validation pass on restore (finite values, duration in (0, 1e9], elapsed within the duration, defined easing, positive powers). A queue is addressed with a stable part reference; the per-part count is capped at 26 (one active plus 25 queued).

## Complete example

| File | Role |
|---|---|
| [zippo.lib/LightAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/LightAnimation.cs) | One eased transition |
| [zippo.lib/LightAnimationManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/LightAnimationManager.cs) | Per-part active step and bounded queue, promotion |
| [zippo.lib/LightAnimation.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/LightAnimation.Persistence.cs) | Saved step DTO, capture and restore |
| [ksa-abstractions.lib/EasingHelper.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/EasingHelper.cs) | Shared easing curves |

## Variations and gotchas

- **Template scope.** Each tick writes the part template, so an animation on one light changes every light of that part type on every vessel, like a manual edit (approach 1). Use a per-instance template (approach 5) for isolation.
- **Cost.** The resolver scans every vessel and its light parts for every active key every frame; many simultaneous animations on large fleets cost linear scans.
- **Start values of queued steps are ignored**; only end values, duration and easing matter. API callers only get their chosen start for the first, immediately started step.
- **Cancel does not restore.** `CancelAll` and the queue-full path leave the light at its last values; queue a "return" step explicitly.
- **Real seconds.** Pausing the game does not pause light animations, and a low frame rate makes steps advance in coarse increments (end values are still exact).
- **Mutual exclusion with other writers.** Zippo stops its Disco effect on a part before queueing an animation, and the reverse, so two engines never write one light.

## See also

- [Overview](overview.md)
- [Approach 1](control-part-light-color-and-state-1.md), [approach 5](control-part-light-color-and-state-5.md)
- [Per-light queues of eased color and intensity transitions](../../../../mods/unscience/features/zippo-light-animation-queue.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
