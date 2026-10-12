---
type: How-To
title: Build a minimal StarMap mod by hand from the discovery rules
description: Create the smallest folder StarMap will load - a mod.toml with only a name, one StarMapMod class, a csproj with Private=false game references - and see it log
category: getting-started
group: loader
topic: create-a-minimal-starmap-mod
approach: 1
difficulty: beginner
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/starmap/features/manifest-mod-discovery
  - mods/starmap/features/attribute-hook-discovery
  - mods/kitten-engineer-redux/features/dist-zip-and-post-build-deploy
techniques: [starmap-lifecycle, toml-config]
hooks:
  - starmap StarMapMod
  - starmap StarMapBeforeMain
  - starmap StarMapImmediateLoad
  - starmap StarMapAllModsLoaded
  - starmap StarMapUnload
lifecycle: [startup:starmap-before-main, startup:immediate-load, startup:all-mods-loaded]
game_types: [KSA.Mod, KSA.ModLibrary, KSA.ModEntry]
tags: [starmap, mod-toml, csproj, skeleton, getting-started]
---

This article builds the smallest mod StarMap will load, using nothing but the discovery rules: a
folder, a `mod.toml` with a `name`, a DLL named after the folder, and one `[StarMapMod]` class. It
ends with the mod writing to the game console and the KSA log. Prerequisite: StarMap installed and
launching KSA (run `StarMap.exe` from its own folder; see [How StarMap launches KSA and loads your mod](../how-starmap-launches-ksa/overview.md)).
[Kitten-Engineer-Redux](https://github.com/BarneyCompiler/Kitten-Engineer-Redux) is the closest real
example: its whole manifest is one line and its entry class is 20 lines.[^ker-modtoml][^ker-mod]
The alternative of cloning a ready-made scaffold is [Copy and rename the repo template with mkmod.ts (plus: ISubmod contract with a supermod host)](./create-a-minimal-starmap-mod-2.md).

## Background: how the game does it

KSA has its own data-only mod system: folders under `Content/` or `<Documents>/My Games/Kitten Space
Agency/mods/` that contain a `mod.toml`, listed in a `manifest.toml`. `ModLibrary.PrepareManifest`
adds any folder it finds that is not yet listed as a `ModEntry` with `Enabled = false` and
`New = true`, and the manifest id is the **folder name**.[^ksa-modlibrary] `Mod.MakeUsing` then reads
`mod.toml` into a `KSA.Mod`; keys it does not know (such as `[StarMap]`) are ignored.[^ksa-mod]

StarMap reuses that manifest before the game starts. For each enabled entry, `RuntimeMod.TryCreateMod`
finds `mod.toml`, reads an optional `[StarMap]` table, and, when there is none, sets
`EntryAssembly = <manifest id>`. It then looks for `<mod folder>/<EntryAssembly>.dll`; if the file
is missing the mod is skipped **silently**.[^sm-runtimemod] Inside the entry assembly it takes the
first type carrying an attribute named `StarMapModAttribute`, instantiates it with the public
parameterless constructor and binds its public hook methods.[^sm-runtimemod] So a minimal mod needs
no `[StarMap]` table, provided the folder name, the DLL name and the manifest id agree. The full
walk is in [Discover code mods through KSA's mod manifest](../../../../mods/starmap/features/manifest-mod-discovery.md) and [Discover lifecycle hooks by attribute type name](../../../../mods/starmap/features/attribute-hook-discovery.md).

## Lifecycle and timing

Every method below runs once on the main thread, in this order (vocabulary and exact game state in
[StarMap lifecycle attributes](../../lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md)):

| Hook | Lifecycle value | State you can rely on |
|---|---|---|
| `[StarMapBeforeMain]` | `startup:starmap-before-main` | `KSA.dll` loaded, no window, no KSA log sinks; only `Console.WriteLine` is safe for logging |
| `[StarMapImmediateLoad]` (needs a `KSA.Mod` parameter) | `startup:immediate-load` | Logging, window and ImGui exist; your `Mod` is registered with `ModLibrary` but no assets are loaded |
| `[StarMapAllModsLoaded]` | `startup:all-mods-loaded` | Postfix on `ModLibrary.LoadAll`: assets deserialized, every StarMap mod initialized, no scene yet; the usual place to install Harmony patches |
| `[StarMapUnload]` | `event:shutdown` | After `Program.Main` returned and StarMap already removed all Harmony patches |

Which of the three startup hooks to pick for real work is the subject of [Choose and use the right StarMap lifecycle hook](../../lifecycle/choose-a-starmap-startup-hook/overview.md).

## Steps

1. **Create the folder and decide the id.** Pick a folder name with no spaces, for example `HelloKsa`.
   This single string is the manifest id, the default `EntryAssembly` and the DLL file name. Build
   output must therefore contain `HelloKsa.dll`.

2. **Write `mod.toml`.** The smallest valid file is one line, exactly what Kitten-Engineer-Redux ships:[^ker-modtoml]

   ```toml
   # Kitten-Engineer-Redux/mod.toml
   name = "KittenEngineerRedux"
   ```

   `name` only fills `Mod.Name` (display name); the id comes from the folder. You do not need a
   `[StarMap]` table. Add one only to name a different entry DLL (see [Write mod.toml - KSA keys and the [StarMap] table](../mod-toml-reference/overview.md)).

3. **Create the project.** One class library targeting `net10.0`. NuGet supplies `StarMap.API` (the
   attributes) and `Lib.Harmony`; the game DLLs are referenced by `HintPath` with `Private=false` so
   they are never copied into your output. Copying `KSA.dll` or `0Harmony.dll` next to your mod gains
   nothing, because StarMap's load context always hands your assembly the already-loaded game and
   Harmony copies (details in [Ship third-party DLLs next to your mod](../../dependencies/ship-third-party-dlls-with-a-mod/overview.md)).

   ```xml
   <!-- adapted from Kitten-Engineer-Redux/KittenEngineerRedux.csproj (references) -->
   <Project Sdk="Microsoft.NET.Sdk">
     <PropertyGroup>
       <TargetFramework>net10.0</TargetFramework>
       <AssemblyName>HelloKsa</AssemblyName>
       <Nullable>enable</Nullable>
       <KsaDir Condition="'$(KsaDir)' == ''">C:\Program Files\Kitten Space Agency\</KsaDir>
     </PropertyGroup>
     <ItemGroup>
       <PackageReference Include="StarMap.API" Version="0.3.6" />
       <PackageReference Include="Lib.Harmony" Version="2.4.2" PrivateAssets="all" />
     </ItemGroup>
     <ItemGroup>
       <Reference Include="KSA">
         <HintPath>$(KsaDir)KSA.dll</HintPath>
         <Private>false</Private>
       </Reference>
       <Reference Include="Brutal.Core.Logging">
         <HintPath>$(KsaDir)Brutal.Core.Logging.dll</HintPath>
         <Private>false</Private>
       </Reference>
     </ItemGroup>
     <ItemGroup>
       <None Update="mod.toml">
         <CopyToOutputDirectory>Always</CopyToOutputDirectory>
       </None>
     </ItemGroup>
   </Project>
   ```

   Add further `Brutal.*.dll` references the same way as you use their types. Kitten-Engineer-Redux
   lists the full set it needs in [KittenEngineerRedux.csproj](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/KittenEngineerRedux.csproj#L1-L66); its versions of `StarMap.API` and
   `Lib.Harmony` (0.3.6 and 2.4.2) are the ones every mod in this corpus uses. Reference
   resolution for contributors and CI is covered in [Reference the game's assemblies from a mod project without shipping them](../../build/reference-game-assemblies-from-a-mod-project/overview.md).

4. **Write the entry class.** Public class, public parameterless constructor (implicit is fine), public
   hook methods with the exact parameter lists below. The shape is Kitten-Engineer-Redux's `Mod`
   plus the two startup hooks it does not use:[^ker-mod][^sm-baseattributes]

   ```csharp
   // adapted from Kitten-Engineer-Redux/Mod.cs L16-L37
   using Brutal.Logging;
   using HarmonyLib;
   using StarMap.API;

   namespace HelloKsa;

   [StarMapMod]
   public sealed class Mod
   {
       private static Harmony? _harmony;

       [StarMapBeforeMain]
       public void OnBeforeMain() => Console.WriteLine("HelloKsa: before main");

       [StarMapImmediateLoad]
       public void OnImmediateLoad(KSA.Mod definingMod) =>
           DefaultCategory.Log.Info($"HelloKsa: immediate load for mod id '{definingMod.Id}'");

       [StarMapAllModsLoaded]
       public void OnAllModsLoaded()
       {
           _harmony = new Harmony("com.example.helloksa");
           _harmony.PatchAll(typeof(Mod).Assembly);
           DefaultCategory.Log.Info("HelloKsa: loaded and patched");
       }

       [StarMapUnload]
       public void Unload() => _harmony = null;
   }
   ```

   `DefaultCategory.Log` is the game's own log category (`LogCategory.Make("Default")`);
   `Brutal.Logging` lives in `Brutal.Core.Logging.dll`, hence the second reference.[^ksa-defaultcategory]
   `PatchAll` applies every `[HarmonyPatch]` class in your assembly; with none it is a harmless no-op.

5. **Build and deploy.** Copy `HelloKsa.dll` and `mod.toml` into
   `<Documents>/My Games/Kitten Space Agency/mods/HelloKsa/` (automate this: [Deploy a mod on every build and debug it from an IDE](../../build/deploy-a-mod-on-every-build/overview.md)).
   Note that Kitten-Engineer-Redux's post-build target copies only the DLLs, not `mod.toml`, so a
   first-time deploy needs the manifest copied by hand.

6. **Run it.** Start the game through `StarMap.exe`. The first launch finds a folder not yet in
   `manifest.toml`, adds it as disabled and new, and KSA asks "A new mod with id 'HelloKsa' has been
   found, do you want it enabled?". Enable it. StarMap then offers a restart (the code was skipped
   this launch); choose Restart (the mechanics are in [Folder and manifest rules as StarMap consumes them](../how-starmap-launches-ksa/how-starmap-launches-ksa-2.md)).

7. **Check the output.** On the second launch the StarMap console shows `HelloKsa: before main`
   followed by `StarMap - Loaded mod: HelloKsa from manifest`[^sm-modloader] (`InitializeMod` invokes
   `[StarMapBeforeMain]` before it returns, and the loader prints the line afterwards). The two `Info` lines
   go through the game's logging: the `Program` constructor registers the in-game console, the process
   console and a log file under `Constants.LogsFolderPath` (`<Documents>/My Games/Kitten Space Agency/logs`)
   as sinks.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L689-L725) If you see nothing, work through the checklist below.

## Complete example

The reference implementation of this minimal shape is Kitten-Engineer-Redux; read it side by side:

| File | What it shows |
|---|---|
| [mod.toml](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/mod.toml) | One-line manifest, no `[StarMap]` table |
| [Mod.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Mod.cs) | `[StarMapMod]` class with `AllModsLoaded` and `Unload` |
| [KittenEngineerRedux.csproj](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/KittenEngineerRedux.csproj) | NuGet and `Private=false` references, `CopyToOutputDirectory` for `mod.toml`, post-build deploy |

## Variations and gotchas

- **Silent skips.** StarMap prints nothing when `mod.toml` or `<EntryAssembly>.dll` is missing, or when
  a hook method has the wrong signature or is not public. A missing `[StarMapMod]` class is reported
  only as `StarMap - Failed to initialize mod: <id> from manifest`, and a disabled entry as `Not loading
  mod: <id> because it is disabled in manifest`.[^sm-modloader] Check in order: folder name equals DLL
  name; entry DLL is in the folder; the mod is `enabled = true` in `manifest.toml`; the entry class has
  a public parameterless constructor and the hook methods are `public` (`Type.GetMethods()` returns
  public members only); `OnImmediateLoad` takes a `KSA.Mod`.[^sm-runtimemod]
- **A parameterless `[StarMapImmediateLoad]` is never called**; the signature requires exactly one
  `KSA.Mod` parameter, so use the exact form above.[^sm-baseattributes]
- **No exception handling.** StarMap invokes your hooks without a `try`, so a throw in
  `StarMapAllModsLoaded` ends startup. Wrap risky work, see [Keep mod exceptions out of KSA's frame loop](../../lifecycle/contain-exceptions-in-starmap-hooks/overview.md).
- **The StarMap API README is slightly off.** It shows `[StarMapBeforeMain]` on the `KSA.Mod`
  example; use `[StarMapImmediateLoad]`, and it describes `ExportedAssemblies` as effective though
  it is not at the pinned commit.[^sm-api-readme]
- **No mod needs `StarMap.API.dll` in its folder.** NuGet gives you compile-time types; at run time
  StarMap's own copy, loaded into the game load context beside `StarMap.Core`, is used.
- **Unload is not an unpatch point.** StarMap removes every Harmony patch before `StarMapUnload`
  runs (details in [Clean up on game exit and relaunch the game from a mod](../../lifecycle/clean-up-on-game-exit/overview.md)).

## See also

- [Copy and rename the repo template with mkmod.ts (plus: ISubmod contract with a supermod host)](./create-a-minimal-starmap-mod-2.md) - the same skeleton, cloned from a script, with a library project
- [Create a minimal StarMap mod](./overview.md) - comparison of both approaches
- [Discover code mods through KSA's mod manifest](../../../../mods/starmap/features/manifest-mod-discovery.md), [Discover lifecycle hooks by attribute type name](../../../../mods/starmap/features/attribute-hook-discovery.md),
  [Minimal mod.toml, post-build deploy and release zip](../../../../mods/kitten-engineer-redux/features/dist-zip-and-post-build-deploy.md)
- [Game loop and mod lifecycle](../../lifecycle/game-loop-and-lifecycle/overview.md) - frame order and hook timing
- [Write mod.toml - KSA keys and the [StarMap] table](../mod-toml-reference/overview.md), [Choose and use the right StarMap lifecycle hook](../../lifecycle/choose-a-starmap-startup-hook/overview.md), [Deploy a mod on every build and debug it from an IDE](../../build/deploy-a-mod-on-every-build/overview.md),
  [Build a KSA mod that references only StarMap.API and Harmony](../../build/build-a-mod-without-game-references/overview.md)

[^ker-modtoml]: Kitten-Engineer-Redux: mod.toml
[^ker-mod]: Kitten-Engineer-Redux: Mod.cs
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-mod]: KSA.Mod (decompiled, build 2026.10.10.5554)
[^sm-runtimemod]: StarMap: StarMap.Core/ModRepository/RuntimeMod.cs
[^sm-baseattributes]: StarMap: StarMap.API/BaseAttributes.cs
[^ksa-defaultcategory]: Brutal.Logging.DefaultCategory (decompiled, build 2026.10.10.5554)
[^sm-modloader]: StarMap: StarMap.Core/ModRepository/ModLoader.cs
[^sm-api-readme]: StarMap: StarMap.API/README.md
