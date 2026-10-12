---
type: How-To
title: Fly an EVA kitten as a vessel - IsKitten postfix, Ctrl2Body postfix, queued mode switches, input and HUD transpilers
description: Iron Man flips VehicleUpdateState.IsKitten for one chosen kitten, publishes membership to workers, queues mode switches to the PrepareFrame handoff, rotates Ctrl2Body, swaps the backpack RCS map and routes vessel keys and flight-computer gauges to the kitten
category: kittens
group: eva
topic: treat-an-eva-kitten-as-a-rocket
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony, transpiler]
demonstrated_by: [mods/unscience/features/iron-man-kitten-rocket-flight]
techniques: [harmony-postfix, harmony-transpiler, harmony-prefix]
hooks:
  - harmony-postfix KSA.VehicleUpdateState.PrepareFromVehicle
  - harmony-postfix KSA.Vehicle.get_Ctrl2Body
  - harmony-prefix KSA.KittenEva.OnKey
  - harmony-prefix KSA.KittenEva.ProcessInput
  - harmony-transpiler KSA.ThrusterController.RecomputeDynamicData
  - harmony-transpiler KSA.GaugeCanvas.IsContextVisible
  - harmony-transpiler KSA.GaugeButtonFlightComputer.IsDisabled
lifecycle:
  - frame:draw-ui
  - frame:prepare
  - frame:dispatch-solvers
  - physics:vehicle-solver
  - frame:input
  - frame:render
  - render:gauges
game_types:
  - KSA.KittenEva
  - KSA.Vehicle
  - KSA.VehicleUpdateState
  - KSA.PhysicsBubble
  - KSA.FlightComputer
  - KSA.ThrusterController
  - KSA.Rocket
  - KSA.EngineController
  - KSA.GaugeCanvas
  - KSA.GaugeButtonFlightComputer
  - KSA.InputEvents
tags: [kitten, eva, rocket, iron-man, postfix, transpiler, worker-thread]
---
After this article one kitten of your choice, with an engine, tanks and RCS fitted in the vehicle editor, flies like a rocket: throttle, attitude and staging keys act on it, the flight-computer HUD appears, the head is the nose and the backpack jets fire in the rocket sense. Walking, swimming and ladders come back when the player selects EVA mode. This is the Iron Man mode of unscience. The article covers the whole set of patches and, as sub-steps, the input and HUD routing. Prerequisites: StarMap, Harmony including transpilers ([write a transpiler](../../../patching/transpilers/write-a-harmony-transpiler/overview.md)), and the worker-thread rules ([write worker-thread-safe patches](../../../physics/solver/write-worker-thread-safe-solver-patches/overview.md)). Editing the kitten's tree is [another topic](../open-the-vehicle-editor-on-a-live-eva-kitten/overview.md).

## Background: how the game does it

See the table in the [overview](overview.md). In short: the worker branches on `VehicleUpdateState.IsKitten`; key handling goes through the `KittenEva` overrides of `OnKey` and `ProcessInput`; the control frame is `Vehicle.Ctrl2Body`; the stock backpack carries an authored `ManualControlMap`; the HUD decides contexts with `is KittenEva` checks; flight-computer buttons call the virtual `IsFlightComputerDisabled`. `OnKey` is reached from the GLFW key callback (`Program.OnKey` -> `DispatchKeyEvent` -> `ControlledVehicle.OnKey`, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1992-L2000)); `ProcessInput` from `InputEvents.VehicleInputData.Apply` in the input phase.

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Buttons queue actions | `ISubmod.RenderContent` (from `[StarMapAfterGui]`) | `frame:draw-ui` |
| `Enable` / `Disable` / arm / ignite run | the physics frame hook's dispatcher at the `PrepareFrame` handoff | `frame:prepare` |
| Derived data rebuilt | `PartTree.FlushDirtyDerived`, `FlushDirtyResourceManagers` | `frame:prepare` |
| `PrepareFromVehicle` postfix | `ExecuteNextVehicleSolvers` -> `Universe.PrepareVehicleWorkers` -> `Vehicle.PrepareWorker`, main thread | `frame:dispatch-solvers` |
| `IsKitten` branches, `Ctrl2Body` postfix, RCS transpiler | vehicle worker jobs (`PhysicsBubble`, `Rocket.UpdateThrusterCache`) | `physics:vehicle-solver` |
| `OnKey` prefix | GLFW key callback during `Glfw.PollEvents` | `frame:input` |
| `ProcessInput` prefix | `InputEvents.VehicleInputData.Apply` in `ApplyInputEvents` | `frame:input` |
| Gauge context (`IsContextVisible`) and click eligibility (`IsDisabled`, from the button hover and release handlers) | `GaugeCanvas.OnDrawUi` from `OnDrawUiThreadSafe` | `frame:draw-ui` |
| Button GPU state (`PackData` -> `IsFlightComputerDisabled`), refilled every frame | `GaugeCanvas.CanvasesRender` -> `CanvasRenderer.Render` -> `GaugeBundle.UpdateInstanceBuffer`, during command recording ([game loop reference, render sequence row 21](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence)) | `frame:render`, `render:gauges` |

The handoff matters: the previous step's results are applied, so stopping engines and changing flight-computer modes cannot be overwritten by a worker; the derived data is flushed before the snapshot so the first rocket step has the right mass and thrust; and `InvalidateCache` writes module global state that workers read, so it must be called only with the workers joined. `PrepareFromVehicle` runs on the main thread, before `JobSystems.VehicleSolver.ExecuteJobs()`, so writing the snapshot field there is race-free. The `Ctrl2Body` postfix and the RCS replacement, on the other hand, also run on worker threads, which is why they only read published membership and never mutate ([game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-physics-step)).

## Steps

1. **Key membership by the object and publish it to workers.** State is keyed by the actual `KittenEva` object, never by id, so a new save with the same vehicle ids starts in EVA mode. Because patches read it on physics workers, `PublishEnabled` copies the keys into an array with `Volatile.Write` and `IsEnabled` does an `Array.IndexOf` on a `Volatile.Read` snapshot; workers never touch the `Dictionary`:

```csharp
// iron-man.lib/IronManSubmod.cs L13-L27
    private readonly Dictionary<KittenEva, IronManEvaSettings> _enabled = new();
    private readonly HashSet<KittenEva> _configured = new();
    private KittenEva[] _enabledSnapshot = Array.Empty<KittenEva>();
    private bool _disposed;
    private bool _pending;
    private string _status = "EVA mode by default. Edit your equipment or choose iron man to fly.";

    public static IronManSubmod? Instance { get; private set; }
    public string Name => "Iron Man";
    public string Tooltip => "Opt an EVA kitten into vessel editing, attachment nodes and rocket flight.";
    // The control frame and RCS mapping are also read by physics workers. Publish membership
    // snapshots so pruning a disposed kitten never races a worker's Dictionary lookup.
    public bool IsEnabled(KittenEva kitten) => Array.IndexOf(Volatile.Read(ref _enabledSnapshot), kitten) >= 0;
    public bool IsConfigured(KittenEva kitten) => !_disposed && _configured.Contains(kitten);
    private void PublishEnabled() => Volatile.Write(ref _enabledSnapshot, _enabled.Keys.ToArray());
```

2. **Queue every mode switch to the handoff.** The UI buttons call `Queue`, which allows one pending action and enqueues it on the physics frame hook; the action re-checks that the kitten still exists:

```csharp
// iron-man.lib/IronManSubmod.cs L49-L69
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
```

3. **Enable: snapshot the EVA settings, stop engines, force manual modes, publish.** Refuse while the editor is open or the kitten is on a ladder; snapshot the flight-computer modes and control part before touching them, stop and disarm every engine controller, select manual attitude, manual burn and direct thrust, publish membership and invalidate the thruster cache:

```csharp
// iron-man.lib/IronManSubmod.cs L71-L96
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

    private static void CheckCanEnter(KittenEva kitten)
    {
        if (!IronManPatches.Ready)
            throw new InvalidOperationException("Iron Man integration is unavailable; see the game log.");
        if (Program.IsEditorOpen)
            throw new InvalidOperationException("Return to flight before changing modes.");
        if (kitten.LocomotionState.Mode == LocomotionMode.Ladder)
            throw new InvalidOperationException("Release the ladder before editing or entering Iron Man mode.");
    }
```

4. **Flip the worker flag in a postfix on `PrepareFromVehicle`.** The original just assigned `IsKitten = true`; the postfix sets it false for enabled kittens and remembers the touched states so `Remove` can put the flag back after joining the solver. All modules, mass, propellant use and thrust already come from the part tree; this flag alone selects away from the character servos, locomotion and the ground-mode RCS input filter:

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

5. **Make the head the nose.** A postfix on the getter `Vehicle.Ctrl2Body` returns a 90 degree rotation about Y (control +X nose maps to body -Z, the head) for an enabled kitten that has no explicit control part or docking port. `Ctrl2Body` feeds the navball, attitude control, body rates and the worker's flight-computer navigation, so "Up" now points the head away from the ground:

```csharp
// iron-man.lib/IronManControlFramePatches.cs L35-L46
    private static void FramePostfix(Vehicle __instance, ref doubleQuat __result)
    {
        // A deliberately selected control part/docking port retains its native orientation.
        // This one shared frame feeds navball, attitude control, body rates and RCS mapping.
        if (_applied && __instance is KittenEva kitten
            && kitten.ControlPart == null && kitten.ControlConnector == null
            && IronManSubmod.Instance?.IsEnabled(kitten) == true)
            __result = IronManOrientation.RocketCtrl2Body;
    }

    private static MethodInfo Target() => AccessTools.PropertyGetter(typeof(Vehicle), nameof(Vehicle.Ctrl2Body))
        ?? throw new MissingMethodException(typeof(Vehicle).FullName, "get_Ctrl2Body");
```

6. **Swap the backpack RCS map.** `ThrusterController.RecomputeDynamicData` uses the authored `ManualControlMap` (a nullable field) when set, else derives a map from geometry in the control frame. A transpiler replaces its single `ldfld ManualControlMap` with a call that returns null for the enabled kitten's root backpack, which selects the geometric map; the authored field is never written, so disable, copies and saves keep it. Throw unless exactly one match so a game change disables the feature instead of corrupting a method:

```csharp
// iron-man.lib/IronManRcsOrientationPatches.cs L47-L85
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

    internal static IEnumerable<CodeInstruction> ReplaceManualControlMap(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.Select(code => new CodeInstruction(code)).ToList();
        FieldInfo field = AccessTools.Field(typeof(ThrusterController), nameof(ThrusterController.ManualControlMap))
            ?? throw new MissingFieldException(typeof(ThrusterController).FullName, nameof(ThrusterController.ManualControlMap));
        MethodInfo replacement = AccessTools.DeclaredMethod(typeof(IronManRcsOrientationPatches), nameof(ReadManualControlMap))!;
        int replaced = 0;
        foreach (var code in codes)
        {
            if (code.opcode != OpCodes.Ldfld || !Equals(code.operand, field)) continue;
            // Receiver -> nullable map has the same stack effect as the original ldfld;
            // changing the existing instruction preserves branch labels and EH blocks.
            code.opcode = OpCodes.Call;
            code.operand = replacement;
            replaced++;
        }
        if (replaced != 1)
            throw new InvalidOperationException($"Iron Man expected one authored thruster-map read, found {replaced}.");
        return codes;
    }
```

   `Rocket` caches authority and recomputes only when bounding radius, centre of mass, mass, pressure, `Ctrl2Body` or propellant availability change; because an explicit control part can keep `Ctrl2Body` constant across a mode switch, `InvalidateCache` resets the thruster global state to `ThrusterControllerGlobalState.Zero` at the handoff:

```csharp
// iron-man.lib/IronManRcsOrientationPatches.cs L37-L45
    /// <summary>Call only at the joined physics handoff when activation membership changes.</summary>
    public static void InvalidateCache(KittenEva kitten)
    {
        var states = ModuleStateful<ThrusterController, ThrusterControllerState,
            ThrusterControllerGlobalState, EmptyStruct>.InitializeHotPathList(kitten.Parts.States);
        // Changing eligibility can change the mapping even when an explicit control part
        // keeps Ctrl2Body unchanged. Force the native authority cache to recalculate.
        states.GetMutableGlobalStateForInitialization() = ThrusterControllerGlobalState.Zero;
    }
```

7. **Route vessel input (sub-step).** `KittenEva.OnKey` and `ProcessInput` override the vessel handlers and intercept most actions. Prefixes on the overrides skip the original and call the base `Vehicle` implementation for an enabled kitten ([approach 2](treat-an-eva-kitten-as-a-rocket-2.md) shows how the base call is made):

```csharp
// iron-man.lib/IronManFlightPatches.cs L95-L123
    private static bool OnKeyPrefix(KittenEva __instance, GlfwKeyEvent keyEvent, ref bool __result)
    {
        if (!IsEnabled(__instance)) return true;
        try
        {
            __result = VehicleOnKey(__instance, keyEvent);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"iron-man: vessel key handling failed: {ex.Message}");
            // Do not run a second handler after a partially queued input action.
            __result = true;
        }
        return false;
    }

    private static bool ProcessInputPrefix(KittenEva __instance, InputAction action, GlfwKeyAction keyAction, GlfwModifier modifiers)
    {
        if (!IsEnabled(__instance)) return true;
        try
        {
            VehicleProcessInput(__instance, action, keyAction, modifiers);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"iron-man: vessel input processing failed: {ex.Message}");
        }
        return false;
    }
```

8. **Show the vessel flight-computer HUD (sub-step).** `Gauges.xml` shows `AutopilotSettings` in the `Vehicle` context and `KittenFlightControl` in the `EVA` context, and `GaugeCanvas.IsContextVisible` decides both with `controlledVehicle is KittenEva`. A transpiler turns exactly those two `isinst KittenEva` instructions into a call to `AsOrdinaryEva(Vehicle)`, which returns null for an enabled kitten; every other context predicate and the AND semantics stay stock. Button eligibility is `Program.ControlledVehicle.IsFlightComputerDisabled(_enumValue)` in both `IsDisabled` (click) and `PackData` (GPU button state), and `KittenEva` overrides it to disable every non-kitten action, so two transpilers replace the single `callvirt` in each with a static adapter whose enabled-kitten branch reaches the base ([approach 3](treat-an-eva-kitten-as-a-rocket-3.md)):

```csharp
// iron-man.lib/IronManFlightComputerPatches.cs L57-L102
    private static bool UsesVesselControls(Vehicle? vehicle) => _applied
        && vehicle is KittenEva kitten && IronManSubmod.Instance?.IsEnabled(kitten) == true;

    // These two substitutions change only the EVA/Vehicle predicates, leaving the canvas's
    // AND semantics, saved visibility, engine/target/burn/atmosphere/IVA conditions untouched.
    private static KittenEva? AsOrdinaryEva(Vehicle? vehicle) =>
        UsesVesselControls(vehicle) ? null : vehicle as KittenEva;

    private static bool IsButtonDisabled(Vehicle vehicle, Enum value) => UsesVesselControls(vehicle)
        ? _basePolicy!(vehicle, value)
        : vehicle.IsFlightComputerDisabled(value);

    internal static IEnumerable<CodeInstruction> ReplaceEvaContext(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.Select(code => new CodeInstruction(code)).ToList();
        int replaced = 0;
        foreach (var code in codes)
        {
            if (code.opcode != OpCodes.Isinst || !Equals(code.operand, typeof(KittenEva))) continue;
            code.opcode = OpCodes.Call;
            code.operand = OwnMethod(nameof(AsOrdinaryEva));
            replaced++;
        }
        if (replaced != 2)
            throw new InvalidOperationException($"Iron Man expected two EVA/Vehicle gauge context checks, found {replaced}.");
        return codes;
    }

    internal static IEnumerable<CodeInstruction> ReplaceButtonPolicy(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.Select(code => new CodeInstruction(code)).ToList();
        MethodInfo original = BasePolicyMethod();
        int replaced = 0;
        foreach (var code in codes)
        {
            if (!code.Calls(original)) continue;
            // The static adapter consumes the same receiver + Enum pair as callvirt.
            // Both click eligibility and packed GPU button state must use the same policy.
            code.opcode = OpCodes.Call;
            code.operand = OwnMethod(nameof(IsButtonDisabled));
            replaced++;
        }
        if (replaced != 1)
            throw new InvalidOperationException($"Iron Man expected one flight-computer button policy call, found {replaced}.");
        return codes;
    }
```

9. **Tear down safely.** `Dispose` waits on `JobSystems.VehicleSolver` and `ClothSolvers` before stopping engines and restoring settings; `IronManFlightPatches.Remove` waits again before restoring `IsKitten` on retained snapshots, because `PrepareFromVehicle` snapshots are read by physics jobs and must never be restored in flight.

10. **Check the result.** Enable on a kitten with equipment: the log prints `iron-man: rocket mode <id>`; keys fly it, the vessel gauges appear and RCS fires in the rocket sense. The saved mode is restored disarmed after a load.

## Complete example

- [iron-man.lib/IronManSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManSubmod.cs#L11-L174) - membership, queueing, enable/disable, teardown.
- [iron-man.lib/IronManFlightPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManFlightPatches.cs#L13-L143) - `IsKitten` postfix, input prefixes, reverse patches.
- [iron-man.lib/IronManFlightComputerPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManFlightComputerPatches.cs#L12-L125) - gauge context and button-policy transpilers.
- [iron-man.lib/IronManControlFramePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManControlFramePatches.cs#L10-L50) and [iron-man.lib/IronManOrientation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManOrientation.cs#L7-L12) - the headward control frame.
- [iron-man.lib/IronManRcsOrientationPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManRcsOrientationPatches.cs#L12-L93) - geometric RCS map and cache invalidation.
- Feature page: [Iron Man kitten rocket flight](../../../../mods/unscience/features/iron-man-kitten-rocket-flight.md).

## Variations and gotchas

- **Burn progress and solver telemetry are not rewound** when leaving the mode; the settings snapshot restores modes, targets, roll, deadband, rate limit, control part and connector.
- **Control part or port wins.** A deliberately selected control part keeps its native orientation; the headward frame applies only when none is set.
- **Workers read published snapshots only.** Never touch the `Dictionary` from a patch that can run on a worker.
- **Ladders and the editor refuse.** Enable and disable refuse on a ladder or when the editor is open; the editor path has its own topic.
- **Scene saves restore the mode disarmed** (feature page), so a load never arms an engine.
- **Transpilers throw on a mismatch** so a game update disables the feature loudly rather than silently mispatching ([validate patch targets](../../../patching/safety/validate-patch-targets-and-detect-game-update-breakage/overview.md)).

## See also

- [Fly an EVA kitten as an ordinary rocket vessel](overview.md), [approach 2: reverse patch](treat-an-eva-kitten-as-a-rocket-2.md), [approach 3: non-virtual call](treat-an-eva-kitten-as-a-rocket-3.md)
- [Kitten locomotion, modes and the servo](../kitten-locomotion-and-servo-reference/overview.md), [Open the vehicle editor on a live EVA kitten](../open-the-vehicle-editor-on-a-live-eva-kitten/overview.md)
- [Mutate vehicles at a physics-safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md)
