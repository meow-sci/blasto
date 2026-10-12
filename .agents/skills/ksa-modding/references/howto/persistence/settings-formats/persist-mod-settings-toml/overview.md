---
type: Game Feature
title: Persist mod settings as TOML
description: Store settings in a TOML file a player can read and edit - through the Tomlyn serializer with snake_case names, a structured POCO with a do-not-touch table and legacy migration, or a few dozen lines of hand parsing when the mod must not carry dependencies
category: persistence
group: settings-formats
topic: persist-mod-settings-toml
demonstrated_by:
  - mods/gatos/features/toml-config-seeded-template
  - mods/purrtty/features/toml-themes-and-settings
  - mods/realatmospheres/features/modmenu-page-and-toml-settings
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Constants, KSA.GameSettings]
tags: [settings, toml, tomlyn, config, documents-folder, hand-parsing]
---

TOML is the format KSA itself uses for player settings, and it is comfortable to hand-edit. Three mods persist their settings as TOML in three ways: a serializer onto a flat class, a structured class with a migration, and no TOML library at all.

## Engine background

KSA's own settings are `settings.toml` in the documents folder: `GameSettings.SettingsFilePath` is `Path.Combine(Constants.DocumentsFolderPath, "settings.toml")` ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1382), [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)). `GameSettings.LoadFromFile` parses it with `TomlParser`, maps it with `TomletMain.To<GameSettings>` (with `IgnoreInvalidEnumValues = true`), and rewrites it with `Current.Save()`; `Save` serialises with `TomletMain.TomlStringFrom` and `File.WriteAllText` ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L2826-L2870), [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L2922-L2934)). If the file is missing it is created with defaults; if loading throws, the game logs "failed to load settings file, using default settings".

Mods that write TOML in this repository's reference set use Tomlyn (gatOS 2.6.0, purrTTY 2.3.0) and ship their own copy ([gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj#L129), [purrTTY.Display/purrTTY.Display.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/purrTTY.Display.csproj#L28); see [Ship third-party DLLs next to your mod](../../../getting-started/dependencies/ship-third-party-dlls-with-a-mod/overview.md)); I did not verify whether mods can rely on the game's own TOML library. Where the file lives and how to choose between the install folder, the documents folder and a data folder is [Find where KSA keeps user data and choose where a mod stores its config and data files](../../user-data/choose-where-a-mod-stores-its-files/overview.md).

Hook placement is the same for all three: read once at `startup:all-mods-loaded` or earlier (no game state needed), write from the UI pass (`frame:draw-ui`) or a background task. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Tomlyn TomlSerializer onto one flat POCO with snake_case names](./persist-mod-settings-toml-1.md) | gatOS | `toml-config` | `startup:all-mods-loaded`, `thread:background` | Missing keys keep defaults, unknown ignored; a template file seeds the live one; needs the Tomlyn package |
| [Structured POCO with a do-not-touch table, legacy migration and a single shared instance](./persist-mod-settings-toml-2.md) | purrTTY | `toml-config` | `startup:all-mods-loaded`, `frame:draw-ui` | Separates user settings from UI state and migrates an older file; one shared instance is a rule, not a safeguard |
| [Hand-parsed settings.toml with no TOML library](./persist-mod-settings-toml-3.md) | RealAtmospheres | `toml-config` | `frame:draw-ui` | No dependency, fits a reflection-only mod; one key, a line parser rather than TOML |

## See also

- [Persist mod settings as JSON with System.Text.Json](../persist-mod-settings-as-json/overview.md), [Persist mod settings in a hand-rolled ini file](../persist-mod-settings-ini/overview.md), [Write config files atomically and load hand-edited ones without losing them](../write-and-load-config-files-safely/overview.md)
- [Find where KSA keeps user data and choose where a mod stores its config and data files](../../user-data/choose-where-a-mod-stores-its-files/overview.md), [Build a mod settings window (and developer panel)](../../../ui/windows/build-a-settings-window/overview.md), [Weld or anchor one vessel to another](../../../physics/kinematics/weld-one-vessel-to-another/overview.md)
