---
type: Mod Feature
title: Continued fork with a new mod id and the old assembly name
description: KittenExtensions Continued installs under the new mod id KittenExtensionsContinued but keeps the KittenExtensions assembly name and namespace, so dependents keep binding through a non-optional StarMap dependency that imports that assembly
mod: kittenextensions
category: getting-started
techniques: [starmap-lifecycle, assembly-load-context, inter-mod-api, msbuild-targets]
hooks: [starmap StarMapBeforeMain]
lifecycle: [startup:starmap-before-main]
game_types: [KSA.ModEntry, KSA.ModLibrary]
tags: [starmap, dependencies, assembly-load-context, fork, compatibility, mod-id]
---

KittenExtensions Continued (KXC) is AMPW's continuation of tom_is_unlucky's KittenExtensions (KX). It
had to become a separate mod without breaking the mods built against the original, and it does so by
changing exactly one identity and keeping all the others: the KSA mod id (the folder name) is new,
while the assembly name, the C# namespace, the attribute full names and the Harmony id stay the same.
The README spells out the consequence for players and for dependents: remove KX before installing
KXC, and depend on KXC as a non-optional StarMap dependency.[^kx-readme]

## How it works

1. **Two names in the csproj.** `ModName` is `KittenExtensionsContinued` and `AssemblyName` is
   `KittenExtensions` ([KittenExtensions/KittenExtensions.csproj](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KittenExtensions.csproj#L4-L13)). The build writes the
   mod to a folder named after `ModName` and generates `mod.toml` with `name = "$(ModName)"` and
   `[StarMap] EntryAssembly = "$(AssemblyName)"`
   ([KittenExtensions/CopyOutput.targets](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/CopyOutput.targets#L46-L61)).[^kx-csproj] [^kx-copyoutput]
2. **The game's id is the folder.** KSA uses the folder name as the mod id (Core's manifest says so,
   [current/Content/Core/mod.toml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/mod.toml#L4), and `ModLibrary.AddMods` creates manifest entries from
   directory names, [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L338-L369)), so the manifest entry is
   `id = "KittenExtensionsContinued"` ([README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L24-L29)).[^ksa-core-modtoml] [^ksa-modlibrary]
3. **StarMap loads `<EntryAssembly>.dll`.** `RuntimeMod.TryCreateMod` reads `[StarMap]`, loads
   `<mod folder>/KittenExtensions.dll` into a fresh `ModAssemblyLoadContext`, and records the manifest
   id as the mod's `ModId` ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L36-L76)).[^starmap-runtimemod]
4. **What stays the same.** Namespace `KittenExtensions`, the attribute types
   `KittenExtensions.KxAssetAttribute` and `KxAssetInjectAttribute` that dependents copy, and the
   Harmony id `"KittenExtensions"` ([KittenExtensions/Mod.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Mod.cs#L20)). `AssemblyVersion` is pinned to
   `0.5.0.0` while `VersionPrefix` is `0.5.2` ([KittenExtensions/KittenExtensions.csproj](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KittenExtensions.csproj#L20-L23)).
   The one place the new id appears in code is the debug-flag lookup
   ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L201)).[^kx-mod] [^kx-patcher]
5. **How a dependent declares it.** ShaderExtensions and MPFX compile against
   `$(KSAModsDir)KittenExtensionsContinued/KittenExtensions.dll` with `Private=False` (not copied) and
   add a `StarMapModDependency` item whose identity is the mod id and whose `ImportedAssemblies` is the
   assembly name ([ShaderExtensions/ShaderExtensions.csproj](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderExtensions.csproj#L62-L72),
   [MPFX/MPFX.csproj](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.csproj#L68-L81)). The shared targets file turns it into
   `[[StarMap.ModDependencies]]` with `ModId`, `Optional` (defaulting to `false`) and
   `ImportedAssemblies` ([KittenExtensions/CopyOutput.targets](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/CopyOutput.targets#L78-L98)).[^sx-csproj] [^mpfx-csproj]
6. **How StarMap resolves it.** The dependency is looked up by `ModId` in StarMap's registry
   ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L78-L107)). When the dependent's code asks for an
   assembly, its load context first checks the default and game contexts, then forwards the request
   to a dependency's context only if the requested assembly name is in that dependency's imported set
   ([StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L73)). The mod id and the assembly
   name are therefore independent keys, which is what lets KXC change one and keep the other.[^starmap-modalc]
7. **Why non-optional.** A mod with a missing non-optional dependency is parked and never initialized
   ([StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L106-L140)), so a dependent cannot start without KXC.
   A dependent also needs KXC's `Program.Main` prefix to register its custom XML element names
   before the game reads its asset files (see [Custom XML asset types](custom-xml-asset-types.md));
   without KXC those elements would not be known to the game's serializers.[^starmap-modloader]

### Who depends on it, and how

| Mod | Declaration | What it needs from KXC |
|---|---|---|
| ShaderExtensions | non-optional `KittenExtensionsContinued`, imports `KittenExtensions` | `KxAsset` registration of `<ShaderEx>`, `<PostProcessingShader>`, `<GlobalPostShader>`, `<ImGuiShader>` |
| MPFX | non-optional `KittenExtensionsContinued` and `ShaderExtensions` | `KxAsset` registration of `<MPFXProfile>` |
| KSA-AdvancedFlightComputer | optional `ModId = "KittenExtensions"`, the original id ([AdvancedFlightComputer/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/mod.toml#L7-L9)) | Its `patches` files; KXC applies them anyway because it reads `patches` from every manifest mod |

## Lifecycle and timing

`startup:starmap-before-main`. Identity and dependencies are resolved while StarMap loads mods,
before the game's entry point: StarMap loads each enabled mod in manifest order, initializes it (which
runs its `[StarMapBeforeMain]`) when all dependencies are present, and releases waiting dependents
after their dependency initializes ([StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L52-L104)). With
a non-optional dependency, KXC's `StarMapBeforeMain` (Harmony `PatchAll`) always runs before the
dependent's. KXC's `Program.Main` prefix then sees every mod assembly
([KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs#L13-L18)).[^kx-patches]

## Key code

| Path | Role |
|---|---|
| [KittenExtensions/KittenExtensions.csproj](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KittenExtensions.csproj#L4-L23) | `ModName`, `AssemblyName`, version properties |
| [KittenExtensions/CopyOutput.targets](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/CopyOutput.targets#L46-L61) | Generated `mod.toml` with `EntryAssembly` |
| [KittenExtensions/CopyOutput.targets](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/CopyOutput.targets#L78-L98) | `StarMapModDependency` items to `[[StarMap.ModDependencies]]` |
| [ShaderExtensions/ShaderExtensions.csproj](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderExtensions.csproj#L62-L72) | A dependent's reference and dependency items |
| [StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L49-L66) | Imported-assembly forwarding by name |

## Snippets

```xml
<!-- KittenExtensions/KittenExtensions.csproj L3-L23 -->
	<PropertyGroup>
		<ModName>KittenExtensionsContinued</ModName>
		<Configuration Condition=" '$(Configuration)' == '' ">Debug</Configuration>
		<Platform Condition=" '$(Platform)' == '' ">AnyCPU</Platform>
		<TargetFramework>net10.0</TargetFramework>
		<TargetFrameworkVersion>net10.0</TargetFrameworkVersion>
		<LanguageTargets>$(MSBuildToolsPath)\Microsoft.CSharp.targets</LanguageTargets>
		<AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
		<LangVersion>14.0</LangVersion>
		<OutputPath>bin\$(Configuration)</OutputPath>
		<AssemblyName>KittenExtensions</AssemblyName>
		<OutputType>Library</OutputType>

		<ImplicitUsings>enable</ImplicitUsings>
		<Nullable>enable</Nullable>
		<AllowUnsafeBlocks>True</AllowUnsafeBlocks>

		<VersionPrefix>0.5.2</VersionPrefix>
		<Version>$(VersionPrefix)</Version>
		<AssemblyVersion>0.5.0.0</AssemblyVersion>
		<FileVersion>$(VersionPrefix).0</FileVersion>
```

```xml
<!-- ShaderExtensions/ShaderExtensions.csproj L62-L72 -->
		<Reference Include="KittenExtensions">
			<HintPath>$(KSAModsDir)KittenExtensionsContinued/KittenExtensions.dll</HintPath>
			<Private>False</Private>
		</Reference>
	</ItemGroup>

	<ItemGroup>
		<StarMapModDependency Include="KittenExtensionsContinued">
			<ImportedAssemblies>KittenExtensions</ImportedAssemblies>
		</StarMapModDependency>
	</ItemGroup>
```

```csharp
// StarMap.Core/ModRepository/ModAssemblyLoadContext.cs L49-L66
            if (RuntimeMod is RuntimeMod modInfo && modInfo.Dependencies.Count > 0)
            {
                foreach (var (dependency, importedAssemblies) in modInfo.Dependencies)
                {
                    if (importedAssemblies.Contains(assemblyName.Name ?? string.Empty))
                    {
                        try
                        {
                            var asm = dependency.ModAssemblyLoadContext.LoadFromAssemblyName(assemblyName);
                            if (asm != null)
                                return asm;
                        }
                        catch (FileNotFoundException)
                        {
                        }
                    }
                }
            }
```

## Game APIs touched

| Member | What it is for |
|---|---|
| `ModEntry.Id` and the manifest ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L338-L369)) | Mod id = folder name |
| StarMap `StarMapModDependency` (`ModId`, `Optional`, `ImportedAssemblies`) ([StarMap.Core/Config/StarMapConfig.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Config/StarMapConfig.cs#L13-L25)) | Dependency declaration keys |

## Pitfalls and version notes

- **KX and KXC cannot coexist** ([README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L8-L9)). If the original installs the same
  patches as this fork (it is not in the reference set, so that is an assumption), both would patch
  under the same Harmony id: two `Program.Main` prefixes, two `ModLibrary.PrepareAll` prefixes each
  building and running its own GameData document, and two transpilers on the same loader methods
  where only the first finds `XmlLoader.Load<T>` to rewrite. The README states the rule; these
  mechanics are inferred from this fork's code, not observed.
- **Use the new id in dependency declarations.** `ModId = "KittenExtensions"` refers to the original
  mod and does not resolve against KXC. AFC still declares the old id as optional, which works only
  because it is optional and AFC needs no KXC code.
- **Do not ship `KittenExtensions.dll` in a dependent.** Dependents reference it with
  `Private=False`; the copy in the KXC folder is the one StarMap loads.
- **The debug flag uses the new id** (`KittenExtensionsContinued`), even though the README's install
  section says "the KittenExtensions mod entry" ([README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L30)).
- ShaderExtensions and MPFX both repeat the coexistence warning in their READMEs
  ([README.md](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/README.md#L6), [README.md](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/README.md#L6)).[^sx-readme] [^mpfx-readme]

## See also

- [Custom XML asset types via KxAsset attributes](custom-xml-asset-types.md)
- [MSBuild-generated mod.toml and deploy](msbuild-mod-toml-generation.md)
- [StarMap mod dependency graph](../../starmap/features/mod-dependency-graph.md)
- [StarMap shared contract assemblies](../../starmap/features/shared-contract-assemblies.md)
- [StarMap per-mod assembly load context](../../starmap/features/per-mod-assembly-load-context.md)
- [Fork or rename a library mod without breaking its dependents: new mod id, unchanged assembly identity](../../../howto/getting-started/dependencies/share-types-between-mods/share-types-between-mods-5.md) - how-to article demonstrating this feature

[^kx-readme]: KittenExtensions README.md
[^kx-csproj]: KittenExtensions/KittenExtensions.csproj
[^kx-copyoutput]: KittenExtensions/CopyOutput.targets
[^ksa-core-modtoml]: Core mod.toml (build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^starmap-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^kx-mod]: KittenExtensions/Mod.cs
[^kx-patcher]: KittenExtensions/Patch/Patcher.cs
[^sx-csproj]: ShaderExtensions/ShaderExtensions.csproj
[^mpfx-csproj]: MPFX/MPFX.csproj
[^starmap-modalc]: StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
[^starmap-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^kx-patches]: KittenExtensions/Patches.cs
[^sx-readme]: ShaderExtensions README.md
[^mpfx-readme]: MPFX README.md
