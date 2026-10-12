---
type: Game Feature
title: Staging a vehicle from code, from igniting the stage to firing one decoupler
description: How the stage key activates a sequence, how decouplers split through the input buffer, and three ways a mod can stage a vehicle without racing the vehicle worker
category: vehicles-and-parts
group: staging-and-engines
topic: stage-a-vehicle-from-code
demonstrated_by:
  - mods/gatos/features/ignition-staging-and-module-actuators
  - mods/ksarmory/features/prepareworker-prefix-command-window
  - mods/ksarmory/features/launcher-separation-at-decoupler
lifecycle: [frame:input, frame:dispatch-solvers, frame:draw-ui]
game_types:
  - KSA.SequenceList
  - KSA.Sequence
  - KSA.Decoupler
  - KSA.Vehicle
  - KSA.InputEvents
  - KSA.PartTree
  - KSA.DockingPort
tags: [staging, decoupler, sequence, input-buffer, ignition, undock]
---
Staging in KSA is two different things that a mod can trigger separately: **activating the next sequence** (which lights the
engines and fires the decouplers whose sequence number matches) and **firing one decoupler** directly. Both end in
`Vehicle.Split`, which restructures the part tree and spawns a vehicle, so when and where you call them matters as much as
what you call.

## Engine background

- **The stage key.** The vehicle's key handler calls `Parts.SequenceList.ActivateNextSequence(this)` directly
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3564)[^ksa-vehicle]. Key events fire from `Glfw.PollEvents` in `frame:input`, a point where no vehicle worker is running.
- **Activating a sequence.** `ActivateNextSequence` finds the first sequence that is not activated and has parts (marking
  empty ones activated on the way), sets `ActiveSequence`, walks that sequence's parts in reverse calling
  `Part.ActivateSubtreeInStage(vehicle, number)`, rebuilds caches if the list went dirty and removes spent sequences
  [current/decomp/KSA/SequenceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequenceList.cs#L171-L206)[^ksa-sequencelist]. A part holding modules in two different sequences needs two presses (gatOS notes).
- **Igniting.** Engine ignition is independent of the sequence: `Vehicle.SetEnum(VehicleEngine.MainIgnite)` sets the manual
  `EngineOn` input, which lights the active stage's engines [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6410-L6420).
- **Decouplers split through a buffer.** `Decoupler.SetIsActive(vehicle, true)` does not split; it queues an
  `IActivateInputData{Operation = Decouple}` into `InputEvents.IActivateInputBuffer`
  [current/decomp/KSA/Decoupler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Decoupler.cs#L149-L161)[^ksa-decoupler]. `InputEvents.ApplyInputEvents` (called in `frame:input` after the key handlers) applies the
  batch, calling `Decoupler.Decouple`, which runs `Vehicle.Split` [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1114-L1140)[^ksa-inputevents]
  [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L456-L466) [current/decomp/KSA/Decoupler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Decoupler.cs#L163-L172). A mod that calls `SetIsActive` from `frame:draw-ui` therefore
  splits the vehicle at the start of the next frame.
- **Undocking** uses a sibling buffer, `InputEvents.VehicleDockingInputBuffer`, which `ApplyInputEvents` applies first.
- **The worker window.** `RecomputeForFlight` on `PerformanceSequences` walks the live `SequenceList` on the worker for the
  controlled vehicle while the `Sequence` or `EngineControl` gauge canvas is enabled (both start enabled, so by default on every step) [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L793-L796)[^ksa-physicsbubble], so mutating
  the sequence list while a worker step runs can race it (article 2).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: `ActivateNextSequence` plus tree refresh, `SetEnum(MainIgnite)`, decoupler and undock buffers](stage-a-vehicle-from-code-1.md) | gatOS | `command-queue`, `input-event-buffer` | any main-thread point | smallest code; the stage call itself is immediate and unguarded |
| [2: stage in the `PrepareWorker` prefix window](stage-a-vehicle-from-code-2.md) | KSArmory | `prepareworker-window` | `frame:dispatch-solvers` | race-free; needs a Harmony prefix and a queue |
| [3: fire only the decoupler on one joint](stage-a-vehicle-from-code-3.md) | KSArmory | `input-buffer-activation`, `latch-on-request` | `frame:draw-ui` then `frame:input` | never touches the player's staging; split lands next frame |

## See also

- [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md)
- Automatic staging driven by thresholds: `stage-automatically`
- Keeping references valid across the split: `anchor-to-parts-across-vehicle-changes`
- [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md)
- Mod features: [gatos/ignition-staging-and-module-actuators](../../../../mods/gatos/features/ignition-staging-and-module-actuators.md), [ksarmory/prepareworker-prefix-command-window](../../../../mods/ksarmory/features/prepareworker-prefix-command-window.md), [ksarmory/launcher-separation-at-decoupler](../../../../mods/ksarmory/features/launcher-separation-at-decoupler.md)
- [Staging automatically when engines burn out, spent stages ride along, or a delay is wanted](../stage-automatically/overview.md) - related topic
- [Keeping an anchor on a part across staging, docking, editor changes and saves](../../part-geometry/anchor-to-parts-across-vehicle-changes/overview.md) - related topic

[^ksa-sequencelist]: current/decomp/KSA/SequenceList.cs (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: current/decomp/KSA/InputEvents.cs (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: current/decomp/KSA/PhysicsBubble.cs (decompiled, build 2026.10.10.5554)
[^ksa-decoupler]: current/decomp/KSA/Decoupler.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
