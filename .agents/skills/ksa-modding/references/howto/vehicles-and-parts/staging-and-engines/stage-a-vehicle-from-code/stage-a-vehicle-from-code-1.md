---
type: How-To
title: Stage, ignite, fire decouplers and undock by calling the game's own entry points
description: Call SequenceList.ActivateNextSequence, Vehicle.SetEnum(MainIgnite), Decoupler.SetIsActive and the docking input buffer from the main thread, the way gatOS exposes them as commands
category: vehicles-and-parts
group: staging-and-engines
topic: stage-a-vehicle-from-code
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/ignition-staging-and-module-actuators]
techniques: [command-queue, input-event-buffer]
hooks: [starmap StarMapBeforeGui]
lifecycle: [frame:input, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.SequenceList, KSA.Decoupler, KSA.DockingPort, KSA.InputEvents, KSA.VehicleEngine]
tags: [staging, ignition, decoupler, undock, input-buffer]
---
Outcome: from mod code you can ignite the main engines, advance the stage, fire a single decoupler and undock a port, using
exactly the calls the game's own keys and context menus use. gatOS wraps each in a small actuator that runs on the game
thread. Prerequisite: StarMap. Demonstrated by [gatos/ignition-staging-and-module-actuators](../../../../mods/gatos/features/ignition-staging-and-module-actuators.md).

## Background: how the game does it

See the [overview](overview.md). Of the four calls, only `ActivateNextSequence` and `SetEnum` act immediately; decoupling
and undocking are deferred through `InputEvents` buffers that the game applies in `frame:input`.

## Lifecycle and timing

Run these on the main thread, never from the physics worker. The deferred calls (`Decoupler.SetIsActive`, the docking
buffer) are safe from `frame:draw-ui` because the split happens next frame in `InputEvents.ApplyInputEvents`. The immediate
call, `ActivateNextSequence`, mutates the sequence list on the spot; gatOS calls it from a frame hook with no guard against the worker (the in-game key does it in `frame:input`, where no worker is running). A StarMap hook runs while the step
dispatched by `PrepareFrame` is in flight, and with the default UI the `Sequence` and `EngineControl` gauge canvases are enabled, so for the controlled vehicle the vehicle-solver worker walks the live `SequenceList` (`PerformanceSequences.RecomputeForFlight`
in `PhysicsBubble.RunVehiclePostWorkInner`) on every step: the unguarded call overlaps that walk. If you cannot tolerate that window, use [article 2](stage-a-vehicle-from-code-2.md). See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Light the engines.** `SetEnum` with `VehicleEngine.MainIgnite` sets the manual `EngineOn` input:

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/EngineActuator.cs L14-21
    [KsaAnchor("Vehicle.SetEnum(VehicleEngine.MainIgnite)", SourceFile = "KSA/Vehicle.cs",
        Verified = "2026-06-12", Risk = ChurnRisk.Medium,
        Notes = "Sets _manualControlInputs.EngineOn = true (ignites the active stage's engines).")]
    internal static CommandResult Ignite(Vehicle vehicle)
    {
        vehicle.SetEnum(VehicleEngine.MainIgnite);
        return CommandResult.Ok;
    }
```

2. **Stage.** The same call the stage key makes, then refresh the part tree so derived data (the sequence list, masses) match
   what the activation changed:

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/StagingActuator.cs L27-32
    internal static CommandResult Stage(Vehicle vehicle)
    {
        vehicle.Parts.SequenceList.ActivateNextSequence(vehicle);
        vehicle.UpdateAfterPartTreeModification();
        return CommandResult.Ok;
    }
```

3. **Fire one decoupler.** Check `IsActive` (a decoupler fires once and KSA ignores a re-fire) and `IsEnabled` (a player-disabled
   module is ignored too), then call `SetIsActive`; the split follows next frame:

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/DecouplerActuator.cs L27-40
    internal static CommandResult Fire(Vehicle vehicle, int ordinal)
    {
        var decouplers = vehicle.Parts.Modules.Get<Decoupler>();
        if (ordinal < 0 || ordinal >= decouplers.Length)
            return new CommandResult(CommandOutcome.NotFound, $"decoupler {ordinal} does not exist");
        var decoupler = decouplers[ordinal];
        if (decoupler.IsActive)
            return new CommandResult(CommandOutcome.Busy, $"decoupler {ordinal} already fired");
        if (!decoupler.IsEnabled)
            return new CommandResult(CommandOutcome.Unsupported,
                $"decoupler {ordinal} is disabled on its part and cannot fire");
        decoupler.SetIsActive(vehicle, true);
        return CommandResult.Ok;
    }
```

4. **Undock a port.** Enqueue the same input data the context menu does; `Undock` itself only enqueues and `Vehicle.Split`
   happens when `ApplyInputEvents` runs:

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/DockingActuator.cs L33-48
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

5. Expect the vehicle count to change one frame later (a decoupler or undock spawns a vehicle). Do not keep using the old
   `Part` references past that; see `anchor-to-parts-across-vehicle-changes`.

## Complete example

- [gatOS.GameMod/Game/Ksa/Actuators/StagingActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/StagingActuator.cs)[^gatos-stagingactuator]
- [gatOS.GameMod/Game/Ksa/Actuators/EngineActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/EngineActuator.cs)[^gatos-engineactuator]
- [gatOS.GameMod/Game/Ksa/Actuators/DecouplerActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/DecouplerActuator.cs)[^gatos-decoupleractuator]
- [gatOS.GameMod/Game/Ksa/Actuators/DockingActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/DockingActuator.cs)[^gatos-dockingactuator]

## Variations and gotchas

- **Several sequences per part.** Since `ActivateNextSequence` walks `ActivateSubtreeInStage(vehicle, number)`, only modules
  whose own sequence matches fire; a part with modules in two sequences needs two stage calls (gatOS anchor notes).
- **Parachutes now stage.** gatOS records that parachute deploy and cut modules are sequenced and fire from a stage through
  the same input buffer.
- **Disabled and fired decouplers** are silent no-ops in the game, so check first or you will report success for nothing.
- **Anchors.** gatOS pins each call with a `KsaAnchor` attribute recording the verified game build; re-verify after updates.

## See also

- [Overview](overview.md), [Article 2](stage-a-vehicle-from-code-2.md), [Article 3](stage-a-vehicle-from-code-3.md)
- [gatos/ignition-staging-and-module-actuators](../../../../mods/gatos/features/ignition-staging-and-module-actuators.md)
- `stage-automatically`, [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md)

[^gatos-engineactuator]: gatOS: gatOS.GameMod/Game/Ksa/Actuators/EngineActuator.cs
[^gatos-decoupleractuator]: gatOS: gatOS.GameMod/Game/Ksa/Actuators/DecouplerActuator.cs
[^gatos-dockingactuator]: gatOS: gatOS.GameMod/Game/Ksa/Actuators/DockingActuator.cs
[^gatos-stagingactuator]: gatOS: gatOS.GameMod/Game/Ksa/Actuators/StagingActuator.cs
