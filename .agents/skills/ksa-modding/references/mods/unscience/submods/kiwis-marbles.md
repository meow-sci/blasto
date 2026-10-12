---
type: Submod
title: Kiwi's Marbles
description: Welds planets and moons to other celestials or vessels at a CCI-frame offset by rewriting their orbits in a solver-dispatch prefix, with explicit parent-tree fix-ups, weld chains and scene-save restore
mod: unscience
submod: kiwis-marbles
tags: [celestials, orbits, welding, solver-timing, parent-child]
mod_id: kiwis-marbles
---

Kiwi's Marbles ("Destroyer of Worlds" in the toolbox) welds a planet or moon to any orbiter, another
celestial or a vessel, at an offset in the target parent's CCI frame entered in m, km, Mm or Gm, with a
surface lon/lat helper when the target is a celestial. Each sim step the source body gets a fresh orbit
built from the target's state vectors, from a `Priority.First` Harmony prefix on
`Universe.ExecuteNextVehicleSolvers`, the one main-thread window where orbit results are applied and the
next `CelestialUpdateTask` has not been queued. When the new orbit has a different parent the sub-mod
moves the body between the parents' `Children` lists itself, because `Celestial.SetOrbit` does not.
Weld chains are ordered with Kahn's topological sort, unwelding restores the orbit captured at weld
time, and unscience scene saves restore welds and original orbits.

## Hosting

- Manifest id `kiwis-marbles`, entry assembly `MeowSci.KiwisMarbles` (development host only; only the
  supermod ships).
- Standalone host: F9 toggles the "Kiwi's Marbles" window; its Patcher applies `HotkeyGuard` and
  `KiwisMarblesPatches` (the solver prefix).
- In unscience the sub-mod is created in `Mod.OnFullyLoaded` (unscience/Mod.cs L95), and
  `unscience/Patcher.cs` applies `KiwisMarblesPatches` at L77 (right after Eternal Flame's prefix on the
  same method) and removes it at L131. `ISubmod.Update` is intentionally empty.

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Celestial welding by rewriting a body's orbit before every sim step](../features/kiwis-marbles-celestial-welding.md) | celestials | `frame:dispatch-solvers`, `frame:draw-ui`, `event:save`, `event:load` | Kiwi's Marbles pins a planet or moon at a CCI-frame offset from another celestial or a vessel by building a fresh orbit from the target's state vectors in a Priority.First prefix on Universe.ExecuteNextVehicleSolvers, before the orbit workers snapshot it |
| [Cross-parent celestial welds with explicit IParentBody.Children re-parenting](../features/kiwis-marbles-cross-parent-reparenting.md) | celestials | `frame:dispatch-solvers`, `event:load` | When a weld moves a body under a different parent, Kiwi's Marbles calls Celestial.SetOrbit, then moves the body between the old and new parents' Children lists itself and refreshes the moved subtree, because KSA re-parents only vessels automatically |

## Library classes

`KiwisMarblesSubmod` (weld list, deferred restores, UI, static `Instance` used by the prefix),
`KiwisMarblesPatches` (prefix apply/remove), `CelestialWeldEngine` (`UpdateWeld`, `RestoreOrbit`,
`ApplyOrbit`, `Reparent`, `TopologicalSort`), `CelestialWeldEntry`, `SavedCelestialWeld`; shared
`ksa-abstractions.lib` `CelestialProvider` and `SimTimeProvider`.

## Scene saves and persistence

Participant `kiwis-marbles`, restore order 40: per weld the source and target ids, whether the target is
a vessel, the offset, and the original orbit as parent id, state time, CCI position and velocity (native
saves never store celestial orbits). Validation rejects duplicate sources, non-finite values and saved
weld cycles; reset restores original orbits in reverse order before native load; replay rebuilds the
original orbits, removes welds that would make a body its own ancestor, sorts and applies each weld once
before the next solver dispatch.[^kiwis-marbles-persistence-cs][^kiwis-marbles-lib-readme-md]

## Further reading

- [kiwis-marbles/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles/README.md) (usage, offset conventions, timing write-up)
- [kiwis-marbles.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/README.md) (scene saves)
- [scope/celestial-and-lights.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/celestial-and-lights.md) (integration table and per-build checks)
- [REPOSITORY_INDEX.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/REPOSITORY_INDEX.md) (project summary)

## Entry points

- [kiwis-marbles/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles/mod.toml)
- [kiwis-marbles/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles/Mod.cs)
- [kiwis-marbles/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles/Patcher.cs)
- [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L77)
- [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L95)

## See also

- [unscience overview](../overview.md)
- [Eternal Flame](./eternal-flame.md) (the other prefix on `Universe.ExecuteNextVehicleSolvers`)
- [Garry's Torch](./garrys-torch.md) (the same idea for vessels)
- [Bloomin' Onion](./bloomin-onion.md) (rings are celestial-bound; weld a ringed moon to a vessel)

[^kiwis-marbles-persistence-cs]: kiwis-marbles.lib/KiwisMarblesSubmod.Persistence.cs
[^kiwis-marbles-lib-readme-md]: kiwis-marbles.lib/README.md
