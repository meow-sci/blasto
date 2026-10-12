---
type: Mod Feature
title: Launch window to a target vehicle's plane, co-elliptic chase orbit and warp to the window
description: Derive the target's plane from its state vectors, time the next ascending or descending crossing of the launch site with a lead for the ascent, aim a co-elliptic orbit below the target, and request the warp through the game's InputEvents.AutoWarpBuffer
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [input-buffer, orbital-geometry]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker, starmap StarMapAfterGui]
lifecycle: [frame:dispatch-solvers, frame:draw-ui, frame:input]
game_types: [KSA.Orbit, KSA.StateVectors, KSA.IParentBody, KSA.InputEvents, KSA.Universe, KSA.Vehicle]
tags: [launch-window, rendezvous, ascent, time-warp, inclination, lan, guidance]
---

Picking a target vehicle on the Ascent tab turns the ascent into a rendezvous launch: AFC computes the
orbit to fly (co-elliptic with the target, a chosen offset below it), the time until the launch site
passes under the target's plane, offers to warp there, and fires EXECUTE itself when the window arrives.
The geometry is plain vector math on the target's CCI state; the timing and warp use the game's own
frame plumbing ([AdvancedFlightComputer/Features/Guidance/Modes/ChaseOrbit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/ChaseOrbit.cs)).

## How it works

### Plane and chase orbit (`TryChaseOrbit`)

| Step | Detail |
|---|---|
| Status checks | No target, target id not found, target orbits another body (`Orbit.Parent` differs), plane unreachable |
| Target plane | `n = normalize(r x v)` from `targetOrbit.StateVectors`; `i = acos(n.z)`, `LAN = atan2(n.x, -n.y)`; an exactly equatorial plane gets LAN 0, matching where KSA's own elements put the node |
| Chase orbit | Same eccentricity and argument of periapsis as the target, semi-major axis lowered by the player's offset, so it stays inside an eccentric target all the way round |
| Reachability | `|tan(lat) / tan(i)| > 1` means the site never passes under the plane (`PlaneUnreachable`; the orbit is still filled in) |
| Window | Site right ascension from the vehicle's CCI position; required RA is `LAN + delta` (ascending) or `LAN + pi - delta` (descending), `delta = asin(tan(lat)/tan(i))`; wait = wrapped difference / `IParentBody.GetAngularVelocity()` |
| Launch lead | The site RA is advanced by `LanLeadSeconds = 180` before the wrap, because the pad keeps moving east through the vertical rise and pitch-over; ignition in the plane would leave the ascent behind it ([AdvancedFlightComputer/Features/Guidance/Modes/Ascent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Ascent.cs#L47-L58)) |
| No lost revolution | Inside the lead window the wrap would report the next day's launch; that case returns 0 (go now, at most 180 s late) |

`ApplyChaseOrbit` writes the result into the ascent inputs (inclination, LAN, Pe/Ap, optionally argument
of periapsis), which the panel then shows greyed out because they are outputs of the target pick. The
node direction is frozen once a launch is armed, so the other node becoming "next" cannot turn the ascent
round.

### Armed launch and warp (`StepLaunchWindow`)

- Not armed: every 250 ms of wall-clock time (`GuidanceClock`), re-run `TryChaseOrbit` and latch an
  absolute `LaunchTargetTime = now + WaitSec` ([AdvancedFlightComputer/Features/Guidance/Modes/Ascent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Ascent.cs#L212-L264)).
- Armed: compare the latched time with `Universe.GetElapsedSeconds()` every step; at `<= 1 s` (so a warp
  step that overshoots still fires) stop auto-warp with `Universe.AutoWarpStop(true)` and start guidance;
  otherwise, with auto-launch on, ask to warp to 10 s before the window.
- The warp itself needs the player's confirmation in the panel. The Warp button does not call
  `Universe.AutoWarpTo`; it appends an `InputEvents.AutoWarpData` to `InputEvents.AutoWarpBuffer`, which
  `InputEvents.ApplyInputEvents` drains inside `Program.PrepareFrame` the way stock's warp gauge button and
  burn context menu do ([AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs#L65-L106),
  [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L854-L875)).[^ksa-inputevents]
- "Launch now" next to the prompt launches off-window to the nearest crossing; UPFG steers out the plane
  difference at a dV cost.

## Lifecycle and timing

| Part | Where it runs |
|---|---|
| `StepLaunchWindow` (tracking and firing) | Guidance step, `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`), called from `ApplyAutopilot` for the focused vehicle even when the panel is closed ([AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs#L1017-L1018)) |
| Window readout, warp prompt, Launch now | Panel draw in `StarMapAfterGui` (`frame:draw-ui`) |
| Warp request | Applied next frame in `InputEvents.ApplyInputEvents` (`frame:input`, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2181)) |

The author's comment on the buffer explains the choice: the UI draw overlaps the running vehicle solver,
and the input buffers are applied in `PrepareFrame` while the solvers are joined, so changing the warp
there cannot race a physics step ([current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1114-L1132)).
The launch fires from the simulation step rather than the draw so it works with the HUD hidden.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/ChaseOrbit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/ChaseOrbit.cs) | Plane, chase orbit, launch window |
| [AdvancedFlightComputer/Features/Guidance/Modes/Ascent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Ascent.cs) | Arming, `StepLaunchWindow`, launch now, LAN lead |
| [AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs) | Warp prompt through `InputEvents.AutoWarpBuffer` |
| [AdvancedFlightComputer/Features/Guidance/Upfg/UpfgTarget.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Upfg/UpfgTarget.cs) | `ArgumentOfPeriapsisOf(r, v, mu, lan)` |
| [AdvancedFlightComputer/Features/Guidance/Ui/Gauges/AscentGauge.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Gauges/AscentGauge.cs) | T-minus readout |

## Snippets

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/ChaseOrbit.cs L85-L117
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
        // ...
        double raNow = ra + omega * LanLeadSeconds;
        double period = 2.0 * Math.PI / omega;

        double WaitFor(bool descending)
        {
            double raRequired = descending ? lanT + Math.PI - delta : lanT + delta;
            double wait = Wrap2Pi(raRequired - raNow) / omega;
```

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

## Game APIs touched

| Member | Use |
|---|---|
| `Orbit.StateVectors`, `Parent`, `Periapsis`, `Apoapsis` ([current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1185)) | Target plane and shape; launch site position |
| `IParentBody.GetAngularVelocity()` ([current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L78)) | Rotation rate that carries the site to the plane |
| `InputEvents.AutoWarpBuffer`, `AutoWarpData` (`StopWarp`, `WarpToTime`, `SimTimeMargin`, `IsUncapped`), `ApplyInputEvents` ([current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1012)) | Deferred, frame-safe warp request |
| `Universe.AutoWarpTo`, `AutoWarpStop`, `IsAutoWarpActive`, `GetElapsedSeconds`, `GetElapsedTime` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2246)) | Warp control and simulation clock |
| `GaugeButtonWarpTo` ([current/decomp/KSA/GaugeButtonWarpTo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonWarpTo.cs#L85-L100)) | Stock precedent for warping through the buffer |

## Pitfalls and version notes

- Latch the launch time as an absolute simulation time and compare with `<=`; a relative countdown or a
  narrow window is skipped by a single large warp step.
- Recomputing the window every frame is wasteful (`TryChaseOrbit` searches the system for the target id), and
  gating it on simulation time would make it run every step under warp; AFC gates on wall-clock time.
- `AutoWarpStop(true)` is called directly from the guidance step (inside the `PrepareWorker` prefix, a
  main-thread point between solver apply and dispatch); only the UI-originated warp goes through the buffer.
- The LAN convention (`Normal = (sin i sin LAN, -sin i cos LAN, cos i)`) is AFC's and is stated in the code;
  check it before mixing with other element routines.

## See also

- [UPFG ascent guidance](upfg-ascent-guidance.md)
- [Minimum-propellant convex ascent](convex-ascent-scvx.md)
- [Guidance vehicle step loop](guidance-vehicle-step-loop.md)
- [Guidance panel and overlays](guidance-panel-and-overlays.md)
- [Request a warp-to-time through InputEvents.AutoWarpBuffer](../../../howto/flight-and-orbits/flight-state/set-simulation-speed-from-a-mod/set-simulation-speed-from-a-mod-2.md) - how-to article demonstrating this feature
- [Find the launch window into a target plane from site latitude, inclination and a pad lead](../../../howto/flight-and-orbits/transfers/build-a-site-frame-and-compute-a-launch-window/build-a-site-frame-and-compute-a-launch-window-1.md) - how-to article demonstrating this feature

[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
