---
type: Mod Feature
title: Minimal mod.toml, post-build deploy and release zip
description: Ships a one-line mod.toml that relies on StarMap's default entry assembly, copies the mod DLL and the ModMenu attribute DLL into the mods folder after each build, and commits a three-file release zip under dist/
mod: kitten-engineer-redux
category: getting-started
techniques: [msbuild-post-build-copy, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapUnload]
lifecycle: [startup:all-mods-loaded]
game_types: [KSA.ModLibrary]
tags: [build, deploy, packaging, mod-toml, starmap, msbuild, release, zip]
---

Kitten Engineer Redux is a single-project mod with no assets. Its packaging shows the smallest
footprint StarMap accepts: a `mod.toml` with only a `name`, the mod DLL named after the mod folder,
and one third-party DLL that has to sit next to it. A hard-coded MSBuild post-build target deploys
to the author's mods folder, and a prebuilt zip is committed for players.

## How it works

- **`mod.toml`.** The whole file is `name = "KittenEngineerRedux"` with no trailing newline and no
  `[StarMap]` table ([mod.toml](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/mod.toml)).[^ker-modtoml] StarMap's `RuntimeMod.TryCreateMod`
  then defaults `EntryAssembly` to the manifest id, i.e. the mod folder name (the game creates each
  `ModEntry` from the directory name, [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L355-L357)), and loads
  `<folder>/<folder>.dll` into a per-mod `ModAssemblyLoadContext`
  ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L36-L63)).[^starmap-runtimemod] [^ksa-modlibrary]
  The folder and the DLL must therefore both be `KittenEngineerRedux`; the `name` key is only the
  game's display name (`Mod.Name`) and plays no part in the lookup. If the DLL is missing,
  `TryCreateMod` returns `false` and `ModLoader` skips the mod without logging
  ([StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L69-L70)).
- **Entry class.** `[StarMapMod] public sealed class Mod` with `[StarMapAllModsLoaded] OnFullyLoaded`
  (creates `Harmony("com.matthew.kittenengineerredux")` and runs `PatchAll` on the assembly) and
  `[StarMapUnload] Unload` (unpatches by id and clears the analyzer pools)
  ([Mod.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Mod.cs#L16-L37)).[^ker-mod]
- **Project.** `net10.0`, nullable and implicit usings on. `Lib.Harmony` 2.4.2 with
  `PrivateAssets=all`, `StarMap.API` 0.3.6, `ModMenu.Attributes` 1.0.2 and
  `CommunityToolkit.HighPerformance` 8.4.2 from NuGet; KSA, Bepu and Brutal assemblies referenced from
  `C:\Program Files\Kitten Space Agency\` with `Private=false`. `CopyLocalLockFileAssemblies` is on,
  and `KSA_Source/**/*.cs` (a local decompile, git-ignored) is removed from compilation
  ([KittenEngineerRedux.csproj](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/KittenEngineerRedux.csproj#L1-L66)).[^ker-csproj] [^ker-gitignore]
- **Post-build deploy.** A `PostBuild` target (`AfterTargets="PostBuildEvent"`) creates
  `C:\Users\Matthew\Documents\My Games\Kitten Space Agency\mods\KittenEngineerRedux\` and copies
  `KittenEngineerRedux.dll` and, if present, `ModMenu.Attributes.dll` into it
  ([KittenEngineerRedux.csproj](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/KittenEngineerRedux.csproj#L68-L83)).
- **Dependency resolution at runtime.** StarMap's mod load context first returns assemblies already
  loaded in the default or game context (Harmony, StarMap.API, KSA, Brutal, and
  `CommunityToolkit.HighPerformance`, which the game ships), then falls back to an
  `AssemblyDependencyResolver` rooted at the mod DLL, which is how the bundled
  `ModMenu.Attributes.dll` is found
  ([StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L72)).[^starmap-modalc]
- **Release zip.** [dist/KittenEngineerRedux.zip](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/dist/KittenEngineerRedux.zip) holds exactly
  `KittenEngineerRedux.dll`, `mod.toml` and `ModMenu.Attributes.dll` at the zip root, to be extracted
  into a `KittenEngineerRedux` folder under the mods directory. There is no script that builds it.[^ker-dist]
- **Build stamp.** `BuildMarker.Stamp` is a `const string` shown in the HUD's first row, which makes
  it easy to tell which DLL the game actually loaded ([BuildMarker.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/BuildMarker.cs)).[^ker-buildmarker]

## Lifecycle and timing

- `[StarMapAllModsLoaded]` fires from StarMap's Harmony postfix on `ModLibrary.LoadAll`
  ([StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L17-L28)),
  before any universe, vehicle or editor exists. Installing patches there is safe because every KER
  patch targets UI methods that first run much later.[^starmap-modlibrarypatches]
- `[StarMapUnload]` runs from `ModLoader.Dispose` at shutdown, after the game's `Main` has returned
  ([StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L143-L151)).
  `StarMapCore.DeInit` calls `UnpatchAll()` without an id immediately before it, which removes every
  Harmony patch in the process ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)), so KER's
  `UnpatchAll(_harmony.Id)` finds nothing left to remove; only the pool clearing does any work.[^starmap-modloader] [^starmap-starmapcore]

## Key code

| Path | Role |
|---|---|
| [mod.toml](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/mod.toml) | Manifest name only |
| [KittenEngineerRedux.csproj](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/KittenEngineerRedux.csproj) | References, compile exclusions, post-build copy |
| [Mod.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Mod.cs) | StarMap entry, patch install and unload |
| [BuildMarker.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/BuildMarker.cs) | Version string shown in the HUD |
| [dist/KittenEngineerRedux.zip](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/dist/KittenEngineerRedux.zip) | Committed release archive |

## Snippets

```xml
<!-- KittenEngineerRedux.csproj L68-L83 -->
    <Target Name="PostBuild" AfterTargets="PostBuildEvent">
        <PropertyGroup>
            <SourceDll>$(TargetDir)KittenEngineerRedux.dll</SourceDll>
            <SourceAttributesDll>$(TargetDir)ModMenu.Attributes.dll</SourceAttributesDll>
            <DestinationDir>C:\Users\Matthew\Documents\My Games\Kitten Space Agency\mods\KittenEngineerRedux\</DestinationDir>
        </PropertyGroup>
        
        <Message Importance="High" Text="Executing Post-Build Deployment..." />
        
        <MakeDir Directories="$(DestinationDir)" Condition="!Exists('$(DestinationDir)')" />
        
        <Copy SourceFiles="$(SourceDll)" DestinationFolder="$(DestinationDir)" SkipUnchangedFiles="false" />
        <Copy SourceFiles="$(SourceAttributesDll)" DestinationFolder="$(DestinationDir)" SkipUnchangedFiles="false" Condition="Exists('$(SourceAttributesDll)')" />
        
        <Message Importance="High" Text="Mod successfully deployed to: $(DestinationDir)" />
    </Target>
```

```csharp
// Mod.cs L16-L37
[StarMapMod]
public sealed class Mod
{
    private static Harmony? _harmony;

    [StarMapAllModsLoaded]
    public void OnFullyLoaded()
    {
        _harmony = new Harmony("com.matthew.kittenengineerredux");
        _harmony.PatchAll(typeof(Mod).Assembly);
        DefaultCategory.Log.Info("[KER] Loaded and patched.");
    }

    [StarMapUnload]
    public void Unload()
    {
        _harmony?.UnpatchAll(_harmony.Id);
        _harmony = null;
        SequenceAnalyzer.ResetPools();
        DefaultCategory.Log.Info("[KER] Unloaded.");
    }
}
```

## Game APIs touched

| Member | Source | Use |
|---|---|---|
| `RuntimeMod.TryCreateMod` default `EntryAssembly` | [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L49-L55) | Lets `mod.toml` omit `[StarMap]` |
| `ModLibrary` mod discovery (`new ModEntry(modDir.Name, ...)`) | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L355-L357) | Manifest id comes from the folder name |
| `StarMapCore.DeInit` | [StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35) | Id-less `UnpatchAll` before `StarMapUnload` |
| `ModAssemblyLoadContext.Load` | [StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L72) | Resolves shared and bundled assemblies |

## Pitfalls and version notes

- The committed zip is stale: its DLL carries the build stamp `v0.5.2`, while `BuildMarker.cs` at the
  pinned commit says `v0.5.7`, and the zip has not changed since commit `8635580` (whose own
  `BuildMarker.cs` said `v0.5.5`); the DLL's zip timestamp (2026-10-02 21:39) matches commit `9ad3975`.
  Strings such as "Export CSV" and "Configure alerts" from the current source are absent from it.
  Build from source to get the analysed behaviour. The bundled `ModMenu.Attributes.dll` is assembly
  version 1.0.2, matching the project's package reference.
- The post-build target copies only the two DLLs, not `mod.toml`, even though the project marks
  `mod.toml` `CopyToOutputDirectory=Always`; a first-time deploy needs the manifest copied by hand.
- Hint paths and the deploy folder are absolute Windows paths for one machine; anyone else must edit
  the project (compare KSAero's parameterised `ModDeploy.targets`).
- The README carries no install steps or game-version statement; the mod declares no target KSA build
  anywhere in the repo.[^ker-readme]

## See also

- [ModMenu entry with a standalone menu fallback](modmenu-entry-with-standalone-fallback.md)
- [KSAero: build and deploy targets](../../ksaero/features/mod-deploy-targets.md)
- [Game loop and mod lifecycle](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Add a plain post-build copy with an overridable KSAGameDir](../../../howto/getting-started/build/deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-4.md) - how-to article demonstrating this feature
- [Copy a vendored DLL beside the mod and let AssemblyDependencyResolver find it](../../../howto/getting-started/dependencies/ship-third-party-dlls-with-a-mod/ship-third-party-dlls-with-a-mod-1.md) - how-to article demonstrating this feature
- [Build a minimal StarMap mod by hand from the discovery rules](../../../howto/getting-started/loader/create-a-minimal-starmap-mod/create-a-minimal-starmap-mod-1.md) - how-to article demonstrating this feature
- [Write mod.toml: KSA keys and the StarMap table](../../../howto/getting-started/loader/mod-toml-reference/mod-toml-reference.md) - how-to article demonstrating this feature

[^ker-modtoml]: mod.toml
[^starmap-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^ker-mod]: Mod.cs
[^ker-csproj]: KittenEngineerRedux.csproj
[^ker-gitignore]: .gitignore
[^starmap-modalc]: StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
[^ker-dist]: dist/KittenEngineerRedux.zip
[^ker-buildmarker]: BuildMarker.cs
[^starmap-modlibrarypatches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^starmap-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^ker-readme]: README.md
[^starmap-starmapcore]: StarMap.Core/StarMapCore.cs
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
