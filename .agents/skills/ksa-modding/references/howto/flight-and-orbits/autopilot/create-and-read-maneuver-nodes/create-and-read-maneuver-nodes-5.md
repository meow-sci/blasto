---
type: How-To
title: Read planned burns with a TryGetBurn loop and draw them on a mission timeline
description: Walk the flight computer's BurnPlan each frame, convert burn times to mission time, and draw them as predicted events with collision-avoiding labels
category: flight-and-orbits
group: autopilot
topic: create-and-read-maneuver-nodes
approach: 5
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/mission-timeline-with-planned-burns]
techniques: [starmap-lifecycle, imgui-drawlist]
hooks: [starmap StarMapAfterOnFrame, starmap StarMapAfterGui]
lifecycle: [frame:after-on-frame, frame:draw-ui]
game_types: [KSA.BurnPlan, KSA.Burn, KSA.FlightComputer, KSA.Vehicle, KSA.UniverseTime]
tags: [maneuver-node, burn-plan, timeline, mission-time, imgui, labels]
---

Outcome: a thin timeline under the mission clock on which every planned maneuver node shows up as a dimmed "BURN" label that approaches the marker.
KSA-Public-Telemetry demonstrates it.[Mission timeline with planned burns](../../../../mods/ksa-public-telemetry/features/mission-timeline-with-planned-burns.md) Prerequisite: StarMap.

## Background: how the game does it

`Vehicle.FlightComputer.BurnPlan` holds the burns. The read API is `HasActiveBurns` (any burn), `BurnCount`, `TryGetBurn(int index, out Burn? burn)`;
`Burn.Time` is the burn's `UniverseTime` and `Burn.HasDeltaV` tells a trivial placeholder from a real node.[^ksa-burnplan] [current/decomp/KSA/BurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnPlan.cs#L22-L42)
[current/decomp/KSA/BurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnPlan.cs#L201-L210) [^ksa-burn] [current/decomp/KSA/Burn.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Burn.cs#L36) [current/decomp/KSA/Burn.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Burn.cs#L151) The vehicle's `FlightComputer` is the main-thread copy that the game refreshes from the worker result with
`CopyFrom` when it applies the step (`BurnPlan` itself is shared by reference).

## Lifecycle and timing

The plan is read in `frame:after-on-frame` (`[StarMapAfterOnFrame]`): after the frame the main-thread copy holds the applied step plus anything the player changed in the
planner UI this frame, and edits only reach the plan through `ApplyInputEvents`, so a read there is race-free. The timeline is drawn in `frame:draw-ui` the next frame from
the snapshot. Event times are in mission seconds (simulation time), so markers move at warp speed. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Walk the plan each sample** and skip burns without delta-v; clear predictions when there is no active burn:

```csharp
// Telemetry/TelemetrySampler.cs L281-L306
private static void SamplePlannedBurns(Vehicle vehicle, Mission mission, double nowUniverseSeconds)
{
    _burnTimes.Clear();

    FlightComputer? computer = vehicle.FlightComputer;
    BurnPlan? plan = computer?.BurnPlan;

    if (plan is null || !plan.HasActiveBurns)
    {
        mission.Events.ClearPredictions();
        return;
    }

    int count = plan.BurnCount;
    for (int i = 0; i < count; i++)
    {
        if (!plan.TryGetBurn(i, out Burn? burn) || burn is null || !burn.HasDeltaV)
        {
            continue;
        }

        _burnTimes.Add(mission.Clock.MissionTimeFor(burn.Time.Seconds(), nowUniverseSeconds));
    }

    mission.Events.SetPredictions(CollectionsMarshal.AsSpan(_burnTimes));
}
```

2. **Convert to mission time** relative to the liftoff epoch, or the countdown zero, or "now" before either exists, so pre-launch burns sit relative to the present:

```csharp
// Telemetry/MissionClock.cs L106-L113
public double MissionTimeFor(double universeSeconds, double nowUniverseSeconds)
{
    double epoch = _hasLiftoff
        ? _liftoffUniverseSeconds
        : _hasCountdown ? _countdownZero : nowUniverseSeconds;

    return universeSeconds - epoch;
}
```

   (see [Show mission elapsed time from actual liftoff and pick the right clock under time warp](../../flight-state/compute-mission-elapsed-time-and-detect-liftoff/overview.md) for the epoch).
3. **Replace the predictions every frame**: clear and refill a separate `Predicted` list so edits and deletions in the planner show up immediately; predictions never raise a banner.

```csharp
// Telemetry/MissionEventLog.cs L260-L271
public void SetPredictions(ReadOnlySpan<double> missionTimes)
{
    _predicted.Clear();

    for (int i = 0; i < missionTimes.Length; i++)
    {
        _predicted.Add(new MissionEvent(
            MissionEventKind.PlannedBurn, missionTimes[i], isPrediction: true));
    }
}

public void ClearPredictions() => _predicted.Clear();
```

4. **Map time to x** over the visible window: `x = left + width * ((t - now + half) / (2 * half))`. Draw a dot per event on the track.
5. **Place labels with two row cursors.** Each label is centred on its dot, shifted right past the cursor of the row it uses, clamped to the track, alternated above and below, and
   joined to the dot with a hairline leader when it had to move:

```csharp
// Rendering/TimelinePanel.cs L145-L180
float2 extent = Gfx.MeasureWithFont(OverlayFonts.Label, labelSize, e.Label);
float centred = x - extent.X * 0.5f;
int chosen = row;
float placed = float.MaxValue;

for (int attempt = 0; attempt < rowCursor.Length; attempt++)
{
    int candidate = (row + attempt) % rowCursor.Length;
    float shifted = MathF.Max(centred, rowCursor[candidate] + minGap);

    if (shifted < placed)
    {
        placed = shifted;
        chosen = candidate;
    }
}

placed = Math.Clamp(placed, left, MathF.Max(left, left + size.X - extent.X));

rowCursor[chosen] = placed + extent.X;
row = (chosen + 1) % rowCursor.Length;

float labelY = chosen == 0
    ? trackY - labelOffset - extent.Y
    : trackY + labelOffset;

uint color = e.IsPrediction ? OverlayStyle.TextDim : OverlayStyle.TextPrimary;
float labelCentre = placed + extent.X * 0.5f;
float leaderAt = MathF.Max(extent.X * 0.5f, MinLeaderShift * scale);

if (MathF.Abs(labelCentre - x) > leaderAt)
{
    float2 from = new(x, trackY + (chosen == 0 ? -labelOffset * 0.4f : labelOffset * 0.4f));
    float2 to = new(labelCentre, chosen == 0 ? labelY + extent.Y : labelY);

    drawList.AddLine(in from, in to,
```

## Complete example

| File | Role |
|---|---|
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L281-L306) | read the burn plan |
| [Telemetry/MissionClock.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionClock.cs#L106-L113) | universe time to mission time |
| [Telemetry/MissionEventLog.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionEventLog.cs#L260-L271) | prediction list |
| [Rendering/TimelinePanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/TimelinePanel.cs#L38-L188) | window, mapping, labels, leaders |

## Variations and gotchas

- **Allocate nothing per frame.** The mod reuses a `_burnTimes` list and passes a span to `SetPredictions`.
- **`TryGetBurn(i, ...)` can fail** if the plan changes between `BurnCount` and the call; keep the null check.
- The window is 240 s wide by default and adjustable from 60 to 1800 s; events beyond half a window are not collected.
- The panel draws only with the HUD visible (`DrawUI`); the sampler keeps running.

## See also

- [Overview](overview.md); [impulse formulas](create-and-read-maneuver-nodes-3.md)
- [Mission timeline with planned burns](../../../../mods/ksa-public-telemetry/features/mission-timeline-with-planned-burns.md)
- [Show mission elapsed time from actual liftoff and pick the right clock under time warp](../../flight-state/compute-mission-elapsed-time-and-detect-liftoff/overview.md)

[^ksa-burnplan]: KSA.BurnPlan (decompiled, build 2026.10.10.5554)
[^ksa-burn]: KSA.Burn (decompiled, build 2026.10.10.5554)
