---
type: Mod Feature
title: Save-scoped TOML registries beside the mod
description: Per-save, per-vehicle execution state lives in TOML files in the mod folder, keyed by save id and vehicle id, loaded and rekeyed by postfixes on UncompressedSave.Load and Write
mod: ksa-advancedflightcomputer
category: persistence
techniques: [harmony-postfix, harmony-prefix, save-sidecar, toml-config]
hooks:
  - harmony-prefix KSA.UncompressedSave.Load
  - harmony-postfix KSA.UncompressedSave.Load
  - harmony-postfix KSA.UncompressedSave.Write
  - harmony-postfix KSA.Vehicle.Dispose
  - harmony-postfix KSA.Vehicle.SetName
lifecycle: [event:save-load, event:save-write, event:vehicle-destroyed]
game_types: [KSA.UncompressedSave, KSA.GameSave, KSA.Universe, KSA.Vehicle]
tags: [save, sidecar, toml, registry, vehicle-id, rename]
---

A multi-pass departure and an RCS burn are long-running executions: they must survive a save and a
reload in the middle, yet AFC must never change the stock save format, so uninstalling the mod
keeps every planned burn. AFC therefore stores its execution state in its own TOML files in the mod
folder (`multipass.toml`, `rcs-exec.toml`), keys every entry by `(save id, vehicle id)`, and keeps
the keys in sync with the game through postfixes on the concrete save class.

## How it works

- **Save id discriminator.** `SaveLoadObserver.CurrentSaveId` tracks the id of the loaded save
  (empty for a fresh world). Registries are dictionaries keyed by `(SaveId, VehicleId)`, so one file
  holds entries for every save on the machine ([AdvancedFlightComputer/Features/MultiPass/MultiPassRegistry.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassRegistry.cs#L34-L42)).
- **Load.** A prefix on `UncompressedSave.Load()` captures the current `UniverseData` reference; the
  postfix compares it, because stock `Load` returns normally when the editor refuses the load or the
  universe file cannot be read, and only a completed load replaces `UniverseData`
  ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L45-L92)).[^ksa-uncompressedsave] On a real load the observer
  resets every save-lifetime cache (`SaveScopedState.ResetAll`), reloads the multi-pass registry from
  disk, then raises `SaveLoaded` for subscribers (RCS registry, guidance release). Each stage runs in
  its own try/catch ([AdvancedFlightComputer/Core/SaveLoadObserver.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SaveLoadObserver.cs#L35-L74)).
- **Write.** A postfix on `UncompressedSave.Write()` runs only when it returned `true`. It rekeys the
  entries of the old save id to the written id (a "save as" moves the live execution to the new
  save, and entries of an overwritten destination are dropped), sets `CurrentSaveId`, persists, then
  raises `SaveWritten(old, new)` so the RCS registry rekeys and saves as well
  ([AdvancedFlightComputer/Core/SaveLoadObserver.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SaveLoadObserver.cs#L93-L144),
  [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L253-L257)).
- **Persistence is driven by the game's save.** Changes between saves stay in memory, so quitting
  without saving drops them, exactly as the game drops unsaved burns.
- **Atomic file writes.** `Save()` writes `<file>.<guid>.tmp` and `File.Move(temp, path, overwrite: true)`;
  a file that loaded with defects is not overwritten while nothing new needs saving
  ([AdvancedFlightComputer/Features/MultiPass/MultiPassRegistry.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassRegistry.cs#L194-L235)).
- **Hand-written TOML.** No TOML package is shipped. Writers emit `[[execution]]` tables with
  `CultureInfo.InvariantCulture` and round-trip `R` formatting for doubles; the line parser keeps
  readable blocks and reports dropped ones with file and line. `TomlIo` only escapes and unescapes
  strings ([AdvancedFlightComputer/Features/MultiPass/TomlIo.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/TomlIo.cs)).
- **Init-time load.** `MultiPassRegistry.Init()` loads the file at patch time, because the default
  world may never go through `UncompressedSave.Load`, and its first save would otherwise overwrite
  other saves' entries.
- **Vehicle identity.** Entries follow the vehicle. A prefix/postfix pair on `Vehicle.SetName(string)`
  records the old id and, only when the rename succeeded (`__result`), moves the registry entries;
  a postfix on `Vehicle.Dispose(bool)` removes them so a recycled id cannot inherit an execution
  ([AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs#L88-L165)).
- **One list of save-lifetime caches.** `SaveScopedState.ResetAll()` names every cache that holds
  `Vehicle`, `Burn` or `FlightPlan` references or body indices, with the reason in its doc comment:
  `Universe.DeserializeSave` destroys and rebuilds every vehicle, a disposed vehicle's
  `FlightComputer` and `BurnPlan` stay intact (so a kept `Burn` still looks live), and
  `LookupCollection.Deregister` swap-removes (so a kept index silently resolves to another body)
  ([AdvancedFlightComputer/Core/SaveScopedState.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SaveScopedState.cs)).

## Lifecycle and timing

`UncompressedSave.Load` and `Write` run on the main thread when the player loads or saves from the
menus or the `load`/`save` terminal actions ([current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L273-L289)). The load postfix runs after
`Universe.DeserializeSave` and `Program.OnGameLoaded` have finished inside `Load`, so the new
vehicles exist and can be looked up by id. `Vehicle.Dispose(bool)` is hooked rather than
`Dispose()` because `Dispose()` delegates to it and EVA boarding calls the bool overload directly.
Registries are only touched from the main thread.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Core/SaveLoadObserver.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SaveLoadObserver.cs) | Load/Write postfixes, save id, events |
| [AdvancedFlightComputer/Core/SaveScopedState.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SaveScopedState.cs) | Every save-lifetime cache reset on load and unload |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassRegistry.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassRegistry.cs) | `multipass.toml` registry: rekey, atomic save, tolerant parser |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsExecRegistry.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecRegistry.cs) | `rcs-exec.toml` registry with the same shape |
| [AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs) | Rename and dispose hooks that move or drop entries |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Core/RefusedLoadTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Core/RefusedLoadTest.cs) | Test: a refused load leaves the save id and registry untouched |

## Snippets

```csharp
// AdvancedFlightComputer/Core/SaveLoadObserver.cs L35-L61
    [HarmonyPatch(typeof(UncompressedSave), nameof(UncompressedSave.Load), new Type[0])]
    private static class LoadPatch
    {
        static void Prefix(UncompressedSave __instance, out UniverseData __state) => __state = __instance.UniverseData;

        static void Postfix(UncompressedSave __instance, UniverseData __state)
        {
            // UncompressedSave.Load returns normally when GameSaves.RefusedInEditor refuses the load or the universe file cannot be read.
            // Only a completed load replaces UniverseData.
            if (ReferenceEquals(__instance.UniverseData, __state))
            {
                if (DebugConfig.MultiPass)
                    DefaultCategory.Log.Debug(
                        $"[AFC] SaveLoadObserver.LoadPatch: ignored save '{__instance.Id}' because it did not load.");
                return;
            }

            string saveId = __instance.Id ?? string.Empty;
            CurrentSaveId = saveId;

            // Keep each stage independent so one failure does not prevent the others.
            RunStage(saveId, "reset the save-scoped state", SaveScopedState.ResetAll);
            RunStage(saveId, "restore the multi-pass registry", MultiPassRegistry.Load);
            RunStage(saveId, "notify the save-loaded subscribers", () => SaveLoaded?.Invoke());
            if (DebugConfig.MultiPass)
                RunStage(saveId, "log the loaded state", () => LogLoadedState(saveId));
        }
```

```csharp
// AdvancedFlightComputer/Core/SharedVehicleHooks.cs L147-L165
    // Vehicle.Dispose() delegates to Dispose(bool), and EVA boarding calls the bool overload directly.
    [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.Dispose), new[] { typeof(bool) })]
    private static class DisposePatch
    {
        static void Postfix(Vehicle __instance) => OnDisposed(__instance);
    }

    // A refused name leaves the vehicle unchanged, so it must not move registry entries.
    [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.SetName))]
    private static class SetNamePatch
    {
        static void Prefix(Vehicle __instance, out string __state) => __state = __instance.Id;

        static void Postfix(Vehicle __instance, bool __result, string __state)
        {
            if (__result)
                OnRenamed(__instance, __state);
        }
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `UncompressedSave.Load()` / `Write()` ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs)) | Hooked save paths; `Write` returns `bool` |
| `GameSave.UniverseData`, `GameSave.Id` ([current/decomp/KSA/GameSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSave.cs)) | Completed-load detection and save discriminator |
| `Universe.DeserializeSave` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs)) | Rebuilds every vehicle; the reason caches must be reset |
| `Vehicle.SetName(string)` returns `bool`, `Vehicle.Dispose(bool)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1802)) | Keep id-keyed entries in sync |

## Pitfalls and version notes

- `UncompressedSave` is the concrete type; patching the abstract `GameSave.Load` would not run. If
  the game adds another save format, it needs its own hook.
- Never keep `Vehicle`, `Burn` or `FlightPlan` references across a load. Store ids and resolve them
  again.
- A failed `Write` keeps entries on the previous save id, otherwise a later successful save of the old
  name would lose them.
- Paths come from `Environment.SpecialFolder.MyDocuments` + `My Games/Kitten Space Agency/mods/AdvancedFlightComputer`;
  this matches the Windows install layout named in the README.

## See also

- [Multi-pass execution](multi-pass-execution.md)
- [RCS burn execution](rcs-burn-execution.md)
- [Post-solver vehicle tick](post-solver-vehicle-tick.md)
- [Staging delays](staging-delays.md) (per-vehicle TOML overrides)
- [Observe UncompressedSave.Load and reset a registry of save-lifetime caches](../../../howto/persistence/saves/detect-save-load/detect-save-load-3.md) - how-to article demonstrating this feature
- [Key mod data on Vehicle.Id and follow renames and disposals with postfixes](../../../howto/persistence/saves/identify-vessels-and-parts-across-saves/identify-vessels-and-parts-across-saves-1.md) - how-to article demonstrating this feature
- [Write a per-save settings store from a postfix on UncompressedSave.Make](../../../howto/persistence/saves/save-mod-state-with-the-game-save/save-mod-state-with-the-game-save-2.md) - how-to article demonstrating this feature
- [Keep TOML in the mod folder keyed by (save id, vehicle id) and rekey it on save-as](../../../howto/persistence/saves/save-mod-state-with-the-game-save/save-mod-state-with-the-game-save-3.md) - how-to article demonstrating this feature

[^ksa-uncompressedsave]: KSA.UncompressedSave (decompiled, build 2026.10.10.5554)
