---
type: Mod Feature
title: Fly one EVA kitten as an ordinary rocket vessel
description: Per-kitten Iron Man mode flips the worker IsKitten flag, routes input through reverse-patched base Vehicle handlers, enables native flight-computer gauges and re-points the control frame and backpack RCS headward at the physics handoff
mod: unscience
submod: iron-man
category: kittens
techniques: [harmony-postfix, harmony-prefix, harmony-transpiler, harmony-reverse-patch, dynamic-method]
hooks:
  - unscience PhysicsFrameHook.Enqueue (enable, disable, arm, ignite)
  - harmony-postfix KSA.VehicleUpdateState.PrepareFromVehicle
  - harmony-prefix KSA.KittenEva.OnKey
  - harmony-prefix KSA.KittenEva.ProcessInput
  - harmony-reverse-patch KSA.Vehicle.OnKey
  - harmony-reverse-patch KSA.Vehicle.ProcessInput
  - harmony-transpiler KSA.GaugeCanvas.IsContextVisible
  - harmony-transpiler KSA.GaugeButtonFlightComputer.IsDisabled
  - harmony-transpiler KSA.GaugeButtonFlightComputer.PackData
  - harmony-postfix KSA.Vehicle.get_Ctrl2Body
  - harmony-transpiler KSA.ThrusterController.RecomputeDynamicData
lifecycle: [frame:draw-ui, frame:prepare, frame:input, frame:dispatch-solvers, physics:vehicle-solver]
game_types:
  - KSA.KittenEva
  - KSA.Vehicle
  - KSA.VehicleUpdateState
  - KSA.PhysicsBubble
  - KSA.FlightComputer
  - KSA.GaugeCanvas
  - KSA.GaugeButtonFlightComputer
  - KSA.ThrusterController
  - KSA.ThrusterControllerGlobalState
  - KSA.Rocket
  - KSA.ModuleStateful
  - KSA.EngineController
  - KSA.Universe
  - KSA.InputEvents
tags: [eva, kitten, flight-computer, gauges, rcs, input, harmony, reverse-patch, transpiler, worker-thread]
---

Iron Man mode turns one chosen EVA kitten into an ordinary vessel for physics, input and the HUD,
so engines, tanks and RCS fitted in the editor actually fly it. Walking, swimming, ladders and the
MMU servo come back when the player selects **eva mode**. Nothing is added to the physics: stock
modules compute mass, propellant use, thrust, RCS and collisions; Iron Man only changes which branch
the game takes for this one kitten.[^scope-iron-man]

Mechanism in one line: a postfix on `VehicleUpdateState.PrepareFromVehicle` sets the worker
snapshot's `IsKitten` to false for enabled kittens; input, gauge, control-frame and RCS-map patches
make the rest of the game treat that kitten as a vessel, and every mode change is queued to the
`PhysicsFrameHook` handoff.

## How it works

**Membership** ([iron-man.lib/IronManSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManSubmod.cs#L13-L47)).
The submod keys state by the actual `KittenEva` object, never by id, so a new save with the same
vehicle ids starts in EVA mode. `_configured` gates editor support; `_enabled` maps an enabled
kitten to the EVA settings it had before entry. Because the control-frame and RCS patches are read on
physics workers, `PublishEnabled` copies the keys into an array published with `Volatile.Write`, and
`IsEnabled` does an `Array.IndexOf` on a `Volatile.Read` snapshot: workers never touch the
`Dictionary`. `Update` prunes kittens that were disposed or left the system.

**Mode switches.** The UI's **eva mode** / **iron man** buttons call `Queue`, which allows one
pending action at a time and enqueues it on `PhysicsFrameHook`; the action re-checks that the kitten
still exists. `Enable` refuses while the integration is not installed, while the editor is open or
while the kitten is on a ladder, then snapshots `IronManEvaSettings` (flight-computer modes, frame,
targets, roll, deadband, rate limit, `ControlPart`, `ControlConnector`, `KittenEva.ControlMode`),
stops and disarms every `EngineController`, selects manual attitude, manual burn and direct thrust,
publishes membership and invalidates the thruster authority cache. `Disable` stops engines,
restores the snapshot (through `Vehicle.SetControlPart`, which re-validates the part, and
`KittenEva.SetControlMode`) and republishes. Burn progress and solver telemetry are not rewound
([iron-man.lib/IronManFlightSettings.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManFlightSettings.cs)).
Arm, Ignite, RCS toggle, manual attitude and rate hold buttons are queued the same way
([iron-man.lib/IronManSubmod.Ui.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManSubmod.Ui.cs#L74-L108)).

**Solver routing.** Stock assigns `IsKitten = ReadOnlyVehicle is KittenEva` in
`PrepareFromVehicle`.[^ksa-vehicle-update-state] Worker code branches on that flag, not on the CLR
type: `PhysicsBubble.FlightComputerInputsFor` zeroes thruster commands for kittens outside MMU mode,
`AdvanceKittenLocomotion` runs the character controller, `DetectStructuralFailure` gives kittens a
2.5x G-load allowance, `PoseIntegratorCallbacks` lets the kitten servo own body rates, and
`PartFailure.Detect` skips kittens entirely.[^ksa-physics-bubble][^ksa-pose-integrator][^ksa-part-failure]
The postfix sets `IsKitten = false` for enabled kittens and tracks the touched states so `Remove` can
restore them after joining the vehicle solver.

**Input.** `KittenEva.OnKey` and `KittenEva.ProcessInput` override the vessel handlers and intercept
most actions.[^ksa-kitten-eva] Prefixes on the overrides call the base `Vehicle` implementations
instead. Calling `((Vehicle)kitten).OnKey(...)` would dispatch virtually back into the patched
override, so Iron Man installs Harmony reverse patches of `Vehicle.OnKey` and `Vehicle.ProcessInput`
onto two stub methods
([iron-man.lib/IronManFlightPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManFlightPatches.cs#L23-L39)):
the stubs get a copy of the base body and are called statically.

**Native flight-computer HUD**
([iron-man.lib/IronManFlightComputerPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManFlightComputerPatches.cs)).
`Gauges.xml` shows `AutopilotSettings` in the `Vehicle` context and `KittenFlightControl` in the
`EVA` context, and `GaugeCanvas.IsContextVisible` decides both with `controlledVehicle is
KittenEva`.[^ksa-gauges-xml][^ksa-gauge-canvas] A transpiler turns exactly those two `isinst
KittenEva` instructions into a call to `AsOrdinaryEva(Vehicle)`, which returns null for an enabled
kitten; every other context predicate and the AND semantics stay stock. Button eligibility is
`Program.ControlledVehicle.IsFlightComputerDisabled(_enumValue)` in both `IsDisabled` (click) and
`PackData` (GPU button state); `KittenEva` overrides it to disable every non-kitten action. Two
transpilers replace that single `callvirt Vehicle.IsFlightComputerDisabled<Enum>` in each method with
a static adapter. For enabled kittens the adapter calls a `DynamicMethod` that emits a plain `call`
(not `callvirt`) to the closed generic base method, so the stock vessel policy (target, burn and
engine restrictions) runs on the kitten.[^ksa-gauge-button-fc]

**Headward control frame.** A kitten's body +X is its chest; the head is -Z. A postfix on
`Vehicle.get_Ctrl2Body` returns `IronManOrientation.RocketCtrl2Body`, a 90 degree rotation about Y
that maps control +X (nose) to body -Z, when the kitten is enabled and has no explicit control part
or docking port
([iron-man.lib/IronManControlFramePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManControlFramePatches.cs#L35-L43)).
`Ctrl2Body` feeds the navball, attitude control, body rates and the worker's
`FlightComputerNavigation`, so "Up" now points the head away from the ground.[^ksa-vehicle][^plan-orientation]

**Backpack RCS.** The stock backpack thrusters carry authored control maps (`ManualControlMap`) for
EVA. `ThrusterController.RecomputeDynamicData` uses `ManualControlMap` when set and otherwise derives
a map from geometry in the current control frame.[^ksa-thruster-controller] A transpiler replaces its
single `ldfld ManualControlMap` with a call that returns null for the root backpack of an enabled
kitten (the authored field is never written, so disable, copies and saves keep it). `Rocket` caches
authority and only recomputes when bounding radius, CoM, mass, pressure, `Ctrl2Body` or propellant
availability change; because an explicit
control part can keep `Ctrl2Body` constant across a mode switch, `InvalidateCache` resets the
thruster global state to `ThrusterControllerGlobalState.Zero` at the handoff.[^ksa-rocket][^ksa-module-stateful]

## Lifecycle and timing

| Step | Where (build 5554) | Lifecycle |
|---|---|---|
| Buttons queue actions | `ISubmod.RenderContent` (unscience `StarMapAfterGui`) | `frame:draw-ui` |
| `Enable`/`Disable`/arm/ignite run | `PhysicsFrameHook` dispatcher at `Program.PrepareFrame` L2216 | `frame:prepare` |
| Derived data rebuilt | `PartTree.FlushDirtyDerived` L2218, `FlushDirtyResourceManagers` L2219 | `frame:prepare` |
| `PrepareFromVehicle` postfix | `ExecuteNextVehicleSolvers` (L2220) -> `Universe.PrepareVehicleWorkers` -> `Vehicle.PrepareWorker` (`Vehicle.cs` L2422), main thread | `frame:dispatch-solvers` |
| `IsKitten` branches, `Ctrl2Body` postfix, RCS transpiler | Vehicle worker jobs (`PhysicsBubble`, `Rocket.UpdateThrusterCache`) | `physics:vehicle-solver` |
| `OnKey` prefix | GLFW key callback `Program.OnKey` -> `DispatchKeyEvent` -> `controlledVehicle.OnKey` (`Program.cs` L1995) during `Glfw.PollEvents` | `frame:input` |
| `ProcessInput` prefix | `InputEvents.VehicleInputData.Apply` (`InputEvents.cs` L776) in `ApplyInputEvents` | `frame:input` |
| Gauge context and button policy | `GaugeCanvas.OnDrawUi` (L717-L719) from `OnDrawUiThreadSafe`; click queues `FlightComputerInputData` | `frame:draw-ui` |

The handoff matters for three reasons. The previous step's results are applied, so stopping engines
and changing flight-computer modes cannot be overwritten by a worker. `RecomputeAllDerivedData` only
marks data dirty (since 5482) and the flush two lines later rebuilds it before the snapshot, so the
first rocket step already has the right mass and thrust. And `InvalidateCache` writes module global
state that workers read; the comment on it says to call it only at the joined handoff.

`PrepareFromVehicle` runs on the main thread inside `ExecuteNextVehicleSolvers`, before
`JobSystems.VehicleSolver.ExecuteJobs()`, so writing the snapshot field there is race-free; the
worker reads it for the whole step. The `Ctrl2Body` postfix and the RCS replacement, on the other
hand, run on worker threads as well as the main thread (`PhysicsBubble` reads
`ReadOnlyVehicle.Ctrl2Body`), which is why they read the published membership array and never
mutate anything.

Teardown runs outside the handoff: `IronManSubmod.Dispose` waits on `JobSystems.VehicleSolver` and
`ClothSolvers` before stopping engines and restoring settings, and `IronManFlightPatches.Remove`
waits again before restoring `IsKitten` on retained snapshots.

## Key code

| Path | Role |
|---|---|
| [iron-man.lib/IronManSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManSubmod.cs) | Membership snapshots, queued enable/disable, engine stop, teardown |
| [iron-man.lib/IronManEvaSettings.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManEvaSettings.cs) | Pre-entry EVA control part/port/mode snapshot |
| [iron-man.lib/IronManFlightSettings.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManFlightSettings.cs) | Flight-computer settings snapshot without telemetry rollback |
| [iron-man.lib/IronManFlightPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManFlightPatches.cs) | `IsKitten` postfix, input prefixes, reverse patches |
| [iron-man.lib/IronManFlightComputerPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManFlightComputerPatches.cs) | Gauge context and button-policy transpilers, nonvirtual base adapter |
| [iron-man.lib/IronManControlFramePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManControlFramePatches.cs) | Headward `Ctrl2Body` |
| [iron-man.lib/IronManRcsOrientationPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManRcsOrientationPatches.cs) | Geometric RCS map for the enabled backpack, authority cache reset |
| [iron-man.lib/IronManOrientation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManOrientation.cs) | The single control-to-body quaternion |
| [iron-man.lib/IronManSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManSubmod.Saves.cs) | Scene-save participant: restores the saved mode disarmed |

## Snippets

Queue and enable; the snapshot is taken before engines are stopped and modes forced to manual:

```csharp
// iron-man.lib/IronManSubmod.cs L49-L86
    private void Queue(KittenEva kitten, Action action)
    {
        if (_pending) return;
        _pending = true;
        PhysicsFrameHook.Enqueue(() =>
        {
            _pending = false;
            if (_disposed) return;
            try
            {
                if (kitten.IsDisposed || !VehicleProvider.GetAllVehicles(includeDebris: true).Contains(kitten))
                    throw new InvalidOperationException("The selected kitten is no longer in the current system.");
                action();
            }
            catch (Exception ex)
            {
                _status = ex.Message;
                Console.WriteLine($"iron-man: {ex}");
            }
        });
    }

    private void Enable(KittenEva kitten)
    {
        if (IsEnabled(kitten)) return;
        CheckCanEnter(kitten);
        Configure(kitten);
        var original = new IronManEvaSettings(kitten);
        StopEngines(kitten);
        kitten.FlightComputer.AttitudeMode = FlightComputerAttitudeMode.Manual;
        kitten.FlightComputer.BurnMode = FlightComputerBurnMode.Manual;
        kitten.FlightComputer.SetManualThrustMode(FlightComputerManualThrustMode.Direct);
        _enabled.Add(kitten, original);
        PublishEnabled();
        IronManRcsOrientationPatches.InvalidateCache(kitten);
        _status = "Iron Man mode. Rocket controls are manual; arm and ignite when ready.";
        Console.WriteLine($"iron-man: rocket mode {kitten.Id}");
    }
```

The one flag that selects vessel physics for this kitten's next step:

```csharp
// iron-man.lib/IronManFlightPatches.cs L70-L93
    private static void PreparePostfix(VehicleUpdateState __instance)
    {
        if (__instance.ReadOnlyVehicle is not KittenEva kitten) return;
        try
        {
            if (IsEnabled(kitten))
            {
                // All modules, mass, propellant consumption and thrust already use Vehicle's
                // part tree. This flag alone selects away from the character servos, locomotion
                // and the ground-mode RCS input filter, retaining ordinary vessel physics.
                __instance.IsKitten = false;
                OwnedStates.Add(__instance);
            }
            else
            {
                // The original just restored IsKitten=true. Stop retaining the worker state.
                OwnedStates.Remove(__instance);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"iron-man: flight preparation failed: {ex.Message}");
        }
    }
```

A nonvirtual call to the base flight-computer policy, used from the button transpilers:

```csharp
// iron-man.lib/IronManFlightComputerPatches.cs L26-L67
            var method = new DynamicMethod("IronManVesselFlightComputerPolicy", typeof(bool),
                new[] { typeof(Vehicle), typeof(Enum) }, typeof(IronManFlightComputerPatches), true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, BasePolicyMethod());
            il.Emit(OpCodes.Ret);
            _basePolicy = method.CreateDelegate<Func<Vehicle, Enum, bool>>();
// ...
    private static bool UsesVesselControls(Vehicle? vehicle) => _applied
        && vehicle is KittenEva kitten && IronManSubmod.Instance?.IsEnabled(kitten) == true;

    // These two substitutions change only the EVA/Vehicle predicates, leaving the canvas's
    // AND semantics, saved visibility, engine/target/burn/atmosphere/IVA conditions untouched.
    private static KittenEva? AsOrdinaryEva(Vehicle? vehicle) =>
        UsesVesselControls(vehicle) ? null : vehicle as KittenEva;

    private static bool IsButtonDisabled(Vehicle vehicle, Enum value) => UsesVesselControls(vehicle)
        ? _basePolicy!(vehicle, value)
        : vehicle.IsFlightComputerDisabled(value);
```

Worker-safe RCS map override and the authority cache reset done at the handoff:

```csharp
// iron-man.lib/IronManRcsOrientationPatches.cs L38-L64
    public static void InvalidateCache(KittenEva kitten)
    {
        var states = ModuleStateful<ThrusterController, ThrusterControllerState,
            ThrusterControllerGlobalState, EmptyStruct>.InitializeHotPathList(kitten.Parts.States);
        // Changing eligibility can change the mapping even when an explicit control part
        // keeps Ctrl2Body unchanged. Force the native authority cache to recalculate.
        states.GetMutableGlobalStateForInitialization() = ThrusterControllerGlobalState.Zero;
    }

    private static ThrusterMapFlags? ReadManualControlMap(ThrusterController controller)
    {
        if (_applied)
        {
            var root = controller.Parent.FullPart;
            if (root.Template.Id == IronManConnectors.BackpackTemplateId
                && root.Tree?.OwningVehicle is KittenEva kitten
                && ReferenceEquals(kitten.Parts.Root, root)
                && IronManSubmod.Instance?.IsEnabled(kitten) == true)
            {
                // Null selects native geometry-based mapping. Do not mutate the module:
                // this runs on physics workers and the authored map must survive disable,
                // editor copies, serialization and unload unchanged.
                return null;
            }
        }
        return controller.ManualControlMap;
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `VehicleUpdateState.IsKitten`, `PrepareFromVehicle` | [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L296-L305) | Worker snapshot classification (field at L98) |
| `Vehicle.PrepareWorker`, `Universe.PrepareVehicleWorkers` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423) | Main-thread snapshot call chain ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2155)) |
| `PhysicsBubble.FlightComputerInputsFor`, `AdvanceKittenLocomotion`, `DetectStructuralFailure` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1822-L1830) | `IsKitten` branches (also L999-L1008, L1969-L1974) |
| `PoseIntegratorCallbacks` kitten servo branch | [current/decomp/KSA/PoseIntegratorCallbacks.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PoseIntegratorCallbacks.cs#L84) | Servo owns rates only for kittens |
| `PartFailure.Detect` | [current/decomp/KSA/PartFailure.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartFailure.cs#L45-L49) | Kittens skip structural part failure |
| `Vehicle.OnKey`, `Vehicle.ProcessInput` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3274) | Reverse-patched base handlers (L3274, L3536) |
| `KittenEva.OnKey`, `ProcessInput`, `IsFlightComputerDisabled<T>` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L110) | EVA overrides (L110, L187, L281) |
| `Vehicle.IsFlightComputerDisabled<T>` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6860) | Base policy, closed over `Enum` |
| `GaugeCanvas.IsContextVisible` | [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L238-L265) | Two `is KittenEva` context checks |
| `GaugeButtonFlightComputer.OnReleased`, `IsDisabled`, `PackData` | [current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L130-L163) | Click eligibility and packed button state |
| `Vehicle.Ctrl2Body`, `ControlPart`, `ControlConnector`, `SetControlPart` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L581-L587) | Control frame and its restore (`SetControlPart` L2205) |
| `KittenEva.ControlMode`, `SetControlMode`, `LocomotionState` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L51-L67) | View/Direct preference, ladder check (`SetControlMode` L209) |
| `ThrusterController.ManualControlMap`, `RecomputeDynamicData` | [current/decomp/KSA/ThrusterController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ThrusterController.cs#L84-L135) | Authored vs geometric RCS map (field L23) |
| `Rocket.UpdateThrusterCache` | [current/decomp/KSA/Rocket.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Rocket.cs#L192-L208) | Cache keyed on `Ctrl2Body`; calls `RecomputeDynamicData` |
| `ModuleStateful<...>.InitializeHotPathList`, `GetMutableGlobalStateForInitialization` | [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L852) | Reset thruster global state (L473) |
| `Program.DispatchKeyEvent` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1756) | Key callback path to `controlledVehicle.OnKey` (L1995) |
| `InputEvents.VehicleInputData.Apply` | [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L764-L777) | Deferred `ProcessInput` dispatch |
| `AutopilotSettings` / `KittenFlightControl` canvases | [current/Content/Core/Gauges.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Gauges.xml#L1428-L1429) | `Vehicle` vs `EVA` visibility (EVA canvas at L1097-L1098) |

## Pitfalls and version notes

- Every transpiler counts its target: two `isinst KittenEva` in `IsContextVisible`, one
  `IsFlightComputerDisabled<Enum>` call in each button method, one `ldfld ManualControlMap`. A
  different count throws during `Apply`, `IronManPatches.Apply` removes everything, and the panel
  reports the integration unavailable. All counts hold in 5554.
- The nonvirtual adapter targets the closed generic `Vehicle.IsFlightComputerDisabled<Enum>`. The
  authors found that another mod's postfix on that generic entry point can be bypassed by the emitted
  nonvirtual call, so composition with such patches is unsupported.[^iron-man-flight-tests-readme][^plan-flight-computer]
- The reverse patches copy the base bodies at install time. A later mod patching `Vehicle.OnKey` is
  not seen by the copy; KSA 5449 changed those bodies to `Input.Contains/Matches`, which the reverse
  patch picks up automatically on the next install.[^scope-iron-man]
- With `IsKitten` false the kitten gets normal vessel G-load limits (no 2.5x), structural failure,
  contact and sleep rules. `Vehicle.Dispose(bool)` still uses a type test (`this is KittenEva`) to
  skip waking vessels resting on it.[^ksa-vehicle]
- Equipment is rigid on the body, not on animated foot bones; boots drift visually from walking feet.
- Mode is session state; native saves never carry it. The scene-save participant restores the saved
  mode with engines disarmed and refuses a saved control part that no longer belongs to the
  kitten.[^scope-iron-man]
- Physics-debug **Teleport To** would place an enabled kitten as a +X-up rocket; see
  [surface teleport correction](./iron-man-surface-teleport-correction.md).

## See also

- [Physics frame hook](./lib-physics-frame-hook.md), which runs every queued switch
- [Iron Man kitten vehicle editing](./iron-man-kitten-vehicle-editing.md)
- [Iron Man equipment render ordering](./iron-man-equipment-render-ordering.md)
- [Iron Man surface teleport correction](./iron-man-surface-teleport-correction.md)
- [Iron Man connector serialization](./iron-man-connector-serialization.md)
- [Iron Man sub-mod](../submods/iron-man.md) and [mod overview](../overview.md)
- [Find the kitten branches in the worker and patch them from outside the solver](../../../howto/kittens/eva/kitten-locomotion-and-servo-reference/kitten-locomotion-and-servo-reference-1.md) - how-to article demonstrating this feature
- [Fly an EVA kitten as a vessel - IsKitten postfix, Ctrl2Body postfix, queued mode switches, input and HUD transpilers](../../../howto/kittens/eva/treat-an-eva-kitten-as-a-rocket/treat-an-eva-kitten-as-a-rocket-1.md) - how-to article demonstrating this feature
- [Call the base Vehicle handlers through a Harmony reverse patch onto a static stub](../../../howto/kittens/eva/treat-an-eva-kitten-as-a-rocket/treat-an-eva-kitten-as-a-rocket-2.md) - how-to article demonstrating this feature
- [Call the base through a DynamicMethod non-virtual call so other mods' patches still run](../../../howto/kittens/eva/treat-an-eva-kitten-as-a-rocket/treat-an-eva-kitten-as-a-rocket-3.md) - how-to article demonstrating this feature

[^scope-iron-man]: scope/iron-man.md (authors' hook inventory, build 5482)
[^ksa-vehicle-update-state]: KSA.VehicleUpdateState (decompiled, build 2026.10.10.5554)
[^ksa-physics-bubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-pose-integrator]: KSA.PoseIntegratorCallbacks (decompiled, build 2026.10.10.5554)
[^ksa-part-failure]: KSA.PartFailure (decompiled, build 2026.10.10.5554)
[^ksa-kitten-eva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
[^ksa-gauges-xml]: Content/Core/Gauges.xml (build 2026.10.10.5554)
[^ksa-gauge-canvas]: KSA.GaugeCanvas (decompiled, build 2026.10.10.5554)
[^ksa-gauge-button-fc]: KSA.GaugeButtonFlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^plan-orientation]: plans/iron-man/ORIENTATION.md (authors' orientation notes)
[^ksa-thruster-controller]: KSA.ThrusterController (decompiled, build 2026.10.10.5554)
[^ksa-rocket]: KSA.Rocket (decompiled, build 2026.10.10.5554)
[^ksa-module-stateful]: KSA.ModuleStateful (decompiled, build 2026.10.10.5554)
[^iron-man-flight-tests-readme]: iron-man-flight.tests/README.md (managed Harmony checks)
[^plan-flight-computer]: plans/iron-man/FLIGHT_COMPUTER.md (authors' HUD investigation)
