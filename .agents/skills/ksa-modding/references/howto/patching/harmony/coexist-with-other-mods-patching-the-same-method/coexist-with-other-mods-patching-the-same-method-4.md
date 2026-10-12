---
type: How-To
title: One shared tick with per-feature enable flags set from TryApply results
description: Drive several per-vehicle state machines from one prefix and postfix pair on Universe.ApplyVehicleSolvers, with a static enable flag per feature that is set only from that feature's own patch result
category: patching
group: harmony
topic: coexist-with-other-mods-patching-the-same-method
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/post-solver-vehicle-tick
  - mods/ksa-advancedflightcomputer/features/feature-gated-patch-sets
techniques: [harmony-prefix, harmony-postfix, feature-flags]
hooks: [harmony-prefix KSA.Universe.ApplyVehicleSolvers, harmony-postfix KSA.Universe.ApplyVehicleSolvers]
lifecycle: [startup:all-mods-loaded, frame:apply-solvers]
game_types: [KSA.Universe, KSA.Vehicle, KSA.VehicleUpdateTask, KSA.FlightComputer]
tags: [harmony, shared-seam, feature-flags, tryapply, solver, post-solver]
---

KSA-AdvancedFlightComputer runs three per-vehicle state machines every frame (automatic staging, a multi-pass
scheduler and an RCS burn executor). All need the physics result of the step that just finished, and two must act
before stock reacts to it. Rather than hooking each separately, it installs *one* pair of patches on
`Universe.ApplyVehicleSolvers` that tick all of them, and gives each feature a static enable flag that is set
only from the result of that feature's own patch block. A shared hook that stays installed therefore never drives a
feature whose block failed.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by AFC in
[Main-thread vehicle tick right after the solver apply](../../../../mods/ksa-advancedflightcomputer/features/post-solver-vehicle-tick.md) and
[Feature-gated Harmony patch sets with rollback](../../../../mods/ksa-advancedflightcomputer/features/feature-gated-patch-sets.md).

## Background: how the game does it

`Universe.ApplyVehicleSolvers` is a public static method with no vehicle argument. It copies the finished solver
results onto the main-thread vehicles (`_vehicleUpdateTask.ApplyResultsToMainThread()`) and advances `_lastSimStep`
([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1950)[^ksa-universe]). `VehicleUpdateTask.ApplyResultsToMainThread` applies each bubble's results in a
parallel batch on the vehicle worker pool, and `Vehicle.UpdateFromTaskResultsSynchronized` is
`AggressiveInlining`, so a detour on a per-vehicle callee cannot be relied on
([Postfix the single non-inlined outer method instead of a per-vehicle callee](../../internals/patch-around-aggressive-inlining/patch-around-aggressive-inlining-3.md)). The static outer method runs once on the main thread after all of that
finishes.

## Lifecycle and timing

In `Program.PrepareFrame` the order is: wait for the orbit, vehicle and cloth solvers; `ApplyOrbitSolvers`;
**`ApplyVehicleSolvers`** (prefix and postfix); `ApplyClothSolvers`; `Glfw.PollEvents`; `ApplyInputEvents`;
`RefreshVehiclesInFrame`; `RaisePendingAlerts`; and finally `ExecuteNextVehicleSolvers`
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2222)[^ksa-program]). At the postfix the vehicle state and flight computer are fresh from the finished
step, the next step is not dispatched, and writes made here are copied into the next step by `Vehicle.PrepareWorker`
(`frame:apply-solvers`; see [Mutate simulation state safely relative to the physics step](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md)). `Program.VehiclesInFrame` is stale at this point, so the tick enumerates
`Universe.CurrentSystem.All` instead.

The patches are applied at `startup:all-mods-loaded` (`[StarMapAllModsLoaded]`), after every other mod's patches
exist and before any universe is loaded.

## Steps

1. **Put the enable flags in the shared hook class and clear them wholesale.** Each flag defaults to `false`:

```csharp
// AdvancedFlightComputer/Core/SharedVehicleHooks.cs L11-L36
internal static class SharedVehicleHooks
{
    // Core remains patched when a feature fails to load. Only complete feature blocks may drive vehicles.
    internal static bool MultiPassEnabled { get; set; }
    internal static bool RcsEnabled { get; set; }
    internal static bool GuidanceEnabled { get; set; }
    internal static bool AutoStageEnabled { get; set; }
    internal static bool AutoRemoveEnabled { get; set; }

    internal static void ApplyPatches(Harmony harmony)
    {
        harmony.CreateClassProcessor(typeof(ApplySolversPatch)).Patch();
        harmony.CreateClassProcessor(typeof(PendingAlertsPatch)).Patch();
        harmony.CreateClassProcessor(typeof(DisposePatch)).Patch();
        harmony.CreateClassProcessor(typeof(SetNamePatch)).Patch();
    }

    internal static void Reset()
    {
        VehicleControlOwnership.Clear();
        MultiPassEnabled = false;
        RcsEnabled = false;
        GuidanceEnabled = false;
        AutoStageEnabled = false;
        AutoRemoveEnabled = false;
    }
```

Source: [AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs#L11-L36)[^ksa-advancedflightcomputer-sharedvehiclehooks]

2. **Patch once, with a prefix and a postfix, in the shared owner.** The prefix samples the state that the apply is
   about to overwrite (the staging detector's baseline); the postfix ticks the features in a deliberate order:

```csharp
// AdvancedFlightComputer/Core/SharedVehicleHooks.cs L108-L120
// Program.PrepareFrame joins the workers before this call, then drains input events, then lets FlightComputer.RaisePendingAlerts end the burns the workers or the RCS executor finished or stopped.
// The prefix runs before the worker results overwrite the burn state the staging detector compares against.
[HarmonyPatch(typeof(Universe), nameof(Universe.ApplyVehicleSolvers), new Type[0])]
private static class ApplySolversPatch
{
    static void Prefix()
    {
        if (AutoStageEnabled)
            StagingDetector.Sample();
    }

    static void Postfix() => TickVehicles(LoadedVehicles.All);
}
```

Source: [AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs#L108-L120)

3. **Tick through the flags only.** Staging runs first so a staging that keeps an Auto burn alive can take back the
   worker's out-of-propellant stop before anything reads it. Per vehicle the multi-pass scheduler reads the burn mode
   before the RCS driver may change it. Each driver is called only when its flag is set:

```csharp
// AdvancedFlightComputer/Core/SharedVehicleHooks.cs L38-L55
internal static void TickVehicles(ReadOnlySpan<Astronomical> bodies)
{
    // Staging first, so a staging that keeps an Auto burn alive has taken back the stock out-of-propellant stop before anything else reads it.
    if (AutoStageEnabled)
        StagingDetector.Evaluate();

    foreach (Astronomical body in bodies)
    {
        if (body is not Vehicle vehicle || vehicle.IsDisposed)
            continue;

        // The pass state machine reads the burn mode before the RCS driver can change it.
        if (MultiPassEnabled)
            PassCompletionPatch.TickVehicle(vehicle);
        if (RcsEnabled)
            RcsDriverPatch.TickVehicle(vehicle);
    }
}
```

Source: [AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs#L38-L55)

4. **Give each feature its own Harmony owner and set the flag from `TryApply`.** `FeaturePatchSet.TryApply` creates a
   Harmony instance named `<prefix>.<feature>`, runs the feature's apply delegate and, on any exception, unpatches by
   that id and returns `false`:

```csharp
// AdvancedFlightComputer/Core/FeaturePatchSet.cs L6-L26
internal sealed class FeaturePatchSet(string idPrefix)
{
    private readonly List<Harmony> _owners = new();

    internal bool TryApply(string feature, Action<Harmony> apply)
    {
        Harmony harmony = new($"{idPrefix}.{feature}");
        _owners.Add(harmony);
        try
        {
            apply(harmony);
            return true;
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Warning($"[AFC] {feature} patching failed. Removing its patches: {ex}");
            if (TryUnpatch(harmony))
                _owners.Remove(harmony);
            return false;
        }
    }
```

Source: [AdvancedFlightComputer/Core/FeaturePatchSet.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/FeaturePatchSet.cs#L6-L26)[^ksa-advancedflightcomputer-featurepatchset]

5. **Gate dependants on the result.** `Core` (the shared hooks, save observer, command sink) must apply before
   MultiPass, RCS, Guidance and AutoStage may; each flag takes the `TryApply` return value, and a failed block
   calls its own `Disable*` for the non-Harmony side effects:

```csharp
// AdvancedFlightComputer/Mod.cs L39-L69
bool coreReady = Validated("Core", GameReflection.ValidateCore)
    && _patches.TryApply("Core", PatchCore);

// Its own owner, so a settings-window rename costs the mod sections and nothing else.
if (Validated("SettingsPage", GameReflection.ValidateSettingsPage))
    _patches.TryApply("SettingsPage", ModSettingsPage.ApplyPatches);

if (Validated("HyperbolicTargets", GameReflection.ValidateHyperbolicTargets))
    _patches.TryApply("HyperbolicTargets", HyperbolicTargets.ApplyPatches);

if (Validated("ManeuverTools", GameReflection.ValidateManeuverTools))
{
    if (!_patches.TryApply("ManeuverTools", PatchManeuverTools))
        DisableManeuverTools();
    else if (!Validated("PlanWindow", GameReflection.ValidatePlanWindow)
             || !_patches.TryApply("PlanWindow", PatchPlanWindow))
        DisableManeuverTools();
    else if (coreReady && Validated("MultiPass", GameReflection.ValidateMultiPass))
    {
        SharedVehicleHooks.MultiPassEnabled = _patches.TryApply("MultiPass", PatchMultiPass);
        if (!SharedVehicleHooks.MultiPassEnabled)
            DisableMultiPass();
    }
}

if (coreReady && Validated("RcsTranslation", GameReflection.ValidateRcsTranslation))
{
    SharedVehicleHooks.RcsEnabled = _patches.TryApply("RcsTranslation", PatchRcsTranslation);
    if (!SharedVehicleHooks.RcsEnabled)
        DisableRcsTranslation();
}
```

Source: [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L39-L69)[^ksa-advancedflightcomputer-mod]

6. **Use the live registry, not a cache.** `LoadedVehicles.All` is `Universe.CurrentSystem.All`; disposed vehicles
   are skipped with `IsDisposed` ([AdvancedFlightComputer/Core/LoadedVehicles.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/LoadedVehicles.cs)[^ksa-advancedflightcomputer-loadedvehicles]).

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs) | Flags, shared patches, tick order |
| [AdvancedFlightComputer/Core/FeaturePatchSet.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/FeaturePatchSet.cs) | One Harmony owner per feature, rollback by id |
| [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L30-L104) | Gates, `TryApply` and flag assignment |

## Variations and gotchas

- **Keep the shared patch even when a feature fails.** The shared tick is installed under `Core`; other features
  still need it. Only the flag decides whether a given feature runs.
- **Do not do expensive work per vehicle here.** This is on the main thread before the next step can be dispatched
  and lengthens the frame directly.
- **The prefix sees the previous step's state.** That is what the staging detector wants as a baseline.
- **Harmony re-runs transpilers whenever a block is added or removed,** so a rollback of one feature re-executes
  another feature's transpiler; AFC's are idempotent and log once ([Anchors that one mod can consume (and what the next transpiler then sees)](coexist-with-other-mods-patching-the-same-method-2.md)).
- **Side effects outside Harmony are not undone by unpatching.** Entries added to `TransferPlanner.TransferTypes`,
  settings sections and event subscriptions need an explicit `Disable*`.

## See also

- [Priority.First prefixes and Priority.Last finalizers around shared seams](coexist-with-other-mods-patching-the-same-method-1.md), [Anchors that one mod can consume (and what the next transpiler then sees)](coexist-with-other-mods-patching-the-same-method-2.md), [Idempotent shared installs with a registration API](coexist-with-other-mods-patching-the-same-method-3.md), [One worker-side postfix merging every writer](coexist-with-other-mods-patching-the-same-method-5.md)
- [Coexisting with other mods that patch the same method](overview.md)
- [One Harmony owner per feature with TryApply, validated handles and runtime flags](../../safety/refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-2.md) for the rollback half of `FeaturePatchSet`
- [Main-thread vehicle tick right after the solver apply](../../../../mods/ksa-advancedflightcomputer/features/post-solver-vehicle-tick.md), [Feature-gated Harmony patch sets with rollback](../../../../mods/ksa-advancedflightcomputer/features/feature-gated-patch-sets.md)

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-advancedflightcomputer-sharedvehiclehooks]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/SharedVehicleHooks.cs
[^ksa-advancedflightcomputer-featurepatchset]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/FeaturePatchSet.cs
[^ksa-advancedflightcomputer-mod]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Mod.cs
[^ksa-advancedflightcomputer-loadedvehicles]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/LoadedVehicles.cs
