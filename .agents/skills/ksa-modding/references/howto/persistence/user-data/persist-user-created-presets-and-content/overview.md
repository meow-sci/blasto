---
type: Game Feature
title: Persist user-created palettes, presets and content files
description: Store what the player creates rather than what the mod configures - named color palettes, theme files, override-only color tables, exported XML presets, versioned layout files anchored to a body, and imported media copied into a catalog folder
category: persistence
group: user-data
topic: persist-user-created-presets-and-content
demonstrated_by:
  - mods/ksa-measuretools/features/color-palettes-persistence
  - mods/purrtty/features/toml-themes-and-settings
  - mods/ksa-public-telemetry/features/json-config-in-documents
  - mods/ksa-public-telemetry/features/live-tuning-and-palette-reflection
  - mods/mpfx/features/xml-effect-profiles
  - mods/ksagolf/features/course-grid-and-save-file
  - mods/unscience/features/lib-shared-file-library
lifecycle: [startup:all-mods-loaded, frame:draw-ui, event:starmap-unload, startup:asset-load]
game_types:
  - Brutal.ImGuiApi.ImGui
  - KSA.SerializedId
  - KSA.IKeyed
  - KSA.ModLibrary
  - KSA.Celestial
  - KSA.Universe
  - KSA.CelestialSystem
tags: [presets, palettes, themes, user-content, import, export, json, toml, xml, versioning]
---
Settings are what a mod configures; presets and content are what the player makes: a color scheme they tuned, a theme they named, a layout they built on a planet, a PNG they imported. These need names, lists, a way to delete and overwrite safely, and a file format that survives mod and game updates. KSA offers no preset store for mods, so each of the six approaches below owns its files. They differ in what is stored (colors, a bundle of window appearance, an effect profile, a placed layout, a media file) and in who writes the file (the mod on save, the player by hand, or an export-and-paste flow).

## Engine background

The game contributes only a few pieces, and every approach leans on a different one:

| Piece | Where | Used by |
|---|---|---|
| `Constants.DocumentsFolderPath`: parent of every per-user path, redirected per launch by StarMap | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281) | Approach 3 (the config folder); approach 2 recomputes the same folder from `MyDocuments` |
| `ImGui.ColorEdit3` / `ColorEdit4` and `ImGuiColorEditFlags`: float color pickers | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7110-L7121) | Approaches 1, 3 |
| `SerializedId`, `IKeyed`: the XML asset base type and its key, with `OnDataLoad(Mod)` | [current/decomp/KSA/SerializedId.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedId.cs#L6-L31) | Approach 4 |
| `ModLibrary.LocalModsFolderPath` (`<docs>/mods`) | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178) | Approach 4 (an unused save directory) |
| `Celestial.GetDirCcfFromLatLon`: latitude/longitude to a body-fixed direction | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L667-L675) | Approach 5 |
| `Universe.CurrentSystem` / `CelestialSystem`: finding a body by id | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs) | Approach 5 |

Timing is shared: read presets at `startup:all-mods-loaded` (or `startup:asset-load` for XML assets), edit and save from the UI pass (`frame:draw-ui`), and do final saves at `[StarMapUnload]`. StarMap removes all Harmony patches before it invokes `[StarMapUnload]`, so the instance-path redirect of the documents folder is gone by then and a path recomputed from `Constants.DocumentsFolderPath` at unload points at the default Documents folder; resolve the folder once at load, or derive it from the assembly location, as approach 1 does. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Find where KSA keeps user data and choose where a mod stores its config and data files](../choose-where-a-mod-stores-its-files/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Named, editable color palettes in a sectioned file with a ColorEdit4 round trip](./persist-user-created-presets-and-content-1.md) | KSA-MeasureTools | `imgui-window`, `starmap-lifecycle` | `startup:all-mods-loaded`, `frame:draw-ui`, `event:starmap-unload` | Shipped plus user palettes, restore by name; the file sits beside the DLL and is lost when the folder is wiped |
| [Bundled and user theme TOML files, user wins on a name clash](./persist-user-created-presets-and-content-2.md) | purrTTY | `toml-config` | `startup:all-mods-loaded`, `frame:draw-ui` | Hand-editable, shareable alacritty format and a display name stored inside; a typo hides the theme, and the path ignores the instance redirect |
| [Override-only color table in the main config](./persist-user-created-presets-and-content-3.md) | KSA-Public-Telemetry | `json-config`, `reflection` | `startup:all-mods-loaded`, `frame:draw-ui`, `event:starmap-unload` | Changed defaults reach players who never customised; no named presets, and field names are the schema |
| [Export effect presets as trimmed XML and load them back as an asset type](./persist-user-created-presets-and-content-4.md) | MPFX | `xml-asset`, `imgui-window` | `startup:asset-load`, `frame:draw-ui` | Presets ship in any mod's asset XML; nothing is written back, the player pastes the export by hand |
| [Versioned snake_case JSON layout anchored by latitude, longitude and ground height](./persist-user-created-presets-and-content-5.md) | KSAGolf | `json-config` | `frame:draw-ui` | Survives terrain changes with a drift warning; unknown ids are dropped, newer files refused |
| [Import user files into a catalog folder and refer to them by name](./persist-user-created-presets-and-content-6.md) | unscience | `save-sidecar`, `imgui-window` | `frame:draw-ui` | Saved scenes reproduce a file by name; copying blocks a frame and duplicates are name-based |

Choosing between them:

- **A fixed set of tunable colors**: approach 3 when there are no named presets, approach 1 when there are.
- **A bundle the player shares or edits by hand**: approach 2, with a documented format.
- **A preset that other mods may ship**: approach 4, because it rides the game's asset XML loading.
- **A thing the player builds in the world**: approach 5, with a version and a ground-relative anchor.
- **Media the player brings**: approach 6, then store only the catalog name.

## See also

- [Persist mod settings as JSON with System.Text.Json](../../settings-formats/persist-mod-settings-as-json/overview.md), [Persist mod settings as TOML](../../settings-formats/persist-mod-settings-toml/overview.md), [Write config files atomically and load hand-edited ones without losing them](../../settings-formats/write-and-load-config-files-safely/overview.md)
- [Save a whole mod's scene state as versioned sidecar records, with reset and replay on load](../../saves/save-mod-state-with-the-scene/overview.md), [Find where KSA keeps user data and choose where a mod stores its config and data files](../choose-where-a-mod-stores-its-files/overview.md)
- [Build a mod settings window (and developer panel)](../../../ui/windows/build-a-settings-window/overview.md), [Build searchable lists and browsers](../../../ui/windows/build-searchable-lists-and-browsers/overview.md)
- [Weld or anchor one vessel to another](../../../physics/kinematics/weld-one-vessel-to-another/overview.md), [Place static objects on a planet surface](../../../celestials/terrain/place-static-objects-on-a-planet-surface/overview.md)
