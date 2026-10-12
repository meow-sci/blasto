---
type: Mod Feature
title: Build and deploy targets
description: An MSBuild setup that compiles against the installed game's DLLs via a KsaDir property, takes Harmony and StarMap.API from NuGet, deploys the DLL and mod.toml into the KSA mods folder on every build with a locked-file diagnosis and a deploy stamp, and zips a release
mod: ksaero
category: getting-started
techniques: [msbuild-targets, nuget-references, mod-packaging]
hooks: []
lifecycle: []
game_types: [KSA.ModLibrary, KSA.Constants]
tags: [msbuild, build, deploy, nuget, starmap, tooling]
---

`dotnet build KSAero` compiles the mod against the game you have installed and drops `KSAero.dll` and
`mod.toml` straight into the game's mods folder, so the next launch through StarMap runs the new
build. The deploy logic lives in an importable [build/ModDeploy.targets](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/build/ModDeploy.targets)
that its header credits to KSA-AdvancedFlightComputer's file of the same name
([build/ModDeploy.targets](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/ModDeploy.targets) in that repository). Release builds also
produce a ready-to-share `KSAero.zip`.[^ksaero-moddeploy] [^afc-moddeploy] [^ksaero-readme]

## How it works

### Project file

| Piece | What it does | Where |
|---|---|---|
| `KsaDir` | Game install folder, default `C:\Program Files\Kitten Space Agency`, overridable with `-p:KsaDir=...` | [KSAero/KSAero.csproj](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/KSAero.csproj#L8) |
| Game references | `KSA`, `Brutal.Core.Numerics`, `Brutal.Core.Logging`, `Brutal.Core.Strings`, `Brutal.ImGui`, `BepuPhysics`, `BepuUtilities` by `HintPath` into `$(KsaDir)`, each `Private=false` so they are never copied next to the mod | [KSAero/KSAero.csproj](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/KSAero.csproj#L16-L45) |
| NuGet | `Lib.Harmony` 2.4.2 and `StarMap.API` 0.3.6, compile-time only in practice (see below) | [KSAero/KSAero.csproj](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/KSAero.csproj#L11-L14) |
| Package source | `NuGet.config` clears inherited sources and uses nuget.org only | [NuGet.config](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/NuGet.config) |
| `mod.toml` | Copied to the output on every build | [KSAero/KSAero.csproj](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/KSAero.csproj#L47-L51) |
| Deploy import | `<Import Project="..\build\ModDeploy.targets" />` after the project's own groups | [KSAero/KSAero.csproj](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/KSAero.csproj#L53-L55) |
| `PackageRelease` | Release only: stage `KSAero.dll` and `mod.toml` in `release\KSAero\`, `ZipDirectory` to `KSAero.zip`, clean up | [KSAero/KSAero.csproj](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/KSAero.csproj#L57-L69) |

Sources:[^ksaero-csproj] [^ksaero-nugetconfig]

### ModDeploy.targets

| Target or property | Behaviour | Where |
|---|---|---|
| Inputs | `ModDeployName` (default `AssemblyName`), `DeployToMods` (default `true`), `GenerateLaunchProfile` (default `true`), `StarMapDir` (default `%USERPROFILE%\StarMap`) | [build/ModDeploy.targets](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/build/ModDeploy.targets#L26-L36) |
| `ModDeployDir` | `%USERPROFILE%\Documents\My Games\Kitten Space Agency\mods\<ModDeployName>\` | [build/ModDeploy.targets](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/build/ModDeploy.targets#L32) |
| `EnsureLaunchProfile` (before Build) | Writes `Properties\launchSettings.json` with a `StarMap` profile pointing at `StarMap.exe`, only when missing; warns if `StarMap.exe` is not there. The file holds absolute paths and is gitignored | [build/ModDeploy.targets](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/build/ModDeploy.targets#L38-L58) |
| `CopyToMods` (after Build) | One copy attempt (`Retries="0"`, `ContinueOnError`); on failure only, a PowerShell `Get-Process StarMap,KSA` names the running game in a single readable error; then copies `mod.toml` | [build/ModDeploy.targets](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/build/ModDeploy.targets#L60-L87) |
| `StampDeploySlot` (after CopyToMods) | Writes `.deployed-from` in the mod folder (checkout path, branch, short commit, UTC time) and prints a message when a different checkout or worktree takes the slot over | [build/ModDeploy.targets](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/build/ModDeploy.targets#L89-L127) |

The generated launch profile is excluded by `.gitignore`.[^ksaero-gitignore]

### What StarMap needs at runtime

- The folder holds only `KSAero.dll` and `mod.toml`. `mod.toml` is `name = "KSAero"` and
  `[StarMap] EntryAssembly = "KSAero"` ([KSAero/mod.toml](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/mod.toml)).[^ksaero-modtoml]
- StarMap looks for `mod.toml` in the game's content folder first and then in
  `ModLibrary.LocalModsFolderPath`, reads `EntryAssembly`, and loads `<EntryAssembly>.dll` into a
  per-mod `ModAssemblyLoadContext`
  ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L41-L63)).[^starmap-runtimemod]
  `LocalModsFolderPath` is `Constants.DocumentsFolderPath` + `mods`, and `DocumentsFolderPath` is the
  user's Documents folder + `My Games\Kitten Space Agency`
  ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178),
  [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)), which is the folder the
  targets write to.[^ksa-modlibrary] [^ksa-constants]
- Harmony and `StarMap.API` are not deployed. StarMap loads `0Harmony.dll` into the default load
  context before the game ([StarMap/Program.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/Program.cs#L18)), and the mod load
  context resolves any assembly already present in the default or core context before looking next to
  the mod ([StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L47)).[^starmap-program] [^starmap-modalc]
- The player still has to enable the mod in the game's mod list or `manifest.toml`.

## Lifecycle and timing

Build time, not game time. The deployed layout is consumed once per launch when StarMap creates its
runtime mods, which is what later fires `[StarMapAllModsLoaded]`, `[StarMapAfterGui]` and
`[StarMapUnload]` on the entry class. A build while the game is running cannot replace the loaded DLL;
the targets detect that case and fail with a clear message instead of retrying.

## Key code

| Path | Role |
|---|---|
| [KSAero/KSAero.csproj](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/KSAero.csproj) | `KsaDir`, references, NuGet, import, release zip |
| [build/ModDeploy.targets](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/build/ModDeploy.targets) | Launch profile, deploy, lock diagnosis, deploy stamp |
| [KSAero/mod.toml](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/mod.toml) | StarMap manifest |
| [NuGet.config](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/NuGet.config) | Package source pinning |
| [.gitignore](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/.gitignore) | Ignores the generated `launchSettings.json` |

## Snippets

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

```xml
<!-- KSAero/KSAero.csproj L3-L24 -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PlatformTarget>x64</PlatformTarget>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <KsaDir Condition="'$(KsaDir)' == ''">C:\Program Files\Kitten Space Agency</KsaDir>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Lib.Harmony" Version="2.4.2" />
    <PackageReference Include="StarMap.API" Version="0.3.6" />
  </ItemGroup>

  <ItemGroup>
    <Reference Include="KSA">
      <HintPath>$(KsaDir)\KSA.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Brutal.Core.Numerics">
      <HintPath>$(KsaDir)\Brutal.Core.Numerics.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <!-- ... -->
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ModLibrary.LocalModsFolderPath` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178) | Where StarMap finds a user-installed mod |
| `Constants.DocumentsFolderPath` | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281) | `Documents\My Games\Kitten Space Agency` |

## Pitfalls and version notes

- **Windows only as written.** `%USERPROFILE%`, backslash paths, `StarMap.exe` and PowerShell are
  assumed. Elsewhere, pass `-p:DeployToMods=false` and `-p:GenerateLaunchProfile=false`, or override
  the paths.
- **Instance paths.** StarMap can redirect `Constants.DocumentsFolderPath` with `-InstancePath` or the
  `STARMAP_INSTANCE_PATH` environment variable
  ([StarMap.Core/Patches/DocumentsPathPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/DocumentsPathPatches.cs#L8-L44));
  the targets always write under `%USERPROFILE%\Documents`, so a redirected instance will not see the
  build.[^starmap-documentspath]
- **Match the game build.** The references come from whatever is in `KsaDir`; building against a
  different game build than the one you run can bind to members that no longer exist.
- **Do not ship Harmony or StarMap.API.** They are resolved from StarMap's contexts; a private copy in
  the mod folder would not be used for those names anyway, because StarMap's contexts are checked
  first.
- **One checkout per slot.** Two worktrees deploying the same `ModDeployName` overwrite each other;
  the `.deployed-from` stamp and its takeover message are there to make that visible.
- `StarMap.API` 0.3.6 is a NuGet version; the StarMap checkout pinned here (`4dc60f8`) carries no
  package version, so the exact match between the two is unverified.

## See also

- [Headless model tests](headless-model-tests.md)
- [Game version check](game-version-check.md)
- [Import a shared ModDeploy.targets that diagnoses a locked DLL](../../../howto/getting-started/build/deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-1.md) - how-to article demonstrating this feature
- [Generate a launchSettings profile so Rider or Visual Studio runs StarMap and hits mod breakpoints](../../../howto/getting-started/build/deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-5.md) - how-to article demonstrating this feature
- [Zip the deployed folder with a Release MSBuild target](../../../howto/getting-started/build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-1.md) - how-to article demonstrating this feature
- [Reference game DLLs with a KSADir property and wildcard Reference items](../../../howto/getting-started/build/reference-game-assemblies-from-a-mod-project/reference-game-assemblies-from-a-mod-project-1.md) - how-to article demonstrating this feature

[^ksaero-moddeploy]: build/ModDeploy.targets
[^afc-moddeploy]: KSA-AdvancedFlightComputer build/ModDeploy.targets
[^ksaero-readme]: KSAero/README.md
[^ksaero-csproj]: KSAero/KSAero.csproj
[^ksaero-nugetconfig]: NuGet.config
[^ksaero-gitignore]: .gitignore
[^ksaero-modtoml]: KSAero/mod.toml
[^starmap-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-constants]: KSA.Constants (decompiled, build 2026.10.10.5554)
[^starmap-program]: StarMap/Program.cs
[^starmap-modalc]: StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
[^starmap-documentspath]: StarMap.Core/Patches/DocumentsPathPatches.cs
