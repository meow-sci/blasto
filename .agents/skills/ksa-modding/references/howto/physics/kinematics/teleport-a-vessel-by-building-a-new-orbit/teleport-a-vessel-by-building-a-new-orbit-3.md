---
type: How-To
title: Teleport at the PrepareFrame handoff and fire at a clicked point
description: Capture a target-relative click snapshot in the GUI pass and, at the PrepareFrame handoff, build an orbit around the target's parent and teleport the source there with a velocity relative to the target, handling kittens, hulls and terrain
category: physics
group: kinematics
topic: teleport-a-vessel-by-building-a-new-orbit
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/dent-wizard-vessel-launcher]
techniques: [harmony-transpiler, kinematic-teleport, mesh-raycast]
hooks: [starmap StarMapAfterGui, harmony-transpiler KSA.Program.PrepareFrame]
lifecycle: [frame:draw-ui, frame:prepare]
game_types: [KSA.Vehicle, KSA.Orbit, KSA.KittenEva, KSA.Cursor, KSA.Camera, KSA.Part, KSA.Ray, KSA.InputEvents]
tags: [physics, kinematics, teleport, orbit, launcher, cursor, relative-velocity, kitten, unscience]
---

Outcome: the player picks a vessel, EVA kitten or debris as a "source", points the main camera and clicks a vessel or terrain
surface up to 10 km away. On the next frame the source's centre of mass appears at the camera and flies toward the clicked
point at a speed measured relative to the target, so a 10 m/s shot at a craft doing 7.5 km/s arrives at 10 m/s. unscience's Dent Wizard
does it; it is also the kitten-flavoured recipe (sphere-proxy picking for `KittenEva`, null attitude and rates so EVA conventions
survive, a flight-plan identity check to detect a rejected trajectory). Prerequisites: StarMap, Harmony and the shared handoff hook
([mutate approach 3](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-3.md)).

## Background: how the game does it

`Vehicle.Teleport(orbit, null, null)` recomputes the flight plan, calls `RemoveFromCurrentBubble()` and, with the new orbit
around the target's parent, detects a parent change in `SetFlightPlan` and moves the vessel between the parents' `Children`
lists, so cross-parent launches need no extra code.[^ksa-vehicle] Two engine facts shape the timing: the
`NearestOrbitPointJob` re-queued at `Program.cs` L2196-L2200 may still be reading the source's cached orbit points, which
`Teleport` -> `SetFlightPlanUnsynchronized` -> `FlightPlan.ReleaseCachedPoints` disposes; and `ExecuteNextVehicleSolvers` has
already called `Vehicle.PrepareWorker` for every vessel by the GUI pass, so a teleport there would race the running step
("No render-loop teleport").

## Lifecycle and timing

| Phase | Hook | What the code does | State |
|---|---|---|---|
| `frame:draw-ui` | `StarMapAfterGui` -> `RenderFloatingWindows` | Draw the hint, read the cursor ray, pick, store a `LaunchRequest` | Main-thread vessel state is the result applied at the top of the frame; workers are computing the next step |
| `frame:prepare` (next frame) | `PhysicsFrameHook.BeforePhysics` inside `Program.PrepareFrame` | Join the orbit job, build the orbit, `Vehicle.Teleport` | Previous step applied, input applied, no vehicle worker running |

The launch runs at the handoff the `lib-physics-frame-hook` transpiler creates at the `Universe.GetJobSimStep` call
(`Program.cs` L2216). `stateTime` is `SimStep.PreviousTime`, the time the just-applied state represents and the start of the step
about to run, so the new orbit and the solver agree on epoch. `Teleport` calls `RemoveFromCurrentBubble()`; two lines later
`ExecuteNextVehicleSolvers` -> `PrepareVehicleWorkers` collects the bubble-less vessel and the worker re-homes it. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md). The request carries target-relative
offsets, so one frame of orbital motion between click and launch does not shift the aim.

## Steps

1. Arm and capture the click. On a left click that ImGui did not capture (`WantCaptureMouse` false) over the main viewport,
   pick. The ray is `Cursor.GetEgoRay(Program.MainViewport)`; candidates compete by hit distance, capped at 10 km: every
   part of every other vessel through `Part.RayCastEgo` with `GetMatrixAsmb2Ego(camera)`; `KittenEva` vessels through a sphere proxy
   (root part position, radius `BoundingSphereRadiusBody` times the largest `ScaleTotal` axis) because an EVA avatar has no part
   mesh; and the terrain of `camera.NearbyCelestial` by a CPU height-field march (128 samples spaced quadratically, dense near
   the camera, then 24 bisection steps):

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

   See [cursor picking](../../mod-simulation/ray-cast-against-vessel-meshes-and-terrain/ray-cast-against-vessel-meshes-and-terrain-2.md).
2. Store a snapshot, not positions. For a vessel target store the camera and hit offsets from the target in the parent's Cci axes
   (`target.Parent.GetCce2Cci()`); for terrain store camera and hit positions in the body-fixed Ccf frame. The source's centre of mass
   goes to the camera origin (`-camera.GetPositionEgo(target)`), not the ray origin on the near plane.
3. Queue one request; ignore further clicks until it fires:

```csharp
// dent-wizard.lib/DentWizardSubmod.cs L66-L77
    internal void AcceptPick(LaunchRequest? request)
    {
        if (!_armed || _pending != null) return;
        if (request == null)
        {
            SetStatus("No target within 10 km. Click again or press Esc.", true);
            return;
        }
        _pending = request;
        _armed = _automatic;
        SetStatus("Launch queued for the next physics handoff.", false);
    }
```

4. Fire at the handoff: join the orbit readers, then execute with the committed state time:

```csharp
// dent-wizard.lib/DentWizardSubmod.cs L42-L51
    private void FirePending(double dt, UniverseTime stateTime)
    {
        var request = _pending;
        _pending = null;
        if (request == null) return;
        // The launch teleports the source, which disposes orbit points the hover job may read.
        PhysicsFrameHook.JoinOrbitReaders();
        try { SetStatus(request.Execute(stateTime), false); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }
```

5. Build the orbit and teleport. Re-validate identities (source and target still live, same parent), advect the snapshot with the
   target's committed position (`GetPositionCci() + cameraOffset`), build `Orbit.CreateFromStateCci(parent, stateTime, ...)`, call
   `Source.Teleport(orbit, null, null)`, and detect a rejected trajectory because `FlightPlan` is still the same object. Terrain
   targets use `Parent.GetCcf2Cci(stateTime)` and `GetAngularVelocityCci()` for the surface velocity at the hit:

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

6. Velocity relative to the target including the hit point's rotation: `v = v_target + omega x r_hit + speed * dir/|dir|`:

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

## Complete example

| File | Role |
|---|---|
| [dent-wizard.lib/DentWizardPicker.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard.lib/DentWizardPicker.cs#L13-L104) | Ray, vessel and kitten and terrain candidates |
| [dent-wizard.lib/LaunchRequest.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard.lib/LaunchRequest.cs#L8-L49) | Click snapshot and `Execute` |
| [dent-wizard.lib/LaunchMath.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard.lib/LaunchMath.cs) | Velocity relative to the target |
| [dent-wizard.lib/DentWizardSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard.lib/DentWizardSubmod.cs#L42-L77) | Queue and fire at the handoff |
| [dent-wizard.lib/DentWizardSubmod.Ui.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard.lib/DentWizardSubmod.Ui.cs#L101-L121) | Arming and click capture |

## Variations and gotchas

- Null attitude and rates preserve the source's orientation and spin, including EVA conventions.
- `BeforePhysics` subscribers run in subscription order; a launch of a welded source is overwritten by the weld in the same handoff.
- The UI drag range (0.1 to 100 m/s) is not a validation limit; the code only requires at least 0.001 m/s.
- Reading vessel matrices in the GUI pass is safe; mutating there is not.
- Because the previous step's results were already committed, a launch here does not discard the source's own module results
  (the bug Garry's Torch hit when it teleported after the UI).

## See also

- [Overview and comparison](overview.md)
- [Teleport in the solver-dispatch window](teleport-a-vessel-by-building-a-new-orbit-1.md)
- Mod feature: [Dent Wizard vessel launcher](../../../../mods/unscience/features/dent-wizard-vessel-launcher.md)
- The topic spawn-an-eva-kitten-from-code (other batch) covers EVA kitten spawning

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
