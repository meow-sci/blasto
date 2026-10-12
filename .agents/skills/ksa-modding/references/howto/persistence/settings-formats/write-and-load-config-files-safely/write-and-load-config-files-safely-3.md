---
type: How-To
title: Save from Task.Run with an atomic helper, clamp bad values on load and leave an unparseable file alone
description: Write side - an AtomicFile helper called from a thread-pool task so the render thread does no file I/O; load side - Normalize clamps every numeric knob into a bootable range with a warning, and a parse error or unknown schema returns in-memory defaults without touching the file
category: persistence
group: settings-formats
topic: write-and-load-config-files-safely
approach: 3
difficulty: intermediate
prerequisites: [starmap, tomlyn]
demonstrated_by: [mods/gatos/features/toml-config-seeded-template]
techniques: [background-thread, toml-config]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, thread:background]
game_types: []
tags: [config, atomic-write, background-thread, clamp, normalize, schema-version]
---

Outcome: a config that never makes the render thread wait for the disk, cannot be left half-written, always boots with a sane value, and is never replaced by the loader just because the player made a typo. gatOS applies all four rules to a TOML file; the rules are format-independent. Demonstrated by [TOML config seeded from a shipped template](../../../../mods/gatos/features/toml-config-seeded-template.md); the TOML mapping itself is [Map one flat settings class to gatos.toml with Tomlyn and snake_case names](../persist-mod-settings-toml/persist-mod-settings-toml-1.md).

## Background: how the game does it

Nothing in the engine reads or protects mod config. The one engine-side constraint is threading: ImGui and Vulkan are main-thread only, but plain `System.IO` is not, so file writes can go to the .NET thread pool as long as the data they serialise is not mutated meanwhile (see the [overview](./overview.md#engine-background)).

## Lifecycle and timing

The config loads once in `[StarMapAllModsLoaded]` before any subsystem is built (`startup:all-mods-loaded`, [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L225-L236)); saves run on `Task.Run` (`thread:background`) from `PersistConfig`, called after a live setting changes. The task captures the config reference at call time and returns to nothing - there is no synchronisation point back to the main thread because the result is not needed. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

### Write side

1. **A tiny atomic helper**, shared by every write of the file:

   ```csharp
   // gatOS.GameMod/Configuration/AtomicFile.cs L3-L16
   /// <summary>
   ///     Crash-safe file writes for the config file: the content is written to a sibling temp file
   ///     and renamed over the destination, so an interrupted write can never leave a truncated file
   ///     behind (the pattern from purrTTY's <c>Configuration/AtomicFile</c>; OS_PLAN.md T6.3).
   /// </summary>
   internal static class AtomicFile
   {
       public static void WriteAllText(string path, string contents)
       {
           var tmp = path + ".tmp";
           File.WriteAllText(tmp, contents);
           File.Move(tmp, path, overwrite: true);
       }
   }
   ```

2. **Save from a task, log failures, never throw into the caller.** The comment records the rule it follows ("no file I/O in draw code"):

   ```csharp
   // gatOS.GameMod/Mod.cs L933-L953
   /// <summary>
   ///     Persists the current config to disk on a background task so an in-game settings change
   ///     becomes the next session's startup default. Off the render thread (threading rule 5: no
   ///     file I/O in draw code); failures are logged, never thrown — the live change already took
   ///     effect via <see cref="Telemetry"/>.
   /// </summary>
   private void PersistConfig()
   {
       var config = _config;
       _ = Task.Run(() =>
       {
           try
           {
               config.Save(GatOsPaths.ConfigFile);
           }
           catch (Exception ex)
           {
               ModLog.Log.Warn($"Could not persist gatOS settings: {ex.Message}");
           }
       });
   }
   ```

### Load side

3. **Never throw out of load.** `LoadOrCreate` returns a config in every case: seeded from the bundled template, created with defaults, deserialised, or defaults after a failure:

   ```csharp
   // gatOS.GameMod/Configuration/GatOsConfig.cs L557-L619
   public static GatOsConfig LoadOrCreate(string path, string? bundledDefaultPath = null)
   {
       if (!File.Exists(path))
       {
           // Prefer seeding from the bundled default so pre-launch edits in the install folder
           // carry over; the copied file is then read below like any existing config.
           if (bundledDefaultPath is not null && File.Exists(bundledDefaultPath))
           {
               try
               {
                   File.Copy(bundledDefaultPath, path);
                   ModLog.Log.Info($"Seeded the config at '{path}' from the bundled default '{bundledDefaultPath}'.");
               }
               catch (Exception ex)
               {
                   ModLog.Log.Warn(
                       $"Could not seed the config from '{bundledDefaultPath}' ({ex.Message}); writing defaults instead.");
               }
           }

           if (!File.Exists(path))
           {
               var fresh = new GatOsConfig();
               try
               {
                   fresh.Save(path);
                   ModLog.Log.Info($"Created the default config at '{path}'.");
               }
               catch (Exception ex)
               {
                   ModLog.Log.Warn($"Could not write the default config to '{path}': {ex.Message}");
               }

               return fresh;
           }
       }

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

   Two specific rules in that code: a parse error is logged with "Fix or delete the file." and answered with in-memory defaults while the file on disk is untouched; a `schema` value this build does not understand does the same.

4. **Version the file.** `const int CurrentSchema = 1` and a `schema` key give a future build a way to say "I cannot read this" instead of mis-reading it ([gatOS.GameMod/Configuration/GatOsConfig.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Configuration/GatOsConfig.cs#L20-L21)).

5. **Clamp, do not reject.** `Normalize()` pulls every numeric knob into a bootable range and logs each correction:

   ```csharp
   // gatOS.GameMod/Configuration/GatOsConfig.cs L722-L744
   /// <summary>Clamps out-of-range values into something bootable, logging each correction.</summary>
   internal void Normalize()
   {
       MemoryMb = Clamp(nameof(MemoryMb), MemoryMb, 128, 8192); // Alpine floor / sanity ceiling
       Cpus = Clamp(nameof(Cpus), Cpus, 1, 16);
       DiskSizeGb = Clamp(nameof(DiskSizeGb), DiskSizeGb, 1, 128);
       SampleRateHz = Clamp(nameof(SampleRateHz), SampleRateHz, 1, 120);
       BootTimeoutSeconds = Clamp(nameof(BootTimeoutSeconds), BootTimeoutSeconds, 0, 3600);
       CommandTimeoutMs = Clamp(nameof(CommandTimeoutMs), CommandTimeoutMs, 100, 30000);
       MaxCommandsPerFrame = Clamp(nameof(MaxCommandsPerFrame), MaxCommandsPerFrame, 1, 4096);
       HttpBindHost = NormalizeBindHost(nameof(HttpBindHost), HttpBindHost);
       McpBindHost = NormalizeBindHost(nameof(McpBindHost), McpBindHost);
       MqttBindHost = NormalizeBindHost(nameof(MqttBindHost), MqttBindHost);
       if (HttpPreferredPort != 0)
           HttpPreferredPort = Clamp(nameof(HttpPreferredPort), HttpPreferredPort, 1024, 65535);
       if (McpPreferredPort != 0)
           McpPreferredPort = Clamp(nameof(McpPreferredPort), McpPreferredPort, 1024, 65535);
       if (MqttPreferredPort != 0)
           MqttPreferredPort = Clamp(nameof(MqttPreferredPort), MqttPreferredPort, 1024, 65535);
       SerialIntervalMs = Clamp(nameof(SerialIntervalMs), SerialIntervalMs, 50, 60000);
       FieldFeedHz = Clamp(nameof(FieldFeedHz), FieldFeedHz, 1, 30);
       MqttPublishHz = Clamp(nameof(MqttPublishHz), MqttPublishHz, 0, 120);
       DisplayFps = Clamp(nameof(DisplayFps), DisplayFps, 1, 60);
   ```

   String enums are normalised the same way (`display_encoding` to `rgba-zlib`, `schedule_default_clock` to `render`) with a warning ([gatOS.GameMod/Configuration/GatOsConfig.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Configuration/GatOsConfig.cs#L785-L799)).

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Configuration/GatOsConfig.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Configuration/GatOsConfig.cs#L557-L619) | Load, seed, parse, schema check |
| [gatOS.GameMod/Configuration/GatOsConfig.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Configuration/GatOsConfig.cs#L722-L800) | `Normalize` |
| [gatOS.GameMod/Configuration/AtomicFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Configuration/AtomicFile.cs) | Temp-and-rename |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L935-L953) | `PersistConfig` on a worker |

## Variations and gotchas

- **The fallback protects the file only until the next save.** `LoadOrCreate` never overwrites a file it could not read, but `PersistConfig` writes the in-memory config (the defaults) to `GatOsPaths.ConfigFile` whenever a live setting changes, which replaces the file the player was told to fix. If the file must survive, remember "load failed" and skip saves until the player acknowledges it.
- **Clamped values are written back.** After `Normalize()` the clamped value, not the one the player typed, is what the next save writes.
- **Thread safety.** The task serialises `_config` from a pool thread while the main thread may mutate it; the mod's settings changes are discrete UI actions, so the window is small, but the code takes no lock.
- **Overlapping saves** share the fixed temp name `gatos.toml.tmp`; two simultaneous saves could collide (inferred from the code).
- **Prefer this write path** when saves are frequent or the file is large; for a few bytes written on a click it is overkill (compare [Skip the dirty flag by comparing the serialised snapshot with the last write](write-and-load-config-files-safely-2.md)).

## See also

- [Mark dirty, debounce, swap the file in atomically, and retry deserialisation by dropping the broken field](write-and-load-config-files-safely-1.md), [Flush on close and unload, skip junk keys on load and migrate the previous format](write-and-load-config-files-safely-4.md), [overview](./overview.md)
- [TOML config seeded from a shipped template](../../../../mods/gatos/features/toml-config-seeded-template.md)
- [Map one flat settings class to gatos.toml with Tomlyn and snake_case names](../persist-mod-settings-toml/persist-mod-settings-toml-1.md), [Find where KSA keeps user data and choose where a mod stores its config and data files](../../user-data/choose-where-a-mod-stores-its-files/overview.md)
