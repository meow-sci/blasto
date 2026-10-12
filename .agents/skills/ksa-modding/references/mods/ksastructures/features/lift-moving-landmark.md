---
type: Mod Feature
title: A lift built from a moving launch-pad landmark
description: Runs a passenger lift by rewriting a second launch-pad landmark's GroundOffset every simulated step, carrying and waking riders from a PrepareWorker prefix, and reading the floor keys through ImGui
mod: ksastructures
category: physics
techniques: [harmony-prefix, custom-physics, imgui-drawlist, starmap-lifecycle]
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - harmony-postfix KSA.LocationReference.UpdateStaticObjectRenderData
lifecycle: [frame:draw-ui, frame:dispatch-solvers, frame:render, physics:bepu]
game_types:
  - KSA.StaticObject
  - KSA.StaticObjectTemplate
  - KSA.ConstraintSim
  - KSA.NarrowPhaseCallbacks
  - KSA.Vehicle
  - KSA.KittenEva
  - KSA.PhysicsStates
  - KSA.VehicleProperties
  - KSA.FlightPlan
  - KSA.LandmarkReference
  - Brutal.ImGuiApi.ImGui
tags: [lift, elevator, moving-platform, static-object, ground-offset, kinematic, riders, hotkeys]
---

The tower's lift is a car you walk into: stand in it and press **1** (garage), **2** (lobby) or
**3** (office), or **0** for the optional vault. There is no moving rigid body. The car's floor is
the collider set of a second static object, `KSAOffice_LiftObject`, attached to a second launch-pad
landmark three metres west of the tower's, and the mod moves it by rewriting that static object's
`GroundOffset` every simulated step.[^mod-lift][^mod-office-xml] Kittens standing on it are carried
by contact, kept awake while it moves, and jumped together with the car across the blind shaft.

## How it works

1. **Height as `GroundOffset`.** `ConstraintSim.UpdateStaticObjectCollider` places a body's launch-pad
   collider at `positionCcf + upCcf * staticObject.GroundOffset`, reading `GroundOffset` from the
   template each time it updates the static's description.[^ksa-constraintsim][^ksa-staticobject]
   So writing `StaticObject.Template.GroundOffset` moves the car's floor for every body that
   collides with the lift landmark. `Office.Update` does that at the end of each step, adding a
   correction for the difference in terrain height between the tower's and the lift's landmark
   points, because each static object stands on the terrain under its own landmark.[^mod-lift]
2. **Which landmark a kitten stands on.** The engine gives a body only its nearest launch pad within
   300 m; the lift landmark sits on the shaft's centre, so a kitten past the doorway plane is nearer
   it and collides with the car alone (see [launch-pad landmarks](./office-landmarks-static-objects.md)).
3. **Motion.** The car moves between `Stops` with a trapezoidal profile: at most 1.3 m/s,
   0.8 m/s^2, and an exact stop when the remaining distance fits in one step. Speed is kept under the
   1.5 m/s `MaximumRecoveryVelocity` the engine's contact callbacks give every pair, so the floor
   pushes a rider up by contact without the rider leaving the ground.[^ksa-narrowphase][^mod-lift]
   Between lobby and office (and vault and garage) the blind shaft is skipped in one step once the
   car is `CrawlMetres` (3 m) past a doorway, and every rider is queued to move by the same amount.
4. **Who is aboard.** Each step every live `KittenEva` on Earth is expressed in the lift's frame
   (up, east, north from the lift landmark, with the tower's ground as zero). Inside the car's box
   and between -0.6 m and 2.7 m above its floor it is a rider; within 10 m of the shaft at a stop's
   height it is calling the car. With no riders a single caller summons the car; with riders, a
   number key or a 4 s dwell sends it on.[^mod-lift]
5. **Carrying riders.** Writes to a craft's physics state only survive from a prefix on
   `Vehicle.PrepareWorker`: the engine copies the state for its worker right after, and the worker's
   result overwrites anything written elsewhere.[^mod-vehiclecommand] The mod's `BeforeWorker`
   prefix (the same one that marks the asker for the [terrain height override](./terrain-height-roof-override.md))
   calls `Carry`, which applies queued shaft skips as a position change and, while the car moves,
   wakes riders that are on rails.[^mod-terrain][^mod-lift]
6. **Why wake them.** `ConstraintSim.UpdateSimFromVehicle` only calls `UpdateStaticObjectCollider`
   for a body that is not on rails. A kitten standing still goes to sleep (on rails), its copy of the
   lift collider stops following `GroundOffset`, and the floor would leave without it.[^ksa-constraintsim]
   `VehicleCommand.TryChangeVelocity(v, zero)` refreshes the state from the analytic orbit with
   `PhysicsStates.UpdateFromAnalytic`, sets `Props.SetOnRails(false)` and installs a new
   `FlightPlan`. The first call mirrors the engine's own `Vehicle.Split`, which runs
   `UpdateFromAnalytic` on a vehicle that is on rails before changing it.[^mod-vehiclecommand][^ksa-vehicle][^ksa-physicsstates]
7. **Drawing the car.** The engine never draws `KSAOffice_LiftVisual` (it is on no landmark). The
   render-data postfix draws it at `_drawn`, the height one step ahead (`height + speed * lastStep`),
   so the floor and the rider moved onto it are drawn together; for 12 frames after a shaft skip it
   draws the car under the rider's resting height instead, hiding the frame or two before the
   rider's move shows up.[^mod-office][^mod-lift]
8. **Keys.** `Office.Keys` runs from `[StarMapAfterGui]` and reads `ImGui.IsKeyPressed(ImGuiKey._1,
   repeat: false)` (and the keypad variants), only while the flown kitten is aboard, the car is
   stopped and `ImGui.GetIO().WantTextInput` is false; the key only sets `_asked`, which the next
   step acts on. A hint line is drawn centred near the bottom of the main viewport with
   `ImGui.GetForegroundDrawList().AddText`.[^mod-lift][^mod-entry]

## Lifecycle and timing

| Phase | Hook | Work | Notes |
|---|---|---|---|
| `frame:draw-ui` | `[StarMapAfterGui]` -> `StepOnce` -> `Office.Update(dtSim)` | Advance the car, find riders, queue skips, write `GroundOffset` | Runs on the engine's simulated step (`KsaWorld.ConsumeSimStep`), so a paused game freezes the lift; falls back to the `Program.OnFrameCelestials` prefix (`frame:celestials`) or the frame postfix when the UI is hidden (see [frame step plumbing](./frame-step-plumbing.md)) |
| `frame:draw-ui` | `[StarMapAfterGui]` -> `Office.Keys` | Read 0-3, draw the hint | ImGui is only valid between `NewFrame` and `Render` |
| `frame:dispatch-solvers` (next frame) | prefix on `Vehicle.PrepareWorker` | Apply queued moves, wake riders | Main thread, immediately before `PrepareFromVehicle` copies the state |
| `physics:bepu` | engine | Colliders placed at the new `GroundOffset` | The step runs after this frame's solvers were dispatched, so a `GroundOffset` written in the GUI pass is read by the worker either this step or the next; the mod draws the car one step ahead to match |
| `frame:render` (before command recording) | postfix on `UpdateStaticObjectRenderData`, from `Program.Render` -> `UpdateRenderingResources` | Draw the car | Once per visible viewport |

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeLift.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeLift.cs#L13-L60) | Stops, speed limits, and the state carried between steps |
| [src/KSAStructures/Ksa/OfficeLift.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeLift.cs#L68-L94) | `Keys`: floor buttons and hint |
| [src/KSAStructures/Ksa/OfficeLift.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeLift.cs#L96-L279) | `Update`: motion, riders, calls, `GroundOffset` |
| [src/KSAStructures/Ksa/OfficeLift.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeLift.cs#L292-L316) | `Carry`: moves and wake-ups from the worker prefix |
| [src/KSAStructures/Ksa/VehicleCommand.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/VehicleCommand.cs#L15-L31) | The one write to a craft's physics state |
| [src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L142-L157) | Drawing the car a step ahead |
| [src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L186-L195) | `InLiftFrame`: a vehicle in the lift's up/east/north frame |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L5995-L6047) | The car's colliders as a static sub-object |

## Snippets

Floor keys, read in the GUI pass:

```csharp
// src/KSAStructures/Ksa/OfficeLift.cs L69-L80
    public static void Keys()
    {
        try
        {
            if (!_flownAboard || _height != _target || ImGui.GetIO().WantTextInput) return;

            // The garage, lobby and office keep their keys with or without a vault under them.
            int first = Stops.Length - 3;
            if (ImGui.IsKeyPressed(ImGuiKey._1, repeat: false) || ImGui.IsKeyPressed(ImGuiKey.Keypad1, repeat: false)) _asked = first;
            else if (ImGui.IsKeyPressed(ImGuiKey._2, repeat: false) || ImGui.IsKeyPressed(ImGuiKey.Keypad2, repeat: false)) _asked = first + 1;
            else if (ImGui.IsKeyPressed(ImGuiKey._3, repeat: false) || ImGui.IsKeyPressed(ImGuiKey.Keypad3, repeat: false)) _asked = first + 2;
            else if (first > 0 && (ImGui.IsKeyPressed(ImGuiKey._0, repeat: false) || ImGui.IsKeyPressed(ImGuiKey.Keypad0, repeat: false))) _asked = 0;
```

Moving the car and skipping the blind shaft with its riders:

```csharp
// src/KSAStructures/Ksa/OfficeLift.cs L135-L168
            if (_height != _target && dtSim > 0.0)
            {
                double left = Math.Abs(_target - _height);
                double wanted = Math.Min(MaxSpeed, Math.Sqrt(2.0 * Acceleration * left) + 0.05);
                double speed = Math.Min(wanted, Math.Abs(_speed) + (Acceleration * dtSim));
                double step = speed * dtSim;

                if (step >= left) { _height = _target; _speed = 0.0; Log.Info($"office: lift arrived at {_height:F1} m"); }
                else { _speed = Math.Sign(_target - _height) * speed; _height += _speed * dtSim; }

                // Only the blind shafts are skipped, lobby to office and vault to garage: the car
                // passes every doorway at its own pace, whichever way it is going and wherever from.
                bool up = _target > _height;
                foreach ((double low, double high) in (ReadOnlySpan<(double, double)>)
                         [(LobbyStop + CrawlMetres, OfficeStop - CrawlMetres), (VaultStop + CrawlMetres, GarageStop - CrawlMetres)])
                {
                    if (high <= low || _height <= low - 0.001 || _height >= high + 0.001 || (up ? _target <= high : _target >= low)) continue;

                    double by = (up ? high : low) - _height;
                    if (Math.Abs(by) > 1.0)
                    {
                        _height += by;
                        foreach (Vehicle rider in Riders.Keys) Skips[rider] = by;
                        _carOnRider = 12;
                    }
                }
            }
            else _speed = 0.0;
            if (wasMoving && _speed == 0.0) _settle = 0.25;

            // What the engine draws and collides next frame is the car a step on, so that the floor
            // and the rider carried onto it are in the same place when that frame is drawn.
            _lastStep = dtSim > 0.0 ? dtSim : _lastStep;
            _drawn = _speed == 0.0 ? _height : Math.Clamp(_height + (_speed * _lastStep), LowestStop, OfficeStop);
```

The height written into the landmark's static object, and riders carried from the worker prefix:

```csharp
// src/KSAStructures/Ksa/OfficeLift.cs L264-L273, L295-L316
            // Each landmark sits on the terrain under its own point, so the car's height is taken
            // from the tower's ground rather than its own.
            double correction = 0.0;
            if (earth is not null)
                correction = earth.GetTerrainHeightFromDirCcf(_tower.ForwardCcf) - earth.GetTerrainHeightFromDirCcf(_lift.ForwardCcf);
            if (!double.IsFinite(correction)) correction = 0.0;

            _correction = correction;

            _car.Template.GroundOffset = new DistanceReference(_height + correction);
    // ...
    private static void Carry(Vehicle v)
    {
        if (Moves.Remove(v, out double3 to))
        {
            VehicleCommand.TryChangeVelocity(v, double3.Zero, to.Transform(doubleQuat.Conjugate(v.Body2Cce)));
            return;
        }

        if (Skips.Remove(v, out double by) && _lift is not null && KsaWorld.ParentBody(v) is { } body)
        {
            _lift.GetAxesCcf(out double3 upCcf, out _, out _);
            double3 move = (upCcf.Transform(body.GetCcf2Cce()) * by).Transform(doubleQuat.Conjugate(v.Body2Cce));
            if (VehicleCommand.TryChangeVelocity(v, double3.Zero, move)) _carried++;
            else _refused++;
            return;
        }

        if (_speed == 0.0 || !Riders.ContainsKey(v) || !v.Situation.IsOnRails()) return;

        if (VehicleCommand.TryChangeVelocity(v, double3.Zero)) _carried++;
        else _refused++;
    }
```

Changing a craft's state where the change survives:

```csharp
// src/KSAStructures/Ksa/VehicleCommand.cs L15-L31
    public static bool TryChangeVelocity(Vehicle craft, double3 changeAsmb, double3 moveAsmb = default)
    {
        if (!KsaWorld.IsAlive(craft) || !craft.HasPhysicsBubble || !Vec.IsFinite(changeAsmb)) return false;

        PhysicsStates states = craft.GetPhysicsStatesMutable();
        if (craft.Situation.IsOnRails())
        {
            states.UpdateFromAnalytic(craft.Orbit, in craft.Orbit.StateVectors, craft.Body2Cce, craft.BodyRates,
                                      Situation.Maneuvering);
        }

        states.Kinematic.VelocityPhys += changeAsmb.Transform(states.Kinematic.Body2Phys);
        if (Vec.IsFinite(moveAsmb)) states.Kinematic.PositionPhys += moveAsmb.Transform(states.Kinematic.Body2Phys);
        states.Props.SetOnRails(isOnRails: false);
        craft.SetFlightPlan(new FlightPlan(states.ComputeOrbit(craft.OrbitColor), craft.Hash));
        return true;
    }
```

## Game APIs touched

| API | Decomp | Use |
|---|---|---|
| `StaticObject.Find`, `StaticObject.Template`, `StaticObjectTemplate.GroundOffset` | [current/decomp/KSA/StaticObjectTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectTemplate.cs#L18-L33) | The car's height |
| `ConstraintSim.UpdateStaticObjectCollider`, `UpdateSimFromVehicle` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L339-L389) | Collider repositioned only for off-rails bodies |
| `NarrowPhaseCallbacks.ConfigureContactManifold` | [current/decomp/KSA/NarrowPhaseCallbacks.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NarrowPhaseCallbacks.cs#L174-L179) | `MaximumRecoveryVelocity = 1.5f` |
| `Vehicle.GetPhysicsStatesMutable`, `SetFlightPlan`, `HasPhysicsBubble`, `Situation`, `Body2Cce` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L921-L931) | Mutable refs into the vehicle's private state |
| `PhysicsStates.UpdateFromAnalytic`, `ComputeOrbit`, `Kinematic.VelocityPhys`, `PositionPhys`, `Body2Phys` | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L835) | Off-rails state edit |
| `Universe.PrepareVehicleWorkers` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2141-L2155) | Where the prefix runs |
| `ImGui.IsKeyPressed`, `ImGui.GetIO().WantTextInput`, `ImGui.GetForegroundDrawList` | Brutal.ImGui | Floor keys and hint |

## Pitfalls and version notes

- **A static object is not a body.** It has no velocity, so a rider is carried only by the contact
  solver's push-out; keep platform speed below the contact recovery speed (1.5 m/s in this build).
- **Sleeping bodies keep their old copy of the platform.** Wake riders while the platform moves.
- **`GroundOffset` is template state shared by every user of that static object.** One static object
  per moving platform.
- **Write physics state only from a `PrepareWorker` prefix**; anywhere else the worker overwrites it.
- **The lift's height is written on the main thread while the vehicle worker may be stepping.** The
  field is a reference (`DistanceReference`) swapped whole, so a reader sees either the old or the
  new height.
- **Keys compete with KSA's own bindings** for 0-3 while a kitten is flown; the mod only reads them
  when the flown kitten is aboard and the car is stopped.

## See also

- [Launch-pad landmarks](./office-landmarks-static-objects.md) - the landmark pair and the render-data postfix
- [Terrain height roof override](./terrain-height-roof-override.md) - shares the `PrepareWorker` prefix
- [Frame step plumbing](./frame-step-plumbing.md) - where `Office.Update` is called from and on which clock
- [Rewrite GroundOffset of a dedicated launch-pad object](../../../howto/physics/colliders/build-a-moving-platform-kittens-can-ride/build-a-moving-platform-kittens-can-ride.md) - how-to article demonstrating this feature
- [Write inside a Vehicle.PrepareWorker prefix](../../../howto/physics/solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-1.md) - how-to article demonstrating this feature

[^mod-lift]: src/KSAStructures/Ksa/OfficeLift.cs
[^mod-office]: src/KSAStructures/Ksa/Office.cs
[^mod-terrain]: src/KSAStructures/Ksa/OfficeTerrain.cs
[^mod-vehiclecommand]: src/KSAStructures/Ksa/VehicleCommand.cs
[^mod-entry]: src/KSAStructures/Ksa/KSAStructuresMod.cs
[^mod-office-xml]: src/KSAStructures/KSAStructuresOffice.xml
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-narrowphase]: KSA.NarrowPhaseCallbacks (decompiled, build 2026.10.10.5554)
[^ksa-staticobject]: KSA.StaticObject (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
