---
type: Mod Feature
title: Live tuning window and palette by reflection
description: Exposes every public static float and int of a Tuning class and every colour field of a style class through reflection, so a debug window can edit layout and animation constants live, save only the overrides, and print the changed values back as C# source
mod: ksa-public-telemetry
category: testing-and-tooling
techniques: [reflection, imgui-window, json-config]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui]
tags: [tooling, reflection, tuning, debug-ui, colours, imgui]
---

The author "got sick of recompiling the mod with every little tweak", so all layout and animation
numbers live as public static fields in one class, and an in-game "Overlay tuning" window edits them
live ([Rendering/Tuning.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Tuning.cs#L6-L9)). The same reflection pattern backs the colour editors. It is a
cheap, reusable recipe for iterating on visuals inside KSA without a hot-reload system.

## How it works

1. **Fields are the schema.** `Tuning` declares about 50 `public static float`/`int` fields (intro stage
   timings, shelf geometry, margins, engine-dot sizes, fade times)
   ([Rendering/Tuning.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Tuning.cs#L11-L73)). At type initialisation it reflects them
   (`GetFields(Public | Static)`, non-literal, float or int, sorted by name) and snapshots their
   initial values as `_defaults` ([Rendering/Tuning.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Tuning.cs#L75-L84)).
2. **Overrides only.** `CaptureOverrides` returns just the fields whose value differs from the default;
   `ApplyOverrides` resets everything and applies a name-to-value map, ignoring unknown names. These are
   the `tuning` section of `config.json` ([Rendering/Tuning.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Tuning.cs#L86-L130)).
3. **Back to source.** "Copy as source" builds lines like `public static float BoxHeight = 177f;   // changed`
   for every field, prints them to the console and puts them on the clipboard with
   `ImGui.SetClipboardText`, ready to paste into `Tuning.cs` as the new defaults
   ([Rendering/Tuning.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Tuning.cs#L132-L150), [Rendering/TuningUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/TuningUi.cs#L59-L65)).
4. **The window.** `TuningUi` draws `ImGui.Begin("Overlay tuning")` with `SliderFloat`/`SliderInt` bound by
   `ref` directly to the static fields, grouped in collapsing headers, plus "Replay intro" and "Reset
   all"; any change marks the config dirty ([Rendering/TuningUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/TuningUi.cs#L23-L172)). It is opened from the
   settings' Advanced section.
5. **Palette.** `OverlayStyle` holds mutable `public static uint` colours initialised from `const`
   defaults in a nested `Defaults` class. `OverlayPalette` reflects both (fields vs constants via
   `GetRawConstantValue`) to get, set, reset and diff colours by name and to serialise them as
   `#RRGGBBAA` ([Config/OverlayPalette.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/OverlayPalette.cs#L6-L141), [Rendering/OverlayStyle.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayStyle.cs#L5-L53)). The
   settings UI exposes 18 of them through `ImGui.ColorEdit3` keyed by `nameof(...)`
   ([Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs#L25-L45), [Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs#L469-L497)).

## Lifecycle and timing

- **Overrides applied at `startup:all-mods-loaded`** by `ConfigStore.Load`, before any panel reads them
  ([Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs#L72-L73)).
- **Edited in `frame:draw-ui`.** The tuning window is drawn from `DrawEverything` when the game UI is
  visible ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L122-L126)). Because panels read the static fields every frame,
  edits show on the next draw with no rebuild.

## Key code

| Path | Role |
|---|---|
| [Rendering/Tuning.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Tuning.cs#L75-L184) | Reflection, overrides, source dump |
| [Rendering/TuningUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/TuningUi.cs#L40-L172) | Slider window over static fields |
| [Config/OverlayPalette.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/OverlayPalette.cs#L6-L141) | Colour fields and defaults by reflection |

## Snippets

```csharp
// Rendering/Tuning.cs L75-L110
    private static readonly FieldInfo[] _fields = typeof(Tuning)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => !f.IsLiteral && (f.FieldType == typeof(float) || f.FieldType == typeof(int)))
        .OrderBy(f => f.Name, StringComparer.Ordinal)
        .ToArray();

    private static readonly Dictionary<string, double> _defaults =
        _fields.ToDictionary(f => f.Name, Read, StringComparer.Ordinal);

    public static IReadOnlyList<string> Names { get; } = _fields.Select(f => f.Name).ToArray();

    public static void ResetAll()
    {
        for (int i = 0; i < _fields.Length; i++)
        {
            Write(_fields[i], _defaults[_fields[i].Name]);
        }
    }

    public static Dictionary<string, double> CaptureOverrides()
    {
        Dictionary<string, double> overrides = new(StringComparer.Ordinal);

        for (int i = 0; i < _fields.Length; i++)
        {
            FieldInfo field = _fields[i];
            double value = Read(field);

            if (value != _defaults[field.Name])
            {
                overrides[field.Name] = value;
            }
        }

        return overrides;
    }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ImGui.SetClipboardText`, `SliderFloat`, `SliderInt`, `ColorEdit3`, `CollapsingHeader` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7110), [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7260) | Brutal ImGui bindings used by the window |

## Pitfalls and version notes

- **Defaults are whatever the field held at type init.** If anything writes a field before `Tuning` is
  first touched, that value becomes the "default". Here `ConfigStore.Load` calls `ResetAll` or
  `ApplyOverrides` first, which forces initialisation before any write.
- **Float round-trip.** Values go through `double` in the config; comparisons with the default use exact
  equality, so a slider nudged back by hand may still count as "changed".
- **Reflection on static fields** is slow compared with direct access, but it only runs on load, save and
  dump; the per-frame path reads the fields directly.

## See also

- [JSON config in the KSA documents folder](json-config-in-documents.md)
- [Staged intro and outro animation](intro-outro-animation.md)
- [ModMenu entry with a standalone fallback window](modmenu-entry-with-standalone-fallback.md)
- [Persist a settings object with override-only colors and tuning, saved by a debounced dirty flag](../../../howto/persistence/settings-formats/persist-mod-settings-as-json/persist-mod-settings-as-json-2.md) - how-to article demonstrating this feature
- [Store only the colors a player changed, keyed by field name, in the main config](../../../howto/persistence/user-data/persist-user-created-presets-and-content/persist-user-created-presets-and-content-3.md) - how-to article demonstrating this feature
- [Edit static constants with reflected sliders, keep only the overrides and copy them back as source](../../../howto/testing-and-tooling/in-game-testing/live-tweak-constants-in-game/live-tweak-constants-in-game.md) - how-to article demonstrating this feature

