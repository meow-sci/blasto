---
type: How-To
title: Import a shared ModDeploy.targets that diagnoses a locked DLL
description: Copy the mod DLL and mod.toml into the KSA mods folder after every build with a single-attempt copy that names the running game when the file is locked, plus a deploy stamp
category: getting-started
group: build
topic: deploy-a-mod-on-every-build
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksaero/features/mod-deploy-targets
  - mods/ksa-deltavmap/features/mod-deploy-targets-and-release
  - mods/ksa-measuretools/features/mod-deploy-targets-and-release
techniques: [msbuild]
hooks: [msbuild AfterTargets=Build]
lifecycle: [startup:starmap-before-main]
game_types: [KSA.Constants]
tags: [msbuild, deploy, modsdeploy, locked-dll, worktree, ksaero]
---

You want `dotnet build` (or F5) to leave a runnable mod in the KSA mods folder, and a build that fails because the
game is running to say so. KSAero's `build/ModDeploy.targets` (the same file, adapted, appears in KSA-DeltaVMap and
KSA-MeasureTools) does this with a post-build copy that makes exactly one attempt and, only on failure, asks PowerShell
which of `StarMap` or `KSA` is running. It also stamps the deployed folder with the checkout it came from. The
lightest alternatives are [OutputPath straight into the mods folder](./deploy-a-mod-on-every-build-2.md) and [Post-build copy with an overridable game directory](./deploy-a-mod-on-every-build-4.md); the IDE launch
profile part is [Generated launchSettings profile from ModDeploy.targets](./deploy-a-mod-on-every-build-5.md).

## Background: how the game does it

KSA and StarMap look for a mod in `<Documents>/My Games/Kitten Space Agency/mods/<id>/` (or `Content/<id>`), where
`Constants.DocumentsFolderPath` is `<Personal>/My Games/Kitten Space Agency` and `ModLibrary.LocalModsFolderPath` is its
`mods` subfolder.[current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L287)[current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178-L180) The folder name is the mod id, so the deploy
folder must be named after the mod, and the DLL inside must match `EntryAssembly`. Once the game runs, Windows keeps the
loaded mod DLL open, so a rebuild cannot overwrite it; that is the failure the target explains. Under a StarMap
`-InstancePath`, the mods folder moves with `DocumentsFolderPath` ([Manual Harmony prefix on Constants.DocumentsFolderPath](../../loader/how-starmap-launches-ksa/how-starmap-launches-ksa-3.md)); this deploy writes to
the default location.

## Lifecycle and timing

Build time. The deployed layout is what StarMap and the game read at the next launch (`startup:starmap-before-main` for
`mod.toml`'s `[StarMap]` table). A deploy cannot update a running game; restart it.

## Steps

1. **Import the targets near the end of the csproj**, after your own groups. KSAero's project is otherwise just
   references plus a `mod.toml` copy rule:[KSAero/KSAero.csproj](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/KSAero.csproj#L47-L56)

   ```xml
   <!-- KSAero/KSAero/KSAero.csproj L47-L55 -->
     <ItemGroup>
       <None Update="mod.toml">
         <CopyToOutputDirectory>Always</CopyToOutputDirectory>
       </None>
     </ItemGroup>

     <!-- Deploy into the game's mods folder on every build, write the StarMap launch profile, and
          guard against the running game holding the DLL. -->
     <Import Project="..\build\ModDeploy.targets" />
   ```

2. **Configure by properties**, all optional: `ModDeployName` (default `AssemblyName`), `DeployToMods` (false builds
   without touching the game folder), `GenerateLaunchProfile`, `StarMapDir` (default `%USERPROFILE%\StarMap`):[build/ModDeploy.targets](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/build/ModDeploy.targets#L26-L36)

   ```xml
   <!-- build/ModDeploy.targets L26-L36 -->
     <PropertyGroup>
       <ModDeployName Condition="'$(ModDeployName)' == ''">$(AssemblyName)</ModDeployName>
       <DeployToMods Condition="'$(DeployToMods)' == ''">true</DeployToMods>
       <GenerateLaunchProfile Condition="'$(GenerateLaunchProfile)' == ''">true</GenerateLaunchProfile>
       <StarMapDir Condition="'$(StarMapDir)' == ''">$(USERPROFILE)\StarMap</StarMapDir>

       <ModDeployDir>$(USERPROFILE)\Documents\My Games\Kitten Space Agency\mods\$(ModDeployName)\</ModDeployDir>
       <ModDeployStamp>$(ModDeployDir).deployed-from</ModDeployStamp>
       <LaunchProfile>$(MSBuildProjectDirectory)\Properties\launchSettings.json</LaunchProfile>
       <ModRepoRoot>$([System.IO.Path]::GetFullPath('$(MSBuildThisFileDirectory)..'))</ModRepoRoot>
     </PropertyGroup>
   ```

3. **Copy with one attempt; explain failure.** `Retries="0"` avoids ten silent retries; the PowerShell probe runs only
   on failure, so a normal build never pays for it; the error names the running processes:[build/ModDeploy.targets](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/build/ModDeploy.targets#L60-L87)

   ```xml
   <!-- build/ModDeploy.targets L60-L87 -->
     <Target Name="CopyToMods" AfterTargets="Build" Condition="'$(DeployToMods)' == 'true'">
       <MakeDir Directories="$(ModDeployDir)" />

       <!-- One attempt: the only thing that ever locks this file is the running game. -->
       <Copy SourceFiles="$(OutputPath)$(AssemblyName).dll"
             DestinationFolder="$(ModDeployDir)"
             Retries="0"
             ContinueOnError="true">
         <Output TaskParameter="CopiedFiles" ItemName="_ModDeployedAssembly" />
       </Copy>

       <!-- Only on failure, so a normal build never pays for starting PowerShell. -->
       <Exec Condition="'@(_ModDeployedAssembly)' == ''"
             Command="powershell -NoProfile -NonInteractive -Command &quot;(Get-Process -Name StarMap,KSA -ErrorAction SilentlyContinue | ForEach-Object { $_.ProcessName + '.exe pid ' + $_.Id }) -join ', '&quot;"
             ConsoleToMSBuild="true"
             StandardOutputImportance="low"
             IgnoreExitCode="true"
             ContinueOnError="true">
         <Output TaskParameter="ConsoleOutput" PropertyName="_RunningGameProcesses" />
       </Exec>

       <Error Condition="'@(_ModDeployedAssembly)' == '' AND '$(_RunningGameProcesses)' != ''"
              Text="The game is running ($(_RunningGameProcesses)) and holds the deployed $(AssemblyName).dll open. Compiling succeeded, only the copy into &quot;$(ModDeployDir)&quot; was skipped. Close the game, then build again." />
       <Error Condition="'@(_ModDeployedAssembly)' == '' AND '$(_RunningGameProcesses)' == ''"
              Text="Could not copy $(AssemblyName).dll into &quot;$(ModDeployDir)&quot;. The game does not look like it is running, so check that the path exists and is writable." />

       <Copy SourceFiles="$(MSBuildProjectDirectory)\mod.toml" DestinationFolder="$(ModDeployDir)" />
     </Target>
   ```

4. **Stamp the slot.** `StampDeploySlot` writes `.deployed-from` (checkout path, branch, short commit, UTC time) and
   prints "deploy slot taken over" when a different checkout or worktree deploys last, since only one checkout can reach
   the game at a time (see the file for the full target).
5. **Add content and patches** by extending the copy: KSA-DeltaVMap's version of the same targets also accepts
   `@(PatchFile)` items to deploy into `<mod>\Patches\` for KittenExtensions; AdvancedFlightComputer's adds a shared
   payload list ([Explicit payload list shared by deploy and packaging](./deploy-a-mod-on-every-build-3.md)).
6. **Register the mod in `manifest.toml`** once, or accept the new-mod dialog and the restart prompt
   ([Folder and manifest rules as StarMap consumes them](../../loader/how-starmap-launches-ksa/how-starmap-launches-ksa-2.md)).

## Complete example

| File | Role |
|---|---|
| [build/ModDeploy.targets](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/build/ModDeploy.targets) | `EnsureLaunchProfile`, `CopyToMods`, `StampDeploySlot` |
| [KSAero/KSAero.csproj](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/KSAero.csproj) | Import, `mod.toml` copy rule, `PackageRelease` ([Release target that zips the deployed folder](../package-and-publish-a-mod-release/package-and-publish-a-mod-release-1.md)) |
| [build/ModDeploy.targets](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/build/ModDeploy.targets), [build/ModDeploy.targets](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/build/ModDeploy.targets) | The same plumbing in two more repos (DeltaVMap and MeasureTools), with `PatchFile` support and their own comments |

## Variations and gotchas

- **Windows-only as written.** `%USERPROFILE%`, backslashes and PowerShell assume Windows; Linux users need a script
  (KSArmory's `deploy.sh`, approach 3 notes).
- **NuGet DLLs are not copied.** KSAero deploys only its own DLL and `mod.toml`; Harmony and `StarMap.API` are
  compile-time only because StarMap supplies them ([Ship third-party DLLs next to your mod](../../dependencies/ship-third-party-dlls-with-a-mod/overview.md)).
- **A running game fails the build** after compiling; the message is an `Error`, not a retry loop.
- **Old files linger.** This target does not clean the mod folder; stale files from earlier builds remain.
- **The folder name is the id**: change `ModDeployName` only together with the manifest entry.

## See also

- [OutputPath straight into the mods folder](./deploy-a-mod-on-every-build-2.md), [Explicit payload list shared by deploy and packaging](./deploy-a-mod-on-every-build-3.md), [Post-build copy with an overridable game directory](./deploy-a-mod-on-every-build-4.md), [Generated launchSettings profile from ModDeploy.targets](./deploy-a-mod-on-every-build-5.md), [Deploy a mod on every build and debug it from an IDE](./overview.md)
- [Build and deploy targets](../../../../mods/ksaero/features/mod-deploy-targets.md), [Shared MSBuild deploy targets, release zip and SpaceDock publishing](../../../../mods/ksa-deltavmap/features/mod-deploy-targets-and-release.md),
  [Shared MSBuild deploy targets, release zip and SpaceDock publishing](../../../../mods/ksa-measuretools/features/mod-deploy-targets-and-release.md)
- [Reference the game's assemblies from a mod project without shipping them](../reference-game-assemblies-from-a-mod-project/overview.md), [Package and publish a mod release (zip, CI publishing, installer)](../package-and-publish-a-mod-release/overview.md)
