---
type: How-To
title: Switch engines on and off and set minimum throttle through KSA's input-event buffers
description: Ignite with Vehicle.SetEnum, activate individual engines and RCS thrusters with SetIsActive, and set deep-throttle floors with a derived-data dirty mark
category: vehicles-and-parts
group: modules-and-resources
topic: command-engines-throttle-and-rcs-from-code
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/ignition-staging-and-module-actuators]
techniques: [input-event-buffer, starmap-lifecycle]
hooks: [harmony-prefix KSA.GameViewport.OnFrame]
lifecycle: [frame:viewports, frame:input, frame:dispatch-solvers]
game_types:
  - KSA.Vehicle
  - KSA.EngineController
  - KSA.ThrusterController
  - KSA.InputEvents
  - KSA.PartTree
  - KSA.VehicleEngine
tags: [engine, ignition, set-is-active, input-events, min-throttle, rcs]
---
Outcome: your mod ignites a vehicle, switches one engine or one RCS thruster on or off, and sets an engine's
deep-throttle floor, using the same entry points the keybinds and part menus use. gatOS exposes these as control files
(`ctl/ignite`, `engines/<n>/active`, `engines/<n>/min_throttle`); the actuators are tiny, and the interesting part is
which calls act immediately and which queue an input event for the next frame.

Prerequisite: a main-thread hook. Demonstrated by [gatos/ignition-staging-and-module-actuators](../../../../mods/gatos/features/ignition-staging-and-module-actuators.md). Staging itself is
[Ignite engines, activate the next stage, fire a decoupler or undock from code](../../staging-and-engines/stage-a-vehicle-from-code/overview.md).

## Background: how the game does it

| Call | What it does | Takes effect |
|---|---|---|
| `Vehicle.SetEnum(VehicleEngine.MainIgnite / MainShutdown)` | writes `_manualControlInputs.EngineOn` directly [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6321-L6407)[^ksa-vehicle] | copied into the worker snapshot by `PrepareWorker` at the next dispatch |
| `EngineController.SetIsActive(vehicle, bool)` | adds an `IActivateInputData` (`Operation = Engine`) to `InputEvents.IActivateInputBuffer` [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L77-L87)[^ksa-enginecontroller] | `ApplyInputEvents` of the next `PrepareFrame` [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1046-L1055)[^ksa-inputevents] |
| `ThrusterController.SetIsActive`, `Decoupler.SetIsActive` | the same buffer, other `ActivateOp`s | same |
| `EngineController.MinimumThrottle = f` | public field; the manual-throttle clamp reads `PartTree.EngineThrottleMin`, a cached minimum recomputed on `RocketControls` dirty | flushed by `PartTree.FlushDirtyDerived()` before the next dispatch |

`IActivateInputData.Apply` sets `EngineController.IsActive = ActivationState` (or the thruster's), and **for a non-null
`Vehicle` also queues a flight-computer reset** for that vehicle (`VehiclesToResetFlightComputer`)
[current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L434-L495). Passing `null` for the vehicle skips the reset, which is what bulk animators want
(article 3).

## Lifecycle and timing

- gatOS runs the actuators in its frame-phase drain, from a prefix on the main viewport's `GameViewport.OnFrame`
  (`frame:viewports`), after `PrepareFrame`: the current step is already in flight with the old inputs.
- Buffered activations are applied at `frame:input` of the **next** frame, after the previous step's apply and before
  the next dispatch: the same timing as a player pressing the key, and race-free with the worker
  [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1113-L1140).
- `EngineOn` is a field of the struct `Vehicle.PrepareWorker` copies at `ExecuteNextVehicleSolvers`, so ignition also
  lands at the next dispatch (`frame:dispatch-solvers`) [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423).

See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) for the window and [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md).

## Steps

1. **Ignite and shut down** with `SetEnum`; the vehicle needs to be ignited and throttled up for engines to fire:

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/EngineActuator.cs L17-21
    internal static CommandResult Ignite(Vehicle vehicle)
    {
        vehicle.SetEnum(VehicleEngine.MainIgnite);
        return CommandResult.Ok;
    }
```

2. **Activate one engine** by its ordinal in `Modules.Get<EngineController>()` (the index shown by
   [Read live part module state: engines, tanks, propellant, batteries, panels, docking ports](../read-live-part-module-state/overview.md)), returning "not found" for a bad ordinal:

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/EngineActuator.cs L38-45
    internal static CommandResult SetActive(Vehicle vehicle, int ordinal, bool active)
    {
        var engines = vehicle.Parts.Modules.Get<EngineController>();
        if (ordinal < 0 || ordinal >= engines.Length)
            return new CommandResult(CommandOutcome.NotFound, $"engine {ordinal} does not exist");
        engines[ordinal].SetIsActive(vehicle, active);
        return CommandResult.Ok;
    }
```

3. **Set the deep-throttle floor**, then mark the cached tree-wide floor dirty so the manual-throttle clamp sees it:

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/EngineActuator.cs L62-71
    internal static CommandResult SetMinThrottle(Vehicle vehicle, int ordinal, double fraction)
    {
        var engines = vehicle.Parts.Modules.Get<EngineController>();
        if (ordinal < 0 || ordinal >= engines.Length)
            return new CommandResult(CommandOutcome.NotFound, $"engine {ordinal} does not exist");
        engines[ordinal].MinimumThrottle = (float)Math.Clamp(fraction, 0, 1);
        // Refresh the tree-wide floor (PartTree.EngineThrottleMin) the manual-throttle clamp reads.
        vehicle.Parts.MarkDerivedDirty(DerivedData.RocketControls);
        return CommandResult.Ok;
    }
```

4. **Do not call `SetIsActive` from a worker thread**, and expect no immediate effect: read the engine back next frame.
5. **Reuse the buffer** for things KSA has no direct call for, as gatOS does for undocking:
   `InputEvents.VehicleDockingInputBuffer.Add(new VehicleDockingInputData { Undock = true })` (applied in the next
   `ApplyInputEvents`).

## Complete example

- [gatOS.GameMod/Game/Ksa/Actuators/EngineActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/EngineActuator.cs)[^gatos-engineactuator] - ignite, activate, min throttle
- [gatOS.GameMod/Game/Ksa/Actuators/DockingActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/DockingActuator.cs)[^gatos-dockingactuator] - the docking buffer

## Variations and gotchas

- **A bad or stale ordinal.** Ordinals shift when the part tree changes; resolve the module by ordinal each call and
  return an error rather than caching it across edits.
- **Multi-engine floor.** The effective floor is a cached minimum over every engine's `MinimumThrottle`; changing one
  engine changes it only after the dirty flush.
- **Staging from here.** gatOS's staging actuator calls `UpdateAfterPartTreeModification()` directly from the frame
  phase, which its own notes record as a pre-existing race with the running step; prefer
  [Ignite engines, activate the next stage, fire a decoupler or undock from code](../../staging-and-engines/stage-a-vehicle-from-code/overview.md) approach 1 or 2.
- **Updates.** All calls are public API; the buffer types can change shape between builds.

## See also

- [Overview](overview.md), [Article 2](command-engines-throttle-and-rcs-from-code-2.md), [Article 3](command-engines-throttle-and-rcs-from-code-3.md)
- [gatos/ignition-staging-and-module-actuators](../../../../mods/gatos/features/ignition-staging-and-module-actuators.md)
- [Ignite engines, activate the next stage, fire a decoupler or undock from code](../../staging-and-engines/stage-a-vehicle-from-code/overview.md), [Create and attach parts to a live vessel and feed runtime engines from tanks](../add-parts-to-a-live-vessel/overview.md)

[^ksa-inputevents]: current/decomp/KSA/InputEvents.cs (decompiled, build 2026.10.10.5554)
[^ksa-enginecontroller]: current/decomp/KSA/EngineController.cs (decompiled, build 2026.10.10.5554)
[^gatos-dockingactuator]: gatOS: gatOS.GameMod/Game/Ksa/Actuators/DockingActuator.cs
[^gatos-engineactuator]: gatOS: gatOS.GameMod/Game/Ksa/Actuators/EngineActuator.cs
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
