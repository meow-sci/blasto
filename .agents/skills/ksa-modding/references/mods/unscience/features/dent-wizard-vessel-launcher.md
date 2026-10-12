---
type: Mod Feature
title: Camera-origin vessel launcher with target-relative velocity
description: Dent Wizard teleports a chosen vessel, EVA kitten or debris to the camera and fires it at a clicked hull or terrain point by building a new orbit and calling Vehicle.Teleport at the PrepareFrame physics handoff
mod: unscience
submod: dent-wizard
category: physics
techniques: [starmap-lifecycle, imgui-drawlist, custom-physics, harmony-transpiler]
hooks:
  - starmap StarMapAfterGui (via unscience ISubmod.RenderFloatingWindows, click capture)
  - starmap StarMapBeforeGui (via unscience ISubmod.Update, source validation)
  - harmony-transpiler KSA.Program.PrepareFrame (shared PhysicsFrameHook.BeforePhysics, launch)
lifecycle: [frame:draw-ui, frame:prepare]
game_types:
  - KSA.Vehicle
  - KSA.Orbit
  - KSA.FlightPlan
  - KSA.Program
  - KSA.Cursor
  - KSA.Camera
  - KSA.Celestial
  - KSA.IParentBody
  - KSA.Part
  - KSA.Ray
  - KSA.KittenEva
  - KSA.SimStep
  - KSA.JobSystems
  - KSA.VehicleUpdateTask
tags: [teleport, launch, picking, raycast, terrain, orbit, physics-handoff, threading]
---

Dent Wizard lets the player pick any live vessel, EVA kitten or debris fragment as a "source",
point the main camera, and left-click a vessel or terrain surface up to 10 km away. On the next
frame the source's centre of mass appears at the camera and flies toward the clicked point at a
chosen speed measured relative to the target, so a 10 m/s shot at a craft doing 7.5 km/s in orbit
arrives at 10 m/s. Collisions, dents and structural failure are the game's own.[^dent-wizard-readme]

Mechanism in one line: the ImGui click stores a target-relative "click snapshot", and a
`PhysicsFrameHook.BeforePhysics` callback later turns it into an `Orbit` around the target's parent
body and calls `Vehicle.Teleport(orbit, null, null)` after the previous physics results were applied
and before the next step is snapshotted.

## How it works

1. **Arming and click capture** ([dent-wizard.lib/DentWizardSubmod.Ui.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard.lib/DentWizardSubmod.Ui.cs#L101-L121)).
   "Aim and fire" sets `_armed`. Every frame `RenderFloatingWindows` draws a cursor hint on the
   foreground draw list and, on a left click that ImGui did not capture (`WantCaptureMouse` false)
   over the main viewport (`Program.HoveredViewport.IsMain()`), calls `DentWizardPicker.Pick`.
   Esc, right-click, entering the editor or losing the source cancel. "Automatic mode" keeps the
   panel armed so every click re-fires the same source.
2. **Picking** ([dent-wizard.lib/DentWizardPicker.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard.lib/DentWizardPicker.cs#L13-L104)). The ray is
   `Cursor.GetEgoRay(Program.MainViewport)` (camera-relative EGO space). Candidates compete by hit
   distance, capped at 10 km:
   - every part of every other vessel through `Part.RayCastEgo` with the vessel's
     `GetMatrixAsmb2Ego(camera)` (art meshes, nested sub-parts included; the source is skipped);
   - `KittenEva` vessels through a sphere proxy (root part position, radius
     `BoundingSphereRadiusBody` times the largest `ScaleTotal` axis) and `Ray.Raycast(BoundingSphere3D)`,
     because an EVA avatar has no part mesh to hit;
   - terrain of `camera.NearbyCelestial` by a CPU heightfield march: 128 samples spaced
     quadratically (`range * f^2`, dense near the camera), then 24 bisection steps on
     `radius - MeanRadius - GetTerrainHeightFromDirCcf(dir, accurate: true)`. The march range is
     the best vessel distance so far, so a hill in front of a hull wins.
3. **Click snapshot** ([dent-wizard.lib/LaunchRequest.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard.lib/LaunchRequest.cs#L8-L10)). The picker never
   stores absolute positions. For a vessel it stores the camera and hit offsets from the target in
   the parent's CCI axes (`target.Parent.GetCce2Cci()`); for terrain it stores camera and hit
   positions in the body-fixed CCF frame. The source's centre of mass goes to the camera origin
   (`-camera.GetPositionEgo(target)`), not the ray origin on the near plane.
4. **Queue** ([dent-wizard.lib/DentWizardSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard.lib/DentWizardSubmod.cs#L66-L77)). `AcceptPick` stores one
   `_pending` request; further clicks are ignored until it fires.
5. **Launch at the handoff** ([dent-wizard.lib/DentWizardSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard.lib/DentWizardSubmod.cs#L42-L51),
   [dent-wizard.lib/LaunchRequest.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard.lib/LaunchRequest.cs#L12-L49)). `FirePending` takes the request, calls
   `PhysicsFrameHook.JoinOrbitReaders()`, then `Execute(stateTime)`:
   - re-validates identities: source still live in the current system, not the edited vehicle, not
     in the editor; target still live, not the source, and still orbiting the same parent;
   - vessel target: `position = target.GetPositionCci() + cameraOffset`, direction
     `hitOffset - cameraOffset`, velocity from `LaunchMath.Velocity` with the target's
     `GetVelocityCci()` and its angular velocity `BodyRates` rotated by `GetBody2Cci()`;
   - terrain target: transform the CCF offsets with `Parent.GetCcf2Cci(stateTime)` and use
     `Parent.GetAngularVelocityCci()` for the surface velocity at the hit point;
   - `Orbit.CreateFromStateCci(parent, stateTime, position, velocity, source.Orbit.OrbitLineColor)`,
     then `Source.Teleport(orbit, null, null)`; null attitude and rates keep the source's own
     orientation and spin, including EVA conventions;
   - if `Source.FlightPlan` is still the same object afterwards, KSA rejected the trajectory and the
     shot is reported as failed; otherwise `UpdatePerFrameData()` refreshes cached per-frame data.
6. **Velocity** ([dent-wizard.lib/LaunchMath.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard.lib/LaunchMath.cs#L14-L24)):
   `v = v_target + omega x r_hit + speed * dir / |dir|`, with finite-value checks and a 0.001 m/s
   floor. The UI drag range (0.1 to 100) is not a validation limit; typed values may exceed it.

Cross-parent launches need no extra code: the new orbit is built around the target's parent, and
`Vehicle.Teleport` calls `SetFlightPlan`, which detects the parent change and moves the vessel
between the parents' `Children` lists.[^ksa-vehicle]

## Lifecycle and timing

| Phase | Hook | What the code does | State at that instant |
|---|---|---|---|
| `frame:draw-ui` | `[StarMapBeforeGui]` -> `ISubmod.Update` (or the hidden-HUD replay) | Drop a source that left the world; cancel in the editor | Main thread; next physics step already dispatched |
| `frame:draw-ui` | `[StarMapAfterGui]` -> `RenderFloatingWindows` | Draw the hint, read the cursor ray, pick, store a `LaunchRequest` | Main-thread vessel state is the result applied at the top of this frame; workers are computing the next step into their own buffers |
| `frame:prepare` (next frame) | `PhysicsFrameHook.BeforePhysics` inside `Program.PrepareFrame` | Join the orbit job, build the orbit, `Vehicle.Teleport` | Previous step applied (`ApplyVehicleSolvers`), input applied, no vehicle worker running |

The click is captured during the GUI phase because that is where ImGui input and the rendered
camera of the current frame exist. Reading vessel matrices there is safe: the vehicle worker only
publishes into main-thread `Vehicle` fields during `Universe.ApplyVehicleSolvers` at the top of
`PrepareFrame`. Mutating there is not: `ExecuteNextVehicleSolvers` has already called
`Vehicle.PrepareWorker` for every vessel, so a teleport would race the running step. The README
states the rule as "No render-loop teleport".[^vehicle-physics]

The launch runs in the shared `Program.PrepareFrame` handoff that the `lib-physics-frame-hook`
transpiler creates at the `Universe.GetJobSimStep` call (L2216 in build 5554).[^ksa-program] At that
point:

- `stateTime` is `SimStep.PreviousTime`, the time the just-applied state represents and the start of
  the step about to run, so the new orbit and the solver agree on epoch.
- The nearest-orbit hover job was re-queued at L2196-L2200 and may still be reading the source's
  cached orbit points; `Teleport` -> `SetFlightPlanUnsynchronized` -> `FlightPlan.ReleaseCachedPoints`
  disposes them. Hence `JoinOrbitReaders()` (one `JobSystems.NearestOrbitAndPerformanceWorker.Wait()`
  per frame, skipped when nothing is pending).[^ksa-vehicle]
- `Teleport` calls `RemoveFromCurrentBubble()`. Two lines later `ExecuteNextVehicleSolvers` ->
  `PrepareVehicleWorkers` collects the bubble-less vessel through `TakeUpdateStateIfOrphaned`, and the
  worker's `VehicleUpdateTask.Run` re-homes it with `AssignOrphanVehicles` before stepping.[^ksa-vehicle-update-task]
  Bubble eviction after a parent change also happens on the worker (`ReassignDivergentVehicles`).
- Because the previous step's results were already committed, a launch made here does not discard
  the source's own module results (the bug Garry's Torch hit when it teleported after the UI).

The request carries target-relative offsets, so one frame of orbital motion between click and
launch does not shift the aim: the camera position is re-anchored on the target's committed
position at launch time. Terrain offsets are body-fixed and re-rotated at `stateTime`.

`BeforePhysics` subscribers run in subscription order. In the supermod, Dent Wizard subscribes in
`Initialize`, before Godzilla's session check and before Garry's Torch registers its weld callback in
`Patcher.Patch`, so a launch of a welded source is overwritten by the weld in the same handoff.

## Key code

| Path | Role |
|---|---|
| [dent-wizard.lib/DentWizardSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard.lib/DentWizardSubmod.cs) | Submod: `BeforePhysics` subscription, pending shot, arming state, transient save participant |
| [dent-wizard.lib/DentWizardSubmod.Ui.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard.lib/DentWizardSubmod.Ui.cs) | Form, source combo with live mass, floating click gesture |
| [dent-wizard.lib/DentWizardPicker.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard.lib/DentWizardPicker.cs) | Part-mesh, EVA-sphere and terrain picking within 10 km |
| [dent-wizard.lib/LaunchRequest.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard.lib/LaunchRequest.cs) | Click snapshot and the launch itself (`Orbit.CreateFromStateCci` + `Vehicle.Teleport`) |
| [dent-wizard.lib/LaunchMath.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard.lib/LaunchMath.cs) | Target-relative velocity with spin, finite checks |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L68) | Supermod applies the shared handoff hook for Dent Wizard |
| [dent-wizard.tests/Program.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard.tests/Program.cs) | Managed checks: velocity math, deferred geometry, rejection paths, one orbit-reader join per shot |
| [dent-wizard/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard/README.md) | Player-facing semantics and limits |
| [scope/vehicle-physics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/vehicle-physics.md) | Authors' integration scope (Dent Wizard section) |

## Snippets

The launch, executed at the handoff with the committed state time:

```csharp
// dent-wizard.lib/LaunchRequest.cs L12-L49
    public string Execute(UniverseTime stateTime)
    {
        if (Program.EditorFlag || Source.IsDisposed || Source.IsEditedVehicle
            || !VehicleProvider.GetAllVehicles(true).Contains(Source))
            throw new InvalidOperationException("The source is no longer available in flight.");
        double3 position, direction, velocity;
        if (Target != null)
        {
            if (Target.IsDisposed || Target.IsEditedVehicle || Target == Source || Target.Parent != Parent
                || !VehicleProvider.GetAllVehicles(true).Contains(Target))
                throw new InvalidOperationException("The target disappeared or changed parent body; select it again.");
            // Advect the click snapshot with the target's committed position. This avoids a frame
            // of orbital drift between the rendered click and the next safe physics handoff.
            position = Target.GetPositionCci() + CameraOffset;
            direction = HitOffset - CameraOffset;
            velocity = LaunchMath.Velocity(direction, Speed, Target.GetVelocityCci(),
                double3.Transform(Target.BodyRates, Target.GetBody2Cci()), HitOffset);
        }
        else
        {
            var ccf2Cci = Parent.GetCcf2Cci(stateTime);
            position = double3.Transform(CameraOffset, ccf2Cci);
            var hit = double3.Transform(HitOffset, ccf2Cci);
            direction = hit - position;
            velocity = LaunchMath.Velocity(direction, Speed, double3.Zero, Parent.GetAngularVelocityCci(), hit);
        }
        if (!LaunchMath.IsFinite(position) || position.LengthSquared() <= 0)
            throw new InvalidOperationException("The camera launch position is invalid.");
        var orbit = Orbit.CreateFromStateCci(Parent, stateTime, position, velocity, Source.Orbit.OrbitLineColor);
        var oldPlan = Source.FlightPlan;
        // Null attitude/rates preserve source orientation and spin, including EVA conventions.
        // Teleport also rebuilds the flight plan, moves parent membership and leaves its old bubble.
        Source.Teleport(orbit, null, null);
        if (ReferenceEquals(oldPlan, Source.FlightPlan))
            throw new InvalidOperationException("KSA rejected the launch trajectory; the source was not moved.");
        Source.UpdatePerFrameData();
        return $"Fired {Source.Id} at {Target?.Id ?? Parent.Id}, {Speed:G6} m/s relative to the target.";
    }
```

Velocity relative to the target, including the hit point's rotational velocity:

```csharp
// dent-wizard.lib/LaunchMath.cs L14-L24
    public static double3 Velocity(double3 direction, double speed, double3 targetVelocity,
        double3 angularVelocity, double3 hitOffset)
    {
        double length = direction.Length();
        if (!IsValidSpeed(speed) || !IsFinite(direction) || !double.IsFinite(length) || length <= 0
            || !IsFinite(targetVelocity) || !IsFinite(angularVelocity) || !IsFinite(hitOffset))
            throw new ArgumentException("Launch requires a finite direction, target state and speed of at least 0.001 m/s.");
        var velocity = targetVelocity + double3.Cross(angularVelocity, hitOffset) + direction / length * speed;
        if (!IsFinite(velocity)) throw new ArgumentException("The resulting launch velocity is not finite.");
        return velocity;
    }
```

Terrain hit search on the CPU heightfield (quadratic coarse march, then bisection):

```csharp
// dent-wizard.lib/DentWizardPicker.cs L75-L95
        double above = 0;
        // Same CPU heightfield/bisection approach as Graffiti. Fine near-camera steps avoid
        // skipping small nearby hills when the far end of the ray is kilometres away.
        for (int i = 1; i <= 128; i++)
        {
            double fraction = i / 128.0;
            double below = range * fraction * fraction;
            if (Depth(body, origin + direction * below) <= 0)
            {
                for (int j = 0; j < 24; j++)
                {
                    double middle = (above + below) * 0.5;
                    if (Depth(body, origin + direction * middle) <= 0) below = middle;
                    else above = middle;
                }
                hit = origin + direction * below;
                return true;
            }
            above = below;
        }
        return false;
```

What `Vehicle.Teleport` does with the orbit in build 5554 (failure leaves `FlightPlan` untouched):

```csharp
// current/decomp/KSA/Vehicle.cs L2241-L2270
	public void Teleport(Orbit? orbit, doubleQuat? body2Cce, double3? bodyRates)
	{
		Celestial nearbyCelestial = Program.GetNearbyCelestial();
		bool flag = false;
		if (orbit != null)
		{
			FlightPlan flightPlan = new FlightPlan(orbit, Hash);
			flightPlan.ImpactClearanceMargin = BoundingSphereRadiusBody;
			flightPlan.ComputeCompleteTrajectory(out var errorsEncountered, 100, 16, null, resolveImpactsCompletely: true);
			if (errorsEncountered)
			{
				flag = false;
				LogCategory log = DefaultCategory.Log;
				LogCategory logCategory = log;
				bool shouldAppend;
				LogString<Error> message = new LogString<Error>(78, 0, log, out shouldAppend);
				if (shouldAppend)
				{
					message.AppendLiteral("Failed to compute trajectory for new orbit during teleport, aborting teleport.");
				}
				logCategory.Error(message, "Teleport", "C:\\prototype-planet\\KSA\\Vehicle.cs", 2315);
			}
			else
			{
				RemoveFromCurrentBubble();
				flightPlan.CalculateTargetNodes(Target);
				SetFlightPlan(flightPlan);
				flag = true;
			}
		}
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `Vehicle.Teleport(Orbit?, doubleQuat?, double3?)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2241-L2290) | The launch; leaves the bubble, sets the flight plan, overwrites physics state |
| `Vehicle.FlightPlan`, `SetFlightPlanUnsynchronized` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L649-L670) | Identity check for rejection; releases old cached orbit points; parent-change detection |
| `Vehicle.TakeUpdateStateIfOrphaned` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2318-L2321) | How the teleported vessel re-enters the next step |
| `VehicleUpdateTask.Run` / `AssignOrphanVehicles` | [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L261-L297) | Worker-side bubble assignment of orphans |
| `Orbit.CreateFromStateCci(...)`, `Orbit.OrbitLineColor` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1611) | New orbit at `stateTime` around the target's parent |
| `Vehicle.GetPositionCci`, `GetVelocityCci`, `GetBody2Cci`, `BodyRates` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2570-L2630) | Target state at the handoff |
| `IParentBody.GetCce2Cci`, `GetCcf2Cci(UniverseTime)`, `GetAngularVelocityCci` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L39-L90) | Frame conversions and surface rotation |
| `Cursor.GetEgoRay(IViewport)` | [current/decomp/KSA/Cursor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Cursor.cs#L27-L30) | Click ray in EGO space |
| `Program.GetMainCamera`, `MainViewport`, `HoveredViewport`, `EditorFlag` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L484-L486) | Main-viewport-only, flight-only gating |
| `Part.RayCastEgo(...)` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2534-L2550) | Mesh picking; bounding-sphere early-out before the sub-part loop |
| `Ray.Raycast(BoundingSphere3D, out double, out bool)` | [current/decomp/KSA/Ray.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Ray.cs#L38) | EVA sphere proxy |
| `Camera.NearbyCelestial`, `Camera.GetPositionEgo(IPosition)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L71) | Terrain body and floating-origin offsets |
| `Celestial.GetCce2Ccf`, `GetTerrainHeightFromDirCcf`, `MeanRadius` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789) | Heightfield depth test |
| `JobSystems.NearestOrbitAndPerformanceWorker` | [current/decomp/KSA/JobSystems.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/JobSystems.cs#L12) | Joined before the teleport |

## Pitfalls and version notes

- This is an initial ballistic velocity, not an intercept solver: gravity differences, drag, target
  acceleration and target rotation after the shot are not compensated, so long or slow shots miss.
- Vessel offsets are stored in the parent's CCI axes, not the target's body frame, so one frame of
  target rotation between click and launch is not compensated (translation is).
- Aiming needs the HUD: with F2 the StarMap GUI hooks are skipped and the supermod's hidden-HUD
  replay only calls `Update`, not `RenderFloatingWindows`. A shot already pending still fires because
  the handoff is not part of the GUI phase.
- If `PhysicsFrameHook` failed to install (its transpiler rejects an unexpected `PrepareFrame`
  shape), "Aim and fire" is disabled instead of falling back to a UI-time teleport.
- A welded source (Garry's Torch) is re-positioned by its weld in the same handoff; unweld first.
- `Vehicle.Teleport` with a non-null attitude or rates would apply those even when the trajectory is
  rejected; Dent Wizard passes nulls, so a rejected launch changes nothing.[^ksa-vehicle]
- Build 5554 matches the 5482 shapes the authors verified: `Teleport` at `Vehicle.cs` L2241, the
  nearest-orbit job queued before the handoff, and the `Part.RayCastEgo` bounding-sphere early-out
  (L2544-L2549), which is conservative for picking.
- Picking ignores parachute cloth, imported statics and ground clutter; very narrow terrain spikes can
  fall between heightfield samples.

## See also

- [Physics-safe vessel mutation at the PrepareFrame solver handoff](./lib-physics-frame-hook.md)
- [Hidden-HUD frame hook](./lib-hidden-ui-frame-hook.md)
- [Garry's Torch vehicle welding](./garrys-torch-vehicle-welding.md) (the same teleport pattern every frame)
- [The Tick indestructibility](./the-tick-vessel-indestructibility.md) (survive your own shots)
- [Graffiti decal placement picking](./graffiti-decal-placement-picking.md) (the picking approach this mirrors)
- [Dent Wizard sub-mod](../submods/dent-wizard.md) and [mod overview](../overview.md)
- [Teleport at the PrepareFrame handoff and fire at a clicked point](../../../howto/physics/kinematics/teleport-a-vessel-by-building-a-new-orbit/teleport-a-vessel-by-building-a-new-orbit-3.md) - how-to article demonstrating this feature
- [Run a callback at the PrepareFrame handoff with a GetJobSimStep transpiler](../../../howto/physics/solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-3.md) - how-to article demonstrating this feature

[^dent-wizard-readme]: dent-wizard/README.md
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^vehicle-physics]: scope/vehicle-physics.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicle-update-task]: KSA.VehicleUpdateTask (decompiled, build 2026.10.10.5554)
