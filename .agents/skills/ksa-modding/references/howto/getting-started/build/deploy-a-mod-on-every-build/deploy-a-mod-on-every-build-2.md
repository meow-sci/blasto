---
type: How-To
title: Point OutputPath straight at the mods folder
description: The simplest possible deploy - set OutputPath to the user mods folder and mark content CopyToOutputDirectory, so every build is already the install; no target needed
category: getting-started
group: build
topic: deploy-a-mod-on-every-build
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksa-aircrafthud/features/props-based-build]
techniques: [msbuild]
hooks: [msbuild OutputPath]
lifecycle: [startup:starmap-before-main]
game_types: [KSA.Constants]
tags: [msbuild, deploy, outputpath, props, aircrafthud, simplest]
---

If you only need `dotnet build` to leave a working mod where StarMap looks for it, you can skip custom targets:
make the build's `OutputPath` the mod folder under the user mods directory, and mark every content file
`CopyToOutputDirectory`. KSA_AircraftHUD does this in a tracked props file; the csproj itself is almost empty. The
trade-off is that nothing diagnoses a running game and nothing cleans stale files. Richer options: the imported targets
of [Imported ModDeploy.targets with locked-DLL diagnosis](./deploy-a-mod-on-every-build-1.md) and the post-build copy of [Post-build copy with an overridable game directory](./deploy-a-mod-on-every-build-4.md).

## Background: how the game does it

KSA's per-user mods folder is `ModLibrary.LocalModsFolderPath` = `<Documents>/My Games/Kitten Space Agency/mods`, one
folder per mod, whose name is the mod id.[current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L287) StarMap loads `<folder>/<EntryAssembly>.dll` and
`mod.toml` from it. Any build that puts exactly those files (plus content) into `mods/<ModName>/` is a deploy.

## Lifecycle and timing

Build time. The game must not be running during a build (Windows locks the loaded DLL); a failed overwrite then shows
as a plain MSBuild copy error without naming the cause.

## Steps

1. **Set the output path in a props file** (or the csproj):[AircraftHUD.props](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.props#L2-L10)

   ```xml
   <!-- AircraftHUD.props L2-L10 -->
     <PropertyGroup>
       <ModName>AircraftHUD</ModName>
       <KSADir>E:\\Games\\Kitten Space Agency\\</KSADir>
       <KSAContent>$(KSADir)Content\</KSAContent>
       <ModDir>$(KSAContent)$(ModName)\</ModDir>
   	<ModsDir>$(UserProfile)\Documents\My Games\Kitten Space Agency\mods\</ModsDir>
   	<OutputPath>$(ModsDir)$(ModName)\</OutputPath>
   	<AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
     </PropertyGroup>
   ```

   `AppendTargetFrameworkToOutputPath=false` keeps the layout flat (no `net10.0` subfolder).
2. **Mark every file the mod needs** as copied to the output, so content lands beside the DLL:[AircraftHUD.csproj](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.csproj#L20-L36)

   ```xml
   <!-- AircraftHUD.csproj L20-L32 -->
       </None>
       <None Update="mod.toml">
         <CopyToOutputDirectory>Always</CopyToOutputDirectory>
       </None>
       <None Update="HudAssests.xml">
         <CopyToOutputDirectory>Always</CopyToOutputDirectory>
       </None>
       <None Update="ModMenu.Attributes.dll">
         <CopyToOutputDirectory>Always</CopyToOutputDirectory>
       </None>
       <None Update="Shaders\*">
         <CopyToOutputDirectory>Always</CopyToOutputDirectory>
       </None>
   ```

3. **Set `CopyLocalLockFileAssemblies=true` only if you want NuGet DLLs in the folder.** AircraftHUD does, which also
   copies `StarMap.API.dll` (a harmless but needless file; see gotchas).
4. **Register the mod once** in `manifest.toml` or accept the new-mod dialog and restart prompt
   ([Folder and manifest rules as StarMap consumes them](../../loader/how-starmap-launches-ksa/how-starmap-launches-ksa-2.md)).
5. **Build with the game closed**, then start through `StarMap.exe`.

## Complete example

[AircraftHUD.props](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.props) (paths, NuGet and game references) and [AircraftHUD.csproj](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.csproj) (content copy rules). The game-reference part of the
same props file is the wildcard transform of [KsaDir property with wildcard Reference items and HintPath](../reference-game-assemblies-from-a-mod-project/reference-game-assemblies-from-a-mod-project-1.md).

## Variations and gotchas

- **No cleanup.** Files removed from the project stay in the mods folder; delete the folder occasionally.
- **`bin/` stays mostly empty** because output goes to the mods folder; `.gitignore` still covers `bin/`, `obj/`, `.vs/`.
- **Needless copies.** `CopyLocalLockFileAssemblies` plus default `Private` on HintPath references copies
  `StarMap.API.dll` into the mods folder. A built copy of this mod committed elsewhere as a test fixture contained
  `StarMap.API.dll` and `AircraftHUD.deps.json`. Prefer `Private=false` on references you do not ship.
- **Hard-coded Windows path.** `KSADir` is `E:\Games\...` and `$(UserProfile)\Documents` is Windows-only; make the
  property conditional so it can be overridden.
- **Dead items.** `ModFile`, `ModDir`, `KSAContent` are defined and unused.
- **Releasing is a separate job**: zip the folder yourself or add a target ([Package and publish a mod release (zip, CI publishing, installer)](../package-and-publish-a-mod-release/overview.md)).

## See also

- [Imported ModDeploy.targets with locked-DLL diagnosis](./deploy-a-mod-on-every-build-1.md), [Post-build copy with an overridable game directory](./deploy-a-mod-on-every-build-4.md), [Deploy a mod on every build and debug it from an IDE](./overview.md)
- [Props-based build straight into the mods folder](../../../../mods/ksa-aircrafthud/features/props-based-build.md)
- [Reference the game's assemblies from a mod project without shipping them](../reference-game-assemblies-from-a-mod-project/overview.md), [Ship third-party DLLs next to your mod](../../dependencies/ship-third-party-dlls-with-a-mod/overview.md)
