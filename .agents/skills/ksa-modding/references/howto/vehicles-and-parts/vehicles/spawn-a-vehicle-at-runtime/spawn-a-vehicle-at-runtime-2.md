---
type: How-To
title: Spawn test drones into orbit from a state vector
description: Build a vehicle on an orbit created from a computed ecliptic state, choose Vehicle or KittenEva from the save, and attach it under the shapes lock
category: vehicles-and-parts
group: vehicles
topic: spawn-a-vehicle-at-runtime
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/spawn-and-teleport-test-craft]
techniques: [vehicle-spawn, starmap-lifecycle]
hooks: [imgui button handler, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame]
game_types:
  - KSA.Vehicle
  - KSA.KittenEva
  - KSA.Orbit
  - KSA.IParentBody
  - KSA.CelestialSystem
  - KSA.VehicleSave
  - KSA.VehicleSaves
  - KSA.DefaultVehicleSaves
  - KSA.ConstraintSim
  - KSA.PartTree
tags: [spawn, orbit, state-vector, kitten, drone, shapes-lock]
---
Outcome: spawn a craft that is already moving on a chosen trajectory, rather than parked on the ground.
KSArmory uses it for test drones that fly a timed pass over a weapons platform ("Overhead", "Head-on",
"Passing by"); the same function can clone the platform's own part tree or load a stock craft such as
`Gemini7`. It is the right pattern when the spawn state, not just the spawn place, matters.

Prerequisites: StarMap. Demonstrated by KSArmory [ksarmory/spawn-and-teleport-test-craft](../../../../mods/ksarmory/features/spawn-and-teleport-test-craft.md). Approach 1
([Spawn a vehicle from code (from the vehicle library or a hand-built part)](overview.md)) is the simpler variant that teleports instead of choosing a state.

## Background: how the game does it

- A vehicle's initial dynamics are an `Orbit`. `Orbit.CreateFromStateCci(parent, stateTime, positionCci,
  velocityCci, orbitLineColor)` builds one from a position and velocity in the parent's inertial frame
  [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1611)[^ksa-orbit]. `IParentBody.GetCce2Cci()` is the fixed rotation from the parent-centred
  ecliptic to that inertial frame [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L53)[^ksa-iparentbody].
- The constructor demands parent, orbit-or-situation, root and rotation; see approach 1 [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1426-L1470)[^ksa-vehicle].
- `KittenEva` is a `Vehicle` subclass whose `IsControllable` is a constant `true`; the base class returns
  true only when the tree contains `Control` modules [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L589) [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L63)[^ksa-kitteneva].
  KSA chooses the class on whether the save names a character.
- `Vehicle.MeanRadius` is `BoundingSphereRadiusBody`, which `UpdateCollisionGeometry` sets
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1955-L1975), so a hand-built vehicle that never passes through
  `UpdateAfterPartTreeModification` keeps radius zero.
- A vehicle that belongs to no physics bubble is picked up by `VehicleUpdateTask.AssignOrphanVehicles`
  [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L301-L330)[^ksa-vehicleupdatetask].

## Lifecycle and timing

| Where | What | Notes |
|---|---|---|
| ImGui button (`frame:draw-ui`), file bridge or scenario runner (`frame:after-on-frame`) | the whole `TestTarget.Spawn` call | main thread; the vehicle step for this frame was dispatched earlier in `PrepareFrame` and is still running |
| `ConstraintSim.UnlockShapesBlocking()` | waits for `EndVehicleUpdate` | one worker step of latency on the button press |
| next frame's `frame:dispatch-solvers` | `ExecuteNextVehicleSolvers` snapshots the drone as an orphan and `VehicleUpdateTask.Run` calls `AssignOrphanVehicles` at the start of that step | the first worker step that includes the drone; the step already in flight when you spawned does not |

Same reasoning as approach 1: the only safe place to touch shapes is behind the lock, and the cost is one
stalled frame per spawn. For the full table see [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. Work out where the craft starts. KSArmory spawns at a fixed elevation above the horizon so the drone
   starts inside the radar cone and above the dense air:

```csharp
// src/KSArmory/Ksa/TestTarget.cs L94-99
            double elevationDeg = profile switch
            {
                Profile.HeadOn => 75.0,     // steep dive onto the site
                Profile.PassingBy => 40.0,  // shallow, off to one side
                _ => 55.0,                  // Overhead: crosses well above
            };
```

2. Solve the ballistic problem so that the drone arrives at the aim point despite gravity (a vacuum
   solution; atmosphere shortens the real trajectory, which the mod accepts), keeping the platform's
   velocity so the site's rotation is inherited:

```csharp
// src/KSArmory/Ksa/TestTarget.cs L135-136
            double3 gravity = KsaWorld.GravityAt(platform, spawnEcl);
            double3 spawnVelEcl = originVel + heading * speed - gravity * (0.5 * t);
```

3. Convert the ecliptic state to the parent's inertial frame and refuse a degenerate parent, then create
   the orbit:

```csharp
// src/KSArmory/Ksa/TestTarget.cs L438-458
            if (save.Id == name) return save;
        }
        return null;
    }

    // Converts an ecliptic state into the parent body's inertial frame. Cce is the parent-centred
    // *ecliptic* frame, so it differs from Ecl only by the body's own position and velocity. Cci is
    // parent-centred *inertial*, a fixed rotation away from Cce - both are non-rotating, so the
    // same quaternion carries position and velocity.
    private static bool ToParentInertial(
        IParentBody parent, double3 posEcl, double3 velEcl, out double3 posCci, out double3 velCci)
    {
        posCci = default;
        velCci = default;

        if (parent is not IPosition parentPos || parent is not IVelocity parentVel) return false;

        double3 posCce = posEcl - parentPos.GetPositionEcl();
        double3 velCce = velEcl - parentVel.GetVelocityEcl();

        doubleQuat cce2Cci = parent.GetCce2Cci();
```

```csharp
// src/KSArmory/Ksa/TestTarget.cs L150-161
            if (parent.Mu <= 0.0)
            {
                Log.Warn("test target: parent Mu is zero, orbit would be degenerate");
                return null;
            }

            Orbit orbit = Orbit.CreateFromStateCci(
                parent,
                Universe.GetElapsedTime(),
                posCci,
                velCci,
                new byte4(255, 80, 80, 255));
```

4. Take the shapes lock and gather the part tree. A stock craft comes from `DefaultVehicleSaves` or the
   player's library; failing that the mod clones the platform with `PartTree.DeepCopy()`:

```csharp
// src/KSArmory/Ksa/TestTarget.cs L166-182
            // Held from the parts until the drone is placed. Building a vehicle mutates the shapes
            // registry, the vehicle worker holds that for its whole run, and a hook of this mod can
            // land inside the run. Racing it builds half a vehicle, which the game does not survive;
            // this waits the run out instead, at the cost of one worker step on a button press.
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

5. Choose the vehicle class from the save. A save that names a character must become a `KittenEva`, or
   the result is a plain vehicle wearing a kitten's tree that can never be controlled or zoomed:

```csharp
// src/KSArmory/Ksa/TestTarget.cs L375-399
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

6. Run the same post-build calls as approach 1 so the radius, mass and per-frame position exist:

```csharp
// src/KSArmory/Ksa/TestTarget.cs L194-209
            try
            {
                // Rebuild the part tree's derived data, then the vehicle's, in that order: the
                // second reads the SubstanceStores and inert mass properties the first rebuilds.
                // Between them they are what a tree assembled from a save has never been through.
                //
                // Neither bears on whether the drone can be *controlled*: RecomputeAllDerivedData
                // rebuilds substance stores, static mass, motor stacks and seats, and never
                // touches Controls. Controllability is a matter of class, not of cache - see
                // CreateDroneVehicle.
                drone.Parts.RecomputeAllDerivedData();
                drone.UpdateAfterPartTreeModification();
            }
            catch (Exception e)
            {
                Log.Warn($"test target: could not compute drone bounds ({e.GetType().Name}); "
```

   then call `drone.UpdatePerFrameData()` (line 242 of the file) before you read the position back.

7. Expect in the log: `spawned '<id>' - <profile>, <speed> m/s, CPA in <t>s at <miss> m, spawn range <km>`.
   A debug line prints `placed at <km> from the system (intended <km>)`; if they disagree the orbit did not take.

## Complete example

- [src/KSArmory/Ksa/TestTarget.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TestTarget.cs#L57-L270)[^ksarmory-testtarget] - the spawner; the parked variant is lines 276-311
- [src/KSArmory/Ksa/Ui/UiDebug.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiDebug.cs#L270-L295)[^ksarmory-uidebug] - the ImGui buttons that call it
- [src/KSArmory/Ksa/ScenarioRunner.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ScenarioRunner.cs#L1013)[^ksarmory-scenariorunner] - a scenario runner spawning the same drone
- [src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs#L634)[^ksarmory-bridge] - the file bridge's parked variant (same recipe as approach 1)

## Variations and gotchas

- **Bad orbit, silent failure.** The mod's comment records that placement goes through
  `Orbit.StateVectors`, so a bad orbit drops the vehicle at the frame origin instead of erroring. Check
  `parent.Mu > 0` and `Vec.IsFinite` on the state, as the mod does.
- **Kitten saves need `KittenEva`.** `KittenEva`'s constructor resolves the character with
  `ModLibrary.Get<CharacterReference>`, which throws for a renamed or unloaded character; the mod catches it
  and falls back to a plain vehicle that cannot be flown.
- **The drone has no `Control` module if it came from a kitten save.** Do not depend on controls for craft
  built this way.
- **Parked variant.** `SpawnParked` in the same file is approach 1's recipe over the same blueprint helper.
- **Cost.** `Describe` logs radius, half extents, zoom power and bubble state for the drone and every other
  vehicle; keep that out of a production spawner.

## See also

- [Overview](overview.md), [Approach 1](spawn-a-vehicle-at-runtime-1.md), [Approach 3](spawn-a-vehicle-at-runtime-3.md)
- [ksarmory/spawn-and-teleport-test-craft](../../../../mods/ksarmory/features/spawn-and-teleport-test-craft.md)
- `spawn-an-eva-kitten-from-code`, [teleport-a-vessel-by-building-a-new-orbit](../../../physics/kinematics/teleport-a-vessel-by-building-a-new-orbit/overview.md), [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../change-vehicles-without-racing-the-solver/overview.md)

[^ksa-vehicleupdatetask]: current/decomp/KSA/VehicleUpdateTask.cs (decompiled, build 2026.10.10.5554)
[^ksa-orbit]: current/decomp/KSA/Orbit.cs (decompiled, build 2026.10.10.5554)
[^ksa-kitteneva]: current/decomp/KSA/KittenEva.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-testtarget]: KSArmory: src/KSArmory/Ksa/TestTarget.cs
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-uidebug]: KSArmory: src/KSArmory/Ksa/Ui/UiDebug.cs
[^ksarmory-scenariorunner]: KSArmory: src/KSArmory/Ksa/ScenarioRunner.cs
[^ksarmory-bridge]: KSArmory: src/KSArmory/Ksa/Bridge.cs
[^ksa-iparentbody]: current/decomp/KSA/IParentBody.cs (decompiled, build 2026.10.10.5554)
