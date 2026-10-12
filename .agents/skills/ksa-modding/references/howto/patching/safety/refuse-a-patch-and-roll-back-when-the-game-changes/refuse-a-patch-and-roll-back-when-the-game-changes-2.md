---
type: How-To
title: One Harmony owner per feature with TryApply, validated handles and runtime flags
description: Give each feature its own Harmony id, validate its reflection handles before patching, roll a failed block back by id, gate dependent features on success and set the runtime flags that shared hooks read from the TryApply result
category: patching
group: safety
topic: refuse-a-patch-and-roll-back-when-the-game-changes
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/feature-gated-patch-sets]
techniques: [harmony-prefix, harmony-unpatch-by-id, reflection]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapUnload]
lifecycle: [startup:all-mods-loaded, event:starmap-unload]
game_types: [HarmonyLib.Harmony, KSA.Universe, KSA.TransferPlanner]
tags: [harmony, rollback, feature-gating, tryapply, validation, safety]
---

KSA-AdvancedFlightComputer ships nine independent features plus a shared core in one assembly and patches about thirty
game methods. A rename in a KSA update should cost the player one feature, not the whole mod, and a patch block
that fails halfway must not leave half of its detours installed. The mod gets this from a small `FeaturePatchSet` class:
every feature gets its own Harmony id, its reflection handles are validated before anything is patched, a block that
throws is unpatched by id, and the runtime flags that shared hooks read are set from the result.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by AFC in
[Feature-gated Harmony patch sets with rollback](../../../../mods/ksa-advancedflightcomputer/features/feature-gated-patch-sets.md).

## Background: how the game and StarMap behave

Harmony unpatching by owner id removes only that owner's patches. A local test against Lib.Harmony 2.4.2: two owners
patch one method; `UnpatchAll("mod.a")` removes only `mod.a`'s prefix, while an id-less `UnpatchAll()` removes every
owner's (see [Throw on a bad match, UnpatchAll(own id), publish the instance on success](refuse-a-patch-and-roll-back-when-the-game-changes-1.md)). Harmony also re-runs every transpiler on a method when another patch is added or removed, so a
rollback re-executes other features' transpilers; AFC's are idempotent and log through once-only helpers.

At shutdown StarMap runs an id-less `UnpatchAll()` before any `[StarMapUnload]` method
([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)[^starmap-starmapcore]), so AFC's ordered teardown matters only to a host that unloads mods
while the game keeps running, such as a test harness.

## Lifecycle and timing

Everything here runs in `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`): every game type is loaded, other mods'
patches already exist, and nothing in the universe is touched yet, so no vehicle state can be half-initialised by a
failed block. The runtime flags are plain static booleans read later from the main thread and the vehicle workers;
they are written only during load and unload. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Create one `Harmony` per feature and roll back by id.** `TryApply` creates `new Harmony($"{prefix}.{feature}")`,
   runs the feature's apply delegate, and on any exception calls `UnpatchAll(harmony.Id)`. Unpatching by id removes only
   that feature's patches, so earlier features and other mods stay intact. An owner whose unpatch itself threw is kept
   so a later unload can retry:

```csharp
// AdvancedFlightComputer/Core/FeaturePatchSet.cs L6-L50
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

    internal void UnpatchAll()
    {
        for (int i = _owners.Count - 1; i >= 0; i--)
        {
            if (TryUnpatch(_owners[i]))
                _owners.RemoveAt(i);
        }
    }

    private static bool TryUnpatch(Harmony harmony)
    {
        try
        {
            harmony.UnpatchAll(harmony.Id);
            return true;
        }
        catch (Exception ex)
        {
            // Keep the owner so unload can retry without leaving other feature owners unprocessed.
            DefaultCategory.Log.Warning($"[AFC] Could not remove patches for '{harmony.Id}': {ex}");
            return false;
        }
    }
```

Source: [AdvancedFlightComputer/Core/FeaturePatchSet.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/FeaturePatchSet.cs#L6-L50)[^ksa-advancedflightcomputer-featurepatchset]

2. **Validate reflection handles before patching.** Each static handle carries a `[UsedBy(Feature.A | Feature.B)]` tag.
   `Validate(feature)` logs the missing members that feature needs, a handle with no tag fails every validation, and the
   feature is skipped rather than patched:

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

Source: [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L373-L395)[^ksa-advancedflightcomputer-gamereflection]

   See [Feature-tagged handle registry with a Validate per feature](../validate-patch-targets-and-detect-game-update-breakage/validate-patch-targets-and-detect-game-update-breakage-1.md) for the handle registry itself.

3. **Run every feature through the same two gates.** `OnFullyLoaded` calls `Validated(name, ...)` and then
   `_patches.TryApply(name, ...)`. Dependencies are explicit booleans: `coreReady` gates MultiPass, RCS, Guidance, AutoStage and
   AutoRemove; ManeuverTools needs PlanWindow; MultiPass needs both. Each flag read by a shared hook takes the
   `TryApply` result, and a failed block calls its own `Disable*` for non-Harmony side effects:

```csharp
// AdvancedFlightComputer/Mod.cs L30-L69
[StarMapAllModsLoaded]
public void OnFullyLoaded()
{
    string gameVersion = VersionInfo.Current.VersionString;
    DefaultCategory.Log.Info($"[AFC] Game version: {gameVersion}");
    if (gameVersion != TestedGameVersion)
        DefaultCategory.Log.Warning(
            $"[AFC] Tested against {TestedGameVersion}, current is {gameVersion}. Some features may not work correctly.");

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

Source: [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L30-L69)[^ksa-advancedflightcomputer-mod]

```csharp
// AdvancedFlightComputer/Mod.cs L106-L112
private static bool Validated(string feature, Func<bool> validate)
{
    if (validate())
        return true;
    DefaultCategory.Log.Warning($"[AFC] {feature} disabled - reflection targets not found.");
    return false;
}
```

Source: [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L106-L112)

4. **Keep the shared hook installed but inert.** The shared vehicle tick stays installed under `Core` even when one
   feature fails; it drives a feature only if that feature's flag is set (see
   [One shared tick with per-feature enable flags set from TryApply results](../../harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-4.md)):

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

5. **Warn on version drift, do not block.** `OnFullyLoaded` logs a warning when `VersionInfo.Current.VersionString` differs
   from the tested constant; the structural gates above are what decide.

6. **Apply patches manually, never with `PatchAll`.** Each class must land in the owner of the feature it belongs to, via
   `harmony.CreateClassProcessor(typeof(X)).Patch()` or `harmony.Patch(method, prefix: ...)`.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Core/FeaturePatchSet.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/FeaturePatchSet.cs) | One owner per feature, rollback by id |
| [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L30-L112) | Gates, `TryApply`, dependants and flags |
| [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L373-L395) | Feature-tagged validation |

## Variations and gotchas

- **`UnpatchAll()` without an id removes every mod's patches.** Always pass the owner id.
- **Side effects outside Harmony are not undone by unpatching.** Entries added to `TransferPlanner.TransferTypes`,
  settings sections and event subscriptions need an explicit `Disable*`; the flags of a failed block are cleared wholesale.
- **A failed shared block disables every dependant.** Without the shared tick a feature would never complete its burns.
- **A thrown anchor check inside `TryApply` is contained.** Compare with [Let an anchor failure stop the launch (and why that can be right)](refuse-a-patch-and-roll-back-when-the-game-changes-3.md), where the same kind of throw in
  `StarMapBeforeMain` is deliberately left to abort the launch.
- **Teardown order is mostly moot under StarMap 4dc60f8.** `DeInit` has already removed every patch, so AFC's own
  `UnpatchAll` finds nothing.

## See also

- [Throw on a bad match, UnpatchAll(own id), publish the instance on success](refuse-a-patch-and-roll-back-when-the-game-changes-1.md), [Let an anchor failure stop the launch (and why that can be right)](refuse-a-patch-and-roll-back-when-the-game-changes-3.md)
- [Refusing a patch and rolling back when the game changes](overview.md)
- [Feature-gated Harmony patch sets with rollback](../../../../mods/ksa-advancedflightcomputer/features/feature-gated-patch-sets.md)
- [Isolating patch failures and keeping patch bodies from throwing](../isolate-patch-failures-and-fault-proof-patch-bodies/overview.md), [Removing Harmony patches safely at runtime and at exit](../remove-your-patches-safely-at-runtime-and-at-exit/overview.md)

[^starmap-starmapcore]: StarMap/StarMap.Core/StarMapCore.cs
[^ksa-advancedflightcomputer-featurepatchset]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/FeaturePatchSet.cs
[^ksa-advancedflightcomputer-gamereflection]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/GameReflection.cs
[^ksa-advancedflightcomputer-mod]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Mod.cs
[^ksa-advancedflightcomputer-sharedvehiclehooks]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/SharedVehicleHooks.cs
