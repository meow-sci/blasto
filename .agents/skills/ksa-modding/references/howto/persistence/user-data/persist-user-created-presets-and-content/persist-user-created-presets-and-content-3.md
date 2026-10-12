---
type: How-To
title: Store only the colors a player changed, keyed by field name, in the main config
description: Keep a mod's whole color scheme as public static fields, find them by reflection, and write only the values that differ from the compiled defaults as
category: persistence
group: user-data
topic: persist-user-created-presets-and-content
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksa-public-telemetry/features/json-config-in-documents
  - mods/ksa-public-telemetry/features/live-tuning-and-palette-reflection
techniques: [json-config, reflection, imgui-window]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterGui, starmap StarMapUnload]
lifecycle: [startup:all-mods-loaded, frame:draw-ui, event:starmap-unload]
game_types: [KSA.Constants, Brutal.ImGuiApi.ImGui]
tags: [colors, overrides, reflection, json, config, palette, defaults]
---
Outcome: a color scheme the player can edit in a settings window and that survives restarts, stored as a small `colors` object inside the mod's `config.json` that lists only the colors that differ from the defaults shipped in the code. A release that changes a default color therefore reaches every player who never touched that color, and adding a new color needs no file migration. KSA-Public-Telemetry keeps its 22 overlay colors this way and edits 18 of them through `ImGui.ColorEdit3`. Demonstrated by [JSON config in the KSA documents folder](../../../../mods/ksa-public-telemetry/features/json-config-in-documents.md) and [Live tuning window and palette by reflection](../../../../mods/ksa-public-telemetry/features/live-tuning-and-palette-reflection.md). The config file's load, debounce and atomic-write behavior is covered by [Persist a settings object with override-only colors and tuning, saved by a debounced dirty flag](../../settings-formats/persist-mod-settings-as-json/persist-mod-settings-as-json-2.md); this article is the color-table half.

## Background: how the game does it

Nothing in the game stores mod colors. The pieces the approach touches are the documents folder the file lives under (`Constants.DocumentsFolderPath`, [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)) and the ImGui color widgets (`ImGui.ColorEdit3`, [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7110)), which edit a `float3` in place. Everything else is plain .NET reflection over the mod's own static class.

## Lifecycle and timing

| Moment | What | Why there |
|---|---|---|
| `startup:all-mods-loaded` | `ConfigStore.Load` calls `OverlayPalette.ApplyOverrides` before any panel reads a color | `ApplyOverrides` first resets every field to its default, then applies the file, so reflection initialises the defaults table before anything is written |
| `frame:draw-ui` | `SettingsUi.DrawColorEditors` edits fields; any change calls `ConfigStore.MarkDirty` | Panels read the static fields every frame, so an edit shows on the next draw |
| `event:starmap-unload` | `ConfigStore.SaveNow` | Final flush for edits within the debounce window |

One trap belongs to the unload row: `ConfigStore.DirectoryPath` is an expression-bodied property over `Constants.DocumentsFolderPath`, so it follows StarMap's instance-path redirect while it is active, but StarMap removes every Harmony patch (including that redirect) before it invokes `[StarMapUnload]`. A launch with an instance path therefore writes the final save to the default Documents folder. The remedy (resolve and keep the folder during `[StarMapAllModsLoaded]`) is described in [Find where KSA keeps user data and choose where a mod stores its config and data files](../choose-where-a-mod-stores-its-files/overview.md); see also the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Make the color scheme one class of public static fields, with a nested class of constants.** The fields are what the renderer reads; the constants are the defaults that never change at runtime:

   ```csharp
   // Rendering/OverlayStyle.cs L5-L13, L29-L36, L51-L53
   public static class OverlayStyle
   {
       public static uint PanelBackground = Defaults.PanelBackground;
       public static uint Hairline        = Defaults.Hairline;
       public static uint TextPrimary = Defaults.TextPrimary;
       public static uint TextMuted   = Defaults.TextMuted;
       public static uint TextDim     = Defaults.TextDim;
       public static uint TextShadow  = Defaults.TextShadow;
       public static uint EngineNominal   = Defaults.EngineNominal;
   // ...
       public static class Defaults
       {
           public const uint PanelBackground = 0x99080A0Cu;
           public const uint Hairline        = 0x59FFFFFFu;
           public const uint TextPrimary = 0xFFFFFFFFu;
           public const uint TextMuted   = 0xFFB9C4CCu;
           public const uint TextDim     = 0xFF7A868Fu;
           public const uint TextShadow  = 0x8C000000u;
   // ...
           public const uint Caution = 0xFF5CC8FFu;
           public const uint Critical = 0xFF4A4AF5u;
       }
   ```

2. **Find the fields and the defaults by reflection, once.** The editable set is every public static non-literal `uint` field; the defaults come from the constants of the same name via `GetRawConstantValue()`:

   ```csharp
   // Config/OverlayPalette.cs L8-L40
   private static readonly FieldInfo[] _fields = typeof(OverlayStyle)
       .GetFields(BindingFlags.Public | BindingFlags.Static)
       .Where(f => f.FieldType == typeof(uint) && !f.IsLiteral)
       .OrderBy(f => f.Name, StringComparer.Ordinal)
       .ToArray();

   private static readonly Dictionary<string, uint> _defaults = typeof(OverlayStyle.Defaults)
       .GetFields(BindingFlags.Public | BindingFlags.Static)
       .Where(f => f.FieldType == typeof(uint))
       .ToDictionary(f => f.Name, f => (uint)f.GetRawConstantValue()!, StringComparer.Ordinal);

   public static IReadOnlyList<string> Names { get; } =
       _fields.Select(f => f.Name).ToArray();

   public static uint Get(string name)
   {
       FieldInfo? field = Find(name);
       return field is null ? 0u : (uint)field.GetValue(null)!;
   }

   public static void Set(string name, uint value)
       => Find(name)?.SetValue(null, value);

   public static bool IsDefault(string name)
       => _defaults.TryGetValue(name, out uint d) && Get(name) == d;

   public static void Reset(string name)
   {
       if (_defaults.TryGetValue(name, out uint d))
       {
           Set(name, d);
       }
   }
   ```

3. **Capture only what differs.** `CaptureOverrides` walks the field names and emits `#RRGGBBAA` for each non-default one, so an untouched scheme serialises to an empty object:

   ```csharp
   // Config/OverlayPalette.cs L49-L62
   public static Dictionary<string, string> CaptureOverrides()
   {
       Dictionary<string, string> overrides = new(StringComparer.Ordinal);

       foreach (string name in Names)
       {
           if (!IsDefault(name))
           {
               overrides[name] = ToHex(Get(name));
           }
       }

       return overrides;
   }
   ```

4. **Apply by resetting first, then overlaying.** Unknown names and unparseable values are ignored, so a renamed or removed color in a later release never breaks loading:

   ```csharp
   // Config/OverlayPalette.cs L64-L80
   public static void ApplyOverrides(Dictionary<string, string>? overrides)
   {
       ResetAll();

       if (overrides is null)
       {
           return;
       }

       foreach (KeyValuePair<string, string> entry in overrides)
       {
           if (Find(entry.Key) is not null && TryParseHex(entry.Value, out uint packed))
           {
               Set(entry.Key, packed);
           }
       }
   }
   ```

5. **Use a forgiving hex format.** The writer emits eight digits; the reader accepts `#RRGGBB` or `#RRGGBBAA`, with or without the hash, and defaults alpha to opaque. The packed value is ABGR (`a<<24 | b<<16 | g<<8 | r`), which is the byte order ImGui draw lists take:

   ```csharp
   // Config/OverlayPalette.cs L82-L127
   public static string ToHex(uint packed)
   {
       OverlayStyle.Unpack(packed, out float r, out float g, out float b, out byte alpha);

       return string.Create(
           CultureInfo.InvariantCulture,
           $"#{(byte)(r * 255f):X2}{(byte)(g * 255f):X2}{(byte)(b * 255f):X2}{alpha:X2}");
   }

   public static bool TryParseHex(string? text, out uint packed)
   {
       packed = 0u;

       if (string.IsNullOrWhiteSpace(text))
       {
           return false;
       }

       ReadOnlySpan<char> span = text.AsSpan().Trim();
       if (span.Length > 0 && span[0] == '#')
       {
           span = span[1..];
       }

       if (span.Length != 6 && span.Length != 8)
       {
           return false;
       }

       if (!byte.TryParse(span[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)
           || !byte.TryParse(span[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)
           || !byte.TryParse(span[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
       {
           return false;
       }

       byte a = 0xFF;
       if (span.Length == 8
           && !byte.TryParse(span[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a))
       {
           return false;
       }

       packed = ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;
       return true;
   }
   ```

6. **Edit through ColorEdit3 and keep alpha out of the picker.** The window lists display labels against `nameof(...)` field names, edits RGB, and writes back with `WithRgb`, which preserves the stored alpha. Alpha-heavy colors (shelf fades, dim plates) therefore keep the author's alpha unless the player edits the file by hand:

   ```csharp
   // Rendering/SettingsUi.cs L25-L45, L469-L497
   private static readonly (string Label, string Field)[] _editableColors =
   [
       ("Highlight / arc fill", nameof(OverlayStyle.ArcFill)),
       ("Arc track",            nameof(OverlayStyle.ArcTrack)),
       ("Caution",              nameof(OverlayStyle.Caution)),
       ("Critical",             nameof(OverlayStyle.Critical)),
       ("Primary text",         nameof(OverlayStyle.TextPrimary)),
       ("Secondary text",       nameof(OverlayStyle.TextMuted)),
       ("Dim text",             nameof(OverlayStyle.TextDim)),
       ("Engine - nominal",     nameof(OverlayStyle.EngineNominal)),
       ("Engine - throttled",   nameof(OverlayStyle.EngineThrottled)),
       ("Engine - armed",       nameof(OverlayStyle.EngineArmed)),
       ("Engine - starved",     nameof(OverlayStyle.EngineStarved)),
       ("Engine - inactive",    nameof(OverlayStyle.EngineInactive)),
       ("Backdrop fade",        nameof(OverlayStyle.ShelfBottom)),
       ("Shelf plates",         nameof(OverlayStyle.ShelfBox)),
       ("Gauge plate",          nameof(OverlayStyle.GaugePlate)),
       ("Gauge rim",            nameof(OverlayStyle.GaugeRim)),
       ("Timeline - elapsed",   nameof(OverlayStyle.TimelinePast)),
       ("Timeline - upcoming",  nameof(OverlayStyle.TimelineFuture)),
   ];
   ..
   private static bool DrawColorEditors()
   {
       bool changed = false;

       for (int i = 0; i < _editableColors.Length; i++)
       {
           (string label, string field) = _editableColors[i];

           uint packed = OverlayPalette.Get(field);
           OverlayStyle.Unpack(packed, out float r, out float g, out float b, out _);

           float3 rgb = new(r, g, b);

           ImGui.SetNextItemWidth(140f);
           if (ImGui.ColorEdit3(label, ref rgb, ImGuiColorEditFlags.NoInputs))
           {
               OverlayPalette.Set(field, OverlayStyle.WithRgb(packed, rgb.X, rgb.Y, rgb.Z));
               changed = true;
           }
       }

       if (ImGui.SmallButton("Reset colours"u8))
       {
           OverlayPalette.ResetAll();
           changed = true;
       }

       return changed;
   }
   ```

7. **Put the table in the one config document.** `ConfigStore` writes `settings`, `colors` and `tuning` sections together, with the temp-file-and-move write, and loads each section independently so a bad `colors` value cannot discard `settings`:

   ```csharp
   // Config/ConfigStore.cs L32-L42, L72-L73, L175-L186
   private sealed class ConfigFile
   {
       [JsonPropertyName("settings")]
       public OverlayConfig? Settings { get; set; }

       [JsonPropertyName("colors")]
       public Dictionary<string, string>? Colors { get; set; }

       [JsonPropertyName("tuning")]
       public Dictionary<string, double>? Tuning { get; set; }
   }
   ..
           OverlayPalette.ApplyOverrides(LoadSection<Dictionary<string, string>>(root?["colors"], "colors"));
           Rendering.Tuning.ApplyOverrides(LoadSection<Dictionary<string, double>>(root?["tuning"], "tuning"));
   ..
           ConfigFile file = new()
           {
               Settings = _tracked,
               Colors = OverlayPalette.CaptureOverrides(),
               Tuning = Rendering.Tuning.CaptureOverrides(),
           };

           string path = FilePath;
           string temp = path + ".tmp";

           File.WriteAllText(temp, JsonSerializer.Serialize(file, _options));
           File.Move(temp, path, overwrite: true);
   ```

8. **Optional: use the same trick for numbers.** `Tuning` does the identical reflection over `float` and `int` fields and adds a *Copy as source* button that turns the current values into field declarations, so a tuned set can be promoted to new defaults:

   ```csharp
   // Rendering/Tuning.cs L94-L110
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

## Complete example

| File | Role |
|---|---|
| [Rendering/OverlayStyle.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayStyle.cs#L5-L53) | The fields and their default constants |
| [Config/OverlayPalette.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/OverlayPalette.cs) | Reflection table, capture, apply, hex |
| [Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs#L469-L497) | Color editors and *Reset colours* |
| [Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs) | Load, debounce, atomic save |
| [Rendering/Tuning.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Tuning.cs#L75-L130) | The same pattern for numeric tuning |

## Variations and gotchas

- **Defaults are whatever the field held at type initialisation.** If anything writes a field before the class is first touched, that value becomes the "default" for the process. Calling `ResetAll` or `ApplyOverrides` first, as `Load` does, forces initialisation before any write.
- **Changing a default silently changes the player's look** wherever they never edited that color. That is the point of the design, but it also means a "default" is not a stable value: a player who likes a default cannot pin it without editing it.
- **A color the player nudges back to the default disappears from the file.** Comparison is exact `uint` equality, which is reliable for colors and the reason the numeric tuning table (exact `double` equality) is flakier.
- **Renaming a field orphans saved overrides.** Names are the file schema; rename only with a migration or accept that old overrides are ignored.
- **Alpha is not editable in the picker** (see step 6); use `ColorEdit4` as in [Keep named, editable color palettes in a sectioned file with a ColorEdit4 round trip](./persist-user-created-presets-and-content-1.md) when it must be.
- **Truncating hex output.** `ToHex` casts `r * 255f` to `byte` without rounding. Because each channel started as a byte divided by 255, the product comes back to the same integer in single precision (checked offline for all 256 values); a color computed from floats would need `Math.Round`.

## See also

- Sibling approaches: [Named palettes in a sectioned ini](./persist-user-created-presets-and-content-1.md), [Bundled and user theme TOML files](./persist-user-created-presets-and-content-2.md), [overview](./overview.md)
- [JSON config in the KSA documents folder](../../../../mods/ksa-public-telemetry/features/json-config-in-documents.md), [Live tuning window and palette by reflection](../../../../mods/ksa-public-telemetry/features/live-tuning-and-palette-reflection.md)
- [Persist mod settings as JSON with System.Text.Json](../../settings-formats/persist-mod-settings-as-json/overview.md), [Write config files atomically and load hand-edited ones without losing them](../../settings-formats/write-and-load-config-files-safely/overview.md)
- [Build a mod settings window (and developer panel)](../../../ui/windows/build-a-settings-window/overview.md)
