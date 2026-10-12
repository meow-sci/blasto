---
type: How-To
title: Load settings first at StarMapImmediateLoad and rewrite on change
description: Read settings.ini before patches and UI exist so the first frame already knows the saved options, clamp colors to 0 to 1, keep the default for each malformed value and save when a pointer option or the window flag changes
category: persistence
group: settings-formats
topic: persist-mod-settings-ini
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/compendium/features/settings-ini-persistence]
techniques: [toml-config]
hooks: [starmap StarMapImmediateLoad, starmap StarMapAfterGui]
lifecycle: [startup:immediate-load, frame:draw-ui]
game_types: [KSA.Mod, Brutal.ImGuiApi.ImGui]
tags: [settings, ini, config, immediate-load, clamp, defaults]
---

Outcome: the same flat ini as [Rewrite a flat settings.ini on every widget change](persist-mod-settings-ini-1.md) but loaded earlier, with clamping and a smaller surface. Compendium remembers whether its window was open and its pointer options (on or off, one or two lines, rainbow, color). Demonstrated by [settings.ini persistence](../../../../mods/compendium/features/settings-ini-persistence.md).

## Background: how the game does it

`StarMapImmediateLoad(Mod)` is called once per mod, in manifest order, from a prefix on `Mod.PrepareSystems` inside `ModLibrary.PrepareAll` (see [Choose and use the right StarMap lifecycle hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md)); nothing is loaded yet - no assets, systems or other mods - but the `Program` constructor has already created the ImGui context. A method must take exactly one `KSA.Mod` parameter or StarMap never calls it. Compendium does not use that parameter; it resolves its folder from the executing assembly (`dllDir`).

## Lifecycle and timing

| Piece | Hook | Lifecycle |
|---|---|---|
| `LoadSettings()` | first call in `[StarMapImmediateLoad]` ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1235-L1245)), before `Patcher.Patch()` and `LoadFonts` | `startup:immediate-load` |
| `PersistWindowOpenState()` | top of `[StarMapAfterGui]` ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L56-L60)) | `frame:draw-ui` |
| `SaveSettings()` | pointer option handlers (`CdmBodyPointer.cs`) and `PersistWindowOpenState` | `frame:draw-ui` |

Loading first means the very first drawn frame already knows whether the window should be open, and there is no flash of the default state. The per-frame cost is one boolean comparison; writes happen only on change. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Resolve the mod folder from the executing assembly** and combine the file name:

   ```csharp
   // src/CdmSettings.cs L8-L16
   private const string SettingsFileName = "settings.ini";

   // Open/closed state last written to settings.ini; ImGui's imgui.ini only keeps position/size, not visibility.
   private static bool savedCompendiumWindow = true;

   private static string? GetSettingsFilePath()
   {
       return string.IsNullOrWhiteSpace(dllDir) ? null : Path.Combine(dllDir, SettingsFileName);
   }
   ```

   `dllDir` is assigned at the top of `OnImmediateLoad`:

   ```csharp
   // src/CompendiumMain.cs L1235-L1242
   [StarMapImmediateLoad]
   public void OnImmediateLoad(Mod mod)
   {
       try
       {
           // Gets the current working directory path (of the DLL)
           dllDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
           LoadSettings();
   ```

2. **Create the file from current defaults when missing; otherwise read it tolerantly.** Every line with an `=` is a case-insensitive key; section headers are ignored on read:

   ```csharp
   // src/CdmSettings.cs L18-L59
   private static void LoadSettings()
   {
       string? settingsPath = GetSettingsFilePath();
       if (settingsPath == null)
       {
           return;
       }

       if (!File.Exists(settingsPath))
       {
           SaveSettings();
           return;
       }

       try
       {
           var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
           foreach (string line in File.ReadAllLines(settingsPath))
           {
               int equalsIndex = line.IndexOf('=');
               if (equalsIndex > 0)
               {
                   values[line[..equalsIndex].Trim()] = line[(equalsIndex + 1)..].Trim();
               }
           }

           CompendiumWindow = ReadBool(values, "ShowWindow", CompendiumWindow);
           showBodyPointer = ReadBool(values, "ShowPointer", showBodyPointer);
           pointerDualLines = ReadBool(values, "PointerDualLines", pointerDualLines);
           pointerRainbow = ReadBool(values, "PointerRainbow", pointerRainbow);
           pointerColor = new float3(
               ReadFloat(values, "PointerColorR", pointerColor.X),
               ReadFloat(values, "PointerColorG", pointerColor.Y),
               ReadFloat(values, "PointerColorB", pointerColor.Z));
       }
       catch (Exception ex)
       {
           Console.WriteLine($"Compendium: Failed to read {settingsPath}: {ex.Message}");
       }

       savedCompendiumWindow = CompendiumWindow;
   }
   ```

3. **Typed readers fall back and clamp.** Booleans use `bool.TryParse`; colors use `float.TryParse` with the invariant culture and clamp to 0..1; a malformed or missing value keeps its default:

   ```csharp
   // src/CdmSettings.cs L100-L110
   private static bool ReadBool(Dictionary<string, string> values, string key, bool fallback)
   {
       return values.TryGetValue(key, out var text) && bool.TryParse(text, out bool value) ? value : fallback;
   }

   private static float ReadFloat(Dictionary<string, string> values, string key, float fallback)
   {
       return values.TryGetValue(key, out var text) && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
           ? Math.Clamp(value, 0f, 1f)
           : fallback;
   }
   ```

4. **Write the whole file with invariant formatting** and record the saved window flag so the change detector has something to compare against:

   ```csharp
   // src/CdmSettings.cs L61-L89
   private static void SaveSettings()
   {
       string? settingsPath = GetSettingsFilePath();
       if (settingsPath == null)
       {
           return;
       }

       savedCompendiumWindow = CompendiumWindow;
       try
       {
           File.WriteAllLines(settingsPath, new[]
           {
               "[Window]",
               $"ShowWindow={CompendiumWindow.ToString(CultureInfo.InvariantCulture)}",
               "[Pointer]",
               $"ShowPointer={showBodyPointer.ToString(CultureInfo.InvariantCulture)}",
               $"PointerDualLines={pointerDualLines.ToString(CultureInfo.InvariantCulture)}",
               $"PointerRainbow={pointerRainbow.ToString(CultureInfo.InvariantCulture)}",
               $"PointerColorR={pointerColor.X.ToString(CultureInfo.InvariantCulture)}",
               $"PointerColorG={pointerColor.Y.ToString(CultureInfo.InvariantCulture)}",
               $"PointerColorB={pointerColor.Z.ToString(CultureInfo.InvariantCulture)}"
           });
       }
       catch (Exception ex)
       {
           Console.WriteLine($"Compendium: Failed to write {settingsPath}: {ex.Message}");
       }
   }
   ```

5. **Detect a window flag that changed from outside the widgets.** The flag is flipped by the ModMenu entry and by the window's close button (`ImGui.Begin("Compendium", ref CompendiumWindow)`), so compare it every frame:

   ```csharp
   // src/CdmSettings.cs L91-L98
   // Called every frame so both the mod menu toggle and the window's close (X) button get persisted.
   private static void PersistWindowOpenState()
   {
       if (CompendiumWindow != savedCompendiumWindow)
       {
           SaveSettings();
       }
   }
   ```

6. **Call `SaveSettings()` directly from option handlers** in the pointer settings (`CdmBodyPointer.cs`).

7. **Ship the defaults.** The csproj copies `settings.ini` to the output on every build and then to the mods folder, with a comment that every build resets the test copy so published files carry the defaults ([src/Compendium.csproj](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium.csproj#L31-L37), [src/Compendium.csproj](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium.csproj#L81-L84)).

## Complete example

| File | Role |
|---|---|
| [src/CdmSettings.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmSettings.cs#L18-L98) | Parse with defaults and clamping; write; change detection |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1230-L1248) | Where the load happens |
| [src/Compendium.csproj](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium.csproj#L81-L84) | Default file copied each build |

## Variations and gotchas

- **Failures are logged and ignored.** The load and the save each have a `try`/`catch` that prints to the console; a read error leaves the in-memory defaults and, because `savedCompendiumWindow` is set after the `try`, no immediate rewrite is triggered by the failure.
- **Not atomic, whole-file writes.** Same caveats as [Rewrite a flat settings.ini on every widget change](persist-mod-settings-ini-1.md).
- **Value domain.** Only color channels are clamped; `ReadBool` has no domain beyond true/false. Clamp any numeric option whose range matters before it reaches the renderer.
- **A hidden HUD** means no `StarMapAfterGui`, so the open flag is not written until it returns.
- **Unhandled exceptions in the hook would end the game.** The load sits inside `OnImmediateLoad`'s own `try`; keep config loading inside the hook's containment ([Keep mod exceptions out of KSA's frame loop](../../../getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md)).

## See also

- [Rewrite a flat settings.ini on every widget change](persist-mod-settings-ini-1.md), [Save an eleven-key hud.ini only when the player presses a button](persist-mod-settings-ini-3.md), [overview](./overview.md)
- [settings.ini persistence](../../../../mods/compendium/features/settings-ini-persistence.md)
- [Choose and use the right StarMap lifecycle hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md), [Persist ImGui window state and layouts](../../user-data/persist-imgui-window-state/overview.md)
