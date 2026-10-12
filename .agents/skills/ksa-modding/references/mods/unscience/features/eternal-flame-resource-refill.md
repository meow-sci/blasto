---
type: Mod Feature
title: Infinite fuel and battery refill before the vehicle solver snapshot
description: Eternal Flame refills tanks with Vehicle.RefillConsumables and batteries with Battery.Refill from a Priority.First prefix on Universe.ExecuteNextVehicleSolvers so the refill lands before the vehicle worker snapshots module state
mod: unscience
submod: eternal-flame
category: vehicles-and-parts
techniques: [harmony-prefix]
hooks: [harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers (priority First)]
lifecycle: [frame:dispatch-solvers]
game_types:
  - KSA.Universe
  - KSA.Vehicle
  - KSA.PartTree
  - KSA.ResourceManager
  - KSA.Battery
  - KSA.ModuleStateful
  - KSA.ModuleStateUpdaters
  - KSA.VehicleUpdateData
  - KSA.InputEvents
tags: [fuel, battery, resources, module-state, solver, timing, harmony]
---

Eternal Flame keeps a list of monitored vessels topped up: fuel and other tank contents through the
same call KSA's own "refill" command uses, and every battery to full charge, on a configurable
wall-clock interval (default 100 ms). The interesting part is not the refill itself but where it
runs: a refill made from UI code is silently undone while engines burn, and this feature is the
worked example of why.[^eternal-flame-lib]

Mechanism in one line: a parameterless Harmony prefix with `Priority.First` on
`Universe.ExecuteNextVehicleSolvers` calls `FuelManager.RefillBeforeVehicleSolvers`, which runs on the
main thread after the previous physics step was applied and before `Vehicle.PrepareWorker` snapshots
module state for the next one.

## How it works

- **Prefix** ([unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L184-L218)). `EternalFlamePatches.Apply` resolves
  `Universe.ExecuteNextVehicleSolvers` with `AccessTools.Method` (single overload, no parameter list
  needed) and installs `BeforeVehicleSolvers` as a prefix. The prefix returns `void`, so the original
  always runs, and it reaches the submod through the static `EternalFlameSubmod.Instance`. The
  standalone development host carries an identical copy
  ([eternal-flame/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/eternal-flame/Patcher.cs#L43-L69)); the supermod re-declares it inline because only
  the supermod ships.
- **Interval gate** ([eternal-flame.lib/EternalFlameLib.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/eternal-flame.lib/EternalFlameLib.cs#L56-L67)). `Environment.TickCount64`
  against `RefillIntervalMs` (clamped to at least 1 ms). The first call refills immediately.
- **Lookup**. Each monitored entry is matched by `Vehicle.Id` against
  `VehicleProvider.GetAllVehicles()` (current system, debris excluded); missing vessels are skipped.
- **Fuel** ([eternal-flame.lib/EternalFlameLib.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/eternal-flame.lib/EternalFlameLib.cs#L82-L92)). `Vehicle.RefillConsumables()`,
  which is `Parts.RefillConsumables()` (-> `ResourceManager.RefillAllTanks`), then
  `RecomputeMassProperties` and `FlightComputer.ReadUpdatedVehicleConfiguration`.[^ksa-vehicle]
  `RefillAllTanks` ends in `OnContentsReplaced`, which recomputes the fill fraction, sets the `Moles`
  global state's `ValuesUpdated` flag and marks the performance sequences dirty.[^ksa-resource-manager]
- **Batteries** ([eternal-flame.lib/EternalFlameLib.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/eternal-flame.lib/EternalFlameLib.cs#L108-L121)). `RefillConsumables`
  only walks `ISubstanceStore` modules, so batteries are refilled separately: iterate
  `vehicle.Parts.Batteries.Modules`, get a mutable ref with
  `GetModuleAndAllMutableStatesForInitialization(battery)` and call `Battery.Refill(ref state)`
  (`state.Charge = MaximumCapacity`). This is the same `.Module.Refill(ref .State)` shape the game
  uses for tanks and solid grains.[^ksa-battery]
- **Per-vessel toggles** (`RefillFuel`, `RefillElectricity`) and the interval are edited in the ImGui
  panel; `EternalFlameSubmod.Update` is deliberately empty.

## Lifecycle and timing

Order inside `Program.PrepareFrame` in build 5554:[^ksa-program]

| Line | Call | Relevance |
|---|---|---|
| L2164-L2167 | `Universe.ApplyOrbitSolvers` / `ApplyVehicleSolvers` | Worker results committed: `PartTree.UpdateFromTaskResults` -> `Updaters.UpdateBulkStates()` writes module states and global states back |
| L2181 | `InputEvents.ApplyInputEvents()` | KSA's own refill command (`VehicleResourcesChangeData.Apply` -> `Vehicle.RefillConsumables`) runs here |
| L2216 | `Universe.GetJobSimStep` | Shared `PhysicsFrameHook` handoff (welds, launches, scaling) |
| L2217 | `ExecuteNextClothSolvers` | Parachute cloth step dispatched |
| L2218-L2219 | `PartTree.FlushDirtyDerived()`, `FlushDirtyResourceManagers()` | Lazy part-tree data rebuilt for the coming snapshot |
| L2220 | **`Universe.ExecuteNextVehicleSolvers`** | **Eternal Flame prefix runs here**, then `PrepareVehicleWorkers` and `JobSystems.VehicleSolver.ExecuteJobs()` |

Why it has to be before the snapshot (confirmed in the 5554 decomp):

1. `ExecuteNextVehicleSolvers` -> `PrepareVehicleWorkers` -> `Vehicle.PrepareWorker` ->
   `VehicleUpdateState.PrepareFromVehicle` -> `VehicleUpdateData.Prepare` -> `Updaters.Prepare()`
   resets each `ModuleStateful<...>.StateUpdater`: it copies the current global state into
   `_newGlobalState` and marks "no new states yet".[^ksa-module-stateful]
2. On the worker, the first write to a module state (an engine draining a tank, a consumer draining
   a battery) calls `GetNewStates()`, which copies the main-thread state array at that moment and
   lets the worker mutate the copy.
3. On the next frame `ApplyVehicleSolvers` commits: `_globalState = updater.GlobalState`
   unconditionally, and the copied states over the live array when any were written.

A refill from `ISubmod.Update` (`[StarMapBeforeGui]`, after L2220) therefore loses the race whenever
the worker writes that module type in the same step, which is exactly the burning-engine case, and
always loses the `ValuesUpdated` flag because the global state was captured in step 1. Idle tanks
looked fine, which is why the bug appeared only during burns. The authors root-caused and fixed this
in their 5482 pass; before that only batteries were refilled from the prefix.[^vehicle-physics]
Refilling in the prefix puts the new contents and the raised flag into the snapshot, and the authors
note that a dry engine then re-reads its propellant and relights (not verified in game here).

Why a prefix rather than the `PhysicsFrameHook` handoff: either window is before the snapshot, and
this one is the same main-thread, no-worker window KSA's own refill effectively uses. Running after
`FlushDirtyDerived`/`FlushDirtyResourceManagers` is acceptable because the refill calls neither
`MarkDerivedDirty` nor `MarkResourceManagersDirty`; edits that do dirty derived data belong in the
earlier handoff. Because the prefix is not tied to the GUI, refills continue with the HUD hidden
(F2).

## Key code

| Path | Role |
|---|---|
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L184-L218) | `EternalFlamePatches`: the shipped prefix, applied at L76 |
| [eternal-flame/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/eternal-flame/Patcher.cs#L43-L69) | Identical prefix for the standalone development host |
| [eternal-flame.lib/EternalFlameLib.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/eternal-flame.lib/EternalFlameLib.cs) | `FuelManager`: monitored list, interval gate, fuel and battery refill |
| [eternal-flame.lib/EternalFlameSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/eternal-flame.lib/EternalFlameSubmod.cs#L15-L33) | Static `Instance` bridge; empty `Update`; `UpdateBeforeVehicleSolvers` |
| [eternal-flame.lib/EternalFlameSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/eternal-flame.lib/EternalFlameSubmod.Saves.cs) | Scene-save record of interval and per-vessel flags |
| [eternal-flame/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/eternal-flame/README.md) | User-facing notes |
| [scope/vehicle-physics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/vehicle-physics.md) | Authors' root-cause write-up of the burn-refill bug (5482 section) |

## Snippets

The shipped prefix (supermod host):

```csharp
// unscience/Patcher.cs L184-L218
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

Battery refill through the mutable state ref:

```csharp
// eternal-flame.lib/EternalFlameLib.cs L108-L121
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

The snapshot the prefix must beat (game, build 5554). `Prepare` runs from `Vehicle.PrepareWorker`
inside the patched method:

```csharp
// current/decomp/KSA/ModuleStateful.cs L777-L786
		public void Prepare()
		{
			if (_newStatesBuffer.Length < _readOnlyTypeStates.NumModules)
			{
				STATE_ARRAY_POOL.Return(_newStatesBuffer);
				_newStatesBuffer = STATE_ARRAY_POOL.Rent(_readOnlyTypeStates.NumModules);
			}
			_numNewStates = -1;
			_newGlobalState = _readOnlyTypeStates.GlobalState;
		}
```

The patched method itself: the prefix runs before `PrepareVehicleWorkers`:

```csharp
// current/decomp/KSA/Universe.cs L2126-L2139
	public static void ExecuteNextVehicleSolvers(double dtPlayer, SimStep simStep)
	{
		if (CurrentSystem != null)
		{
			PrepareVehicleWorkers(simStep);
			SyncGroundClutter();
			_vehicleUpdateTask.PrepareFrame(CollectionsMarshal.AsSpan(_orphanVehicles), simStep);
			JobSystems.VehicleSolver.Queue(_vehicleUpdateTask, 1);
			_nextPlayerDeltaTime = dtPlayer;
			_nextSimStep = simStep;
			JobSystems.VehicleSolverProfiler.Prepare();
			JobSystems.VehicleSolver.ExecuteJobs();
		}
	}
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `Universe.ExecuteNextVehicleSolvers(double, SimStep)` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2139) | Prefix target |
| `Universe.PrepareVehicleWorkers(SimStep)` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2141-L2156) | Calls `Vehicle.PrepareWorker` per vessel (the snapshot) |
| `Vehicle.PrepareWorker` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2422) | -> `PrepareFromVehicle` -> `UpdateData.Prepare` |
| `VehicleUpdateData.Prepare` | [current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84-L104) | Calls `Updaters.Prepare()` |
| `ModuleStateful<...>.StateUpdater.Prepare` / `GetNewStates` | [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L685-L786) | Global-state capture and copy-on-first-write |
| `ModuleStateful<...>.StateList.UpdateBulkStates` | [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L383-L410) | Commit that overwrote UI-time refills |
| `PartTree.UpdateFromTaskResults` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1195-L1198) | Commit entry point during `ApplyVehicleSolvers` |
| `Vehicle.RefillConsumables()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3201-L3206) | Fuel refill, mass and flight-computer refresh |
| `ResourceManager.RefillAllTanks` / `OnContentsReplaced` | [current/decomp/KSA/ResourceManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ResourceManager.cs#L454-L487) | Tank refill and `ValuesUpdated` flag |
| `PartTree.Batteries`, `StateList.GetModuleAndAllMutableStatesForInitialization` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L78) | Battery state access |
| `Battery.Refill(ref BatteryState)` | [current/decomp/KSA/Battery.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Battery.cs#L63-L66) | `Charge = MaximumCapacity` |
| `InputEvents.VehicleResourcesChangeData.Apply` | [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L706-L721) | KSA's own refill path, for comparison |
| `Program.PrepareFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2216-L2221) | Placement of the prefix in the frame |

## Pitfalls and version notes

- `Universe.ExecuteNextVehicleSolvers` is a single public overload in 5554 (L2126); a second overload
  would make the no-parameter `AccessTools.Method` lookup ambiguous.
- Since 5482 the method no longer evicts vehicles itself and `PartTree.FlushDirty*` run just before it.
  Anything a prefix here dirties is rebuilt lazily, possibly on a worker; keep prefixes to state that
  is part of the snapshot (tank and battery contents), not part-tree structure.
- The interval is wall-clock (`Environment.TickCount64`), independent of simulation speed and pause.
- Vessels are matched by `Vehicle.Id` every refill; two vessels with the same id both resolve to the
  first match.
- `Battery.Refill` and `Vehicle.RefillConsumables` are compile-checked typed calls; the authors flag
  `Battery.Refill(ref BatteryState)` as the member to watch because KSA reworked the electrical
  serialization types around it in earlier builds.
- Kiwi's Marbles installs another `Priority.First` prefix on the same method; equal priorities run in
  registration order, and the supermod registers Eternal Flame first.
- Running both the standalone host and the supermod would install the prefix twice.

## See also

- [Physics-safe vessel mutation at the PrepareFrame solver handoff](./lib-physics-frame-hook.md) (the earlier, more general window)
- [Kiwi's Marbles celestial welding](./kiwis-marbles-celestial-welding.md) (the other `ExecuteNextVehicleSolvers` prefix)
- [Garry's Torch vehicle welding](./garrys-torch-vehicle-welding.md) (another fix for results being overwritten)
- [Scene save sidecar](./unscience-scene-save-sidecar.md)
- [Eternal Flame sub-mod](../submods/eternal-flame.md) and [mod overview](../overview.md)
- [Mutate simulation state safely relative to the physics step](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) - how-to article demonstrating this feature
- [Keep a vehicle's fuel and batteries full with a refill before the solver snapshot](../../../howto/vehicles-and-parts/modules-and-resources/refill-or-drain-resources/refill-or-drain-resources-1.md) - how-to article demonstrating this feature
- [Write state in a prefix on Universe.ExecuteNextVehicleSolvers](../../../howto/vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/change-vehicles-without-racing-the-solver-4.md) - how-to article demonstrating this feature

[^eternal-flame-lib]: eternal-flame.lib/EternalFlameLib.cs
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-resource-manager]: KSA.ResourceManager (decompiled, build 2026.10.10.5554)
[^ksa-battery]: KSA.Battery (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-module-stateful]: KSA.ModuleStateful (decompiled, build 2026.10.10.5554)
[^vehicle-physics]: scope/vehicle-physics.md
