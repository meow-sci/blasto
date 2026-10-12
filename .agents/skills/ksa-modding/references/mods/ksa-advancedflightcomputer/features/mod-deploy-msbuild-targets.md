---
type: Mod Feature
title: MSBuild deploy and packaging for a StarMap mod with native payload
description: Shared MSBuild targets that copy a mod into the game's mods folder with a running-game check, a launch profile and a worktree stamp, plus an explicit payload list that drives both deployment and a Release zip verified file by file
mod: ksa-advancedflightcomputer
category: getting-started
techniques: [msbuild-targets]
hooks: []
lifecycle: []
game_types: []
tags: [msbuild, build, deploy, packaging, release, worktree, native-payload]
---

Every build of AFC lands in the game's mods folder ready to run, and every Release build produces a zip
that contains exactly the declared files. The plumbing lives in three files under `build/` that any mod
project can import, and it solves problems every KSA modder hits: the running game locks the deployed
DLL, several git worktrees deploy into one folder, and a release must carry native libraries and license
texts but never game assemblies.

## How it works

### Compile references

The csproj references game assemblies from `$(KsaDir)` (default `..\..\..\refs\game`, overridable with
`-p:KsaDir=`) with `<Private>false</Private>`, so `KSA.dll`, `Brutal.*` and friends are compiled against
but never copied; Harmony and StarMap.API come from NuGet
([AdvancedFlightComputer/AdvancedFlightComputer.csproj](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/AdvancedFlightComputer.csproj#L1-L52)). `Directory.Build.props` turns on
NuGet lock files and declares both runtime identifiers so CI can restore once and build per runtime
([Directory.Build.props](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/Directory.Build.props)); `global.json` pins the .NET SDK band and `NuGet.config` clears inherited
feeds.

### Deploy (`ModDeploy.targets`)

- `CopyToMods` (after `Build`, unless `-p:DeployToMods=false`) copies the assembly to
  `%USERPROFILE%\Documents\My Games\Kitten Space Agency\mods\<ModDeployName>\` with `Retries="0"` and
  `ContinueOnError`. On failure it runs PowerShell once to list `StarMap`/`KSA` processes and fails with
  "The game is running ... holds the deployed DLL open", instead of ten silent retries. It then copies
  `mod.toml`, every `PatchFile` into `Patches\`, and every `ModPayloadFile` to its `TargetPath`
  ([build/ModDeploy.targets](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/ModDeploy.targets#L66-L99)).
- `EnsureLaunchProfile` writes `Properties\launchSettings.json` pointing at `StarMap.exe` if missing (the
  file holds absolute paths and is gitignored, so fresh worktrees otherwise have nothing to run).
- `StampDeploySlot` writes `.deployed-from` with the checkout path, branch and commit, and prints a
  "deploy slot taken over" notice when a different worktree deployed last.

### Payload (`GuidancePayload.targets`, `GuidanceNative.props`)

- One explicit `ModPayloadFile` list: the four numerical assemblies, the native libraries for each runtime
  in `GuidanceNativeRids` (host only for local builds, both for releases), `LICENSE`,
  `THIRD-PARTY-NOTICES.md` and `licenses/*.txt` ([build/GuidancePayload.targets](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/GuidancePayload.targets#L1-L25)).
- `GuidanceNative.props` is the only place that knows the native layout (`build/native/<rid>/`), so a new
  runtime is added there and nowhere else ([build/GuidanceNative.props](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/GuidanceNative.props)).
- `ValidateGuidanceRuntimeFiles` fails a Release or deploying build when a native library is missing, with
  the command that builds it.

### Release zip

`PackageRelease` in the csproj (Release only) assembles `release\AdvancedFlightComputer\` from the DLL,
`mod.toml`, patches and the payload list and zips it ([AdvancedFlightComputer/AdvancedFlightComputer.csproj](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/AdvancedFlightComputer.csproj#L84-L98)).
`VerifyGuidancePackage` unzips the result and diffs it against the expected list in both directions:
a missing file or any undeclared file (such as a game or loader assembly that slipped in) fails the build
([build/GuidancePayload.targets](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/GuidancePayload.targets#L34-L58)).

## Lifecycle and timing

Build time only. The deployed layout is what StarMap and the game read at startup: `mod.toml` in the mod
folder, the entry assembly named by `[StarMap] EntryAssembly`, satellite assemblies resolved by StarMap's
per-mod load context, natives next to `Conic`, XML patches in `Patches\` for KittenExtensions.

## Key code

| Path | Role |
|---|---|
| [build/ModDeploy.targets](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/ModDeploy.targets) | Deploy, launch profile, running-game check, worktree stamp |
| [build/GuidancePayload.targets](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/GuidancePayload.targets) | Payload list, native presence check, package verification |
| [build/GuidanceNative.props](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/GuidanceNative.props) | Native layout per runtime |
| [AdvancedFlightComputer/AdvancedFlightComputer.csproj](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/AdvancedFlightComputer.csproj) | References, `PatchFile` items, `PackageRelease` |
| [Directory.Build.props](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/Directory.Build.props) | Lock files and runtime identifiers |
| [global.json](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/global.json) | SDK pin |

## Snippets

```msbuild
<!-- build/GuidancePayload.targets L36-L56 -->
  <Target Name="VerifyGuidancePackage" AfterTargets="PackageRelease" Condition="'$(Configuration)' == 'Release'">
    <PropertyGroup>
      <GuidanceVerifyDir>$(OutputPath)package-verify/</GuidanceVerifyDir>
      <ZipPath Condition="'$(ZipPath)' == ''">$(OutputPath)$(AssemblyName).zip</ZipPath>
    </PropertyGroup>
    <RemoveDir Directories="$(GuidanceVerifyDir)" />
    <Unzip SourceFiles="$(ZipPath)" DestinationFolder="$(GuidanceVerifyDir)" />
    <ItemGroup>
      <ExpectedPackageFile Include="$(GuidanceVerifyDir)$(AssemblyName)/$(AssemblyName).dll" />
      <ExpectedPackageFile Include="$(GuidanceVerifyDir)$(AssemblyName)/mod.toml" />
      <ExpectedPackageFile Include="@(PatchFile->'$(GuidanceVerifyDir)$(AssemblyName)/Patches/%(Filename)%(Extension)')" />
      <ExpectedPackageFile Include="@(ModPayloadFile->'$(GuidanceVerifyDir)$(AssemblyName)/%(TargetPath)')" />
      <ActualPackageFile Include="$(GuidanceVerifyDir)**/*" />
      <MissingPackageFile Include="@(ExpectedPackageFile->'%(FullPath)')" Exclude="@(ActualPackageFile->'%(FullPath)')" />
      <UnexpectedPackageFile Include="@(ActualPackageFile->'%(FullPath)')" Exclude="@(ExpectedPackageFile->'%(FullPath)')" />
    </ItemGroup>
    <Error Condition="'@(MissingPackageFile)' != ''"
           Text="The release package is missing @(MissingPackageFile->'%(Filename)%(Extension)', ', ')." />
    <Error Condition="'@(UnexpectedPackageFile)' != ''"
           Text="The release package contains undeclared files, @(UnexpectedPackageFile->'%(Filename)%(Extension)', ', ')." />
    <Message Importance="high" Text="Release package verified, @(ExpectedPackageFile->Count()) files." />
```

## Game APIs touched

None. The build references `KSA.dll` and other game assemblies only for compilation, with `Private=false`.

## Pitfalls and version notes

- Never ship game or loader assemblies in a mod zip; verify the archive against an allow-list.
- A running game holds the deployed DLL; fail fast with a clear message instead of retrying.
- The deploy path uses `%USERPROFILE%`, i.e. the Windows layout; the native layout supports Linux builds,
  but the deploy target as written assumes Windows paths.

## See also

- [Native solver build and ABI check](native-solver-build-and-abi-check.md)
- [CI reference assemblies](ci-reference-assemblies.md)
- [Optional KittenExtensions patches](optional-kittenextensions-patches.md)
- [Headless harness test mod](headless-harness-test-mod.md)
- [Share one payload list between the deploy copy and the release zip](../../../howto/getting-started/build/deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-3.md) - how-to article demonstrating this feature
- [Verify the release zip against the declared payload file by file](../../../howto/getting-started/build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-2.md) - how-to article demonstrating this feature
- [Ship a per-RID native payload from the deploy targets and bind it with layout-exact structs](../../../howto/getting-started/dependencies/ship-native-libraries-with-a-mod/ship-native-libraries-with-a-mod-2.md) - how-to article demonstrating this feature

