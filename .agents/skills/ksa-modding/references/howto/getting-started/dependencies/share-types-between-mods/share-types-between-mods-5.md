---
type: How-To
title: "Fork or rename a library mod without breaking its dependents: new mod id, unchanged assembly identity"
description: Change only the KSA mod id (folder name) while keeping the assembly name, namespace, attribute full names and Harmony id, so dependents keep working through ImportedAssemblies
category: getting-started
group: dependencies
topic: share-types-between-mods
approach: 5
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/kittenextensions/features/continued-fork-identity]
techniques: [toml-config, msbuild, assembly-load-context]
hooks: [starmap StarMapBeforeMain]
lifecycle: [startup:starmap-before-main]
game_types: [KSA.ModLibrary]
tags: [fork, mod-id, assembly-identity, kittenextensions, mod-toml, dependents]
---

KittenExtensions Continued (KXC) is a continuation of tom_is_unlucky's KittenExtensions (KX). It had to become a
separate mod without breaking the mods built against the original. It does that by changing exactly one identity and
keeping all the others: the KSA **mod id** (the folder name) is new, while the **assembly name**, the C# namespace, the
attribute full names and the Harmony id stay the same. Dependents therefore keep a non-optional dependency whose
`ImportedAssemblies` names the unchanged `KittenExtensions` assembly. Use this when you continue, rename or replace a
library mod that others import ([Importer lists the contract assembly in ImportedAssemblies](./share-types-between-mods-1.md) explains why the import name is what matters).

## Background: how the game does it

StarMap uses **two independent keys**. The mod id (the manifest entry, i.e. the folder name) identifies the mod in the
registry and in `ModDependencies.ModId`. The assembly simple name identifies what a dependent may import from that
mod's load context and what the entry DLL is called. KSA derives the id from the folder name: `ModLibrary.AddMods`
creates `ModEntry(modDir.Name, ...)`.[current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L338-L369) StarMap loads `<mod folder>/<EntryAssembly>.dll` and records the manifest
id as `ModId`.[StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L36-L76) A dependent's context forwards an assembly request to a dependency's context only if the
requested simple name is in the dependency's import set.[StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L73) Changing one key leaves the other alone.

## Lifecycle and timing

`startup:starmap-before-main`. Identity and dependencies resolve while StarMap loads mods, before the game's entry
point. With a non-optional dependency, the library's `StarMapBeforeMain` (Harmony `PatchAll`) always runs before the
dependent's, and the library's `Program.Main` prefix then sees every mod assembly.[StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L52-L104)

## Steps

1. **Give the build two names:** `ModName` (the new folder and manifest id) and `AssemblyName` (unchanged). The build
   writes the mod to a folder named after `ModName` and generates `mod.toml` with
   `name = "$(ModName)"` and `[StarMap] EntryAssembly = "$(AssemblyName)"`:[KittenExtensions/KittenExtensions.csproj](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KittenExtensions.csproj#L3-L23)

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

   Pinning `AssemblyVersion` (0.5.0.0) while `VersionPrefix` moves keeps binding stable for compiled dependents.
2. **Generate the manifest from those properties** (the targets file writes the lines):[KittenExtensions/CopyOutput.targets](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/CopyOutput.targets#L45-L61)

   ```xml
   <!-- KittenExtensions/CopyOutput.targets L45-L61 -->
           <ItemGroup>
               <ModManifestLines Include="name = &quot;$(ModName)&quot;" />
               <ModManifestLines Include="version = &quot;v$(Version)&quot;" />
               <ModManifestLines Include="author = &quot;$(Authors)&quot;" />
               <ModManifestLines Include="description = &quot;$(Description)&quot;" />
               <ModManifestLines Include="@(ModManifestArrayLines)">
                   <Separator>$([System.Environment]::NewLine)</Separator>
               </ModManifestLines>
               <ModManifestLines Include="[StarMap]">
                   <Separator>$([System.Environment]::NewLine)</Separator>
               </ModManifestLines>
               <ModManifestLines Include="EntryAssembly = &quot;$(AssemblyName)&quot;" />
               <ModManifestLines Include="@(StarMapModDependencyLines)" />
           </ItemGroup>

           <WriteLinesToFile File="$(SingleCopyDestination)mod.toml" Lines="@(ModManifestLines->'%(Separator)%(Identity)')" Overwrite="true" />
   ```

   (the broader recipe is [Generate mod.toml and StarMap dependencies from the project file](../../build/generate-mod-toml-from-msbuild/overview.md)).
3. **Keep everything else identical.** Namespace `KittenExtensions`, the attribute types `KxAssetAttribute` and
   `KxAssetInjectAttribute` that dependents copy, and the Harmony id `"KittenExtensions"`. The one place the new id
   appears in code is a debug-flag lookup in the patch code.
4. **State the rules in the README:** KX and KXC cannot be installed together, and dependents must use the new id:[README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L1-L9)

   ```markdown
   # README.md L5-L9
   It replaces the original mod under the new `KittenExtensionsContinued` mod and directory name while keeping the `KittenExtensions` assembly name for compatibility.

   > [!WARNING]
   > KittenExtensions Continued (KXC) and the original KittenExtensions (KX) cannot be installed together. Mods that depend on KXC require it as a non-optional dependency, so remove any existing KX installation before installing KXC.
   ```

5. **Tell dependents how to declare it.** ShaderExtensions compiles against
   `$(KSAModsDir)KittenExtensionsContinued/KittenExtensions.dll` with `Private=False` and declares the new id with the
   old assembly name:[ShaderExtensions/ShaderExtensions.csproj](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderExtensions.csproj#L62-L72)

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

## Complete example

| Mod | Declaration | Need from KXC |
|---|---|---|
| ShaderExtensions | non-optional `KittenExtensionsContinued`, imports `KittenExtensions` | `KxAsset` registration of its shader elements |
| MPFX | non-optional `KittenExtensionsContinued` and `ShaderExtensions` | `KxAsset` registration of `<MPFXProfile>` |
| KSA-AdvancedFlightComputer | optional `ModId = "KittenExtensions"` (the **old** id) | its `patches` files; KXC applies them anyway because it reads `patches` from every manifest mod |

[AdvancedFlightComputer/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/mod.toml) shows the old-id optional form, which works only because it is optional and AFC needs no KXC code.

## Variations and gotchas

- **Both forks installed at once is the failure case.** Same Harmony id, so two `Program.Main` prefixes, two
  `ModLibrary.PrepareAll` prefixes and two transpilers on the same loader methods (inferred from the fork's code, not
  observed). The README states the rule.
- **Use the new id in dependency declarations.** `ModId = "KittenExtensions"` refers to the original mod and does not
  resolve against KXC.
- **Do not ship `KittenExtensions.dll` in a dependent.** Dependents reference it with `Private=False`; the copy in the
  library's folder is the one StarMap loads.
- **Non-optional** because a dependent also needs the library's `Program.Main` prefix to register custom XML element
  names before the game reads its asset files.
- **Folder-name drift.** The README's install snippet and the debug flag both use `KittenExtensionsContinued`; the id
  has to be the folder name everywhere ([Folder and manifest rules as StarMap consumes them](../../loader/how-starmap-launches-ksa/how-starmap-launches-ksa-2.md)).

## See also

- [Importer lists the contract assembly in ImportedAssemblies](./share-types-between-mods-1.md), [Duck-typed attributes copied into the consumer (plus: Scan every AssemblyLoadContext with GetCustomAttributesData)](./share-types-between-mods-3.md), [Share types and design an inter-mod API between StarMap mods](./overview.md)
- [Continued fork with a new mod id and the old assembly name](../../../../mods/kittenextensions/features/continued-fork-identity.md)
- [Declare a dependency on another StarMap mod, required or optional](../declare-a-mod-dependency/overview.md), [Generate mod.toml and StarMap dependencies from the project file](../../build/generate-mod-toml-from-msbuild/overview.md), [Write mod.toml - KSA keys and the [StarMap] table](../../loader/mod-toml-reference/overview.md)
