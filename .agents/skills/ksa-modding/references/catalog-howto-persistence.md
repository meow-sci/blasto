# Persistence

Where a mod keeps its files, how it reads and writes settings (ini, json, toml), and how it ties its own data to KSA's native saves - detecting loads and saves, sidecars, scene records, stable ids, window layouts and user data folders.

## Saves

Tying mod data to KSA's native saves: saving and loading from code, detecting loads, sidecars, scene records, stable ids and keeping mod craft out of saves.

### [Detect a save load or quickload and reset mod state safely](howto/persistence/saves/detect-save-load/overview.md)

What a load destroys and keeps, the signals a mod can watch, and six ways mods react - latch on OnGameLoaded, pin the target, observe UncompressedSave.Load, postfix DeserializeSave, infer from the clock, or rebuild from the live world

- [Latch Program.OnGameLoaded and forget the old world in the next frame step](howto/persistence/saves/detect-save-load/detect-save-load-1.md) — intermediate
- [Pin the OnGameLoaded signature at compile time so a game update breaks the build, not the patch](howto/persistence/saves/detect-save-load/detect-save-load-2.md) — intermediate
- [Observe UncompressedSave.Load and reset a registry of save-lifetime caches](howto/persistence/saves/detect-save-load/detect-save-load-3.md) — advanced
- [Postfix Universe.DeserializeSave when the CelestialSystem object survives a load](howto/persistence/saves/detect-save-load/detect-save-load-4.md) — intermediate
- [Infer a reload from universe time running backwards](howto/persistence/saves/detect-save-load/detect-save-load-5.md) — intermediate
- [Skip detection: rebuild a derived registry from the live world every frame](howto/persistence/saves/detect-save-load/detect-save-load-6.md) — intermediate

### [Identify vessels and parts stably across saves](howto/persistence/saves/identify-vessels-and-parts-across-saves/overview.md)

Which vessel and part identifiers survive a save and load and which do not, and four ways mods key their data - Vehicle.Id with rename and dispose hooks, display-name keys, a window layout that can only anchor on the vehicle id, and a structural part address with a topology hash

- [Key mod data on Vehicle.Id and follow renames and disposals with postfixes](howto/persistence/saves/identify-vessels-and-parts-across-saves/identify-vessels-and-parts-across-saves-1.md) — intermediate
- [Key settings on the craft display name and warn when two craft share it](howto/persistence/saves/identify-vessels-and-parts-across-saves/identify-vessels-and-parts-across-saves-2.md) — beginner
- [Anchor a saved window layout on Vehicle.Id and accept best-effort part ids](howto/persistence/saves/identify-vessels-and-parts-across-saves/identify-vessels-and-parts-across-saves-3.md) — intermediate
- [Reference a specific part with tree paths and a topology hash, resolving to null instead of guessing](howto/persistence/saves/identify-vessels-and-parts-across-saves/identify-vessels-and-parts-across-saves-4.md) — advanced

### [Keep mod-only vehicles out of save files](howto/persistence/saves/keep-mod-vehicles-out-of-saves/overview.md)

Why a save that names a part template from a missing mod cannot be opened without it, and the postfix on Universe.SerializeSystems that filters a mod's own craft out of the serialised universe

- [Filter your own craft out of Universe.SerializeSystems](howto/persistence/saves/keep-mod-vehicles-out-of-saves/keep-mod-vehicles-out-of-saves.md) — intermediate

### [Create and load native KSA saves from mod code](howto/persistence/saves/save-and-load-the-game-from-code/overview.md)

The game methods that write and read a save, and three ways mods call them - a timed autosave, a command-file load that waits for a craft, and a scenario runner that loads after startup

- [Write timed native saves with SaveName.TryAccept and UncompressedSave.Make](howto/persistence/saves/save-and-load-the-game-from-code/save-and-load-the-game-from-code-1.md) — intermediate
- [Load a save from a command file and wait until a craft is flown again](howto/persistence/saves/save-and-load-the-game-from-code/save-and-load-the-game-from-code-2.md) — intermediate
- [Load a save a few seconds after start from an unattended scenario runner](howto/persistence/saves/save-and-load-the-game-from-code/save-and-load-the-game-from-code-3.md) — advanced

### [Store per-save mod data next to a KSA save and notice when the player saves](howto/persistence/saves/save-mod-state-with-the-game-save/overview.md)

Why per-save mod data lives in a sidecar, where the sidecar can go, how to detect that the game wrote a save, and the three layouts mods use - JSON in the save folder, a per-save store written on Make, and TOML in the mod folder keyed by save and vehicle

- [Keep a JSON sidecar inside the save folder and write it when universe.xml changes](howto/persistence/saves/save-mod-state-with-the-game-save/save-mod-state-with-the-game-save-1.md) — intermediate
- [Write a per-save settings store from a postfix on UncompressedSave.Make](howto/persistence/saves/save-mod-state-with-the-game-save/save-mod-state-with-the-game-save-2.md) — intermediate
- [Keep TOML in the mod folder keyed by (save id, vehicle id) and rekey it on save-as](howto/persistence/saves/save-mod-state-with-the-game-save/save-mod-state-with-the-game-save-3.md) — advanced

### [Save a whole mod's scene state as versioned sidecar records, with reset and replay on load](howto/persistence/saves/save-mod-state-with-the-scene/overview.md)

How to persist many features with native objects to rebuild - versioned JSON records per feature, validated before the world is touched, reset in reverse order and replayed in order - and the patches that make it work

- [Persist feature state as ISaveParticipant records through a coordinator that retains what it cannot restore](howto/persistence/saves/save-mod-state-with-the-scene/save-mod-state-with-the-scene-1.md) — advanced
- [Pair each scene capture with its native write by object identity](howto/persistence/saves/save-mod-state-with-the-scene/save-mod-state-with-the-scene-2.md) — advanced
- [Hook native save and load with five patches, eight callbacks and a deferred world change](howto/persistence/saves/save-mod-state-with-the-scene/save-mod-state-with-the-scene-3.md) — advanced

## Settings formats

Settings files in ini, JSON and TOML, written atomically and loaded without losing hand edits.

### [Persist mod settings as JSON with System.Text.Json](howto/persistence/settings-formats/persist-mod-settings-as-json/overview.md)

Serialise a settings object to a JSON file under the KSA documents folder, load it before the UI is built and write only when something changed - a two-second snapshot compare, or a debounced dirty flag with override-only colors

- [Autosave settings.json by comparing a serialised snapshot every two seconds](howto/persistence/settings-formats/persist-mod-settings-as-json/persist-mod-settings-as-json-1.md) — intermediate
- [Persist a settings object with override-only colors and tuning, saved by a debounced dirty flag](howto/persistence/settings-formats/persist-mod-settings-as-json/persist-mod-settings-as-json-2.md) — intermediate

### [Persist mod settings in a hand-rolled ini file](howto/persistence/settings-formats/persist-mod-settings-ini/overview.md)

A flat key=value settings file with no library - where to read it, what ImGui already remembers, and four write policies - on every change, load first, an explicit Save button, or a dirty flag flushed on close and unload

- [Rewrite a flat settings.ini on every widget change](howto/persistence/settings-formats/persist-mod-settings-ini/persist-mod-settings-ini-1.md) — beginner
- [Load settings first at StarMapImmediateLoad and rewrite on change](howto/persistence/settings-formats/persist-mod-settings-ini/persist-mod-settings-ini-2.md) — beginner
- [Save an eleven-key hud.ini only when the player presses a button](howto/persistence/settings-formats/persist-mod-settings-ini/persist-mod-settings-ini-3.md) — beginner
- [Write a sectioned palette file when a dirty flag is set, on window close and at unload](howto/persistence/settings-formats/persist-mod-settings-ini/persist-mod-settings-ini-4.md) — intermediate

### [Persist mod settings as TOML](howto/persistence/settings-formats/persist-mod-settings-toml/overview.md)

Store settings in a TOML file a player can read and edit - through the Tomlyn serializer with snake_case names, a structured POCO with a do-not-touch table and legacy migration, or a few dozen lines of hand parsing when the mod must not carry dependencies

- [Map one flat settings class to gatos.toml with Tomlyn and snake_case names](howto/persistence/settings-formats/persist-mod-settings-toml/persist-mod-settings-toml-1.md) — intermediate
- [Persist a structured TOML config with a do-not-touch table, legacy migration and one shared instance](howto/persistence/settings-formats/persist-mod-settings-toml/persist-mod-settings-toml-2.md) — intermediate
- [Read and write a one-key settings.toml by hand, with no TOML library](howto/persistence/settings-formats/persist-mod-settings-toml/persist-mod-settings-toml-3.md) — beginner

### [Write config files atomically and load hand-edited ones without losing them](howto/persistence/settings-formats/write-and-load-config-files-safely/overview.md)

Write rules (atomic replace, save on change, debounce, flush at unload) and load rules (clamp, drop the broken field, migrate, never overwrite an unparseable file) for mod config files

- [Mark dirty, debounce, swap the file in atomically, and retry deserialisation by dropping the broken field](howto/persistence/settings-formats/write-and-load-config-files-safely/write-and-load-config-files-safely-1.md) — intermediate
- [Skip the dirty flag by comparing the serialised snapshot with the last write](howto/persistence/settings-formats/write-and-load-config-files-safely/write-and-load-config-files-safely-2.md) — beginner
- [Save from Task.Run with an atomic helper, clamp bad values on load and leave an unparseable file alone](howto/persistence/settings-formats/write-and-load-config-files-safely/write-and-load-config-files-safely-3.md) — intermediate
- [Flush on close and unload, skip junk keys on load and migrate the previous format](howto/persistence/settings-formats/write-and-load-config-files-safely/write-and-load-config-files-safely-4.md) — intermediate

## User data

Where a mod keeps its files and what players create: data folders, presets and content files, and ImGui window layouts.

### [Find where KSA keeps user data and choose where a mod stores its config and data files](howto/persistence/user-data/choose-where-a-mod-stores-its-files/overview.md)

The folder map derived from Constants.DocumentsFolderPath and how StarMap redirects it, then five placements for a mod's own files - beside the DLL, a subfolder of the documents folder, a separate data folder, a path derived from the Logs folder, or the instance-aware constant

- [Keep settings beside the mod DLL, resolved from the loaded assembly](howto/persistence/user-data/choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-1.md) — beginner
- [Store config in a mod-named subfolder under Constants.DocumentsFolderPath](howto/persistence/user-data/choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-2.md) — beginner
- [Keep user data in Documents/mods/<mod> apart from the install, and seed the live config from a template](howto/persistence/user-data/choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-3.md) — intermediate
- [Derive the data folder from the mod's log folder, with a temp-directory fallback](howto/persistence/user-data/choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-4.md) — beginner
- [Know the folder map and redirect it with a StarMap instance path](howto/persistence/user-data/choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-5.md) — advanced

### [Persist ImGui window state and layouts](howto/persistence/user-data/persist-imgui-window-state/overview.md)

What Dear ImGui's ini remembers for a window and what it never does, then three ways to keep window state - a plain ImGui.Begin plus a saved open flag, a filtered ini section of your own, and named layouts of every live window applied on request

- [Let imgui.ini restore window geometry with a plain ImGui.Begin and persist the open flag yourself](howto/persistence/user-data/persist-imgui-window-state/persist-imgui-window-state-1.md) — beginner
- [Persist only your own ImGui window section with SaveIniSettingsToMemory and LoadIniSettingsFromMemory](howto/persistence/user-data/persist-imgui-window-state/persist-imgui-window-state-2.md) — intermediate
- [Save every live window as a named layout in one TOML file and apply it only on request](howto/persistence/user-data/persist-imgui-window-state/persist-imgui-window-state-3.md) — advanced

### [Persist user-created palettes, presets and content files](howto/persistence/user-data/persist-user-created-presets-and-content/overview.md)

Store what the player creates rather than what the mod configures - named color palettes, theme files, override-only color tables, exported XML presets, versioned layout files anchored to a body, and imported media copied into a catalog folder

- [Keep named, editable color palettes in a sectioned file with a ColorEdit4 round trip](howto/persistence/user-data/persist-user-created-presets-and-content/persist-user-created-presets-and-content-1.md) — intermediate
- [Load bundled and user color themes from TOML files and let the user's theme win a name clash](howto/persistence/user-data/persist-user-created-presets-and-content/persist-user-created-presets-and-content-2.md) — intermediate
- [Store only the colors a player changed, keyed by field name, in the main config](howto/persistence/user-data/persist-user-created-presets-and-content/persist-user-created-presets-and-content-3.md) — intermediate
- [Export effect presets as trimmed XML and load them back as a mod's own asset type](howto/persistence/user-data/persist-user-created-presets-and-content/persist-user-created-presets-and-content-4.md) — advanced
- [Save a user-built layout as a versioned snake_case JSON file anchored to a body by latitude and longitude](howto/persistence/user-data/persist-user-created-presets-and-content/persist-user-created-presets-and-content-5.md) — advanced
- [Import user files into a catalog folder and refer to them by name](howto/persistence/user-data/persist-user-created-presets-and-content/persist-user-created-presets-and-content-6.md) — intermediate
