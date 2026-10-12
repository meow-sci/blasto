---
type: Mod Feature
title: Spawn test drones and teleport craft from the panel, a click or a script
description: Test drones are built from a stock or library craft save on an orbit solved backwards from the requested pass, created under the engine's shapes-registry lock and added to the parent body's children, while craft are moved by queuing the engine's own TeleportToLocation
mod: ksarmory
category: vehicles-and-parts
techniques:
  - vehicle-spawn
  - orbit-from-state-vector
  - shapes-registry-lock
  - teleport-input-buffer
  - imgui-window
  - cursor-ray
hooks:
  - starmap StarMapAfterGui (panel buttons and UpdateWorldClicks)
  - harmony-prefix KSA.Program.OnFrameCelestials (UpdateWorldClicks via StepOnce, HUD hidden)
  - starmap StarMapAfterOnFrame (bridge spawn and site commands)
lifecycle:
  - frame:draw-ui
  - frame:celestials
  - frame:after-on-frame
  - frame:input
  - frame:dispatch-solvers
  - physics:vehicle-solver
game_types:
  - KSA.Vehicle
  - KSA.KittenEva
  - KSA.Orbit
  - KSA.ConstraintSim
  - KSA.ShapesUnlock
  - KSA.PartTree
  - KSA.VehicleSave
  - KSA.DefaultVehicleSaves
  - KSA.VehicleSaves
  - KSA.VehicleTemplate
  - KSA.IParentBody
  - KSA.Celestial
  - KSA.CelestialSystem
  - KSA.Universe
  - KSA.InputEvents
  - KSA.VehicleUpdateTask
  - KSA.Program
  - Brutal.ImGuiApi.ImGui
tags: [spawn, vehicle, drone, test-target, teleport, orbit, kitten-eva, shapes-lock, imgui, debug-tools]
---

The debug pane of KSArmory's panel spawns a drone that flies a timed pass over the selected weapons
system ("Overhead", "Head-on", "Passing by"), and a "move craft" tool picks any craft up with one
click and sets it down on the ground under the next. A file bridge and the scenario runners reuse
both, so a test range can be laid out and shot at without flying anything into place.[^ksarmory-uidebug][^ksarmory-craftmover][^ksarmory-bridge][^ksarmory-scenariorunner][^ksarmory-gunneryscenario]

Two engine techniques do the work: a vehicle is **built** from a craft save's `PartTree` on an
orbit created from a state vector, inside the engine's shapes-registry lock, and then attached to
the world by adding it to the parent body's `Children`; a craft is **moved** with
`Vehicle.TeleportToLocation`, which queues a teleport the engine applies itself on the next
frame.[^ksarmory-testtarget][^ksa-vehicle]

## How it works

### Spawning a drone (`TestTarget.Spawn`)

| Step | Detail |
|---|---|
| Geometry | Spawn point at a fixed elevation above the horizon (75 deg head-on, 55 overhead, 40 passing) and `speed * t` out; closest-approach point at the platform, above it, or offset to the side[^ksarmory-testtarget] |
| Velocity | Platform velocity + `heading * speed` - `0.5 * g * t`: the vacuum ballistic correction `v0 = d/t - 0.5 g t`, so the drone does not fall 18 km over a 60 s run; it still lands short in air, which the code accepts[^ksarmory-testtarget] |
| Frame | Ecliptic state minus the parent's `GetPositionEcl`/`GetVelocityEcl` gives CCE; `IParentBody.GetCce2Cci()` rotates both into CCI (`ToParentInertial`)[^ksarmory-testtarget][^ksa-iparentbody] |
| Orbit | `Orbit.CreateFromStateCci(parent, Universe.GetElapsedTime(), posCci, velCci, colour)`, refused when `parent.Mu <= 0`[^ksarmory-testtarget][^ksa-orbit] |
| Parts | `DefaultVehicleSaves.FindSave(name)` or a fresh `VehicleSaves.Refresh()` scan of the player's library, then `VehicleSave.Load(Program.MainViewport)`; falls back to `platform.Parts.DeepCopy()`[^ksarmory-testtarget] |
| Class | A save that names a `Character` is built as `KittenEva`, otherwise `Vehicle.CreateVehicle(system, body2Cce, bodyRates, parent, id, root, orbit)`, matching `VehicleTemplate`'s own branch[^ksarmory-testtarget][^ksa-vehicletemplate] |
| Attach | `parent.Children.Add(drone)`; then `Parts.RecomputeAllDerivedData()`, `UpdateAfterPartTreeModification()` (the only public path to the private `UpdateCollisionGeometry` that sets `BoundingSphereRadiusBody`, hence `MeanRadius` and camera zoom), and `UpdatePerFrameData()`[^ksarmory-testtarget][^ksa-vehicle] |

Everything from the part load to the attach runs inside `using ShapesUnlock shapes =
ConstraintSim.UnlockShapesBlocking();`. Building a vehicle mutates the Bepu shapes registry, and
the vehicle worker owns that registry for its whole run: `VehicleUpdateTask.Run` starts with
`ConstraintSim.BeginVehicleUpdate()` (state -1) and ends with `EndVehicleUpdate()`
(VehicleUpdateTask.cs L261-L296). `UnlockShapesBlocking` spins while the state is -1, so a spawn from
a mod hook waits out the worker step instead of building half a vehicle under it
(ConstraintSim.cs L111-L153).[^ksarmory-testtarget][^ksa-constraintsim][^ksa-vehicleupdatetask]

`SpawnParked` uses the same chain 500 m above the platform and then calls `TeleportToLocation`
onto a latitude and longitude, which is how the bridge's `spawn` command parks a stock craft as a
ground target.[^ksarmory-testtarget][^ksarmory-bridge]

### Moving a craft (`CraftMover`, `KsaWorld.TryPlaceOnSurface`)

- Each frame: skip if ImGui wants the mouse; project every craft to the screen
  (`TryProjectAhead`) with a pick radius equal to its apparent size clamped to 14-140 px; the
  nearest within reach is the hover (`Picking.NearestWithin`).[^ksarmory-craftmover][^ksarmory-picking]
- First click holds that craft; the second resolves a target: a hovered craft's own footing
  (`TryCraftSurfacePoint`), otherwise the cursor ray on the terrain (`TryCursorGroundPoint`), both
  returned as body, latitude and longitude via `Celestial.GetLatitudeFromCce`/`GetLongitudeFromCce`.
  Within 12 m of where it is, or on itself, means "leave it".[^ksarmory-craftmover][^ksarmory-ksaworld][^ksa-celestial]
- `TryPlaceOnSurface` finds the body by `Id` in `Universe.CurrentSystem` and calls
  `craft.TeleportToLocation(body, lat, lon)`.[^ksarmory-ksaworld]
- While held, gizmo rings mark the craft's footprint and the landing spot; the craft itself does not
  follow the cursor, because each placement is a buffered engine event that re-initialises the
  vehicle's kinematic state.[^ksarmory-craftmover]

`TeleportToLocation` builds an `InitialKinematicState` for the location at
`Universe.GetNextSimStep().NextTime` from the craft's bounding box, so the hull rests on the
ground, and adds a `TeleportInputData` to `InputEvents.TeleportInputBuffer`
(Vehicle.cs L4219-L4233).[^ksa-vehicle][^ksa-inputevents]

## Lifecycle and timing

| Value | Where (build 5554) | What runs |
|---|---|---|
| `frame:draw-ui` | `[StarMapAfterGui]`: the panel's buttons inside `_ui.Draw()`, and `UpdateWorldClicks` after the panel (KSArmoryMod.cs L605-L609) | `TestTarget.Spawn`, `TryPlaceOnSurface` from the body buttons, `CraftMover.Update`[^ksarmory-mod][^ksarmory-uidebug] |
| `frame:celestials` | HUD hidden: `StepOnce` from the `OnFrameCelestials` prefix calls `UpdateWorldClicks` when the UI pass did not run (L354) | Clicks still work with F2 |
| `frame:after-on-frame` | `[StarMapAfterOnFrame]` runs `Bridge.Update` (L292) | Bridge `spawn` (`SpawnParked`) and `site` (`TryPlaceOnSurface`) commands[^ksarmory-bridge] |
| `frame:input` | Next frame's `PrepareFrame`: `InputEvents.ApplyInputEvents()` (Program.cs L2181) applies `TeleportInputBuffer` (InputEvents.cs L1138) | The teleport actually happens[^ksa-program][^ksa-inputevents] |
| `frame:dispatch-solvers` | `Universe.PrepareVehicleWorkers` collects vehicles in no bubble via `TakeUpdateStateIfOrphaned` (Universe.cs L2141-L2155) | The new drone is found[^ksa-universe] |
| `physics:vehicle-solver` | `VehicleUpdateTask.Run` -> `AssignOrphanVehicles` (L270, L301) | The drone gets a physics bubble[^ksa-vehicleupdatetask] |

Every mod hook runs after `ExecuteNextVehicleSolvers` has started the next worker step, so the
shapes lock is usually held when a button is pressed; the mod pays at most one worker step of
blocking on the main thread for a button press, by design.[^ksarmory-testtarget][^ksa-constraintsim]
A teleported craft is still at its old position for the rest of the current frame, which is why
the bridge's `site` command answers on a later frame.[^ksarmory-bridge]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/TestTarget.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TestTarget.cs#L57-L269) | `Spawn`: pass geometry, ballistic velocity, orbit, build, attach |
| [src/KSArmory/Ksa/TestTarget.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TestTarget.cs#L276-L311) | `SpawnParked` |
| [src/KSArmory/Ksa/TestTarget.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TestTarget.cs#L375-L463) | Vehicle class choice, save lookup, ECL to CCI |
| [src/KSArmory/Ksa/CraftMover.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CraftMover.cs) | Hover, pick up, put down, markers |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L1126-L1160) | `TryPlaceOnSurface` |
| [src/KSArmory/Ksa/Ui/UiDebug.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiDebug.cs#L198-L296) | Body buttons, drone type slider, spawn buttons |
| [src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs#L623-L637) | Bridge `spawn` command |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L410-L431) | `UpdateWorldClicks` |
| [tools/install-testcraft.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/install-testcraft.sh) | Writes a launch-ready test craft into the player's vehicle library |

## Snippets

```csharp
// src/KSArmory/Ksa/TestTarget.cs L156-L205
            Orbit orbit = Orbit.CreateFromStateCci(
                parent,
                Universe.GetElapsedTime(),
                posCci,
                velCci,
                new byte4(255, 80, 80, 255));
            // ...
            using ShapesUnlock shapes = ConstraintSim.UnlockShapesBlocking();

            DroneBlueprint blueprint = BuildDroneParts(platform, craftName);

            string id = $"AD Test Drone {++_counter}";
            Vehicle drone = CreateDroneVehicle(blueprint, system, platform, parent, id, orbit, bodyRates);

            // Constructing the Vehicle is not enough to put it in the world: without this,
            // CelestialSystem.UpdatePerFrameData never walks it, so its cached Ecl position stays
            // at the frame origin and it neither moves nor can be seen. A bubble it does not need
            // - Universe.PrepareVehicleWorkers collects every vehicle in no bubble and
            // VehicleUpdateTask.AssignOrphanVehicles gives it one before the step it was found on.
            parent.Children.Add(drone);
            // ...
                drone.Parts.RecomputeAllDerivedData();
                drone.UpdateAfterPartTreeModification();
```

```csharp
// src/KSArmory/Ksa/TestTarget.cs L375-L399
    private static Vehicle CreateDroneVehicle(
        DroneBlueprint blueprint, CelestialSystem system, Vehicle platform,
        IParentBody parent, string id, Orbit orbit, double3 bodyRates)
    {
        if (!string.IsNullOrEmpty(blueprint.Character))
        {
            try
            {
                return new KittenEva(system, blueprint.Character, platform.Body2Cce,
                                     bodyRates: bodyRates, parent, id,
                                     blueprint.Parts.Root, orbit);
            }
            catch (Exception e)
            {
                // ...
                Log.Warn($"test target: '{blueprint.Character}' is not a loadable character "
                         + $"({e.GetType().Name}); spawning a plain vehicle, which cannot be flown");
            }
        }

        return Vehicle.CreateVehicle(system, platform.Body2Cce, bodyRates: bodyRates,
                                     parent, id, blueprint.Parts.Root, orbit);
    }
```

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L1134-L1153
    public static bool TryPlaceOnSurface(Vehicle craft, string bodyName,
                                         double latitudeDeg, double longitudeDeg)
    {
        if (!IsAlive(craft)) return false;
        if (!double.IsFinite(latitudeDeg) || !double.IsFinite(longitudeDeg)) return false;

        try
        {
            if (Universe.CurrentSystem is not { } system) return false;

            for (int i = 0; i < system.Count; i++)
            {
                if (system.GetIndex(i) is not Celestial body) continue;
                if (body.Id != bodyName) continue;

                craft.TeleportToLocation(body, latitudeDeg, longitudeDeg);
                return true;
            }

            return false;
        }
```

```csharp
// current/decomp/KSA/Vehicle.cs L4219-L4233
	public void TeleportToLocation(Celestial celestial, double lat, double lon)
	{
		double3 double5 = new double3(_props.BoundingBoxAsmb.HalfWidth, _props.BoundingBoxAsmb.HalfHeight, _props.BoundingBoxAsmb.HalfLength);
		double3 boundsMinAsmb = MassToGeometryAsmb - double5;
		double3 boundsMaxAsmb = MassToGeometryAsmb + double5;
		InitialKinematicState initialKinematicStateForLocation = GetInitialKinematicStateForLocation(celestial, Universe.GetNextSimStep().NextTime, lat, lon, boundsMinAsmb, boundsMaxAsmb, double3.Zero, Orbit.OrbitLineColor);
		InputEvents.TeleportInputData data = new InputEvents.TeleportInputData
		{
			Vehicle = this,
			Orbit = initialKinematicStateForLocation.Orbit,
			Body2Cce = initialKinematicStateForLocation.Body2Cce,
			BodyRates = initialKinematicStateForLocation.BodyRates
		};
		InputEvents.TeleportInputBuffer.Add(data);
	}
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Vehicle.CreateVehicle(system, body2Cce, bodyRates, parent, id, root, orbit)`, `UpdateAfterPartTreeModification`, `UpdatePerFrameData`, `MeanRadius`, `IsControllable` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1426) | Build and initialise the drone; `MeanRadius` is `BoundingSphereRadiusBody` (L535), set in the private `UpdateCollisionGeometry` (L1944) |
| `Vehicle.TeleportToLocation` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4219-L4233) | Queue a bounding-box-aware placement |
| `KittenEva(system, characterId, ...)`, `IsControllable => true` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L63-L78) | Character saves must be this class to be flyable |
| `VehicleTemplate` character branch | [current/decomp/KSA/VehicleTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleTemplate.cs#L105) | The engine's own choice the mod mirrors |
| `Orbit.CreateFromStateCci` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1611) | Orbit from a CCI state vector |
| `ConstraintSim.UnlockShapesBlocking`, `BeginVehicleUpdate`, `EndVehicleUpdate` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L111-L153) | Shapes-registry ownership between worker and main thread |
| `VehicleUpdateTask.Run`, `AssignOrphanVehicles` | [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L261-L310) | Holds the lock; gives orphans a bubble |
| `Universe.PrepareVehicleWorkers`, `GetElapsedTime`, `GetNextSimStep`, `CurrentSystem` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2141-L2155) | Orphan collection; times for orbit and teleport |
| `InputEvents.TeleportInputBuffer`, `ApplyInputEvents` | [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1114-L1138) | Where the queued teleport is applied |
| `Program.PrepareFrame` (`ApplyInputEvents` call), `MainViewport` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2181) | Teleport timing; viewport passed to `VehicleSave.Load` |
| `DefaultVehicleSaves.FindSave`, `VehicleSaves.Refresh`, `VehicleSaves.AsSpan` | [current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs#L229-L280) | Stock craft and the player's library ([current/decomp/KSA/DefaultVehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DefaultVehicleSaves.cs#L67)) |
| `IParentBody.GetCce2Cci`, `Children` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L27-L53) | Frame rotation; attaching the drone to the world |
| `Celestial.GetLatitudeFromCce`, `GetLongitudeFromCce` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L691-L726) | Cursor ground point to latitude and longitude |

## Pitfalls and version notes

- **`new Vehicle` is not in the world.** Without `parent.Children.Add`, the system's per-frame data
  never walks it, its ecliptic position stays at the frame origin and it neither moves nor draws.[^ksarmory-testtarget]
- **Zero radius means no zoom.** A tree assembled outside the engine's spawn path keeps
  `MeanRadius = 0` until `UpdateAfterPartTreeModification` runs `UpdateCollisionGeometry`; the
  camera scales zoom by `MeanRadius`. The mod logs each drone's radius, extents and zoom power beside
  every craft already in the scene to diagnose this.[^ksarmory-testtarget][^ksa-vehicle]
- **Kittens are a class, not a cache.** Stock "Hunter", "Banjo" and "Polaris" carry a character;
  rebuilt through `Vehicle.CreateVehicle` they have no `Control` module and `IsControllable` stays
  false, so they must be built as `KittenEva`. A character id that does not resolve throws, and the
  mod falls back to a plain vehicle with a warning.[^ksarmory-testtarget][^ksa-kitteneva]
- **A bad orbit fails silently.** Placement goes through `Orbit.StateVectors`, so a degenerate orbit
  drops the vehicle at the frame origin; the mod logs the CCI state and refuses `Mu <= 0`.[^ksarmory-testtarget]
- **Vacuum aim.** The drone's velocity ignores drag, so shallow passes arrive kilometres short; steep
  head-on passes are least affected.[^ksarmory-testtarget]
- **Teleport is one frame late and not continuous.** The queued event lands at the next frame's
  input stage; teleporting every frame would rebuild the kinematic state and orbit each time, which
  is why the mover holds the choice rather than the craft.[^ksarmory-craftmover][^ksa-inputevents]
- **Placement re-derives height.** `TeleportToLocation` places from latitude, longitude and the
  bounding box, so placing a craft at its own spot can drop it (on a launch pad it sits metres
  below); the mover treats "within 12 m" as no-op.[^ksarmory-craftmover]
- **Stock saves are not writable.** `DefaultVehicleSaves` reads the game's install folder; test craft
  go into the player's vehicle library (`tools/install-testcraft.sh`), which `VehicleSaves.Refresh`
  re-reads.[^ksarmory-install-testcraft][^ksa-defaultvehiclesaves][^ksa-vehiclesaves]

## See also

- [Cursor-ray world click tools](./cursor-ray-world-click-tools.md)
- [Operator panel in ImGui](./operator-panel-imgui.md)
- [Unattended scenario runner](./unattended-scenario-runner.md)
- [File command bridge and MCP](./file-command-bridge-and-mcp.md)
- [Safe vehicle destruction and part failure](./safe-vehicle-destruction-and-part-failure.md)
- [Gizmo world overlays](./gizmo-world-overlays.md)
- [Build a KittenEva from a saved craft that names a Character, inside the shapes lock](../../../howto/kittens/characters/spawn-an-eva-kitten-from-code/spawn-an-eva-kitten-from-code-2.md) - how-to article demonstrating this feature
- [Queue TeleportToLocation once per action](../../../howto/physics/kinematics/place-a-vessel-on-a-surface-at-latitude-and-longitude/place-a-vessel-on-a-surface-at-latitude-and-longitude-1.md) - how-to article demonstrating this feature
- [Orbit solved backwards from a requested pass](../../../howto/physics/kinematics/teleport-a-vessel-by-building-a-new-orbit/teleport-a-vessel-by-building-a-new-orbit-2.md) - how-to article demonstrating this feature
- [Hold the shapes lock around a vehicle build](../../../howto/vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/change-vehicles-without-racing-the-solver-1.md) - how-to article demonstrating this feature
- [Spawn test drones into orbit from a state vector](../../../howto/vehicles-and-parts/vehicles/spawn-a-vehicle-at-runtime/spawn-a-vehicle-at-runtime-2.md) - how-to article demonstrating this feature

[^ksarmory-testtarget]: src/KSArmory/Ksa/TestTarget.cs
[^ksarmory-craftmover]: src/KSArmory/Ksa/CraftMover.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-uidebug]: src/KSArmory/Ksa/Ui/UiDebug.cs
[^ksarmory-bridge]: src/KSArmory/Ksa/Bridge.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-picking]: src/KSArmory/Sim/Picking.cs
[^ksarmory-scenariorunner]: src/KSArmory/Ksa/ScenarioRunner.cs
[^ksarmory-gunneryscenario]: src/KSArmory/Ksa/GunneryScenario.cs
[^ksarmory-install-testcraft]: tools/install-testcraft.sh
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-kitteneva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatetask]: KSA.VehicleUpdateTask (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicletemplate]: KSA.VehicleTemplate (decompiled, build 2026.10.10.5554)
[^ksa-vehiclesaves]: KSA.VehicleSaves (decompiled, build 2026.10.10.5554)
[^ksa-defaultvehiclesaves]: KSA.DefaultVehicleSaves (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
