---
type: Mod Feature
title: Staged intro and outro animation
description: A time-driven animator that sweeps the overlay in as five overlapping eased stages, staggers gauges from the screen edge inward, plays itself backwards as an outro, and cross-fades engine dots on state changes
mod: ksa-public-telemetry
category: ui
techniques: [imgui-drawlist, animation-easing]
hooks: [starmap StarMapAfterGui, harmony-postfix KSA.Program.DrawFps]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImDrawListPtr]
tags: [imgui, animation, easing, overlay, tweening]
---

When the overlay is toggled on, the bar does not just appear: the backdrop slides up, gauge plates
grow, ring outlines draw themselves around, the arc tracks sweep, and the numbers fade in, with gauges
near the screen edges leading the ones near the centre.[^kpt-readme] Turning it off can play the same
sequence in reverse. The sequence is a pure function of elapsed seconds and the frame `dt`; there is
no tweening library, and the only state is the elapsed time (the engine dots keep a little per-dot
state of their own, item 6).

## How it works

1. **Stages.** `IntroAnimator` defines five stages (backdrop, gauges, rim, arc sweep, readouts), each a
   `(Start, Duration, Decay)` triple read from the tunable `Tuning` fields, all multiplied by a global
   `IntroTimeScale` ([Rendering/IntroAnimator.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/IntroAnimator.cs#L18-L44), [Rendering/Tuning.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Tuning.cs#L11-L34)).
2. **Easing.** Each stage's progress is `1 - (1 - t)^Decay` for normalised `t` in [0, 1]: decay 1 is
   linear, 3 is a cubic ease-out, 20 is a very fast start with a long tail
   ([Rendering/IntroAnimator.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/IntroAnimator.cs#L117-L151)). `Phases` returns an `IntroPhases` struct with the five
   values, or `IntroPhases.Complete` once the whole sequence has run.
3. **Consumers multiply.** Panels scale opacity by the matching phase (`BackdropOpacity`,
   `GaugeOpacity`, `ReadoutOpacity` on `PanelContext`), scale ring radius and text size from a start
   fraction up to 1, sweep arcs from 0 to their level, and draw the rim as a partial arc while
   `rimPhase < 1` ([Rendering/IOverlayPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/IOverlayPanel.cs#L34-L38), [Rendering/ReadoutPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/ReadoutPanel.cs#L79-L118),
   [Rendering/Gfx.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Gfx.cs#L202-L226)). The shelf plates slide in horizontally by
   `(1 - backdrop)^ShelfSlideEase * ShelfSlideDistance` ([Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs#L190-L218)).
4. **Edge-in stagger.** `PanelContext.IntroFor(centerX, width)` counts how many element widths a gauge is
   from its nearer screen edge and asks the animator for phases delayed by
   `steps * GaugeStaggerStep`, capped at `GaugeStaggerMax`
   ([Rendering/IOverlayPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/IOverlayPanel.cs#L40-L58), [Rendering/IntroAnimator.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/IntroAnimator.cs#L102-L115)).
5. **Outro.** `Reverse()` captures the current elapsed time (or the point where every stage is visually
   done, `VisibleEnd`, when the intro had finished) and then scales elapsed time down to zero linearly over
   `OutroSeconds`, so the same easing curves run backwards. Re-showing mid-outro resumes from where it
   retracted to ([Rendering/IntroAnimator.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/IntroAnimator.cs#L56-L100)). The renderer keeps drawing while the
   outro runs and only hands the stock HUD back once `IsHidden` ([Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs#L68-L106)).
6. **Engine dots.** The diagram has its own small animations: each dot's status change is delayed by a
   random jitter up to `StateJitterSeconds` and then colour-lerped over `StateFadeSeconds`, so a cluster
   of engines lighting up flickers on like a real ignition sequence; a change in engine count plays a
   cubic ease-out swap (old set shrinks out, new set pops in), and rings of dots pop in one ring after
   another ([Rendering/EngineClusterPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/EngineClusterPanel.cs#L168-L283), [Rendering/EngineClusterPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/EngineClusterPanel.cs#L308-L380),
   [Rendering/EngineClusterPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/EngineClusterPanel.cs#L414-L464)).
7. **Notification banners** fade in over 0.2 s, hold 4.5 s and fade out over 0.8 s
   ([Rendering/NotificationPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/NotificationPanel.cs#L8-L11), [Rendering/NotificationPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/NotificationPanel.cs#L162-L176)).

## Lifecycle and timing

- Advanced inside the draw, in `frame:draw-ui` (`[StarMapAfterGui]`, or the `DrawFps` postfix when the
  HUD is hidden). `IntroAnimator.Update` clamps each step to 0.1 s, so a hitch (for example the first
  frame after loading) does not skip the whole sequence ([Rendering/IntroAnimator.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/IntroAnimator.cs#L85-L100)).
- The `dt` is the `dtPlayer` that `Program.OnFrame` hands to its UI calls (and StarMap forwards), the
  player-frame delta rather than the simulation step, so the animation runs at the same speed under
  time warp or pause. On the `DrawFps` path it is the previous frame's `dtPlayer`, recorded in
  `[StarMapAfterOnFrame]` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2264-L2275)).
- The standalone windows pass `IntroPhases.Complete` and never animate
  ([Rendering/TelemetryWindow.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/TelemetryWindow.cs#L62-L65)).

## Key code

| Path | Role |
|---|---|
| [Rendering/IntroAnimator.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/IntroAnimator.cs#L18-L152) | Stage table, easing, reverse playback |
| [Rendering/IOverlayPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/IOverlayPanel.cs#L40-L58) | Edge-in stagger per element |
| [Rendering/EngineClusterPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/EngineClusterPanel.cs#L168-L243) | Jittered state fades and swap trigger |
| [Rendering/Tuning.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Tuning.cs#L11-L34) | Default stage timings |

## Snippets

```csharp
// Rendering/IntroAnimator.cs L85-L100, L128-L151
    public void Update(double dt)
    {
        double step = Math.Clamp(dt, 0.0, 0.1);

        if (_reversing)
        {
            _outroLeft = Math.Max(0.0, _outroLeft - step);
            _elapsed = _outroFrom * Math.Clamp(_outroLeft / _outroTotal, 0.0, 1.0);
            return;
        }

        if (_elapsed < TotalSeconds)
        {
            _elapsed += step;
        }
    }
// ...
        if (t <= 0.0)
        {
            return 0f;
        }

        if (t >= 1.0)
        {
            return 1f;
        }

        return (float)(1.0 - Falloff(1.0 - t, stage.Decay));
    }

    private static double Falloff(double inverse, int decay)
    {
        double result = inverse;

        for (int i = 1; i < decay; i++)
        {
            result *= inverse;
        }

        return result;
    }
```

## Game APIs touched

None beyond the ImGui draw list the panels draw into; the animation is engine-independent.

## Pitfalls and version notes

- **`dt`-driven with a 0.1 s clamp.** Stages advance by real frame time, so the intro takes the same
  time at any frame rate above 10 fps; below that, or on a long first frame after loading, the clamp
  makes it run slower rather than skip.
- **`Restart` while reversing** keeps the retracted elapsed time on purpose; if you copy this, decide
  whether a re-show should snap or resume.
- **Random jitter** uses `System.Random` per panel; deterministic replays would need a seeded source.

## See also

- [Broadcast overlay on the ImGui foreground draw list](foreground-drawlist-broadcast-overlay.md)
- [Live tuning window and palette by reflection](live-tuning-and-palette-reflection.md)
- [Engine diagram from real nozzle positions](engine-diagram-from-nozzle-positions.md)
- [HUD instruments: overlapping staged intro and outro animation with easing and edge-in stagger](../../../howto/ui/overlays/draw-hud-instruments-with-the-draw-list/draw-hud-instruments-with-the-draw-list-2.md) - how-to article demonstrating this feature

[^kpt-readme]: README.md
