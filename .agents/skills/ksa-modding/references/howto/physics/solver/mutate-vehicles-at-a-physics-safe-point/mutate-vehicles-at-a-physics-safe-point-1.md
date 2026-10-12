---
type: How-To
title: Write inside a Vehicle.PrepareWorker prefix
description: Queue a vehicle change from any hook and perform it in a Harmony prefix on Vehicle.PrepareWorker, the last main-thread moment before KSA snapshots that vehicle for the physics worker
category: physics
group: solver
topic: mutate-vehicles-at-a-physics-safe-point
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksacars/features/prepareworker-impulse-injection
  - mods/ksarmory/features/prepareworker-prefix-command-window
  - mods/ksastructures/features/lift-moving-landmark
techniques: [harmony-prefix, reflection]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker, starmap StarMapAllModsLoaded]
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.PhysicsStates, KSA.SimStep, KSA.FlightComputer, KSA.Universe]
tags: [physics, vehicle, prepareworker, harmony, prefix, flight-computer, command-queue]
---

Outcome: velocity, spin, position, flight-computer fields, tank contents or mass that you write to a
vehicle are integrated by the very next physics step instead of being reverted. Prerequisites: StarMap
(for `StarMapAllModsLoaded`) and Harmony. KSACars writes velocity here, KSArmory queues attitude,
throttle, staging and mass writes, and KSAStructures writes position after taking a body off rails.

## Background: how the game does it

`Universe.ExecuteNextVehicleSolvers` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2139) calls the virtual
`Vehicle.PrepareWorker(SimStep)` for every vehicle in `CurrentSystem.All` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2141-L2155)
and only then queues the vehicle job. The body of `PrepareWorker` ends with
`_threadWorkerUpdateState.PrepareFromVehicle(...)`, which copies the bubble origin, kinematic states, flight
computer and manual inputs into the worker's input:

```csharp
// current/decomp/KSA/Vehicle.cs L2396-L2401
	public virtual void PrepareWorker(SimStep simStep)
	{
		if (Program.ControlledVehicle == this && (!Program.IsControlledVehicleActive || ImGui.GetIO().WantCaptureKeyboard || Universe.GetSimulationSpeed() > 30.0))
		{
			ClearHeldPlayerInput();
		}
		float num = (float)Program.GetPlayerDeltaTime() * 0.7f;
```

A write from a StarMap hook happens after that snapshot. The worker never sees it, and the next
`ApplyVehicleSolvers` replaces `_kinematicStates` and copies the flight computer back over the live object
(`UpdateFromTaskResultsUnsynchronized` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2435-L2544)). KSArmory measured the flight-computer
case directly: attitude commands written from a hook read back `Manual/None` every frame.[^ksarmory-attitudehook]

## Lifecycle and timing

The prefix runs on the main thread inside `PrepareFrame`'s dispatch, after the apply and input steps and
before this vehicle's snapshot. This is the last settled point for that vehicle, as documented in the
[settled-window write-path table](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md#lifecycle-and-timing) and the
[per-frame sequence](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence) (row 6). Inside the prefix:

| Fact | Consequence |
|---|---|
| The vehicle holds the result of the step applied this frame | Read current state; write what the next step should start from |
| `simStep.DeltaTime` is the length of the step about to be integrated | Scale per-step changes by it; it is 0 while paused, because the solvers are dispatched every frame |
| The window is per vehicle | A vehicle earlier in `CurrentSystem.All` is already snapshotted when a later one's prefix runs; write only `__instance` |
| `ClearHeldPlayerInput` and the throttle ramp run after your prefix | Held keys are read before they are cleared; a throttle write is not clamped yet |
| `_manualControlInputs.EngineOn` is cleared while `BurnMode` is Auto | An engine command written here survives only in Manual |
| Cloth and the nearest-orbit job are already running | Write kinematic state and props; do not edit burn lists or part-tree derived data here |

Anything you decide in a UI or StarMap hook of frame N is therefore performed in frame N+1's `PrepareFrame`
and integrated by the step dispatched there.

## Steps

1. Install the prefix once, from `[StarMapAllModsLoaded]`. Look the method up by reflection so a missing
   method disables the feature instead of crashing. KSACars' installer:

```csharp
// src/KSACars/Ksa/PhysicsHook.cs L33-L60
    public static void Install()
    {
        if (Installed) return;

        try
        {
            MethodInfo? target = typeof(Vehicle).GetMethod(nameof(Vehicle.PrepareWorker), [typeof(SimStep)]);
            if (target is null)
            {
                Log.Warn("cars cannot drive: KSA has no Vehicle.PrepareWorker(SimStep) to hook");
                return;
            }

            MethodInfo prefix = typeof(PhysicsHook).GetMethod(
                nameof(BeforePrepareWorker), BindingFlags.NonPublic | BindingFlags.Static)!;

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));

            Installed = true;
            Log.Info("car physics hooked into Vehicle.PrepareWorker");
        }
        catch (Exception e)
        {
            Log.Error("could not hook car physics; no car can drive", e);
        }
    }
```

2. Make the prefix non-throwing and let a signature change break the build. The prefix receives
   `__instance` and the `SimStep`; the never-called `PinTheSignature` makes the compiler reference the
   patched method:

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

3. Queue from anywhere, perform in the prefix. KSArmory keeps static sets and dictionaries keyed by
   `Vehicle` (`Wanted`, `Quieted`, `Staging`, `Shoves`, ...). UI and weapon code only add to them; the
   prefix does the writes, starting each branch with a cheap `Count` test because it runs for every vehicle,
   kittens on EVA included:

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

4. Write flight-computer fields in the prefix, and restate standing commands every frame. A command that
   stays in force must be written every frame; releasing it is a write too (KSArmory writes `Manual/None`):

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

5. Write kinematics through `GetPhysicsStatesMutable`. For a craft on rails you must first rebuild the
   state from the analytic orbit and take it off rails, or the step integrates nothing. KSACars does this
   for a coasting car, and writes the velocity change in the physics frame:

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

6. For a position write on a body that may be asleep (on rails), KSAStructures wraps the whole recipe in one
   helper: rebuild from the orbit when railed, add velocity and position deltas, take it off rails, install
   a new flight plan from the edited state:

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

7. Expect the log to be quiet: both mods log a single error and then stop (`_complained`) rather than every
   frame, and a vehicle whose write throws is dropped from the active sets.

## Complete example

| Mod | Files |
|---|---|
| KSACars | [src/KSACars/Ksa/PhysicsHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/PhysicsHook.cs), [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L519-L714) |
| KSArmory | [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs#L36-L436), [src/KSArmory/Ksa/VehicleCommand.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/VehicleCommand.cs) |
| KSAStructures | [src/KSAStructures/Ksa/OfficeLift.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeLift.cs#L292-L316), [src/KSAStructures/Ksa/VehicleCommand.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/VehicleCommand.cs#L15-L31) |

## Variations and gotchas

- A prefix on `PrepareWorker` runs once per frame, not per substep. A force applied here is one velocity
  change per frame that the engine integrates through its own substeps. For per-substep forces patch inside
  the step instead (see [worker-thread-safe patches](../../solver/write-worker-thread-safe-solver-patches/overview.md)).
- A command takes effect one frame after the decision: decide in frame N's hook, performed in frame N+1.
- `PrepareWorker` is called for every vehicle including EVA kittens (`KittenEva` overrides it and calls
  `base.PrepareWorker`) [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L319-L325); filter at the first dictionary lookup.
- `Universe.ApplyVehicleSolvers` is public, but calling it from a hook to "apply" a write also advances the
  simulation clock; never do that.
- Reflection into `_manualControlInputs` (KSArmory's throttle nudge) turns into a silent no-op if the field
  is renamed; probe once and fall back.
- Burn lists race the nearest-orbit job queued earlier in `PrepareFrame`: add or remove burns only where the
  game does (see the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md#lifecycle-and-timing)).
- After a save load every `Vehicle` object is replaced; clear your queues (KSArmory's `Remove()` clears only
  `Wanted`, `Staging` and `Shoves`, so keep a full reset of your own).

## See also

- [Overview and comparison](overview.md)
- [Approach 2: ExecuteNextVehicleSolvers prefix](mutate-vehicles-at-a-physics-safe-point-2.md)
- [Approach 3: PrepareFrame handoff](mutate-vehicles-at-a-physics-safe-point-3.md)
- [Approach 4: join the vehicle solver](mutate-vehicles-at-a-physics-safe-point-4.md)
- Mod features: [KSACars impulse injection](../../../../mods/ksacars/features/prepareworker-impulse-injection.md), [KSArmory command window](../../../../mods/ksarmory/features/prepareworker-prefix-command-window.md), [KSAStructures lift](../../../../mods/ksastructures/features/lift-moving-landmark.md)
- [Inject forces and impulses at the physics safe point](../../solver/inject-forces-and-impulses-at-the-physics-safe-point/overview.md)

[^ksarmory-attitudehook]: KSArmory: src/KSArmory/Ksa/AttitudeHook.cs
