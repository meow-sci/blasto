---
type: Mod Feature
title: Feature-gated Harmony patch sets with rollback
description: One Harmony instance per feature, validated before patching and rolled back on failure, so a game update disables one feature instead of the whole mod
mod: ksa-advancedflightcomputer
category: patching
techniques: [starmap-lifecycle, harmony-prefix, harmony-postfix, harmony-transpiler, reflection]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapUnload]
lifecycle: [startup:all-mods-loaded, event:mod-unload]
game_types: [KSA.VersionInfo]
tags: [harmony, robustness, feature-flags, unload, version-check]
---

AFC ships nine independent features plus a shared core in one assembly and patches around 30 game
methods. A rename in a KSA update should cost the player one feature, not the whole mod, and a patch
block that fails halfway must not leave half of its detours installed. AFC gets this with a tiny
`FeaturePatchSet` class: every feature gets its own Harmony id, its reflection handles are validated
before anything is patched, and a block that throws is unpatched by id.

## How it works

1. `FeaturePatchSet` is constructed once with an id prefix (`com.maxi.advancedflightcomputer`).
   `TryApply(feature, apply)` creates `new Harmony($"{prefix}.{feature}")`, runs the feature's apply
   delegate, and on any exception calls `harmony.UnpatchAll(harmony.Id)`. Unpatching by id removes
   only that feature's patches, so earlier features and other mods that patched the same methods stay
   intact ([AdvancedFlightComputer/Core/FeaturePatchSet.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/FeaturePatchSet.cs#L10-L26)).
2. `UnpatchAll()` walks the owners newest first and keeps an owner whose unpatch threw, so a later
   unload can retry it ([AdvancedFlightComputer/Core/FeaturePatchSet.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/FeaturePatchSet.cs#L28-L50)).
3. `Mod.OnFullyLoaded` runs every feature through the same two gates: `Validated(name, GameReflection.ValidateX)`
   checks that every reflection handle tagged for the feature resolved (see
   [validated reflection handles](validated-reflection-handles.md)), then `_patches.TryApply(name, PatchX)`
   ([AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L30-L104)).
4. Dependencies between features are explicit booleans. `coreReady` (shared vehicle hooks, save
   observer, command sink) gates MultiPass, RCS, Guidance, AutoStage and AutoRemove; ManeuverTools
   needs PlanWindow; MultiPass needs both. When a prerequisite fails, the dependent feature's
   `Disable*` method undoes non-Harmony side effects such as injected planner types or settings
   sections.
5. The runtime switches that patched code reads (`SharedVehicleHooks.MultiPassEnabled`, `RcsEnabled`,
   `GuidanceEnabled`, `AutoStageEnabled`, `AutoRemoveEnabled`) are set from the `TryApply` result, so
   a shared hook that stays installed never drives a feature whose own block failed
   ([AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs#L13-L18)).
6. A version check logs a warning when `VersionInfo.Current.VersionString` differs from the
   `TestedGameVersion` constant; it does not block loading. The same constant drives CI (see
   [CI reference assemblies](ci-reference-assemblies.md)).
7. `[StarMapUnload]` tears down in a deliberate order: flush pending staging rows, reset the shared
   hooks, `UnpatchAll`, release guidance, remove injected planner types, reset the save-scoped
   registries, then the per-feature flags and finally `LogHelper.Reset()` so the transpiler re-runs
   that unpatching triggers do not print their once-only lines again
   ([AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L220-L251)). Under StarMap 4dc60f8 the order is
   moot: `StarMapCore.DeInit` calls `_harmony.UnpatchAll()` without an id, which removes every
   Harmony patch in the process (AFC's included), and only then `ModLoader.Dispose` invokes the
   `[StarMapUnload]` methods; `DeInit` itself runs from `GameSurveyer.Dispose` after the game's entry
   point has returned ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35),
   [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L143-L151), [StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L51-L61)).
   AFC's own `UnpatchAll` therefore finds nothing to remove, and the "before the patches come off"
   comment on the staging flush does not hold. The ordered teardown matters only to a host that unloads
   mods while the game keeps running, such as a test harness.

Patches are applied manually (`harmony.CreateClassProcessor(typeof(X)).Patch()` or
`harmony.Patch(method, prefix: ...)`), never with `PatchAll`, because each class must land in the
owner of the feature it belongs to.

## Lifecycle and timing

| Hook | When it fires | What AFC does there |
|---|---|---|
| `[StarMapImmediateLoad]` | Harmony prefix on `KSA.Mod.PrepareSystems`, before this mod's systems and XML load | Only the AUTOSTAGE gauge enum injection (see [AUTOSTAGE gauge button](autostage-gauge-button.md)) |
| `[StarMapAllModsLoaded]` | Harmony postfix on `ModLibrary.LoadAll` ([StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L17-L28)) | Version check, validation and every `TryApply` |
| `[StarMapAfterGui]` | Postfix on `Program.OnDrawUiViewports` | Guidance panel draw |
| `[StarMapUnload]` | StarMap `ModLoader.Dispose`, called from `StarMapCore.DeInit` after `Harmony.UnpatchAll()` and after the game's `Main` returned | Ordered teardown above (no patches left to remove) |

Patching at all-mods-loaded means every game type is loaded and other mods' patches already exist;
nothing in the universe is touched yet, so no vehicle state can be half-initialized by a failed
block. The feature flags are plain static booleans read later from the main thread and the vehicle
workers; they are written only during load and unload.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Core/FeaturePatchSet.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/FeaturePatchSet.cs) | Per-feature Harmony owners, rollback, retrying unpatch |
| [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs) | Validation and patch order, dependency gates, unload order |
| [AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs) | Feature enable flags read by shared hooks |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Core/FeaturePatchSetTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Core/FeaturePatchSetTest.cs) | In-game test: a failing block rolls back without touching other owners |

## Snippets

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

## Game APIs touched

| Member | Use |
|---|---|
| `VersionInfo.Current.VersionString` ([current/decomp/KSA/VersionInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionInfo.cs)) | Compared with `TestedGameVersion` for a load-time warning |
| `HarmonyLib.Harmony.UnpatchAll(string)` | Removes only one owner's patches |

## Pitfalls and version notes

- `UnpatchAll()` without an id removes every mod's patches; always pass the owner id. StarMap itself
  makes that call at shutdown, before any `[StarMapUnload]` runs, so unload code must not depend on its
  own patches still being installed.
- Harmony re-runs every transpiler on a method whenever another patch is added to or removed from it,
  so a rollback re-executes transpilers of other features. AFC's transpilers are idempotent and log
  through `LogHelper.DebugOnce` for this reason (see [warn-once logging](warn-once-logging-and-diagnostics.md)).
- Side effects outside Harmony (entries added to `TransferPlanner.TransferTypes`, settings sections,
  event subscriptions on `SaveLoadObserver`) are not undone by unpatching. Each feature therefore has a
  `Disable*` method, and the flags of a failed block are cleared wholesale.
- A failed shared block (`Core`) disables every feature that drives vehicles, because a feature
  without the shared tick would never complete its burns.

## See also

- [Validated reflection handles](validated-reflection-handles.md)
- [Post-solver vehicle tick](post-solver-vehicle-tick.md)
- [Warn-once logging and diagnostics](warn-once-logging-and-diagnostics.md)
- [Overview](../overview.md)
- [One shared tick with per-feature enable flags set from TryApply results](../../../howto/patching/harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-4.md) - how-to article demonstrating this feature
- [One Harmony owner per feature with TryApply, validated handles and runtime flags](../../../howto/patching/safety/refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-2.md) - how-to article demonstrating this feature

