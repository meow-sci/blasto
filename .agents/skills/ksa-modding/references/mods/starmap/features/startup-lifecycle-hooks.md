---
type: Mod Feature
title: Startup lifecycle hooks (BeforeMain, ImmediateLoad, AllModsLoaded)
description: Where StarMapBeforeMain, StarMapImmediateLoad and StarMapAllModsLoaded fire relative to KSA's Program constructor, and exactly which game state exists at each
mod: starmap
category: getting-started
techniques: [starmap-lifecycle, harmony-prefix, harmony-postfix]
hooks:
  - starmap StarMapBeforeMain
  - harmony-prefix KSA.Mod.PrepareSystems
  - harmony-postfix KSA.ModLibrary.LoadAll
lifecycle: [startup:starmap-before-main, startup:immediate-load, startup:all-mods-loaded]
game_types: [KSA.Program, KSA.Mod, KSA.ModLibrary, KSA.Universe]
tags: [starmap, lifecycle, startup, harmony, mod-loading, timing]
---

Three of StarMap's seven hooks fire once, during startup. They sit at very different points of
KSA's startup sequence, and most "it works in one hook but not another" problems come from
assuming more state exists than really does. This page maps each to the exact line in the
decompiled `Program` constructor and lists what is and is not available there.

## How it works

| Hook | Attach point | Called from |
|---|---|---|
| `StarMapBeforeMain()` | Direct call in `RuntimeMod.InitializeMod`, right after the mod's hooks are bound | `StarMapCore.Init` -> `ModLoader.Init` -> `PrepareMods`, before StarMap applies its own Harmony patches and before KSA's `Main` runs[^sm-runtimemod][^sm-starmapcore] |
| `StarMapImmediateLoad(KSA.Mod)` | Harmony prefix on `Mod.PrepareSystems`, looked up by `__instance.Id` | `ModLibrary.PrepareAll`, once per enabled, valid manifest entry that has a StarMap entry assembly[^sm-modpatches] |
| `StarMapAllModsLoaded()` | Harmony postfix on `ModLibrary.LoadAll` | Broadcast to all mods in initialization order[^sm-modlibrarypatches] |

KSA's startup, condensed from the `Program` constructor
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L689-L1327)),
with the three hooks placed:[^ksa-program]

| Line | Step | Hook |
|---|---|---|
| - | StarMap discovers mods, loads entry DLLs | **StarMapBeforeMain** (per mod) |
| 1693 | `Program.Main`: reset working directory, CLI args, `new Program(args)` | |
| 692-723 | Console window, log directories, Brutal monitor, `LogSystem.Initialize` | |
| 747-776 | `Glfw.Init`, window created, `GameSettings.LoadFromFile` | |
| 781 | `ModLibrary.OnApplicationStart` (create Documents and mods folders) | |
| 794 | `new Renderer(...)`: Vulkan device and swapchain | |
| 842-853 | `ImGui.CreateContext`, `ImGuiBackend.Initialize`, `FontManager.Initialize` | |
| 855 | `new Loading()`: loading screen | |
| 861-874 | Console commands registered | |
| 887-897 | New mods: `ConfirmMods` popups, `Manifest.Save` | |
| 899 | `ModLibrary.PrepareAll`: per mod `MakeUsing`, register, `PrepareSystems`, `LoadSettings` | **StarMapImmediateLoad** (prefix of each `PrepareSystems`) |
| 900-901 | `PreloadAssetBundles`, `PreloadLanguages` | |
| 902-939 | System selection (`SelectSystem` popup or last system) | |
| 941 | `ModLibrary.LoadEditorTags` | |
| 942 | `ModLibrary.LoadAll`: fonts, asset bundles, systems, planet meshes, console lists, parallel `ILoader.Load` | **StarMapAllModsLoaded** (postfix) |
| 943 | `ModLibrary.AssignDefaults` | |
| 945-971 | Viewports built (`MainViewport`, secondary, portraits), render targets | |
| 972-974 | Mod `OnBoot` console commands, `JobSystems.Initialize` | |
| 976-983 | `ModLibrary.Bind` (GPU upload), mesh and heightmap collections | |
| 985 | `ModLibrary.AttachGameData` (part game data onto templates) | |
| 987-991 | Sounds, location music | |
| 992-1064 | Material system, renderers, stars, substances | |
| 1069 | `Universe.LoadDefaultSystem` | |
| 1075-1316 | Gauges, remaining renderers, part models, thumbnails | |
| 1317 | `IsLoaded = true`; then `Run` starts the frame loop | |

## Lifecycle and timing

All three run once, on the main thread, before the first `Program.OnFrame`. What exists at each:

**StarMapBeforeMain** (`startup:starmap-before-main`)

- Exists: `KSA.dll` in the core load context; working directory = KSA folder;
  `ModLibrary.Manifest` (StarMap called `PrepareManifest`); the instance-path override if enabled;
  every mod initialized before this one.
- Does not exist: any `Program` instance or `Program` static setup, GLFW window, Vulkan device,
  ImGui context, the player's settings (`GameSettings.Current` still holds the built-in defaults
  until `GameSettings.LoadFromFile` runs in the constructor), KSA's log sinks, any loaded asset.
  Game assemblies KSA loads lazily may not be loaded yet (RealAtmospheres has to preload
  `Planet.Render.Core`).[^ra-modmain]
- StarMap's own patches are not applied yet; they are applied after every mod's `BeforeMain`.
- Use it for: Harmony patches on code that runs inside the `Program` constructor (ShaderExtensions
  calls `PatchAll` here; RealAtmospheres patches asset and shader file loading here), file
  preparation, `Console.WriteLine` logging.[^shaderex-main][^telemetry-mod]

**StarMapImmediateLoad(KSA.Mod definingMod)** (`startup:immediate-load`)

- Runs inside `PrepareAll`, interleaved with other mods: this mod's call happens after earlier
  manifest entries ran `PrepareSystems` and `LoadSettings`, before this mod's `PrepareSystems`.[^ksa-modlibrary]
- Exists: logging, window, `GameSettings`, Vulkan `Renderer`, ImGui context and fonts, the loading
  screen, console commands, the final manifest. `definingMod` is registered in
  `ModLibrary` and carries `Id`, `Name`, `DirectoryPath` (`Content/<id>` relative to the install
  for install mods, an absolute path under Documents for local mods) and the raw `mod.toml` lists.[^ksa-mod]
- Does not exist: any asset bundle, part, system template, mod font, viewport, universe. No ImGui
  frame is open (the loading screen pumps its own `NewFrame`/`Render` inside `Loading.OnFrame`).[^ksa-loading]
- Use it for: capturing `DirectoryPath` (KSA-Public-Telemetry), writing files that later asset or
  shader loading will read (KSAStructures writes a generated shader include), registering things
  that XML deserialization in `LoadAll` must already see (AdvancedFlightComputer injects a gauge
  enum value "before any AllModsLoaded hook").[^telemetry-mod][^structures-mod][^afc-mod]

**StarMapAllModsLoaded** (`startup:all-mods-loaded`)

- Exists: everything above, plus every mod's fonts registered with `FontManager`, asset bundles
  deserialized and registered (parts, sounds, textures as CPU-side references), system templates,
  planet mesh collections, and all `ILoader`s run; `Universe.OnDataLoaded` has sorted sim speeds.
- Does not exist: `AssignDefaults` (default textures), viewports and `Program.MainViewport`, render
  targets, `JobSystems`, GPU-side assets (`ModLibrary.Bind`), part game data attached to templates
  (`AttachGameData`), sounds, renderers, the universe and its celestials, vehicles, gauges.
- Use it for: Harmony patches on gameplay and rendering code (KSAero, AdvancedFlightComputer),
  reading loaded asset definitions, cross-mod setup (all StarMap mods are initialized by now), and
  work that must precede `ModLibrary.Bind` (unscience's parts-now reserves extra space in the
  shared mesh buffers here, because those buffers are sized once inside `Bind`).[^ksaero-mod][^partsnow-meshbudget]
- Fires once: `ModLibrary.LoadAll` has a single call site, in the `Program` constructor.

Nothing in StarMap marks "the scene is ready"; mods that need `Universe`, vehicles or viewports do
it lazily on the first frame hook or patch the game themselves.

## Key code

| Path | Role |
|---|---|
| [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L156-L159) | Calls `BeforeMainAction` at the end of `InitializeMod` |
| [StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L25-L29) | Mods first, then `PatchAll` |
| [StarMap.Core/Patches/ModPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModPatches.cs#L8-L23) | `StarMapImmediateLoad` dispatch |
| [StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L17-L28) | `StarMapAllModsLoaded` dispatch |

## Snippets

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

```csharp
// StarMap.Core/Patches/ModLibraryPatches.cs L14-L28
[HarmonyPatch(typeof(ModLibrary))]
internal class ModLibraryPatches
{
    [HarmonyPatch(nameof(ModLibrary.LoadAll))]
    [HarmonyPostfix]
    public static void AfterLoad()
    {
        var modRegistry = StarMapCore.Instance?.Loader.ModRegistry;
        if (modRegistry is not ModRegistry registry) return;

        foreach (var (_, @object, method) in registry.Get<StarMapAllModsLoadedAttribute>())
        {
            method.Invoke(@object, []);
        }
    }
```

```csharp
// current/decomp/KSA/Program.cs L887-L901, L941-L943
if (ModLibrary.HasNewMods())
{
    if (!ProfilerCapture.IsEnabled)
    {
        ConfirmMods confirmMods = new ConfirmMods();
        while (confirmMods.Show)
        {
            confirmMods.OnFrame();
        }
    }
    ModLibrary.Manifest.Save();
}
ModLibrary.PrepareAll();
ModLibrary.PreloadAssetBundles();
ModLibrary.PreloadLanguages();
// ...
ModLibrary.LoadEditorTags();
ModLibrary.LoadAll();
ModLibrary.AssignDefaults();
```

## Game APIs touched

| Member | File | Use |
|---|---|---|
| `Mod.PrepareSystems` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L417-L441) | Prefix target for `StarMapImmediateLoad`; reads the `systems` list into `SelectSystem` |
| `ModLibrary.PrepareAll` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L447) | Only caller of `PrepareSystems` (line 443) |
| `ModLibrary.LoadAll` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L478-L522) | Postfix target for `StarMapAllModsLoaded` |
| `Program..ctor` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L689-L1327) | The startup sequence the table above follows |
| `Loading.OnFrame` | [current/decomp/KSA/Loading.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Loading.cs#L91-L110) | Loading screen frame pump between startup steps |

## Pitfalls and version notes

- **ImmediateLoad only reaches mods with code.** The prefix looks the `KSA.Mod` up in StarMap's
  registry by id; content-only entries and mods whose method signature is wrong are skipped
  silently. The parameter is mandatory: `StarMapImmediateLoadAttribute.IsValidSignature` requires
  exactly one `KSA.Mod` parameter, so a parameterless `[StarMapImmediateLoad]` method (gatOS,
  purrTTY and the unscience submods declare one) is never bound and never called.
- **ImmediateLoad does not mean "my assets are loaded".** It is the earliest hook that has a
  `KSA.Mod`, not the latest; assets arrive in `LoadAll`.
- **AllModsLoaded is not "game loaded".** No celestial, vehicle or viewport exists yet; touching
  `Universe.CurrentSystem` or `Program.MainViewport` here fails.
- **Logging in BeforeMain.** KSA's `LogSystem` is initialized in the `Program` constructor, so
  `BeforeMain` code in this corpus logs with `Console.WriteLine` or its own file.
- **Patches installed in BeforeMain are installed before StarMap's own.** If a mod patches the
  same method StarMap patches (`Program.OnFrame`, `OnDrawUiFrame`, `OnDrawUiViewports`,
  `ModLibrary.PrepareAll`, `LoadAll`, `Mod.PrepareSystems`), the execution order of the two
  prefixes or postfixes follows Harmony's priority rules, not the order of installation alone.
- **Version history.** `StarMapImmediateLoad` attaches to `Mod.PrepareSystems` and
  `StarMapAllModsLoaded` to `ModLibrary.LoadAll` by name; both exist unchanged in build
  2026.10.10.5554.

## See also

- [Frame lifecycle hooks](frame-lifecycle-hooks.md)
- [Unload hook and shutdown order](unload-and-shutdown.md)
- [Discover lifecycle hooks by attribute type name](attribute-hook-discovery.md)
- [Restart when new mods are enabled](new-mods-restart-flow.md)
- [StarMapBeforeMain: install patches on code that runs during startup](../../../howto/getting-started/lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-1.md) - how-to article demonstrating this feature
- [StarMapImmediateLoad: register data before assets deserialize](../../../howto/getting-started/lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-2.md) - how-to article demonstrating this feature
- [StarMapAllModsLoaded: work that needs the full mod set](../../../howto/getting-started/lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-3.md) - how-to article demonstrating this feature
- [Hook the frame with StarMap lifecycle attributes](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md) - how-to article demonstrating this feature

[^sm-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^sm-starmapcore]: StarMap.Core/StarMapCore.cs
[^sm-modpatches]: StarMap.Core/Patches/ModPatches.cs
[^sm-modlibrarypatches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^shaderex-main]: ShaderExtensions/ShaderExtensions.cs
[^ra-modmain]: RealAtmospheres ModMain.cs
[^telemetry-mod]: KSA-Public-Telemetry TelemetryOverlayMod.cs
[^structures-mod]: KSAStructures KSAStructuresMod.cs
[^afc-mod]: KSA-AdvancedFlightComputer AdvancedFlightComputer/Mod.cs
[^ksaero-mod]: KSAero/Mod.cs
[^partsnow-meshbudget]: unscience parts-now.lib/Runtime/MeshBudget.cs
[^ksa-program]: KSA.Program (decompiled)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled)
[^ksa-mod]: KSA.Mod (decompiled)
[^ksa-loading]: KSA.Loading (decompiled)
