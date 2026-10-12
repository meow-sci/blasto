---
type: How-To
title: Edit static constants with reflected sliders, keep only the overrides and copy them back as source
description: Declare layout and animation numbers as public static fields, reflect them at type initialisation, bind ImGui sliders by ref and persist or dump only the values that differ from the defaults
category: testing-and-tooling
group: in-game-testing
topic: live-tweak-constants-in-game
difficulty: intermediate
prerequisites: [starmap, imgui]
demonstrated_by: [mods/ksa-public-telemetry/features/live-tuning-and-palette-reflection]
techniques: [reflection, imgui-window, json-config]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui]
tags: [tuning, reflection, imgui, sliders, iteration, config]
---

Outcome: an "Overlay tuning" window with a slider per constant, "Replay intro", "Reset all" and "Copy as source" buttons. Changes apply on the next frame, are saved to the mod's `config.json` as a map of only the changed values, and "Copy as source"
produces `public static float BoxHeight = 177f;   // changed` lines to paste into the source. Demonstrated by KSA-Public-Telemetry ([live tuning](../../../../mods/ksa-public-telemetry/features/live-tuning-and-palette-reflection.md)). Prerequisites: StarMap and a mod ImGui window.

## Background: how the game does it

The game offers nothing for this; it is a mod pattern built on Brutal ImGui. Two engine facts frame it. A C# change needs a game restart (StarMap loads a mod assembly once at startup and nothing reloads it), so anything you can turn into data is worth turning into data. And the UI hooks run each frame inside `if (DrawUI)`
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2269)), so a tuning window draws only while the game UI is visible; the clipboard binding is `ImGui.SetClipboardText` ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8299-L8303)).

## Lifecycle and timing

| Step | Hook |
|---|---|
| Load overrides from `config.json` | `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`), `ConfigStore.Load` calls `Tuning.ApplyOverrides` ([Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs#L72-L73)) |
| Draw the window | `[StarMapAfterGui]` (`frame:draw-ui`) via `DrawEverything` ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L85-L126)) |
| Readers | Every panel reads the static fields each frame, so an edit shows on the next draw with no rebuild |

Everything is main-thread. Reflection runs only on load, save and the source dump; the per-frame path reads the fields directly. See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare every tunable as a `public static float` or `int` field in one class**, with the shipped values as initialisers and a comment that says edits are only saved in config and must be copied back by hand:

   ```csharp
   // Rendering/Tuning.cs L6-L28
   /// static defaults for the overlay appearance and animation. I got sick of recompiling the mod with every little tweak. 
   /// These are editable in-game but must be updated here manually after changed are made since they are only saved in config.
   /// mostly a note for myself.
   public static class Tuning
   {
       // intro sequence animation
       public static float IntroTimeScale = 1.994f;
       public static float OutroSeconds = 0.9f;
       public static float BackdropStart = 0.149f;
       public static float BackdropDuration = 0.641f;
       public static int BackdropDecay = 3;
       public static float GaugesStart = 0.255f;
       public static float GaugesDuration = 0.623f;
       public static int GaugesDecay = 3;
       public static float RimStart = 0.32f;
       public static float RimDuration = 0.182f;
       public static int RimDecay = 1;
       public static float ArcSweepStart = 0.657f;
       public static float ArcSweepDuration = 3f;
       public static int ArcSweepDecay = 20;
       public static float ReadoutsStart = 0.49f;
       public static float ReadoutsDuration = 0.56f;
       public static int ReadoutsDecay = 3;
   ```

2. **Reflect and snapshot the defaults at type initialisation.** Non-literal public statics of type float or int, sorted by name, with the initial values captured as the defaults:

   ```csharp
   // Rendering/Tuning.cs L75-L85
       private static readonly FieldInfo[] _fields = typeof(Tuning)
           .GetFields(BindingFlags.Public | BindingFlags.Static)
           .Where(f => !f.IsLiteral && (f.FieldType == typeof(float) || f.FieldType == typeof(int)))
           .OrderBy(f => f.Name, StringComparer.Ordinal)
           .ToArray();

       private static readonly Dictionary<string, double> _defaults =
           _fields.ToDictionary(f => f.Name, Read, StringComparer.Ordinal);

       public static IReadOnlyList<string> Names { get; } = _fields.Select(f => f.Name).ToArray();

   ```

3. **Persist overrides only.** `CaptureOverrides` returns just the fields that differ; `ApplyOverrides` resets everything and applies a name-to-value map, ignoring unknown names (so a renamed field cannot crash a load):

   ```csharp
   // Rendering/Tuning.cs L87-L130
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

       public static void ApplyOverrides(Dictionary<string, double>? overrides)
       {
           ResetAll();

           if (overrides is null)
           {
               return;
           }

           foreach (KeyValuePair<string, double> entry in overrides)
           {
               FieldInfo? field = Find(entry.Key);

               if (field is not null)
               {
                   Write(field, entry.Value);
               }
           }
       }
   ```

4. **Read and write through `double`** so one dictionary holds both kinds:

   ```csharp
   // Rendering/Tuning.cs L152-L171
       private static double Read(FieldInfo field)
       {
           if (field.FieldType == typeof(int))
           {
               return (int)field.GetValue(null)!;
           }

           return (float)field.GetValue(null)!;
       }

       private static void Write(FieldInfo field, double value)
       {
           if (field.FieldType == typeof(int))
           {
               field.SetValue(null, (int)value);
               return;
           }

           field.SetValue(null, (float)value);
       }
   ```

5. **Dump as source.** One line per field, with `// changed` on the ones that moved:

   ```csharp
   // Rendering/Tuning.cs L132-L150
       public static string DumpSource()
       {
           System.Text.StringBuilder builder = new();

           for (int i = 0; i < _fields.Length; i++)
           {
               FieldInfo field = _fields[i];
               double value = Read(field);
               bool moved = value != _defaults[field.Name];

               builder.Append(field.FieldType == typeof(int)
                   ? $"    public static int {field.Name} = {(int)value};"
                   : $"    public static float {field.Name} = {value.ToString("0.####", CultureInfo.InvariantCulture)}f;");

               builder.AppendLine(moved ? "   // changed" : string.Empty);
           }

           return builder.ToString();
       }
   ```

6. **Bind sliders by `ref` to the static fields** inside a window, group them in collapsing headers, and track `changed` to mark the config dirty. The three buttons call `Tuning.ResetAll`, the renderer's replay, and the dump with `ImGui.SetClipboardText`:

   ```csharp
   // Rendering/TuningUi.cs L40-L72
       private static void DrawContent()
       {
           bool changed = false;

           if (ImGui.SmallButton("Replay intro"u8))
           {
               _renderer?.ReplayIntro();
           }

           ImGui.SameLine();

           if (ImGui.SmallButton("Reset all"u8))
           {
               Tuning.ResetAll();
               changed = true;
           }

           ImGui.SameLine();

           if (ImGui.SmallButton("Copy as source"u8))
           {
               string dump = Tuning.DumpSource();

               Console.WriteLine("[KSATelemetryOverlay] tuning dump:\n" + dump);
               ImGui.SetClipboardText(dump);
           }

           ImGui.TextDisabled("Changes apply live and are saved to config.json."u8);

           if (ImGui.CollapsingHeader("Intro sequence"u8, ImGuiTreeNodeFlags.DefaultOpen))
           {
               ImGui.TextDisabled("Stage starts and lengths are in seconds, before the scale."u8);
               changed |= Slider("Time scale", ref Tuning.IntroTimeScale, 0.25f, 3f);
   ```
   ```csharp
   // Rendering/TuningUi.cs L165-L172
       private static bool Slider(string label, ref float value, float min, float max)
           => Slider(label, ref value, min, max, "%.3f"u8);

       private static bool Slider(string label, ref float value, float min, float max, ImString format)
       {
           ImGui.SetNextItemWidth(SliderWidth);
           return ImGui.SliderFloat(label, ref value, min, max, format);
       }
   ```

7. **Load on startup.** The loader applies the `tuning` section after the settings:

   ```csharp
   // Config/ConfigStore.cs L72-L73
               OverlayPalette.ApplyOverrides(LoadSection<Dictionary<string, string>>(root?["colors"], "colors"));
               Rendering.Tuning.ApplyOverrides(LoadSection<Dictionary<string, double>>(root?["tuning"], "tuning"));
   ```

8. **Use it.** Open the window (the mod opens it from its settings' Advanced section: `TuningUi.SetOpen` is called from [Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs#L245-L256)), drag sliders until the overlay looks right, press "Copy as source", paste into `Tuning.cs`, rebuild.

## Complete example

| File | Role |
|---|---|
| [Rendering/Tuning.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Tuning.cs) | Fields, reflection, overrides, source dump |
| [Rendering/TuningUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/TuningUi.cs) | The window |
| [Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs) | `tuning` section of `config.json` |
| [Config/OverlayPalette.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/OverlayPalette.cs) | The same reflection for colour fields (`uint`), by name, serialised as `#RRGGBBAA` |

## Variations and gotchas

- **Defaults are whatever the field held at type init.** If anything writes a field before the class is first touched, that value becomes the "default"; here `ConfigStore.Load` calls `ResetAll` or `ApplyOverrides` first, which forces initialisation before any write.
- **Float round-trip.** Values travel as `double`; the comparison with the default is exact equality, so a slider nudged back by hand may still count as changed.
- **Only plain numbers.** Colours, enums and strings need their own handling (the palette class); lists need a different UI.
- **Keep it out of player builds if the window is noise.** It is a debug window; gate it with one of the developer gates in [Drive a running game through a file bridge](../drive-a-running-game-through-a-file-bridge/overview.md) if you do not want players to see it. In this mod it is a settings option.
- **Hidden UI.** While the game UI is hidden, `StarMapAfterGui` does not fire; the tuning window is drawn only when `gameUiHidden` is false.

## See also

- [Overview](overview.md)
- [Build a settings window](../../../ui/windows/build-a-settings-window/overview.md)
- [Live tuning and palette reflection](../../../../mods/ksa-public-telemetry/features/live-tuning-and-palette-reflection.md)
