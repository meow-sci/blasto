---
type: Game Feature
title: Detect flight events when the game has no event bus
description: Derive liftoff, staging, flameout, SOI change, docking and similar events by diffing consecutive samples with allocation-free edge detection, and deliver them to consumers as an ordered stream
category: external-io
group: telemetry
topic: detect-flight-events-from-snapshots
demonstrated_by:
  - mods/gatos/features/snapshot-diff-flight-events
  - mods/gatos/features/telemetry-sampler-snapshot
  - mods/ksa-public-telemetry/features/flight-event-detection-and-callouts
lifecycle: [frame:viewports, frame:draw-ui, frame:after-on-frame, thread:background]
game_types: [KSA.Vehicle, KSA.Universe]
tags: [events, edge-detection, snapshot-diff, debounce, staging, flameout]
---

KSA does not publish "engine 0 flamed out", "the vehicle entered the Moon's SOI" or "stage separated" as events a mod can subscribe to. A mod that wants them (a script that blocks until a flameout, a broadcast overlay that shows a MECO banner) has to derive them: sample the relevant state, keep the previous sample, and report an event whenever a value crosses an edge.

The two approaches here differ in what they compare and how they stay stable. gatOS diffs complete immutable snapshots, field by field, and gives every consumer the same ordered event list. KSA-Public-Telemetry diffs a handful of counters (burning engines, part count, vehicle count) and applies debounces and per-vehicle baselines because a flickering count must not repeat a callout.

## Engine background

| Type | Use | File |
|---|---|---|
| `Vehicle.Situation` | The text behind `situation-change`; `Situation.HasAnyContact()` and `IsOnRails()` give contact and rails flags | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L549-L551) |
| `EngineControllerState.IsPropellantAvailable` | The flameout signal (an active engine loses its propellant) | [current/decomp/KSA/EngineControllerState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineControllerState.cs#L1-L25) |
| `Universe.CurrentSystem.All` | Vehicle roster for appeared, removed and mission-vehicle counts | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L64-L64) |
| `PartTree.Count`, `ModuleList.Get<Decoupler>()` | Part count and decoupler state for staging heuristics | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L134-L134) |

Timing: both approaches detect during the sampling hook, on the state applied by `Universe.ApplyVehicleSolvers` earlier in the same `PrepareFrame` (see [the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)). Any split, decoupling or destruction the game performed earlier in the frame is already reflected, so the detector does not need to know which phase did it. Resolution is the sample rate: an edge that flips and flips back between samples is missed, and at high time warp an event can be stamped up to one frame late in universe time.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: EventDiffer over consecutive immutable SimSnapshots](detect-flight-events-from-snapshots-1.md) | gatos | `edge-detection` | `frame:viewports`, `thread:background` | Complete, testable, one list for every transport; needs the snapshot pipeline and misses sub-sample transients |
| [2: debounced heuristics on burning-engine and part counts](detect-flight-events-from-snapshots-2.md) | ksa-public-telemetry | `edge-detection` | `frame:after-on-frame`, `frame:draw-ui` | Cheap and fits a UI-only mod; heuristic and "not totally reliable", needs baselines per vehicle |

## See also

- [Export live game state to background threads](../export-game-state-to-other-threads/overview.md): the snapshot pipeline approach 1 builds on
- [Connect a VM guest to the game with slirp TCP, a 9P filesystem and pinned SSH](../../processes/host-guest-transports-tcp-9p-and-ssh/overview.md): the blocking events file
- [Compute mission elapsed time and detect liftoff](../../../flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/overview.md)
- Tracking and classifying craft is covered in the flight-and-orbits category.
- [Tracking which craft carry your part, classifying them and detecting staging and loss](../../../vehicles-and-parts/craft-tracking/track-and-classify-craft/overview.md) - related topic

