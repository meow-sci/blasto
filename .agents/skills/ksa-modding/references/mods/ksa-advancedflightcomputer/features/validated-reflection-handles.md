---
type: Mod Feature
title: Validated reflection handles with per-feature tags
description: A single class resolves every private game member once, tags each handle with the features that need it, and lets each feature refuse to load when one of its handles is gone
mod: ksa-advancedflightcomputer
category: patching
techniques: [reflection, harmony-transpiler]
hooks:
  - reflection KSA.TransferPlanner._transferType
  - reflection KSA.Vehicle._manualControlInputs
  - reflection KSA.GameSettings._openTab
  - reflection KSA.FlightComputer.UpdateAttitudeTarget
lifecycle: [startup:all-mods-loaded]
game_types:
  - KSA.TransferPlanner
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.GameSettings
  - KSA.PatchedConic
  - KSA.Program
tags: [reflection, accesstools, fieldref, version-drift, validation]
---

AFC reaches into roughly 40 private game members: static fields of the Transfer Planner, the
vehicle's manual control inputs, private methods such as `FlightComputer.UpdateAttitudeTarget`, and
IL anchors for transpilers. `GameReflection` resolves all of them once when the class initializes,
tags each handle with the features that use it, and exposes one `ValidateX()` per feature. A feature
whose handle did not resolve is skipped at load with a log line that names the missing member, and
the other features still load.

## How it works

- **One class, static readonly fields.** Every handle is a `FieldInfo`, `MethodInfo`,
  `PropertyInfo`, a typed `AccessTools.FieldRef` or a precomputed value (such as the boxed
  `SettingsTab.Mods` enum member). Method lookups always pass an explicit parameter list, so a new
  overload with the same name added by a game update cannot be picked up by accident
  ([AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L176-L187)).
- **Tagging.** A private `[Flags] enum Feature` lists Core, ManeuverTools, PlanWindow, MultiPass,
  RcsTranslation, HyperbolicTargets, Guidance, GuidanceDiagnostics, AutoStage, AutoRemove,
  SettingsPage and `SoftAnchor`. Each field carries `[UsedBy(Feature.A | Feature.B)]`.
- **Validation by self-reflection.** `Validate(feature)` enumerates the class's own public static
  fields, skips handles not tagged for that feature, and fails when a tagged value is null. A handle
  with no `UsedBy` at all fails every validation, so a key cannot be added without being covered
  ([AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L373-L395)).
- **Soft anchors.** Handles tagged `SoftAnchor` (for example `PatchedConic.FindClosestApproaches`, the
  two `DrawSelectedTransfer*` methods, `ModLibrary.AllParts`) are ignored by the validators; each one
  controls only its own optional patch, which checks it itself and logs when it is absent.
- **Typed accessors.** Hot fields get a `FieldRef` delegate built with
  `AccessTools.StaticFieldRefAccess<F>` or `AccessTools.FieldRefAccess<T, F>` (no boxing per
  access). The helper returns null when the field's type no longer matches, so a type change is
  reported by the same validation as a missing field
  ([AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L414-L445)).
- **Readable names.** `DisplayName` turns `TransferPlanner_sourceBodyRef` into
  `TransferPlanner._sourceBody` so the warning names the game member, and a handle and its typed twin
  are reported once.
- **Thin wrappers.** Callers do not touch `FieldInfo`. `StockPlanner` wraps the planner handles as
  properties (`ShowPlanWindow`, `TransferCalculated`, `TransferType`) with comments on what each stock
  flag means ([AdvancedFlightComputer/Core/StockPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/StockPlanner.cs#L22-L66)).
- **Drift test.** The headless test `afc-reflection-targets` runs every validator, every transpiler
  anchor probe (`IsAnchorPresent`) and a read/write round trip through the typed accessors against
  the live game, so a rename fails in CI rather than silently at a player's load
  ([tests/AdvancedFlightComputer.HarnessTests/Features/Core/ReflectionTargetsTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Core/ReflectionTargetsTest.cs)).

## Lifecycle and timing

The static initializer runs the first time `GameReflection` is touched. The first touch is in
`[StarMapImmediateLoad]` (`AutoStageFeature.InjectGaugeEnumAtLoad` calls `ValidateAutoStage`), which
StarMap runs as a prefix on `KSA.Mod.PrepareSystems`; all game types are loadable by then because
KSA.dll is already running. The per-feature `Validate` calls happen in `[StarMapAllModsLoaded]`
before each feature's Harmony block. After that the handles are read only on the main thread: the
planner, settings and gauge draws, the selected-transfer prefixes in pre-render, the staging
execution in the solver-apply tick, and the guidance step in the `Vehicle.PrepareWorker` prefix, which writes the manual control inputs through
`Vehicle_manualControlInputsRef`. No worker-side postfix touches `GameReflection`; the
`UpdateAttitudeTarget` handle is used only to install the patch. The handles are `static readonly`, so
no locking would be needed either way.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs) | All handles, `UsedBy` tags, validators, typed accessor factories |
| [AdvancedFlightComputer/Core/StockPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/StockPlanner.cs) | Typed property wrappers over the Transfer Planner handles |
| [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L106-L112) | `Validated(feature, validate)` gate used before every patch block |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Core/ReflectionTargetsTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Core/ReflectionTargetsTest.cs) | Headless drift test over every handle and anchor |

## Snippets

```csharp
// AdvancedFlightComputer/Core/GameReflection.cs L317-L338
    // The per-vehicle guidance step runs as a prefix here.
    // Universe.PrepareVehicleWorkers calls it after InputEvents.ApplyInputEvents drained the player's input and before VehicleUpdateState.PrepareFromVehicle snapshots the flight computer and the manual inputs for the worker, so it is the last main-thread site before the snapshot and the only one after the drain.
    // The rest of the method still runs after the prefix and clears EngineOn while BurnMode is Auto, which is why guidance holds the mode in Manual.
    [UsedBy(Feature.Guidance)]
    public static readonly MethodInfo? Vehicle_PrepareWorker =
        AccessTools.Method(typeof(Vehicle), nameof(Vehicle.PrepareWorker), new[] { typeof(SimStep) });

    // Stock builds AttitudeTarget here and reads it in UpdateAttitudeError right after, so a postfix can add the commanded turning rate without changing where the craft points.
    // It is a private method with one call site, so an inlining attribute added on a game update would leave this handle resolving and the postfix silent. afc-guidance-driver checks that the rate arrives.
    [UsedBy(Feature.Guidance)]
    public static readonly MethodInfo? FlightComputer_UpdateAttitudeTarget =
        AccessTools.Method(typeof(FlightComputer), "UpdateAttitudeTarget",
            new[] { typeof(FlightComputerNavigation).MakeByRefType() });

    // The engine master switch and throttle that the player's ignite and shutdown actions write.
    [UsedBy(Feature.Guidance)]
    public static readonly FieldInfo? Vehicle_manualControlInputs =
        AccessTools.Field(typeof(Vehicle), "_manualControlInputs");

    [UsedBy(Feature.Guidance)]
    public static readonly AccessTools.FieldRef<Vehicle, ManualControlInputs>? Vehicle_manualControlInputsRef =
        InstanceFieldRef<Vehicle, ManualControlInputs>(Vehicle_manualControlInputs);
```

```csharp
// AdvancedFlightComputer/Core/GameReflection.cs L373-L395
    private static bool Validate(Feature feature)
    {
        bool allOk = true;
        HashSet<string>? reported = null;
        foreach (FieldInfo handle in typeof(GameReflection).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (handle.GetCustomAttribute<UsedByAttribute>() is not { } tag)
            {
                DefaultCategory.Log.Error($"[AFC] {handle.Name} carries no UsedBy tag, so no feature validates it.");
                allOk = false;
                continue;
            }
            if ((tag.Features & feature) == 0 || handle.GetValue(null) != null)
                continue;

            // A handle and its typed twin name the same field, so both get one line.
            string name = DisplayName(handle);
            if ((reported ??= new HashSet<string>()).Add(name))
                DefaultCategory.Log.Error($"[AFC] {feature}: {name} not found - game version may have changed.");
            allOk = false;
        }
        return allOk;
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `TransferPlanner._sourceBody`, `_transferType`, `_transferCalculated`, `_transferBeingCalculated`, `_displaySelectedTransfer`, ... ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L53-L67)) | Static private planner state read and written through `StaticFieldRefAccess` |
| `Vehicle._manualControlInputs` and `Vehicle.PrepareWorker(SimStep)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396)) | Guidance engine writes and its per-vehicle step |
| `FlightComputer.UpdateAttitudeTarget(ref FlightComputerNavigation)` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs)) | Private method postfixed by guidance |
| `GameSettings._openTab` and the private `SettingsTab` enum ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L150)) | Detect the Mods settings page |
| `PatchedConic.FindClosestApproaches` ([current/decomp/KSA/PatchedConic.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PatchedConic.cs)) | Soft anchor for an optional hyperbolic-target guard |

## Pitfalls and version notes

- A private method that a game update marks `AggressiveInlining` still resolves by reflection, but a
  Harmony patch on it goes silent. AFC's comment on `UpdateAttitudeTarget` calls this out; only a
  behavioral test (here `afc-guidance-driver`) catches it.
- By-ref and `out` parameters must be expressed with `MakeByRefType()` in the type list, or the
  lookup returns null.
- A private nested enum (`GameSettings.SettingsTab`) cannot be named from mod code. Resolve the field,
  check `FieldType.IsEnum`, `Enum.TryParse` the member once, and compare boxed values.
- Static `FieldRef` delegates are only valid for static fields; `FieldRefAccess<T, F>` is needed for
  instance fields, with `T` a reference type.

## See also

- [Feature-gated patch sets](feature-gated-patch-sets.md)
- [Headless harness test mod](headless-harness-test-mod.md)
- [Mods settings page sections](mods-settings-page-sections.md)
- [Transfer Planner custom plan types](transfer-planner-custom-plan-types.md)
- [Typed FieldRefAccess, boxed private enum members and by-ref lookups](../../../howto/patching/internals/call-private-game-members-through-reflection-and-delegates/call-private-game-members-through-reflection-and-delegates-3.md) - how-to article demonstrating this feature
- [Explicit parameter arrays with MakeByRefType for overloaded and by-reference targets](../../../howto/patching/internals/patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-3.md) - how-to article demonstrating this feature
- [Postfix the single non-inlined outer method instead of a per-vehicle callee](../../../howto/patching/internals/patch-around-aggressive-inlining/patch-around-aggressive-inlining-3.md) - how-to article demonstrating this feature
- [Feature-tagged handle registry with a Validate per feature](../../../howto/patching/safety/validate-patch-targets-and-detect-game-update-breakage/validate-patch-targets-and-detect-game-update-breakage-1.md) - how-to article demonstrating this feature
- [Build a consumer mod with its own mod.toml, InternalsVisibleTo and a test base class](../../../howto/testing-and-tooling/offline-tests/headless-test-harness-for-game-mods/headless-test-harness-for-game-mods-1.md) - how-to article demonstrating this feature
- [Run transpilers over the game's real IL and scope test patches to a disposable Harmony owner](../../../howto/testing-and-tooling/offline-tests/test-harmony-patches-without-the-game/test-harmony-patches-without-the-game-3.md) - how-to article demonstrating this feature

