---
type: Mod Feature
title: Command vehicles from a Vehicle.PrepareWorker prefix
description: Every write KSArmory makes to another craft (attitude, pulse mode, throttle, staging, blast impulse, tanks, store mass) is queued and performed in a Harmony prefix on Vehicle.PrepareWorker, the only point where it survives KSA's double-buffered vehicle state
mod: ksarmory
category: patching
techniques: [harmony-prefix, reflection, command-queue, signature-pinning]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - reflection KSA.Vehicle._manualControlInputs
lifecycle: [startup:all-mods-loaded, frame:dispatch-solvers]
game_types:
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.VehicleUpdateState
  - KSA.VehicleUpdateData
  - KSA.Universe
  - KSA.SimStep
  - KSA.PhysicsStates
  - KSA.VehicleProperties
  - KSA.SequenceList
  - KSA.ManualControlInputs
  - KSA.KittenEva
  - KSA.PhysicsBubble
tags:
  - harmony
  - prefix
  - flight-computer
  - attitude
  - double-buffer
  - vehicle-solver
  - staging
  - impulse
  - frame-order
---

KSArmory flies other people's rockets: the ballistic computer points them, trims them in pulses,
works the throttle and fires stages, and a nearby blast shoves them. None of that can be written
from a StarMap hook. KSA copies each vehicle's flight computer and kinematic state into a worker
snapshot inside `PrepareFrame`, before any mod hook runs, and the next frame copies the worker's
result back over the live object. A write from `[StarMapAfterGui]` lands in neither and is gone a
frame later (measured by the author as `before Manual/None -> after Auto/Custom` every frame).[^ksarmory-blocked]

So every such write is queued in a static set or dictionary keyed by `Vehicle`, and performed by a
Harmony prefix on the public virtual `Vehicle.PrepareWorker(SimStep)`, which KSA calls on the main
thread for every vehicle immediately before taking that snapshot.[^ksarmory-attitudehook]

## How it works

Queue API (called from mod logic in the frame hooks) and what the prefix writes:

| Call | Held in | Written in the prefix |
|---|---|---|
| `Hold(craft, dirCci, rollRefCci)` | `Wanted` (standing) | `VehicleCommand.TryAim`: `FlightComputer.AttitudeFrame = EclBody`, `AttitudeTrackTarget = Custom`, `CustomAttitudeTarget` (Euler angles of the target in the `EclBody` frame), `AttitudeMode = Auto`, `SetAttitudeProfile(Strict)`, `BurnMode = Manual`; every frame |
| `Quiet(craft)` | `Quieted` (standing) | `ReleaseAttitude`: `AttitudeMode = Manual`, `AttitudeTrackTarget = None`, `CustomAttitudeTarget = 0`; every frame |
| `QuietOnRails(craft)` | `Quieted` + `Railed` | also `GetPhysicsStatesMutable().Props.SetOnRails(true)` |
| `PulseMode(craft, on)` | `Pulsing`, `Pulsed` | `FlightComputer.SetManualThrustMode(Pulse or Direct)` every frame while in `Pulsed`; one `Direct` after `Release` |
| `ThrottleKey(craft, dir)` | `ThrottleKeys` | only when KSA is discarding held keys on that craft: `EngineThrottle += dir x playerDt x 0.7` by reflection on `Vehicle._manualControlInputs`[^ksarmory-ksaworld] |
| `Stage(craft)` | `Staging` (one shot) | `craft.Parts.SequenceList.ActivateNextSequence(craft)` |
| `Shove(craft, J, L, wind, away)` | `Shoves` (summed, one shot) | `VehicleCommand.TryShove`: rebuild from the conic if on rails, add `dv`/`dw` to `Kinematic.VelocityPhys`/`AngularVelocityPhys`, `SetOnRails(false)`, `SetFlightPlan(new FlightPlan(states.ComputeOrbit(...)))`; a probe reads the result back two steps later |
| (other features) | `Leaks`, `StoreMass` owed dictionaries | tank contents and part inert mass, then `RecomputeMassProperties` and `FlightComputer.ReadUpdatedVehicleConfiguration`[^ksarmory-leaks][^ksarmory-storemass] |

`Release(craft)` clears the standing entries and owes the pulse-mode restore. The prefix order is
fixed: stage, throttle, leaks, store mass, shove, pulse mode, then quiet (returns) or aim. Most
branches start with a cheap `Count` or `Contains` test, because the prefix runs for every vehicle
in the system every frame. The prefix catches everything; on a throw it drops that craft from every set and logs once.[^ksarmory-vehiclecommand]

Installation, from `[StarMapAllModsLoaded]`: `typeof(Vehicle).GetMethod("PrepareWorker",
[typeof(SimStep)])`, a private Harmony id, and `Installed`/`Trouble` for the panel. `PinTheSignature`
is never called; it makes the compiler reference the patched method so a signature change in KSA is
a build error and the method appears in the mod's API-surface listing.

## Lifecycle and timing

The double buffer in build 2026.10.10.5554:[^ksa-program][^ksa-universe][^ksa-vehicle]

| Order | What | Where |
|---|---|---|
| 1 | Previous worker finished | `JobSystems.VehicleSolver.Wait()`, Program.cs L2158 |
| 2 | **Result copied over the live vehicle**: `_kinematicStates`, `_props` (when the worker sent new ones), orbit, `FlightComputer.CopyFrom(updateData.NewFlightComputer)`, parts | `ApplyVehicleSolvers` (L2167) -> `VehicleUpdateTask.ApplyResultsToMainThread` (VehicleUpdateTask.cs L226-L259) -> `Vehicle.UpdateFromTaskResultsUnsynchronized` (Vehicle.cs L2435-L2544; flight computer at L2530) |
| 3 | Keys and queued input events applied | `Glfw.PollEvents` L2179, `InputEvents.ApplyInputEvents` L2181 |
| 4 | Part trees flushed | `PartTree.FlushDirtyDerived` / `FlushDirtyResourceManagers`, L2218-L2219 |
| 5 | **Prefix runs**, once per vehicle | `ExecuteNextVehicleSolvers` (L2220) -> `PrepareVehicleWorkers` (Universe.cs L2141-L2155) -> `Vehicle.PrepareWorker` (Vehicle.cs L2396) |
| 6 | `PrepareWorker` body: held keys cleared on the controlled craft if inactive, ImGui wants the keyboard or speed > 30x; throttle ramp; `PrepareFromVehicle` | Vehicle.cs L2398-L2422 |
| 7 | **Snapshot**: bubble origin, `KinematicStates`, `NewFlightComputer.CopyFrom(flightComputer)`, manual inputs | `VehicleUpdateState.PrepareFromVehicle` (L296-L306) -> `VehicleUpdateData.Prepare` (L84-L104) |
| 8 | Worker queued; `_nextSimStep = simStep` | Universe.cs L2132-L2137 |
| 9 | StarMap GUI and frame hooks | Program.cs L2267-L2268 and after L2332 |

`FlightComputer.CopyFrom` copies `BurnMode`, `AttitudeMode`, `AttitudeFrame`, `AttitudeTrackTarget`,
`CustomAttitudeTarget`, `ManualThrustMode` and the controller state (FlightComputer.cs L181-L221), so
the copy is lossless; a write is lost only because it is on the wrong side of step 7.[^ksa-flightcomputer][^ksa-vehicleupdatestate][^ksa-vehicleupdatedata]

Inside the prefix the vehicle holds the step applied this frame, the worker is idle, this frame's
key presses are in, and `simStep` is the interval the worker is about to integrate. Writes to the
flight computer and to `Kinematic` are copied into the snapshot a few lines later and integrated by
this step. It runs inside KSA's frame loop, so it must not throw and must not touch ImGui or Vulkan.

Staging goes through the same window for a different reason: for the controlled craft, while the
`Sequence` gauge canvas (`ResourceGroups.IsOpen`) or the `EngineControl` gauge canvas is enabled
(both start enabled, so with the default UI this is every step), the vehicle-solver worker's
`RunVehiclePostWorkInner` calls `PerformanceSequences.RecomputeForFlight` (PhysicsBubble.cs
L793-L796), which walks the live `SequenceList` while a StarMap hook runs; `ActivateNextSequence`
from a hook races that walk.[^ksa-physicsbubble][^ksarmory-frame-order]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs#L36-L143) | Queues and `Install` |
| [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs#L164-L337) | `Hold`, `Quiet`, `QuietOnRails`, `PulseMode`, `ThrottleKey`, `Stage`, `Shove`, `CheckShoves`, `Release` |
| [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs#L339-L436) | `BeforePrepareWorker`, `PinTheSignature` |
| [src/KSArmory/Ksa/VehicleCommand.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/VehicleCommand.cs) | The writes: `TryAim`, `ReleaseAttitude`, `TryAssertRails`, `TryShove`, `SetPulseMode`, `Stage` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5275-L5338) | `TryNudgeThrottle` (reflection), `PlayerDeltaTime`, `DiscardsHeldControls` |

## Snippets

```csharp
// src/KSArmory/Ksa/AttitudeHook.cs L341-L379
    private static void BeforePrepareWorker(Vehicle __instance)
    {
        try
        {
            if (Staging.Count > 0 && Staging.Remove(__instance)) VehicleCommand.Stage(__instance);

            // The clear runs inside PrepareWorker after this and takes the held keys, never the throttle value, so the
            // step the key would have made is made here, at KSA's own 0.7 a second of the player's frame.
            if (ThrottleKeys.Count > 0 && ThrottleKeys.TryGetValue(__instance, out int key)
                && KsaWorld.DiscardsHeldControls(__instance, out _))
            {
                KsaWorld.TryNudgeThrottle(__instance, key * KsaWorld.PlayerDeltaTime * 0.7);
            }

            // Tank contents are written here for the same reason attitude is: anywhere else the
            // worker's copy goes over them.
            Leaks.Apply(__instance);

            // And a launcher's mass, lighter by what it has fired, for the same reason.
            StoreMass.Apply(__instance);

            if (Shoves.Count > 0 && Shoves.Remove(__instance, out (double3 Linear, double3 Angular, double Wind, double3 AwayEcl) shove))
            {
                // ...
            }
```

```csharp
// src/KSArmory/Ksa/AttitudeHook.cs L381-L414
            // Before everything else, and whatever else this craft is doing: the mode decides how a
            // translation command already standing is spent, and it is restated every frame.
            if (Pulsed.Contains(__instance))
            {
                VehicleCommand.SetPulseMode(__instance, Pulsing.Contains(__instance));
            }
            else if (Restore.Remove(__instance))
            {
                VehicleCommand.SetPulseMode(__instance, pulsing: false);
            }

            if (Quieted.Contains(__instance))
            {
                VehicleCommand.ReleaseAttitude(__instance);

                // ...
                if (Railed.Contains(__instance)) VehicleCommand.TryAssertRails(__instance);

                return;
            }

            if (Wanted.Count == 0) return;
            if (!Wanted.TryGetValue(__instance, out Aim aim)) return;

            VehicleCommand.TryAim(__instance, aim.DirectionCci, aim.RollReferenceCci);
        }
```

```csharp
// src/KSArmory/Ksa/VehicleCommand.cs L77-L96
        doubleQuat frame2Cci = VehicleReferenceFrameEx.GetEclBody2Cci(parent.GetCce2Cci());
        doubleQuat target2Frame = doubleQuat.Concatenate(target2Cci, frame2Cci.Inverse());

        FlightComputer computer = craft.FlightComputer;
        computer.AttitudeFrame = VehicleReferenceFrame.EclBody;
        computer.AttitudeTrackTarget = FlightComputerAttitudeTrackTarget.Custom;
        computer.CustomAttitudeTarget = VehicleReferenceFrame.EclBody.QuaternionToEulerAngles(target2Frame);
        computer.AttitudeMode = FlightComputerAttitudeMode.Auto;

        // ...
        computer.SetAttitudeProfile(FlightComputerAttitudeProfile.Strict);

        // An automatic burn would fight this for the attitude and run its own throttle.
        computer.BurnMode = FlightComputerBurnMode.Manual;
```

```csharp
// current/decomp/KSA/Vehicle.cs L2396-L2423
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
		if (FlightComputer.BurnMode == FlightComputerBurnMode.Auto)
		{
			_manualControlInputs.EngineOn = false;
		}
		Camera mainCamera = Program.GetMainCamera();
		double3 double5 = mainCamera.GetPositionEgo(this) + MassToGeometryAsmb.Transform(Asmb2Ego);
		float num2 = 2f * Props.ComputeBoundingSphereRadiusAsmb();
		bool flag = mainCamera.GetObjectDiameterWholePixels(num2, double5.Length()) > 5;
		bool flag2 = Universe.GetSimulationSpeed() <= 10.0;
		bool useHighFidelityOceanPhysics = flag && flag2;
		_threadWorkerUpdateState.PrepareFromVehicle(useHighFidelityOceanPhysics, _manualControlInputs);
	}
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Vehicle.PrepareWorker(SimStep)` (public virtual) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423) | Patched with the prefix |
| `Vehicle.UpdateFromTaskResultsUnsynchronized` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2435-L2544) | Why late writes are lost |
| `Vehicle.GetPhysicsStatesMutable`, `TotalMass`, `TotalMassPropsBody`, `HasPhysicsBubble`, `SetFlightPlan`, `ProcessInput`, `ClearHeldPlayerInput` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L922-L931) | Shove, rails, inputs |
| `FlightComputer.CopyFrom`, `AttitudeMode`, `AttitudeFrame`, `AttitudeTrackTarget`, `CustomAttitudeTarget`, `BurnMode`, `SetManualThrustMode`, `SetAttitudeProfile` | [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L181-L221) | The double-buffered fields the prefix writes |
| `VehicleUpdateData.Prepare` / `VehicleUpdateState.PrepareFromVehicle` | [current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84-L104) | The snapshot |
| `Universe.ExecuteNextVehicleSolvers`, `PrepareVehicleWorkers` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2155) | Caller of `PrepareWorker`, before the job is queued |
| `VehicleUpdateTask.ApplyResultsToMainThread` | [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L226-L259) | The copy-back, run in a parallel batch on vehicle worker threads[^ksa-vehicleupdatetask] |
| `KittenEva.PrepareWorker` (override) | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L319-L325) | Calls `base.PrepareWorker`, so the prefix also sees EVA kittens[^ksa-kitteneva] |

## Pitfalls and version notes

- **A command takes effect one frame after the decision.** The guidance decides in the StarMap hook
  of frame k; the prefix writes it in frame k+1's `PrepareFrame`. The author times cutoffs instead of
  waiting to observe them for this reason.
- **Standing state must be restated every frame.** Pulse mode is a field on the double-buffered
  computer: writing it only while pulsing leaves the last craft in `Pulse` for good (the `Pulsed`/`Restore` sets exist for that).
- **Releasing is a write too.** Dropping the aim leaves the computer tracking its last target and the
  craft off rails; `Quiet` writes `Manual/None` every frame.
- **Asserting rails is undone by any commanded actuator** on the same sub-step, so it is only done
  together with a released attitude.
- **`Remove()` clears only `Wanted`, `Staging` and `Shoves`**; the other sets keep their vehicles
  after an unload or fault.
- **The throttle nudge is reflection** on the private `_manualControlInputs` struct (boxed, modified,
  written back); a rename turns it into a silent no-op (`TryNudgeThrottle` returns false).
- **Docs vs 5554:** `docs/BLOCKED-ON-KSA.md` cites Program.cs 2165/2211/2259 and puts
  `[StarMapBeforeGui]` on `OnDrawUiViewports`; in 5554 the lines are L2167 (`ApplyVehicleSolvers`),
  L2220 (`ExecuteNextVehicleSolvers`), L2268 (`OnDrawUiViewports`, `AfterGui`) and `BeforeGui` is a
  prefix on `OnDrawUiFrame` (L2267). The mechanism it describes holds. KSA-FRAME-ORDER's "Nor may it
  stage one" holds; the gate is now `ResourceGroups.IsOpen || Program.EngineControlGaugeOpen`
  (PhysicsBubble.cs L793).

## See also

- [Blast shove impulse](./blast-shove-impulse.md)
- [Per-part store mass](./per-part-store-mass.md)
- [Holed tank leaks](./holed-tank-leaks.md)
- [Fly another vehicle](./fly-another-vehicle.md)
- [ICBM ballistic computer](./icbm-ballistic-computer.md)
- [MIRV bus trim and release](./mirv-bus-trim-and-release.md)
- [API surface tracking](./api-surface-tracking.md)
- [Consume the applied simulation step](./consume-applied-sim-step.md)
- [Command other vehicles through queued writes performed in a PrepareWorker prefix](../../../howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/drive-the-flight-computer-and-attitude-modes-from-a-mod-2.md) - how-to article demonstrating this feature
- [Mutate simulation state safely relative to the physics step](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) - how-to article demonstrating this feature
- [Write inside a Vehicle.PrepareWorker prefix](../../../howto/physics/solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-1.md) - how-to article demonstrating this feature
- [Stage inside the PrepareWorker prefix window so the sequence list is not mutated under the worker](../../../howto/vehicles-and-parts/staging-and-engines/stage-a-vehicle-from-code/stage-a-vehicle-from-code-2.md) - how-to article demonstrating this feature
- [Write flight-computer and kinematic fields in a Vehicle.PrepareWorker prefix](../../../howto/vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/change-vehicles-without-racing-the-solver-5.md) - how-to article demonstrating this feature
- [Fly another vehicle through its flight computer and input actions](../../../howto/vehicles-and-parts/vehicles/take-control-of-another-vehicle/take-control-of-another-vehicle-2.md) - how-to article demonstrating this feature

[^ksarmory-attitudehook]: src/KSArmory/Ksa/AttitudeHook.cs
[^ksarmory-vehiclecommand]: src/KSArmory/Ksa/VehicleCommand.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-storemass]: src/KSArmory/Ksa/StoreMass.cs
[^ksarmory-leaks]: src/KSArmory/Ksa/Leaks.cs
[^ksarmory-blocked]: docs/BLOCKED-ON-KSA.md
[^ksarmory-frame-order]: docs/KSA-FRAME-ORDER.md
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-universe]: KSA.Universe (decompiled)
[^ksa-program]: KSA.Program (decompiled)
[^ksa-vehicleupdatestate]: KSA.VehicleUpdateState (decompiled)
[^ksa-vehicleupdatedata]: KSA.VehicleUpdateData (decompiled)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled)
[^ksa-vehicleupdatetask]: KSA.VehicleUpdateTask (decompiled)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled)
[^ksa-kitteneva]: KSA.KittenEva (decompiled)
