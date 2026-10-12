# Game Feature

* [Persist mod settings in a hand-rolled ini file](overview.md) - A flat key=value settings file with no library - where to read it, what ImGui already remembers, and four write policies - on every change, load first, an explicit Save button, or a dirty flag flushed on close and unload

# How-To

* [Load settings first at StarMapImmediateLoad and rewrite on change](persist-mod-settings-ini-2.md) - Read settings.ini before patches and UI exist so the first frame already knows the saved options, clamp colors to 0 to 1, keep the default for each malformed value and save when a pointer option or the window flag changes
* [Rewrite a flat settings.ini on every widget change](persist-mod-settings-ini-1.md) - Keep every option in one Key=Value file next to the mod DLL, read it once at AllModsLoaded with TryParse and default constants, create it from defaults when missing, and call SaveSettings from each changed widget
* [Save an eleven-key hud.ini only when the player presses a button](persist-mod-settings-ini-3.md) - Read hud.ini with a switch on the key, parse enums with Enum.TryParse and colors as r,g,b,a floats, recompute derived values after loading, and write only from a Save HUD Settings button
* [Write a sectioned palette file when a dirty flag is set, on window close and at unload](persist-mod-settings-ini-4.md) - Keep named color palettes in colors.cfg with one [Name] section each, skip unknown keys and malformed values on load, migrate the older flat format, and save only when a dirty flag is set at window close and StarMapUnload
