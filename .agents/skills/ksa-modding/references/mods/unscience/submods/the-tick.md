---
type: Submod
title: The Tick
description: Makes chosen vessels indestructible by forces with a crash-tolerance getter prefix and a worker-thread discard of structural destruction events, leaving shared part templates untouched
mod: unscience
submod: the-tick
tags: [damage, indestructible, worker-thread, harmony]
mod_id: the-tick
---

The Tick makes chosen vessels indestructible by forces while keeping contacts and telemetry native.
A prefix on the `Part.CrashTolerancePascals` getter returns `double.MaxValue` for parts owned by a
registered vessel (no contact failure, no dents), and a prefix and postfix pair on the private static
`PhysicsBubble.DetectStructuralFailure` clears only the destruction event the detector just created
(G-load and dynamic pressure). Both run on vehicle worker threads against a concurrent registry;
shared `PartTemplate` data is never edited. Explicit destroy, explode and recover paths are not covered.

## Hosting

- Manifest id `the-tick`, entry assembly `MeowSci.TheTick` (development host only).
- Standalone host: F11 toggles "The Tick"; its Patcher applies `HotkeyGuard` and `TheTickPatches`.
- In unscience `TheTickPatches` is applied by `unscience/Patcher.cs`; `TickProtection` is a public
  registry other mods can use.

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Per-vessel indestructibility without touching shared part templates](../features/the-tick-vessel-indestructibility.md) | physics | `physics:vehicle-solver`, `physics:bepu`, `frame:draw-ui` | The Tick makes registered vessels immune to force-based destruction with a prefix on the Part.CrashTolerancePascals getter and a prefix and postfix pair on PhysicsBubble.DetectStructuralFailure that discards only the event the detector just created |

## Library classes

`TheTickPatches` (signature-validated install, getter prefix, detector prefix and postfix),
`TickProtection` (concurrent registry: `Add`, `Remove`, `Contains`, `Prune`), `TheTickSubmod`
(picker, active table, save adapter; `Update` prunes disposed or departed vessels).

## Scene saves and persistence

Participant `the-tick`, version 1: the sorted protected vehicle ids. Capture refuses ids that would
not rebind uniquely; replay requires the patches to be installed and reports missing or ambiguous vessels.

## Further reading

- [the-tick/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick/README.md)
- [the-tick.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.lib/README.md)
- [the-tick.tests/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.tests/README.md)
- [plans/VEHICLE_DESTRUCTION_INVESTIGATION.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/plans/VEHICLE_DESTRUCTION_INVESTIGATION.md)
- [scope/vehicle-physics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/vehicle-physics.md)

## Entry points

- [the-tick/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick/mod.toml)
- [the-tick/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick/Mod.cs)
- [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs)

## See also

- [unscience overview](../overview.md)
- [Kitchen Sink](./kitchen-sink.md) (independent G-load-only protection)
