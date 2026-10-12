---
type: How-To
title: Find the launch window into a target plane from site latitude, inclination and a pad lead
description: Compute the target plane from a vehicle's state vectors, a co-elliptic chase orbit, and the time until the rotating launch site passes under that plane, then request a warp to it through the game's auto-warp buffer
category: flight-and-orbits
group: transfers
topic: build-a-site-frame-and-compute-a-launch-window
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/launch-window-to-target-plane]
techniques: [orbital-geometry, input-buffer]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker, starmap StarMapAfterGui]
lifecycle: [frame:dispatch-solvers, frame:draw-ui, frame:input]
game_types: [KSA.Orbit, KSA.StateVectors, KSA.IParentBody, KSA.InputEvents, KSA.Universe, KSA.Vehicle]
tags: [launch-window, plane, right-ascension, chase-orbit, warp, rendezvous, lan]
---

Outcome: pick a target vehicle on the ascent tab and the mod computes the orbit to fly (co-elliptic with the target, a chosen offset below it), the time until the launch site passes under the target's plane, offers to warp there, and fires the launch itself when the window arrives.
KSA-AdvancedFlightComputer implements it.[Launch window to a target vehicle's plane, co-elliptic chase orbit and warp to the window](../../../../mods/ksa-advancedflightcomputer/features/launch-window-to-target-plane.md) Prerequisites: StarMap and Harmony.

## Background: how the game does it

The geometry is plain vector math on CCI state, with the pole at CCI +Z (see the [overview](overview.md)). The site's right ascension comes from its CCI position, advancing at `IParentBody.GetAngularVelocity()`; the plane's node is the ascending or descending crossing where the site's latitude equals the plane's reach. A same-body check is needed: `Orbit.Parent` must be the same object.[^ksa-iparentbody] [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L78)

## Lifecycle and timing

| Part | Where it runs |
|---|---|
| `StepLaunchWindow` (tracking and firing) | the guidance step, `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`), for the focused vehicle even when the panel is closed |
| Window readout, warp prompt, Launch now | panel draw in `StarMapAfterGui` (`frame:draw-ui`) |
| Warp request | applied next frame in `InputEvents.ApplyInputEvents` (`frame:input`) |

The launch fires from the simulation step rather than the draw so it works with the HUD hidden. The warp goes through the input buffer because the UI draw overlaps the running vehicle solver, while the input buffers are applied in `PrepareFrame` with the solvers joined (see [Set, limit and request simulation speed from a mod](../../flight-state/set-simulation-speed-from-a-mod/overview.md), approach 2). See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Target plane from state vectors:** `n = normalize(r x v)`, `i = acos(n.z)`, `LAN = atan2(n.x, -n.y)`; an exactly equatorial plane has no node, so use LAN 0 (where KSA's own elements put the node on +X). Reject a target that orbits another body or whose plane is unreachable.
2. **Chase orbit.** Keep the target's eccentricity and argument of periapsis, lower the semi-major axis by the offset, so it stays inside an eccentric target all the way round.
3. **Reachability:** `|tan(lat) / tan(i)| > 1` means the site never passes under the plane (fill the orbit in anyway).
4. **Window.** Site right ascension from the vehicle's CCI position; required RA is `LAN + delta` (ascending) or `LAN + pi - delta` (descending), `delta = asin(tan(lat)/tan(i))`; wait = wrapped difference / angular velocity. Advance the site RA by a lead before the wrap (180 s in the mod) because the pad keeps moving east through the vertical rise and pitch-over, and return 0 inside the lead window so a wrap does not cost a revolution:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/ChaseOrbit.cs L85-L117, L121-L129
// Launch window: how long until the body's rotation carries the launch site under the target plane, at whichever crossing - ascending or descending - comes first.
// Either puts the vehicle in the same plane; they differ only in heading.
double3 r = orbit.StateVectors.PositionCci;
double lat = Math.Asin(Math.Clamp(r.Z / r.Length(), -1.0, 1.0));
double ra = Math.Atan2(r.Y, r.X);
double tanRatio = Math.Tan(lat) / Math.Tan(Math.Max(incT, 1e-6));
if (Math.Abs(tanRatio) > 1.0)
{
    plan.WaitSec = double.NaN;
    return ChaseStatus.PlaneUnreachable;
}

double delta = Math.Asin(Math.Clamp(tanRatio, -1.0, 1.0));
double omega = parent.GetAngularVelocity();

// LAUNCH EARLY, by the same lead the LAN seeding uses (see LanLeadSeconds).
// The instant the site is in the plane is the wrong instant to LIGHT THE ENGINE: the pad goes on being carried east through the vertical rise and the turn, so a vehicle that ignites in the plane is already out of it by the time it is flying, and the guidance yaws to chase a node it has gone past.
// What we want is the launch time whose plane crossing lands in the middle of that, i.e. solve for T where the site's right ascension T + lead from now is the one the plane needs.
//
// The lead goes INSIDE the wrap, not subtracted after it: subtracting after would turn a window two minutes out into a negative countdown.
if (omega <= 1e-12)
{
    plan.WaitSec = double.NaN;
    return ChaseStatus.Ok;
}

double raNow = ra + omega * LanLeadSeconds;
double period = 2.0 * Math.PI / omega;

double WaitFor(bool descending)
{
    double raRequired = descending ? lanT + Math.PI - delta : lanT + delta;
    double wait = Wrap2Pi(raRequired - raNow) / omega;
// ...
    // Inside the lead window - the ideal ignition is behind us but the plane crossing itself is still ahead - the wrap reports the NEXT revolution's launch, so pressing EXECUTE armed a countdown of most of a day and looked like a dead button.
    // Going now is at most LanLeadSeconds late, which only means the crossing lands earlier in the ascent than the lead intends; waiting a revolution for that is absurd.
    if (wait > period - LanLeadSeconds)
        wait = 0.0;
    return wait;
}

double waitAscending = WaitFor(false);
double waitDescending = WaitFor(true);
```

5. **Arm and track.** Not armed: every 250 ms of wall-clock time re-run the plan and latch an absolute `LaunchTargetTime = now + WaitSec`. Armed: compare with `Universe.GetElapsedSeconds()` every step; at `<= 1 s` (so a warp step that overshoots still fires) stop auto-warp with `Universe.AutoWarpStop(true)` and start guidance; otherwise ask to warp to 10 s before the window. Freeze the node direction once armed so the other node becoming "next" cannot turn the ascent round.
6. **Request the warp through the buffer,** as the stock warp gauge does:

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

7. **Offer "Launch now"** next to the prompt: it launches off-window to the nearest crossing and UPFG steers out the plane difference at a delta-v cost.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/ChaseOrbit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/ChaseOrbit.cs) | plane, chase orbit, launch window |
| [AdvancedFlightComputer/Features/Guidance/Modes/Ascent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Ascent.cs) | arming, `StepLaunchWindow`, launch now, LAN lead |
| [AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs) | warp prompt through `InputEvents.AutoWarpBuffer` |
| [AdvancedFlightComputer/Features/Guidance/Upfg/UpfgTarget.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Upfg/UpfgTarget.cs) | `ArgumentOfPeriapsisOf(r, v, mu, lan)` |

## Variations and gotchas

- **Launch lead.** "The plane overhead right now" is the wrong plane: the pad is carried east at the body's rotation rate while the vehicle climbs, so seed the plane over where the pad will be.
- **Gate the periodic re-solve on wall-clock time**, not simulation time, or it runs every step under warp.
- **A narrow window can be skipped by one large warp step**; warp to a margin before it and stop within a second.
- The wait is in seconds of simulation time; convert with `Universe.GetElapsedTime()` when requesting the warp.

## See also

- [Overview](overview.md); [site frame bridge](build-a-site-frame-and-compute-a-launch-window-2.md)
- [Launch window to a target vehicle's plane, co-elliptic chase orbit and warp to the window](../../../../mods/ksa-advancedflightcomputer/features/launch-window-to-target-plane.md)
- [Set, limit and request simulation speed from a mod](../../flight-state/set-simulation-speed-from-a-mod/overview.md), [Compute phase angles, synodic periods and the time to the next transfer window](../compute-transfer-windows/overview.md)

[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
