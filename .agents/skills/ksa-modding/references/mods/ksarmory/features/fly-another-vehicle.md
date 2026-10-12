---
type: Mod Feature
title: Fly any vehicle from a mod through its flight computer and input actions
description: Another craft is flown through the flight computer's Custom attitude target, keyboard actions sent with Vehicle.ProcessInput and its staging sequence, with attitude and staging written from the PrepareWorker prefix so the worker snapshot sees them, then handed back
mod: ksarmory
category: vehicles-and-parts
techniques: [harmony-prefix, input-injection, reflection, double-buffer-aware-writes]
hooks:
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - starmap StarMapAfterGui
  - reflection KSA.Vehicle._manualControlInputs
lifecycle: [frame:draw-ui, frame:dispatch-solvers]
game_types:
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.VehicleReferenceFrameEx
  - KSA.SequenceList
  - KSA.VehicleUpdateState
  - KSA.VehicleUpdateData
  - KSA.InputAction
  - KSA.PhysicsStates
tags: [flight-computer, attitude, throttle, staging, input, autopilot, guidance]
---

KSArmory's ballistic computer flies whole rockets the player built: it points them, lights and
throttles their engines, fires their translation thrusters, stages them and later hands them back.
All of it lives in one static class, `VehicleCommand`, and every write goes through a channel the
game already uses for itself: the flight computer's `Custom` attitude target (what the engine uses
to point a kitten's manoeuvring unit) and `Vehicle.ProcessInput`, the call the keyboard makes.[^vehicle-command]
The non-obvious part is *when* to write. A vehicle's flight computer is double-buffered across the
frame, so attitude, pulse mode and staging are queued and applied from a Harmony prefix on
`Vehicle.PrepareWorker`, the one moment the next physics step's snapshot is taken.[^attitude-hook]

## How it works

| Command | Implementation |
|---|---|
| Point along a direction | `TryAim(craft, directionCci, rollReferenceCci)` builds a rotation whose rows are forward, `across = roll x forward`, `third = forward x across` (the layout of the engine's own `VehicleReferenceFrameEx.GetTgt2Cci`), re-expresses it in the `EclBody` frame via `GetEclBody2Cci(parent.GetCce2Cci())`, and writes `AttitudeFrame = EclBody`, `AttitudeTrackTarget = Custom`, `CustomAttitudeTarget = EclBody.QuaternionToEulerAngles(...)`, `AttitudeMode = Auto`, `SetAttitudeProfile(Strict)` and `BurnMode = Manual`[^vehicle-command][^ksa-refframe][^ksa-flightcomputer] |
| Roll reference | Supplied by the mod (`Sim/AimFrame.cs`) and carried forward, because the engine's planet-direction reference reverses when the nose points straight up or down and spins a rising rocket[^vehicle-command][^aim-frame] |
| Let go | `ReleaseAttitude`: `AttitudeMode = Manual`, `AttitudeTrackTarget = None`, `CustomAttitudeTarget = 0`[^vehicle-command] |
| Engines on/off | `ProcessInput(InputAction.MainEngineStartup or MainEngineShutdown, GlfwKeyAction.Press, default)`[^vehicle-command][^ksa-inputaction] |
| Throttle | `DriveThrottle` compares `GetManualThrottle()` with the clamped target and holds `MainEngineThrottleUp`/`Down` pressed or released; `PrepareWorker` then moves the throttle by `0.7 * playerDeltaTime` per frame while a key is held, exactly as for the player[^vehicle-command][^ksa-vehicle] |
| Translation | `DriveTranslation` presses or releases the six `Translate*` actions per a `TrimAxes` mask; `SetPulseMode` switches `FlightComputer.SetManualThrustMode(Pulse or Direct)`[^vehicle-command] |
| Stage | `craft.Parts.SequenceList.ActivateNextSequence(craft)`[^vehicle-command][^ksa-sequencelist] |
| Coast on rails | `TryAssertRails`: `GetPhysicsStatesMutable().Props.SetOnRails(true)` after releasing the attitude, so a quiet bus is propagated as a conic instead of integrated[^vehicle-command] |

### The double buffer, and why a prefix

At `Universe.ApplyVehicleSolvers` the worker's result is copied over the live computer
(`FlightComputer.CopyFrom(updateData.NewFlightComputer)`, Vehicle.cs L2530); at
`ExecuteNextVehicleSolvers` each vehicle's `PrepareWorker` snapshots it into the next worker's
state (`VehicleUpdateState.PrepareFromVehicle` -> `UpdateData.Prepare` ->
`NewFlightComputer.CopyFrom(flightComputer)`, VehicleUpdateData.cs L100).[^ksa-vehicle][^ksa-updatestate][^ksa-updatedata]
Every StarMap hook runs after that snapshot, so a computer field written there is neither seen by
the step in flight nor kept: next frame's apply overwrites it. `AttitudeHook` therefore keeps a
per-craft queue (`Hold`, `Quiet`, `QuietOnRails`, `PulseMode`, `Stage`, `Release`) and replays it
in its prefix, immediately before the snapshot.[^attitude-hook][^blocked]

The keyboard channel (`ProcessInput`) needs no prefix: it sets persistent `_engineFlags` bits that
`PrepareWorker` reads on the following frame, so calling it from a hook already has the same
one-frame latency as a player's key press.[^ksa-vehicle]

### The keyboard clear

`PrepareWorker`'s first act on the **controlled** vehicle is `ClearHeldPlayerInput()` when the
vehicle is inactive, ImGui wants the keyboard, or simulation speed exceeds 30x.[^ksa-vehicle] A held
throttle key from a mod is then discarded too, and a prefix cannot avoid it because the clear runs
inside the body. When `KsaWorld.DiscardsHeldControls` predicts the clear, the prefix instead moves
the throttle value directly by the step the key would have made (`TryNudgeThrottle`, reflecting
`Vehicle._manualControlInputs` and its public `EngineThrottle` field, writing the boxed struct
back).[^attitude-hook][^ksaworld][^modding-notes]

## Lifecycle and timing

- **Decisions: `frame:draw-ui`.** The ballistic computer (`IcbmComputer`) decides what the rocket
  should do inside `StepSimulation`, normally from the `[StarMapAfterGui]` postfix on
  `Program.OnDrawUiViewports`; it calls `VehicleCommand.SetEngine`, `DriveThrottle` and
  `DriveTranslation` directly and queues attitude, pulse and staging on `AttitudeHook`.[^icbm-computer]
- **Delivery: `frame:dispatch-solvers` of the next frame.** In `Program.PrepareFrame`,
  `Universe.ExecuteNextVehicleSolvers` (Universe.cs L2126) calls `PrepareVehicleWorkers` (L2141),
  which calls `Vehicle.PrepareWorker(simStep)` (Vehicle.cs L2396) for every vehicle; the prefix
  runs there, after this frame's results were applied and before the worker is queued.[^ksa-universe][^ksa-vehicle]
- **Effect: the step after that.** The command is integrated by the worker queued in that
  `PrepareFrame` and its result is applied one frame later, so the guidance times its cutoff
  rather than waiting to observe it.[^vehicle-command]
- **Staging must also be in the window**, for a second reason: with the engine-control gauge or
  staging list open, KSA recomputes the controlled craft's stage performance off the main thread,
  and a sequence list rebuilt under it throws.[^attitude-hook]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/VehicleCommand.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/VehicleCommand.cs#L52-L110) | `TryAim`, `ReleaseAttitude` |
| [src/KSArmory/Ksa/VehicleCommand.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/VehicleCommand.cs#L215-L312) | Engines, throttle, translation, pulse mode, staging |
| [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs#L341-L420) | The prefix that replays queued commands |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5266-L5338) | `TryNudgeThrottle`, `DiscardsHeldControls` |
| [src/KSArmory/Ksa/IcbmComputer.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/IcbmComputer.cs#L965-L1130) | The caller: point, throttle, ignite, stage |

## Snippets

```csharp
// src/KSArmory/Ksa/VehicleCommand.cs L71-L99
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

```csharp
// current/decomp/KSA/Vehicle.cs L2396-L2411
	public virtual void PrepareWorker(SimStep simStep)
	{
		if (Program.ControlledVehicle == this && (!Program.IsControlledVehicleActive || ImGui.GetIO().WantCaptureKeyboard || Universe.GetSimulationSpeed() > 30.0))
		{
			ClearHeldPlayerInput();
		}
		float num = (float)Program.GetPlayerDeltaTime() * 0.7f;
		if (_engineFlags.IsSet(EngineFlags.ThrottleUp))
		{
			_manualControlInputs.EngineThrottle += num;
		}
		if (_engineFlags.IsSet(EngineFlags.ThrottleDown))
		{
			_manualControlInputs.EngineThrottle -= num;
		}
		_manualControlInputs.EngineThrottle = Math.Clamp(_manualControlInputs.EngineThrottle, GetMinThrottle(), 1f);
```

## Game APIs touched

| Member | Use |
|---|---|
| `Vehicle.PrepareWorker(SimStep)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396)) | Prefix target; snapshot point; held-input clear |
| `Vehicle.ProcessInput(InputAction, GlfwKeyAction, GlfwModifier)` (Vehicle.cs L3536) | Engine start/stop, throttle and translation keys |
| `Vehicle.GetManualThrottle()`, `GetMinThrottle()`, `Parts.SequenceList`, `GetPhysicsStatesMutable()` | Read throttle, clamp, stage, rails |
| `Vehicle._manualControlInputs` (private, Vehicle.cs L239) | Reflected to move the throttle when held keys would be cleared |
| `FlightComputer.AttitudeFrame`, `AttitudeTrackTarget`, `CustomAttitudeTarget`, `AttitudeMode`, `BurnMode`, `SetAttitudeProfile`, `SetManualThrustMode` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs)) | Attitude and thrust mode |
| `VehicleReferenceFrameEx.GetEclBody2Cci`, `QuaternionToEulerAngles` ([current/decomp/KSA/VehicleReferenceFrameEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleReferenceFrameEx.cs#L76)) | Expressing the target in the computer's frame |
| `SequenceList.ActivateNextSequence(Vehicle)` ([current/decomp/KSA/SequenceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequenceList.cs#L171)) | Staging |
| `VehicleUpdateData.Prepare` / `Vehicle.UpdateFromTaskResultsUnsynchronized` ([current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L100)) | The two `FlightComputer.CopyFrom` calls that make the double buffer |

## Pitfalls and version notes

- **A StarMap hook is too late for the flight computer.** Written from any hook, the attitude is
  overwritten before it is read; measured in flight as "before Manual/None -> after Auto/Custom"
  every frame.[^attitude-hook]
- **Held keys vanish on the controlled craft** while ImGui has the keyboard (any focused text field
  or modal, including KSA's update popup), when inactive, or above 30x.[^ksa-vehicle][^modding-notes]
- **Throttle cap.** Since 2026.10.7.5541 `FlightComputer.ComputeControl` caps a non-Auto throttle
  at `0.9 * MaxGLoad * g` (`GLOAD_MARGIN = 0.9f`, `SolveGLoadThrottleCap`, `IsThrottleGLoadLimited`);
  the computer reads back the throttle the vehicle reports rather than fighting it.[^modding-notes][^ksa-flightcomputer]
- **The pointing deadband only ratchets up** after staging onto finer thrusters; re-assigning the
  `Strict` profile resets it.[^vehicle-command]
- **Pulse mode persists.** It is a field on the same double-buffered computer, so it must be
  restated every frame and explicitly restored, or the player's own translation keys keep firing
  in millisecond taps.[^attitude-hook]
- `TryAim` resolves the craft's position inside the prefix because the caller's sample is a frame
  old by then.[^vehicle-command]

## See also

- [PrepareWorker prefix command window](./prepareworker-prefix-command-window.md)
- [ICBM ballistic computer](./icbm-ballistic-computer.md)
- [MIRV bus trim and release](./mirv-bus-trim-and-release.md)
- [Blast shove impulse](./blast-shove-impulse.md)
- [Timewarp policy during engagements](./timewarp-policy-during-engagements.md)
- [Command other vehicles through queued writes performed in a PrepareWorker prefix](../../../howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/drive-the-flight-computer-and-attitude-modes-from-a-mod-2.md) - how-to article demonstrating this feature
- [Drive throttle and translation through Vehicle.ProcessInput, re-applied from the PrepareWorker window](../../../howto/flight-and-orbits/autopilot/execute-burns-with-rcs-and-throttle-inputs/execute-burns-with-rcs-and-throttle-inputs-4.md) - how-to article demonstrating this feature
- [Fly a vehicle that is not the controlled one, then hand it back](../../../howto/flight-and-orbits/autopilot/run-an-autopilot-for-every-vehicle-each-step/run-an-autopilot-for-every-vehicle-each-step-3.md) - how-to article demonstrating this feature
- [Stage inside the PrepareWorker prefix window so the sequence list is not mutated under the worker](../../../howto/vehicles-and-parts/staging-and-engines/stage-a-vehicle-from-code/stage-a-vehicle-from-code-2.md) - how-to article demonstrating this feature
- [Fly another vehicle through its flight computer and input actions](../../../howto/vehicles-and-parts/vehicles/take-control-of-another-vehicle/take-control-of-another-vehicle-2.md) - how-to article demonstrating this feature

[^vehicle-command]: src/KSArmory/Ksa/VehicleCommand.cs
[^attitude-hook]: src/KSArmory/Ksa/AttitudeHook.cs
[^ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^icbm-computer]: src/KSArmory/Ksa/IcbmComputer.cs
[^aim-frame]: src/KSArmory/Sim/AimFrame.cs
[^modding-notes]: docs/KSA-MODDING-NOTES.md
[^blocked]: docs/BLOCKED-ON-KSA.md
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-updatestate]: KSA.VehicleUpdateState (decompiled, build 2026.10.10.5554)
[^ksa-updatedata]: KSA.VehicleUpdateData (decompiled, build 2026.10.10.5554)
[^ksa-refframe]: KSA.VehicleReferenceFrameEx (decompiled, build 2026.10.10.5554)
[^ksa-sequencelist]: KSA.SequenceList (decompiled, build 2026.10.10.5554)
[^ksa-inputaction]: KSA.InputAction (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
