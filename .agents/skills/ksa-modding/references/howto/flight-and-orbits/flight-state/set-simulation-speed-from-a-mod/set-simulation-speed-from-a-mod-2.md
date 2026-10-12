---
type: How-To
title: Request a warp-to-time through InputEvents.AutoWarpBuffer
description: Ask the game's own auto-warp to warp to a UniverseTime by adding an AutoWarpData to the input buffer that PrepareFrame drains, so the stock warp UI, limits and stop behaviour apply
category: flight-and-orbits
group: flight-state
topic: set-simulation-speed-from-a-mod
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/launch-window-to-target-plane]
techniques: [input-buffer]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:input, frame:prepare]
game_types: [KSA.InputEvents, KSA.Universe, KSA.UniverseTime]
tags: [time-warp, auto-warp, input-events, launch-window]
---

Outcome: "warp to T-10 s before the launch window" with the same smooth ramp and limits as the stock warp button.
KSA-AdvancedFlightComputer does it for its launch-window helper.[Launch window to a target vehicle's plane, co-elliptic chase orbit and warp to the window](../../../../mods/ksa-advancedflightcomputer/features/launch-window-to-target-plane.md)
Prerequisite: StarMap.

## Background: how the game does it

Stock warp-to-time buttons never call `Universe.AutoWarpTo` from the UI. They add an `InputEvents.AutoWarpData` to
`InputEvents.AutoWarpBuffer`; `InputEvents.ApplyInputEvents` drains the buffer inside `PrepareFrame` with the vehicle solvers joined,
and `AutoWarpData.Apply` calls `Universe.AutoWarpStop(resetSimulationSpeed: true)` when `StopWarp` is set and
`Universe.AutoWarpTo(WarpToTime, SimTimeMargin, IsUncapped)` otherwise.[^ksa-inputevents] [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L854-L874)
[current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1124-L1128) Each frame `Universe.ProcessAutoWarp(dtPlayer)` then eases the speed toward the target (smootherstep over
the player-time span, capped at the highest quick speed unless `IsUncapped`) and stops itself when the time is reached.[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2208-L2245)
The game's own `WarpToNext` (burn warp) is the same pattern with `SimTimeMargin = 3.0` and `IsUncapped = true`.[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L795-L802)

## Lifecycle and timing

The mod draws its prompt in `StarMapAfterGui` (`frame:draw-ui`) while the vehicle workers are still running, so it must not touch warp
state directly there. The buffer is applied in the next frame's `ApplyInputEvents` (`frame:input` inside `PrepareFrame`,
Program.cs L2181), before `ProcessAutoWarp` and `GetJobSimStep`. A warp stop issued from the mod's `PrepareWorker` prefix
(`frame:dispatch-solvers`, also a main-thread point between apply and dispatch) is called directly. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Compute the target time** as a `UniverseTime`: `Universe.GetElapsedTime() + seconds`.
2. **Add to the buffer, do not call `AutoWarpTo`:**

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs L78-L87
if (ImGui.Button("Warp"))
{
    // The draw overlaps the vehicle solver, so the warp goes through the input buffer that Program.PrepareFrame drains with the solvers joined, as stock's own warp does.
    InputEvents.AutoWarpBuffer.Add(new InputEvents.AutoWarpData
    {
        StopWarp = false,
        WarpToTime = Universe.GetElapsedTime() + wait,
    });
    _warpPromptActive = false;
}
```

   `SimTimeMargin` stops early by that many seconds; `IsUncapped` allows speeds beyond the top quick speed.
3. **Ask the player first** when the warp is unexpected: the mod shows a "Warp to X?" prompt with a Stay-at-1x choice and only
   adds the data on click.
4. **Detect completion** by `Universe.IsAutoWarpActive` turning false; the mod hides its prompt when the wait is under a second or auto-warp is active.
5. **Stop it yourself when you must**: `Universe.AutoWarpStop(true)` from a main-thread point (the mod does it when the armed
   launch window is within a second, so a step that overshoots still fires), or add an `AutoWarpData { StopWarp = true }`.

## Complete example

[AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs) (prompt) and
[AdvancedFlightComputer/Features/Guidance/Modes/Ascent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Ascent.cs#L135-L145) (stop on launch now); the stock precedent is
`GaugeButtonWarpTo` ([current/decomp/KSA/GaugeButtonWarpTo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonWarpTo.cs#L85-L100)).

## Variations and gotchas

- The buffer path respects the stock limits and shows stock alerts; it cannot set an arbitrary speed.
- Gate your wait computation on simulation time (`GetElapsedSeconds`), but throttle per-step recomputation on wall-clock time, or it runs every step under warp.
- A narrow window can be skipped by one large warp step; stop within a margin.
- Editor sessions stop auto-warp and pause the sim in `PrepareFrame`.

## See also

- [Overview](overview.md); [direct write](set-simulation-speed-from-a-mod-1.md); [feedback controller](set-simulation-speed-from-a-mod-3.md)
- [Launch window to a target vehicle's plane, co-elliptic chase orbit and warp to the window](../../../../mods/ksa-advancedflightcomputer/features/launch-window-to-target-plane.md)
- [Build a local site frame and compute a launch window into a target plane](../../transfers/build-a-site-frame-and-compute-a-launch-window/overview.md)

[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
