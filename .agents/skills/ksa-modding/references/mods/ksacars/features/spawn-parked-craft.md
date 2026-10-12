---
type: Mod Feature
title: Spawn a saved craft parked at a latitude and longitude
description: Build a library craft on a placeholder orbit inside ConstraintSim.UnlockShapesBlocking, add it to its parent's Children so per-frame data runs, and set it down with Vehicle.TeleportToLocation
mod: ksacars
category: vehicles-and-parts
techniques: [starmap-lifecycle]
hooks: [starmap StarMapAfterOnFrame]
lifecycle: [frame:after-on-frame, frame:input]
game_types:
  - KSA.Vehicle
  - KSA.VehicleSave
  - KSA.VehicleSaves
  - KSA.DefaultVehicleSaves
  - KSA.PartTree
  - KSA.Orbit
  - KSA.IParentBody
  - KSA.CelestialSystem
  - KSA.ConstraintSim
  - KSA.ShapesUnlock
  - KSA.VehicleUpdateTask
  - KSA.InputEvents
tags: [spawn, vehicle-library, teleport, shapes-lock, orbit, testing, bridge]
---

KSACars' developer bridge can put a car on the ground anywhere: `spawn` with a craft name and a
latitude and longitude parks a fresh copy of a library craft there, so a test starts where it was
asked for rather than wherever a save left one.[^ksacars-bridge] `CraftSpawner.SpawnParked` is a
compact recipe for creating a vehicle from code: find the craft in a vehicle library, give it a
temporary orbit, build it while holding the physics shape registry, hang it in the orbiter tree,
and let `Vehicle.TeleportToLocation` set it down resting on the terrain.[^ksacars-craftspawner]

## How it works

1. **Find the craft.** `DefaultVehicleSaves.FindSave(name)` (Core's shipped craft) first, then the
   player's library: `VehicleSaves.Refresh()` and a case-insensitive match on `VehicleSave.Id` or
   `VehicleSaveData.Id`. `VehicleSave.Load(Program.MainViewport)` returns the `PartTree`; both
   lookups can return null.[^ksacars-craftspawner][^ksa-defaultvehiclesaves][^ksa-vehiclesaves] The cars
   reach the player library through [vehicle library presets](./vehicle-library-presets.md).[^ksacars-install-testcraft]
2. **A temporary orbit.** The new craft needs an orbit or a situation to exist
   (the `Vehicle` constructor throws otherwise). The mod takes the flown craft's ecliptic position
   500 m along its local up and its ecliptic velocity, converts both to the parent's inertial frame
   (subtract the parent's position and velocity, rotate by `GetCce2Cci()`), and calls
   `Orbit.CreateFromStateCci(parent, Universe.GetElapsedTime(), posCci, velCci, colour)`.[^ksa-orbit][^ksa-vehicle]
3. **Build it under the shape lock.** Inside `using ShapesUnlock shapes = ConstraintSim.UnlockShapesBlocking()`:
   `Vehicle.CreateVehicle(system, platform.Body2Cce, default, parent, id, parts.Root, orbit)`,
   `parent.Children.Add(craft)`, `craft.Parts.RecomputeAllDerivedData()`,
   `craft.UpdateAfterPartTreeModification()` and `craft.UpdatePerFrameData()`.
4. **Set it down.** `craft.TeleportToLocation(body, lat, lon)` computes a resting kinematic state
   from the craft's bounding box at `Universe.GetNextSimStep().NextTime` and queues it on
   `InputEvents.TeleportInputBuffer`.[^ksa-vehicle]

**Why the shape lock.** `VehicleUpdateTask.Run` holds the shape registry for the whole vehicle step
(`ConstraintSim.BeginVehicleUpdate` at the start, `EndVehicleUpdate` in its `finally`), and the
plain `ConstraintSim.UnlockShapes` throws "The shapes registry cannot be mutated while the vehicle
update is stepping; stage the change and apply it at the frame sync point" while it is held.
`UpdateAfterPartTreeModification` reaches that plain call through `UpdateCollisionGeometry`.
`UnlockShapesBlocking` instead spins until the step releases the registry, then takes it for the
calling thread; the nested `UnlockShapes` inside the vehicle build is re-entrant for the same
thread.[^ksa-constraintsim][^ksa-vehicleupdatetask][^ksa-vehicle]

**Why `parent.Children.Add`.** `CelestialSystem.UpdatePerFrameData` walks the orbiter tree from each
non-orbiting parent (`IParentBody.UpdatePerFrameDataTree` recurses through `Children`). A vehicle
that is not in its parent's `Children` is never visited, so its cached per-frame position stays
unset; the mod's notes describe it as a craft sitting at the solar system barycentre. KSA's own
spawn path does the same two calls (`InputEvents.CreateVehicleData.Apply`, snippet
below).[^ksa-celestialsystem][^ksa-iparentbody][^ksacars-notes][^ksa-inputevents]

## Lifecycle and timing

| Step | Where | Physics |
|---|---|---|
| `spawn` command handled | `[StarMapAfterOnFrame]` -> `Bridge.Update` -> `Bridge.Spawn` -> `CraftSpawner.SpawnParked` (main thread, after `PostRender`) | vehicle step in flight |
| Shape lock taken | `UnlockShapesBlocking` spins until `VehicleUpdateTask.Run` calls `EndVehicleUpdate` | waits for the step to finish |
| Craft built, attached, per-frame data filled | same call | step finished but not yet applied |
| Teleport applied | next frame, `Program.PrepareFrame` -> `InputEvents.ApplyInputEvents` (after `ApplyVehicleSolvers`) | settled |
| First physics step for the craft | next frame's `ExecuteNextVehicleSolvers` | dispatched |

Because `StarMapAfterOnFrame` runs while the next step is in flight, a spawn there stalls the main
thread until the vehicle step completes; the bridge spawns once per command, so this is a single
hitch. KSA's own vehicle creation (`Universe` and `VehicleLaunchMenu` queue an
`InputEvents.CreateVehicleData`) instead runs in `ApplyInputEvents`, in the settled window, where
the registry is free and nothing has to wait.[^ksa-inputevents] The teleport uses the in-flight
step's `NextTime`, which is the universe time once the next frame's `ApplyVehicleSolvers` has
run, so the craft lands at the epoch the next step starts from.[^ksa-vehicle]

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/CraftSpawner.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CraftSpawner.cs#L17-L52) | `SpawnParked`: orbit, shape lock, create, attach, teleport |
| [src/KSACars/Ksa/CraftSpawner.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CraftSpawner.cs#L56-L85) | `LoadParts` / `LibraryCraft`: stock library, then the player's |
| [src/KSACars/Ksa/CraftSpawner.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CraftSpawner.cs#L90-L106) | `ToParentInertial`: Ecl to the parent's Cci frame |
| [src/KSACars/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Bridge.cs#L181-L195) | The `spawn` command |
| [docs/KSA-MODDING-NOTES.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/docs/KSA-MODDING-NOTES.md) | The spawn recipe and the `Children` requirement |

## Snippets

```csharp
// src/KSACars/Ksa/CraftSpawner.cs L17-L52
    public static Vehicle? SpawnParked(Vehicle platform, string craftName, string id, Celestial body,
                                       double latitudeDeg, double longitudeDeg)
    {
        try
        {
            if (Universe.CurrentSystem is not { } system || platform.Parent is not { } parent) return null;
            if (LoadParts(craftName) is not { } parts) return null;

            double3 up = KsaWorld.LocalUp(platform);
            double3 spawnEcl = KsaWorld.PositionEcl(platform) + up * 500.0;
            if (!ToParentInertial(parent, spawnEcl, KsaWorld.VelocityEcl(platform), out double3 posCci, out double3 velCci))
            {
                return null;
            }

            Orbit orbit = Orbit.CreateFromStateCci(parent, Universe.GetElapsedTime(), posCci, velCci,
                                                   new byte4(255, 80, 80, 255));

            using ShapesUnlock shapes = ConstraintSim.UnlockShapesBlocking();
            Vehicle craft = Vehicle.CreateVehicle(system, platform.Body2Cce, bodyRates: default,
                                                  parent, id, parts.Root, orbit);
            parent.Children.Add(craft);
            craft.Parts.RecomputeAllDerivedData();
            craft.UpdateAfterPartTreeModification();
            craft.UpdatePerFrameData();
            craft.TeleportToLocation(body, latitudeDeg, longitudeDeg);

            Log.Info($"parked '{id}' ({craftName}) at {latitudeDeg:F4}, {longitudeDeg:F4} on {body.Id}");
            return craft;
        }
        catch (Exception e)
        {
            Log.Error("parked spawn failed", e);
            return null;
        }
    }
```

```csharp
// current/decomp/KSA/InputEvents.cs L929-L940
		public void Apply()
		{
			if (Template == null)
			{
				Vehicle vehicle = Vehicle.CreateVehicle(System, Body2Cce, BodyRates, System.HomeBody, Id, Root, Orbit);
				Parent.Children.Add(vehicle);
				vehicle.UpdatePerFrameData();
				Program.ControlledVehicle = vehicle;
				Program.MainViewport.MapCamera.SetFollow(vehicle, tidalLocking: false, changeControl: true, alert: false);
				Program.MainViewport.BaseCamera.SetFollow(vehicle, tidalLocking: false, changeControl: true, alert: false);
			}
		}
```

## Game APIs touched

| API | Purpose |
|---|---|
| `Vehicle.CreateVehicle(CelestialSystem, doubleQuat, double3, IParentBody, string, Part, Orbit)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1426-L1429)) | Builds and registers the vehicle; needs an orbit or a situation |
| `Vehicle.UpdateAfterPartTreeModification` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1894-L1908)) | Mass properties, analytic state, collision geometry (plain `UnlockShapes` at L1946) |
| `Vehicle.TeleportToLocation` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4219-L4233)) | Resting state at lat/lon, queued on `TeleportInputBuffer` |
| `ConstraintSim.UnlockShapes`, `UnlockShapesBlocking`, `BeginVehicleUpdate` ([current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L111-L153)) | The shape-registry lock the vehicle step holds |
| `VehicleUpdateTask.Run` ([current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L261-L298)) | Holds the registry for the whole step |
| `CelestialSystem.UpdatePerFrameData` ([current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L274-L285)), `IParentBody.UpdatePerFrameDataTree` ([current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L110-L125)) | Why the craft must be in `Children` |
| `InputEvents.CreateVehicleData.Apply` ([current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L929-L940)) | KSA's own buffered spawn, applied in the settled window |
| `Orbit.CreateFromStateCci` ([current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1611)) | The temporary orbit |
| `DefaultVehicleSaves.FindSave`, `VehicleSaves.Refresh` / `AsSpan`, `VehicleSave.Load(IViewport)` | Craft lookup and part-tree load |

## Pitfalls and version notes

- **Spawning from a frame hook waits for the physics step.** Without `UnlockShapesBlocking` the
  build throws; with it, the main thread spins until the step ends. For something done every frame,
  queue an `InputEvents.CreateVehicleData` instead and let `ApplyInputEvents` build it in the
  settled window.[^ksa-inputevents]
- **Add the craft to `parent.Children`.** `CreateVehicle` registers it with the system but does not
  attach it to the orbiter tree; both KSA spawn paths add it themselves.[^ksa-inputevents][^ksacars-notes]
- **The orbit is only a placeholder.** It exists so the constructor accepts the craft; the queued
  teleport replaces orbit, attitude and rates at the next `ApplyInputEvents`.
- **`TeleportToLocation` faces the craft east**, stands it on a launch pad inside a pad's footprint
  and on the seabed over water, per the mod's notes; see the
  [craft mover](./craft-mover-mouse-pick-place.md).[^ksacars-notes]
- **Library lookups are nullable.** `FindSave` and `Load` both return null for a missing or
  unloadable craft; the mod logs and returns null rather than throwing into the frame.

## See also

- [File-drop command bridge](./file-drop-command-bridge.md) (the `spawn` command)
- [Vehicle library presets](./vehicle-library-presets.md) (where the cars come from)
- [Craft mover with mouse picking and ground placement](./craft-mover-mouse-pick-place.md) (the same teleport)
- [Once-per-frame car step and registry](./frame-step-latch-and-car-registry.md) (how a spawned car is picked up)
- [KSACars overview](../overview.md)
- [UnlockShapesBlocking around vehicle construction](../../../howto/physics/colliders/mutate-the-shared-bepu-shape-registry/mutate-the-shared-bepu-shape-registry-3.md) - how-to article demonstrating this feature
- [Hold the shapes lock around a vehicle build](../../../howto/vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/change-vehicles-without-racing-the-solver-1.md) - how-to article demonstrating this feature
- [Spawn a parked copy of a library craft at a latitude and longitude](../../../howto/vehicles-and-parts/vehicles/spawn-a-vehicle-at-runtime/spawn-a-vehicle-at-runtime-1.md) - how-to article demonstrating this feature

[^ksacars-craftspawner]: src/KSACars/Ksa/CraftSpawner.cs
[^ksacars-bridge]: src/KSACars/Ksa/Bridge.cs
[^ksacars-notes]: docs/KSA-MODDING-NOTES.md
[^ksacars-install-testcraft]: tools/install-testcraft.sh
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled)
[^ksa-vehicleupdatetask]: KSA.VehicleUpdateTask (decompiled)
[^ksa-inputevents]: KSA.InputEvents (decompiled)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled)
[^ksa-iparentbody]: KSA.IParentBody (decompiled)
[^ksa-orbit]: KSA.Orbit (decompiled)
[^ksa-defaultvehiclesaves]: KSA.DefaultVehicleSaves (decompiled)
[^ksa-vehiclesaves]: KSA.VehicleSaves (decompiled)
