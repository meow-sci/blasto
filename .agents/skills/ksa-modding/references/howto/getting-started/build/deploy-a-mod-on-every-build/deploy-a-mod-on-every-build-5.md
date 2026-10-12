---
type: How-To
title: Generate a launchSettings profile so Rider or Visual Studio runs StarMap and hits mod breakpoints
description: Write a StarMap.exe launch profile with the right working directory on first build, so F5 builds, deploys and starts the game under the debugger in any fresh checkout or worktree
category: getting-started
group: build
topic: deploy-a-mod-on-every-build
approach: 5
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksaero/features/mod-deploy-targets
  - mods/ksa-deltavmap/features/mod-deploy-targets-and-release
  - mods/starmap/features/host-process-launch
techniques: [msbuild, starmap-lifecycle]
hooks: [msbuild BeforeTargets=Build]
lifecycle: [startup:starmap-before-main]
game_types: [KSA.Constants]
tags: [msbuild, launchsettings, debugging, rider, visual-studio, worktree, starmap]
---

To debug a mod, the debugger has to launch the **game**, not your DLL. StarMap hosts KSA inside `StarMap.exe`, so the
IDE must start `StarMap.exe` with its own folder as the working directory; then breakpoints in your mod hit, because the
mod runs inside that process. KSAero, KSA-DeltaVMap and KSA-MeasureTools write that launch profile automatically, once
per checkout, from the same `ModDeploy.targets` that deploys the mod ([Imported ModDeploy.targets with locked-DLL diagnosis](./deploy-a-mod-on-every-build-1.md)). This article
is that sub-step; the working-directory rule comes from how StarMap boots ([Host process, contexts and hook binding walkthrough](../../loader/how-starmap-launches-ksa/how-starmap-launches-ksa-1.md)).

## Background: how the game does it

`StarMap.exe` reads `./StarMapConfig.json` and loads `./0Harmony.dll` and `./StarMap.Core.dll` relative to the **current
directory**, so a launch profile whose working directory is not the StarMap folder fails before the game starts.
If the config file is missing it writes a default and exits with "Please fill the StarMapConfig.json and restart the
program".[StarMap/Program.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/Program.cs#L9-L29)[StarMap/LoaderConfig.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/LoaderConfig.cs#L7-L51) After loading `KSA.dll` it sets the working directory and
`APP_CONTEXT_BASE_DIRECTORY` to the KSA folder itself.[StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L25-L56) The IDE's attached debugger then sees the
mod assemblies loaded into their own contexts: breakpoints bind when the assembly loads, which happens during
`startup:starmap-before-main` for the entry assembly.

## Lifecycle and timing

Build time for the target; run time for the profile. The deploy (`CopyToMods`, after `Build`) must run before the game
starts, which is why the profile belongs to the same project: **Run** builds, deploys and launches. A rebuild cannot
replace the DLL while the debugged game runs.

## Steps

1. **Generate the profile when missing**, before `Build`. It is gitignored because it holds absolute paths:[build/ModDeploy.targets](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/build/ModDeploy.targets#L38-L58)

   ```xml
   <!-- build/ModDeploy.targets L38-L58 -->
     <Target Name="EnsureLaunchProfile" BeforeTargets="Build"
             Condition="'$(GenerateLaunchProfile)' == 'true' AND !Exists('$(LaunchProfile)')">
       <PropertyGroup>
         <_StarMapDirJson>$(StarMapDir.Replace('\','\\'))</_StarMapDirJson>
         <_LaunchProfileBody>{
     "profiles": {
       "StarMap": {
         "commandName": "Executable",
         "executablePath": "$(_StarMapDirJson)\\StarMap.exe",
         "workingDirectory": "$(_StarMapDirJson)"
       }
     }
   }</_LaunchProfileBody>
       </PropertyGroup>
       <MakeDir Directories="$(MSBuildProjectDirectory)\Properties" />
       <WriteLinesToFile File="$(LaunchProfile)" Lines="$(_LaunchProfileBody)" Overwrite="true" />
       <Message Importance="high"
                Text="$(MSBuildProjectName): wrote the StarMap launch profile to Properties\launchSettings.json. Reload the solution to get it into the run configurations." />
       <Warning Condition="!Exists('$(StarMapDir)\StarMap.exe')"
                Text="No StarMap.exe in &quot;$(StarMapDir)&quot;. Build with -p:StarMapDir=... and delete Properties\launchSettings.json to regenerate it." />
     </Target>
   ```

2. **Set `StarMapDir`** (default `%USERPROFILE%\StarMap`) with `-p:StarMapDir=...` or a property in the project; delete
   the generated file to regenerate it.
3. **Gitignore it:** `**/Properties/launchSettings.json`. Fresh worktrees otherwise have no launch profile at all
   and the IDE has nothing to run; generating it lets any worktree start StarMap without copying files by hand.
4. **Reload the solution** after the first generation so the IDE picks the profile up (Rider and Visual Studio both read
   `Properties/launchSettings.json`).
5. **Stamp which checkout last deployed.** `StampDeploySlot` writes `.deployed-from` (checkout path, branch, commit,
   time) and says "deploy slot taken over" when a different worktree deploys last, because only one checkout at a time
   can reach the game:[build/ModDeploy.targets](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/build/ModDeploy.targets#L89-L127)

   ```xml
   <!-- build/ModDeploy.targets L115-L117 (excerpt) -->
       <Message Importance="high"
                Condition="'$(_PreviousCheckout)' != '' AND '$(_PreviousCheckout)' != '$(_DeployCheckout)'"
                Text="$(ModDeployName): deploy slot taken over. The game was loading the build from &quot;$(_PreviousCheckout)&quot;, from now on it loads &quot;$(_DeployCheckout)&quot;. Only one checkout at a time can reach the game." />
   ```

6. **Debug.** Select the `StarMap` profile, press Run. The console shows `StarMap - Loaded mod: <id>`; set breakpoints in
   `StarMapAllModsLoaded` or a frame hook. Attach-mode also works (attach to `StarMap.exe`), but the profile makes the
   working directory right.

## Complete example

| File | Role |
|---|---|
| [build/ModDeploy.targets](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/build/ModDeploy.targets) | `EnsureLaunchProfile`, `CopyToMods`, `StampDeploySlot` |
| [build/ModDeploy.targets](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/build/ModDeploy.targets) | The same file in KSA-DeltaVMap, with a comment explaining the fresh-worktree motivation and `PatchFile` support |

KittenExtensions solves the same thing with a checked-in `launchSettings.json` and a `StarMapStart` profile selected in
a `.csproj.user` ([Shared KSADLLs.props glob for several mods](../reference-game-assemblies-from-a-mod-project/reference-game-assemblies-from-a-mod-project-3.md)).

## Variations and gotchas

- **Windows-only as written** (`%USERPROFILE%`, `.exe`). KSAGolf's README tells Linux players to run `dotnet StarMap.dll` from the StarMap folder; an
  analogous profile would use `dotnet` as the executable, `StarMap.dll` as the argument and the StarMap folder as the
  working directory (not exercised here).
- **The profile holds absolute paths**; do not commit it.
- **Hot reload is limited.** The mod loads at startup; edits need a rebuild and restart, and the build cannot overwrite
  the DLL while the game runs.
- **The first launch needs `StarMapConfig.json`**: run `StarMap.exe` once from its folder and fill `GameLocation`.
- **Attach timing.** Assembly breakpoints in `StarMapBeforeMain` need the debugger attached from process start, which
  the launch profile gives you and an attach-later flow does not.

## See also

- [Imported ModDeploy.targets with locked-DLL diagnosis](./deploy-a-mod-on-every-build-1.md), [Deploy a mod on every build and debug it from an IDE](./overview.md)
- [Build and deploy targets](../../../../mods/ksaero/features/mod-deploy-targets.md), [Shared MSBuild deploy targets, release zip and SpaceDock publishing](../../../../mods/ksa-deltavmap/features/mod-deploy-targets-and-release.md), [Host KSA in-process from a launcher executable](../../../../mods/starmap/features/host-process-launch.md)
- [How StarMap launches KSA and loads your mod](../../loader/how-starmap-launches-ksa/overview.md), [Choose and use the right StarMap lifecycle hook](../../lifecycle/choose-a-starmap-startup-hook/overview.md)
