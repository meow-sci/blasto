---
type: How-To
title: Map one flat settings class to gatos.toml with Tomlyn and snake_case names
description: Deserialize a flat POCO with TomlSerializer and a snake_case naming policy, seed the live file from a commented template on first run, write a sectioned commented layout from a Sections table, and save from Task.Run
category: persistence
group: settings-formats
topic: persist-mod-settings-toml
approach: 1
difficulty: intermediate
prerequisites: [starmap, tomlyn]
demonstrated_by: [mods/gatos/features/toml-config-seeded-template]
techniques: [toml-config, background-thread]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, thread:background]
game_types: [KSA.Constants, KSA.GameSettings]
tags: [settings, toml, tomlyn, snake-case, template, seed, commented-output]
---

Outcome: players edit `gatos.toml` (memory, CPUs, disk size and a few dozen advanced options) and mod updates never overwrite it. gatOS maps a flat C# class to a flat TOML file with Tomlyn's serializer, ships a fully commented template in the install folder, and seeds the live file from that template on the first run. Prerequisite: the `Tomlyn` NuGet package, shipped with the mod. Demonstrated by [TOML config seeded from a shipped template](../../../../mods/gatos/features/toml-config-seeded-template.md). The load-side tolerance (clamping, leaving a broken file alone) and the atomic write are treated as techniques in [Save from Task.Run with an atomic helper, clamp bad values on load and leave an unparseable file alone](../write-and-load-config-files-safely/write-and-load-config-files-safely-3.md); the folder layout (install folder versus data folder) in [Keep user data in Documents/mods/<mod> apart from the install, and seed the live config from a template](../../user-data/choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-3.md).

## Background: how the game does it

The game keeps its own `settings.toml` flat in the documents folder and creates it from defaults when it is missing ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L2826-L2870)); gatOS follows that convention for its own file but keeps it in a separate data folder, `<MyDocuments>/My Games/Kitten Space Agency/mods/gatOS/gatos.toml` ([gatOS.Vm/GatOsPaths.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/GatOsPaths.cs#L21-L48)). The data folder is built from `Environment.SpecialFolder.MyDocuments`, not from `Constants.DocumentsFolderPath`, so a StarMap instance-path override does not move it.

## Lifecycle and timing

The config is read once in `[StarMapAllModsLoaded]` (`OnFullyLoaded`), before any subsystem is constructed, so every server, store and the VM options are built from the clamped values ([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L225-L236)). Most settings take effect on the next launch; runtime-tunable ones (telemetry rate and gates) are copied into volatile settings objects the game thread reads each tick. Saves run on a thread-pool task (`thread:background`), so the render thread never does file I/O ([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L935-L953)). See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Add Tomlyn and one cached options instance.** `TomlSerializerOptions` compiles mapping metadata on first use, so create it once. `SnakeCaseLower` maps `MemoryMb` to `memory_mb`:

   ```csharp
   // gatOS.GameMod/Configuration/GatOsConfig.cs L18-L27
   public sealed class GatOsConfig
   {
       /// <summary>The config schema version this build reads and writes.</summary>
       public const int CurrentSchema = 1;

       // One cached options instance: TomlSerializerOptions compiles mapping metadata on first use.
       private static readonly TomlSerializerOptions TomlOptions = new()
       {
           PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
       };
   ```

2. **Write properties with defaults.** Every property on the class has an initialiser, so a missing key keeps its default and an unknown key is ignored (per the code comment, verified by the author against Tomlyn 2.6.0). A `[[mounts]]` array of tables is a `List<MountSpec>` property ([gatOS.GameMod/Configuration/GatOsConfig.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Configuration/GatOsConfig.cs#L548)).

3. **Ship a commented template and seed once.** The mod folder contains `gatos.default.toml`; if the live file does not exist it is copied from there so pre-launch edits of the template carry over, and if that fails fresh defaults are written:

   ```csharp
   // gatOS.GameMod/Configuration/GatOsConfig.cs L557-L592
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
   ```

4. **Deserialize; on any failure keep the file and use defaults.** A parse error or an unknown schema value returns in-memory defaults and logs; `LoadOrCreate` itself never writes in those cases:

   ```csharp
   // gatOS.GameMod/Configuration/GatOsConfig.cs L594-L619
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

5. **Clamp to a bootable range.** `Normalize()` clamps each numeric knob and logs every correction (for example `memory_mb` 128..8192, `cpus` 1..16, `disk_size_gb` 1..128); see [Save from Task.Run with an atomic helper, clamp bad values on load and leave an unparseable file alone](../write-and-load-config-files-safely/write-and-load-config-files-safely-3.md).

6. **Write a grouped, commented file from a `Sections` table.** Tomlyn renders every value; the table only controls grouping, order and inline docs, and a catch-all emits any key the table forgot, so adding a property never drops it from the file:

   ```csharp
   // gatOS.GameMod/Configuration/GatOsConfig.cs L43-L61
   // The file layout: each serialized key is emitted under a section header with a short comment.
   // Common knobs come first so a player never has to scroll past the advanced surface. Tomlyn
   // still renders every value (quoting/escaping/bools) — this table only controls grouping,
   // ordering, and the inline docs. A key the table forgets still ships (see Serialize's catch-all),
   // so adding a property can never silently drop it from the file.
   private static readonly (string Title, (string Key, string Comment)[] Keys)[] Sections =
   {
       ("COMMON — most players only need to touch these", new[]
       {
           ("memory_mb", "Guest RAM in MiB. Bump it if you install heavy software."),
           ("cpus", "Guest virtual CPU count."),
           ("disk_size_gb",
               "Guest disk size in GiB (clamped 1..128). Grow-only: raising it expands the active\n"
               + "save's disk on the next boot; lowering it is a no-op (use Reset Disk to reclaim space)."),
           ("restrict_network", "true = guest gets no internet, only the gatOS channels (off = apk works)."),
           ("accel_override", "Force one accelerator: \"whpx\" | \"kvm\" | \"hvf\" | \"tcg\" (\"\" = auto ladder)."),
           ("cpu_model", "Override the guest CPU model (\"\" = auto; WHPX needs a named model, not host)."),
           ("boot_timeout_seconds", "Overall boot timeout in seconds; 0 = automatic (60 s accelerated / 300 s TCG)."),
       }),
   ```

   ```csharp
   // gatOS.GameMod/Configuration/GatOsConfig.cs L648-L708
   public string Serialize()
   {
       // Index the rendered scalar lines by key so we can re-emit them in section order. Tomlyn owns
       // the value formatting; we never reformat a scalar ourselves.
       var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
       foreach (var line in TomlSerializer.Serialize(this, TomlOptions)
                    .Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
       {
           var eq = line.IndexOf('=');
           if (eq > 0)
               byKey[line[..eq].Trim()] = line;
       }

       // Tomlyn renders the list as an inline `mounts = [...]` scalar; the MOUNTS section renders it
       // in block form below, so drop the inline line here.
       byKey.Remove("mounts");

       var sb = new StringBuilder();
       sb.Append(FileHeader);

       // schema sits above the sections: it is the file-format version, not a tunable.
       if (byKey.Remove("schema", out var schemaLine))
           sb.Append('\n').Append(schemaLine).Append('\n');

       foreach (var (title, keys) in Sections)
       {
           sb.Append("\n# ===== ").Append(title).Append(" =====\n");
           foreach (var (key, comment) in keys)
           {
               if (!byKey.Remove(key, out var valueLine))
                   continue; // property removed from the class since this table was written

               sb.Append('\n');
               foreach (var commentLine in comment.Split('\n'))
                   sb.Append("# ").Append(commentLine).Append('\n');
               sb.Append(valueLine).Append('\n');
           }
       }

       // Host folder mounts: always the help block, then one [[mounts]] table per entry.
       sb.Append("\n# ===== ").Append(MountsSectionTitle).Append(" =====\n\n");
       sb.Append(MountsHelp).Append('\n');
       foreach (var mount in Mounts)
       {
           sb.Append("\n[[mounts]]\n");
           sb.Append("name = ").Append(RenderTomlString(mount.Name)).Append('\n');
           sb.Append("path = ").Append(RenderTomlString(mount.Path)).Append('\n');
           sb.Append("read_only = ").Append(mount.ReadOnly ? "true" : "false").Append('\n');
       }

       // Catch-all: any key the section table did not place (e.g. a freshly added property) still
       // ships, so the generated file is always complete even if this table lags the class.
       if (byKey.Count > 0)
       {
           sb.Append("\n# ===== OTHER =====\n\n");
           foreach (var line in byKey.Values)
               sb.Append(line).Append('\n');
       }

       return sb.ToString();
   }
   ```

7. **Write atomically and off the render thread.** `Save` goes through `AtomicFile` (temp file then `File.Move(..., overwrite: true)`), called from `Task.Run` by `PersistConfig`:

   ```csharp
   // gatOS.GameMod/Configuration/AtomicFile.cs L10-L15
   public static void WriteAllText(string path, string contents)
   {
       var tmp = path + ".tmp";
       File.WriteAllText(tmp, contents);
       File.Move(tmp, path, overwrite: true);
   }
   ```

   ```csharp
   // gatOS.GameMod/Mod.cs L939-L953
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

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Configuration/GatOsConfig.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Configuration/GatOsConfig.cs) | Typed config, load-or-seed, normalize, sectioned serialize |
| [gatOS.GameMod/Configuration/AtomicFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Configuration/AtomicFile.cs) | Temp-and-rename write |
| [gatOS.Vm/GatOsPaths.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/GatOsPaths.cs#L21-L48) | Config and data folder paths |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L225-L236) and [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L935-L953) | Load at AllModsLoaded; background persist |

## Variations and gotchas

- **Fallback is not protection.** `LoadOrCreate` leaves a broken file alone, but `PersistConfig` later saves the in-memory defaults (`config.Save(GatOsPaths.ConfigFile)`) as soon as a live setting changes, which replaces the file the player was told to fix.
- **Overlapping saves.** `AtomicFile` uses the fixed temp name `gatos.toml.tmp` and `PersistConfig` takes no lock, so two saves started close together would target the same temp file and one of them can fail; the failure is caught and logged. This is inferred from the code, not observed.
- **Keep the template in sync.** A new property needs an entry in the `Sections` table for a comment; the catch-all still emits it.
- **Dependency.** The mod ships Tomlyn; see [Ship third-party DLLs next to your mod](../../../getting-started/dependencies/ship-third-party-dlls-with-a-mod/overview.md) and the hand-parsed alternative [Read and write a one-key settings.toml by hand, with no TOML library](persist-mod-settings-toml-3.md).

## See also

- [Persist a structured TOML config with a do-not-touch table, legacy migration and one shared instance](persist-mod-settings-toml-2.md), [Read and write a one-key settings.toml by hand, with no TOML library](persist-mod-settings-toml-3.md), [overview](./overview.md)
- [TOML config seeded from a shipped template](../../../../mods/gatos/features/toml-config-seeded-template.md)
- [Write config files atomically and load hand-edited ones without losing them](../write-and-load-config-files-safely/overview.md), [Find where KSA keeps user data and choose where a mod stores its config and data files](../../user-data/choose-where-a-mod-stores-its-files/overview.md)
