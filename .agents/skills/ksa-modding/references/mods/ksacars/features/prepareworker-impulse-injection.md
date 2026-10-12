---
type: Mod Feature
title: Inject wheel forces from a Vehicle.PrepareWorker prefix
description: Write a car's per-step linear and angular impulse straight into the vehicle's kinematic state from a Harmony prefix on Vehicle.PrepareWorker, the only main-thread window where the write reaches the physics worker
mod: ksacars
category: physics
techniques: [harmony-prefix, custom-physics, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [startup:all-mods-loaded, frame:dispatch-solvers]
game_types:
  - KSA.Vehicle
  - KSA.PhysicsStates
  - KSA.KinematicStates
  - KSA.Universe
  - KSA.SimStep
  - KSA.SituationEx
  - BepuUtilities.Symmetric3x3
tags: [physics, harmony, prefix, vehicle-solver, impulse, kinematic-state, frame-order]
---

KSA has no wheel module, so KSACars ("Fast & Purrious") computes every spring, tyre and engine
force itself and hands the result to the engine as a change of velocity and spin. The whole trick
is *where* that write happens: a Harmony prefix on `Vehicle.PrepareWorker`, which the engine calls
on the main thread for every vehicle immediately before it snapshots the vehicle's state for the
physics worker.[^ksacars-physicshook] Anything written to a vehicle's velocity from a StarMap hook
is lost, because the snapshot has already been taken and the worker's result overwrites the field
on the next frame.[^ksacars-blocked]

The same window is reused to "unflip" a car: the righting move rewrites position, attitude and
velocity in one go.[^ksacars-righting]

## How it works

1. `[StarMapAllModsLoaded]` calls `PhysicsHook.Install()`, which looks up
   `Vehicle.PrepareWorker(SimStep)` by reflection and patches it with a Harmony prefix under its
   own Harmony id (`com.ksacars.physics`).[^ksacars-mod]
2. The prefix receives `__instance` and the `SimStep` the worker is about to integrate and calls
   `Buggies.Physics(craft, simStep.DeltaTime)`. A car is any vehicle whose part list contains a part
   with a known `BuggyProfile.PartId`; everything else returns at the first dictionary lookup.
3. `Buggies.Step` takes `craft.GetPhysicsStatesMutable()`. `PhysicsStates` is a `ref struct` whose
   fields alias the vehicle's own `_bubbleOrigin`, `_kinematicStates`, `_props` and
   `_environment`, so assigning `states.Kinematic.VelocityPhys` writes the vehicle directly.[^ksa-physicsstates][^ksa-vehicle]
4. The step converts to the planet-fixed frame (`PhysicsStates.GetStatesCcf`), builds per-wheel
   contacts (see [terrain contact](./terrain-contact-under-wheels.md)), and asks
   `BuggyDrive.Step` (see [wheel model](./wheel-suspension-tyre-model.md)) for a `DriveImpulse`:
   one impulse through the centre of mass and one about it, in the body frame.
5. It turns the impulse into `dv = J / mass` and `dw = I^-1 L` with
   `Symmetric3x3.Invert(craft.TotalMassPropsBody.Inertia)`, adds the rocket pushes (see
   [rockets](./rocket-lift-hover-boost-downforce.md)), rejects non-finite results, rotates both
   into the physics frame with `Kinematic.Body2Phys` and adds them to `VelocityPhys` and
   `AngularVelocityPhys`.[^ksacars-buggies]
6. A railed craft ignores velocity, so a landed car being driven is woken with
   `Vehicle.TakeOffRails()` (which just clears the on-rails flag in `_props`). A car coasting on
   its orbit is first rebuilt with `PhysicsStates.UpdateFromAnalytic`; see
   [keeping a thrusting craft off rails](./keep-thrusting-craft-off-rails.md).

Guards that keep it cheap and stable:

| Condition | Behaviour |
|---|---|
| `dt <= 0` (paused) | inputs are read, nothing is written |
| `dt > 0.1` s (time warp) | springs and tyres skipped (`warped`), the car rests on its colliders; rockets still push |
| on rails, no input, no rockets, no righting queued | return early: a parked car costs nothing |
| no physics bubble or parent is not a `Celestial` | skipped, reason stored in `Entry.Skipped` for the bridge |
| any exception | the car is removed from `Active` and one error is logged; the prefix itself also catches |

Righting (the panel's **Unflip** button, or the bridge's `drive unflip`) is queued in a static
dictionary from the GUI and consumed by the next prefix call. `Righting.Solve` returns the shortest
turn that puts the car's up on the ground's up (a 180 degree roll about forward when it is exactly
upside down) and the lift that leaves the lowest tyre 0.15 m clear; the step then adds the lift to
`PositionPhys`, post-multiplies `Body2Phys` by the turn, cancels the ground-relative velocity and
zeroes `AngularVelocityPhys`.[^ksacars-righting]

## Lifecycle and timing

Settled from build 2026.10.10.5554. The prefix runs inside `Program.PrepareFrame`
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2236), L2141-L2236), on the main thread, in this order:[^ksa-program][^ksa-universe]

| # | Program.cs line | What runs | Physics |
|---|---|---|---|
| 1 | L2151-L2162 | Waits on the nearest-orbit, orbit, vehicle and cloth schedulers | joins the previous step |
| 2 | L2164, L2167, L2170 | `ApplyOrbitSolvers`, `ApplyVehicleSolvers` (writes the worker's `NewKinematicStates` over `_kinematicStates` in `Vehicle.UpdateFromTaskResultsUnsynchronized`), `ApplyClothSolvers` | settled |
| 3 | L2179, L2181 | `Glfw.PollEvents` (key callbacks, so `Vehicle.OnKey`), `InputEvents.ApplyInputEvents` (`Vehicle.ProcessInput`, teleports, spawns) | settled |
| 4 | L2196-L2201 | Nearest-orbit job queued; it reads flight plans and cached orbit points while the rest of `PrepareFrame` runs | settled (that job in flight) |
| 5 | L2215-L2216 | `ProcessAutoWarp`, `GetJobSimStep` builds the `SimStep` | settled |
| 6 | L2217-L2219 | `ExecuteNextClothSolvers` (cloth step starts), `PartTree.FlushDirtyDerived`, `FlushDirtyResourceManagers` | cloth in flight |
| 7 | L2220 | `Universe.ExecuteNextVehicleSolvers` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2139), L2126-L2139): only when `CurrentSystem != null`, `PrepareVehicleWorkers` (L2130) calls `PrepareWorker(simStep)` for every vehicle in `CurrentSystem.All` (L2148). **The prefix runs here**, then the body, which ends in `PrepareFromVehicle` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2422), L2422) | settled for vehicles |
| 8 | Universe.cs L2131-L2137 | `SyncGroundClutter`, `_vehicleUpdateTask.PrepareFrame`, `JobSystems.VehicleSolver.Queue` and `ExecuteJobs` | vehicle step starts |
| 9 | L2221 | `ExecuteNextOrbitSolvers` | all in flight |
| 10 | later in `OnFrame` | `StarMapBeforeGui`, `StarMapAfterGui`, `StarMapAfterOnFrame` | in flight |

`PrepareFromVehicle` copies `BubbleOrigin`, `KinematicStates` and the flight computer into the
worker's `UpdateData` (`VehicleUpdateData.Prepare` leaves `NewVehicleProperties` null, so the worker
reads the vehicle's live props, which is why `TakeOffRails` in the prefix reaches the
step).[^ksa-vehicleupdatestate][^ksa-vehicle] So inside the prefix:

- The vehicle holds the result of the step that was just applied, the same epoch every other
  engine sample has this frame.
- `simStep.DeltaTime` is the length of the step the worker is about to integrate (built by
  `Universe.GetJobSimStep` from this frame's `dtPlayer` and simulation speed). The solvers are
  dispatched every frame, so the prefix also runs while paused, with `DeltaTime` 0; `Buggies.Step`
  writes nothing then.
- This frame's key presses have already been applied to `_manualControlInputs`, so
  `GetThrusterFlags()` is current (see [driving with flight keys](./drive-with-flight-keys.md)).
- Writes to `Kinematic` are copied into the worker snapshot a few lines later, so they are
  integrated by this step. Every StarMap frame hook (`StarMapBeforeGui` is a prefix on
  `OnDrawUiFrame`, `StarMapAfterGui` a postfix on `OnDrawUiViewports`, `StarMapAfterOnFrame` a
  postfix on `OnFrame`) runs after step 8, while the step is in flight, and the next
  `ApplyVehicleSolvers` overwrites whatever it wrote to kinematic state.
- The window is per vehicle: a vehicle earlier in `CurrentSystem.All` has already been snapshotted
  when a later one's prefix runs, so a prefix should only write the vehicle it was called for.
- The cloth step (step 6) and the nearest-orbit job (step 4) are already running. The prefix writes
  only kinematic state and props; `UpdateFromAnalytic` reads the orbit but does not change it.

`StarMapAllModsLoaded` is a Harmony postfix StarMap puts on `ModLibrary.LoadAll`, so the patch is
installed once, after asset loading and before any scene exists.[^starmap-modlibrarypatches]
The prefix runs inside the engine's frame loop: it must not throw, and it must not touch ImGui or
Vulkan state. The mod's own analysis of this order is in `docs/KSA-FRAME-ORDER.md` (written
against build 2026.8.22.5348; the order it describes still holds in 2026.10.10.5554, but it lists
only two scheduler waits and none of the cloth or `PartTree` steps).[^ksacars-frame-order]

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/PhysicsHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/PhysicsHook.cs) | Installs the prefix, catches every exception, pins the signature |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L519-L714) | `Physics` / `Step`: inputs, guards, frame conversions, impulse to velocity and spin, righting |
| [src/KSACars/Sim/Righting.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/Righting.cs) | Turn and lift that set a car on its wheels, in the car's own frame |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L37-L57) | Installs all patches from `StarMapAllModsLoaded`, removes them on unload or after 10 faults |

## Snippets

```csharp
// src/KSACars/Ksa/PhysicsHook.cs L77-L94
    private static void BeforePrepareWorker(Vehicle __instance, SimStep simStep)
    {
        try
        {
            Buggies.Physics(__instance, simStep.DeltaTime);
        }
        catch (Exception e)
        {
            if (_complained) return;
            _complained = true;
            Log.Error("car physics failed inside the engine's step", e);
        }
    }

    // Never called. It exists so the compiler emits a reference to the patched method, which is what
    // puts Vehicle.PrepareWorker in docs/KSA-API-SURFACE.md and turns a signature change in KSA into a
    // build error here.
    private static void PinTheSignature(Vehicle vehicle, SimStep step) => vehicle.PrepareWorker(step);
```

```csharp
// src/KSACars/Ksa/Buggies.cs L586-L605
        PhysicsStates states = craft.GetPhysicsStatesMutable();

        // A car coasting on its orbit is rebuilt from it, as KSA rebuilds a craft it is about to split;
        // one standing on the ground is on rails too, held to the ground and not to an orbit, and is
        // only woken, below.
        bool coasting = railed && !craft.Situation.HasAnyContact();
        if (coasting)
        {
            if (!burning) return;
            Orbit orbit = craft.Orbit;
            states.UpdateFromAnalytic(orbit, in orbit.StateVectors, craft.Body2Cce, craft.BodyRates, Situation.Maneuvering);
            railed = false;
        }

        PhysicsStates.GetStatesCcf(in states.Origin, in states.Kinematic,
                                   out double3 positionCcf, out double3 velocityCcf, out doubleQuat body2Ccf);
        doubleQuat ccf2Body = body2Ccf.Inverse();
        doubleQuat body2Phys = states.Kinematic.Body2Phys;
        double3 spinBody = states.Kinematic.AngularVelocityPhys.Transform(body2Phys.Inverse());
        double3 velocityBody = velocityCcf.Transform(ccf2Body);
```

```csharp
// src/KSACars/Ksa/Buggies.cs L689-L713
        double3 dv = impulse.Linear / mass;
        Symmetric3x3 inverse = Symmetric3x3.Invert(craft.TotalMassPropsBody.Inertia);
        double3 l = impulse.Angular;
        double3 dw = new((inverse.XX * l.X) + (inverse.YX * l.Y) + (inverse.ZX * l.Z),
                         (inverse.YX * l.X) + (inverse.YY * l.Y) + (inverse.ZY * l.Z),
                         (inverse.ZX * l.X) + (inverse.ZY * l.Y) + (inverse.ZZ * l.Z));

        // The wheels are off the ground, so the keys that drove them fly the car instead.
        // Under warp the wheels are not stepped, so the hull's own contact says whether it is down.
        bool airborne = warped ? !e.Scraping : !e.Drive.Grounded.Any(g => g);
        double3 groundUpBody = Vec.Unit(positionCcf).Transform(ccf2Body);
        LiftPush lift = Lift.Step(new LiftInput(e.Rockets, e.Input.Throttle, e.Input.Steer, e.Turn), airborne, up, forward,
                                  groundUpBody, spinBody, gravity, dt);
        dv += hovering ? Hover.Push(up, groundUpBody, e.Climb, gravity, dt) : lift.Velocity + Downforce.Push(pressed, up, dt);
        dv += Boost.Push(e.Boosting, forward, dt);
        dw += lift.Spin;
        if (!Vec.IsFinite(dv) || !Vec.IsFinite(dw)) return;

        states.Kinematic.VelocityPhys += dv.Transform(body2Phys);
        states.Kinematic.AngularVelocityPhys += dw.Transform(body2Phys);

        // Woken the way KSA wakes a landed craft something has bumped into. Not rebuilt from its orbit,
        // as a craft in flight is: a landed craft on rails is held to the ground, not to that orbit, and
        // rebuilt from it the car is left standing still in space while the planet turns away at 400 m/s.
        if (railed) craft.TakeOffRails();
```

```csharp
// current/decomp/KSA/Universe.cs L2141-L2155
	public static void PrepareVehicleWorkers(SimStep simStep)
	{
		_orphanVehicles.Clear();
		LookupCollection<Astronomical>.TypeFilter<Vehicle> enumerator = CurrentSystem.All.OfType<Vehicle>().GetEnumerator();
		while (enumerator.MoveNext())
		{
			Vehicle current = enumerator.Current;
			current.PrepareWorker(simStep);
			VehicleUpdateState vehicleUpdateState = current.TakeUpdateStateIfOrphaned();
			if (vehicleUpdateState != null)
			{
				_orphanVehicles.Add(vehicleUpdateState);
			}
		}
	}
```

## Game APIs touched

| API | Purpose |
|---|---|
| `Vehicle.PrepareWorker(SimStep)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423)) | `public virtual`; patched with a prefix. Its body clears held input, ramps throttle and calls `PrepareFromVehicle` |
| `Universe.PrepareVehicleWorkers` / `ExecuteNextVehicleSolvers` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2155)) | Main-thread caller of `PrepareWorker`, before the vehicle solver job is queued |
| `Universe.ApplyVehicleSolvers` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1951)) | Applies the previous worker result and advances `_lastSimStep` |
| `Vehicle.UpdateFromTaskResultsUnsynchronized` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2435-L2442)) | Overwrites `_kinematicStates`; why late writes are lost |
| `VehicleUpdateState.PrepareFromVehicle` ([current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L296-L306)) | Copies `BubbleOrigin` and `KinematicStates` into the worker's input |
| `Vehicle.GetPhysicsStatesMutable` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L922-L931)) | `ref struct` view over the vehicle's own state fields |
| `PhysicsStates.GetStatesCcf`, `UpdateFromAnalytic` ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L428)) | Planet-fixed position, velocity and attitude; rebuild from the orbit |
| `Vehicle.TakeOffRails` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L933-L936)) | Clears the on-rails flag so velocity writes are integrated |
| `Vehicle.TotalMassPropsBody`, `TotalMass`, `CenterOfMassAsmb`, `HasPhysicsBubble`, `Situation` | Mass, inertia tensor (`BepuUtilities.Symmetric3x3`), centre of mass, gating |
| `SituationEx.IsOnRails`, `HasAnyContact`, `HasTerrainContact` ([current/decomp/KSA/SituationEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SituationEx.cs)) | Railed, parked, coasting and scraping tests[^ksa-situationex] |

## Pitfalls and version notes

- **Nothing in the prefix may throw.** Both `PhysicsHook` and `Buggies.Physics` catch and log once;
  a failing car is dropped from the active set rather than retried every frame.
- **The prefix runs before `ClearHeldPlayerInput`.** `PrepareWorker` clears the controlled craft's
  held keys (thruster flags, sprint, grab, engine flags) when it is not the active controlled vehicle,
  when ImGui wants the keyboard, or above 30x simulation speed, but only after the prefix
  ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2398-L2401), L2398-L2401); the prefix reads that frame's flags and the
  clear takes effect for the mod the following frame.[^ksa-vehicle]
- **Explicit impulses ring when the step is long.** The springs are left out past a 0.1 s step;
  the mod's tests show why (see [headless tests](./headless-sim-tests.md)).
- **Forces act on the whole craft.** The impulse goes through the craft's centre of mass and
  inertia, so a car docked to something else drives the whole assembly.
- **`Buggies.Physics`' XML doc names an `AttitudeHook`** that does not exist in this checkout; the
  caller is `PhysicsHook`.
- **`Universe.ApplyVehicleSolvers` is public**, but calling it from a hook to "apply" a write also
  advances `_lastSimStep` and `UpdatePerFrameData`; the mod's frame-order notes warn against it.[^ksacars-frame-order]
- `PrepareWorker` is `public virtual`; `KittenEva` overrides it and calls `base.PrepareWorker`, so
  the prefix also sees every kitten on EVA. The first `Active.TryGetValue` filters them out cheaply.
- Verified against build 2026.10.10.5554. `PinTheSignature` makes a signature change a compile
  error, see [API surface pinning](./api-surface-pinning-and-diff.md).

## See also

- [Wheel, suspension and tyre model](./wheel-suspension-tyre-model.md)
- [Terrain contact under each wheel](./terrain-contact-under-wheels.md)
- [Rocket lift, hover, boost and downforce](./rocket-lift-hover-boost-downforce.md)
- [Keeping a thrusting craft off rails](./keep-thrusting-craft-off-rails.md)
- [Driving with the flight keys](./drive-with-flight-keys.md)
- [Mod overview](../overview.md)
- [Mutate simulation state safely relative to the physics step](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) - how-to article demonstrating this feature
- [Add velocity deltas in the PrepareWorker prefix](../../../howto/physics/solver/inject-forces-and-impulses-at-the-physics-safe-point/inject-forces-and-impulses-at-the-physics-safe-point-1.md) - how-to article demonstrating this feature
- [Write inside a Vehicle.PrepareWorker prefix](../../../howto/physics/solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-1.md) - how-to article demonstrating this feature

[^ksacars-physicshook]: src/KSACars/Ksa/PhysicsHook.cs
[^ksacars-buggies]: src/KSACars/Ksa/Buggies.cs
[^ksacars-mod]: src/KSACars/Ksa/KSACarsMod.cs
[^ksacars-righting]: src/KSACars/Sim/Righting.cs
[^ksacars-frame-order]: docs/KSA-FRAME-ORDER.md
[^ksacars-blocked]: docs/BLOCKED-ON-KSA.md
[^starmap-modlibrarypatches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^ksa-program]: KSA.Program (decompiled)
[^ksa-universe]: KSA.Universe (decompiled)
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-vehicleupdatestate]: KSA.VehicleUpdateState (decompiled)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled)
[^ksa-situationex]: KSA.SituationEx (decompiled)
