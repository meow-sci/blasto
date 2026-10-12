---
type: How-To
title: Keep a vehicle's fuel and batteries full with a refill before the solver snapshot
description: Call Vehicle.RefillConsumables and Battery.Refill from a prefix on Universe.ExecuteNextVehicleSolvers so the worker's copy already holds the refilled state
category: vehicles-and-parts
group: modules-and-resources
topic: refill-or-drain-resources
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/eternal-flame-resource-refill]
techniques: [harmony-prefix]
hooks: [harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers]
lifecycle: [frame:dispatch-solvers, frame:apply-solvers]
game_types: [KSA.Vehicle, KSA.PartTree, KSA.Battery, KSA.ResourceManager, KSA.Universe, KSA.ModuleStateful]
tags: [refill, fuel, battery, prefix, infinite-resources]
---
Outcome: chosen vessels never run out of propellant or charge, including while engines burn. unscience's Eternal Flame
refills a monitored list on a wall-clock interval (default 100 ms) from a prefix on
`Universe.ExecuteNextVehicleSolvers`. A refill made from UI code is silently undone while engines burn; this
approach is the fix. The mechanism and the frame-order proof are in
[Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md) article 4; this article is the recipe from the resources side.

Prerequisites: StarMap, Harmony. Demonstrated by [unscience/eternal-flame-resource-refill](../../../../mods/unscience/features/eternal-flame-resource-refill.md).

## Background: how the game does it

- The calls: `Vehicle.RefillConsumables()` for tanks (it also recomputes mass properties and the flight computer's
  configuration), `Battery.Refill(ref state)` for each battery [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3201-L3206)[^ksa-vehicle]
  [current/decomp/KSA/Battery.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Battery.cs#L63-L66)[^ksa-battery].
- `RefillAllTanks` ends in `OnContentsReplaced`, which sets the `Moles` global state's `ValuesUpdated` flag and marks
  performance sequences dirty [current/decomp/KSA/ResourceManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ResourceManager.cs#L454-L466)[^ksa-resourcemanager] [current/decomp/KSA/ResourceManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ResourceManager.cs#L482-L487).
- The worker snapshots module state in `PrepareWorker` and its result overwrites the live state at the next apply
  (`UpdateBulkStates`) [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L383-L413)[^ksa-modulestateful], so only a refill that precedes the snapshot survives.

## Lifecycle and timing

The prefix runs inside `Program.PrepareFrame` on the main thread, after `ApplyVehicleSolvers` (L2167) and
`ApplyInputEvents` (L2181) and immediately before `PrepareVehicleWorkers` snapshots (L2220)
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2158-L2220)[^ksa-program]. KSA's own refill command (`VehicleResourcesChangeData.Apply`) runs earlier in the
same window, in `ApplyInputEvents`. A refill from `StarMapBeforeGui` or `StarMapAfterGui` runs after the
snapshot and loses whenever the worker writes the same module type that step, which is exactly the burning-engine
case. Because the prefix is not tied to the GUI, refills continue with the HUD hidden. See
[game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. Install the prefix with `Priority.First`; throw `MissingMethodException` if the target is missing so a game update
   is a startup error (full snippet in article 4 of the race topic):

```csharp
// unscience/Patcher.cs L184-197
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
```

2. Gate by interval; the prefix runs every frame:

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

3. Refill fuel with the game's call; refill batteries separately, because `RefillConsumables` only walks substance
   stores:

```csharp
// eternal-flame.lib/EternalFlameLib.cs L73-96
        foreach (var entry in _monitored)
        {
            if (!entry.RefillFuel && !entry.RefillElectricity)
                continue;

            var vehicle = vehicles.FirstOrDefault(v => v.Id == entry.VehicleId);
            if (vehicle == null)
                continue;

            if (entry.RefillFuel)
            {
                try
                {
                    vehicle.RefillConsumables();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"eternal-flame: Error refilling fuel {entry.DisplayName}: {ex.Message}");
                }
            }

            if (entry.RefillElectricity)
            {
                try
```

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

4. Look vehicles up by id each pass (never keep indices) and catch every exception inside the prefix.
5. **No-Harmony alternative for one-shot or per-event refills:** queue the stock request and let `ApplyInputEvents`
   run it in the settled window (adapted from `Universe.Refill`, `current/decomp/KSA/Universe.cs`):

```csharp
// adapted from current/decomp/KSA/Universe.cs (Refill terminal action)
InputEvents.VehicleResourcesChangeBuffer.Add(new InputEvents.VehicleResourcesChangeData
{
    Vehicle = vehicle,
    Refill = true,
    Empty = false,
    Control = false
});
```

   This refills tanks only (not batteries) and lands in the next `ApplyInputEvents`, which is before the next
   snapshot, so it survives the worker. A continuous refill every frame can also use it.

## Complete example

- [eternal-flame.lib/EternalFlameLib.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/eternal-flame.lib/EternalFlameLib.cs#L56-L121)[^unscience-eternalflamelib] - interval, fuel, batteries
- [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L184-L218)[^unscience-patcher] - installation

## Variations and gotchas

- **Per-vessel toggles.** Eternal Flame has independent fuel and electricity flags per monitored vessel.
- **Dry engines relight.** The `ValuesUpdated` flag raised by the refill is what makes a dry engine re-read its
  propellant; skipping the game's call and writing `MoleState` directly loses that (see article 3).
- **Hidden-derived-data edits** belong earlier in the frame; this refill dirties nothing the flush has not already
  run.
- **Cost.** The refill recomputes mass and the flight computer's configuration per vessel per pass; keep the
  interval at tens of milliseconds, not per frame, if many vessels are monitored.

## See also

- [Overview](overview.md), [Article 2](refill-or-drain-resources-2.md), [Article 3](refill-or-drain-resources-3.md)
- [unscience/eternal-flame-resource-refill](../../../../mods/unscience/features/eternal-flame-resource-refill.md)
- [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md)

[^unscience-patcher]: unscience: unscience/Patcher.cs
[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
[^ksa-battery]: current/decomp/KSA/Battery.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^unscience-eternalflamelib]: unscience: eternal-flame.lib/EternalFlameLib.cs
[^ksa-resourcemanager]: current/decomp/KSA/ResourceManager.cs (decompiled, build 2026.10.10.5554)
[^ksa-modulestateful]: current/decomp/KSA/ModuleStateful.cs (decompiled, build 2026.10.10.5554)
