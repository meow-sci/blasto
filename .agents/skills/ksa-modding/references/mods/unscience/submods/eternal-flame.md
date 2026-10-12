---
type: Submod
title: Eternal Flame
description: Keeps monitored vessels topped up with fuel and battery charge from a solver-dispatch prefix that runs before the vehicle worker snapshots module state
mod: unscience
submod: eternal-flame
tags: [fuel, batteries, resources, solver-timing]
mod_id: eternal-flame
---

Eternal Flame keeps selected vessels topped up with fuel (`Vehicle.RefillConsumables`) and battery
charge (`Battery.Refill` on each battery's mutable state) on a wall-clock interval (default 100 ms,
0 to 5000 ms). Both refills run from a parameterless `Priority.First` Harmony prefix on
`Universe.ExecuteNextVehicleSolvers`, before `Vehicle.PrepareWorker` snapshots module state. Fuel
refilled from UI code before KSA 5482 was overwritten by the worker's commit during burns; the
feature page explains why.

## Hosting

- Manifest id `eternal-flame`, entry assembly `MeowSci.EternalFlame` (development host only).
- Standalone host: F11 toggles "Eternal Flame - Infinite Fuel"; its Patcher applies an identical solver
  prefix and `HotkeyGuard`.
- In unscience the prefix is declared inline as `EternalFlamePatches` in `unscience/Patcher.cs`, because
  only the supermod ships. `EternalFlameSubmod.Update` is intentionally empty.

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Infinite fuel and battery refill before the vehicle solver snapshot](../features/eternal-flame-resource-refill.md) | vehicles-and-parts | `frame:dispatch-solvers` | Eternal Flame refills tanks with Vehicle.RefillConsumables and batteries with Battery.Refill from a Priority.First prefix on Universe.ExecuteNextVehicleSolvers so the refill lands before the vehicle worker snapshots module state |

## Library classes

`FuelManager` (monitored list, interval gate, `RefillBeforeVehicleSolvers`, `RefillBatteries`),
`MonitoredVehicle` (id, display name, fuel and electricity flags), `EternalFlameSubmod` (static
`Instance` bridge used by the prefix, ImGui panel).

## Scene saves and persistence

Participant `eternal-flame`, version 1: refill interval and per-vessel `{VehicleId, Fuel, Electricity}`,
rebound by id with `VehicleProvider.FindVehicle` (missing vessels reported). Reset replaces the
`FuelManager`, clearing the refill timer.

## Further reading

- [eternal-flame/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/eternal-flame/README.md)
- [eternal-flame.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/eternal-flame.lib/README.md)
- [scope/vehicle-physics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/vehicle-physics.md)
- [plans/KSA_5482_UPGRADE.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/plans/KSA_5482_UPGRADE.md)

## Entry points

- [eternal-flame/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/eternal-flame/mod.toml)
- [eternal-flame/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/eternal-flame/Mod.cs)
- [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs)

## See also

- [unscience overview](../overview.md)
- [Consolidated patcher](../features/unscience-consolidated-patcher.md) (where the prefix is installed)
