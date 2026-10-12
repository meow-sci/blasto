---
type: How-To
title: Build a KittenEva from a saved craft that names a Character, inside the shapes lock
description: KSArmory loads a craft save, builds a KittenEva instead of a Vehicle when the save names a Character (as VehicleTemplate does), holds ConstraintSim.UnlockShapesBlocking around construction and attaches it to the parent body with the derived-data calls a saved tree has never been through
category: kittens
group: characters
topic: spawn-an-eva-kitten-from-code
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/spawn-and-teleport-test-craft]
techniques: [vehicle-spawn]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame]
game_types:
  - KSA.KittenEva
  - KSA.Vehicle
  - KSA.VehicleSave
  - KSA.PartTree
  - KSA.Orbit
  - KSA.IParentBody
  - KSA.CelestialSystem
  - KSA.Universe
  - KSA.InputEvents
tags: [kitten, spawn, vehicle-save, shapes-lock, orbit, drone]
---
KSArmory spawns test drones as ordinary vehicles from craft saves. Its helper also works when the save is a kitten: a `VehicleSaveData` that carries a `Character` is built as a `KittenEva`, not a `Vehicle`, exactly as the game's own `VehicleTemplate` does. This article shows that branch and the surrounding recipe (state vector to orbit, shapes lock, attach, derived data). It is the right base when the kitten's backpack and tanks should come from a saved part tree rather than from a fresh `KittenBackPackPart` ([approach 1](spawn-an-eva-kitten-from-code-1.md) builds those by hand). Prerequisites: a StarMap mod and a craft save (a stock craft such as `Hunter`, `Banjo` or `Polaris` is a kitten save).

## Background: how the game does it

- A saved vehicle can name a character: `VehicleSaveData.Character` is a string (empty for craft) and is the only thing that distinguishes a kitten save from a vehicle save; it cannot be recovered from the part tree ([current/decomp/KSA/VehicleSaveData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaveData.cs#L13-L14)).
- `VehicleTemplate.CreateInto` chooses the class by that field: `Character != null` builds `KittenEva.CreateKittenEva(system, this, parent, id)`, otherwise `Vehicle.CreateVehicle(system, this, parent, id)` ([current/decomp/KSA/VehicleTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleTemplate.cs#L60-L105)).
- `KittenEva` overrides `IsControllable` to a constant true, and the stock kittens carry no control module, so a kitten's part tree rebuilt through `Vehicle.CreateVehicle` is a plain vehicle wearing a kitten's parts: same radius and part count, but never controllable (a comment in the mod records this; the class, not the cache, decides) ([current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L60-L63)).
- `VehicleSave.Load(IViewport)` returns the `PartTree?` of a save, and `DefaultVehicleSaves.FindSave(name)` finds a shipped one ([current/decomp/KSA/VehicleSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSave.cs#L21), [current/decomp/KSA/DefaultVehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DefaultVehicleSaves.cs#L67)).
- Construction mutates the Bepu shapes registry, which the vehicle worker owns for its whole step (`BeginVehicleUpdate` sets state -1); `ConstraintSim.UnlockShapesBlocking` spins until it is free and returns a `ShapesUnlock` token whose `Dispose` relocks ([current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L119-L153)).

## Lifecycle and timing

| Where | What runs | Lifecycle |
|---|---|---|
| Panel buttons drawn from `[StarMapAfterGui]` | `TestTarget.Spawn` | `frame:draw-ui` |
| `[StarMapAfterOnFrame]` bridge command | `TestTarget.SpawnParked` | `frame:after-on-frame` |

Every StarMap hook runs after `ExecuteNextVehicleSolvers` has started the next worker step, so the shapes lock is usually held when a button is pressed. `UnlockShapesBlocking` therefore costs the main thread at most one worker step, by design. The new vehicle is not in the step in flight: `Universe.PrepareVehicleWorkers` finds vehicles in no bubble next frame and `VehicleUpdateTask.AssignOrphanVehicles` gives it one. A teleported craft stays at its old position for the rest of the current frame. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-physics-step).

## Steps

1. **Turn a world state into a CCI orbit.** The orbit comes from `Orbit.CreateFromStateCci(parent, stateTime, positionCci, velocityCci, colour)`; refuse a parent with `Mu <= 0`. KSArmory converts an ecliptic state through the parent's `GetCce2Cci()` (position and velocity differ from the parent's by its own motion):

```csharp
// src/KSArmory/Ksa/TestTarget.cs L447-L463
    private static bool ToParentInertial(
        IParentBody parent, double3 posEcl, double3 velEcl, out double3 posCci, out double3 velCci)
    {
        posCci = default;
        velCci = default;

        if (parent is not IPosition parentPos || parent is not IVelocity parentVel) return false;

        double3 posCce = posEcl - parentPos.GetPositionEcl();
        double3 velCce = velEcl - parentVel.GetVelocityEcl();

        doubleQuat cce2Cci = parent.GetCce2Cci();
        posCci = cce2Cci * posCce;
        velCci = cce2Cci * velCce;

        return Vec.IsFinite(posCci) && Vec.IsFinite(velCci);
    }
```

   (To put the kitten beside a vessel, as [approach 1](spawn-an-eva-kitten-from-code-1.md) does, skip this and add an offset to `Orbit.StateVectors` of that vessel in one read; the `StateTime` from the same struct must be the orbit's epoch.)

2. **Load the parts and keep the character.** Prefer a shipped craft, fall back to the player's library (refreshed, so one written while the game runs is found), and return the `Character` string with the tree:

```csharp
// src/KSArmory/Ksa/TestTarget.cs L407-L420
    private static DroneBlueprint BuildDroneParts(Vehicle platform, string? craftName)
    {
        if (!string.IsNullOrEmpty(craftName))
        {
            try
            {
                // Both are genuinely nullable in KSA - FindSave returns VehicleSave? and Load
                // returns PartTree? - so the checks below are load-bearing rather than defensive.
                VehicleSave? save = DefaultVehicleSaves.FindSave(craftName) ?? LibrarySave(craftName);
                if (save is not null)
                {
                    PartTree? tree = save.Load(Program.MainViewport);
                    if (tree is not null) return new DroneBlueprint(tree, save.VehicleSaveData.Character);
                }
```

3. **Choose the class the way the game does.** A named character makes a `KittenEva` through the public eight-argument constructor with the tree's root part and the orbit; an unloadable character falls back to a plain vehicle with a warning (it cannot be flown):

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
                // The id is resolved through ModLibrary, so a renamed or unloaded character
                // throws here. A plain vehicle is still a perfectly good thing to shoot at; it
                // just cannot be flown, which is what the warning is for.
                Log.Warn($"test target: '{blueprint.Character}' is not a loadable character "
                         + $"({e.GetType().Name}); spawning a plain vehicle, which cannot be flown");
            }
        }

        return Vehicle.CreateVehicle(system, platform.Body2Cce, bodyRates: bodyRates,
                                     parent, id, blueprint.Parts.Root, orbit);
    }
```

4. **Hold the shapes lock from part load to attach,** build, add to the parent's children, then ask for the derived data a tree assembled from a save has never been through. `UpdateAfterPartTreeModification` is the only public path to the private `UpdateCollisionGeometry` that sets `BoundingSphereRadiusBody`, hence `MeanRadius` and the camera zoom; without `parent.Children.Add` the vehicle's cached position stays at the frame origin:

```csharp
// src/KSArmory/Ksa/TestTarget.cs L170-L182
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
```

```csharp
// src/KSArmory/Ksa/TestTarget.cs L204-L205, L242
                drone.Parts.RecomputeAllDerivedData();
                drone.UpdateAfterPartTreeModification();
            // ...
            drone.UpdatePerFrameData();
```

5. **Move it where you want it.** For a ground target the mod builds the craft 500 m above the platform and then calls `TeleportToLocation(body, lat, lon)`, which queues an `InputEvents.TeleportInputBuffer` entry applied in the next input phase:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L1134-L1154
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

6. **Check the log.** KSArmory logs `spawned '<id>' - ...` with the spawn range and a one-line description of the new vehicle beside the vehicles already in the scene (radius, half extents, zoom power, `controllable`). A kitten built correctly reports `controllable True`.

## Complete example

- [src/KSArmory/Ksa/TestTarget.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TestTarget.cs#L156-L263) - `Spawn`: geometry, orbit, build, attach.
- [src/KSArmory/Ksa/TestTarget.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TestTarget.cs#L276-L311) - `SpawnParked`: build above a platform, then teleport to a latitude and longitude.
- [src/KSArmory/Ksa/TestTarget.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TestTarget.cs#L361-L441) - class choice, blueprint loading and library lookup.
- [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L1134-L1153) - `TryPlaceOnSurface`.

## Variations and gotchas

- **The save, not the class, carries the kitten.** Keep `save.VehicleSaveData.Character` with the tree; losing it turns a kitten into an unflyable plain vehicle.
- **`UnlockShapesBlocking` versus waiting on the scheduler.** Approach 1 calls `JobSystems.VehicleSolver.Wait()`, KSArmory takes the shapes lock itself. Both block the main thread for at most a worker step; the lock also covers part loading inside the `using` scope. Disposing a `ShapesUnlock` on another thread throws.
- **Do not spawn into a step in flight.** If you need no blocking at all, stage the spawn to the next input phase like the stock door ([safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md)).
- **Unloaded character.** A renamed or unloaded character throws from `ModLibrary.Get<CharacterReference>` inside the constructor; the base constructor has already registered the vehicle, so a robust caller also removes the orphan ([approach 1, step 5](spawn-an-eva-kitten-from-code-1.md)).
- **Epoch.** KSArmory stamps the orbit with `Universe.GetElapsedTime()`; if you derive the state from another vehicle, use that vehicle's `StateTime` instead (pairing a position with a different clock shifts the result by velocity times the difference) ([epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)).
- **Library saves.** `VehicleSaves.Refresh()` rescans the player's library; call it before a lookup so a craft saved while the game runs is found.

## See also

- [Spawn an EVA kitten from code](overview.md), [approach 1: replicate EVADoor.CreateKittenEva](spawn-an-eva-kitten-from-code-1.md)
- Mod feature: [Spawn and teleport test craft](../../../../mods/ksarmory/features/spawn-and-teleport-test-craft.md)
- [Spawn a vehicle at runtime](../../../vehicles-and-parts/vehicles/spawn-a-vehicle-at-runtime/overview.md), [Teleport a vessel by building a new orbit](../../../physics/kinematics/teleport-a-vessel-by-building-a-new-orbit/overview.md), [Change vehicles without racing the solver](../../../vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/overview.md)
