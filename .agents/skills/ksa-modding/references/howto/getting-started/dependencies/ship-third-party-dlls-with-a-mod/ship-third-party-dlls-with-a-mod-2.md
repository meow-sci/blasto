---
type: How-To
title: Package NuGet dependencies with deps.json and ship exactly the reference closure
description: Deploy only the assemblies your mod references (project libraries and a NuGet package such as Tomlyn) together with the entry assembly's deps.json, and never the DLLs StarMap or the game already supply
category: getting-started
group: dependencies
topic: ship-third-party-dlls-with-a-mod
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/starmap/features/per-mod-assembly-load-context
  - mods/unscience/features/unscience-build-and-release
techniques: [assembly-load-context, msbuild]
hooks: [reflection System.Runtime.Loader.AssemblyDependencyResolver]
lifecycle: [startup:starmap-before-main]
game_types: []
tags: [nuget, deps-json, assembly-load-context, packaging, tomlyn, reference-closure]
---

A mod that grows beyond one DLL (a suite of libraries, a TOML parser from NuGet) needs a packaging rule: which files
go into the mod folder, and why does StarMap find them. unscience ships 31 feature libraries and `Tomlyn` as a single
folder and proves, in a script, that the folder is exactly the reference closure. The rule behind it is StarMap's
five-step resolution order, which makes the `.deps.json` file part of the deploy set. For one-off vendored DLLs see
[Copy the DLL beside the mod and let AssemblyDependencyResolver find it](./ship-third-party-dlls-with-a-mod-1.md).

## Background: how the game does it

StarMap builds one `ModAssemblyLoadContext` per mod from the path of the entry DLL, with an
`AssemblyDependencyResolver` rooted at it (which reads `<Entry>.deps.json`). `Load(AssemblyName)` tries in order:
`Default` by simple name, the game context's loaded assemblies, the game context's own resolver, dependency mods'
contexts whose import set contains the name, and finally the mod's resolver.[StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L72) In this order,
**game copies win**: any assembly name the game can resolve beats a copy in your folder, whatever version you built
against, so shipping Harmony or StarMap.API is wrong, and there is no per-mod override for a library the game already
ships. The loader's launcher put `0Harmony.dll` into `Default` before anything else loaded.[StarMap/Program.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/Program.cs#L9-L29)

```csharp
// StarMap.Core/ModRepository/ModAssemblyLoadContext.cs L24-L47
protected override Assembly? Load(AssemblyName assemblyName)
{
    var existingInDefault = Default.Assemblies
        .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
    if (existingInDefault != null)
        return existingInDefault;

    var existingInGameContext = _coreAssemblyLoadContext?.Assemblies
        .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
    if (existingInGameContext != null)
        return existingInGameContext;

    if (_coreAssemblyLoadContext != null)
    {
        try
        {
            var asm = _coreAssemblyLoadContext.LoadFromAssemblyName(assemblyName);
            if (asm != null)
                return asm;
        }
        catch (FileNotFoundException)
        {
        }
    }
```

## Lifecycle and timing

The context is created and the entry assembly loaded in `startup:starmap-before-main`; the other assemblies resolve
lazily on first use, from any hook or thread. Two mods that both ship, say, a TOML library, get two separate copies
with separate type identities and statics unless one imports the other's ([Share types and design an inter-mod API between StarMap mods](../share-types-between-mods/overview.md)).

## Steps

1. **Reference NuGet packages; mark loader packages private.** The StarMap and Harmony packages are compile-time only;
   `Tomlyn` is a runtime dependency that must ship:[unscience/unscience.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/unscience.csproj#L50-L54)

   ```xml
   <!-- unscience/unscience.csproj L50-L54 -->
     <ItemGroup>
       <PackageReference Include="StarMap.API" Version="0.3.6" PrivateAssets="all" />
       <PackageReference Include="Tomlyn" Version="0.19.0" />
       <PackageReference Include="Lib.Harmony" Version="2.4.2" PrivateAssets="all" />
     </ItemGroup>
   ```

2. **Copy the closure, not the folder.** The deploy target copies `mod.toml`, the entry assembly with `.pdb` and
   `.deps.json`, the license, then only the `MeowSci.*` items of `@(ReferenceCopyLocalPaths)` plus `Tomlyn.dll` and
   the licence texts:[unscience/unscience.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/unscience.csproj#L94-L120)

   ```xml
   <!-- unscience/unscience.csproj L94-L110 -->
     <Target Name="CopyCustomContent" AfterTargets="AfterBuild">
       <MakeDir Directories="$(DistDir)" />
       <ItemGroup>
         <FilesToCopy Include="$(OutputPath)mod.toml" />
         <FilesToCopy Include="$(OutputPath)$(AssemblyName).dll" />
         <FilesToCopy Include="$(OutputPath)$(AssemblyName).pdb" />
         <FilesToCopy Include="$(OutputPath)$(AssemblyName).deps.json" />
         <FilesToCopy Include="$(MSBuildThisFileDirectory)..\LICENSE" />
       </ItemGroup>
       <Copy SourceFiles="@(FilesToCopy)" DestinationFolder="$(DistDir)" />
       <ItemGroup>
         <!-- Copy only resolved project dependencies, never stale DLLs left in bin by old builds. -->
         <MeowSciAssemblies Include="@(ReferenceCopyLocalPaths)"
             Condition="$([System.String]::Copy('%(Filename)').StartsWith('MeowSci.'))" />
       </ItemGroup>
       <Copy SourceFiles="@(MeowSciAssemblies)" DestinationFolder="$(DistDir)" Condition="'@(MeowSciAssemblies)' != ''" />
       <Copy SourceFiles="$(TargetDir)Tomlyn.dll" DestinationFolder="$(DistDir)" Condition="Exists('$(TargetDir)Tomlyn.dll')" />
   ```

   The `.deps.json` must be in the folder: the mod's resolver finds `MeowSci.*.dll` and `Tomlyn.dll` through it.
3. **Give libraries distinctive assembly names** (`MeowSci.ZippoLib`, ...) so the `StartsWith` filter selects exactly
   them and nothing collides with the game or other mods.
4. **Prove the folder in a script.** `scripts/check-dist.py` asserts the dist contains only `unscience/`, exactly one
   `mod.toml` naming the right `EntryAssembly`, the runtime files, and that the set of `MeowSci.*.dll` files equals the
   transitive `ProjectReference` closure of the entry project, and that `KSA.dll` was not copied:[scripts/check-dist.py](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scripts/check-dist.py#L6-L30)

   ```python
   # scripts/check-dist.py L7-L13
   repo = Path(__file__).resolve().parent.parent
   package = root / "unscience"
   assert sorted(p.name for p in root.iterdir()) == ["unscience"], "Expected only unscience/ in dist"
   assert list(root.rglob("mod.toml")) == [package / "mod.toml"], "Expected one StarMap manifest"
   assert 'EntryAssembly = "MeowSci.Unscience"' in (package / "mod.toml").read_text()
   for name in ("MeowSci.Unscience.dll", "MeowSci.Unscience.deps.json", "Tomlyn.dll", "LICENSE"):
       assert (package / name).is_file(), f"Missing runtime file: {name}"
   ```

5. **Ship third-party licence texts** next to the binaries (`third-party-licenses/`), see
   [Package and publish a mod release (zip, CI publishing, installer)](../../build/package-and-publish-a-mod-release/overview.md).
6. **Check the result by running it:** the mod loads (`StarMap - Loaded mod: ...`) and the first use of `Tomlyn` does
   not throw `FileNotFoundException`; if it does, the DLL or the `deps.json` entry is missing.

## Complete example

[unscience/unscience.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/unscience.csproj) (references and deploy target) and [scripts/check-dist.py](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scripts/check-dist.py) (verification). The release workflow around them
([Rolling releases from a multi-assembly mod](../../build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-5.md)) builds the whole solution and runs `check-dist.py` before zipping.

## Variations and gotchas

- **Stale files.** Copy from `ReferenceCopyLocalPaths`, not from a folder sweep, so DLLs left in `bin` by older builds
  never ship.
- **`Harmony` and `StarMap.API` copies never load**, so omitting them is both correct and required for a clean
  folder; `Default` supplies Harmony and the game context supplies `StarMap.API`.
- **Version conflicts resolve to the game.** If you need a newer version of a library the game already ships, step 3
  still returns the game's copy; use a differently named fork instead.
- **Only the entry assembly is scanned** for `[StarMapMod]`; other DLLs load on demand through the context.
- **`CopyLocalLockFileAssemblies`** (commented out in the unscience template, set true in the repo-wide
  `Directory.Build.props`) controls whether NuGet DLLs land in `bin`; deploy explicitly regardless.
- **Native libraries are not resolved through `deps.json`**; see [Ship and load native libraries from a mod folder](../ship-native-libraries-with-a-mod/overview.md).

## See also

- [Copy the DLL beside the mod and let AssemblyDependencyResolver find it](./ship-third-party-dlls-with-a-mod-1.md), [Ship third-party DLLs next to your mod](./overview.md)
- [Isolate each mod in its own AssemblyLoadContext](../../../../mods/starmap/features/per-mod-assembly-load-context.md), [One shipped mod folder from thirty-plus projects, with dist validation and rolling GitHub releases](../../../../mods/unscience/features/unscience-build-and-release.md)
- [Ship and load native libraries from a mod folder](../ship-native-libraries-with-a-mod/overview.md), [Share types and design an inter-mod API between StarMap mods](../share-types-between-mods/overview.md), [Package and publish a mod release (zip, CI publishing, installer)](../../build/package-and-publish-a-mod-release/overview.md)
