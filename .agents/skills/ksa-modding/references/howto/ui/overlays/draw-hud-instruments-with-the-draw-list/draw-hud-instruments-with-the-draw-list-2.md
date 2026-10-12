---
type: How-To
title: "HUD instruments: overlapping staged intro and outro animation with easing and edge-in stagger"
description: Animate a draw-list HUD in and out as a pure function of elapsed seconds - five overlapping stages with 1-(1-t)^n easing, an edge-in stagger and a reversible outro
category: ui
group: overlays
topic: draw-hud-instruments-with-the-draw-list
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/intro-outro-animation]
techniques: [imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImDrawListPtr, KSA.Program]
tags: [hud, animation, easing, intro, outro, stagger, draw-list]
---
When the telemetry overlay is toggled on, the bar does not just appear: the backdrop slides up, gauge plates grow, ring outlines draw themselves around, the arc tracks sweep and the numbers fade in, with gauges near the screen edges leading the ones near the centre.
Turning it off can play the same sequence in reverse. The sequence is a pure function of elapsed seconds and the frame delta: there is no tweening library, and the only state is the elapsed time. This article is that animator and how
consumers use it. The drawing itself is in [approach 1](draw-hud-instruments-with-the-draw-list-1.md). Demonstrated by KSA-Public-Telemetry.

## Background: how the game does it

Nothing in KSA is involved beyond the draw list the panels draw into; the animation is engine-independent. The one engine fact that matters is the time source: the delta is the `dtPlayer` that `Program.OnFrame` hands its UI calls, StarMap forwards it to the
GUI hooks, and it is the player-frame delta, not the simulation step. The animation therefore runs at the same speed under time warp or pause. On the `DrawFps` path (HUD hidden) it is the previous frame's `dtPlayer`, recorded in `[StarMapAfterOnFrame]`.
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2245)[^ksa-program]

## Lifecycle and timing

The animator is advanced inside the draw, in `frame:draw-ui` (`[StarMapAfterGui]`, or the `DrawFps` postfix when the HUD is hidden). `IntroAnimator.Update` clamps each step to 0.1 s, so a hitch (the first frame after loading) does not skip the sequence; it
runs slower instead. The renderer keeps drawing while the outro runs and only hands the stock HUD back once `IsHidden`. Standalone windows pass `IntroPhases.Complete` and never animate. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define the phases** your panels consume - one float 0..1 per visual stage - and a constant for "done":

```csharp
// Rendering/IntroAnimator.cs L1-L15
namespace KSATelemetryOverlay.Rendering;

public readonly struct IntroPhases(
    float backdrop, float gauges, float rim, float arcSweep, float readouts)
{
    public readonly float Backdrop = backdrop;
    public readonly float Gauges = gauges;
    public readonly float Rim = rim;
    public readonly float ArcSweep = arcSweep;
    public readonly float Readouts = readouts;
    public static IntroPhases Complete => new(1f, 1f, 1f, 1f, 1f);

    public bool IsComplete =>
        Backdrop >= 1f && Gauges >= 1f && Rim >= 1f && ArcSweep >= 1f && Readouts >= 1f;

```

2. **Define stages as `(Start, Duration, Decay)` triples** read from tunable fields, all multiplied by one global time scale. `VisibleEnd` is where a stage is visually finished (1% remaining), used to start an outro from the point the viewer sees as complete:

```csharp
// Rendering/IntroAnimator.cs L18-L49
public sealed class IntroAnimator
{
    private readonly record struct Stage(double Start, double Duration, int Decay)
    {
        public double VisibleEnd =>
            Start + Duration * (1.0 - Math.Pow(0.01, 1.0 / Math.Max(Decay, 1)));
    }
    private static Stage Backdrop => new(Tuning.BackdropStart, Tuning.BackdropDuration, Tuning.BackdropDecay);
    private static Stage Gauges   => new(Tuning.GaugesStart,   Tuning.GaugesDuration,   Tuning.GaugesDecay);
    private static Stage Rim      => new(Tuning.RimStart,      Tuning.RimDuration,      Tuning.RimDecay);
    private static Stage ArcSweep => new(Tuning.ArcSweepStart, Tuning.ArcSweepDuration, Tuning.ArcSweepDecay);
    private static Stage Readouts => new(Tuning.ReadoutsStart, Tuning.ReadoutsDuration, Tuning.ReadoutsDecay);

    private static double TimeScale => Math.Max(Tuning.IntroTimeScale, 0.01f);

    private static double SequenceSeconds => Math.Max(
        Math.Max(Backdrop.Start + Backdrop.Duration, Gauges.Start + Gauges.Duration),
        Math.Max(Rim.Start + Rim.Duration,
            Math.Max(ArcSweep.Start + ArcSweep.Duration, Readouts.Start + Readouts.Duration)))
        * TimeScale;

    private static double TotalSeconds => SequenceSeconds + Math.Max(Tuning.GaugeStaggerMax, 0f);
    private static double VisibleSeconds => Math.Max(
        Math.Max(Backdrop.VisibleEnd, Gauges.VisibleEnd),
        Math.Max(Rim.VisibleEnd, Math.Max(ArcSweep.VisibleEnd, Readouts.VisibleEnd)))
        * TimeScale
        + Math.Max(Tuning.GaugeStaggerMax, 0f);
    private double _elapsed = double.PositiveInfinity;
    private bool _reversing;
    private double _outroFrom;
    private double _outroTotal;
    private double _outroLeft;
```

3. **Ease with `1 - (1 - t)^Decay`.** Decay 1 is linear, 3 a cubic ease-out, 20 a very fast start with a long tail. Normalise `t` per stage and clamp:

```csharp
// Rendering/IntroAnimator.cs L117-L151
private float Progress(Stage stage, double elapsed)
{
    double duration = stage.Duration * TimeScale;

    if (duration <= 0.0)
    {
        return 1f;
    }

    double t = (elapsed - stage.Start * TimeScale) / duration;

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

4. **Advance and reverse by scaling elapsed time.** `Update` adds `dt` clamped to 0.1 s. `Reverse()` captures the current elapsed time (or `VisibleEnd` if the intro had finished) and then scales elapsed down to zero linearly over `OutroSeconds`, so the same curves run
   backwards; `Restart()` mid-outro resumes from where it retracted to:

```csharp
// Rendering/IntroAnimator.cs L56-L100
public void Restart()
{
    _elapsed = _reversing ? Math.Clamp(_elapsed, 0.0, TotalSeconds) : 0.0;
    _reversing = false;
}

public void Finish()
{
    _elapsed = double.PositiveInfinity;
    _reversing = false;
}

public void Reverse()
{
    if (_reversing)
    {
        return;
    }

    _outroFrom = double.IsFinite(_elapsed)
        ? Math.Min(_elapsed, VisibleSeconds)
        : VisibleSeconds;

    _outroTotal = Math.Max(Tuning.OutroSeconds, 0.05f);
    _outroLeft = _outroTotal;
    _elapsed = _outroFrom;
    _reversing = true;
}

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
```

5. **Stagger from the edges inward.** Count how many element widths a gauge is from its nearer screen edge and ask the animator for phases delayed by `steps * GaugeStaggerStep`, capped at `GaugeStaggerMax`:

```csharp
// Rendering/IOverlayPanel.cs L40-L58
public IntroPhases IntroFor(float centerX, float elementWidth)
{
    if (elementWidth <= 0f)
    {
        return Intro;
    }

    float middle = ViewportPos.X + ViewportSize.X * 0.5f;

    float fromEdge = centerX < middle
        ? centerX - ViewportPos.X
        : ViewportPos.X + ViewportSize.X - centerX;

    float steps = MathF.Max(fromEdge / elementWidth - 0.5f, 0f);

    float delay = MathF.Min(steps * Tuning.GaugeStaggerStep, MathF.Max(Tuning.GaugeStaggerMax, 0f));

    return _animator is null ? Intro : _animator.PhasesDelayedBy(delay);
}
```

6. **Multiply in the consumers.** Scale opacity by the matching phase, scale ring radius and text size from a start fraction up to 1, sweep arcs from 0 to their level, and draw the rim as a partial arc while `rimPhase < 1`:

```csharp
// Rendering/ReadoutPanel.cs L79-L109
IntroPhases intro = context.IntroFor(center.X, capsule);

float ringRadius =
    radius * (IntroRingStartScale + (1f - IntroRingStartScale) * intro.Gauges);

float ringOpacity = context.Opacity * intro.Gauges;
float textOpacity = context.Opacity * intro.Readouts;

ReadoutKind kind = Kinds[i];

ReadOnlySpan<char> value = Unavailable(kind, context.Snapshot)
    ? "--".AsSpan()
    : ValueFor(kind, _smoothed[i], buffer);

if (ringOpacity > 0f)
{
    Gfx.GaugePlate(context.DrawList, center, ringRadius, ringOpacity);

    float fullScale = FullScaleFor(kind, context.Config);
    if (fullScale > 0f && intro.ArcSweep > 0f)
    {
        DrawSweep(context.DrawList, center, ringRadius,
            intro.ArcSweep, OverlayStyle.ArcTrack, ringOpacity, scale);

        float level = Math.Clamp(_smoothed[i] / fullScale, 0f, 1f) * intro.ArcSweep;
        DrawSweep(context.DrawList, center, ringRadius,
            level, OverlayStyle.ArcFill, ringOpacity, scale);
    }

    Gfx.ReadoutBrackets(context.DrawList, center, ringRadius, ringOpacity, scale);
}
```

7. **Slide plates in** with the same phase, `(1 - backdrop)^ShelfSlideEase * ShelfSlideDistance`:

```csharp
// Rendering/OverlayRenderer.cs L204-L207
float remaining = MathF.Pow(
    1f - Math.Clamp(context.Intro.Backdrop, 0f, 1f), MathF.Max(Tuning.ShelfSlideEase, 0.01f));

float slideOffset = remaining * Tuning.ShelfSlideDistance * context.Scale;
```

8. **Small state animations.** The engine diagram delays each dot's status change by a random jitter up to `StateJitterSeconds` and then colour-lerps it over `StateFadeSeconds`; notification banners fade in over 0.2 s, hold 4.5 s and fade out over 0.8 s.

## Complete example

| File | Role |
|---|---|
| [Rendering/IntroAnimator.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/IntroAnimator.cs) | Stage table, easing, reverse playback |
| [Rendering/IOverlayPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/IOverlayPanel.cs#L16-L59) | `PanelContext` and the per-element stagger |
| [Rendering/ReadoutPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/ReadoutPanel.cs#L62-L121) | A consumer |
| [Rendering/Tuning.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Tuning.cs) | Default stage timings |

## Variations and gotchas

- **`dt`-driven with a 0.1 s clamp.** The intro takes the same time at any frame rate above 10 fps; below that the clamp makes it run slower rather than skip.
- **`Restart` while reversing** keeps the retracted elapsed time on purpose; decide whether a re-show should snap or resume.
- **Random jitter** uses `System.Random` per panel; deterministic replays would need a seeded source.
- **Hide the stock HUD only after the outro.** The renderer hands the stock flight HUD back once `IsHidden`, so the two never overlap.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of draw-hud-instruments-with-the-draw-list](overview.md)
- [Approach 1: arcs and gradients](draw-hud-instruments-with-the-draw-list-1.md)
- [Overlay on the foreground draw list](../draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-3.md)
- Mod feature: [Staged intro and outro animation](../../../../mods/ksa-public-telemetry/features/intro-outro-animation.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
