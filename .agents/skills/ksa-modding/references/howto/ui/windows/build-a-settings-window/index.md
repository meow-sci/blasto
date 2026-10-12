# Game Feature

* [Build a mod settings window (and developer panel)](overview.md) - KSA has no settings-window API for mods, so six mods show six ImGui layouts - tabbed tables with colour pickers, label-gutter forms, ColorButton popups with INI, small auto-sized windows with tooltips, an XKCD colour catalogue and a developer panel

# How-To

* [Settings window: a developer panel opened from a menu, ModMenu or a floating button](build-a-settings-window-6.md) - Build a closable control panel that always has a way back - a floating reopen button - and give it simulation-speed buttons that go below the game's own 0.1x floor through Universe.SetSimulationSpeed
* [Settings window: a menu entry, a small auto-sized window and wrapped tooltips](build-a-settings-window-4.md) - Offer a few settings in a small AlwaysAutoResize window with a version-proof id, wrapped hover tooltips, a keyboard-safe flag set and expensive settings applied only when the slider is released
* [Settings window: a reflected XKCD colour catalogue behind a filterable picker](build-a-settings-window-5.md) - Reflect once over KSAColor.Xkcd to get a sorted, cached list of named colours and use it in a filter combo with swatches beside a ColorEdit4, keeping the colour and the name in step
* [Settings window: ColorButton popups and a hand-parsed INI file](build-a-settings-window-3.md) - Build a compact HUD settings window from buttons, sliders and swatch buttons that open ColorPicker4 popups, and load and save the values with a tiny INI reader and writer
* [Settings window: label-gutter form tables and full-width widgets](build-a-settings-window-2.md) - Lay out a settings form as a two-column table with a fixed label gutter, full-width widgets, a filter combo and a red destructive button, so several sections of one dialog line up
* [Settings window: slider-plus-input rows, ColorPicker4 on a float4 and Reset buttons](build-a-settings-window-1.md) - Edit a profile of float parameters with a reusable slider-and-input table row, colour pickers bound directly to float4 fields, per-effect enable checkboxes, Reset buttons and derived values
