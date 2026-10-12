---
type: Mod Feature
title: TOML config seeded from a shipped template
description: Ship an editable gatos.default.toml in the mod folder, copy it once to a live gatos.toml in the user data folder, deserialize with Tomlyn into one typed class, clamp bad values with a warning instead of failing, and persist in-game changes atomically on a background task
mod: gatos
category: persistence
techniques: [toml-config, background-thread]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, thread:background]
game_types: []
tags: [config, toml, tomlyn, settings, data-folder, atomic-write]
---

Players want to set memory, CPUs or disk size before the first launch, and mod updates must never
overwrite their settings. gatOS ships a fully commented template in the install folder and treats a
separate file in the user data folder as the live config. Everything is one flat TOML file mapped
onto one C# class with snake_case names.

## How it works

**Seed once** ([gatOS.GameMod/Configuration/GatOsConfig.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Configuration/GatOsConfig.cs#L557-L620)):

1. If `<data>/gatos.toml` does not exist, copy `<mod>/gatos.default.toml` to it (so pre-launch edits
   of the template carry over); if that fails, write defaults.
2. Deserialize with `TomlSerializer.Deserialize<GatOsConfig>(text, options)` using
   `PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower` (Tomlyn 2.6.0): missing keys keep their
   property defaults, unknown keys are ignored.
3. On a parse error or an unknown `schema` value, log a warning and return in-memory defaults
   without touching the file, so a typo never destroys the player's config.
4. `Normalize()` clamps every numeric knob into a bootable range and logs each correction
   (`memory_mb` 128..8192, `cpus` 1..16, `disk_size_gb` 1..128, `sample_rate_hz` 1..120 ...)
   ([gatOS.GameMod/Configuration/GatOsConfig.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Configuration/GatOsConfig.cs#L722-L752)).

**Write.** `Save` serializes in a sectioned, commented layout driven by a `Sections` table (common
knobs first); a key missing from the table is still emitted by a catch-all so no setting is
dropped ([gatOS.GameMod/Configuration/GatOsConfig.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Configuration/GatOsConfig.cs#L42-L60)). The write is atomic: temp file
plus `File.Move(..., overwrite: true)` ([gatOS.GameMod/Configuration/AtomicFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Configuration/AtomicFile.cs)).

**Live changes.** The ImGui menu and status window change values such as the sample rate; the mod
mutates its in-memory config and calls `PersistConfig`, which saves on `Task.Run` so the render
thread never does file I/O ([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L939-L953)).

**Where files live.** `GatOsPaths.ConfigFile` is `<MyDocuments>/My Games/Kitten Space Agency/mods/gatOS/gatos.toml`;
the template ships at `<mod>/gatos.default.toml` via the build's deploy target.

## Lifecycle and timing

- Read once in `[StarMapAllModsLoaded]` (`OnFullyLoaded`) before any subsystem is constructed, so
  every server, store and the VM options are built from the clamped values
  ([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L229-L233)).
- Most settings take effect on next launch; runtime-tunable ones (telemetry rate and gates) are
  copied into volatile settings objects that the game thread reads each tick.
- Saves run on a thread-pool task (`thread:background`).

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Configuration/GatOsConfig.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Configuration/GatOsConfig.cs) | typed config, load-or-seed, normalize, sectioned serialize |
| [gatOS.GameMod/Configuration/AtomicFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Configuration/AtomicFile.cs) | temp-and-rename write |
| [gatOS.GameMod/Configuration/gatos.default.toml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Configuration/gatos.default.toml) | the shipped, commented template |
| [gatOS.Vm/GatOsPaths.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/GatOsPaths.cs) | config and data folder paths |

## Snippets

```csharp
// gatOS.GameMod/Configuration/GatOsConfig.cs L593-L619

        GatOsConfig config;
        try
        {
            // Missing keys keep their property defaults; unknown keys are ignored; malformed
            // TOML and wrong value types throw TomlException (verified against Tomlyn 2.6.0).
            config = TomlSerializer.Deserialize<GatOsConfig>(File.ReadAllText(path), TomlOptions)
                     ?? new GatOsConfig();
        }
        catch (Exception ex)
        {
            ModLog.Log.Warn(
                $"Config '{path}' could not be read ({ex.Message}); using defaults. Fix or delete the file.");
            return new GatOsConfig();
        }

        if (config.Schema != CurrentSchema)
        {
            ModLog.Log.Warn(
                $"Config '{path}' has schema {config.Schema}; this build understands schema {CurrentSchema}. "
                + "Using defaults (the file is left untouched).");
            return new GatOsConfig();
        }

        config.Normalize();
        return config;
    }
```

## Game APIs touched

None.

## Pitfalls and version notes

- Never overwrite a config you could not parse; fall back to defaults in memory and tell the user.
- Keep the template and the class in lockstep; gatOS's contribution rules require a config key to
  have a property, a `Sections` entry, clamp behavior and a template block in the same change.
- On Windows the gatOS install folder and data folder coincide (repo notes), which is why the live
  file and the template have different names.

## See also

- [ModMenu entry with a fallback menu](modmenu-entry-with-fallback-menu.md)
- [Logging shim and perf stats](logging-and-perfstats.md)
- [qcow2 overlay guest disks](qcow2-overlay-guest-disks.md)
- [gatOS overview](../overview.md)
- [Map one flat settings class to gatos.toml with Tomlyn and snake_case names](../../../howto/persistence/settings-formats/persist-mod-settings-toml/persist-mod-settings-toml-1.md) - how-to article demonstrating this feature
- [Save from Task.Run with an atomic helper, clamp bad values on load and leave an unparseable file alone](../../../howto/persistence/settings-formats/write-and-load-config-files-safely/write-and-load-config-files-safely-3.md) - how-to article demonstrating this feature
- [Keep user data in Documents/mods/<mod> apart from the install, and seed the live config from a template](../../../howto/persistence/user-data/choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-3.md) - how-to article demonstrating this feature

