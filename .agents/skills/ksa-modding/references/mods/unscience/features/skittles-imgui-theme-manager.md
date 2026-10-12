---
type: Mod Feature
title: Global ImGui themes by rewriting ImGui.GetStyle(), with TOML theme files and scene-saved styles
description: Skittles re-themes every KSA and mod window by writing the shared ImGuiStyle through ImGui.GetStyle(), keeps themes as Tomlyn TOML files, wraps ImGui.ShowStyleEditor and needs no Harmony patch
mod: unscience
submod: skittles
category: ui
techniques: [imgui-window, toml-config, save-sidecar]
hooks:
  - starmap StarMapAllModsLoaded (ThemeManager.Initialize captures the default style and applies the configured theme)
  - starmap StarMapAfterGui (picker in ISubmod.RenderContent, editor window in RenderFloatingWindows)
  - starmap StarMapUnload (ISubmod.Dispose restores the captured default)
  - unscience save participant skittles (order 5) via the shared native save hooks
lifecycle: [startup:all-mods-loaded, frame:draw-ui, event:save, event:load, frame:prepare]
game_types:
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImGuiStylePtr
  - Brutal.ImGuiApi.ImGuiStyle
  - Brutal.ImGuiApi.ImGuiCol
  - KSA.Program
  - KSA.FontManager
  - KSA.KSAColor
tags: [imgui, theme, style, toml, tomlyn, style-editor, ui-customization, scene-saves]
---

Skittles changes the look of the whole game UI at once: every KSA window, menu and control, and every
mod window. Pick a theme from a filterable list (Game Default, ImGui's Dark, Light and Classic, the
shipped "Inanimate Carbon Rod" green-on-black terminal look, or your own), open the full Dear ImGui
style editor to tweak any of the 60 colours and the sizes, roundings and spacings live, and save the
result under a name. The last theme is re-applied at the next start, a KSA save remembers the exact
style in use, and unloading the mod puts the original style back.[^skittles-readme][^skittles-lib-readme]

Mechanism in one line: Dear ImGui keeps one global `ImGuiStyle` that every widget reads at submission
time, and Brutal exposes it as `ImGui.GetStyle()` returning an `ImGuiStylePtr` with `ref` properties,
so assigning its fields (including the 60-entry `Colors` inline array) re-themes everything without a
Harmony patch.[^theme-definition][^imgui-styleptr]

## How it works

**Snapshot type** ([skittles.lib/ThemeDefinition.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/skittles.lib/ThemeDefinition.cs)).
A plain class holding `Colors` as 60 `float[4]` indexed by `(int)ImGuiCol`, 35 float style vars,
15 `float2` vars stored as `float[2]`, and 3 anti-aliasing bools. `CaptureFromImGui()` copies them
out of `ImGui.GetStyle()`; `ApplyToImGui()` writes them back field by field.[^theme-definition]

**Manager** ([skittles.lib/ThemeManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/skittles.lib/ThemeManager.cs)).

1. `Initialize()` captures the current style as "Game Default" before touching anything, creates
   `<My Documents>/My Games/Kitten Space Agency/skittles/themes`, writes
   `inanimate-carbon-rod.toml` from `BuiltInThemes.CarbonRod()` if missing, loads `config.toml`,
   lists themes, and applies the configured `active_theme` if it still exists.[^theme-manager][^built-in-themes]
2. `ApplyTheme(name)`: "Game Default" re-applies the snapshot; "Dark", "Light" and "Classic" call
   `ImGui.StyleColorsDark/Light/Classic()` (null destination = the current style), which only sets
   colours, then re-apply the snapshot's float style vars; any other name loads its TOML file and
   applies it. Every apply writes `active_theme` to `config.toml`.
3. `SaveCurrentAsTheme(name)` slugifies the name into `themes/<slug>.toml`, captures the live style
   (including editor changes) and makes it the active theme. `DeleteTheme` removes a custom file and
   falls back to Game Default if it was active.
4. `RefreshThemeList()` lists the four built-ins first, then each `*.toml` file, named from the
   `name` line of its `[meta]` table by a small line scanner, sorted case-insensitively.

**File format** ([skittles.lib/ThemeSerializer.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/skittles.lib/ThemeSerializer.cs)).
Tomlyn 0.19 `TomlTable` models: `[meta]` (name, description), `[colors]` (60 keys named after the
`ImGuiCol` members, each a four-number array rounded to two decimals) and `[style]` (floats, two-number
arrays, bools). Reading is lenient: unknown colour names are skipped, missing keys keep the class
defaults, numbers are parsed with the invariant culture, and a parse failure logs Tomlyn's
diagnostics and returns null.[^theme-serializer][^skittles-csproj] `config.toml` holds only
`active_theme`.[^mod-config]

**UI** ([skittles.lib/SkittlesSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/skittles.lib/SkittlesSubmod.cs)).
The panel shows the active name, a filterable combo that applies on selection, "Open Theme Editor"
and a Delete button for custom themes (a confirmation modal opened outside the table's ID stack).
The editor is a separate floating window (`RenderFloatingWindows`) with "Save <name>", "Save as New"
and `ImGui.ShowStyleEditor()` with no reference style, so it edits the live global style.[^skittles-submod]

**Scene saves** ([skittles.lib/SkittlesSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/skittles.lib/SkittlesSubmod.Saves.cs)).
Participant `skittles`, order 5. Capture serialises a full `ThemeDefinition` of the live style, so
unsaved editor tweaks are kept. Restore applies it and labels the active theme "Saved scene style".
Reset applies a baseline captured once, when the unscience host built its participant list right after
every submod's `Initialize` (so: the user's configured startup theme). Validation rejects a colour
array that is not 60 by 4 finite numbers, any non-finite float or a `float[]` that is not two finite
numbers, an `Alpha` outside 0.05 to 1 and non-positive tessellation values, so a corrupt record cannot
make the UI invisible.[^skittles-saves][^unscience-saves]

## Lifecycle and timing

| Step | Hook | Phase |
|---|---|---|
| Capture "Game Default", apply startup theme | `ISubmod.Initialize` from `[StarMapAllModsLoaded]` | `startup:all-mods-loaded` |
| Apply, save, delete, style editor | `RenderContent` / `RenderFloatingWindows` from `[StarMapAfterGui]` | `frame:draw-ui` |
| Capture / reset and restore | Native save; native load at the `PrepareFrame` handoff | `event:save`, `event:load`, `frame:prepare` |
| Restore the captured default | `ISubmod.Dispose` from `[StarMapUnload]` | unload |

`ImGui.CreateContext()` runs in the `Program` constructor (L842) before mods are loaded (L899), so the
style exists when `Initialize` captures it.[^ksa-program] A change made from `[StarMapAfterGui]`
(the postfix of `OnDrawUiViewports`) affects widgets submitted after it in the same ImGui frame, such
as the game's `OnDrawUiThreadSafe` windows; windows drawn earlier pick it up on the next frame.

How the game sets its own style in build 5554: the managed game code never calls `StyleColors*` and
never assigns `ImGuiStyle` colours; the only global style write found is
`FontManager.LoadFont` setting `FontScaleMain = 1` (L102). "Game Default" is therefore Dear ImGui's
context default as created at L842. The game's distinctive looks come from per-widget overrides:
`PushStyleColor` appears on 137 lines across 32 files of the `KSA` namespace (for example
`ConsoleStyle`), and those pushes win over any global theme for the widgets they wrap.[^ksa-fontmanager][^ksa-consolestyle]

## Key code

| Path | Role |
|---|---|
| [skittles.lib/ThemeDefinition.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/skittles.lib/ThemeDefinition.cs#L81-L239) | Capture from and apply to `ImGui.GetStyle()` |
| [skittles.lib/ThemeManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/skittles.lib/ThemeManager.cs#L29-L126) | Paths, initialisation, apply |
| [skittles.lib/ThemeManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/skittles.lib/ThemeManager.cs#L128-L260) | Save, delete, list, restore, built-in style vars |
| [skittles.lib/ThemeSerializer.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/skittles.lib/ThemeSerializer.cs) | TOML mapping with named colour keys |
| [skittles.lib/ModConfig.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/skittles.lib/ModConfig.cs) | `active_theme` preference |
| [skittles.lib/BuiltInThemes.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/skittles.lib/BuiltInThemes.cs) | Inanimate Carbon Rod preset |
| [skittles.lib/SkittlesSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/skittles.lib/SkittlesSubmod.cs) | Picker, delete modal, editor window, `Dispose` |
| [skittles.lib/SkittlesSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/skittles.lib/SkittlesSubmod.Saves.cs) | Scene-save participant and validation |

## Snippets

Start-up order: capture first, then theme:

```csharp
// skittles.lib/ThemeManager.cs L38-L72
    public void Initialize()
    {
        // 1. Capture game default style BEFORE any theming
        DefaultTheme = ThemeDefinition.CaptureFromImGui();
        DefaultTheme.Name = "Game Default";
        DefaultTheme.Description = "The game's original default style";

        // 2. Ensure directories exist
        Directory.CreateDirectory(_themesDirectory);

        // 3. Ship the Inanimate Carbon Rod preset if not present
        string icr_path = Path.Combine(_themesDirectory, "inanimate-carbon-rod.toml");
        if (!File.Exists(icr_path))
        {
            var icr = BuiltInThemes.CarbonRod();
            ThemeSerializer.SaveToFile(icr, icr_path);
            Console.WriteLine("skittles: Shipped Inanimate Carbon Rod preset");
        }

        // 4. Load config
        Config = ModConfigSerializer.LoadFromFile(_configFilePath);

        // 5. Discover themes
        RefreshThemeList();

        // 6. Apply startup theme
        if (!string.IsNullOrEmpty(Config.ActiveThemeName))
        {
            bool found = AvailableThemes.Any(t => t.Name == Config.ActiveThemeName);
            if (found)
            {
                ApplyTheme(Config.ActiveThemeName);
            }
        }
    }
```

Built-in schemes, files and the persisted choice:

```csharp
// skittles.lib/ThemeManager.cs L83-L119
            if (themeName == "Game Default")
            {
                DefaultTheme?.ApplyToImGui();
            }
            else if (themeName == "Dark")
            {
                ImGui.StyleColorsDark();
                ApplyDefaultStyleVars();
            }
            else if (themeName == "Light")
            {
                ImGui.StyleColorsLight();
                ApplyDefaultStyleVars();
            }
            else if (themeName == "Classic")
            {
                ImGui.StyleColorsClassic();
                ApplyDefaultStyleVars();
            }
            else
            {
                var entry = AvailableThemes.FirstOrDefault(t => t.Name == themeName);
                if (entry?.FilePath != null)
                {
                    var theme = ThemeSerializer.LoadFromFile(entry.FilePath);
                    theme?.ApplyToImGui();
                }
                else
                {
                    Console.WriteLine($"skittles: Theme '{themeName}' not found");
                    return;
                }
            }

            ActiveThemeName = themeName;
            Config.ActiveThemeName = themeName;
            ModConfigSerializer.SaveToFile(Config, _configFilePath);
```

Writing the global style (colours via the inline array):

```csharp
// skittles.lib/ThemeDefinition.cs L170-L183
    public void ApplyToImGui()
    {
        ImGuiStylePtr style = ImGui.GetStyle();

        // Colors
        for (int i = 0; i < 60; i++)
        {
            float[] c = Colors[i];
            style.Colors[i] = new float4(c[0], c[1], c[2], c[3]);
        }

        // Float vars
        style.Alpha = Alpha;
        style.DisabledAlpha = DisabledAlpha;
        // ...
```

The scene-save participant and its guard rails:

```csharp
// skittles.lib/SkittlesSubmod.Saves.cs L12-L39
    public IEnumerable<ISaveParticipant> SaveParticipants
    {
        get
        {
            // The user's configured startup theme remains the baseline for native saves without state.
            ThemeDefinition baseline = ThemeDefinition.CaptureFromImGui();
            yield return new SaveParticipant<ThemeDefinition>("skittles", ThemeDefinition.CaptureFromImGui,
                () => { baseline.ApplyToImGui(); _sceneThemeRestored = false; },
                (theme, _) => { theme.ApplyToImGui(); _sceneThemeRestored = true; }, order: 5, validate: ValidateSavedTheme);
        }
    }

    private static void ValidateSavedTheme(ThemeDefinition theme)
    {
        if (theme.Colors == null || theme.Colors.Length != 60 || theme.Colors.Any(c => c == null || c.Length != 4 || c.Any(v => !float.IsFinite(v))))
            throw new InvalidOperationException("Invalid saved theme colors.");
        // ThemeDefinition is an explicit detached DTO. Validate its scalar/array style surface before native ImGui writes.
        foreach (var property in typeof(ThemeDefinition).GetProperties())
        {
            object? value = property.GetValue(theme);
            if (property.PropertyType == typeof(float) && (value is not float scalar || !float.IsFinite(scalar)))
                throw new InvalidOperationException($"Invalid theme value {property.Name}.");
            if (property.PropertyType == typeof(float[]) && (value is not float[] vector || vector.Length != 2 || vector.Any(v => !float.IsFinite(v))))
                throw new InvalidOperationException($"Invalid theme vector {property.Name}.");
        }
        if (theme.Alpha < .05f || theme.Alpha > 1 || theme.CurveTessellationTol <= 0 || theme.CircleTessellationMaxError <= 0)
            throw new InvalidOperationException("Saved theme would hide the UI or invalidate tessellation.");
    }
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `ImGui.GetStyle()` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5448-L5451) | The global style pointer |
| `ImGui.ShowStyleEditor(ImGuiStylePtr ref = default)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5538-L5541) | Editor window body |
| `ImGui.StyleColorsDark/Light/Classic(ImGuiStylePtr dst = default)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5569-L5582) | Built-in colour schemes |
| `ImGuiStylePtr` `ref` properties, `Colors` (`float4_60`) | [current/decomp/Brutal.ImGuiApi/ImGuiStylePtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiStylePtr.cs#L131) | Field-level read and write |
| `ImGuiStyle.Colors` | [current/decomp/Brutal.ImGuiApi/ImGuiStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiStyle.cs#L188) | Native layout behind the pointer |
| `ImGuiCol` (`Text` .. `ModalWindowDimBg`, `COUNT`) | [current/decomp/Brutal.ImGuiApi/ImGuiCol.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiCol.cs#L5-L65) | 60 colour slots in 5554 |
| `Program` constructor `ImGui.CreateContext()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L842-L853) | Where the default style comes from |
| `FontManager.LoadFont` (`FontScaleMain = 1`) | [current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L100-L102) | The only global style write in game code |
| `ConsoleStyle` | [current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs) | Example of per-widget `PushStyleColor` overrides |

## Pitfalls and version notes

- **Hard-coded 60 colours and a fixed member list.** A Dear ImGui update that adds an `ImGuiCol`
  slot or style member would be silently ignored rather than crash; watch `ImGuiCol.COUNT` and
  `ImGuiStyle` on every Brutal update.[^scope-ui]
- **Members not captured** (5554): `FontSizeBase`, `FontScaleMain`, `FontScaleDpi`,
  `WindowMenuButtonPosition`, `TreeLinesFlags`, `ColorButtonPosition`, the hover delays and hover
  flags. Font scaling stays under the game's control (good), but editor changes to those fields are
  not saved in themes or scene saves.[^imgui-styleptr]
- **Per-widget pushes win.** The game's own `PushStyleColor` calls (and any mod's) override the theme
  for the widgets they wrap, so parts of the KSA UI keep their colours.
- **Built-in schemes only reset colours.** After "Dark", "Light" or "Classic" the snapshot's float vars
  are restored, but `float2` and bool vars keep whatever the previous theme set.[^theme-manager]
- **Colours are rounded to two decimals in TOML**, so a save-and-reload round trip shifts colours
  slightly; scene saves keep full precision (JSON).[^theme-serializer]
- **Slug collisions overwrite.** "My Theme" and "my-theme" map to the same file.
- **Scene reset baseline is fixed at host start.** Loading a save without a skittles record restores
  the start-up theme, not whatever was active just before the load.[^skittles-saves]
- **Standalone host gap.** `skittles/Mod.cs` never calls `RenderFloatingWindows`, so "Open Theme
  Editor" does nothing in the standalone development host; unscience calls it every frame.[^skittles-mod]
- **README drift.** The README lists quick-apply buttons (Dark, Light, Classic, Rod, Reset) that the
  current panel does not have, the library README names `BuiltInThemes.InanimateCarbonRod()` while the
  method is `CarbonRod()`, and part of the README is leftover template text.[^skittles-readme][^skittles-lib-readme]
- Theme files live under `My Games/Kitten Space Agency/skittles/`, not the `.unscience` folder the
  other sub-mods use, and the path comes from `SpecialFolder.MyDocuments`.[^theme-manager]
- 5482 and earlier checks found `ImGuiStyle`, `ImGuiStylePtr` and `ImGuiCol` unchanged; the Brutal
  decomp folders were identical at 5482.[^scope-ui]

## See also

- [Supermod host](./unscience-supermod-host.md) (calls `RenderFloatingWindows` for every sub-mod)
- [ISubmod contract](./lib-isubmod-contract.md)
- [Hotkey guard](./lib-hotkey-guard.md) (typing a theme name does not trigger game keys)
- [Scene save coordinator](./lib-scene-save-coordinator.md)
- [Skittles sub-mod](../submods/skittles.md) and the [mod overview](../overview.md)
- [Re-theme every KSA and mod window through the global ImGuiStyle (capture, restore and TOML presets)](../../../howto/ui/windows/match-the-stock-console-ui-style/match-the-stock-console-ui-style-4.md) - how-to article demonstrating this feature

[^theme-manager]: skittles.lib/ThemeManager.cs
[^theme-definition]: skittles.lib/ThemeDefinition.cs
[^theme-serializer]: skittles.lib/ThemeSerializer.cs
[^mod-config]: skittles.lib/ModConfig.cs
[^built-in-themes]: skittles.lib/BuiltInThemes.cs
[^skittles-submod]: skittles.lib/SkittlesSubmod.cs
[^skittles-saves]: skittles.lib/SkittlesSubmod.Saves.cs
[^skittles-csproj]: skittles.lib/skittles.lib.csproj
[^skittles-mod]: skittles/Mod.cs
[^skittles-readme]: skittles/README.md
[^skittles-lib-readme]: skittles.lib/README.md
[^unscience-saves]: unscience/UnscienceSaves.cs
[^scope-ui]: scope/ui-customization.md
[^imgui-styleptr]: Brutal.ImGuiApi.ImGuiStylePtr (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-fontmanager]: KSA.FontManager (decompiled, build 2026.10.10.5554)
[^ksa-consolestyle]: KSA.ConsoleStyle (decompiled, build 2026.10.10.5554)
