---
type: How-To
title: "StarMapImmediateLoad: register data before assets deserialize"
description: Use the per-mod prefix on Mod.PrepareSystems to write files, inject enum values or capture your KSA.Mod before asset XML is read, and avoid the parameterless-signature trap
category: getting-started
group: lifecycle
topic: choose-a-starmap-startup-hook
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/starmap/features/startup-lifecycle-hooks
  - mods/ksa-advancedflightcomputer/features/superseded-mod-detection-and-settings-import
  - mods/compendium/features/cross-mod-json-catalog
techniques: [starmap-lifecycle, reflection]
hooks: [starmap StarMapImmediateLoad, harmony-prefix KSA.Mod.PrepareSystems]
lifecycle: [startup:immediate-load, startup:asset-load]
game_types: [KSA.Mod, KSA.ModLibrary, KSA.Program]
tags: [starmap, immediate-load, prepare-systems, startup, enum-injection, files]
---

`StarMapImmediateLoad(KSA.Mod)` is the earliest hook that has game services (logging, window, ImGui) and your own
`KSA.Mod`, but runs **before** any asset bundle is loaded. That makes it the place for work that XML
deserialization must already see: a generated shader include, a registered enum value, a captured folder path, a
file scan. Prerequisite: StarMap. The siblings are [StarMapBeforeMain - patch code that runs during startup](./choose-a-starmap-startup-hook-1.md) (earlier, no game state) and
[StarMapAllModsLoaded - work that needs the full mod set](./choose-a-starmap-startup-hook-3.md) (later, everything loaded).

## Background: how the game does it

StarMap does not hook a "mod loaded" event; it prefixes `KSA.Mod.PrepareSystems` and looks the instance up by
`__instance.Id`:[StarMap.Core/Patches/ModPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModPatches.cs#L8-L23)

```csharp
// StarMap.Core/Patches/ModPatches.cs L8-L23
[HarmonyPatch(typeof(Mod))]
internal static class ModPatches
{
    [HarmonyPatch(nameof(Mod.PrepareSystems))]
    [HarmonyPrefix]
    public static void OnLoadMod(this Mod __instance)
    {
        var modRegistry = StarMapCore.Instance?.Loader.ModRegistry;
        if (modRegistry is not ModRegistry registry) return;

        if (registry.TryGetMod(__instance.Id, out var modInfo) && modInfo.PrepareSystemsAction is MethodInfo action)
        {
            action.Invoke(modInfo.ModInstance, [__instance]);
        }
    }
}
```

`PrepareSystems` is called once per enabled mod from `ModLibrary.PrepareAll` (right after
`Mod.MakeUsing` and `RegisterTo(Lookup)`, before `LoadSettings`), which the `Program` constructor calls at
L899, one line before `PreloadAssetBundles`.[current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L447)[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L900) Only mods that have
a StarMap entry assembly and a correctly shaped method are reached. The required signature is exactly one
`KSA.Mod` parameter, void return:[StarMap.API/BaseAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/BaseAttributes.cs#L72-L80)

```csharp
// StarMap.API/BaseAttributes.cs L72-L80
public class StarMapImmediateLoadAttribute : StarMapMethodAttribute
{
    public override bool IsValidSignature(MethodInfo method)
    {
        return method.ReturnType == typeof(void) &&
               method.GetParameters().Length == 1 &&
               method.GetParameters()[0].ParameterType == typeof(Mod);
    }
}
```

## Lifecycle and timing

`startup:immediate-load`. Interleaved with other mods: your call happens after earlier manifest entries ran their
`PrepareSystems` and `LoadSettings`, and before yours.

| Exists | Does not exist |
|---|---|
| Logging, window, `GameSettings`, Vulkan `Renderer`, ImGui context and fonts, loading screen, console commands, the final manifest; `definingMod` registered with `Id`, `Name`, `DirectoryPath` and raw `mod.toml` lists | Any asset bundle, part, system template, mod font, viewport, universe; no ImGui frame is open (the loading screen pumps its own) |

It is the earliest hook that has a `KSA.Mod`, **not** the latest: assets arrive in `LoadAll`. Full table:
[StarMap lifecycle attributes](../game-loop-and-lifecycle/game-loop-and-lifecycle-1.md).

## Steps

1. **Use the exact signature**, public, one `KSA.Mod` parameter. A parameterless `[StarMapImmediateLoad]` method
   is never bound and never called, silently (gatOS, purrTTY and the unscience template all declare one, harmlessly,
   because their bodies are empty):

   ```csharp
   // KSA-AdvancedFlightComputer/AdvancedFlightComputer/Mod.cs L27-L28
       [StarMapImmediateLoad]
       public void OnImmediateLoad(KSA.Mod mod) => AutoStageFeature.InjectGaugeEnumAtLoad();
   ```

2. **Pick the job.** Four patterns appear in the corpus:
   - *Inject a value the XML will reference.* AdvancedFlightComputer appends its marker enum to the game's private
     `GaugeButtonFlightComputer.EnumTypes` list before the game reads `Gauges.xml` and binds the button's
     `Action="AfcAutoStageToggle"` once (it cannot be undone later):[AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs#L8-L35)

     ```csharp
     // AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs L20-L29
         internal static void InjectGaugeEnumAtLoad()
         {
             if (!GameReflection.ValidateAutoStage())
             {
                 DefaultCategory.Log.Warning("[AFC] AutoStage reflection keys did not resolve; the AUTOSTAGE gauge button is not registered.");
                 return;
             }
             if (!InjectGaugeEnum())
                 DefaultCategory.Log.Warning("[AFC] GaugeButtonFlightComputer.EnumTypes is not a List<EnumTypeOption>; the AUTOSTAGE gauge button is not registered.");
         }
     ```

     The game's resolver, `GaugeButtonFlightComputer.FindTypeOption`, matches by plain `Type.Name`, which is why
     AFC names its marker uniquely.[current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L55-L65)
   - *Write a file that a later loader reads.* KSArmory writes a generated shader include into
     `mod.DirectoryPath` "before anything asks KSA to compile a shader":[src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L132-L147)

     ```csharp
     // src/KSArmory/Ksa/KSArmoryMod.cs L132-L139
         [StarMapImmediateLoad]
         public void OnImmediateLoad(Mod mod)
         {
             // Before anything asks KSA to compile a shader: the generated header names this machine's
             // own install, and a .comp that includes it will not compile until it is there.
             CoreShaderInclude.Write(mod.DirectoryPath);

             Log.Info($"loading (mod id: {mod.Id})");
     ```

   - *Read data files.* Compendium scans JSON from its own and neighbouring mod folders (plain file I/O, so
     other mods' files are on disk regardless of load order):[src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1235-L1265)

     ```csharp
     // src/CompendiumMain.cs L1235-L1248
             [StarMapImmediateLoad]
             public void OnImmediateLoad(Mod mod)
             {
                 try
                 {
                     // Gets the current working directory path (of the DLL)
                     dllDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                     LoadSettings();

                     // Enable orbit visibility and root navigation targeting patches.
                     Patcher.Patch();

                     // Loads fonts from the Fonts folder
                     LoadFonts(dllDir, fontSizeCurrent);
     ```

   - *Capture your `KSA.Mod`.* KSA-Public-Telemetry stores `mod.DirectoryPath` for its font loader:

     ```csharp
     // TelemetryOverlayMod.cs L32-L37
         [StarMapImmediateLoad]
         public void OnImmediateLoad(Mod mod)
         {
             _modDirectory = mod.DirectoryPath;
             Console.WriteLine(LogPrefix + $"attached to KSA mod '{mod.Id}'");
         }
     ```

3. **Make it idempotent and fail-soft.** AFC checks a reflection shape (`ValidateAutoStage`) first and logs one
   warning instead of registering a half-working button; Compendium wraps the whole body in a `try`.
4. **Do not rely on assets or the scene.** Anything that needs parsed XML, part templates or a universe waits for
   [StarMapAllModsLoaded - work that needs the full mod set](./choose-a-starmap-startup-hook-3.md). Note `AllModsLoaded` fires after every mod's immediate load, so a mod that
   registers a marker here can be detected by others there ([Late-bound type lookup with AccessTools.TypeByName (plus: Marker registered at immediate load, or type lookup, plus copy-once settings import)](../../dependencies/declare-a-mod-dependency/declare-a-mod-dependency-3.md)).

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs) + [AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs) | Enum injection at immediate load, detection of a predecessor's marker later |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs) | Generated shader include written before shaders compile |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs) | File scan, settings, fonts, JSON catalog |
| [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs) | Capturing `DirectoryPath` |

## Variations and gotchas

- **Content-only mods and wrong signatures are skipped silently**; the lookup is by `Mod.Id`, so the manifest id
  must be your folder name.
- **With KittenExtensions installed**, rewriting a `mod.toml`-listed XML file at this point is not seen by its
  redirected loaders (it runs its XML patch pass as a `PrepareAll` prefix that finishes before the first
  immediate load); write only files a later loader reads fresh, as KSArmory does.
- **Not the place for patches on gameplay or rendering code**; those belong in `AllModsLoaded`.
- **Exceptions are not caught**; an exception escaping propagates into `PrepareAll`.
- **Docs drift.** KSArmory's own notes say this hook fires "as this mod finishes loading"; it fires as a prefix on
  `Mod.PrepareSystems`, before the mod's asset bundles are preloaded.

## See also

- [StarMapBeforeMain - patch code that runs during startup](./choose-a-starmap-startup-hook-1.md), [StarMapAllModsLoaded - work that needs the full mod set](./choose-a-starmap-startup-hook-3.md), [Signature checklist against StarMap's binding rules](./choose-a-starmap-startup-hook-4.md), [Choose and use the right StarMap lifecycle hook](./overview.md)
- [Startup lifecycle hooks (BeforeMain, ImmediateLoad, AllModsLoaded)](../../../../mods/starmap/features/startup-lifecycle-hooks.md), [Stand down for a superseded standalone mod and import its settings](../../../../mods/ksa-advancedflightcomputer/features/superseded-mod-detection-and-settings-import.md),
  [Cross-mod JSON catalog](../../../../mods/compendium/features/cross-mod-json-catalog.md)
- [Game loop and mod lifecycle](../game-loop-and-lifecycle/overview.md), [Find your mod's install folder at runtime: Assembly.Location, then ModLibrary.Find, then Documents](../../loader/ship-content-and-code-in-one-mod/ship-content-and-code-in-one-mod-3.md)
