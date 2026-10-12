---
type: How-To
title: Orbit solved backwards from a requested pass
description: Spawn a drone on a computed pass by solving the state vector backwards from a closest-approach point, converting ecliptic to Cci, applying a ballistic correction and attaching the vessel to its parent
category: physics
group: kinematics
topic: teleport-a-vessel-by-building-a-new-orbit
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/spawn-and-teleport-test-craft]
techniques: [harmony-prefix, orbit-from-state-vector]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:input, frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.Orbit, KSA.Universe, KSA.IParentBody, KSA.KittenEva]
tags: [physics, kinematics, teleport, orbit, spawn, cci, ecliptic, test-drone]
---

Outcome: a test craft appears on a trajectory that passes a chosen point at a chosen time: overhead, head-on or passing by.
KSArmory's debug pane uses it to spawn drones for a weapons system to shoot at without flying anything into position.
Prerequisites: StarMap. The same pane's "move craft" tool uses `TeleportToLocation`, covered in
[place a vessel on a surface](../place-a-vessel-on-a-surface-at-latitude-and-longitude/place-a-vessel-on-a-surface-at-latitude-and-longitude-1.md).

## Background: how the game does it

A vehicle needs an orbit to exist (the constructor throws without an orbit or situation), and its authority is that orbit
until the physics bubble takes over. `Orbit.CreateFromStateCci(parent, stateTime, positionCci, velocityCci, color)` builds
one from a state in the parent's inertial frame. Cce (parent-centred, ecliptic-aligned) differs from Ecl only by the body's own
position and velocity; Cci is a fixed rotation away from Cce, and both are non-rotating, so one quaternion carries position and
velocity.[^ksa-orbit] A `parent.Mu <= 0` makes a degenerate orbit and is refused.

A vehicle built in a mod is not in the world until it is added to its parent's `Children` list, because
`CelestialSystem.UpdatePerFrameData` walks the orbiter tree; `Universe.PrepareVehicleWorkers` then collects every vehicle in no
bubble through `TakeUpdateStateIfOrphaned`, and `VehicleUpdateTask.AssignOrphanVehicles` gives it a bubble before the step.[^ksa-universe]

## Lifecycle and timing

| Value | Where | What runs |
|---|---|---|
| `frame:draw-ui` | `StarMapAfterGui`: the panel's buttons | `TestTarget.Spawn`, the move tool |
| `frame:after-on-frame` | `StarMapAfterOnFrame` | The file bridge's `spawn` and `site` commands |
| `frame:input` | Next frame's `PrepareFrame`: `InputEvents.ApplyInputEvents()` | Any queued `TeleportInputBuffer` item is applied |
| `frame:dispatch-solvers` | `Universe.PrepareVehicleWorkers` | The new drone is found as an orphan |

Building a vehicle mutates the Bepu shapes registry, so the spawn runs inside `ConstraintSim.UnlockShapesBlocking()`: a hook
runs after `ExecuteNextVehicleSolvers` has started the step, so the lock is usually held and a button press blocks the
main thread for at most one worker step (see [the registry topic](../../colliders/mutate-the-shared-bepu-shape-registry/mutate-the-shared-bepu-shape-registry-3.md)).
See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Geometry in Ecl. The spawn point is at a fixed elevation above the horizon (75 degrees head-on, 55 overhead, 40 passing)
   and `speed * t` out; the closest-approach point is at the platform, above it, or offset to the side. Aim at the pass point:

```csharp
// src/KSArmory/Ksa/TestTarget.cs L101-L120
            double3 closestPointEcl = profile switch
            {
                Profile.HeadOn => originEcl,
                Profile.PassingBy => originEcl + north * missDistance + up * (missDistance * 0.5),
                _ => originEcl + up * missDistance,
            };

            double t = secondsToClosestApproach;
            double spawnRange = speed * t;

            // Direction from the system to the spawn point: elevation above the horizon,
            // azimuth around it.
            double elev = double.DegreesToRadians(elevationDeg);
            double3 spawnDir = up * Math.Sin(elev) + azimuth * Math.Cos(elev);

            double3 spawnEcl = originEcl + Vec.Unit(spawnDir) * spawnRange;

            // Aim it at the pass point; |closestPoint - spawn| is close enough to spawnRange
            // that the requested speed still lands the timing.
            double3 heading = Vec.Unit(closestPointEcl - spawnEcl);
```

2. Velocity. Platform velocity plus `heading * speed` minus `0.5 * g * t`: the vacuum ballistic correction `v0 = d/t - 0.5 g t`, so the
   drone does not fall 18 km over a 60 s run. It still lands short in air, which the code accepts:

```csharp
// src/KSArmory/Ksa/TestTarget.cs L135-L138
            double3 gravity = KsaWorld.GravityAt(platform, spawnEcl);
            double3 spawnVelEcl = originVel + heading * speed - gravity * (0.5 * t);

            if (!ToParentInertial(parent, spawnEcl, spawnVelEcl, out double3 posCci, out double3 velCci))
```

3. Convert Ecl to the parent's Cci. Subtract the parent's `GetPositionEcl`/`GetVelocityEcl` for Cce, then rotate both with
   `IParentBody.GetCce2Cci()`:

```csharp
// src/KSArmory/Ksa/TestTarget.cs L443-L463
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
        posCci = cce2Cci * posCce;
        velCci = cce2Cci * velCce;

        return Vec.IsFinite(posCci) && Vec.IsFinite(velCci);
    }
```

4. Build the orbit, then the vehicle inside the registry lock, and attach it to the world. A save that names a `Character` is built as
   `KittenEva`, otherwise `Vehicle.CreateVehicle(system, body2Cce, bodyRates, parent, id, root, orbit)`; `parent.Children.Add`
   is what makes the world see it:

```csharp
// src/KSArmory/Ksa/TestTarget.cs L156-L205
            Orbit orbit = Orbit.CreateFromStateCci(
                parent,
                Universe.GetElapsedTime(),
                posCci,
                velCci,
                new byte4(255, 80, 80, 255));

            Log.Debug($"  orbit: pe = {orbit.Periapsis / 1000.0:F1} km, ap = {orbit.Apoapsis / 1000.0:F1} km, " +
                     $"ecc = {orbit.Eccentricity:F4}");

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

            // Work out how big it is.
            //
            // Vehicle.MeanRadius is BoundingSphereRadiusBody, which only ever gets set by
            // UpdateCollisionGeometry, which is private and reachable only through this call.
            // A vehicle assembled here and never handed through it keeps a radius of zero - and
            // the camera scales its zoom by MeanRadius, so such a drone cannot be zoomed in or
            // out at all. It also feeds the flight plan's impact clearance margin.
            //
            // KSA's own runtime spawn path reaches this the same way, after the part tree is
            // attached; a vehicle assembled here has to ask for it explicitly.
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
```

5. `UpdateAfterPartTreeModification()` is the only public path to the private `UpdateCollisionGeometry` that sets
   `BoundingSphereRadiusBody`, hence `MeanRadius` and camera zoom.
6. Log the inputs. Placement goes through `Orbit.StateVectors`, so a bad orbit silently drops the vehicle at the frame origin instead of erroring.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/TestTarget.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TestTarget.cs#L100-L205) | Geometry, velocity, conversion, orbit, spawn |
| [src/KSArmory/Ksa/TestTarget.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TestTarget.cs#L375-L399) | `CreateDroneVehicle`: `KittenEva` or `Vehicle` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L358-L389) | Surface placement helpers shared with the move tool |

## Variations and gotchas

- The ballistic correction is a vacuum solution; the drone sheds speed in air and lands a few kilometres short on a shallow pass.
- `SpawnParked` uses the same chain 500 m above the platform and then `TeleportToLocation`, which is how the bridge parks a
  stock craft as a ground target.
- A teleported craft is still at its old position for the rest of the current frame, so a command answering "done" must wait a frame.
- Taking the parts from the player's library needs a fresh `VehicleSaves.Refresh()` scan.

## See also

- [Overview and comparison](overview.md)
- [Teleport to CCI state vectors](teleport-a-vessel-by-building-a-new-orbit-1.md), [Place on a surface](../place-a-vessel-on-a-surface-at-latitude-and-longitude/overview.md)
- Mod feature: [KSArmory spawn and teleport test craft](../../../../mods/ksarmory/features/spawn-and-teleport-test-craft.md)
- The topic spawn-a-vehicle-at-runtime (other batch) covers vehicle spawning in full

[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
