---
type: How-To
title: Write state in a prefix on Universe.ExecuteNextVehicleSolvers
description: Run a once-per-frame main-thread write after the solver results are applied and immediately before the worker snapshot, with Harmony Priority.First
category: vehicles-and-parts
group: vehicles
topic: change-vehicles-without-racing-the-solver
approach: 4
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/eternal-flame-resource-refill]
techniques: [harmony-prefix]
hooks: [harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers]
lifecycle: [frame:dispatch-solvers, frame:apply-solvers]
game_types: [KSA.Universe, KSA.Vehicle, KSA.PartTree, KSA.Battery, KSA.ModuleStateful, KSA.VehicleUpdateData]
tags: [prefix, snapshot, refill, module-state, timing]
---
Outcome: a write to vehicle module state (tank contents, battery charge) that is not erased by the
worker's result a frame later. unscience's Eternal Flame submod keeps vessels topped up with a
parameterless prefix on `Universe.ExecuteNextVehicleSolvers`; a refill made from UI code instead is
silently undone whenever an engine is burning. The same prefix point is useful for any once-per-frame
write that covers many vehicles. Per-vehicle writes use article 5.

Prerequisites: StarMap, Harmony. Demonstrated by [unscience/eternal-flame-resource-refill](../../../../mods/unscience/features/eternal-flame-resource-refill.md). The refill
calls themselves are in [Refill, drain or top up a vehicle's propellant and batteries](../../modules-and-resources/refill-or-drain-resources/overview.md).

## Background: how the game does it

`Program.PrepareFrame` ends its simulation section with a fixed order ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2158-L2220)[^ksa-program]):

| Line | Call | Meaning |
|---|---|---|
| 2158 | `JobSystems.VehicleSolver.Wait()` | previous step finished |
| 2167 | `Universe.ApplyVehicleSolvers()` | results committed to live vehicles |
| 2181 | `InputEvents.ApplyInputEvents()` | stock refill/stage/teleport commands run here |
| 2218-2219 | `PartTree.FlushDirtyDerived()`, `FlushDirtyResourceManagers()` | lazy part-tree data rebuilt |
| 2220 | `Universe.ExecuteNextVehicleSolvers` | `PrepareVehicleWorkers` snapshots, then the job is queued |

Snapshot and commit mechanics:

1. `ExecuteNextVehicleSolvers` -> `PrepareVehicleWorkers` -> `Vehicle.PrepareWorker` ->
   `VehicleUpdateState.PrepareFromVehicle` -> `VehicleUpdateData.Prepare`, which ends in
   `Updaters.Prepare()` [current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84-L104)[^ksa-vehicleupdatedata]. Each `StateUpdater.Prepare` clears the
   "new states" marker and copies the live global state into `_newGlobalState`
   [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L771-L784)[^ksa-modulestateful].
2. On the worker, the first write to a module type calls `GetNewStates()`, which copies the live state
   array at that moment [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L685-L697).
3. `ApplyVehicleSolvers` commits: `UpdateBulkStates` always sets `_globalState = updater.GlobalState` and
   copies the new states over the live ones if the worker wrote any [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L383-L413).

So a write made after step 1 loses against the global-state copy and, whenever the worker writes the
same module type that step (an engine drawing propellant), against the state copy as well.

## Lifecycle and timing

The prefix runs inside `PrepareFrame` on the main thread with the worker idle, results applied, and the
snapshot not yet taken (`frame:dispatch-solvers`). That is also the window KSA's own refill command uses
(`ApplyInputEvents` at L2181). A hook in `StarMapBeforeGui` or `StarMapAfterGui` runs after L2220 and is
too late; see [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

Edits that mark derived part-tree data dirty belong earlier (`ApplyInputEvents` or a handoff before
L2218), because `FlushDirtyDerived` has already run by this point. A refill calls neither
`MarkDerivedDirty` nor `MarkResourceManagersDirty`, so running after the flushes is fine.

## Steps

1. Install the patch with `Priority.First` and throw if the method is missing, so a game update shows up
   as a startup error:

```csharp
// unscience/Patcher.cs L184-218
internal static class EternalFlamePatches
{
    public static void Apply(Harmony harmony)
    {
        var original = AccessTools.Method(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers));
        var prefixMethod = AccessTools.Method(typeof(EternalFlamePatches), nameof(BeforeVehicleSolvers));

        if (original == null)
            throw new MissingMethodException(typeof(Universe).FullName, nameof(Universe.ExecuteNextVehicleSolvers));
        if (prefixMethod == null)
            throw new MissingMethodException(typeof(EternalFlamePatches).FullName, nameof(BeforeVehicleSolvers));

        harmony.Patch(original, prefix: new HarmonyMethod(prefixMethod) { priority = Priority.First });
    }

    public static void Remove(Harmony harmony)
    {
        var original = AccessTools.Method(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers));
        var prefixMethod = AccessTools.Method(typeof(EternalFlamePatches), nameof(BeforeVehicleSolvers));
        if (original != null && prefixMethod != null)
            harmony.Unpatch(original, prefixMethod);
    }

    private static void BeforeVehicleSolvers()
    {
        try
        {
            EternalFlameSubmod.Instance?.UpdateBeforeVehicleSolvers();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"eternal-flame: Error in solver prefix: {ex.Message}\n{ex}");
        }
    }
}
```

2. In the prefix, do cheap early-outs, then gate by wall-clock interval (the prefix runs every frame):

```csharp
// eternal-flame.lib/EternalFlameLib.cs L56-72
    public void RefillBeforeVehicleSolvers()
    {
        if (_monitored.Count == 0)
            return;

        long now = Environment.TickCount64;
        int interval = Math.Max(RefillIntervalMs, 1);
        long elapsedMs = _lastRefillTickMs == 0 ? interval : now - _lastRefillTickMs;
        if (elapsedMs < interval)
            return;

        _lastRefillTickMs = now;

        var vehicles = VehicleProvider.GetAllVehicles();
        if (vehicles.Count == 0)
            return;

```

3. Refill fuel with the game's own call. It also recomputes mass properties and the flight computer's copy:

```csharp
// current/decomp/KSA/Vehicle.cs L3201-3206
	public void RefillConsumables()
	{
		Parts.RefillConsumables();
		_props.RecomputeMassProperties(Parts.SubstanceStores, Parts.Moles.States);
		FlightComputer.ReadUpdatedVehicleConfiguration(this);
	}
```

4. Refill batteries separately: `RefillConsumables` only walks substance stores. Take the mutable live
   state and call `Battery.Refill(ref state)`:

```csharp
// eternal-flame.lib/EternalFlameLib.cs L108-121
    private static void RefillBatteries(Vehicle vehicle)
    {
        var batteryStates = vehicle.Parts.Batteries;
        if (batteryStates.NumModules == 0)
            return;

        var modules = batteryStates.Modules;
        for (int i = 0; i < modules.Length; i++)
        {
            var battery = modules[i];
            var mutableRef = batteryStates.GetModuleAndAllMutableStatesForInitialization(battery);
            mutableRef.Module.Refill(ref mutableRef.State);
        }
    }
```

5. Wrap the call in a try/catch inside the prefix (unscience does): a throw here is thrown out of
   `PrepareFrame`.
6. Check it works in a burn: engines keep running past the point the tank would be empty.

## Complete example

- [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L184-L218)[^unscience-patcher] - patch installation and prefix
- [eternal-flame.lib/EternalFlameLib.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/eternal-flame.lib/EternalFlameLib.cs#L56-L121)[^unscience-eternalflamelib] - interval gate, fuel and battery refill

## Variations and gotchas

- **Why not `PhysicsFrameHook`-style handoffs?** Any main-thread point between L2167 and L2220 works; the
  prefix is simplest because it needs no extra plumbing. Use a seam earlier in the frame for writes that
  dirty derived data.
- **A refill from a UI hook "works" only when engines are idle.** Idle tanks look fine, which is why the
  bug appears only during burns.
- **Dry engines.** `ResourceManager.RefillAllTanks` ends in `OnContentsReplaced`, which sets the Moles
  global state's `ValuesUpdated` flag [current/decomp/KSA/ResourceManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ResourceManager.cs#L482-L490)[^ksa-resourcemanager]; `Rocket` skips its update unless a rocket
  is firing, the flag is set or other change tests pass [current/decomp/KSA/Rocket.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Rocket.cs#L84-L95)[^ksa-rocket], so the flag is what makes
  a dry engine re-read its propellant.
- **HUD hidden.** The prefix is not tied to the GUI, so it keeps running with the HUD off.
- **Updates.** The patch targets one public static method with a single overload; a rename is caught by the
  `MissingMethodException` check.

## See also

- [Overview](overview.md), [Article 3](change-vehicles-without-racing-the-solver-3.md), [Article 5: PrepareWorker window](change-vehicles-without-racing-the-solver-5.md)
- [unscience/eternal-flame-resource-refill](../../../../mods/unscience/features/eternal-flame-resource-refill.md)
- [Refill, drain or top up a vehicle's propellant and batteries](../../modules-and-resources/refill-or-drain-resources/overview.md)

[^ksa-vehicleupdatedata]: current/decomp/KSA/VehicleUpdateData.cs (decompiled, build 2026.10.10.5554)
[^ksa-rocket]: current/decomp/KSA/Rocket.cs (decompiled, build 2026.10.10.5554)
[^unscience-patcher]: unscience: unscience/Patcher.cs
[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
[^unscience-eternalflamelib]: unscience: eternal-flame.lib/EternalFlameLib.cs
[^ksa-resourcemanager]: current/decomp/KSA/ResourceManager.cs (decompiled, build 2026.10.10.5554)
[^ksa-modulestateful]: current/decomp/KSA/ModuleStateful.cs (decompiled, build 2026.10.10.5554)
