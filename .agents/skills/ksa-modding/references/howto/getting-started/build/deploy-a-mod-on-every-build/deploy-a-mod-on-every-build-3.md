---
type: How-To
title: Share one payload list between the deploy copy and the release zip
description: Declare every shipped file once as ModPayloadFile items - per-RID natives, satellite assemblies, licences - and let both the dev deploy and the packaged zip read the list, with a running-game check
category: getting-started
group: build
topic: deploy-a-mod-on-every-build
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/mod-deploy-msbuild-targets
  - mods/ksarmory/features/build-deploy-package-release
techniques: [msbuild, native-interop]
hooks: [msbuild AfterTargets=Build]
lifecycle: [startup:starmap-before-main]
game_types: [KSA.Constants]
tags: [msbuild, deploy, payload, release, natives, advancedflightcomputer, ksarmory]
---

A bigger mod ships more than a DLL and a manifest: satellite assemblies, native libraries per runtime, XML patches,
licence texts. If the deploy copy and the release zip each keep their own list, they drift. KSA-AdvancedFlightComputer
declares **one `ModPayloadFile` item list** that both the dev deploy and the release package read, and verifies the
finished zip against it. KSArmory solves the same problem in shell: `deploy.sh` discovers asset folders and refuses to
deploy until the assets check passes, and `package.sh` stages the same folders then gates on stray files. The simple
shapes are [OutputPath straight into the mods folder](./deploy-a-mod-on-every-build-2.md) and [Post-build copy with an overridable game directory](./deploy-a-mod-on-every-build-4.md); the diagnosing target is
[Imported ModDeploy.targets with locked-DLL diagnosis](./deploy-a-mod-on-every-build-1.md).

## Background: how the game does it

Everything StarMap needs sits in the mod folder: `mod.toml`, the entry assembly named by `[StarMap] EntryAssembly`,
satellite assemblies (resolved through the entry assembly's `deps.json` by the per-mod context, see
[Package NuGet dependencies with deps.json](../../dependencies/ship-third-party-dlls-with-a-mod/ship-third-party-dlls-with-a-mod-2.md)), native libraries next to the assembly that imports them
([Per-RID payload from the deploy targets](../../dependencies/ship-native-libraries-with-a-mod/ship-native-libraries-with-a-mod-2.md)) and XML patches for KittenExtensions. KSA's mods folder is
`<Documents>/My Games/Kitten Space Agency/mods`.[current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L287)

## Lifecycle and timing

Build and release time only; the deployed layout is what StarMap and the game read at startup.

## Steps

1. **Declare the payload once**, with the target path inside the mod folder for each item, and one props file that owns
   the native layout:[build/GuidancePayload.targets](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/GuidancePayload.targets#L1-L33)

   ```xml
   <!-- build/GuidancePayload.targets L1-L12 -->
   <Project>
     <Import Project="$(MSBuildThisFileDirectory)GuidanceNative.props" />

     <ItemGroup>
       <ModPayloadFile Include="$(OutputPath)AdvancedFlightComputer.Guidance.Numerics.dll" TargetPath="AdvancedFlightComputer.Guidance.Numerics.dll" />
       <ModPayloadFile Include="$(OutputPath)AdvancedFlightComputer.Guidance.Conic.dll" TargetPath="AdvancedFlightComputer.Guidance.Conic.dll" />
       <ModPayloadFile Include="$(OutputPath)AdvancedFlightComputer.Guidance.Gfold.dll" TargetPath="AdvancedFlightComputer.Guidance.Gfold.dll" />
       <ModPayloadFile Include="$(OutputPath)AdvancedFlightComputer.Guidance.Scvx.dll" TargetPath="AdvancedFlightComputer.Guidance.Scvx.dll" />
     </ItemGroup>
   ```

   Natives per runtime identifier go into the same list (see [Per-RID payload from the deploy targets](../../dependencies/ship-native-libraries-with-a-mod/ship-native-libraries-with-a-mod-2.md)); `LICENSE`,
   `THIRD-PARTY-NOTICES.md` and `licenses/*.txt` ride along.
2. **Make the deploy read the list.** `CopyToMods` copies the assembly, `mod.toml`, every `PatchFile` into `Patches\`
   and every `ModPayloadFile` to its `TargetPath`, with the same single-attempt copy and running-game diagnosis as
   [Imported ModDeploy.targets with locked-DLL diagnosis](./deploy-a-mod-on-every-build-1.md).
3. **Make the release package read the same list:**[AdvancedFlightComputer/AdvancedFlightComputer.csproj](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/AdvancedFlightComputer.csproj#L84-L98)

   ```xml
   <!-- AdvancedFlightComputer/AdvancedFlightComputer.csproj L84-L98 -->
     <Target Name="PackageRelease" AfterTargets="Build" Condition="'$(Configuration)' == 'Release'">
       <PropertyGroup>
         <ReleaseDir>$(OutputPath)release\$(AssemblyName)\</ReleaseDir>
         <ZipPath>$(OutputPath)$(AssemblyName).zip</ZipPath>
       </PropertyGroup>
       <RemoveDir Directories="$(OutputPath)release" />
       <MakeDir Directories="$(ReleaseDir)Patches\" />
       <Copy SourceFiles="$(OutputPath)$(AssemblyName).dll" DestinationFolder="$(ReleaseDir)" />
       <Copy SourceFiles="mod.toml" DestinationFolder="$(ReleaseDir)" />
       <Copy SourceFiles="@(PatchFile)" DestinationFolder="$(ReleaseDir)Patches\" />
       <Copy SourceFiles="@(ModPayloadFile)" DestinationFiles="@(ModPayloadFile->'$(ReleaseDir)%(TargetPath)')" />
       <ZipDirectory SourceDirectory="$(OutputPath)release" DestinationFile="$(ZipPath)" Overwrite="true" />
       <RemoveDir Directories="$(OutputPath)release" />
       <Message Importance="high" Text="Release packaged: $(ZipPath)" />
     </Target>
   ```

   The verification target (unzip, diff against the list in both directions, fail on missing or undeclared files) is
   described in [Payload list verified file by file](../package-and-publish-a-mod-release/package-and-publish-a-mod-release-2.md).
4. **Fail early for missing natives** with a `BeforeTargets="PrepareForBuild"` validation that names the script that
   builds them; a local Debug build with `-p:DeployToMods=false` stays a managed compile check.
5. **The shell variant (KSArmory).** `tools/deploy.sh` locates the user directory (`KSA_MODS_DIR` or per-OS candidates),
   builds, runs `validate-parts.py --offline` before copying (a bad asset id is a silent in-game failure), copies the
   DLL (failing with "KSA is still running" when refused and the game is found), `mod.toml`, `KSArmory*.xml` and every
   subfolder of the build output, writes an empty `developer` marker ([Constants.LogsFolderPath and a developer marker file](../../lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-2.md)) and appends
   `[[mods]] id = "KSArmory"` to `manifest.toml` if missing, keeping a `.bak`. `tools/package.sh` stages the same files
   and gates: no `.pdb`, no `.dll` other than the mod's.[tools/deploy.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/deploy.sh)[tools/package.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/package.sh)

## Complete example

| File | Role |
|---|---|
| [build/GuidancePayload.targets](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/GuidancePayload.targets), [build/GuidanceNative.props](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/GuidanceNative.props) | The payload list and the RID layout |
| [AdvancedFlightComputer/AdvancedFlightComputer.csproj](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/AdvancedFlightComputer.csproj) | `PackageRelease` and the imports |
| [tools/deploy.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/deploy.sh), [tools/package.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/package.sh) | Shell deploy and package with gates |

## Variations and gotchas

- **Release passes both runtimes** (`-p:GuidanceNativeRids=win-x64;linux-x64`); a local build ships only the host
  runtime, so a missing cross build cannot fail an ordinary deployment.
- **The deploy path uses `%USERPROFILE%`**, i.e. the Windows layout; the native layout supports Linux builds but the
  deploy target as written assumes Windows paths.
- **Never ship game or loader assemblies**: the package check is an allow-list.
- **Registration is mandatory.** A folder under `mods/` without a `manifest.toml` entry is added by KSA as a new,
  disabled mod: the player is asked to enable it and StarMap loads no code until the restart it then offers
  ([Folder and manifest rules as StarMap consumes them](../../loader/how-starmap-launches-ksa/how-starmap-launches-ksa-2.md));
  `deploy.sh` appends the enabled entry so the first launch already loads the code.
- **Case matters on Linux**; archives are unzipped, never retyped.

## See also

- [Imported ModDeploy.targets with locked-DLL diagnosis](./deploy-a-mod-on-every-build-1.md), [Post-build copy with an overridable game directory](./deploy-a-mod-on-every-build-4.md), [Deploy a mod on every build and debug it from an IDE](./overview.md)
- [MSBuild deploy and packaging for a StarMap mod with native payload](../../../../mods/ksa-advancedflightcomputer/features/mod-deploy-msbuild-targets.md), [Deploy into the KSA mods folder, package a clean archive and cut releases from commits](../../../../mods/ksarmory/features/build-deploy-package-release.md)
- [Package and publish a mod release (zip, CI publishing, installer)](../package-and-publish-a-mod-release/overview.md), [Ship and load native libraries from a mod folder](../../dependencies/ship-native-libraries-with-a-mod/overview.md)
