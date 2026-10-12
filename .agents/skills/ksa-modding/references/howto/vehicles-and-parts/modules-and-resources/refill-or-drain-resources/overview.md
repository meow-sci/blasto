---
type: Game Feature
title: Refilling, draining and topping up a vehicle's propellant and batteries
description: The calls KSA uses to refill or empty tanks and batteries, the stock input buffer that runs them safely, and three mod recipes with their timing windows
category: vehicles-and-parts
group: modules-and-resources
topic: refill-or-drain-resources
demonstrated_by:
  - mods/unscience/features/eternal-flame-resource-refill
  - mods/gatos/features/debug-cheats-teleport-refuel-warp
  - mods/ksarmory/features/holed-tank-leaks
lifecycle: [frame:dispatch-solvers, frame:viewports, frame:input]
game_types:
  - KSA.Vehicle
  - KSA.Tank
  - KSA.Mole
  - KSA.MoleState
  - KSA.Battery
  - KSA.ResourceManager
  - KSA.InputEvents
  - KSA.PartTree
tags: [refill, drain, tanks, batteries, mass, flight-computer, input-events]
---
Changing what a vehicle carries is two jobs: change the stored amounts (tank `MoleState.Mass`, battery `Charge`),
and tell everything that derives from them (fill fraction, the `ValuesUpdated` flag engines wait on, stage
performance, mass properties, the flight computer's copy of the craft). KSA does both in `Vehicle.RefillConsumables`
and `Vehicle.DepleteConsumables`. Where you call them matters as much as what you call, because the physics worker
copies and writes the same states.

## Engine background

- `Vehicle.RefillConsumables()` = `Parts.RefillConsumables()` (`ResourceManager.RefillAllTanks`), then
  `_props.RecomputeMassProperties(...)`, then `FlightComputer.ReadUpdatedVehicleConfiguration(this)`
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3201-L3213)[^ksa-vehicle] [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1039-L1042)[^ksa-parttree]. `DepleteConsumables()` is the same with
  `ResourceManager.DepleteAllTanks` [current/decomp/KSA/ResourceManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ResourceManager.cs#L454-L480)[^ksa-resourcemanager].
- Both tank paths end in `OnContentsReplaced`: recompute total fill fraction, set the `Moles` global state's
  `ValuesUpdated` flag, mark performance sequences dirty [current/decomp/KSA/ResourceManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ResourceManager.cs#L482-L487). The flag is what
  makes a dry engine re-read its propellant (`Rocket` skips its update otherwise) [current/decomp/KSA/Rocket.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Rocket.cs#L84-L95)[^ksa-rocket].
- Batteries are not in `ISubstanceStore`, so `RefillConsumables` does not touch them: iterate
  `Parts.Batteries.Modules`, take a mutable ref with `GetModuleAndAllMutableStatesForInitialization(battery)` and
  `Battery.Refill(ref state)` (`state.Charge = MaximumCapacity`) [current/decomp/KSA/Battery.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Battery.cs#L63-L66)[^ksa-battery].
- `Mole.ConsumeStored(ref MoleState, kg)` removes mass from one mole, clamped at zero [current/decomp/KSA/Mole.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mole.cs#L89-L103)[^ksa-mole].
- **Stock safe path.** The console `refill` and `empty` commands queue
  `InputEvents.VehicleResourcesChangeData { Vehicle, Refill / Empty }` on the public
  `InputEvents.VehicleResourcesChangeBuffer`; `ApplyInputEvents` applies it in the settled window
  [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L706-L762)[^ksa-inputevents] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1277-L1336)[^ksa-universe]. A mod can use the same buffer and needs no Harmony
  patch.

## Approaches

| Article | Mod | Technique | When |
|---|---|---|---|
| [1: refill before the solver snapshot](refill-or-drain-resources-1.md) | unscience | `harmony-prefix` | every vehicle, every N ms, infinite fuel and charge |
| [2: a one-shot cheat in the solver window](refill-or-drain-resources-2.md) | gatOS | `command-queue` | on demand, from a menu or file write |
| [3: drain through the mole states](refill-or-drain-resources-3.md) | KSArmory | `module-state-write` | partial, continuous removal with the bookkeeping repeated |

## See also

- [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md), [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md) (articles 4 and 6 are the timing detail)
- [Read live part module state: engines, tanks, propellant, batteries, panels, docking ports](../read-live-part-module-state/overview.md)
- Mod features: [unscience/eternal-flame-resource-refill](../../../../mods/unscience/features/eternal-flame-resource-refill.md), [gatos/debug-cheats-teleport-refuel-warp](../../../../mods/gatos/features/debug-cheats-teleport-refuel-warp.md), [ksarmory/holed-tank-leaks](../../../../mods/ksarmory/features/holed-tank-leaks.md)

[^ksa-rocket]: current/decomp/KSA/Rocket.cs (decompiled, build 2026.10.10.5554)
[^ksa-universe]: current/decomp/KSA/Universe.cs (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: current/decomp/KSA/InputEvents.cs (decompiled, build 2026.10.10.5554)
[^ksa-battery]: current/decomp/KSA/Battery.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: current/decomp/KSA/PartTree.cs (decompiled, build 2026.10.10.5554)
[^ksa-mole]: current/decomp/KSA/Mole.cs (decompiled, build 2026.10.10.5554)
[^ksa-resourcemanager]: current/decomp/KSA/ResourceManager.cs (decompiled, build 2026.10.10.5554)
