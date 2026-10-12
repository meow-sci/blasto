---
type: How-To
title: Fly another vehicle through its flight computer and input actions
description: Command attitude, throttle, translation, pulse mode and staging on a craft the player is not flying, queueing from frame hooks and applying in a PrepareWorker prefix
category: vehicles-and-parts
group: vehicles
topic: take-control-of-another-vehicle
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksarmory/features/fly-another-vehicle
  - mods/ksarmory/features/prepareworker-prefix-command-window
techniques: [double-buffer-aware-writes, harmony-prefix, reflection]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:dispatch-solvers]
game_types:
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.ManualControlInputs
  - KSA.InputAction
  - KSA.SequenceList
  - KSA.VehicleReferenceFrame
tags: [autopilot, flight-computer, attitude, throttle, staging, guidance]
---
Outcome: your mod points, throttles, translates and stages a rocket while the player keeps flying something
else (or nothing). KSArmory's ballistic computer flies other people's rockets this way. Every write goes
through a channel the game already uses: the flight computer's `Custom` attitude target (what the engine uses
to point a kitten's manoeuvring unit) and `Vehicle.ProcessInput`, the call the keyboard makes.

Prerequisites: StarMap, Harmony, and the `PrepareWorker` prefix described in
[Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../change-vehicles-without-racing-the-solver/overview.md) article 5. Demonstrated by [ksarmory/fly-another-vehicle](../../../../mods/ksarmory/features/fly-another-vehicle.md) and
[ksarmory/prepareworker-prefix-command-window](../../../../mods/ksarmory/features/prepareworker-prefix-command-window.md).

## Background: how the game does it

- The flight computer is double-buffered across the frame. `ApplyVehicleSolvers` copies the worker's result
  over the live computer (`FlightComputer.CopyFrom(updateData.NewFlightComputer)`)
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2525-L2535)[^ksa-vehicle]; `PrepareWorker` snapshots it for the next worker
  [current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84-L104)[^ksa-vehicleupdatedata]. Every StarMap hook runs after the snapshot, so a computer field
  written there is neither seen by the step in flight nor kept: the next apply overwrites it.
- The keyboard channel needs no prefix. `ProcessInput` sets persistent `_engineFlags`/thruster flag bits that
  `PrepareWorker` reads on the following frame, so calling it from a hook has the same one-frame latency as a
  key press [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3536).
- `PrepareWorker` clears held keys on the **controlled** vehicle when it is inactive, when ImGui wants the
  keyboard, or when simulation speed exceeds 30x [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2422). A craft the player is not flying
  is untouched by that clear.
- The throttle has no setter; only the two controls a player holds down, `MainEngineThrottleUp/Down`, which
  `PrepareWorker` applies at `0.7 * playerDeltaTime` per frame.

## Lifecycle and timing

| Moment | What | Where |
|---|---|---|
| frame N `frame:draw-ui` | guidance decides, calls `SetEngine`, `DriveThrottle`, `DriveTranslation` directly; queues attitude, pulse and stage on the hook | `StarMapAfterGui` |
| frame N+1 `frame:dispatch-solvers` | prefix writes the queued flight-computer fields | `Vehicle.PrepareWorker` |
| frame N+1 worker | integrates the command | worker thread |
| frame N+2 `frame:apply-solvers` | result is visible | `ApplyVehicleSolvers` |

So the command takes effect one step later than it was decided; guidance times cutoffs instead of waiting to
observe them. Staging also has to be in the window, because while the `Sequence` or `EngineControl` gauge canvas is
enabled (both start enabled, so by default) the worker recomputes the controlled craft's stage performance off the main thread on every
step, and a sequence list rebuilt under it throws.
See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. Aim. Build a rotation whose rows are forward, `across = roll x forward`, `third = forward x across`
   (the layout of the engine's own `GetTgt2Cci`), express it in the `EclBody` frame, and write the
   custom target. Supply your own roll reference and carry it forward, because the engine's planet-direction
   reference reverses when the nose points straight up or down:

```csharp
// src/KSArmory/Ksa/VehicleCommand.cs L52-99
    public static bool TryAim(Vehicle craft, double3 directionCci, double3 rollReferenceCci)
    {
        if (!KsaWorld.IsAlive(craft)) return false;
        if (KsaWorld.ParentBody(craft) is not { } parent) return false;

        // Resolved here rather than passed in, because this runs inside the engine's own frame pass
        // where the vehicle's state is fresh and the caller's is a frame old.
        doubleQuat cce2Cci = parent.GetCce2Cci();
        double3 positionCci = (craft.GetPositionEcl() - parent.GetPositionEcl()).Transform(cce2Cci);

        double3 forward = Vec.Unit(directionCci);
        if (forward.Equals(Vec.Zero) || !Vec.IsFinite(positionCci)) return false;

        double3 across = Vec.Unit(Vec.Cross(rollReferenceCci, forward));
        if (across.Equals(Vec.Zero)) return false;

        double3 third = Vec.Unit(Vec.Cross(forward, across));
        if (third.Equals(Vec.Zero)) return false;

        doubleQuat target2Cci = doubleQuat.CreateFromRotationMatrix(new double4x4(
            forward.X, forward.Y, forward.Z, 0.0,
            across.X, across.Y, across.Z, 0.0,
            third.X, third.Y, third.Z, 0.0,
            0.0, 0.0, 0.0, 1.0));

        doubleQuat frame2Cci = VehicleReferenceFrameEx.GetEclBody2Cci(parent.GetCce2Cci());
        doubleQuat target2Frame = doubleQuat.Concatenate(target2Cci, frame2Cci.Inverse());

        FlightComputer computer = craft.FlightComputer;
        computer.AttitudeFrame = VehicleReferenceFrame.EclBody;
        computer.AttitudeTrackTarget = FlightComputerAttitudeTrackTarget.Custom;
        computer.CustomAttitudeTarget = VehicleReferenceFrame.EclBody.QuaternionToEulerAngles(target2Frame);
        computer.AttitudeMode = FlightComputerAttitudeMode.Auto;

        // Re-derive the pointing deadband from the thrusters actually aboard. KSA widens it to
        // whatever one control period of the minimum impulse can produce -- a stability guard, so
        // the tracker is not asked to settle inside its own quantum -- but it only ever raises it.
        // A stack that staged carries the guard sized for its boost RCS onto a bus whose thrusters
        // are two orders finer, and nothing lowers it again: measured at 11.4 degrees of deadband
        // against a live rate bit worth 0.07. Assigning the profile puts it back, and KSA's own max
        // re-establishes the real floor on the same frame.
        computer.SetAttitudeProfile(FlightComputerAttitudeProfile.Strict);

        // An automatic burn would fight this for the attitude and run its own throttle.
        computer.BurnMode = FlightComputerBurnMode.Manual;

        return true;
    }
```

2. Release: give the vehicle back.

```csharp
// src/KSArmory/Ksa/VehicleCommand.cs L102-110
    public static void ReleaseAttitude(Vehicle craft)
    {
        if (!KsaWorld.IsAlive(craft)) return;

        FlightComputer computer = craft.FlightComputer;
        computer.AttitudeMode = FlightComputerAttitudeMode.Manual;
        computer.AttitudeTrackTarget = FlightComputerAttitudeTrackTarget.None;
        computer.CustomAttitudeTarget = double3.Zero;
    }
```

3. Lights, throttle and translation use the keyboard channel. The throttle is a servo, not an assignment:
   press `MainEngineThrottleUp` while below target, `Down` while above, and report the real value:

```csharp
// src/KSArmory/Ksa/VehicleCommand.cs L209-215, L226-243
    public static void SetEngine(Vehicle craft, bool running)
    {
        if (!KsaWorld.IsAlive(craft)) return;

        craft.ProcessInput(running ? InputAction.MainEngineStartup : InputAction.MainEngineShutdown,
                           GlfwKeyAction.Press, default);
    }
// ...
    public static double DriveThrottle(Vehicle craft, double wanted, bool throughTheClear = false)
    {
        if (!KsaWorld.IsAlive(craft)) return 1.0;

        double have = craft.GetManualThrottle();
        double want = Math.Clamp(wanted, craft.GetMinThrottle(), 1.0);

        bool up = have < want - ThrottleTolerance;
        bool down = have > want + ThrottleTolerance;
        AttitudeHook.ThrottleKey(craft, throughTheClear ? (up ? 1 : down ? -1 : 0) : 0);

        craft.ProcessInput(InputAction.MainEngineThrottleUp,
                           up ? GlfwKeyAction.Press : GlfwKeyAction.Release, default);
        craft.ProcessInput(InputAction.MainEngineThrottleDown,
                           down ? GlfwKeyAction.Press : GlfwKeyAction.Release, default);

        return have;
    }
```

4. Pulse mode and staging, from the prefix window:

```csharp
// src/KSArmory/Ksa/VehicleCommand.cs L294-301, L307-311
    public static void SetPulseMode(Vehicle craft, bool pulsing)
    {
        if (!KsaWorld.IsAlive(craft)) return;

        craft.FlightComputer.SetManualThrustMode(pulsing
                                                     ? FlightComputerManualThrustMode.Pulse
                                                     : FlightComputerManualThrustMode.Direct);
    }
// ...
    public static void Stage(Vehicle craft)
    {
        if (!KsaWorld.IsAlive(craft)) return;
        craft.Parts.SequenceList.ActivateNextSequence(craft);
    }
```

5. Queue from decision code and replay in the prefix. The standing aim is a dictionary re-applied every
   frame; stage and pulse are one-shots:

```csharp
// src/KSArmory/Ksa/AttitudeHook.cs L165-186
    public static void Hold(Vehicle craft, double3 directionCci, double3 rollReferenceCci)
    {
        if (!KsaWorld.IsAlive(craft)) return;
        Wanted[craft] = new Aim(directionCci, rollReferenceCci);
    }

    /// <summary>
    /// Actively stop the flight computer tracking, every frame, until <see cref="Release"/>.
    ///
    /// <para>Not the same as <see cref="Release"/>. Dropping the standing aim only stops this mod
    /// <em>writing</em> a target; the computer keeps the one it has and goes on firing thrusters to
    /// hold it, which keeps <c>anyActuatorCommanded</c> set and the vehicle off rails. Measured:
    /// a craft with <c>aimed=False</c> still read <c>Auto/Custom</c> and still spent 276 of 376
    /// coast probes off rails, against the pointed arm's 282. Cancelling has to be a write, and it
    /// has to happen in this window like every other one.</para>
    /// </summary>
    public static void Quiet(Vehicle craft)
    {
        if (!KsaWorld.IsAlive(craft)) return;

        Wanted.Remove(craft);
        Quieted.Add(craft);
```

   The prefix that replays the queues is shown in article 5 of
   [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../change-vehicles-without-racing-the-solver/overview.md).
6. If the player might also be flying this craft, remember the held-key clear: when the vehicle is controlled
   and the clear is predicted, nudge the throttle value directly (`KsaWorld.TryNudgeThrottle`), as the prefix
   does.
7. Release everything on unload: clear the queues and call `ReleaseAttitude` for each vehicle so the autopilot
   stops steering after the mod unloads.

## Complete example

- [src/KSArmory/Ksa/VehicleCommand.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/VehicleCommand.cs)[^ksarmory-vehiclecommand] - the command surface
- [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs)[^ksarmory-attitudehook] - queues, install, prefix

## Variations and gotchas

- **Frame choice.** `EclBody` has zero frame rates, so a commanded inertial direction needs no feed-forward;
  a horizon frame would make the flight computer chase a rotation nobody asked for.
- **Automatic burns fight you.** `TryAim` sets `BurnMode = Manual`; an Auto burn would run its own throttle.
- **Deadband.** The profile is re-set to `Strict` because KSA only ever raises the pointing deadband when
  stages change, and nothing lowers it again.
- **Thrusters decide what translation does.** Directions that no nozzle answers to are ignored by the engine.
- **Updates.** The surface is `FlightComputer` public fields plus `ProcessInput`; a rename of either is a
  compile error, and the prefix is compile-pinned (article 5 of the race topic).

## See also

- [Overview](overview.md), [Approach 1](take-control-of-another-vehicle-1.md)
- [ksarmory/fly-another-vehicle](../../../../mods/ksarmory/features/fly-another-vehicle.md), [ksarmory/prepareworker-prefix-command-window](../../../../mods/ksarmory/features/prepareworker-prefix-command-window.md)
- [Switch engines on and off, set throttle and RCS commands, and choose which RCS jets fire](../../modules-and-resources/command-engines-throttle-and-rcs-from-code/overview.md), [Ignite engines, activate the next stage, fire a decoupler or undock from code](../../staging-and-engines/stage-a-vehicle-from-code/overview.md), [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../change-vehicles-without-racing-the-solver/overview.md)

[^ksarmory-attitudehook]: KSArmory: src/KSArmory/Ksa/AttitudeHook.cs
[^ksa-vehicleupdatedata]: current/decomp/KSA/VehicleUpdateData.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-vehiclecommand]: KSArmory: src/KSArmory/Ksa/VehicleCommand.cs
