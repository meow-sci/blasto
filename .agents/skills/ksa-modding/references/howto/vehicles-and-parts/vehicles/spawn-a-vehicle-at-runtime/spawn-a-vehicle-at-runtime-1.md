---
type: How-To
title: Spawn a parked copy of a library craft at a latitude and longitude
description: Load a craft from the stock or player vehicle library, build it on a throw-away orbit under the shapes lock, attach it to its parent body and teleport it onto the ground
category: vehicles-and-parts
group: vehicles
topic: spawn-a-vehicle-at-runtime
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/spawn-parked-craft]
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
  - KSA.CelestialSystem
  - KSA.ConstraintSim
  - KSA.InputEvents
tags: [spawn, vehicle-library, teleport, shapes-lock, orbit]
---
Outcome: a call `SpawnParked(flown, "MyCar", "Car 1", body, lat, lon)` leaves a fresh copy of a saved
craft sitting on the ground at that position, simulated and selectable like any other vehicle. KSACars
uses it for its developer bridge, so a test starts where it is asked to rather than where a save left
the car. This also answers "how do I load a craft from the vehicle library": the lookup is step 1.

Prerequisites: a [StarMap](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md)
mod that references the game assemblies, and a craft in either library (see [Ship whole-vehicle presets and single-part craft with a part mod](../../parts/ship-ready-made-craft/overview.md) for
how to ship one). Demonstrated by KSACars [ksacars/spawn-parked-craft](../../../../mods/ksacars/features/spawn-parked-craft.md).

## Background: how the game does it

- The game keeps two craft libraries. `DefaultVehicleSaves` is filled at startup from
  `Content/Core/defaultvehicles` [current/decomp/KSA/DefaultVehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DefaultVehicleSaves.cs#L24-L70)[^ksa-defaultvehiclesaves]; `VehicleSaves` mirrors the
  player's folder and is rebuilt by `Refresh()` [current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs#L280-L290)[^ksa-vehiclesaves]. `FindSave(name)`
  returns `null` when no such craft exists [current/decomp/KSA/DefaultVehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DefaultVehicleSaves.cs#L67-L70).
- `VehicleSave.Load(IViewport)` returns a nullable `PartTree` [current/decomp/KSA/VehicleSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSave.cs#L12-L22)[^ksa-vehiclesave]. The root part
  of that tree is what `Vehicle.CreateVehicle` wants.
- A vehicle needs an orbit or a situation, a non-null parent and a root; otherwise the constructor
  throws [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1426-L1470)[^ksa-vehicle].
- A new vehicle is not part of the orbiter tree until it is in its parent's `Children` list. Stock spawning
  does `parent.Children.Add(vehicle)` then `vehicle.UpdatePerFrameData()`
  [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L929-L940)[^ksa-inputevents].
- Creation and `UpdateAfterPartTreeModification` mutate the shape registry, which the vehicle step holds
  while it runs, so a mod hook that fires during the step must wait: `UnlockShapesBlocking`
  [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L111-L153)[^ksa-constraintsim].

## Lifecycle and timing

| Moment | What runs | State |
|---|---|---|
| `frame:after-on-frame` (`StarMapAfterOnFrame`) | command handled, library lookup, `UnlockShapesBlocking`, build, attach, `TeleportToLocation` | main thread, after `PostRender`; the step dispatched earlier in the frame is still running, so the lock call waits for it |
| next `frame:input` | `InputEvents.ApplyInputEvents` applies `TeleportInputBuffer` | settled window after `ApplyVehicleSolvers` |
| next `frame:dispatch-solvers` | first physics step that includes the craft | the craft is now in the world |

KSACars spawns on explicit command, so the one-frame stall from waiting on the step is acceptable. The
stock path (`CreateVehicleBuffer` in `ApplyInputEvents`) never waits because it runs in the settled
window; see [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) for why that window is the safe one. If you spawn every
frame, queue an `InputEvents.CreateVehicleData` instead of building inline.

## Steps

1. Hook the end of the frame so the world is quiescent from your point of view and there is a flown
   craft to borrow a frame from:

```csharp
// src/KSACars/Ksa/KSACarsMod.cs L59-82
    [StarMapAfterOnFrame]
    public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
    {
        try
        {
            if (_disabled) return;

            // The fallback, and a no-op on every frame the GUI pass ran. See StepOnce.
            StepOnce();

            // After the step, so a command sees this frame's world.
            _bridge?.Update(dtPlayer, KsaWorld.InFlightScene && !KsaWorld.IsPaused ? KsaWorld.SimStepSeconds : 0.0);
        }
        catch (Exception e)
        {
            Fault("frame", e);
        }
        finally
        {
            // Unconditionally: this is the only hook KSA always calls, so a release skipped by an early
            // return or an exception would stop the mod for the rest of the session.
            _frame.EndFrame();
        }
    }
```

2. Look the craft up in both libraries. Both `FindSave` and `Load` can return `null`; a missing craft
   must not throw into the frame:

```csharp
// src/KSACars/Ksa/CraftSpawner.cs L54-85
    // The stock library first, then the player's own, which is where tools/install-testcraft.sh puts
    // the cars. Both FindSave and Load are genuinely nullable in KSA.
    private static PartTree? LoadParts(string craftName)
    {
        try
        {
            VehicleSave? save = DefaultVehicleSaves.FindSave(craftName) ?? LibraryCraft(craftName);
            if (save?.Load(Program.MainViewport) is { } tree) return tree;

            Log.Warn($"spawn: no craft '{craftName}' in either vehicle library");
        }
        catch (Exception e)
        {
            Log.Warn($"spawn: could not load '{craftName}' ({e.GetType().Name})");
        }

        return null;
    }

    private static VehicleSave? LibraryCraft(string name)
    {
        VehicleSaves.Refresh();
        foreach (VehicleSave save in VehicleSaves.AsSpan())
        {
            if (string.Equals(save.Id, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(save.VehicleSaveData.Id, name, StringComparison.OrdinalIgnoreCase))
            {
                return save;
            }
        }
        return null;
    }
```

3. Give the new craft a place to exist. The mod lends it the flown craft's position 500 m up and its
   velocity, converted from the ecliptic frame to the parent's inertial (CCI) frame. `Cce` is the
   parent-centred ecliptic frame, so only the parent's own position and velocity differ from `Ecl`:

```csharp
// src/KSACars/Ksa/CraftSpawner.cs L87-106
    // Cce is the parent-centred *ecliptic* frame, so it differs from Ecl only by the body's own position
    // and velocity; Cci is a fixed rotation away from it, and both are non-rotating, so the same
    // quaternion carries position and velocity.
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

4. Create the orbit, take the shapes lock, build and attach, then teleport. Everything that changes
   shapes must happen inside the `using`:

```csharp
// src/KSACars/Ksa/CraftSpawner.cs L25-45
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
```

5. Call it from your own handler. KSACars' bridge passes the flown craft as the platform:

```csharp
// src/KSACars/Ksa/Bridge.cs L182-195
    private static Reply Spawn(BridgeCommand command)
    {
        if (KsaWorld.ControlledVehicle is not { } flown) return Failed("no craft is being flown");
        if (KsaWorld.ParentBody(flown) is not { } body) return Failed("no body under the craft");

        string stock = command.String("craft");
        if (stock.Length == 0) return Failed("spawn needs a craft");
        string name = command.String("name");
        if (name.Length == 0) name = stock;

        return CraftSpawner.SpawnParked(flown, stock, name, body, command.Number("lat", 0.0), command.Number("lon", 0.0)) is { }
            ? Done(new() { ["name"] = name })
            : Failed($"could not park a {stock}");
    }
```

6. Check the log. A successful spawn writes `parked '<id>' (<craft>) at <lat>, <lon> on <body>`; a
   missing craft writes `spawn: no craft '<name>' in either vehicle library`.

## Complete example

- [src/KSACars/Ksa/CraftSpawner.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CraftSpawner.cs)[^ksacars-craftspawner] - the whole recipe (107 lines)
- [src/KSACars/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Bridge.cs#L182-L195)[^ksacars-bridge] - the caller
- [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L59-L82)[^ksacars-ksacarsmod] - the `StarMapAfterOnFrame` hook

`KsaWorld.LocalUp`, `PositionEcl` and `VelocityEcl` are small wrappers in the mod; the game calls behind
them are `Vehicle.GetPositionEcl()`, `Vehicle.GetVelocityEcl()` and the direction away from the parent's
position.

## Variations and gotchas

- **`parent.Children.Add` is not optional.** `CelestialSystem.UpdatePerFrameData` walks the orbiter tree
  from the parents; a craft that is only registered stays at the origin of the frame and neither moves nor
  draws.
- **The orbit is a placeholder.** `TeleportToLocation` replaces orbit, attitude and rates when the queued
  teleport is applied, so a poor orbit does not matter; it must only be valid enough for the constructor and
  for `UpdatePerFrameData`. If you want a real orbit instead, see approach 2 and [teleport-a-vessel-by-building-a-new-orbit](../../../physics/kinematics/teleport-a-vessel-by-building-a-new-orbit/overview.md).
- **The library lookup order is a design choice.** Stock craft first means a player craft cannot shadow a
  stock name. `VehicleSaves.Refresh()` rescans the disk every call; call it only when a lookup missed.
- **Saves.** The spawned craft is a normal vehicle and is written to the universe save; see
  `keep-mod-vehicles-out-of-saves` if you do not want that.
- **Failure mode.** The `Vehicle` constructor registers the vehicle with its `CelestialSystem` as soon as it
  is built [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1426-L1470). Anything thrown after that point (for example inside
  `UpdateAfterPartTreeModification`) is caught by the mod's `try` and logged, but the registered vehicle is
  not removed, so keep the work after `CreateVehicle` to the calls shown.
- **Game updates.** The recipe depends on `Vehicle.CreateVehicle`, the public `Children` list,
  `UpdateAfterPartTreeModification`, `UpdatePerFrameData` and `TeleportToLocation`; if a game update changes one
  of them the build fails or the craft stays at the frame origin.

## See also

- [Overview and comparison](overview.md)
- [Approach 2: test drones from a state vector](spawn-a-vehicle-at-runtime-2.md)
- [Approach 3: a one-part vehicle built in code](spawn-a-vehicle-at-runtime-3.md)
- [ksacars/spawn-parked-craft](../../../../mods/ksacars/features/spawn-parked-craft.md)
- [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../change-vehicles-without-racing-the-solver/overview.md), [Ship whole-vehicle presets and single-part craft with a part mod](../../parts/ship-ready-made-craft/overview.md)

[^ksa-defaultvehiclesaves]: current/decomp/KSA/DefaultVehicleSaves.cs (decompiled, build 2026.10.10.5554)
[^ksacars-ksacarsmod]: KSACars: src/KSACars/Ksa/KSACarsMod.cs
[^ksa-inputevents]: current/decomp/KSA/InputEvents.cs (decompiled, build 2026.10.10.5554)
[^ksacars-craftspawner]: KSACars: src/KSACars/Ksa/CraftSpawner.cs
[^ksa-constraintsim]: current/decomp/KSA/ConstraintSim.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehiclesaves]: current/decomp/KSA/VehicleSaves.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehiclesave]: current/decomp/KSA/VehicleSave.cs (decompiled, build 2026.10.10.5554)
[^ksacars-bridge]: KSACars: src/KSACars/Ksa/Bridge.cs
