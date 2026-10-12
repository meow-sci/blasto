---
type: How-To
title: Share one KSADLLs.props across a family of mods and point CI at a private DLL checkout
description: Keep the DLL glob in a small props file imported by each project, pass KSADir and KSAModsDir on the command line, and let CI supply a private checkout without changing the project
category: getting-started
group: build
topic: reference-game-assemblies-from-a-mod-project
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/kittenextensions/features/msbuild-mod-toml-generation]
techniques: [msbuild, github-actions]
hooks: [msbuild Import KSADLLs.props]
lifecycle: []
game_types: []
tags: [msbuild, props, ksadlls, ci, kittenextensions, shaderextensions, mpfx]
---

KittenExtensions, ShaderExtensions and MPFX (all by AMPW) use the same pattern: a small `KSADLLs.props` that turns a
glob of game DLLs into `Private=False` references, imported at the top of each csproj. The folder is a property
(`KSADir`) that a developer sets in an untracked user file and CI passes with `/p:KSADir=`. The file is copied between
the repos (the three copies differ only in a couple of unused `ModFile` items), so it works as a template, not as a
shared dependency. Variants: [KsaDir property with wildcard Reference items and HintPath](./reference-game-assemblies-from-a-mod-project-1.md) (one props in one repo) and
[Tiered KsaDllDir resolution in Directory.Build.props](./reference-game-assemblies-from-a-mod-project-2.md) (tiered discovery).

## Background: how the game does it

Nothing game-specific: the references are compile-time, and `Private=False` keeps the game's assemblies out of the
output so the loaded game copies are used at run time (StarMap's load context returns an already-loaded assembly of the
same name before looking in the mod folder). The glob covers `Brutal*.dll`, `KSA.dll`, `Planet*.dll` and `Tomlet.dll`.
`StarMap.API.dll` is not part of the game; these projects take it from `$(KSAModsDir)StarMap/StarMap.API.dll`, also
non-private, and Harmony from NuGet.

## Lifecycle and timing

Build time. CI sets the two properties on the command line, so the identical project builds on a runner that has a
private DLL repository checked out into a `deps/` folder.

## Steps

1. **Create the props file** with the glob-to-reference transform:[KittenExtensions/KSADLLs.props](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KSADLLs.props)

   ```xml
   <!-- KittenExtensions/KSADLLs.props L1-L9 -->
   <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
     <ItemGroup>
       <_KsaAssemblies Include="$(KSADir)Brutal*.dll" Path="%(FullPath)" />
       <_KsaAssemblies Include="$(KSADir)KSA.dll" Path="%(FullPath)" />
       <_KsaAssemblies Include="$(KSADir)Planet*.dll" Path="%(FullPath)" />
       <_KsaAssemblies Include="$(KSADir)Tomlet.dll" Path="%(FullPath)" />
       <Reference Include="@(_KsaAssemblies->'%(FileName)')" HintPath="%(Path)" Private="False" />
     </ItemGroup>
   </Project>
   ```

2. **Import it first in the csproj**, then add the non-game references (StarMap.API from the mods folder, NuGet
   Harmony):[KittenExtensions/KittenExtensions.csproj](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KittenExtensions.csproj#L1-L3)

   ```xml
   <!-- KittenExtensions/KittenExtensions.csproj L1-L2 -->
   <Project Sdk="Microsoft.NET.Sdk" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
   	<Import Project="KSADLLs.props" />
   ```

3. **Keep per-developer paths in an untracked user file** (or on the command line). KittenExtensions' checked-in
   `.csproj.user` shows the shape: `KSADir`, `KSAModsDir`, an extra `CopyDestination` for the mods folder and a launch
   profile that starts `StarMap.exe` so F5 runs the game through the loader:[KittenExtensions/KittenExtensions.csproj.user](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KittenExtensions.csproj.user)

   ```xml
   <!-- KittenExtensions/KittenExtensions.csproj.user L6-L15 -->
       <PropertyGroup>
           <KSADir>C:/Program Files/Kitten Space Agency/</KSADir>
           <KSAModsDir>C:/Users/AMPW/Documents/My Games/Kitten Space Agency/mods/</KSAModsDir>
       </PropertyGroup>
       <ItemGroup>
           <CopyDestination Include="$(KSAModsDir)$(ModName)/"/>
       </ItemGroup>
   ```

   (This file contains the author's Windows paths and is committed; override `KSADir` and `KSAModsDir` rather than
   editing it.)
4. **Point CI at a private DLL checkout** by passing the same two properties. KittenExtensions' workflow checks out a
   private repository of KSA DLLs with a GitHub App token into `deps/`, then builds:[.github/workflows/build.yml](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/.github/workflows/build.yml#L268-L275)

   ```bash
   # .github/workflows/build.yml L271-L274
             deps_dir="$(pwd)/deps/"
             ksa_dir="$deps_dir/ksa/"
             dotnet build KittenExtensions/KittenExtensions.csproj -c "$CONFIGURATION" --no-restore /p:KSADir="$ksa_dir" /p:KSAModsDir="$deps_dir"
   ```

   The CI details (App token, versions, SpaceDock) are in [Build a mod in CI when the game DLLs are private](../build-in-ci-without-the-game-installed/overview.md).
5. **Do not forget the trailing slash**: the glob is `$(KSADir)Brutal*.dll`, so `KSADir` must end with a separator
   (`C:/Program Files/Kitten Space Agency/`, `.../ksa/`).

## Complete example

| File | Role |
|---|---|
| [KittenExtensions/KSADLLs.props](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KSADLLs.props), [ShaderExtensions/KSADLLs.props](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/KSADLLs.props) | The glob transform (ShaderExtensions' copy also carries two unused `ModFile` items) |
| [KittenExtensions/KittenExtensions.csproj](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KittenExtensions.csproj) | Imports the props, then the manifest targets ([Generate mod.toml and StarMap dependencies from the project file](../generate-mod-toml-from-msbuild/overview.md)) |
| [KittenExtensions/KittenExtensions.csproj.user](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KittenExtensions.csproj.user) | Developer paths |
| [.github/workflows/build.yml](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/.github/workflows/build.yml) | CI invocation |

## Variations and gotchas

- **Committed local paths.** The checked-in `.csproj.user` ties the repo to one machine; override on the command line
  or in your own user file.
- **No default `KSADir`.** In ShaderExtensions `KSADir` and `KSAModsDir` have no defaults; local builds must pass
  `/p:KSADir=... /p:KSAModsDir=...` as CI does.
- **CI needs secrets.** Forks cannot build in CI without their own KSA DLL repository and GitHub App credentials.
- **Every build deletes the destination folder** in the sibling manifest targets, so anything a mod writes into its own
  folder at run time is lost on rebuild ([Generate mod.toml and StarMap dependencies from the project file](../generate-mod-toml-from-msbuild/overview.md)).
- **Prefer a tiered resolver** ([Tiered KsaDllDir resolution in Directory.Build.props](./reference-game-assemblies-from-a-mod-project-2.md)) if you want zero setup for
  contributors.

## See also

- [KsaDir property with wildcard Reference items and HintPath](./reference-game-assemblies-from-a-mod-project-1.md), [Tiered KsaDllDir resolution in Directory.Build.props](./reference-game-assemblies-from-a-mod-project-2.md), [Reference the game's assemblies from a mod project without shipping them](./overview.md)
- [MSBuild-generated mod.toml and deploy](../../../../mods/kittenextensions/features/msbuild-mod-toml-generation.md)
- [Generate mod.toml and StarMap dependencies from the project file](../generate-mod-toml-from-msbuild/overview.md), [Build a mod in CI when the game DLLs are private](../build-in-ci-without-the-game-installed/overview.md)
