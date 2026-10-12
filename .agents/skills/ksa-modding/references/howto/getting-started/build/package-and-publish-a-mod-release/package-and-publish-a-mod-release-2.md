---
type: How-To
title: Verify the release zip against the declared payload file by file
description: Unzip the finished archive and diff it against the payload list in both directions, so a missing file or an undeclared game or loader assembly fails the build or the package script
category: getting-started
group: build
topic: package-and-publish-a-mod-release
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/mod-deploy-msbuild-targets
  - mods/ksacars/features/package-deploy-release
  - mods/ksagolf/features/packaging-and-release
techniques: [msbuild]
hooks: [msbuild AfterTargets=PackageRelease]
lifecycle: []
game_types: []
tags: [packaging, verification, payload, allow-list, release, msbuild, shell]
---

A zip that "usually works" fails in the worst way: a missing mesh breaks a part on the player's machine, or a game DLL
leaks into the archive. The fix is to **check the archive, not the build output**. KSA-AdvancedFlightComputer unzips the
finished package and diffs it against its `ModPayloadFile` list in both directions (missing and undeclared files fail
the build); KSAGolf and KSACars do the shell equivalent with gates for stray `.pdb` and `.dll` files. Build the payload
list once and share it with the deploy ([Explicit payload list shared by deploy and packaging](../deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-3.md)); the plain zip target is
[Release target that zips the deployed folder](./package-and-publish-a-mod-release-1.md).

## Background: how the game does it

Anything loadable that is not part of the mod is a bug waiting to happen: a copy of `KSA.dll` or a Brutal assembly in the
folder is ignored at run time (the game's loaded copy wins), but is still a redistribution of game files; a missing XML
listed in `mod.toml` is skipped silently ([Flat folder with an explicit assets list](../../loader/ship-content-and-code-in-one-mod/ship-content-and-code-in-one-mod-1.md)); a missing native library surfaces as a
`DllNotFoundException` at the first P/Invoke ([Ship and load native libraries from a mod folder](../../dependencies/ship-native-libraries-with-a-mod/overview.md)). The archive is the unit users get,
so it is the unit to verify.

## Lifecycle and timing

Build or package time only.

## Steps

1. **Package from one list** (AFC's `PackageRelease` copies the DLL, `mod.toml`, patches and every `ModPayloadFile` into
   `release\<name>\` and zips it, see [Explicit payload list shared by deploy and packaging](../deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-3.md)).
2. **Unzip the result and compute two sets:**[build/GuidancePayload.targets](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/GuidancePayload.targets#L34-L58)

   ```xml
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

3. **Fail on anything the diff finds**: a missing file or any undeclared file (such as a game or loader assembly that
   slipped in) fails the build, with the names in the message.
4. **Shell variant (KSAGolf `tools/package.sh`).** After staging the DLL, `mod.toml`, XML and every asset folder (discovered,
   not hand-listed, so the archive never lacks a file the XML references), refuse debug symbols and foreign DLLs, then
   zip with Python (no `zip(1)` dependency):[tools/package.sh](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/package.sh#L66-L87)

   ```bash
   # tools/package.sh L65-L78
   # Refuse to ship debug symbols even if the build somehow produced them.
   if compgen -G "$STAGE/*.pdb" >/dev/null || compgen -G "$BUILD/*.pdb" >/dev/null; then
       echo "error: a .pdb reached the release build -- check DebugType in the csproj" >&2
       exit 1
   fi

   # Game assemblies are referenced with Private=false and must never be redistributed. This is
   # the last gate before an archive leaves the machine.
   for stray in "$STAGE"/*.dll; do
       case "$(basename "$stray")" in
           KSAGolf.dll) ;;
           *) echo "error: $stray is not ours and must not ship" >&2; exit 1 ;;
       esac
   done
   ```

5. **Run the check in CI on the release build** so the uploaded artifact is the verified one
   ([Refasmer reference assemblies stored per game build](../build-in-ci-without-the-game-installed/build-in-ci-without-the-game-installed-2.md)).

## Complete example

| File | Role |
|---|---|
| [build/GuidancePayload.targets](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/GuidancePayload.targets) | Payload list and `VerifyGuidancePackage` |
| [AdvancedFlightComputer/AdvancedFlightComputer.csproj](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/AdvancedFlightComputer.csproj) | `PackageRelease` |
| [tools/package.sh](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/package.sh), [tools/deploy.sh](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/deploy.sh) | Shell packaging with gates, and the matching deploy |

## Variations and gotchas

- **Allow-list versus deny-list.** AFC and KSAGolf use allow-lists (anything not declared fails); gatOS copies a glob with
  a deny-list so new NuGet dependencies ship automatically ([Post-build copy with an overridable game directory](../deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-4.md)). Pick consciously.
- **Both directions matter.** "Missing" catches forgotten content; "unexpected" catches leaked assemblies.
- **Natives are validated before packaging** (`ValidateGuidanceRuntimeFiles` fails a Release without the libraries).
- **A hand-committed zip goes stale** (Kitten-Engineer-Redux's `dist` zip carries an older build than the source);
  generate the archive, never commit it.
- **Validate content lists too:** KSAGolf's `validate-parts.py` runs before deploy and package.

## See also

- [Release target that zips the deployed folder](./package-and-publish-a-mod-release-1.md), [Ship data files and third-party licences with the mod](./package-and-publish-a-mod-release-3.md), [Package and publish a mod release (zip, CI publishing, installer)](./overview.md)
- [MSBuild deploy and packaging for a StarMap mod with native payload](../../../../mods/ksa-advancedflightcomputer/features/mod-deploy-msbuild-targets.md), [Build, deploy, package and release pipeline](../../../../mods/ksacars/features/package-deploy-release.md),
  [Packaging, deploy and release pipeline](../../../../mods/ksagolf/features/packaging-and-release.md)
- [Deploy a mod on every build and debug it from an IDE](../deploy-a-mod-on-every-build/overview.md), [Ship and load native libraries from a mod folder](../../dependencies/ship-native-libraries-with-a-mod/overview.md)
