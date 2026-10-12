---
type: How-To
title: Generate mod.toml and StarMap dependencies from the project file
description: Declare version, entry assembly, asset lists and StarMap dependencies as csproj properties and items, and have a shared CopyOutput.targets write mod.toml on every build so it cannot drift from the code
category: getting-started
group: build
topic: generate-mod-toml-from-msbuild
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/kittenextensions/features/msbuild-mod-toml-generation
  - mods/shaderextensions/features/generated-mod-toml
  - mods/mpfx/features/ci-release-pipeline
techniques: [msbuild, toml-config]
hooks: [msbuild AfterTargets=Build]
lifecycle: []
game_types: [KSA.Mod]
tags: [msbuild, mod-toml, generated, copyoutput, dependencies, manifest]
---

KittenExtensions, ShaderExtensions and MPFX keep no `mod.toml` in git. Every build writes one into the output folder
from MSBuild data: the version comes from `VersionPrefix`, the entry assembly from `AssemblyName`, asset XML files from
project items tagged `ModManifestArray`, and the StarMap dependencies from `StarMapModDependency` items. The manifest
cannot drift from the code because the project file is its only source. The same `CopyOutput.targets` file is used by
the three repos (copies, whitespace aside). The key-by-key meaning of the generated file is
[Write mod.toml - KSA keys and the [StarMap] table](../../loader/mod-toml-reference/overview.md); dependency semantics are [Required dependency with ImportedAssemblies](../../dependencies/declare-a-mod-dependency/declare-a-mod-dependency-1.md).

## Background: how the game does it

The generated file must satisfy two readers. KSA deserializes it into `KSA.Mod`: `name`, `assets` and the other
`[TomlField]` members.[current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L26-L89) StarMap parses the `[StarMap]` table into `StarMapConfig`: `EntryAssembly`,
`ModDependencies` with `ModId`, `Optional`, `ImportedAssemblies`.[StarMap.Core/Config/StarMapConfig.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Config/StarMapConfig.cs#L7-L25) Unknown keys such as `version`
and `author` are ignored by both, so the generator can emit them freely. The mod id is the **folder name**, not the
`name` key, so the generator also names the output folder `$(ModName)`.

## Lifecycle and timing

Build time only. The generated file is read at run time: `[StarMap]` at `startup:starmap-before-main`, `assets` and the
other content keys at `startup:asset-load`.

## Steps

1. **Declare mod data in the csproj.** Names, version and authorship are ordinary properties; the entry assembly name
   is `AssemblyName`:[KittenExtensions/KittenExtensions.csproj](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KittenExtensions.csproj#L4-L23)

   ```xml
   <!-- KittenExtensions/KittenExtensions.csproj L4-L13 -->
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
   ```

2. **Tag list-valued manifest keys on `None` items.** MPFX lists its asset XML with `ModManifestArray` metadata; the
   target groups items by that value and emits `assets = [ "MPFXAssests.xml" ]` with forward slashes:[MPFX/MPFX.csproj](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.csproj#L83-L92)

   ```xml
   <!-- MPFX/MPFX/MPFX.csproj L83-L92 -->
   	<ItemGroup>
   		<None Update="$(ModName)Assests.xml">
   			<CopyToOutputDirectory>Always</CopyToOutputDirectory>
   			<ModManifestArray>assets</ModManifestArray>
   		</None>
   		<None Update="Shaders\*">
   			<CopyToOutputDirectory>Always</CopyToOutputDirectory>
   		</None>
   	</ItemGroup>
   ```

3. **Declare StarMap dependencies as items** with `ImportedAssemblies` (and optionally `Optional`):[MPFX/MPFX.csproj](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.csproj#L74-L81)

   ```xml
   <!-- MPFX/MPFX/MPFX.csproj L74-L81 -->
   	<ItemGroup>
   		<StarMapModDependency Include="KittenExtensionsContinued">
   			<ImportedAssemblies>KittenExtensions</ImportedAssemblies>
   		</StarMapModDependency>
   		<StarMapModDependency Include="ShaderExtensions">
   			<ImportedAssemblies>ShaderExtensions</ImportedAssemblies>
   		</StarMapModDependency>
   	</ItemGroup>
   ```

   Several imported assemblies are separated by `;`.
4. **Import the targets at the end of the csproj** (`<Import Project="CopyOutput.targets" />`), which defines the
   destinations (`../Release/$(ModName)/` by default, extended by a `CopyDestination` item in a user file for the
   mods folder) and runs after `Build`.
5. **Understand what the targets write.** For each destination: delete and recreate the folder, copy
   `$(OutputPath)**\*`, then write `mod.toml` with `name`, `version = "v$(Version)"`, `author`, `description`, the array
   lines, `[StarMap]`, `EntryAssembly = "$(AssemblyName)"` and the dependency blocks:[KittenExtensions/CopyOutput.targets](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/CopyOutput.targets#L37-L61)

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

6. **Turn items into TOML.** The two helper targets build the array line and the dependency table:[KittenExtensions/CopyOutput.targets](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/CopyOutput.targets#L64-L98)

   ```xml
   <!-- KittenExtensions/CopyOutput.targets L64-L76 -->
       <Target Name="_CreateModManifestArrayLine" Returns="@(ModManifestArrayLine)">
           <ItemGroup>
               <ModManifestArrayFiles Include="@(None)" Condition="'%(None.ModManifestArray)' == '$(ModManifestArrayName)'">
                   <ManifestPath>$([System.String]::Copy('%(Identity)').Replace('\', '/'))</ManifestPath>
               </ModManifestArrayFiles>
           </ItemGroup>
           <PropertyGroup>
               <ModManifestArrayValues>@(ModManifestArrayFiles->'&quot;%(ManifestPath)&quot;', ', ')</ModManifestArrayValues>
           </PropertyGroup>
           <ItemGroup>
               <ModManifestArrayLine Include="$(ModManifestArrayName) = [ $(ModManifestArrayValues) ]" />
           </ItemGroup>
       </Target>
   ```

   ```xml
   <!-- KittenExtensions/CopyOutput.targets L88-L97 -->
           <ItemGroup>
               <StarMapModDependencyLine Include="[[StarMap.ModDependencies]]">
                   <Separator>$([System.Environment]::NewLine)</Separator>
               </StarMapModDependencyLine>
               <StarMapModDependencyLine Include="ModId = &quot;$(StarMapModDependencyId)&quot;" />
               <StarMapModDependencyLine Include="Optional = $(StarMapModDependencyOptional)" />
               <StarMapModDependencyLine Include="ImportedAssemblies = [ &quot;$(StarMapImportedAssemblyValues)&quot; ]" />
           </ItemGroup>
   ```

7. **Add a version stamp** if you want it in the log: a `SetVersionMetadata` target runs `git rev-parse --short HEAD`
   before `GetAssemblyVersion` and sets `InformationalVersion` to `v$(VersionPrefix).<hash>` (`-debug` in Debug).
8. **Check the result.** Open the generated `Release/<ModName>/mod.toml`; ShaderExtensions' ends up as `name`,
   `version = "v1.0.5"`, `author`, `[StarMap] EntryAssembly = "ShaderExtensions"` and one
   `[[StarMap.ModDependencies]]` for `KittenExtensionsContinued` importing `KittenExtensions`.

## Complete example

| File | Role |
|---|---|
| [KittenExtensions/CopyOutput.targets](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/CopyOutput.targets) | The shared generator |
| [KittenExtensions/KittenExtensions.csproj](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KittenExtensions.csproj), [ShaderExtensions/ShaderExtensions.csproj](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderExtensions.csproj), [MPFX/MPFX.csproj](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.csproj) | Properties and items per mod |

MPFX and ShaderExtensions' release workflows then zip `Release/<ModName>` and publish it
([Private DLL repository fetched with a GitHub App token](../build-in-ci-without-the-game-installed/build-in-ci-without-the-game-installed-1.md), [Package and publish a mod release (zip, CI publishing, installer)](../package-and-publish-a-mod-release/overview.md)).

## Variations and gotchas

- **Every build deletes the destination folder** (`RemoveDir`), so anything a mod writes into its own folder at run
  time is lost on rebuild. MPFX keeps profiles under `mods/MPFX/Profiles`, which a rebuild into the mods folder
  wipes.
- **The shipped manifest is not in git.** Read the targets and the csproj items to know what `mod.toml` contains.
- **`description` is empty** when `$(Description)` is not set (ShaderExtensions), and `version` carries a leading `v`.
- **Only scalar keys, array keys and dependencies are supported.** Add a new TOML shape by extending the target.
- **Dependency id is the folder name**, e.g. `KittenExtensionsContinued`, while the imported assembly name is
  `KittenExtensions` ([New mod id, unchanged assembly identity](../../dependencies/share-types-between-mods/share-types-between-mods-5.md)).
- **CI** passes `/p:KSADir` and `/p:KSAModsDir`; `ShaderExtensions.props` is not imported (it duplicates
  `KSADLLs.props` plus Harmony).

## See also

- [Write mod.toml - KSA keys and the [StarMap] table](../../loader/mod-toml-reference/overview.md), [Declare a dependency on another StarMap mod, required or optional](../../dependencies/declare-a-mod-dependency/overview.md), [Package and publish a mod release (zip, CI publishing, installer)](../package-and-publish-a-mod-release/overview.md)
- [MSBuild-generated mod.toml and deploy](../../../../mods/kittenextensions/features/msbuild-mod-toml-generation.md), [mod.toml generated by MSBuild with a StarMap dependency](../../../../mods/shaderextensions/features/generated-mod-toml.md),
  [Generated mod.toml and CI release pipeline](../../../../mods/mpfx/features/ci-release-pipeline.md)
- [Reference the game's assemblies from a mod project without shipping them](../reference-game-assemblies-from-a-mod-project/overview.md), [Build a mod in CI when the game DLLs are private](../build-in-ci-without-the-game-installed/overview.md)
