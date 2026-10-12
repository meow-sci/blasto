---
type: Mod Feature
title: Save-scoped settings sidecar
description: Postfix UncompressedSave.Load and Make to track the current save and write an atomic per-save settings file into the save folder (vestigial in this version)
mod: ohmystars
category: persistence
techniques: [harmony-postfix, save-sidecar]
hooks: [harmony-postfix KSA.UncompressedSave.Load, harmony-postfix KSA.UncompressedSave.Make]
lifecycle: [startup:all-mods-loaded, event:save, event:load]
game_types: [KSA.UncompressedSave, KSA.GameSave, KSA.GameSaves, KSA.Constants]
tags: [saves, sidecar, harmony, postfix, persistence, toml]
---

Oh My Stars carries a generic per-save settings store inherited from StellariumCatalog: Harmony
postfixes on the game's save and load methods keep a "current save id", and a small TOML-like file
named `OhMyStars_settings.toml` is written into each save's folder when the game creates a save. In
this version the settings type has no fields, so the machinery runs but stores nothing; it is
documented here because the pattern (sidecar next to the native save, atomic write, keyed by save
id) is reusable.[^oms-saveloadobserver-cs] [^oms-savescopedsettingsstore-cs]

## How it works

1. **Hooks.** `SaveLoadObserver` patches `UncompressedSave.Load()` and the static
   `UncompressedSave.Make(string name)` with postfixes ([src/SaveLoadObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SaveLoadObserver.cs#L19-L55)).
   After a load it records `__instance.Id` and calls `OhMyStarsSettingsStore.LoadForSave(id)`; after
   a save it takes `__result?.Id ?? name` and calls `SaveForSave(id)`. Both bodies are wrapped in
   `try`/`catch` with a logged warning.
2. **Store.** `SaveScopedSettingsStore<TSettings>` is generic over the settings type and takes a
   default factory and read/write delegates ([src/SaveScopedSettingsStore.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SaveScopedSettingsStore.cs#L7-L133)).
   `Load()` enumerates every folder under the saves directory and reads
   `<saves>/<saveId>/OhMyStars_settings.toml` when present, keying by folder name. `SaveOne` writes
   to `<file>.tmp` and then `File.Move(tmp, file, overwrite: true)`, deleting the temp on failure.
3. **Format.** `SettingsBlockFile.Read` parses `[[settings]]` blocks of `key = value` lines with
   quoted-string escapes and `#` comments; `SettingsBlock` offers typed getters including a
   `[x, y, z, w]` `float4`; `SettingsBlockWriter` emits the same syntax
   ([src/SaveScopedSettingsStore.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SaveScopedSettingsStore.cs#L135-L441)).
4. **What is actually stored.** `OhMyStarsSettings` is empty, `OhMyStarsSettingsToml.Read` returns a
   new instance, and `Write` only calls `EndBlock()`, which writes a blank line
   ([src/Settings.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/Settings.cs#L5-L96)).[^oms-settings-cs] Each new save therefore gets a one-line
   `OhMyStars_settings.toml`.
5. **Saves folder.** `Init` hard-codes `MyDocuments/My Games/Kitten Space Agency/saves`
   ([src/Settings.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/Settings.cs#L54-L69)), which equals the game's default `GameSaves.SaveFolderPath`
   (`Constants.DocumentsFolderPath` + `saves`)
   ([current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L124),
   [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)).[^ksa-gamesaves-cs] [^ksa-constants-cs]

## Lifecycle and timing

- **Install at `startup:all-mods-loaded`** together with the attitude patches
  ([src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L13-L20)).
- **`event:save`.** `UncompressedSave.Make` builds, populates and writes a new save and returns it
  (or `null` on failure); it is reached through `GameSaves.MakeUncompressedSave`, from `GameSaves.MakeSave` when the
  name is new and from the `save` terminal command ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L141-L146),
  [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L250-L279)).[^ksa-uncompressedsave-cs] The postfix runs after
  the save files exist, on the main thread.
- **`event:load`.** The postfix on `UncompressedSave.Load()` runs after the stock load completes
  ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L45-L93)).

## Key code

| Path | Role |
|---|---|
| [src/SaveLoadObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SaveLoadObserver.cs#L19-L55) | Load and Make postfixes |
| [src/SaveScopedSettingsStore.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SaveScopedSettingsStore.cs#L7-L133) | Generic store with atomic per-save writes |
| [src/SaveScopedSettingsStore.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SaveScopedSettingsStore.cs#L322-L441) | `[[settings]]` block parser |
| [src/Settings.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/Settings.cs#L12-L96) | Concrete (empty) settings and file name |

## Snippets

```csharp
// src/SaveLoadObserver.cs L24-L55
    [HarmonyPatch(typeof(UncompressedSave), nameof(UncompressedSave.Load))]
    private static class LoadPatch {
        static void Postfix(UncompressedSave __instance) {
            try {
                CurrentSaveId = __instance.Id ?? string.Empty;

                OhMyStarsSettingsStore.LoadForSave(CurrentSaveId);
            } catch(Exception ex) {
                DefaultCategory.Log.Warning(
                    $"[OhMyStars] SaveLoadObserver Load Postfix: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(UncompressedSave), nameof(UncompressedSave.Make))]
    private static class MakePatch {
        static void Postfix(string name, GameSave __result) {
            try {
                string newSaveId = __result?.Id ?? name ?? string.Empty;

                if(string.IsNullOrEmpty(newSaveId))
                    return;

                CurrentSaveId = newSaveId;

                OhMyStarsSettingsStore.SaveForSave(newSaveId);
            } catch(Exception ex) {
                DefaultCategory.Log.Warning(
                    $"[OhMyStars] SaveLoadObserver Make Postfix: {ex}");
            }
        }
    }
```

```csharp
// src/SaveScopedSettingsStore.cs L98-L121
    private void SaveOne(string saveId, TSettings settings) {
        string configPath = GetConfigPath(saveId);
        string saveDir = Path.GetDirectoryName(configPath)!;
        string tempPath = configPath + ".tmp";

        try {
            Directory.CreateDirectory(saveDir);

            using(var writer = new StreamWriter(tempPath)) {
                var blockWriter = new SettingsBlockWriter(writer);

                // Keep writing the saveId into the block if your existing parser/writer format expects it.
                // Even though the file is now save-scoped, this preserves compatibility.
                _writeBlock(blockWriter, saveId, settings);
            }

            File.Move(tempPath, configPath, overwrite: true);
        } catch {
            if(File.Exists(tempPath))
                File.Delete(tempPath);

            throw;
        }
    }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `UncompressedSave.Load()` | [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L45) | Postfix: save loaded |
| `UncompressedSave.Make(string)` | [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L141-L146) | Postfix: save created |
| `GameSave.Id` | [current/decomp/KSA/GameSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSave.cs#L13) | Save key (folder name) |
| `GameSaves.SaveFolderPath` | [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L124) | Where saves live (not called; the mod rebuilds the path) |

## Pitfalls and version notes

- **Vestigial.** No setting is stored per save; the visible effect is an extra near-empty file in
  every save folder created through `Make`.
- **Overwrites are covered by the same postfix.** Saving under an existing name goes through a
  confirmation popup to `GameSaves.Overwrite` and `UncompressedSave.Overwrite`, and that method just
  calls `Make(base.Id)`, so the `Make` postfix fires for overwrites too
  ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L100-L106),
  [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L250-L271)). Patching `Make` alone is enough for
  saves made through `UncompressedSave`.
- **The load postfix also runs on refused or failed loads.** `Load()` returns early when refused in
  the editor or when the save cannot be read, and the postfix still records that save id
  ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L45-L93)).
- **Hard-coded Documents path.** StarMap can redirect `Constants.DocumentsFolderPath` with
  `-InstancePath` or `STARMAP_INSTANCE_PATH` ([StarMap.Core/Patches/DocumentsPathPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/DocumentsPathPatches.cs#L12-L31));[^starmap-patches-documentspathpatches-cs]
  the store would keep writing to the default Documents saves folder. Call
  `GameSaves.SaveFolderPath` instead.
- **Every load re-reads every save's sidecar**, because `LoadForSave` calls `_store.Load()` first.
- **Reset hook unused.** `SaveLoadObserver.Reset()` would clear star-orientation state on load, but
  nothing calls it.

## See also

- [settings.ini persistence](ini-settings-persistence.md)
- [Attitude-mode hand-off patches](attitude-mode-handoff-patches.md)
- [Mod root and packaging](mod-root-and-packaging.md)
- [Observe UncompressedSave.Load and reset a registry of save-lifetime caches](../../../howto/persistence/saves/detect-save-load/detect-save-load-3.md) - how-to article demonstrating this feature
- [Write a per-save settings store from a postfix on UncompressedSave.Make](../../../howto/persistence/saves/save-mod-state-with-the-game-save/save-mod-state-with-the-game-save-2.md) - how-to article demonstrating this feature

[^oms-saveloadobserver-cs]: OhMyStars src/SaveLoadObserver.cs
[^oms-savescopedsettingsstore-cs]: OhMyStars src/SaveScopedSettingsStore.cs
[^oms-settings-cs]: OhMyStars src/Settings.cs
[^ksa-gamesaves-cs]: KSA.GameSaves (decompiled, build 2026.10.10.5554)
[^ksa-constants-cs]: KSA.Constants (decompiled, build 2026.10.10.5554)
[^ksa-uncompressedsave-cs]: KSA.UncompressedSave (decompiled, build 2026.10.10.5554)
[^starmap-patches-documentspathpatches-cs]: StarMap StarMap.Core/Patches/DocumentsPathPatches.cs
