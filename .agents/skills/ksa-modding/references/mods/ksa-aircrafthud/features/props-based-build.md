---
type: Mod Feature
title: Props-based build straight into the mods folder
description: Splits machine-specific build settings into an imported AircraftHUD.props that references the game DLLs by wildcard with Private=False, pulls StarMap.API and Harmony from NuGet, and sets OutputPath to the user's KSA mods folder so every build is a deploy
mod: ksa-aircrafthud
category: getting-started
techniques: [msbuild-props]
hooks: []
lifecycle: []
game_types: []
tags: [build, msbuild, props, csproj, deploy, references, nuget]
---

The project file is almost empty: it imports `AircraftHUD.props` and lists which content files to
copy. Everything that depends on the developer's machine (game path, output path, references) lives
in the props file, so another developer edits one small file. Setting `OutputPath` to the KSA mods
folder means `dotnet build` (or Visual Studio's build) drops a ready-to-run mod where StarMap looks
for it.[^ah-props] [^ah-csproj]

## How it works

| Piece | Where | Effect |
|---|---|---|
| `KSADir` = `E:\Games\Kitten Space Agency\` | [AircraftHUD.props](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.props#L4) | Game install used for references |
| `ModsDir` = `$(UserProfile)\Documents\My Games\Kitten Space Agency\mods\`, `OutputPath` = `$(ModsDir)$(ModName)\` | [AircraftHUD.props](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.props#L7-L9) | Build output is the installed mod folder; `AppendTargetFrameworkToOutputPath=false` keeps it flat |
| `StarMap.API` 0.3.6, `Lib.Harmony` 2.4.2 | [AircraftHUD.props](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.props#L12-L13) | NuGet compile references |
| `_KsaAssemblies` = `Brutal*.dll`, `KSA.dll`, `Planet*.dll`, `Tomlet.dll` from `KSADir`; `<Reference Include="@(_KsaAssemblies->'%(FileName)')" HintPath="%(Path)" Private="False" />` | [AircraftHUD.props](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.props#L18-L22) | Wildcard game references, one item batch, never copied |
| `ModMenu.Attributes` HintPath, `ShaderExtensions` HintPath `..\ShaderExtensions\Content\ShaderExtensions\ShaderExtensions.dll` | [AircraftHUD.props](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.props#L24-L30) | Vendored DLL and sibling-checkout reference (both copy local) |
| `CopyLocalLockFileAssemblies=true`, `AllowUnsafeBlocks=True`, `net10.0` | [AircraftHUD.csproj](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.csproj#L3-L9) | NuGet DLLs copied to output; pointers for ImGui |
| `<None Update=... CopyToOutputDirectory=Always>` for fonts, `hud.ini`, `mod.toml`, `HudAssests.xml`, `ModMenu.Attributes.dll`, `Shaders\*`, `Textures\*` | [AircraftHUD.csproj](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.csproj#L11-L36) | Content files land beside the DLL |

The wildcard-plus-metadata trick is the reusable bit: `Include="$(KSADir)Brutal*.dll"` with
`Path="%(FullPath)"` builds an item list, and the transform `@(_KsaAssemblies->'%(FileName)')`
turns it into one `Reference` per DLL with its own `HintPath`. New `Brutal.*` assemblies in a game
update are picked up without editing the project.

`Private="False"` matters at runtime: StarMap's load context returns the game's already-loaded copy
of any assembly with the same name before looking in the mod folder
([StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L47)),
so shipping copies of game DLLs only wastes space and risks confusion.[^starmap-alc]

## Lifecycle and timing

Build time only. Because the output folder is the live mods folder, the game must not be running
during a build (Windows locks the loaded DLL).

## Key code

| Path | Role |
|---|---|
| [AircraftHUD.props](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.props) | Paths, references, unused `ModFile` items |
| [AircraftHUD.csproj](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.csproj) | Framework, props import, content copy |

## Snippets

```xml
<!-- AircraftHUD.props L2-L22 -->
  <PropertyGroup>
    <ModName>AircraftHUD</ModName>
    <KSADir>E:\\Games\\Kitten Space Agency\\</KSADir>
    <KSAContent>$(KSADir)Content\</KSAContent>
    <ModDir>$(KSAContent)$(ModName)\</ModDir>
	<ModsDir>$(UserProfile)\Documents\My Games\Kitten Space Agency\mods\</ModsDir>
	<OutputPath>$(ModsDir)$(ModName)\</OutputPath>
	<AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="StarMap.API" Version="0.3.6" />
    <PackageReference Include="Lib.Harmony" Version="2.4.2" />

    <ModFile Include="$(OutputPath)\$(ModName).dll" />
    <ModFile Include="..\Content\**\*" />

    <_KsaAssemblies Include="$(KSADir)Brutal*.dll" Path="%(FullPath)" />
    <_KsaAssemblies Include="$(KSADir)KSA.dll" Path="%(FullPath)" />
    <_KsaAssemblies Include="$(KSADir)Planet*.dll" Path="%(FullPath)" />
    <_KsaAssemblies Include="$(KSADir)Tomlet.dll" Path="%(FullPath)" />
    <Reference Include="@(_KsaAssemblies->'%(FileName)')" HintPath="%(Path)" Private="False" />
```

## Game APIs touched

None (build time).

## Pitfalls and version notes

- **Hard-coded, committed paths.** `KSADir` is `E:\Games\...` and the props file is tracked, so
  every other developer edits a tracked file. Making the property conditional
  (`Condition="'$(KSADir)' == ''"`) or moving it to an untracked `Directory.Build.props.user` lets it
  be overridden from the command line or environment.
- **Dead items.** `ModFile`, `_ModFiles`, `ModDir` and `KSAContent` are defined but no target uses
  them; they look like the remains of a copy-to-`Content` deploy step.
- **Copy-local side effects.** `CopyLocalLockFileAssemblies` plus default `Private` on the HintPath
  references copies `StarMap.API.dll` (and, if present, `ShaderExtensions.dll`) into the mods folder.
  A built copy of this mod once committed as a test fixture in the ShaderExtensions repo (removed in
  that repo's commit `2d76148`) contained `StarMap.API.dll` and `AircraftHUD.deps.json`. The
  ShaderExtensions reference is not needed at runtime (see
  [ShaderExtensions push constants and uniform buffers from C#](sx-push-constants-and-buffers.md)).
- **Windows only.** Backslash paths and `$(UserProfile)\Documents` assume Windows.
- `.gitignore` covers `bin/`, `obj/` and `.vs/`; because output goes to the mods folder, `bin/` stays
  mostly empty.[^ah-gitignore]

## See also

- [ModMenu entry with a vendored attributes DLL](modmenu-entry-vendored-attributes.md)
- [KSAero: Build and deploy targets](../../ksaero/features/mod-deploy-targets.md)
- [KittenExtensions: MSBuild-generated mod.toml and deploy](../../kittenextensions/features/msbuild-mod-toml-generation.md)
- [Point OutputPath straight at the mods folder](../../../howto/getting-started/build/deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-2.md) - how-to article demonstrating this feature
- [Reference game DLLs with a KSADir property and wildcard Reference items](../../../howto/getting-started/build/reference-game-assemblies-from-a-mod-project/reference-game-assemblies-from-a-mod-project-1.md) - how-to article demonstrating this feature

[^ah-props]: KSA_AircraftHUD/AircraftHUD.props
[^ah-csproj]: KSA_AircraftHUD/AircraftHUD.csproj
[^ah-gitignore]: KSA_AircraftHUD/.gitignore
[^starmap-alc]: StarMap ModAssemblyLoadContext.cs
