---
type: Mod
title: StarMap
description: The community code-mod loader for KSA, which launches the game in its own process, loads each mod's DLL into an isolated AssemblyLoadContext, resolves mod dependencies, and calls mods back at seven lifecycle points through Harmony patches
mod: starmap
tags: [starmap, loader, mod-loader, lifecycle, harmony, assembly-load-context, mod-toml, manifest]
repo: StarMap
license: MIT
author: KlaasWhite
loader: loader
---

StarMap is the loader nearly every code mod in this corpus targets. KSA itself only loads data
mods (XML assets, systems, fonts listed in `mod.toml`); it has no plugin API. StarMap fills that
gap from the outside: the player starts `StarMap.exe` instead of the game, StarMap loads `KSA.dll`
into its own process, loads each enabled mod's DLL into a separate load context, Harmony-patches a
handful of game methods, and from those patches calls methods that mods mark with attributes from
the `StarMap.API` NuGet package. The README calls it "a POC/Prototype arbitrary code modloader";
it credits Lexi's KSALoader as prior art.[^sm-readme] The repository belongs to the StarMapLoader
GitHub organization; 66 of its 67 commits are by KlaasWhite. The pinned commit is on `dev` and has
the same tree as release tag 0.4.7.

## Install footprint

StarMap is not itself a KSA mod: it has no `mod.toml`, no manifest entry and no mod id. A player
installs it as a separate program next to (not inside) the game.

| Item | Location | Content |
|---|---|---|
| Loader folder | anywhere; the installer defaults to `Program Files\StarMap` | `StarMap.exe`, `StarMap.Core.dll`, `StarMap.API.dll`, `StarMap.Types.dll`, `0Harmony.dll`, runtime config files |
| `StarMapConfig.json` | loader folder (relative to the working directory) | `GameLocation` (KSA folder or `KSA.dll`), `RepositoryLocation` (unused), `GameArguments` |
| Mods | `Documents/My Games/Kitten Space Agency/mods/<id>/` (recommended) or `<KSA install>/Content/<id>/` | `mod.toml` plus the entry DLL and any private dependencies |
| Manifest | `Documents/My Games/Kitten Space Agency/manifest.toml` | KSA's own list of mods and their enabled flags |

Repository layout ([StarMap.slnx](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.slnx)):[^sm-sln]

| Project | Role |
|---|---|
| `StarMap/` | The launcher executable: config, load contexts, entry-point invocation |
| `StarMap.Core/` | Runs inside the game's load context: mod repository, Harmony patches, restart UI |
| `StarMap.API/` | The attributes mods compile against (published to NuGet as `StarMap.API`) |
| `StarMap.Types/` | `IStarMapCore`, shared between launcher and core |
| `installer/`, `.github/workflows/` | Inno Setup script and CI |

## Loader model: what happens to your DLL

1. The player runs `StarMap.exe` from its folder. It reads `StarMapConfig.json`, loads
   `0Harmony.dll` into the default load context, creates a load context for the game, loads
   `StarMap.Core` and `KSA.dll` into it, and points the working and base directories at the KSA
   folder. See [host process launch](features/host-process-launch.md).
2. If `-InstancePath` / `STARMAP_INSTANCE_PATH` is set, the KSA documents folder is redirected.
   See [instance path override](features/instance-path-override.md).
3. StarMap asks KSA's `ModLibrary` for the manifest and walks the enabled entries in order. For each
   it reads `mod.toml`, takes `[StarMap] EntryAssembly` (default: the folder name) and loads
   `<EntryAssembly>.dll` from the mod folder into a new `ModAssemblyLoadContext`. See
   [manifest discovery](features/manifest-mod-discovery.md) and
   [per-mod load contexts](features/per-mod-assembly-load-context.md).
4. Declared `[[StarMap.ModDependencies]]` decide the initialization order and which assemblies
   are shared between mods. See [dependency graph](features/mod-dependency-graph.md) and
   [shared contract assemblies](features/shared-contract-assemblies.md).
5. When a mod is initialized, StarMap finds the first class carrying `[StarMapMod]`, creates one
   instance with its parameterless constructor, binds every public method carrying a hook attribute
   with a valid signature, and immediately calls its `[StarMapBeforeMain]` method. See
   [attribute hook discovery](features/attribute-hook-discovery.md).
6. StarMap applies its own Harmony patches (id `com.starmap.core`) and invokes KSA's `Main`.
7. Inside the `Program` constructor, StarMap's patches fire `[StarMapImmediateLoad]` per mod and
   `[StarMapAllModsLoaded]` once; if the player just enabled a new mod, StarMap offers a restart.
   See [startup hooks](features/startup-lifecycle-hooks.md) and
   [restart flow](features/new-mods-restart-flow.md).
8. Every frame, `[StarMapBeforeGui]`, `[StarMapAfterGui]` and `[StarMapAfterOnFrame]` fire. See
   [frame hooks](features/frame-lifecycle-hooks.md).
9. When the game exits normally, StarMap removes all Harmony patches and calls
   `[StarMapUnload]`. See [unload and shutdown](features/unload-and-shutdown.md).

## Lifecycle hooks

Every hook is a public method on the `[StarMapMod]` class returning `void`. Invocation is by
reflection, on the main thread, across mods in initialization order, with no exception handling.
Line numbers refer to decompiled build 2026.10.10.5554.[^ksa-program][^ksa-modlibrary]

| Attribute | Signature | Attach point | Position | Lifecycle | State available |
|---|---|---|---|---|---|
| `StarMapBeforeMain` | `()` | Called by StarMap right after the mod is initialized | Before `Program.Main` and before StarMap's own Harmony patches | `startup:starmap-before-main` | `KSA.dll` loaded, manifest read; no window, renderer, ImGui or KSA logging, and `GameSettings` holds defaults only. Patch code that runs during startup here |
| `StarMapImmediateLoad` | `(KSA.Mod definingMod)` | Harmony prefix on `Mod.PrepareSystems` | `ModLibrary.PrepareAll`, `Program.cs` line 899 | `startup:immediate-load` | Window, Vulkan renderer, ImGui context, settings, loading screen; no assets yet. `definingMod.DirectoryPath` is the mod folder |
| `StarMapAllModsLoaded` | `()` | Harmony postfix on `ModLibrary.LoadAll` | `Program.cs` line 942 | `startup:all-mods-loaded` | All asset bundles, fonts and systems loaded CPU-side; no GPU upload, viewports, game data attachment or universe yet |
| `StarMapBeforeGui` | `(double dt)` | Harmony prefix on `Program.OnDrawUiFrame` | `Program.OnFrame`, after `ImGui.NewFrame` and viewports, only if `DrawUI` | `frame:draw-ui` | ImGui frame open, nothing drawn yet; physics results applied and next step already running |
| `StarMapAfterGui` | `(double dt)` | Harmony postfix on `Program.OnDrawUiViewports` | After static windows and secondary-viewport UI, before menu bar and vehicle windows, only if `DrawUI` | `frame:draw-ui` | Same as above; the usual place to draw mod windows |
| `StarMapAfterOnFrame` | `(double currentPlayerTime, double dtPlayer)` | Harmony postfix on `Program.OnFrame` | After render and present; also on frames that return early (window close, font rebuild, failed swapchain acquire) | `frame:after-on-frame` | No ImGui frame open; next physics step in flight; fires even with the HUD hidden |
| `StarMapUnload` | `()` | Called from `ModLoader.Dispose` after KSA's `Main` returns | After `Program.Dispose` and an id-less `UnpatchAll()` that removes every Harmony patch in the process | `event:shutdown` | Managed state only; renderer, window, ImGui backend and audio are gone. Skipped on `Environment.Exit` paths |

`dt` and `dtPlayer` are real seconds per frame (clamped by the minimum target frame rate), and
`currentPlayerTime` is wall-clock seconds since the frame loop started; none is simulation time.
StarMap offers no hook for "scene loaded"; mods that need the universe or vehicles initialize lazily
from a frame hook. Because `StarMapBeforeGui` and `StarMapAfterGui` stop while the HUD is hidden,
unscience ships a helper that replays them from a prefix on `Program.OnDrawUiConsole`.[^hiddenui]

## KSA's native mod system and the [StarMap] section

| Concern | KSA (native) | StarMap |
|---|---|---|
| Discovery | Folders with `mod.toml` in `Content/` and `<Documents>/mods/` | Same, through `ModLibrary.PrepareManifest` |
| Identity | Manifest id = folder name; `name` is a display name | Same id; dependencies and lookups use it |
| Enable / order | `manifest.toml` `[[mods]] id, enabled`, file order = load order | Same; disabled entries get no code loaded |
| Content | `assets`, `systems`, `fonts`, `planetMeshes`, `planetRandomHeightmapCollections`, `starBinaries`, settings lists, `[console]`[^ksa-mod] | Not involved; loaded by KSA |
| Code | none | `[StarMap] EntryAssembly`, `ExportedAssemblies`, `[[StarMap.ModDependencies]]` |
| New mods | "MOD FOUND" popup per new folder | Restart prompt if a new mod was enabled |

A StarMap mod is therefore a KSA content mod plus an entry assembly in the same folder. KSACars'
`mod.toml` says it in one line: "KSA reads this as a content mod, StarMap reads it as a code mod.
One folder does both."[^ksacars-modtoml] Keys such as `patches` belong to other mods
(KittenExtensions), not to KSA or StarMap.

## Worked minimal mod

Assembled from KSAero, the smallest complete StarMap mod in this corpus, plus the dependency and
export declarations of purrTTY and gatOS. All snippets are verbatim.

Installed layout: KSAero's build copies exactly two files into
`Documents/My Games/Kitten Space Agency/mods/KSAero/`, namely `KSAero.dll` and `mod.toml`.[^ksaero-moddeploy]

`mod.toml`: the folder name, `name` and `EntryAssembly` all agree.[^ksaero-modtoml]

```toml
# KSAero/mod.toml L1-L4
name = "KSAero"

[StarMap]
EntryAssembly = "KSAero"
```

Entry class: one `[StarMapMod]` class, public hook methods with the exact signatures.[^ksaero-mod]

```csharp
// KSAero/Mod.cs L1-L23, L36-L52
using Brutal.Logging;
using HarmonyLib;
using KSA;
using StarMap.API;

namespace KSAero;

[StarMapMod]
public sealed class Mod
{
    private const string TestedGameVersion = "v2026.10.7.5541";
    private const string HarmonyId = "ksaero";

    private static Harmony? _harmony;

    [StarMapAllModsLoaded]
    public void OnFullyLoaded()
    {
        string gameVersion = VersionInfo.Current.VersionString;
        if (gameVersion != TestedGameVersion)
            DefaultCategory.Log.Warning($"[KSAero] Tested against {TestedGameVersion}, current is {gameVersion}.");

        var harmony = new Harmony(HarmonyId);
        // ...
    }

    [StarMapAfterGui]
    public void DrawGui(double dt)
    {
        if (_harmony != null)
            AeroReadout.Draw();
    }

    [StarMapUnload]
    public void Unload()
    {
        _harmony?.UnpatchAll(HarmonyId);
        _harmony = null;
        DefaultCategory.Log.Info("[KSAero] Unloaded.");
    }
}
```

Project references: game assemblies with `Private=false` so they are never copied, Harmony and
`StarMap.API` from NuGet (StarMap supplies both at run time).[^ksaero-csproj]

```xml
<!-- KSAero/KSAero.csproj L11-L20 -->
<ItemGroup>
  <PackageReference Include="Lib.Harmony" Version="2.4.2" />
  <PackageReference Include="StarMap.API" Version="0.3.6" />
</ItemGroup>

<ItemGroup>
  <Reference Include="KSA">
    <HintPath>$(KsaDir)\KSA.dll</HintPath>
    <Private>false</Private>
  </Reference>
```

Exporting a contract for other mods (purrTTY), and depending on it optionally while importing it
(gatOS):[^purrtty-modtoml][^gatos-modtoml]

```toml
# purrtty: purrTTY.GameMod/mod.toml L6-L7, L12
[StarMap]
EntryAssembly = "purrTTY.GameMod"
# ...
ExportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]
```

```toml
# gatOS: gatOS.GameMod/mod.toml L6-L7, L14-L17
[StarMap]
EntryAssembly = "gatOS.GameMod"
# ...
[[StarMap.ModDependencies]]
ModId = "purrTTY"
Optional = true
ImportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]
```

At the pinned commit only the importer's `ImportedAssemblies` decides what is shared; the
exporter's list is parsed but not applied (see
[shared contract assemblies](features/shared-contract-assemblies.md)), so keep both lists identical
as these two mods do.

Checklist distilled from the loader code:

- `[StarMapMod]` on exactly one class in the entry assembly (StarMap takes the first type
  `GetTypes()` returns that carries it), with a public parameterless constructor.
- Hook methods `public`, `void`, with the parameter list from the table above; wrong signatures are
  ignored silently. `StarMapImmediateLoad` must take the `KSA.Mod` parameter: a parameterless
  version is never called.
- `EntryAssembly` equals the DLL file name without `.dll`; `ModId` in dependencies equals the
  dependency's folder name.
- Do not ship `0Harmony.dll` or `StarMap.API.dll`; ship your `.deps.json` if you have private
  dependencies; list `ImportedAssemblies` explicitly for shared contracts.
- Run `StarMap.exe` with its own folder as the working directory (KSAero's build writes an IDE
  launch profile that does this).

## Dependencies

| Dependency | How it is resolved |
|---|---|
| .NET 10 | StarMap is a framework-dependent `net10.0` app; KSA runs on the runtime StarMap starts |
| Harmony (`Lib.Harmony` 2.4.2) | Shipped as `0Harmony.dll` and loaded into the default load context first, so all mods share it |
| Tomlet | Compile-only in `StarMap.Core`; the game's copy is used at run time[^sm-core-csproj] |
| KSA | `RocketWerkz.KSA.Ref` 2026.9.10.5438 reference package at build time; the installed `KSA.dll` at run time |

StarMap has no dependency on other mods. Infrastructure mods such as KittenExtensions and
ShaderExtensions are ordinary StarMap mods with a `[StarMapMod]` class of their own.

## Architecture summary

| Area | Types | Thread | Notes |
|---|---|---|---|
| Launcher | `Program`, `LoaderConfig`, `CoreAssemblyLoadContext`, `GameSurveyer` | main | Builds the process and calls `KSA.Program.Main` |
| Core | `StarMapCore` (`IStarMapCore`) | main | Owns the `ModLoader` and the `com.starmap.core` Harmony instance |
| Mod repository | `ModLoader`, `ModRegistry`, `RuntimeMod`, `ModAssemblyLoadContext`, `StarMapConfig` | main (resolution: any) | Discovery, dependency graph, hook registry |
| Patches | `ProgramPatcher`, `ModLibraryPatches`, `ModPatches`, `DocumentsPathPatches` | main | The only places StarMap code runs during the game[^sm-programpatcher][^sm-modlibrarypatches][^sm-modpatches] |
| UI | `ConfirmRestart`, `ConfirmRestartPopup` | main | Restart prompt during loading |
| API | `StarMapModAttribute`, `StarMapMethodAttribute` and seven hook attributes | n/a | Compile-time surface for mods |

Data flow is one-way: StarMap calls mods; mods never call StarMap (the API has no services, only
attributes).

## Features

| Feature | Category | Lifecycle |
|---|---|---|
| [Host KSA in-process from a launcher executable](features/host-process-launch.md) | getting-started | startup:starmap-before-main |
| [Discover code mods through KSA's mod manifest](features/manifest-mod-discovery.md) | getting-started | startup:starmap-before-main, startup:asset-load |
| [Isolate each mod in its own AssemblyLoadContext](features/per-mod-assembly-load-context.md) | getting-started | startup:starmap-before-main |
| [Order mod loading with a ModDependencies graph](features/mod-dependency-graph.md) | getting-started | startup:starmap-before-main |
| [Share contract assemblies between mods](features/shared-contract-assemblies.md) | external-io | startup:starmap-before-main |
| [Discover lifecycle hooks by attribute type name](features/attribute-hook-discovery.md) | getting-started | startup:starmap-before-main |
| [Startup lifecycle hooks](features/startup-lifecycle-hooks.md) | getting-started | startup:starmap-before-main, startup:immediate-load, startup:all-mods-loaded |
| [Frame lifecycle hooks](features/frame-lifecycle-hooks.md) | getting-started | frame:draw-ui, frame:after-on-frame |
| [Unload hook and shutdown order](features/unload-and-shutdown.md) | getting-started | event:shutdown |
| [Restart when new mods are enabled](features/new-mods-restart-flow.md) | getting-started | startup:asset-load |
| [Startup modal popup with a self-pumped frame](features/startup-modal-popup.md) | ui | startup:asset-load |
| [Redirect the KSA documents folder per instance](features/instance-path-override.md) | persistence | startup:starmap-before-main |
| [Release packaging, StarMap.API NuGet and Windows installer](features/release-packaging-and-installer.md) | testing-and-tooling | (build time) |

## Build, test and tooling

- Four projects in `StarMap.slnx`, configurations Debug (KSA from a local `Import` folder),
  Release (`RocketWerkz.KSA.Ref` package) and SourceNuget (`RocketWerkz.KSA` package).
- No automated tests; the test step in `pr-build.yml` is commented out.
- CI publishes a win-x64 framework-dependent zip, an Inno Setup installer and the `StarMap.API`
  NuGet package; details in [release packaging](features/release-packaging-and-installer.md).
- Logging is plain `Console.WriteLine` with a `StarMap - ` prefix, visible in the console window of
  `StarMap.exe`.

## Compatibility notes

- Compiled against KSA 2026.9.10.5438. Every game member StarMap binds to (`Program.OnFrame`,
  `OnDrawUiFrame`, `OnDrawUiViewports`, `Mod.PrepareSystems`, `ModLibrary.PrepareAll`, `LoadAll`,
  `PrepareManifest`, `HasNewMods`, `Manifest`, `LocalModsFolderPath`, `ModEntry.New` / `Enabled`,
  `Constants.DocumentsFolderPath`) exists with the same name and parameter names in decompiled
  build 2026.10.10.5554.
- Breakage pattern 1: method renames. KSA 2026.4.6.4036 replaced `Program.OnDrawUi` with
  `OnDrawUiFrame` / `OnDrawUiViewports`; StarMap retargeted its patches in commit 93cd07c. Harmony
  also binds patch parameters by name, so a parameter rename breaks `PatchAll` at startup.
- Breakage pattern 2: startup assumptions. KSA r3646 began resetting the working directory from
  `AppDomain.CurrentDomain.BaseDirectory`; StarMap fixed it by setting `APP_CONTEXT_BASE_DIRECTORY`
  (commit 21dcef7).
- `ConfirmRestart` is a copy of an older `KSA.ConfirmMods.OnFrame`; the current game version waits
  for the previous present and records input sample time, which StarMap's copy does not.
- Several behaviours read from the code differ from the API README (default assembly sharing,
  `ExportedAssemblies`, the `StarMapImmediateLoad` example, the "OnDrawUi" wording); the feature
  pages note each one.

## Legacy code

`StarMap.Core/Legacy/ModManagerScreen.cs` is entirely commented out. It is a console menu for a mod
store with managed and unmanaged mods and version selection, built on IPC types
(`StarMap.Types.Proto.IPC`) that no longer exist; it matches the README's "future plans" of a
Factorio-style in-game mod manager using two processes.[^sm-legacy][^sm-readme] gatOS vendors an
older StarMap source tree under `thirdparty/starmap` whose `StarMap.Launcher` project supervised the
game as a separate process over named pipes (also commented out there), which shows the design the
current single-process loader replaced.[^gatos-old-starmap]

## Secondary sources

- [README.md](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/README.md): install steps and mod locations.
- [StarMap.API/README.md](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/README.md): the upstream mod creation guide, dependency strategy and attribute reference; read alongside the corrections in the feature pages.[^sm-api-readme]
- StarMap-ExampleMods (linked from the README, not checked out in this corpus).
- [ksa-abstractions.lib/HiddenUiFrameHook.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/HiddenUiFrameHook.cs) in unscience: a precise write-up of where the GUI hooks sit.

[^sm-readme]: StarMap README
[^sm-api-readme]: StarMap.API README
[^sm-sln]: StarMap.slnx
[^sm-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^sm-modlibrarypatches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^sm-modpatches]: StarMap.Core/Patches/ModPatches.cs
[^sm-legacy]: StarMap.Core/Legacy/ModManagerScreen.cs
[^sm-core-csproj]: StarMap.Core/StarMap.Core.csproj
[^ksaero-mod]: KSAero/Mod.cs
[^ksaero-modtoml]: KSAero/mod.toml
[^ksaero-csproj]: KSAero/KSAero.csproj
[^ksaero-moddeploy]: KSAero build/ModDeploy.targets
[^purrtty-modtoml]: purrtty purrTTY.GameMod/mod.toml
[^gatos-modtoml]: gatOS gatOS.GameMod/mod.toml
[^gatos-old-starmap]: gatOS thirdparty/starmap
[^ksacars-modtoml]: KSACars src/KSACars/mod.toml
[^hiddenui]: unscience ksa-abstractions.lib/HiddenUiFrameHook.cs
[^ksa-program]: KSA.Program (decompiled)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled)
[^ksa-mod]: KSA.Mod (decompiled)
