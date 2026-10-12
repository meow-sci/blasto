---
type: How-To
title: Refuel on demand from a command queue drained in the solver window
description: Queue a one-shot refuel or battery refill from a menu, file write or network call and execute it in a prefix on Universe.ExecuteNextVehicleSolvers
category: vehicles-and-parts
group: modules-and-resources
topic: refill-or-drain-resources
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/gatos/features/debug-cheats-teleport-refuel-warp]
techniques: [command-queue, harmony-prefix]
hooks: [harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers]
lifecycle: [frame:dispatch-solvers, frame:viewports]
game_types: [KSA.Vehicle, KSA.Battery, KSA.PartTree, KSA.Universe, KSA.ModuleStateful]
tags: [refuel, cheat, command-queue, debug, batteries]
---
Outcome: a scenario script, tutorial or agent can say "top up this vessel" at any moment, and the refill lands in a
window where it holds. gatOS exposes `debug.refill_fuel` and `debug.refill_battery` over files, HTTP, MQTT and MCP;
the actuators are four lines each, and the interesting part is the queue that decides *when* they run.

Prerequisites: StarMap, Harmony. Demonstrated by [gatos/debug-cheats-teleport-refuel-warp](../../../../mods/gatos/features/debug-cheats-teleport-refuel-warp.md). The continuous version
is [article 1](refill-or-drain-resources-1.md).

## Background: how the game does it

- `Vehicle.RefillConsumables()` and `Battery.Refill(ref state)` are the same calls as in article 1
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3201-L3206)[^ksa-vehicle] [current/decomp/KSA/Battery.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Battery.cs#L63-L66)[^ksa-battery]. Since an update in build 5482, `RefillAllTanks` raises the
  `ValuesUpdated` flag so an engine that had run dry re-lights (gatOS recorded this) [current/decomp/KSA/ResourceManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ResourceManager.cs#L482-L487)[^ksa-resourcemanager].
- Resource state is read by the vehicle worker, so a refill belongs before the snapshot. The two safe slots are a
  prefix on `Universe.ExecuteNextVehicleSolvers` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2128-L2139)[^ksa-universe] and the stock
  `VehicleResourcesChangeBuffer` applied by `ApplyInputEvents` [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L706-L762)[^ksa-inputevents].

## Lifecycle and timing

gatOS has two command phases. **Frame-phase** commands (warp, vessel switch) only touch main-thread globals and are
drained in its frame step (from the main viewport's `GameViewport.OnFrame` prefix, `frame:viewports`).
**Solver-phase** commands (teleports, refills) are drained from a prefix on `Universe.ExecuteNextVehicleSolvers` with
`Priority.First`, which still runs after KSA's lazy derived-data flush and before `PrepareVehicleWorkers`
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2158-L2220)[^ksa-program]. A command accepted in the frame phase therefore executes at the next dispatch of
the solver window, at most one frame later. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) and
[Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md).

## Steps

1. **Queue commands tagged with a phase.** Whatever receives them (a file write, an HTTP call, a menu button) only
   enqueues; nothing touches vehicle state there.
2. **Install a solver-window prefix once** and drain only the solver-phase commands in it, with a per-frame limit:

```csharp
// gatOS.GameMod/Game/Mod.Game.cs L849-880
    internal void DrainSolverCommands()
    {
        if (_commandsDead || _commandQueue is not { } queue)
            return;
        EnsureControlObjects();
        using (_drainStats.Measure()) // shares the stat with the Frame-phase drain (both game-thread)
            queue.Drain(CommandPhase.Solver, _catalog!, Config.MaxCommandsPerFrame);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    partial void InstallSolverHook()
    {
        try
        {
            _solverHarmony = new Harmony("gatos.solver");
            var original = AccessTools.Method(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers));
            if (original is null)
            {
                ModLog.Log.Warn("Solver hook: Universe.ExecuteNextVehicleSolvers not found; "
                                + "solver-phase commands (refills) are disabled.");
                return;
            }

            var prefix = new HarmonyMethod(typeof(Mod), nameof(SolverDrainPrefix)) { priority = Priority.First };
            _solverHarmony.Patch(original, prefix: prefix);
            ModLog.Log.Info("gatOS solver-phase command hook installed.");
        }
        catch (Exception ex)
        {
            ModLog.Log.Error($"gatOS solver hook failed to install (solver commands disabled): {ex.Message}");
        }
    }
```

3. **Write the actuators** with the game's own calls. Refuel is one line; batteries need a mutable state ref because
   state lists normally hand out `ref readonly`:

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs L122-126, L131-141
    internal static CommandResult RefillFuel(Vehicle vehicle)
    {
        vehicle.RefillConsumables();
        return CommandResult.Ok;
    }
// ...
    internal static CommandResult RefillBattery(Vehicle vehicle)
    {
        var batteries = vehicle.Parts.Batteries;
        foreach (var battery in batteries.Modules)
        {
            var mutable = batteries.GetModuleAndAllMutableStatesForInitialization(battery);
            mutable.Module.Refill(ref mutable.State);
        }

        return CommandResult.Ok;
    }
```

4. **Gate it.** gatOS builds `/sim/debug` only when `debug_namespace` is true and refuses `debug.*` actions over
   HTTP and MQTT otherwise.
5. Verify: the log shows the actuator result, `tanks/<r>` readings ([Read live part module state: engines, tanks, propellant, batteries, panels, docking ports](../read-live-part-module-state/overview.md)) jump to full,
   and an engine that had run dry relights.

## Complete example

- [gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs#L122-L141)[^gatos-debugactuator] - refuel and battery
- [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L836-L880)[^gatos-mod-game] - drain and hook install

## Variations and gotchas

- **Stock buffer instead.** For tanks alone, add an `InputEvents.VehicleResourcesChangeData { Refill = true }` and
  skip the Harmony patch (see article 1, step 5).
- **Teleport shares the window.** The same prefix runs `Vehicle.Teleport`, which detaches the vessel from its physics
  bubble; the shared reasons are in article 2 of the race topic.
- **Un-anchored target.** gatOS notes the prefix target is the single `(double dtPlayer, SimStep simStep)` overload;
  re-check it per game version.
- **Priority.** Use `Priority.First` so other mods' prefixes on the same method see the refilled state.

## See also

- [Overview](overview.md), [Article 1](refill-or-drain-resources-1.md), [Article 3](refill-or-drain-resources-3.md)
- [gatos/debug-cheats-teleport-refuel-warp](../../../../mods/gatos/features/debug-cheats-teleport-refuel-warp.md)
- [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md)

[^ksa-universe]: current/decomp/KSA/Universe.cs (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: current/decomp/KSA/InputEvents.cs (decompiled, build 2026.10.10.5554)
[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
[^gatos-debugactuator]: gatOS: gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs
[^ksa-battery]: current/decomp/KSA/Battery.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^gatos-mod-game]: gatOS: gatOS.GameMod/Game/Mod.Game.cs
[^ksa-resourcemanager]: current/decomp/KSA/ResourceManager.cs (decompiled, build 2026.10.10.5554)
