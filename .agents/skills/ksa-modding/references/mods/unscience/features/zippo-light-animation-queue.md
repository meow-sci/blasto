---
type: Mod Feature
title: Per-light queues of eased color and intensity transitions
description: Zippo keeps a bounded queue of single-step LightAnimation transitions per light part instance, interpolates color and intensity with power-controlled EasingHelper curves from the UI frame tick, re-captures the live start values when each queued step begins, and persists progress in scene saves
mod: unscience
submod: zippo
category: rendering
techniques: [imgui-window, inter-mod-api, save-sidecar, starmap-lifecycle]
hooks:
  - "starmap StarMapBeforeGui (via unscience ISubmod.Update: animation tick)"
  - harmony-prefix KSA.Program.OnDrawUiConsole (shared HiddenUiFrameHook replays Update while the HUD is hidden)
  - "starmap StarMapAfterGui (via unscience ISubmod.RenderContent: queue builder and progress)"
  - "harmony-postfix KSA.GameSave.Populate (shared NativeSaveHooks: capture)"
  - "harmony-postfix KSA.Universe.DeserializeSave (shared NativeSaveHooks: replay)"
lifecycle: [frame:draw-ui, frame:render, event:save, event:load]
game_types: [KSA.Part, KSA.Vehicle, KSA.LightModule, KSA.Program]
tags: [lights, animation, easing, queue, interpolation, persistence, api]
---

Zippo's "Light Animation" section turns a light from one colour and intensity to another over a set
time with a chosen easing curve, and lets the player queue up to 25 more steps behind the running one,
building simple light shows on a single part without scripting. A progress bar shows the running step,
and the light's manual controls are locked while it plays. Other mods can drive the same queue through
`ZippoSubmod.Instance.QueueAnimation` and `ClearAnimationQueue`.[^zippo-readme][^zippo-submod]

Mechanism in one line: `LightAnimationManager` keeps one active `LightAnimation` and a `Queue` of
pending ones per runtime part key; every UI-frame tick advances each active step by `dt`, writes the
interpolated colour and intensity through the shared [light controller](./zippo-light-control.md), and
when a step completes promotes the next one with its start values replaced by whatever the light
actually shows at that moment.[^light-animation-manager]

## How it works

**One step** ([zippo.lib/LightAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/LightAnimation.cs)).
Immutable start and end colour (`float3`) and intensity, duration, `EasingType` and two power
parameters; only `ElapsedSeconds` changes. `Update(dt)` adds `dt`, returns the exact end values once
elapsed reaches the duration, and otherwise linearly interpolates colour and intensity with
`t = EasingHelper.ApplyEasing(elapsed / duration, easing, powerStart, powerEnd)`.[^light-animation]

**Easing** ([ksa-abstractions.lib/EasingHelper.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/EasingHelper.cs#L13-L29)).
Shared with Garry's Torch and Camera Controller Override:

| `EasingType` | Curve |
|---|---|
| `Linear` | `t` |
| `EaseIn` | `t^powerStart` |
| `EaseOut` | `1 - (1 - t)^powerEnd` |
| `EaseInOut` | `(2t)^powerStart / 2` below 0.5, `1 - (2(1 - t))^powerEnd / 2` above |

The UI shows the Start Power slider (1 to 6) only for EaseIn and EaseInOut and End Power only for
EaseOut and EaseInOut, so asymmetric in-out curves are possible.[^easing-helper][^zippo-submod]

**Queues** ([zippo.lib/LightAnimationManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/LightAnimationManager.cs)):

- Keys are `"part-instance:" + Part.InstanceId`, a runtime-unique id (`Universe.GetNextRunningId()` in
  the `Part` constructor), because `Part.Id` is a template or sub-part name that can repeat on one
  vessel.[^light-animation-manager][^ksa-part]
- `Enqueue` starts the animation immediately if the part is idle, otherwise appends it, refusing beyond
  `MaxQueueDepth = 25` pending steps.
- `Update(dt, resolver)` iterates a snapshot of the active keys; a key whose part no longer resolves has
  its active step and queue dropped. Each active step's sample is written with
  `LightController.ApplyColor` and `ApplyIntensity`.
- `PromoteNext` dequeues and rebuilds the next step with start colour and intensity read back from the
  part template, keeping only its end values, duration and easing. A queued step therefore always starts
  from where the light really is, even if something else changed it in between.
- `CancelAll(key)` and `Clear()` drop work without restoring anything; the light keeps its last values.

**Builder UI** ([zippo.lib/ZippoSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/ZippoSubmod.cs#L235-L403)).
Start and end colours each have a swatch picker and a filterable combo over the full
[XKCD colour catalog](./lib-xkcd-color-catalog.md); start intensity, end intensity, duration (0.1 to
60 s), easing and powers follow. Start values are seeded from the part when it is selected. "Queue
Animation" stops Disco on that light, then enqueues; the active step shows "Playing (queued: N)",
elapsed and total seconds and a progress bar. While a step is active on the selected part the manual
on/off, intensity and colour controls are wrapped in `BeginDisabled`.[^zippo-submod]

**Mutual exclusion with Disco.** Starting [Disco](./zippo-disco-party-lights.md) on a part calls
`CancelAll` for its key, and queueing an animation (UI or API) stops Disco on that part first, so the two
engines never write the same light.[^zippo-disco]

**Persistence** ([zippo.lib/LightAnimation.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/LightAnimation.Persistence.cs)).
`SavedLightAnimation` mirrors every field including `Elapsed`; `Validate` requires finite values, a
duration in (0, 1e9], elapsed within the duration, a defined easing and positive powers. The Zippo
participant saves one `SavedLightQueue` per light part that has work, addressed with a
[stable part reference](./lib-save-part-identity.md); `RestoreSaved` makes the first entry active (with
its saved elapsed time) and queues the rest. A queue whose part also has a restored Disco effect is
rejected with a warning, and the per-part count is capped at 26 (one active plus 25 queued).[^light-animation-persistence][^zippo-persistence]

## Lifecycle and timing

| Host | Tick source | Phase |
|---|---|---|
| unscience | `Mod.UpdateSubmods` from `[StarMapBeforeGui]`, or from the `HiddenUiFrameHook` prefix on `Program.OnDrawUiConsole` while F2 hides the HUD | `frame:draw-ui` |
| Standalone Zippo | Its own `[StarMapBeforeGui]` only, so playback pauses while the HUD is hidden | `frame:draw-ui` |

StarMap's `BeforeGui` is a prefix on `Program.OnDrawUiFrame(dtPlayer)`, so `dt` is the player-time frame
delta that `Program.OnFrame` receives; the simulation step is derived from it later by multiplying with
the simulation speed in `Universe.GetJobSimStep`.[^ksa-program] Light transitions therefore run in real
seconds, continue while the game is paused, and do not speed up under time warp. The written template
values are read by `LightModule.UpdateRenderData` during `Program.Render` in the same frame, so there is
no frame of lag and no physics synchronization is needed; colour and intensity are not part of the
vessel solver's state.[^scope-celestial-lights][^zippo-mod]

## Key code

| Path | Role |
|---|---|
| [zippo.lib/LightAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/LightAnimation.cs) | One eased transition |
| [zippo.lib/LightAnimationManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/LightAnimationManager.cs) | Per-part active step and bounded queue, promotion |
| [zippo.lib/LightAnimation.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/LightAnimation.Persistence.cs) | Saved step DTO, capture and restore |
| [zippo.lib/ZippoSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/ZippoSubmod.cs#L525-L537) | `ResolvePartByKey` and `PartKey` |
| [ksa-abstractions.lib/EasingHelper.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/EasingHelper.cs) | Shared easing curves |
| [zippo.lib/ZippoSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/ZippoSubmod.Persistence.cs#L54-L59) | Queue capture inside the `zippo` participant |

## Snippets

One step, eased:

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

Tick, apply, promote:

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

The next step starts from the light's live values:

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

Power-controlled easing shared across sub-mods:

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

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `Part.InstanceId` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L574) | Runtime-unique queue key, assigned from `Universe.GetNextRunningId()` (L1492) |
| `Part.Template` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L576) | Read-back of live values on promotion |
| `Program.OnFrame` / `OnDrawUiFrame(double)` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2276) | Source and units of `dt` |

Light template members written by each tick are listed in [light control](./zippo-light-control.md).

## Pitfalls and version notes

- Each tick writes the part template, so an animation on one light changes every light of that part
  type on every vessel, exactly like a manual edit.[^zippo-lib-readme]
- The resolver scans every vessel (debris included) and its light parts for every active key, every
  frame; many simultaneous animations on large fleets cost linear scans.[^zippo-submod]
- A queued step's start values are ignored; only its end values, duration and easing matter. API callers
  that pass carefully chosen start values only get them for the first, immediately started step.
- `CancelAll` and the queue-full path do not restore anything. There is no "return to original" step;
  queue one explicitly or use the colour combo's Default entry.
- Durations are real seconds of player time: pausing the game does not pause light animations, and a
  low frame rate makes steps advance in coarse increments (end values are still exact).[^ksa-program]
- The persisted elapsed time resumes mid-step after a load; promotion still re-captures the start from
  the restored template values, which the ledger restores first.[^zippo-lib-readme]

## See also

- [Light control](./zippo-light-control.md) and [Disco party lights](./zippo-disco-party-lights.md)
- [Eased weld animation queues and TOML weld presets](./garrys-torch-weld-animation-and-presets.md) (the same queue pattern for welds)
- [Thin static providers](./lib-game-state-providers.md) (`EasingHelper`)
- [Keep per-frame mod work running while the HUD is hidden](./lib-hidden-ui-frame-hook.md)
- [Stable vehicle and part identity across save and reload](./lib-save-part-identity.md)
- [Zippo sub-mod](../submods/zippo.md) and [mod overview](../overview.md)
- [Animate a light with a bounded queue of eased single-step transitions](../../../howto/rendering/lights-and-particles/control-part-light-color-and-state/control-part-light-color-and-state-4.md) - how-to article demonstrating this feature

[^zippo-readme]: zippo/README.md
[^zippo-submod]: zippo.lib/ZippoSubmod.cs
[^light-animation-manager]: zippo.lib/LightAnimationManager.cs
[^light-animation]: zippo.lib/LightAnimation.cs
[^easing-helper]: ksa-abstractions.lib/EasingHelper.cs
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^zippo-disco]: zippo.lib/ZippoSubmod.Disco.cs
[^light-animation-persistence]: zippo.lib/LightAnimation.Persistence.cs
[^zippo-persistence]: zippo.lib/ZippoSubmod.Persistence.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^scope-celestial-lights]: scope/celestial-and-lights.md
[^zippo-mod]: zippo/Mod.cs
[^zippo-lib-readme]: zippo.lib/README.md
