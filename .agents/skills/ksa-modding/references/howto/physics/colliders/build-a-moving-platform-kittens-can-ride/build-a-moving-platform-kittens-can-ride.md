---
type: How-To
title: Rewrite GroundOffset of a dedicated launch-pad object
description: Move a collider-only static object by writing its template GroundOffset each simulated step under the contact recovery speed, wake sleeping riders and carry them across skipped shaft sections from a Vehicle.PrepareWorker prefix
category: physics
group: colliders
topic: build-a-moving-platform-kittens-can-ride
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksastructures/features/lift-moving-landmark]
techniques: [harmony-prefix, xml-asset]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:draw-ui, frame:dispatch-solvers, physics:bepu]
game_types: [KSA.StaticObject, KSA.ConstraintSim, KSA.KittenEva, KSA.Vehicle, KSA.PhysicsStates]
tags: [physics, bepu, elevator, lift, static-object, ground-offset, kittens, rails, ksastructures]
---

Outcome: a lift car you walk into. Stand in it and press 1, 2 or 3; the floor moves between stops with a trapezoidal profile,
kittens standing on it are carried by contact, kept awake while it moves, and moved across the blind shaft with it.
KSAStructures' tower lift is the demo. There is no moving rigid body: the car's floor is the collider set of a second
static object attached to a second launch-pad landmark three metres from the tower's, and the mod moves it by rewriting that
static object's `GroundOffset`. Prerequisites: StarMap and Harmony; the landmark and XML setup is
[approach 3 of the static colliders topic](../add-static-colliders-to-physics-bubbles/add-static-colliders-to-physics-bubbles-3.md).

## Background: how the game does it

`ConstraintSim.UpdateStaticObjectCollider` places a body's launch-pad collider each time it runs, reading the template's
`GroundOffset` fresh:

```csharp
// current/decomp/KSA/ConstraintSim.cs L584-L633
	public void UpdateStaticObjectCollider(ReadOnlyPhysicsStates states, ref BepuHandles handles)
	{
		List<LaunchPadPose> list = ResolveLaunchPads(states);
		if (list.Count <= 0)
		{
			RemoveStaticObjectCollider(ref handles);
			return;
		}
		int num = -1;
		double num2 = 90000.0;
		double3 double5 = states.Origin.PositionBub + states.Kinematic.PositionPhys;
		for (int i = 0; i < list.Count; i++)
		{
			double num3 = (list[i].PositionCcf - double5).LengthSquared();
			if (!(num3 >= num2))
			{
				num2 = num3;
				num = i;
			}
		}
		if (num < 0)
		{
			RemoveStaticObjectCollider(ref handles);
			return;
		}
		LaunchPadPose launchPadPose = list[num];
		StaticObject staticObject = launchPadPose.StaticObject;
		double3 positionCcf = launchPadPose.PositionCcf;
		double3 upCcf = launchPadPose.UpCcf;
		double3 eastCcf = launchPadPose.EastCcf;
		double3 northCcf = launchPadPose.NorthCcf;
		StaticDescription description = new StaticDescription
		{
			Pose = new RigidPose
			{
				Position = (positionCcf + upCcf * staticObject.GroundOffset - states.Origin.PositionBub).ToBepu(),
				Orientation = doubleQuat.CreateFromRotationMatrix(new double4x4(upCcf.X, upCcf.Y, upCcf.Z, 0.0, eastCcf.X, eastCcf.Y, eastCcf.Z, 0.0, northCcf.X, northCcf.Y, northCcf.Z, 0.0, 0.0, 0.0, 0.0, 1.0)).ToBepu()
			},
			Shape = staticObject.CollisionShape
		};
		StaticHandle? staticObjectCollider = handles.StaticObjectCollider;
		if (!staticObjectCollider.HasValue)
		{
			handles.StaticObjectCollider = Simulation.Statics.Add(in description, ref StaticsShouldntAwakenBodies.Shared);
		}
		else
		{
			Simulation.Statics.ApplyDescription(handles.StaticObjectCollider.Value, in description, ref StaticsShouldntAwakenBodies.Shared);
		}
	}
```

A static object has no velocity, so a rider is carried only by the contact solver pushing out of the penetration; the
engine's contact callbacks give every pair `MaximumRecoveryVelocity = 1.5f`.[^ksa-narrowphasecallbacks] The update is
called only for bodies that are not on rails:

```csharp
// current/decomp/KSA/ConstraintSim.cs L386-L392
		if (inPhysicsRadius)
		{
			if (!flag)
			{
				UpdateTerrain(readOnlyStates, ref bepuHandles, vehicleState.TerrainPatchState, terrainLeadTime, subStepDt);
				UpdateStaticObjectCollider(readOnlyStates, ref bepuHandles);
			}
```

A kitten standing still goes to sleep, its copy of the lift collider stops following `GroundOffset`, and the floor would leave without
it.[^ksa-constraintsim]

## Lifecycle and timing

| Phase | Hook | Work |
|---|---|---|
| `frame:draw-ui` | `StarMapAfterGui` -> `StepOnce` -> `Office.Update(dtSim)` | Advance the car on the engine's simulated step (so a paused game freezes the lift), find riders, queue skips, write `GroundOffset` |
| `frame:draw-ui` | `StarMapAfterGui` -> `Office.Keys` | Read 0 to 3, draw the hint; ImGui is only valid between `NewFrame` and `Render` |
| `frame:dispatch-solvers` (next frame) | prefix on `Vehicle.PrepareWorker` | Apply queued moves, wake riders; main thread, immediately before `PrepareFromVehicle` copies the state |
| `physics:bepu` | engine | Colliders placed at the new `GroundOffset`; the step runs after this frame's solvers were dispatched, so a value written in the GUI pass is read this step or the next; the mod draws the car one step ahead to match |

The height is written on the main thread while the vehicle worker may be stepping; the field is a reference
(`DistanceReference`) swapped whole, so a reader sees the old or the new height. Writes to a craft's physics state only
survive from a prefix on `Vehicle.PrepareWorker` (see [the window](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-1.md)).
See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Declare a second `StaticObject` with colliders only (the floor), attach it to its own launch-pad landmark, and a third
   with mesh only that is never attached (the mod draws it). The lift landmark stands on the shaft's centre, so a kitten
   past the doorway plane is nearer it and collides with the car alone.
2. Read keys in the GUI pass, only when the flown kitten is aboard and the car is stopped and
   `ImGui.GetIO().WantTextInput` is false; the key only sets a request that the next step acts on:

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

3. Move the car on simulated time with a trapezoidal profile, at most 1.3 m/s and 0.8 m/s^2, with an exact stop when the
   remaining distance fits in one step. Keep speed below the 1.5 m/s recovery velocity. Skip the blind shaft in one step
   once the car is 3 m past a doorway and queue every rider to move by the same amount:

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

4. Write the height. Each static object stands on the terrain under its own landmark, so add a correction for the difference in
   terrain height between the tower's and the lift's landmark points:

```csharp
// src/KSAStructures/Ksa/OfficeLift.cs L264-L273
            // Each landmark sits on the terrain under its own point, so the car's height is taken
            // from the tower's ground rather than its own.
            double correction = 0.0;
            if (earth is not null)
                correction = earth.GetTerrainHeightFromDirCcf(_tower.ForwardCcf) - earth.GetTerrainHeightFromDirCcf(_lift.ForwardCcf);
            if (!double.IsFinite(correction)) correction = 0.0;

            _correction = correction;

            _car.Template.GroundOffset = new DistanceReference(_height + correction);
```

5. Carry riders from a `Vehicle.PrepareWorker` prefix. Apply queued shaft skips as a position change and, while the car
   moves, wake riders that are on rails. `VehicleCommand.TryChangeVelocity` refreshes the state from the analytic orbit
   with `UpdateFromAnalytic` if on rails (mirroring `Vehicle.Split`), applies the change, takes the craft off rails and
   installs a new `FlightPlan`:

```csharp
// src/KSAStructures/Ksa/OfficeLift.cs L295-L316
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

6. Draw the car yourself, at the height one step ahead (`height + speed * lastStep`), so the floor and the rider moved
   onto it are drawn together; for 12 frames after a shaft skip draw it under the rider's resting height instead.

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeLift.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeLift.cs) | Keys, profile, riders, `GroundOffset` write, `Carry` |
| [src/KSAStructures/Ksa/VehicleCommand.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/VehicleCommand.cs#L15-L31) | State change that survives |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml) | Lift object and visual declarations |

## Variations and gotchas

- A static object is not a body: it has no velocity, a rider is carried only by push-out, so keep platform speed below the
  contact recovery speed (1.5 m/s here).
- Sleeping bodies keep their old copy of the platform; wake riders while it moves.
- `GroundOffset` is template state shared by every user of that static object: one static object per moving platform.
- Keys compete with KSA's own bindings for 0 to 3 while a kitten is flown.
- The engine never draws the visual static object unless it is on a landmark; draw it from a postfix on
  `LocationReference.UpdateStaticObjectRenderData`.

## See also

- [Overview and comparison](overview.md)
- [Add mod-owned static colliders](../add-static-colliders-to-physics-bubbles/overview.md)
- Mod feature: [KSAStructures lift moving landmark](../../../../mods/ksastructures/features/lift-moving-landmark.md)
- [Inject forces, impulses and velocity changes](../../solver/inject-forces-and-impulses-at-the-physics-safe-point/overview.md)

[^ksa-narrowphasecallbacks]: KSA.NarrowPhaseCallbacks (decompiled, build 2026.10.10.5554)
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
