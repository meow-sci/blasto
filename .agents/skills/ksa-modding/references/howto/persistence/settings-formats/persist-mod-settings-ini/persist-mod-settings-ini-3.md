---
type: How-To
title: Save an eleven-key hud.ini only when the player presses a button
description: Read hud.ini with a switch on the key, parse enums with Enum.TryParse and colors as r,g,b,a floats, recompute derived values after loading, and write only from a Save HUD Settings button
category: persistence
group: settings-formats
topic: persist-mod-settings-ini
approach: 3
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksa-aircrafthud/features/ini-settings-and-settings-window]
techniques: [imgui-window]
hooks: [starmap StarMapImmediateLoad]
lifecycle: [startup:immediate-load, frame:draw-ui]
game_types: [KSA.Mod, Brutal.ImGuiApi.ImGui]
tags: [settings, ini, config, explicit-save, enum, color, derived-values]
---

Outcome: a settings file the player controls. KSA_AircraftHUD keeps eleven options in `hud.ini` beside the DLL; the sliders and pickers change the live values at once, and only the **Save HUD Settings** button writes them. Demonstrated by [INI settings file and settings window](../../../../mods/ksa-aircrafthud/features/ini-settings-and-settings-window.md). The window itself is a normal ImGui window (see [Build a mod settings window (and developer panel)](../../../ui/windows/build-a-settings-window/overview.md)).

## Background: how the game does it

Nothing in the engine is involved except the hook the file is read from: `StarMapImmediateLoad` receives the `KSA.Mod` and runs before assets are loaded (see [Choose and use the right StarMap lifecycle hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md)). The mod is drawn through shader passes so some values (font sizes, line thickness) are derived from the vertical resolution and the saved scales at the same moment, which is why load and derivation sit together.

## Lifecycle and timing

`Init(Mod)` is the `[StarMapImmediateLoad]` method (`startup:immediate-load`): it patches with Harmony, reads `hud.ini`, then computes derived values from the ImGui main viewport. The Save button is drawn in the settings window (`frame:draw-ui`) and writes synchronously on the main thread when clicked. Nothing writes at unload, so an unsaved change is lost on exit by design. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Pick the file next to the DLL.** `Path.Combine(dllDir, "hud.ini")` with `dllDir` from `Assembly.GetExecutingAssembly().Location` ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L677-L679)).

2. **Format.** A `[HUD]` header and eleven `Key=Value` lines; booleans are `0`/`1`, enums are their names, colors are `r,g,b,a` floats:

   ```ini
   # hud.ini L1-L12
   [HUD]
   Enabled=1
   OrbitMode=1
   EffectsOutsideIVA=0
   DistanceUnits=Metric
   SpeedUnits=Metric
   HUDColor=0.5345602,1,0.35640138,1
   HUDColor1=0.5345602,1,0.35640138,1
   HUDColor2=0.97923875,0.13892317,0.31338358,1
   ShadowColor=0,0,0,0.5
   TextScale=0.8
   LineScale=1.0
   ```

3. **Parse colors and format them with the invariant culture.**

   ```csharp
   // AircraftHUDMain.cs L557-L574
   static float4 ParseColor(string s)
   {
       var p = s.Split(',');
       return new float4(
           float.Parse(p[0], CultureInfo.InvariantCulture),
           float.Parse(p[1], CultureInfo.InvariantCulture),
           float.Parse(p[2], CultureInfo.InvariantCulture),
           float.Parse(p[3], CultureInfo.InvariantCulture)
       );
   }

   static string FormatColor(float4 c)
   {
       return $"{c.X.ToString(CultureInfo.InvariantCulture)}," +
              $"{c.Y.ToString(CultureInfo.InvariantCulture)}," +
              $"{c.Z.ToString(CultureInfo.InvariantCulture)}," +
              $"{c.W.ToString(CultureInfo.InvariantCulture)}";
   }
   ```

4. **Load with a `switch` on the key.** Blank lines and section headers are skipped; a line must split into exactly two parts on `=`; enums use `Enum.TryParse`, so an unknown name keeps the compiled default:

   ```csharp
   // AircraftHUDMain.cs L576-L638
   static void LoadHudIni(string path)
   {
       if (!File.Exists(path))
       {  
           Console.WriteLine("AircraftHUD: file does not exist " + path);
           return;
       }
       Console.WriteLine("AircraftHUD: Load Settings from " + path);

       string[] lines = File.ReadAllLines(path);

       foreach (string line in lines)
       {
           if (string.IsNullOrWhiteSpace(line) || line.StartsWith("["))
               continue;

           var parts = line.Split('=');
           if (parts.Length != 2)
               continue;

           string key = parts[0].Trim();
           string value = parts[1].Trim();

           switch (key)
           {
               case "Enabled":
                   HUD.enabled = value == "1";
                   break;
               case "OrbitMode":
                   HUD.orbitMode = value == "1";
                   break;
               case "EffectsOutsideIVA":
                   HUD.effectsOutsideIVA = value == "1";
                   break;
               case "DistanceUnits":
                   if (Enum.TryParse(value, out DistanceUnits distUnits))
                       HUD.CurrentDistUnits = distUnits;
                   break;
               case "SpeedUnits":
                   if (Enum.TryParse(value, out SpeedUnits speedUnits))
                       HUD.CurrentSpeedUnits = speedUnits;
                   break;
               case "HUDColor":
                   HUD.hudColorPicker = ParseColor(value);
                   break;
               case "HUDColor1":
                   HUD.hudColor1Picker = ParseColor(value);
                   break;
               case "HUDColor2":
                   HUD.hudColor2Picker = ParseColor(value);
                   break;
               case "ShadowColor":
                   HUD.shadowColorPicker = ParseColor(value);
                   break;
               case "TextScale":
                   HUD.textScale = float.Parse(value, CultureInfo.InvariantCulture);
                   break;
               case "LineScale":
                   HUD.lineScale = float.Parse(value, CultureInfo.InvariantCulture);
                   break;
           }
       }
   }
   ```

5. **Write all eleven keys** from the live values:

   ```csharp
   // AircraftHUDMain.cs L640-L658
   static void SaveHudIni(string path)
   {
       using var w = new StreamWriter(path);

       w.WriteLine("[HUD]");
       w.WriteLine($"Enabled={(HUD.enabled ? 1 : 0)}");
       w.WriteLine($"OrbitMode={(HUD.orbitMode ? 1 : 0)}");
       w.WriteLine($"EffectsOutsideIVA={(HUD.effectsOutsideIVA ? 1 : 0)}");
       w.WriteLine($"DistanceUnits={HUD.CurrentDistUnits.ToString()}");
       w.WriteLine($"SpeedUnits={HUD.CurrentSpeedUnits.ToString()}");
       w.WriteLine($"HUDColor={FormatColor(HUD.hudColorPicker)}");
       w.WriteLine($"HUDColor1={FormatColor(HUD.hudColor1Picker)}");
       w.WriteLine($"HUDColor2={FormatColor(HUD.hudColor2Picker)}");
       w.WriteLine($"ShadowColor={FormatColor(HUD.shadowColorPicker)}");
       w.WriteLine($"TextScale={HUD.textScale.ToString(CultureInfo.InvariantCulture)}");
       w.WriteLine($"LineScale={HUD.lineScale.ToString(CultureInfo.InvariantCulture)}");

       Console.WriteLine("AircraftHUD: Save Settings to " + path);
   }
   ```

6. **Call load from `Init`, then derive.** Load first, so the derived values use the loaded scales:

   ```csharp
   // AircraftHUDMain.cs L675-L699
   ImGuiViewport * viewport = ImGui.GetMainViewport();

   string dllDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

   LoadHudIni(Path.Combine(dllDir, "hud.ini"));

   // most params are scaled by center.Y
   center = new float2(viewport->Pos.X + viewport->Size.X * 0.5f, viewport->Pos.Y + viewport->Size.Y * 0.5f);
   window_size = viewport->Size;
   localCenter = new float2(center.X, center.Y);

   // CALCULATE SCREEN SCALING
   // responsive scaling based on vertical resolution
   verticalScale = viewport->Size.Y / 1440f;

   fontSizeSm = (float)Math.Round(20f * textScale * verticalScale);
   fontSizeMed = (float)Math.Round(26f * textScale * verticalScale);
   fontSizeLrg = (float)Math.Round(32f * textScale * verticalScale);
   line_thickness = Math.Max((float)Math.Round(line_thickness * lineScale * verticalScale), 1f);
   shadowOffset = new float2(0f, line_thickness);

   color = ImGui.ColorConvertFloat4ToU32(hudColorPicker);
   color1 = ImGui.ColorConvertFloat4ToU32(hudColor1Picker);
   color2 = ImGui.ColorConvertFloat4ToU32(hudColor2Picker);
   shadowColor = ImGui.ColorConvertFloat4ToU32(shadowColorPicker);
   ```

7. **Write only from the button.**

   ```csharp
   // AircraftHUDMain.cs L926-L930
   if (ImGui.Button("Save HUD Settings"))
   {
       string dllDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
       SaveHudIni(Path.Combine(dllDir, "hud.ini"));
   }
   ```

## Complete example

| File | Role |
|---|---|
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L557-L658) | Parse, format, load, save |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L660-L713) | `Init`: load then derived values |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L926-L930) | The Save button |
| [hud.ini](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/hud.ini) | The shipped file |

## Variations and gotchas

- **A malformed number throws.** `ParseColor` and the `float.Parse` calls for `TextScale` and `LineScale` are not `TryParse`; a hand-edited `TextScale=abc` or a color with fewer than four parts throws inside `StarMapImmediateLoad`, which is not wrapped by StarMap (see [Keep mod exceptions out of KSA's frame loop](../../../getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md)). Use `TryParse` with a default (as in [Rewrite a flat settings.ini on every widget change](persist-mod-settings-ini-1.md)) and wrap the load.
- **`SaveHudIni` has no `try`/`catch`.** A read-only folder or sharing violation throws out of the draw call that clicked the button.
- **Booleans are `== "1"`**, so `true` or `True` reads as false.
- **Derived values are recomputed once, at load.** A resolution change after load does not recompute them; the saved file stores only the player-facing scales.
- **Explicit saves suit tuning UIs.** The player can try values and leave without committing; compare the write-on-change policy of [Rewrite a flat settings.ini on every widget change](persist-mod-settings-ini-1.md).

## See also

- [Rewrite a flat settings.ini on every widget change](persist-mod-settings-ini-1.md), [Write a sectioned palette file when a dirty flag is set, on window close and at unload](persist-mod-settings-ini-4.md), [overview](./overview.md)
- [INI settings file and settings window](../../../../mods/ksa-aircrafthud/features/ini-settings-and-settings-window.md)
- [Build a mod settings window (and developer panel)](../../../ui/windows/build-a-settings-window/overview.md), [Persist mod settings as TOML](../persist-mod-settings-toml/overview.md)
