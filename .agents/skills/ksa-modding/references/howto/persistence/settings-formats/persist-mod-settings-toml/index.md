# Game Feature

* [Persist mod settings as TOML](overview.md) - Store settings in a TOML file a player can read and edit - through the Tomlyn serializer with snake_case names, a structured POCO with a do-not-touch table and legacy migration, or a few dozen lines of hand parsing when the mod must not carry dependencies

# How-To

* [Map one flat settings class to gatos.toml with Tomlyn and snake_case names](persist-mod-settings-toml-1.md) - Deserialize a flat POCO with TomlSerializer and a snake_case naming policy, seed the live file from a commented template on first run, write a sectioned commented layout from a Sections table, and save from Task.Run
* [Persist a structured TOML config with a do-not-touch table, legacy migration and one shared instance](persist-mod-settings-toml-2.md) - Split user-editable settings from UI state into two TOML tables, load a structured or an older flat file through the Tomlyn DOM, write atomically, and keep a single loaded config instance so a second writer cannot save stale values
* [Read and write a one-key settings.toml by hand, with no TOML library](persist-mod-settings-toml-3.md) - Resolve the game's documents folder by reflection, read a key = value file with a tiny line parser that ignores unknown keys and bad values, and write a commented template on each change so a reflection-only mod needs no references
