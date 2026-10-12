---
type: Submod
title: Iron Man
description: Lets one live EVA kitten be edited in the stock vehicle editor with private attachment nodes, then flown as an ordinary rocket vessel with headward controls, native flight-computer gauges, early equipment rendering and upright surface teleports
mod: unscience
submod: iron-man
tags: [eva, kitten, vehicle-editor, rocket-flight, connectors, teleport, rendering, scene-saves]
mod_id: iron-man
---

Iron Man turns one existing EVA kitten into a small vessel. The player opens the stock vehicle editor
on the live kitten, snaps tanks, engines and RCS onto private attachment nodes on its backpack root,
returns to flight and switches the kitten between native **eva mode** and **iron man** mode. In Iron
Man mode the kitten flies with ordinary vessel physics, headward rocket controls, the stock
flight-computer gauges and geometric backpack RCS. Everything is keyed by the actual `KittenEva`
object and is opt-in per kitten; no shared template or asset is modified. Mode changes are queued
through the shared [physics frame hook](../features/lib-physics-frame-hook.md) so they run between
physics steps.

## Hosting

- Manifest id `iron-man`, entry assembly `MeowSci.IronMan` (development host only; only the
  unscience supermod ships).
- Standalone host: F11 toggles a 500x620 "Iron Man" window that draws `IronManSubmod.RenderContent`;
  `[StarMapBeforeGui]` calls `Update`. Its Patcher applies `HotkeyGuard`, the shared
  `PhysicsFrameHook` and `IronManPatches`, and unloads them in reverse.
- In unscience, `IronManSubmod` is one entry in the sub-mod list and `unscience/Patcher.cs` calls
  `IronManPatches.Apply` (L96); the frame hook is already installed by the supermod.
- `IronManPatches.Apply` is transactional: it installs the connector, editor, flight (which also
  installs the render patches), flight-computer, control-frame, editor-orientation, RCS-orientation
  and teleport patches in order, and on any failure removes all of them so the panel reports the
  integration unavailable instead of allowing a half-patched kitten. `Remove` keeps the passive
  flight, editor and connector hooks if live attachments cannot be safely converted to stock links.

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Per-instance attachment nodes that survive saves, copies and unload](../features/iron-man-connector-serialization.md) | persistence | `frame:prepare`, `event:save`, `event:load`, `event:part-copy`, `event:unload` | Iron Man adds private Part.Connector instances to one kitten's backpack root, encodes them as a versioned marker in the serialized PartInstance.Id, restores them in a Part constructor postfix before connection indices resolve, and converts live links to stock endpoints before its hooks are removed |
| [Submit EVA kitten equipment before the part-instance upload](../features/iron-man-equipment-render-ordering.md) | rendering | `frame:render` | A prefix on PartModelRenderer.UpdateRenderData pushes each equipped kitten's part tree through a nonvirtual call to the live patched Vehicle.UpdateRenderData before the instance lists are uploaded, and a Priority.First prefix skips the late duplicate while the avatar keeps its later draw |
| [Fly one EVA kitten as an ordinary rocket vessel](../features/iron-man-kitten-rocket-flight.md) | kittens | `frame:draw-ui`, `frame:prepare`, `frame:input`, `frame:dispatch-solvers`, `physics:vehicle-solver` | Per-kitten Iron Man mode flips the worker IsKitten flag, routes input through reverse-patched base Vehicle handlers, enables native flight-computer gauges and re-points the control frame and backpack RCS headward at the physics handoff |
| [Open the stock vehicle editor on a live EVA kitten](../features/iron-man-kitten-vehicle-editing.md) | kittens | `frame:prepare`, `frame:input`, `frame:viewports`, `editor:on-frame`, `editor:render` | Iron Man sets Program.EditorFlag from the physics handoff so the stock editor opens on the controlled KittenEva in place, then guards the body root with editor prefixes, draws the avatar into the editor prepass and rotates the editor camera so the kitten stands upright |
| [Upright surface teleport for Iron Man kittens by adapting one placement call](../features/iron-man-surface-teleport-correction.md) | kittens | `frame:draw-ui`, `frame:input` | Iron Man transpiles the one GetInitialKinematicStateForLocation call in Vehicle.TeleportToLocation into an adapter that gives native placement head-up proxy bounds for active Iron Man kittens and converts the attitude and rates back |

## Library classes

`IronManSubmod` (partial: lifecycle and queued mode transitions, `.Ui.cs` panel, `.Saves.cs` record)
keeps two identity sets: kittens configured for editing (`IsConfigured`, used by editor hooks) and
kittens in active rocket mode (`IsEnabled`, a volatile snapshot array read by worker-side hooks).
`IronManEvaSettings` and `IronManFlightSettings` capture and restore the pre-entry EVA control
part, connector, control mode and flight-computer settings. `IronManOrientation.RocketCtrl2Body` is
the shared rocket-to-EVA rotation. Patch classes: `IronManPatches` (coordinator),
`IronManConnectorPatches`, `IronManConnectors`, `IronManConnectorData`, `IronManConnectorUnload`,
`IronManEditorPatches`, `IronManEditorOrientationPatches`, `IronManFlightPatches`,
`IronManFlightComputerPatches`, `IronManControlFramePatches`, `IronManRcsOrientationPatches`,
`IronManRenderPatches` and `IronManTeleportPatches`. Managed checks live in three native-free
projects: `iron-man.tests`, `iron-man-mode.tests` and `iron-man-flight.tests`.

## Scene saves and persistence

- **Native save.** Authored attachment nodes ride inside each part's serialized `PartInstance.Id`,
  so the equipment and its links are rebuilt by KSA's own loader (with the mod installed). The native
  save never records the flight mode.
- **Sidecar record.** Participant `iron-man`, version 1, restore order 90: an array of
  `{ VehicleId, Original, Current }` per configured kitten. `Original` is the captured EVA baseline
  (flight-computer settings, control part as a durable saved part reference, connector index, control
  mode) and is present only for kittens in rocket mode; `Current` is the live flight-computer setup.
  Validation caps the list at 10,000 unique, non-blank ids and checks every enum and float. Reset
  restores the EVA baseline on every active kitten and clears both sets. Replay resolves each id with
  `VehicleProvider.FindVehicle`, re-configures the kitten, re-enters Iron Man mode with engines
  disarmed and re-applies the saved settings; a control part that no longer belongs to the kitten
  produces a warning and the record is kept for recovery.
- No global settings file; activation never outlives the scene.

## Further reading

- [iron-man/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man/README.md)
- [iron-man.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/README.md)
- [scope/iron-man.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/iron-man.md) (hook inventory and acceptance checklist)
- [plans/iron-man/RESEARCH.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/plans/iron-man/RESEARCH.md)
- [plans/iron-man/FLIGHT_MODES.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/plans/iron-man/FLIGHT_MODES.md)
- [plans/iron-man/SURFACE_TELEPORT.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/plans/iron-man/SURFACE_TELEPORT.md)
- [iron-man.tests/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.tests/README.md),
  [iron-man-mode.tests/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man-mode.tests/README.md) and
  [iron-man-flight.tests/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man-flight.tests/README.md)

## Entry points

- [iron-man/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man/mod.toml)
- [iron-man/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man/Mod.cs)
- [iron-man/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man/Patcher.cs)
- [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L96)
- [iron-man.lib/IronManPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManPatches.cs)
- [iron-man.lib/IronManSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManSubmod.Saves.cs)

## See also

- [unscience overview](../overview.md)
- [Physics frame hook](../features/lib-physics-frame-hook.md) (runs every queued mode switch)
- [Save part identity](../features/lib-save-part-identity.md) (durable control-part references)
- Other kitten sub-mods: [DOH](./doh.md) and [Kitten Animations](./kitten-animations.md)
