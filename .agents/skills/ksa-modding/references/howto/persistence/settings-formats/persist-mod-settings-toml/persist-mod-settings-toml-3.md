---
type: How-To
title: Read and write a one-key settings.toml by hand, with no TOML library
description: Resolve the game's documents folder by reflection, read a key = value file with a tiny line parser that ignores unknown keys and bad values, and write a commented template on each change so a reflection-only mod needs no references
category: persistence
group: settings-formats
topic: persist-mod-settings-toml
approach: 3
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/realatmospheres/features/modmenu-page-and-toml-settings]
techniques: [toml-config, reflection]
hooks: [reflection KSA.Constants.DocumentsFolderPath]
lifecycle: [frame:draw-ui]
game_types: [KSA.Constants]
tags: [settings, toml, hand-parsing, reflection, no-dependencies, documents-folder]
---

Outcome: one player option, "Procedural haze bands", persisted in `RealAtmospheres/settings.toml` under the KSA documents folder with no TOML package and no compile-time reference to the game. It is a menu item on a Real Atmospheres page when ModMenu is installed, and a line in the file otherwise. Demonstrated by [ModMenu page and TOML settings](../../../../mods/realatmospheres/features/modmenu-page-and-toml-settings.md). Use this shape when the mod is built without game references (see [Build a KSA mod that references only StarMap.API and Harmony](../../../getting-started/build/build-a-mod-without-game-references/overview.md)) or must not carry dependencies.

## Background: how the game does it

The folder comes from `Constants.DocumentsFolderPath`, which is `Documents/My Games/Kitten Space Agency` ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)). Reading it through the property getter matters: StarMap prefixes that getter to return an instance folder when `-InstancePath` or `STARMAP_INSTANCE_PATH` is set, and the settings file follows ([StarMap.Core/Patches/DocumentsPathPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/DocumentsPathPatches.cs#L12-L31)). RealAtmospheres has no reference to `KSA.dll`, so it finds the type and property by name with `AccessTools` (reflection).

## Lifecycle and timing

`Settings.Load` runs once per session, from the first place that needs the value: the menu page draw (`frame:draw-ui`, drawn by ModMenu while its menu is open; ModMenu-side timing is unverified here) or the atmosphere prefix on first use if no menu was ever drawn. The write happens inside the click handler on the same thread, because a menu click is a discrete event rather than a drag. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Resolve the path by reflection and cache it.** A failure is logged once and means "settings are not kept":

   ```csharp
   // Settings.cs L21-L44
   private const string HazeBandsKey = "haze_bands";

   private static string? _path;
   private static bool _loaded;

   /// <summary>The file, under the game's documents folder (KSA.Constants.DocumentsFolderPath).</summary>
   internal static string? FilePath
   {
       get
       {
           if (_path != null) return _path;
           try
           {
               Type? constants = AccessTools.TypeByName("KSA.Constants");
               if (AccessTools.Property(constants, "DocumentsFolderPath")?.GetValue(null) is string documents)
                   _path = Path.Combine(documents, "RealAtmospheres", "settings.toml");
           }
           catch (Exception ex)
           {
               ShadowBuilder.Log("WARN: the game's documents folder was not found; settings are not kept: " + ex.Message);
           }
           return _path;
       }
   }
   ```

2. **Read once; defaults stand for anything the file does not say.** A missing file or an exception keeps `DefaultHazeBands`:

   ```csharp
   // Settings.cs L46-L66
   /// <summary>Reads the file once; the defaults stand for anything it does not say.</summary>
   internal static void Load()
   {
       if (_loaded) return;
       _loaded = true;
       string? path = FilePath;
       if (path == null || !File.Exists(path))
       {
           ShadowBuilder.Log($"settings: the defaults ({Summary()})");
           return;
       }
       try
       {
           HazeBands = Read(File.ReadAllLines(path));
           ShadowBuilder.Log($"settings from {path}: {Summary()}");
       }
       catch (Exception ex)
       {
           ShadowBuilder.Log("WARN: the settings file could not be read, the defaults stand: " + ex.Message);
       }
   }
   ```

3. **Parse with a minimal line parser.** Blank lines and `#` comments return `null`; a trailing `# comment` after a value is stripped; unknown keys and values that fail `bool.TryParse` are ignored:

   ```csharp
   // Settings.cs L68-L89
   /// <summary>The haze bands' switch as a settings file has it; the default if it does not say.</summary>
   internal static bool Read(IEnumerable<string> lines)
   {
       bool bands = DefaultHazeBands;
       foreach (string line in lines)
           if (Parse(line) is (string key, string value) && key == HazeBandsKey && bool.TryParse(value, out bool on))
               bands = on;
       return bands;
   }

   /// <summary>A line's key and value, or null for a comment, a blank or anything else.</summary>
   internal static (string Key, string Value)? Parse(string line)
   {
       string s = line.Trim();
       if (s.Length == 0 || s[0] == '#') return null;
       int eq = s.IndexOf('=');
       if (eq <= 0) return null;
       string value = s[(eq + 1)..];
       int hash = value.IndexOf('#');
       if (hash >= 0) value = value[..hash];
       return (s[..eq].Trim(), value.Trim());
   }
   ```

4. **Write the whole file on change** (and only if the value differs), creating the folder first. The text is a raw-string template with the current value substituted:

   ```csharp
   // Settings.cs L93-L118
   /// <summary>Sets the switch and writes the file: a click, not a drag, so at once.</summary>
   internal static void SetHazeBands(bool bands)
   {
       Load();
       if (bands == HazeBands) return;
       HazeBands = bands;
       string? path = FilePath;
       if (path == null) return;
       try
       {
           Directory.CreateDirectory(Path.GetDirectoryName(path)!);
           File.WriteAllText(path, Text());
       }
       catch (Exception ex)
       {
           ShadowBuilder.Log("WARN: the settings file could not be written: " + ex.Message);
       }
   }

   internal static string Text() => $"""
       # Real Atmospheres settings, written by its page in the game's Mods menu (ModMenu). Safe to edit with the game closed.

       # The drifting bands in the haze of Pluto, Triton and Titan: true or false. False leaves their haze smooth.
       {HazeBandsKey} = {(HazeBands ? "true" : "false")}

       """;
   ```

5. **Call it from the one place the value changes.** `Menu.Draw` flips the setting with the vendored ModMenu entry; the atmosphere prefix reads `Settings.HazeBands` each frame. Both run on the main thread, so a plain static is enough.

## Complete example

| File | Role |
|---|---|
| [Settings.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Settings.cs) | File location, parse, write |
| [Menu.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Menu.cs#L12-L35) | The ModMenu page that calls `SetHazeBands` |

## Variations and gotchas

- **It is a line parser, not TOML.** Tables, arrays, quoted strings with `#`, multi-line strings and escaped quotes are not handled; it is correct for a flat file of booleans and numbers. The first `#` after the `=` is treated as a comment even inside a quoted string.
- **Whole-file rewrite, not atomic, not merged.** `File.WriteAllText` rewrites the template with only the known keys, so a key added by hand or by a newer version is lost on the next write. Fine for one key.
- **No migration or versioning.** Adding a second key means new `Read` branches and a longer template.
- **Edit with the game closed** is the file's own advice: it is read once.
- When a real TOML library is acceptable, [Map one flat settings class to gatos.toml with Tomlyn and snake_case names](persist-mod-settings-toml-1.md) and [Persist a structured TOML config with a do-not-touch table, legacy migration and one shared instance](persist-mod-settings-toml-2.md) give tolerant parsing, typed classes and nested tables.

## See also

- [Map one flat settings class to gatos.toml with Tomlyn and snake_case names](persist-mod-settings-toml-1.md), [Persist a structured TOML config with a do-not-touch table, legacy migration and one shared instance](persist-mod-settings-toml-2.md), [overview](./overview.md)
- [ModMenu page and TOML settings](../../../../mods/realatmospheres/features/modmenu-page-and-toml-settings.md)
- [Find where KSA keeps user data and choose where a mod stores its config and data files](../../user-data/choose-where-a-mod-stores-its-files/overview.md), [Build a mod settings window (and developer panel)](../../../ui/windows/build-a-settings-window/overview.md), [Weld or anchor one vessel to another](../../../physics/kinematics/weld-one-vessel-to-another/overview.md)
