# Game Feature

* [Persist mod settings as JSON with System.Text.Json](overview.md) - Serialise a settings object to a JSON file under the KSA documents folder, load it before the UI is built and write only when something changed - a two-second snapshot compare, or a debounced dirty flag with override-only colors

# How-To

* [Autosave settings.json by comparing a serialised snapshot every two seconds](persist-mod-settings-as-json-1.md) - Capture the live static settings into a DTO, serialise it every two seconds from the draw loop and write only when the JSON differs from the last write, so a clean load never writes and late-constructed sections still get their saved state
* [Persist a settings object with override-only colors and tuning, saved by a debounced dirty flag](persist-mod-settings-as-json-2.md) - Store a settings wrapper with settings, colors and tuning sections where colors and tuning keep only values that differ from the compiled defaults, accept comments and trailing commas, and save after a second of quiet and again at StarMapUnload
