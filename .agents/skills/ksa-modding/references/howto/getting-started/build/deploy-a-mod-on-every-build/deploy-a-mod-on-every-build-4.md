---
type: How-To
title: Add a plain post-build copy with an overridable KSAGameDir
description: Lowest-ceremony deploy - an AfterBuild target that copies the DLL, deps.json, manifest and data into the mods folder, with the game directory overridable from the environment or an untracked user props file
category: getting-started
group: build
topic: deploy-a-mod-on-every-build
approach: 4
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/ohmystars/features/mod-root-and-packaging
  - mods/kitten-engineer-redux/features/dist-zip-and-post-build-deploy
  - mods/gatos/features/build-deploy-and-ci
techniques: [msbuild]
hooks: [msbuild AfterTargets=AfterBuild]
lifecycle: [startup:starmap-before-main]
game_types: [KSA.Constants]
tags: [msbuild, deploy, post-build, ksagamedir, user-props, ohmystars, gatos]
---

For most mods a deploy is: after the build, copy a handful of named files into
`<Documents>/My Games/Kitten Space Agency/mods/<Mod>/`. OhMyStars, Kitten-Engineer-Redux and gatOS each write that as a
plain `AfterBuild` target with no diagnosis of a running game. The useful extras are an **overridable game directory**
(an environment variable or an untracked `.user.props`), copying only what you ship, and, in gatOS's case, wiping stale
files without destroying user data. Compare the richer imported targets of [Imported ModDeploy.targets with locked-DLL diagnosis](./deploy-a-mod-on-every-build-1.md) and the
zero-target form of [OutputPath straight into the mods folder](./deploy-a-mod-on-every-build-2.md).

## Background: how the game does it

The mods folder is `ModLibrary.LocalModsFolderPath` under `Constants.DocumentsFolderPath`; the folder name is the mod
id.[current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L287) The deploy must place the entry DLL, `mod.toml`, the entry assembly's `deps.json` (when it has private
dependencies; StarMap's per-mod `AssemblyDependencyResolver` reads it) and any data the code locates beside the DLL
([Find your mod's install folder at runtime: Assembly.Location, then ModLibrary.Find, then Documents](../../loader/ship-content-and-code-in-one-mod/ship-content-and-code-in-one-mod-3.md)). It must not place `0Harmony.dll` or `StarMap.API.dll`
([Ship third-party DLLs next to your mod](../../dependencies/ship-third-party-dlls-with-a-mod/overview.md)).

## Lifecycle and timing

Build time. These targets neither check for a running game nor clean up; if the DLL is locked the `Copy` task fails.

## Steps

1. **Make the game directory overridable** so no tracked file holds a personal path:[src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L10-L15)

   ```xml
   <!-- OhMyStars/src/OhMyStars.csproj L10-L15 -->
     <!-- Path to the KSA install; override via KSAGameDir env var or a local, untracked .user.props file -->
     <PropertyGroup>
       <KSAGameDir Condition="'$(KSAGameDir)' == ''">$(ProgramFiles)\Kitten Space Agency</KSAGameDir>
     </PropertyGroup>

     <Import Project="OhMyStars.csproj.user.props" Condition="Exists('OhMyStars.csproj.user.props')" />
   ```

   Add the user props file to `.gitignore`. (`ModsFolder` is derived from `%USERPROFILE%` and is not overridable here.)
2. **Write the copy target** after `AfterBuild`; copy by explicit name, and mirror data folders through an item list with
   `TargetPath` metadata:[src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L25-L48)

   ```xml
   <!-- OhMyStars/src/OhMyStars.csproj L38-L49 -->
     <Target Name="CopyCustomContent" AfterTargets="AfterBuild">
       <Copy SourceFiles="$(BuildDir)mod.toml" DestinationFolder="$(SendBuildToTestDir)" />
       <Copy SourceFiles="$(BuildDir)$(ModNamespace).deps.json" DestinationFolder="$(SendBuildToTestDir)" />
       <Copy SourceFiles="$(BuildDir)$(ModNamespace).dll" DestinationFolder="$(SendBuildToTestDir)" />
       <Copy SourceFiles="ModMenu.Attributes.dll" DestinationFolder="$(SendBuildToTestDir)" />
       <Copy SourceFiles="$(MSBuildProjectDirectory)\settings.ini" DestinationFolder="$(SendBuildToTestDir)" SkipUnchangedFiles="false" />
       <Copy SourceFiles="$(BuildDir)athyg_32_reduced_m10.csv" DestinationFolder="$(SendBuildToTestDir)" />
       <Copy SourceFiles="$(BuildDir)NOTICE.txt" DestinationFolder="$(SendBuildToTestDir)" />
       <MakeDir Directories="$(BuildDir)licenses;$(BuildDir)skycultures;$(SendBuildToTestDir)\licenses;$(SendBuildToTestDir)\skycultures" />
       <Copy SourceFiles="@(ModContent)" DestinationFiles="@(ModContent->'$(BuildDir)\%(TargetPath)')" SkipUnchangedFiles="true" />
       <Copy SourceFiles="@(ModContent)" DestinationFiles="@(ModContent->'$(SendBuildToTestDir)\%(TargetPath)')" SkipUnchangedFiles="true" />
     </Target>
   ```

3. **Kitten-Engineer-Redux's variant** is shorter still: a `PostBuild` target `AfterTargets="PostBuildEvent"` that
   `MakeDir`s the destination and copies the mod DLL and, if present, `ModMenu.Attributes.dll`. It hard-codes the
   author's path, copies no `mod.toml`, and so needs adapting before reuse:[KittenEngineerRedux.csproj](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/KittenEngineerRedux.csproj#L68-L83)

   ```xml
   <!-- Kitten-Engineer-Redux/KittenEngineerRedux.csproj L68-L73 -->
       <Target Name="PostBuild" AfterTargets="PostBuildEvent">
           <PropertyGroup>
               <SourceDll>$(TargetDir)KittenEngineerRedux.dll</SourceDll>
               <SourceAttributesDll>$(TargetDir)ModMenu.Attributes.dll</SourceAttributesDll>
               <DestinationDir>C:\Users\Matthew\Documents\My Games\Kitten Space Agency\mods\KittenEngineerRedux\</DestinationDir>
           </PropertyGroup>
   ```

4. **Wipe stale files, but not user data.** gatOS deletes the previous managed payload so stale DLLs never linger in the
   mod's load context, excluding `gatos.toml` because on Windows the install and data folders coincide, and copies the
   large blobs incrementally with `SkipUnchangedFiles`:[gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj#L43-L112)

   ```xml
   <!-- gatOS.GameMod/gatOS.GameMod.csproj L53-L58 -->
           <ItemGroup>
               <StaleDistFile Include="$(DistDir)*" Exclude="$(DistDir)gatos.toml"/>
           </ItemGroup>
           <Delete Files="@(StaleDistFile)"/>
           <RemoveDir Directories="$(DistDir)third-party-licenses"/>
           <MakeDir Directories="$(DistDir)"/>
   ```

   and copies "everything in the build output EXCEPT the loader-supplied assemblies" with a deny-list glob, so a newly
   added NuGet dependency ships automatically instead of failing in game:

   ```xml
   <!-- gatOS.GameMod/gatOS.GameMod.csproj L82-L85 -->
           <ItemGroup>
               <ModManagedDll Include="$(TargetDir)*.dll"
                              Exclude="$(TargetDir)0Harmony.dll;$(TargetDir)StarMap.API.dll"/>
           </ItemGroup>
   ```

5. **Override the destination for CI** (gatOS: `GATOS_DIST_DIR`, else the user mods folder) so a pipeline can zip what the
   target produced ([Rolling releases from a multi-assembly mod](../package-and-publish-a-mod-release/package-and-publish-a-mod-release-5.md)).

## Complete example

| File | Role |
|---|---|
| [src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj) | `KSAGameDir` override, explicit copy list, data folders |
| [KittenEngineerRedux.csproj](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/KittenEngineerRedux.csproj) | Minimal post-build with a vendored attribute DLL |
| [gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj) | Stale-file wipe, deny-list glob, incremental big blobs |

## Variations and gotchas

- **Windows-only paths.** `%USERPROFILE%` and backslashes; OhMyStars' `ModsFolder` is not overridable.
- **Kitten-Engineer-Redux's deploy copies no `mod.toml`**, even though the project marks it
  `CopyToOutputDirectory=Always`; a first-time deploy needs the manifest copied by hand. Its hint paths and deploy
  folder are absolute for one machine.
- **Never wipe the deploy folder wholesale** if user data can live there (gatOS excludes its live config).
- **Do not ship loader assemblies**; a deny-list glob is robust to new dependencies, an allow-list is robust to strays
  (gatOS notes this trade-off; purrTTY uses an allow-list).
- **`deps.json` must list every private dependency**, including vendored ones, for StarMap's resolver.
- **No running-game guard**, so a locked DLL surfaces as a plain copy error.

## See also

- [Imported ModDeploy.targets with locked-DLL diagnosis](./deploy-a-mod-on-every-build-1.md), [OutputPath straight into the mods folder](./deploy-a-mod-on-every-build-2.md), [Deploy a mod on every build and debug it from an IDE](./overview.md)
- [Mod root and packaging](../../../../mods/ohmystars/features/mod-root-and-packaging.md), [Minimal mod.toml, post-build deploy and release zip](../../../../mods/kitten-engineer-redux/features/dist-zip-and-post-build-deploy.md), [Build, deploy and CI](../../../../mods/gatos/features/build-deploy-and-ci.md)
- [Reference the game's assemblies from a mod project without shipping them](../reference-game-assemblies-from-a-mod-project/overview.md), [Package and publish a mod release (zip, CI publishing, installer)](../package-and-publish-a-mod-release/overview.md)
