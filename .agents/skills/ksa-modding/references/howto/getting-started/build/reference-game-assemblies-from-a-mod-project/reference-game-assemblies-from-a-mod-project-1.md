---
type: How-To
title: Reference game DLLs with a KSADir property and wildcard Reference items
description: Point a props file at the installed KSA folder and turn a DLL glob into References with Private=False so builds compile against the real API and never copy game files
category: getting-started
group: build
topic: reference-game-assemblies-from-a-mod-project
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksa-aircrafthud/features/props-based-build, mods/ksaero/features/mod-deploy-targets]
techniques: [msbuild]
hooks: [msbuild Reference Private=False]
lifecycle: []
game_types: []
tags: [msbuild, references, private-false, props, ksadir, wildcard]
---

A mod compiles against `KSA.dll`, the `Brutal.*` assemblies, `Planet.*` and `Tomlet`, but must never copy or redistribute
them. The rule is the same in every project of the corpus: reference the installed DLLs by `HintPath` with
`Private=false` (also written `Private=False`). This article shows the two shapes of doing it with a single `KSADir`
property: a wildcard item transform (KSA_AircraftHUD) and an explicit reference list (KSAero). Resolution that works for
contributors and CI is [Tiered KsaDllDir resolution in Directory.Build.props](./reference-game-assemblies-from-a-mod-project-2.md) and
[Shared KSADLLs.props glob for several mods](./reference-game-assemblies-from-a-mod-project-3.md); a build without any game reference is
[Build a KSA mod that references only StarMap.API and Harmony](../build-a-mod-without-game-references/overview.md).

## Background: how the game does it

`Private=false` is a compile-time instruction, but it matters at run time because of StarMap's load context: any
assembly name the game or StarMap already has loaded wins over a file in the mod folder, whatever version you built
against.[StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L47) Shipping copies of game DLLs therefore only wastes space and risks confusion; a copy of
`KSA.dll` in a mod folder is never used. The assemblies live in the KSA install folder (the one containing `KSA.dll`),
for example `C:\Program Files\Kitten Space Agency\` on Windows. The managed set includes `KSA.dll`, `Brutal.*.dll`,
`Planet.Core.dll`, `Planet.Render.Core.dll`, `BepuPhysics.dll`, `BepuUtilities.dll` and more (see
`current/dll` in the assemblies checkout).

## Lifecycle and timing

Build time only. The one runtime consequence is the load-context rule above.

## Steps

1. **Define the folder once**, overridable. KSAero's csproj defaults and lets `-p:KsaDir=...` override:[KSAero/KSAero.csproj](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/KSAero.csproj#L1-L9)

   ```xml
   <!-- KSAero/KSAero/KSAero.csproj L3-L9 -->
     <PropertyGroup>
       <TargetFramework>net10.0</TargetFramework>
       <PlatformTarget>x64</PlatformTarget>
       <ImplicitUsings>enable</ImplicitUsings>
       <Nullable>enable</Nullable>
       <KsaDir Condition="'$(KsaDir)' == ''">C:\Program Files\Kitten Space Agency</KsaDir>
     </PropertyGroup>
   ```

   (KSA_AircraftHUD hard-codes `E:\Games\Kitten Space Agency\` in a tracked props file; make it conditional or move it
   to an untracked user file.)
2. **Reference by explicit list** when you know what you use:[KSAero/KSAero.csproj](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/KSAero.csproj#L16-L45)

   ```xml
   <!-- KSAero/KSAero/KSAero.csproj L16-L25 (the same pattern repeats for each assembly) -->
     <ItemGroup>
       <Reference Include="KSA">
         <HintPath>$(KsaDir)\KSA.dll</HintPath>
         <Private>false</Private>
       </Reference>
       <Reference Include="Brutal.Core.Numerics">
         <HintPath>$(KsaDir)\Brutal.Core.Numerics.dll</HintPath>
         <Private>false</Private>
       </Reference>
   ```

3. **Or reference by wildcard**, so new `Brutal.*` assemblies in a game update are picked up without editing the
   project. An item list with `Path="%(FullPath)"` metadata is transformed into one `Reference` per DLL:[AircraftHUD.props](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.props#L18-L22)

   ```xml
   <!-- AircraftHUD.props L18-L22 -->
       <_KsaAssemblies Include="$(KSADir)Brutal*.dll" Path="%(FullPath)" />
       <_KsaAssemblies Include="$(KSADir)KSA.dll" Path="%(FullPath)" />
       <_KsaAssemblies Include="$(KSADir)Planet*.dll" Path="%(FullPath)" />
       <_KsaAssemblies Include="$(KSADir)Tomlet.dll" Path="%(FullPath)" />
       <Reference Include="@(_KsaAssemblies->'%(FileName)')" HintPath="%(Path)" Private="False" />
   ```

4. **Take StarMap.API and Harmony from NuGet** (`StarMap.API` 0.3.6, `Lib.Harmony` 2.4.2); mark them
   `PrivateAssets="all"` so they do not flow, and never copy them to the mod folder.
5. **Keep content beside the DLL** with `CopyToOutputDirectory` (`mod.toml`, fonts, ini) and set
   `AppendTargetFrameworkToOutputPath=false` if the output folder is the mod folder.
6. **Build and check the output folder**: it must contain your DLL, `mod.toml` and content, and no `KSA.dll`,
   `Brutal.*.dll`, `0Harmony.dll` or `StarMap.API.dll`. A packaging check (approach 2 of
   [Package and publish a mod release (zip, CI publishing, installer)](../package-and-publish-a-mod-release/overview.md)) can enforce it.

## Complete example

| File | Role |
|---|---|
| [AircraftHUD.props](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.props) | `KSADir`, output path, NuGet references, wildcard game references |
| [AircraftHUD.csproj](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.csproj) | Imports the props, copies content |
| [KSAero/KSAero.csproj](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/KSAero.csproj) | Explicit references with an overridable `KsaDir` |

## Variations and gotchas

- **Copy-local side effects.** With `CopyLocalLockFileAssemblies` plus default `Private` on HintPath references
  (AircraftHUD's vendored `ModMenu.Attributes` and `ShaderExtensions` references), `StarMap.API.dll` and
  `ShaderExtensions.dll` land in the mods folder, and a built copy of the mod once committed to a repo carried them.
  Keep `Private=false` on everything you do not need to ship.
- **Hard-coded, committed paths** force every other developer to edit a tracked file; use a condition so the property
  can be overridden from the command line or environment.
- **Windows-only paths.** Backslashes and `$(UserProfile)\Documents` assume Windows; see
  [Tiered KsaDllDir resolution in Directory.Build.props](./reference-game-assemblies-from-a-mod-project-2.md) for cross-platform resolution.
- **Dead items.** AircraftHUD defines `ModFile`, `ModDir` and `KSAContent` that no target uses (remains of an older
  copy-to-`Content` deploy).
- **The game must not be running** while you build into the live mods folder (Windows locks the loaded DLL);
  [Deploy a mod on every build and debug it from an IDE](../deploy-a-mod-on-every-build/overview.md) handles that.

## See also

- [Tiered KsaDllDir resolution in Directory.Build.props](./reference-game-assemblies-from-a-mod-project-2.md), [Shared KSADLLs.props glob for several mods](./reference-game-assemblies-from-a-mod-project-3.md), [Reference the game's assemblies from a mod project without shipping them](./overview.md)
- [Props-based build straight into the mods folder](../../../../mods/ksa-aircrafthud/features/props-based-build.md), [Build and deploy targets](../../../../mods/ksaero/features/mod-deploy-targets.md)
- [Deploy a mod on every build and debug it from an IDE](../deploy-a-mod-on-every-build/overview.md), [Build a mod in CI when the game DLLs are private](../build-in-ci-without-the-game-installed/overview.md), [Ship third-party DLLs next to your mod](../../dependencies/ship-third-party-dlls-with-a-mod/overview.md)
