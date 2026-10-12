---
type: Mod Feature
title: Ignition, staging and module actuators
description: Ignite, shut down, stage, toggle engines and RCS, fire decouplers, undock and drive part animations by calling the same entry points as KSA's own keybinds, most of which queue into the game's input buffers
mod: gatos
category: vehicles-and-parts
techniques: [command-queue, input-event-buffer]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapBeforeGui]
lifecycle: [frame:viewports, frame:draw-ui, frame:input]
game_types:
  - KSA.Vehicle
  - KSA.VehicleEngine
  - KSA.EngineController
  - KSA.PartTree
  - KSA.SequenceList
  - KSA.Decoupler
  - KSA.DockingPort
  - KSA.ThrusterController
  - KSA.KeyframeAnimationModule
  - KSA.InputEvents
tags: [control, engines, staging, decouplers, docking, rcs, animations, input-buffer]
---

`echo 1 > ctl/ignite`, `echo 1 > ctl/stage`, `echo 0 > engines/2/active`, `echo 1 >
decouplers/0/fire`, `echo 1 > docking/0/undock`, `echo 0.5 > animations/3/goal`: each control
file becomes a `SimCommand` and lands in a small static actuator that calls the same KSA entry
point the player's keybind or part menu uses. The interesting part for modders is which of those
entry points act immediately and which only queue an input event that KSA applies at the start
of the next frame.

## How it works

| Control | Actuator call | Takes effect |
|---|---|---|
| `ctl/ignite`, `ctl/shutdown`, `ctl/engine` | `Vehicle.SetEnum(VehicleEngine.MainIgnite / MainShutdown)`, which sets `_manualControlInputs.EngineOn` | copied into the solver at the next `PrepareWorker` |
| `ctl/stage` | `Parts.SequenceList.ActivateNextSequence(vehicle)` then `Vehicle.UpdateAfterPartTreeModification()` (mirrors the stage key) | sequence state now; engine and decoupler activations are queued (below) |
| `engines/<n>/active` | `EngineController.SetIsActive(vehicle, bool)` | queues `InputEvents.IActivateInputData` |
| `engines/<n>/min_throttle` | `EngineController.MinimumThrottle = f` then `Parts.MarkDerivedDirty(DerivedData.RocketControls)` | flushed by `PartTree.FlushDirtyDerived()` before the next dispatch |
| `ctl/rcs`, `rcs/<n>/active` | `ThrusterController.SetIsActive(vehicle, bool)` | queues `IActivateInputData` |
| `decouplers/<n>/fire` | `Decoupler.SetIsActive(vehicle, true)` after `IsActive`/`IsEnabled` checks | queues `IActivateInputData` |
| `docking/<n>/undock` | `InputEvents.VehicleDockingInputBuffer.Add(new VehicleDockingInputData { Undock = true })` | applied in the next `ApplyInputEvents` |
| `animations/<n>/goal` | `KeyframeAnimationModule.TimeGoal = f * Shared.Duration` | animation runs toward the goal |
| `debug/.../pushoff_impulse` | `DockingPort.PushoffImpulse = v` | next undock |

Every per-module actuator resolves `vehicle.Parts.Modules.Get<T>()[ordinal]` and returns
`NotFound` for a bad ordinal, `Busy` when the action cannot fire now (decoupler already fired, port
not docked) and `Unsupported` when the part disables it, which the transports turn into errno
values ([gatOS.GameMod/Game/Ksa/Actuators/DecouplerActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/DecouplerActuator.cs#L27-L40)).

**The input-buffer pattern.** `EngineController.SetIsActive`, `ThrusterController.SetIsActive`
and `Decoupler.SetIsActive` do not mutate state; they add an `IActivateInputData` to
`InputEvents.IActivateInputBuffer` ([current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L77-L87)).
`InputEvents.ApplyInputEvents()` applies `IEnableInputBuffer`, `VehicleDockingInputBuffer` and the
batched activations, and `Program.PrepareFrame` calls it right after polling GLFW
([current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1114-L1120)). gatOS's undock reuses exactly that buffer
because there is no direct public undock call ([gatOS.GameMod/Game/Ksa/Actuators/DockingActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/DockingActuator.cs#L33-L48)).

## Lifecycle and timing

- The actuators run in the Frame-phase drain (main-viewport prefix, else `StarMapBeforeGui`), after
  `PrepareFrame`, so the current step is already in flight with the previous inputs.
- Buffered activations are applied at `frame:input` of the next frame (`InputEvents.ApplyInputEvents`
  inside `PrepareFrame`), after the previous step's apply and before the next dispatch: the same
  timing as a player pressing the key, and race-free with the worker.
- `EngineOn` is a field of the `ManualControlInputs` struct that `Vehicle.PrepareWorker` copies
  into the worker state, so ignition also lands at the next dispatch
  ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423)).
- `StagingActuator.Stage` calls `UpdateAfterPartTreeModification()` directly from the Frame
  phase, which mutates `PhysicsStates` and collision geometry while the next step is running.
  gatOS's own matrix records this as a pre-existing, not yet fixed race
  ([docs/KSA_INTEGRATION_MATRIX.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/KSA_INTEGRATION_MATRIX.md#L321-L330)).

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Actuators/EngineActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/EngineActuator.cs) | ignite/shutdown, engine active, min throttle |
| [gatOS.GameMod/Game/Ksa/Actuators/StagingActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/StagingActuator.cs#L27-L32) | next stage |
| [gatOS.GameMod/Game/Ksa/Actuators/DecouplerActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/DecouplerActuator.cs) | fire one decoupler with state checks |
| [gatOS.GameMod/Game/Ksa/Actuators/DockingActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/DockingActuator.cs) | undock through the input buffer, pushoff impulse |
| [gatOS.GameMod/Game/Ksa/Actuators/RcsActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/RcsActuator.cs) | RCS master and per-controller |
| [gatOS.GameMod/Game/Ksa/Actuators/AnimationActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/AnimationActuator.cs) | animation goal fraction |

## Snippets

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/EngineActuator.cs L62-L71
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

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/DockingActuator.cs L33-L48
    internal static CommandResult Undock(Vehicle vehicle, int ordinal)
    {
        var ports = vehicle.Parts.Modules.Get<DockingPort>();
        if (ordinal < 0 || ordinal >= ports.Length)
            return new CommandResult(CommandOutcome.NotFound, $"docking port {ordinal} does not exist");
        var port = ports[ordinal];
        if (!port.Docked)
            return new CommandResult(CommandOutcome.Busy, $"docking port {ordinal} is not docked");
        InputEvents.VehicleDockingInputBuffer.Add(new InputEvents.VehicleDockingInputData
        {
            Vehicle = vehicle,
            DockingPort = port,
            Undock = true,
        });
        return CommandResult.Ok;
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Vehicle.SetEnum(Enum)` / `SetAction(VehicleEngine)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6321-L6425) | main engine on/off |
| `EngineController.SetIsActive`, `MinimumThrottle` | [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L38-L87) | per-engine activation, throttle floor |
| `PartTree.MarkDerivedDirty(DerivedData)` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L480-L490) | refresh cached `EngineThrottleMin` |
| `SequenceList.ActivateNextSequence(Vehicle)` | [current/decomp/KSA/SequenceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequenceList.cs#L171-L190) | staging |
| `Vehicle.UpdateAfterPartTreeModification()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1894-L1910) | mass and collision refresh after staging |
| `Decoupler.SetIsActive`, `IsActive`, `IsEnabled` | [current/decomp/KSA/Decoupler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Decoupler.cs#L71-L160) | fire |
| `ThrusterController.SetIsActive` | [current/decomp/KSA/ThrusterController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ThrusterController.cs#L61-L70) | RCS on/off |
| `InputEvents.VehicleDockingInputBuffer`, `IActivateInputBuffer`, `ApplyInputEvents` | [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1022-L1120) | deferred input events |
| `KeyframeAnimationModule.TimeGoal`, `Shared.Duration` | [current/decomp/KSA/KeyframeAnimationModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KeyframeAnimationModule.cs#L74-L76) | animation goal |

## Pitfalls and version notes

- Staging semantics moved at 5348 (rev 5329): `ActivateNextSequence` now walks
  `GetSubtreeSequencedModules()` and activates only `ISequenced` modules (`EngineController`,
  `Decoupler`) whose own sequence matches, so staging no longer toggles RCS and a part with modules
  in two sequences needs two presses (gatOS anchor notes).
- Since rev 5132 `Decoupler.SetIsActive` silently ignores disabled decouplers; gatOS checks
  `IsEnabled` first so the caller gets `EOPNOTSUPP` instead of a false success.
- Changing `MinimumThrottle` without `MarkDerivedDirty(RocketControls)` leaves the manual throttle
  clamp (`PartTree.EngineThrottleMin`) stale (fixed in gatOS at 5482).
- On solid motors throttle is physically inert; ignition goes through the engine surface.
- All listed members are present in 5554 with the same signatures.

## See also

- [Game-thread command queue](game-thread-command-queue.md)
- [Manual throttle and RCS inputs](manual-throttle-and-rcs-inputs.md)
- [Part and module state reads](part-module-state-reads.md)
- [Part light cone control](light-cone-control.md)
- [gatOS overview](../overview.md)
- [Switch engines on and off and set minimum throttle through KSA's input-event buffers](../../../howto/vehicles-and-parts/modules-and-resources/command-engines-throttle-and-rcs-from-code/command-engines-throttle-and-rcs-from-code-1.md) - how-to article demonstrating this feature
- [Stage, ignite, fire decouplers and undock by calling the game's own entry points](../../../howto/vehicles-and-parts/staging-and-engines/stage-a-vehicle-from-code/stage-a-vehicle-from-code-1.md) - how-to article demonstrating this feature

