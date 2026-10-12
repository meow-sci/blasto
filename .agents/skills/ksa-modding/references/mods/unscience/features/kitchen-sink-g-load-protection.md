---
type: Mod Feature
title: Kitchen Sink G-load protection for exact vessel instances
description: Kitchen Sink exempts selected vessels from KSA's whole-vehicle G-load destruction only, with a guarded transpiler that filters the one StructuralLoad.GLoadFraction read in PhysicsBubble.DetectStructuralFailure via a concurrent registry
mod: unscience
submod: kitchen-sink
category: physics
techniques: [harmony-transpiler, reflection, save-sidecar, imgui-window]
hooks:
  - harmony-transpiler KSA.PhysicsBubble.DetectStructuralFailure (private static, by name, exact signature)
  - starmap StarMapBeforeGui (via unscience ISubmod.Update, registry pruning)
  - event:save kitchen-sink-g-load scene record (capture, reset, replay)
lifecycle: [physics:vehicle-solver, frame:draw-ui, event:save, event:load]
game_types:
  - KSA.PhysicsBubble
  - KSA.StructuralLoad
  - KSA.VehicleUpdateState
  - KSA.VehicleDestructionEvent
  - KSA.VehicleStructuralLimits
  - KSA.Vehicle
  - KSA.LoadAlert
  - KSA.BubbleEndStepJob
tags: [damage, g-load, structural-failure, transpiler, worker-thread, concurrency, scene-saves, harmony]
---

Kitchen Sink's **G-load Invincibility** panel lets the player pick individual vessels and exempt them
from one specific kill: KSA's whole-vehicle G-load verdict. Everything else stays stock. Bepu still
collides the vessels, every part still breaks at its own crash tolerance, dynamic pressure in air or
water still destroys them, and the HUD load readout still shows the real G.[^kitchen-sink-readme]
The authors built it for multi-vessel machines (an axle riding in a cradle) that were destroyed by the
G spike of their own contacts even though every part was strong enough.[^destruction-investigation]

Mechanism in one line: a Harmony transpiler on the private static
`PhysicsBubble.DetectStructuralFailure(VehicleUpdateState)` finds the single call to the
`StructuralLoad.GLoadFraction` getter and inserts a filter that returns `0.0` for registered vessels,
so only the destruction comparison sees a changed value.

## How it works

**Where the verdict is made.** At the end of each bubble step the vehicle worker runs
`PartFailure.Detect` and then `DetectStructuralFailure` for every vessel in the bubble. The detector
first writes the step's telemetry into `UpdateData.NewStructuralLoad` (peak G, size-dependent G
limit, peak dynamic pressure, the 200 kPa limit), then evaluates
`bool flag = newStructuralLoad.GLoadFraction >= 1.0;` and a separate
`DynamicPressureFraction >= 1.0`, and finally creates a `VehicleDestructionEvent` whose cause is
derived from both flags and the contact situation.[^ksa-physics-bubble] The G limit is
`VehicleStructuralLimits.EffectiveMaxGLoad(radius)` (5 to 50 g by bounding-sphere radius), times 2.5
for kittens; part crash tolerance plays no role in it.[^ksa-vehicle-structural-limits]

**The rewrite** ([kitchen-sink.lib/GLoadProtectionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/GLoadProtectionPatches.cs#L42-L69)).
The transpiler scans the method body for instructions that call the typed getter
`AccessTools.PropertyGetter(typeof(StructuralLoad), nameof(StructuralLoad.GLoadFraction))`. It
requires exactly one match and throws on zero or several, then inserts two instructions right after
the getter call:

| IL after rewrite | Stack effect |
|---|---|
| `call StructuralLoad::get_GLoadFraction` (original) | pushes the real fraction |
| `ldarg.0` (inserted) | pushes `vehicleState` |
| `call FilterGLoadFraction(double, VehicleUpdateState)` (inserted) | pops both, pushes the real fraction or `0.0` |
| `ldc.r8 1.0` / compare (original) | the `>= 1.0` test now sees the filtered value |

Because the getter is only read after the telemetry fields are stored, the saved
`NewStructuralLoad` keeps the real peak and limit; `FilterGLoadFraction` only changes the value on the
evaluation stack. With the G flag forced false the native pressure branch and the cause
classification still run: a protected vessel over 200 kPa still dies as `GroundImpact`,
`HydrodynamicForces` or `AerodynamicForces`.[^kitchen-sink-tests-program]

**Installation** ([kitchen-sink.lib/GLoadProtectionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/GLoadProtectionPatches.cs#L19-L40)).
The private target is resolved by name with the exact parameter list `(VehicleUpdateState)` and
checked to be static and `void` before patching. `Remove` clears the registry and unpatches only this
transpiler. Both hosts install it: the shipped supermod in
[unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L89) through
`TryApply`, so a failure is logged and other features keep loading, and the standalone development
host in [kitchen-sink/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink/Patcher.cs#L21).
`IsApplied` gates the UI's Add button and the save replay.

**Registry** ([kitchen-sink.lib/GLoadProtection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/GLoadProtection.cs)).
A static `ConcurrentDictionary<Vehicle, byte>` built with `ReferenceEqualityComparer.Instance`, so
membership is by object identity, never by the `Vehicle.Id` string: a new vessel that happens to
reuse a protected name is not protected. `Contains` also rejects disposed vessels, so protection ends
the moment a vessel is disposed even before pruning. `Prune` drops entries that are disposed or no
longer in the current system's vehicle list (debris included).

**UI** ([kitchen-sink.lib/KitchenSinkSubmod.GLoadProtection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/KitchenSinkSubmod.GLoadProtection.cs)).
A filterable combo of `VehicleProvider.GetAllVehicles()` (debris excluded from the picker), an
**Add G-load Invincible** button disabled while the patch is missing or the vessel is already listed,
and a table of protected vessels with per-row **Delete**. The selection is held as an object
reference and dropped when the vessel leaves the list.

**Scene save record** ([kitchen-sink.lib/KitchenSinkSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/KitchenSinkSubmod.Saves.cs#L24-L65)).
`kitchen-sink-g-load` is a version-1 `SaveParticipant<string[]>` with restore order 20, alongside
Kitchen Sink's older boolean records:

| Phase | What it does |
|---|---|
| Capture | Snapshot live entries; every vessel must round-trip through `VehicleProvider.FindVehicle(vehicle.Id)` to the same object, otherwise the capture throws rather than write an id that cannot be restored uniquely; ids are sorted ordinally |
| Validate | At most 10,000 ids, none blank, no duplicates |
| Reset | Clear the registry and the picker before the native world is replaced |
| Replay | Require the patch when the list is non-empty; resolve each id with `FindVehicle` (which returns null on zero or several matches) and `Add` the reconstructed object; missing, disposed or ambiguous ids produce warnings |

Vanilla saves and older saves without the record restore with no protection. The coordinator keeps a
record whose replay failed so it is not lost on the next save.[^scope-saves]

## Lifecycle and timing

| Code | Thread | When |
|---|---|---|
| `FilterGLoadFraction` (inside the transpiled detector) | Vehicle worker (`JobSystems.VehicleWorkerPool`) | `physics:vehicle-solver`: `VehicleUpdateTask` dispatches `BubbleEndStepJob` per bubble in a `ParallelBatch`, which calls `PhysicsBubble.EndStepFrame` -> `FullPhysicsEndFrame` -> `DetectStructuralFailure`[^ksa-vehicle-update-task] |
| Event application (prevented) | Main thread, next frame | `Universe.ApplyVehicleSolvers` -> `PhysicsBubble.ApplyRenderEventsToVehicles` reads `DestructionEvent` |
| Telemetry publication | Main thread, next frame | `Vehicle.UpdateFromTaskResultsUnsynchronized` copies `NewStructuralLoad` into the vessel; `LoadAlert` draws it[^ksa-vehicle] |
| `GLoadProtection.Prune` | Main thread | `KitchenSinkSubmod.Update`, called from `[StarMapBeforeGui]` (`frame:draw-ui`) or the hidden-HUD replay, while the next step already runs on workers |
| Add / Delete | Main thread | ImGui panel (`frame:draw-ui`) |
| Save capture / reset / replay | Main thread | Native save transaction; reset and replay at the deferred world-change handoff in `Program.PrepareFrame`, before the next solvers are dispatched ([native save hooks](./lib-native-save-hooks.md)) |

Why the worker-side call site: the destruction decision is made on the worker and applied on the main
thread a frame later. Changing the comparison input on the worker means no event is ever created, so
there is nothing to race against on the main thread and no flag to restore.

Why a concurrent registry: the detector runs on several workers at once while the UI edits the set
during `frame:draw-ui`, which in KSA's frame order is after `ExecuteNextVehicleSolvers` has started
the next step. A `ConcurrentDictionary` lookup is lock-free for readers, allocates nothing on the hot
path, and a change simply applies to the next worker read. The managed checks hammer `Add`/`Remove`
against `Contains`/`Snapshot` in parallel to cover exactly this.[^kitchen-sink-tests-program]

## Key code

| Path | Role |
|---|---|
| [kitchen-sink.lib/GLoadProtectionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/GLoadProtectionPatches.cs) | Signature-checked install, single-site transpiler, worker-side filter |
| [kitchen-sink.lib/GLoadProtection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/GLoadProtection.cs) | Concurrent reference-identity registry and pruning |
| [kitchen-sink.lib/KitchenSinkSubmod.GLoadProtection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/KitchenSinkSubmod.GLoadProtection.cs) | Filtered picker, Add button, protected table |
| [kitchen-sink.lib/KitchenSinkSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/KitchenSinkSubmod.Saves.cs#L24-L65) | `kitchen-sink-g-load` capture, validation, reset and rebind |
| [kitchen-sink.lib/KitchenSinkLib.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/KitchenSinkLib.cs#L21-L25) | Per-frame `Prune`; `Dispose` resets protection |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L89) | Supermod install (removal at L146) |
| [kitchen-sink/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink/Patcher.cs) | Standalone host install and removal |
| [kitchen-sink.tests/GameFixture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.tests/GameFixture.cs) | Native-free copy of the detector in namespace `KSA` so the production transpiler binds to it |
| [kitchen-sink.tests/Program.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.tests/Program.cs) | Threshold matrix, cause classification, identity isolation, concurrency, rejected layouts, unpatch |
| [plans/VEHICLE_DESTRUCTION_INVESTIGATION.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/plans/VEHICLE_DESTRUCTION_INVESTIGATION.md) | Authors' trace of why strong parts did not save the cart |

## Snippets

The guarded transpiler and the filter it calls:

```csharp
// kitchen-sink.lib/GLoadProtectionPatches.cs L42-L69
    internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        var getter = AccessTools.PropertyGetter(typeof(StructuralLoad), nameof(StructuralLoad.GLoadFraction));
        int match = -1;
        for (int i = 0; i < code.Count; i++)
        {
            if (!code[i].Calls(getter)) continue;
            if (match >= 0)
                throw new InvalidOperationException("KSA structural-failure detector has multiple G-load comparisons.");
            match = i;
        }
        if (match < 0)
            throw new InvalidOperationException("KSA structural-failure detector G-load comparison was not found.");

        // Change only the value consumed by the destruction decision, preserving
        // telemetry, pressure checks, part damage and flight-computer limits.
        code.InsertRange(match + 1, new[]
        {
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(GLoadProtectionPatches), nameof(FilterGLoadFraction)))
        });
        return code;
    }

    private static double FilterGLoadFraction(double fraction, VehicleUpdateState state) =>
        GLoadProtection.Contains(state.ReadOnlyVehicle) ? 0.0 : fraction;
```

The site it rewrites in the game (build 5554). Telemetry is stored first; the only `GLoadFraction`
read is the comparison on L1016:

```csharp
// current/decomp/KSA/PhysicsBubble.cs L1010-L1021
			ref StructuralLoad newStructuralLoad = ref vehicleState.UpdateData.NewStructuralLoad;
			newStructuralLoad.PeakGLoad = vehicleState.PeakGLoad;
			newStructuralLoad.MaxGLoad = num;
			newStructuralLoad.PeakDynamicPressure = vehicleState.PeakDynamicPressure;
			newStructuralLoad.MaxDynamicPressure = 200000.0;
			newStructuralLoad.IsPressureHydrodynamic = situation.HasOceanContact();
			bool flag = newStructuralLoad.GLoadFraction >= 1.0;
			bool flag2 = newStructuralLoad.DynamicPressureFraction >= 1.0;
			bool flag3 = situation.HasTerrainContact() || situation.HasOceanContact() || vehicleState.HadVehicleContactThisFrame;
			if (flag && flag3 && vehicleState.PartFailureEvent != null)
			{
				flag = false;
```

Rebinding saved ids to reconstructed vessels:

```csharp
// kitchen-sink.lib/KitchenSinkSubmod.Saves.cs L24-L52
        new SaveParticipant<string[]>("kitchen-sink-g-load", CaptureGLoadVehicles,
            ResetGLoadProtection, (state, context) =>
            {
                context.Require(state.Length == 0 || GLoadProtectionPatches.IsApplied,
                    "G-load protection patch is unavailable; saved protection could not be restored.");
                foreach (string id in state)
                {
                    var vehicle = VehicleProvider.FindVehicle(id);
                    if (vehicle == null || vehicle.IsDisposed)
                    {
                        context.Warn($"Missing or ambiguous G-load protection vehicle '{id}'.");
                        continue;
                    }
                    GLoadProtection.Add(vehicle);
                }
            }, order: 20, validate: ValidateGLoadVehicles)
    };

    private static string[] CaptureGLoadVehicles()
    {
        var vehicles = GLoadProtection.Snapshot().Where(vehicle => !vehicle.IsDisposed).ToArray();
        // Avoid producing an apparently valid save that cannot uniquely restore its targets.
        foreach (var vehicle in vehicles)
            if (!ReferenceEquals(vehicle, VehicleProvider.FindVehicle(vehicle.Id)))
                throw new InvalidOperationException($"G-load protection vehicle '{vehicle.Id}' is missing or ambiguous.");
        var ids = vehicles.Select(vehicle => vehicle.Id).Order(StringComparer.Ordinal).ToArray();
        ValidateGLoadVehicles(ids);
        return ids;
    }
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `PhysicsBubble.DetectStructuralFailure(VehicleUpdateState)` (private static) | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L999-L1036) | Transpiler target |
| `PhysicsBubble.FullPhysicsEndFrame` / `EndStepFrame` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1655-L1683) | Worker caller (`PartFailure.Detect` then the detector) |
| `PhysicsBubble.ApplyRenderEventsToVehicles` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L919-L970) | Main-thread consumer of `DestructionEvent` |
| `StructuralLoad.GLoadFraction` | [current/decomp/KSA/StructuralLoad.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StructuralLoad.cs#L15) | IL anchor; `PeakGLoad / MaxGLoad` |
| `VehicleStructuralLimits.EffectiveMaxGLoad` | [current/decomp/KSA/VehicleStructuralLimits.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleStructuralLimits.cs#L17-L20) | Size-dependent G limit the filter bypasses |
| `VehicleUpdateState.ReadOnlyVehicle`, `DestructionEvent` | [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L14) | Identity key on the worker; the event never created |
| `BubbleEndStepJob` dispatch in `VehicleUpdateTask` | [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L742-L753) | Parallel worker batch that runs the detector |
| `Vehicle.UpdateFromTaskResultsUnsynchronized`, `Vehicle.StructuralLoad` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2529) | Real telemetry still published |
| `LoadAlert.Draw` | [current/decomp/KSA/LoadAlert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LoadAlert.cs#L52-L64) | HUD G warning keeps reading the unfiltered fraction |
| `Vehicle.IsDisposed`, `Vehicle.Id` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs) | Liveness and save ids |

## Pitfalls and version notes

- Scope is deliberately narrow. Part contact failure (`Part.CrashTolerancePascals`), the 200 kPa
  dynamic-pressure kill, explicit destroy and explode, and Bepu contacts are untouched. For a
  collision-heavy machine, raise part crash tolerances separately or use
  [The Tick](./the-tick-vessel-indestructibility.md).[^kitchen-sink-readme]
- Compared with The Tick, which wraps the same detector in a prefix and postfix and nulls the event
  afterwards (all causes, including pressure), Kitchen Sink changes one stack value before the
  verdict (G-load only). Both can be installed: Harmony runs the transpiled body inside The Tick's
  prefix and postfix.
- The transpiler insists on exactly one `GLoadFraction` read. KSA adding a second read (for example
  a G-based warning inside the detector) or removing it makes installation fail loudly; the authors
  recorded the body as unchanged across 5402, 5438 and 5482, and it is still a single read at
  `PhysicsBubble.cs` L1016 in 5554.[^scope-ui-customization]
- The private method is found by string name with an exact signature. A rename throws in `Apply`; the
  supermod logs it and the panel shows "G-load protection is unavailable".
- Semantic drift is not caught by the IL guard: if KSA moves the G-load decision elsewhere, protection
  silently stops while the patch still installs.
- Debris shed by a protected vessel is a new `Vehicle` and is not protected; native debris is already
  exempt from the G-load verdict but not from pressure.
- Capture refuses to save when two live vessels share a protected vessel's id, which surfaces as a
  save diagnostic rather than a silently wrong restore.

## See also

- [The Tick vessel indestructibility](./the-tick-vessel-indestructibility.md) (all force-based kills, same detector)
- [Scoped collision suppression](./garrys-torch-scoped-collision-suppression.md) (removing contacts instead)
- [Native save hooks](./lib-native-save-hooks.md) and [scene save sidecar](./unscience-scene-save-sidecar.md)
- [Managed test harnesses](./unscience-managed-test-harnesses.md)
- [Kitchen Sink sub-mod](../submods/kitchen-sink.md) and [mod overview](../overview.md)
- [Filter one value read through a registry check](../../../howto/patching/transpilers/write-a-harmony-transpiler/write-a-harmony-transpiler-4.md) - how-to article demonstrating this feature
- [Concurrent registries keyed by reference](../../../howto/physics/solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-3.md) - how-to article demonstrating this feature
- [Filter only the g-load read with a guarded transpiler and persist it by vessel identity](../../../howto/vehicles-and-parts/vehicles/make-a-vehicle-indestructible/make-a-vehicle-indestructible-3.md) - how-to article demonstrating this feature

[^kitchen-sink-readme]: kitchen-sink/README.md
[^destruction-investigation]: plans/VEHICLE_DESTRUCTION_INVESTIGATION.md (authors' trace of the destruction systems, 5402)
[^ksa-physics-bubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-vehicle-structural-limits]: KSA.VehicleStructuralLimits (decompiled, build 2026.10.10.5554)
[^kitchen-sink-tests-program]: kitchen-sink.tests/Program.cs
[^scope-saves]: scope/saves.md
[^ksa-vehicle-update-task]: KSA.VehicleUpdateTask (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^scope-ui-customization]: scope/ui-customization.md (Kitchen Sink G-load integration points)
