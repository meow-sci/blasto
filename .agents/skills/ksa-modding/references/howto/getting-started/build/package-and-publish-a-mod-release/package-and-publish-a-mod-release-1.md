---
type: How-To
title: Zip the deployed folder with a Release MSBuild target
description: Add a Release-only target that stages exactly the files the deploy copies - DLL and mod.toml - and zips them into a folder users extract into their mods directory
category: getting-started
group: build
topic: package-and-publish-a-mod-release
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/ksaero/features/mod-deploy-targets
  - mods/ksa-deltavmap/features/mod-deploy-targets-and-release
  - mods/realatmospheres/features/release-packaging-and-deploy
techniques: [msbuild]
hooks: [msbuild AfterTargets=Build]
lifecycle: []
game_types: []
tags: [packaging, release, zip, msbuild, ksaero, mods-folder]
---

A release is the exact folder a user unzips into `<Documents>/My Games/Kitten Space Agency/mods/`: the DLL, `mod.toml`,
content and licences, with the mod id as the top-level folder name. KSAero's project does it with one
`PackageRelease` target that runs after `Build` in Release only, stages `KSAero.dll` and `mod.toml` under
`release\KSAero\` and zips the staging folder. The version comes from the project. This is the minimal shape; stricter
ones verify the payload ([Payload list verified file by file](./package-and-publish-a-mod-release-2.md)), add data and licences
([Ship data files and third-party licences with the mod](./package-and-publish-a-mod-release-3.md)) and publish from CI ([Workflow reading the tested game version from Mod.cs](./package-and-publish-a-mod-release-4.md)).

## Background: how the game does it

KSA discovers a mod by the folder name under `Content/` or `LocalModsFolderPath`, so the zip must contain the mod id as
its top-level directory (extracting it into `mods/` then yields `mods/<id>/mod.toml`).[current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L234-L371) The
player still has to enable the mod (the new-mod dialog or `manifest.toml`), and StarMap offers a restart
([Folder and manifest rules as StarMap consumes them](../../loader/how-starmap-launches-ksa/how-starmap-launches-ksa-2.md)). `StarMap.API.dll`, `0Harmony.dll` and game DLLs must not be in the zip; they come
from StarMap and the game ([Ship third-party DLLs next to your mod](../../dependencies/ship-third-party-dlls-with-a-mod/overview.md)).

## Lifecycle and timing

Build and release time. Release only: the target is conditioned on `Configuration == Release`, so day-to-day Debug
builds deploy ([Imported ModDeploy.targets with locked-DLL diagnosis](../deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-1.md)) and do not zip.

## Steps

1. **Add the target to the csproj**, after the deploy import:[KSAero/KSAero.csproj](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/KSAero.csproj#L57-L69)

   ```xml
   <!-- KSAero/KSAero/KSAero.csproj L57-L69 -->
     <Target Name="PackageRelease" AfterTargets="Build" Condition="'$(Configuration)' == 'Release'">
       <PropertyGroup>
         <ReleaseDir>$(OutputPath)release\$(AssemblyName)\</ReleaseDir>
         <ZipPath>$(OutputPath)$(AssemblyName).zip</ZipPath>
       </PropertyGroup>
       <RemoveDir Directories="$(OutputPath)release" />
       <MakeDir Directories="$(ReleaseDir)" />
       <Copy SourceFiles="$(OutputPath)$(AssemblyName).dll" DestinationFolder="$(ReleaseDir)" />
       <Copy SourceFiles="mod.toml" DestinationFolder="$(ReleaseDir)" />
       <ZipDirectory SourceDirectory="$(OutputPath)release" DestinationFile="$(ZipPath)" Overwrite="true" />
       <RemoveDir Directories="$(OutputPath)release" />
       <Message Importance="high" Text="Release packaged: $(ZipPath)" />
     </Target>
   ```

2. **Make the staging folder name the mod id** (`$(AssemblyName)` here; use `ModDeployName` if it differs), because the
   zip's root directory becomes the folder under `mods/`.
3. **Build:** `dotnet build -c Release`; the zip is `bin/Release/net10.0/KSAero.zip`.
4. **Set the version in the project** (`<Version>`), and name the zip after it if you publish several; KSA-DeltaVMap and
   KSA-MeasureTools use the same targets file and add their own release steps for GitHub and SpaceDock
   ([Workflow reading the tested game version from Mod.cs](./package-and-publish-a-mod-release-4.md)).
5. **Test the zip, not the build output:** extract it into a clean `mods/` folder (with the game closed), enable the mod
   and check `StarMap - Loaded mod: <id>` in the console.

## Complete example

[KSAero/KSAero.csproj](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/KSAero.csproj): references, `mod.toml` copy rule, deploy import and `PackageRelease`. The README states the tested game build
(`v2026.10.7.5541`) and the deploy folder ([README.md](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/README.md)).

## Variations and gotchas

- **Nothing checks the contents.** If you add content or a dependency and forget to copy it, the zip ships without it;
  approach 2 adds a verification.
- **NuGet DLLs are not staged.** Harmony and `StarMap.API` are compile-time only; if your mod has private runtime
  dependencies, copy them and the `.deps.json` ([Package NuGet dependencies with deps.json](../../dependencies/ship-third-party-dlls-with-a-mod/ship-third-party-dlls-with-a-mod-2.md)).
- **`mod.toml` from the project directory.** `Copy SourceFiles="mod.toml"` is relative to the project; with a generated
  manifest ([Generate mod.toml and StarMap dependencies from the project file](../generate-mod-toml-from-msbuild/overview.md)) copy it from the output instead.
- **Release builds drop the `.pdb`** only if `DebugType` says so; check what lands in the zip.
- **Windows-centered.** `ZipDirectory` is an MSBuild task that works on any OS, but the deploy it follows assumes Windows.

## See also

- [Payload list verified file by file](./package-and-publish-a-mod-release-2.md), [Ship data files and third-party licences with the mod](./package-and-publish-a-mod-release-3.md), [Workflow reading the tested game version from Mod.cs](./package-and-publish-a-mod-release-4.md), [Package and publish a mod release (zip, CI publishing, installer)](./overview.md)
- [Build and deploy targets](../../../../mods/ksaero/features/mod-deploy-targets.md), [Shared MSBuild deploy targets, release zip and SpaceDock publishing](../../../../mods/ksa-deltavmap/features/mod-deploy-targets-and-release.md),
  [Release packaging and deploy scripts](../../../../mods/realatmospheres/features/release-packaging-and-deploy.md)
- [Deploy a mod on every build and debug it from an IDE](../deploy-a-mod-on-every-build/overview.md), [Warn when the running game build differs from the one a mod was tested on](../../lifecycle/check-the-game-version/overview.md)
