---
type: How-To
title: Ship mixed content with a csproj content copy, an assets guard and a build stamp
description: KSAGolf's variant of the one-folder content-and-code mod - assets XML at the root, glb/png/wav copied beside the DLL, a script that checks the assets list, and the KSA build stamped into the assembly
category: getting-started
group: loader
topic: ship-content-and-code-in-one-mod
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/content-and-code-mod-layout]
techniques: [toml-config, msbuild, xml-asset]
hooks: [starmap StarMapImmediateLoad, starmap StarMapAllModsLoaded]
lifecycle: [startup:immediate-load, startup:asset-load, startup:all-mods-loaded]
game_types: [KSA.Mod, KSA.ModLibrary, KSA.FileReference, KSA.AssetBundle]
tags: [mod-toml, assets, csproj, msbuild, build-stamp, validation, ksagolf]
---

KSAGolf is the second worked example of a folder that KSA reads as content and StarMap reads as code. It adds
three practical habits to the flat layout of [Flat folder with an explicit assets list](./ship-content-and-code-in-one-mod-1.md): a guard that catches an
unlisted asset XML before launch, a csproj whose output folder is the install folder, and the targeted KSA build
stamped into the DLL so the log can compare it with the running game. One correction to the usual shorthand:
`mod.toml` lists only the asset **XML** files; the glb, png and wav files are pulled in by `Path` attributes
inside those XML files and need to exist beside the DLL, so the csproj copies them.[src/KSAGolf/mod.toml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/mod.toml)

## Background: how the game does it

The rules are those of the flat layout: `Mod.LoadAssetBundles` loads each `assets` entry as an `AssetBundle`
relative to the folder of `mod.toml`;[current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L157-L169) each `<Source Path=...>`, `<Diffuse Path=...>` or
`<SoundFile Path=...>` inside is resolved by `FileReference.ModPath` = `Mod.GetPath(LocalPath)` =
`Path.Combine(DirectoryPath, localPath)`.[current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L13-L26) KSA finds the folder through
`ModLibrary.PrepareAll`, which tries `Content/<id>` and then `ModLibrary.LocalModsFolderPath`
(`<Documents>/My Games/Kitten Space Agency/mods/<id>`).[current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L447)

A sound asset shows how binary files are referenced from XML:

```xml
<!-- src/KSAGolf/KSAGolfSounds.xml L4-L13 -->
<Assets>
    <Sound Id="KSAGolf_PuttHard" ChannelId="ExternalChannel" SimSpeed="OnlyLower">
        <SoundFile Path="Sounds/KSAGolf_PuttHard1.wav"/>
        <SoundFile Path="Sounds/KSAGolf_PuttHard2.wav"/>
        <SpatialSoundData>
            <VolumeRange Min="1" Max="120"/>
            <TwoDRange Min="0" Max="0"/>
        </SpatialSoundData>
    </Sound>
```

## Lifecycle and timing

| Moment | What happens |
|---|---|
| `startup:immediate-load` | StarMap's prefix on `Mod.PrepareSystems` calls `OnImmediateLoad(Mod)`; KSAGolf logs the mod id and the built-for versus running KSA build |
| `startup:asset-load` | `ModLibrary.LoadAll` calls `Mod.LoadAssetBundles()` per mod |
| `startup:all-mods-loaded` | `OnFullyLoaded` installs all Harmony patches and builds the panel; asset ids such as `KSAGolf_Ball_Glb` are registered by now |

Hooks are explained in [Choose and use the right StarMap lifecycle hook](../../lifecycle/choose-a-starmap-startup-hook/overview.md); the frame order in [Game loop and mod lifecycle](../../lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Write `mod.toml`** with the assets listed one by one and the `[StarMap]` table:

   ```toml
   # src/KSAGolf/mod.toml L1-L11
   # KSA reads this as a content mod, StarMap reads it as a code mod. One folder does both.

   name = "KSAGolf"

   # Asset definitions, listed one by one rather than globbed: a file left out of this list is never
   # loaded, and nothing reports it. They sit at the mod root beside Meshes/ and Textures/, so their
   # relative paths resolve the same whether read against the mod root or the XML's own directory.
   assets = ["KSAGolfProps.xml", "KSAGolfSounds.xml", "KSAGolfCourse.xml"]

   [StarMap]
   EntryAssembly = "KSAGolf"
   ```

2. **Make `bin/<config>/net10.0` mirror the install folder.** The csproj copies `mod.toml`, the XML and the
   binary assets:[src/KSAGolf/KSAGolf.csproj](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolf.csproj#L30-L47)

   ```xml
   <!-- src/KSAGolf/KSAGolf.csproj L30-L47 -->
     <ItemGroup>
       <None Update="mod.toml">
         <CopyToOutputDirectory>Always</CopyToOutputDirectory>
       </None>
       <!-- Part and asset definitions, listed one by one in mod.toml's assets array. -->
       <None Update="KSAGolf*.xml">
         <CopyToOutputDirectory>Always</CopyToOutputDirectory>
       </None>
       <None Update="Meshes\**\*.glb">
         <CopyToOutputDirectory>Always</CopyToOutputDirectory>
       </None>
       <None Update="Textures\**\*.png">
         <CopyToOutputDirectory>Always</CopyToOutputDirectory>
       </None>
       <None Update="Sounds\**\*.wav">
         <CopyToOutputDirectory>Always</CopyToOutputDirectory>
       </None>
     </ItemGroup>
   ```

3. **Stamp the targeted KSA build into the assembly** so a log line can say "built for X, running Y". The
   csproj reads the `build` line of a lock file (with a regex over the file text, elided below) and emits
   `AssemblyMetadata`:[src/KSAGolf/KSAGolf.csproj](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolf.csproj#L83-L94)

   ```xml
   <!-- src/KSAGolf/KSAGolf.csproj L83-L94 -->
     <PropertyGroup>
       <KsaLockPath>$(MSBuildThisFileDirectory)../../ksa-assemblies.lock</KsaLockPath>
       <!-- ... KsaBuildLine: a regex match of the `build` line in the lock file ... -->
       <KsaBuild Condition="'$(KsaBuildLine)' != ''">$(KsaBuildLine.Substring(6).Trim())</KsaBuild>
     </PropertyGroup>

     <ItemGroup Condition="'$(KsaBuild)' != ''">
       <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
         <_Parameter1>KsaBuild</_Parameter1>
         <_Parameter2>$(KsaBuild)</_Parameter2>
       </AssemblyAttribute>
     </ItemGroup>
   ```

   At run time the mod reads the stamp and the running game's assembly version:[src/KSAGolf/Ksa/Build.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Build.cs#L50-L77)

   ```csharp
   // src/KSAGolf/Ksa/Build.cs L50-L77
       private static string? ResolveKsaRunning()
       {
           try
           {
               return typeof(KSA.Vehicle).Assembly.GetName().Version?.ToString();
           }
           catch
           {
               return null;
           }
       }

       private static string? ResolveKsaBuild()
       {
           try
           {
               foreach (AssemblyMetadataAttribute meta in typeof(Build).Assembly
                            .GetCustomAttributes<AssemblyMetadataAttribute>())
               {
                   if (meta.Key == "KsaBuild") return meta.Value;
               }

               return null;
           }
           catch
           {
               return null;
           }
       }
   ```

4. **Log them from `StarMapImmediateLoad`:**

   ```csharp
   // src/KSAGolf/Ksa/KSAGolfMod.cs L32-L37
       [StarMapImmediateLoad]
       public void OnImmediateLoad(Mod mod)
       {
           Log.Info($"loading (mod id: {mod.Id})");
           Log.Info($"KSAGolf {Build.Version} built for KSA {Build.KsaBuild ?? "?"}, running {Build.KsaRunning ?? "?"}");
       }
   ```

   (`Log` is the mod's own logger, see [Rotation to .prev.log, timer flush, runtime debug toggle](../../lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-1.md); the version comparison itself is
   [Stamp the targeted build into assembly metadata](../../lifecycle/check-the-game-version/check-the-game-version-2.md).)
5. **Guard the assets list in both directions.** KSAGolf's `tools/validate-parts.py` fails when an XML at the
   mod root is not in `mod.toml`, or when `mod.toml` names a file that is not on disk, and the deploy script
   refuses to deploy until it passes:[tools/validate-parts.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/validate-parts.py#L244-L268)

   ```python
   # tools/validate-parts.py L252-L266 (excerpt)
       toml = MOD / "mod.toml"
       # ... declared = the *.xml names read out of mod.toml with a regex ...

       problems = checked = 0
       for path in sorted(MOD.glob("KSAGolf*.xml")):
           checked += 1
           if path.name not in declared:
               print(f"  UNDECLARED {path.name} -- present but not in mod.toml's assets, so KSA "
                     f"never loads it", file=sys.stderr)
               problems += 1

       for name in sorted(declared):
           if not (MOD / name).is_file():
               print(f"  MISSING {name} -- listed in mod.toml, not on disk", file=sys.stderr)
               problems += 1
   ```

6. **Deploy and register.** Copy `bin/Release/net10.0/` to `<user dir>/mods/KSAGolf/` and make sure
   `manifest.toml` has `[[mods]] id = "KSAGolf"`, `enabled = true`. KSAGolf's `tools/deploy.sh` builds,
   validates, copies and appends the entry (keeping a `.bak`); the general approach is in
   [Deploy a mod on every build and debug it from an IDE](../../build/deploy-a-mod-on-every-build/overview.md).

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/mod.toml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/mod.toml) | Content and StarMap manifest in one file |
| [src/KSAGolf/KSAGolf.csproj](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolf.csproj) | Content copy rules, `Private="false"` game references, `KsaBuild` stamp |
| [src/KSAGolf/Ksa/Build.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Build.cs) | Reads the stamp and the running assembly version |
| [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs) | `[StarMapMod]` class with the two startup hooks |
| [tools/validate-parts.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/validate-parts.py) | Assets-list guard |
| [src/KSAGolf/KSAGolfSounds.xml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolfSounds.xml) | Example asset XML referencing wav files by relative `Path` |

## Variations and gotchas

- **An XML that is not in `assets` is never loaded** and nothing logs it, so a guard script is worth its cost.
- **`Mod.Id` is the manifest id, not `name`.** Log `mod.Id` at immediate load so a mismatch is visible.
- **Game assemblies stay out of the output** (`Private="false"`): StarMap's mod context resolves them to the
  copies the game already loaded, so a shipped copy is dead weight and redistributes game files.
- **The build stamp is only as good as the lock file.** KSAGolf targets `2026.9.22.5482`; the running build at
  time of analysis was `2026.10.10.5554`, where the `Mod` and `ModLibrary` members used here are unchanged in
  shape.
- **Dropping the folder into `mods/` is not enough**: the `manifest.toml` entry (or the new-mod dialog) is what
  makes KSA and StarMap load it.

## See also

- [Flat folder with an explicit assets list](./ship-content-and-code-in-one-mod-1.md), [Find your mod's install folder at runtime: Assembly.Location, then ModLibrary.Find, then Documents](./ship-content-and-code-in-one-mod-3.md), [Ship assets and code in one mod folder and find it at runtime](./overview.md)
- [Content-plus-code mod layout](../../../../mods/ksagolf/features/content-and-code-mod-layout.md)
- [Warn when the running game build differs from the one a mod was tested on](../../lifecycle/check-the-game-version/overview.md), [Package and publish a mod release (zip, CI publishing, installer)](../../build/package-and-publish-a-mod-release/overview.md), [Write mod.toml - KSA keys and the [StarMap] table](../mod-toml-reference/overview.md)
