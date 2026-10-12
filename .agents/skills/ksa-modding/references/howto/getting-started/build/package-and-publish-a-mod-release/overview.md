---
type: Game Feature
title: Package and publish a mod release (zip, CI publishing, installer)
description: What a mod release must contain, six ways the corpus produces and verifies it - Release targets, payload verification, data and licences, CI publishing, rolling channels and an installer
category: getting-started
group: build
topic: package-and-publish-a-mod-release
demonstrated_by:
  - mods/ksaero/features/mod-deploy-targets
  - mods/ksa-deltavmap/features/mod-deploy-targets-and-release
  - mods/realatmospheres/features/release-packaging-and-deploy
  - mods/ksa-advancedflightcomputer/features/mod-deploy-msbuild-targets
  - mods/ksacars/features/package-deploy-release
  - mods/ksagolf/features/packaging-and-release
  - mods/ohmystars/features/mod-root-and-packaging
  - mods/ksa-measuretools/features/mod-deploy-targets-and-release
  - mods/unscience/features/unscience-build-and-release
  - mods/purrtty/features/cross-platform-build-and-release
  - mods/starmap/features/release-packaging-and-installer
lifecycle: []
game_types: []
tags: [packaging, release, zip, github-actions, spacedock, installer, licenses]
---

A release is the folder a player unzips into `<Documents>/My Games/Kitten Space Agency/mods/` (or `Content/`): the mod id as
the top-level directory, `mod.toml`, the entry DLL and everything it loads. KSA finds mods by folder name under those two
roots and walks the manifest,[current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L234-L371) and StarMap loads the entry
DLL from the same folder; neither validates the archive, so the packaging step is where completeness and legality are
enforced.

## Engine background

- The mod id is the folder name; the top-level directory of the zip must match.
- Game and loader assemblies in a mod folder are never used (the loaded copies win), so shipping them is only
  redistribution; allow-list the payload ([Ship third-party DLLs next to your mod](../../dependencies/ship-third-party-dlls-with-a-mod/overview.md)).
- Native libraries and data files are located relative to the entry DLL ([Ship and load native libraries from a mod folder](../../dependencies/ship-native-libraries-with-a-mod/overview.md),
  [Ship assets and code in one mod folder and find it at runtime](../../loader/ship-content-and-code-in-one-mod/overview.md)).
- A launcher-style tool also needs the config the loader reads on every start
  ([StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L25-L56)).
- All of this is release time; nothing runs in the game frame ([Game loop and mod lifecycle](../../lifecycle/game-loop-and-lifecycle/overview.md) for run-time behavior).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Release target that zips the deployed folder](./package-and-publish-a-mod-release-1.md) | KSAero, KSA-DeltaVMap, RealAtmospheres | `msbuild` | build | Smallest: one target; no check that the zip is complete |
| [Payload list verified file by file](./package-and-publish-a-mod-release-2.md) | KSA-AdvancedFlightComputer, KSACars, KSAGolf | `msbuild` | build | Fails on missing or undeclared files, covers natives and licences; needs the shared payload list |
| [Ship data files and third-party licences](./package-and-publish-a-mod-release-3.md) | OhMyStars | `msbuild` | build | Big data folders and GPL/CC BY-SA notices handled; Windows-only copy, and a committed zip goes stale |
| [Workflow reading the tested game version from Mod.cs](./package-and-publish-a-mod-release-4.md) | KSA-DeltaVMap, KSA-MeasureTools | `github-actions` | release | One constant drives guard, notes and SpaceDock field |
| [Rolling releases from a multi-assembly mod](./package-and-publish-a-mod-release-5.md) | unscience, purrTTY | `github-actions` | release | Branch channels, stamped version, validated dist, retention; compiles against the newest assemblies |
| [Release RC builds, NuGet and a Windows installer](./package-and-publish-a-mod-release-6.md) | StarMap | `github-actions` | release | The model for launcher-style tools; several unverified CI quirks |

## Release checklist

1. Folder named for the mod id; `mod.toml`, entry DLL, `deps.json` if you have private dependencies.
2. No game, Harmony or `StarMap.API` assemblies; no `.pdb` unless intended.
3. Content, natives and licences present; verify the archive, not the build output.
4. Tested game build recorded and logged ([Warn when the running game build differs from the one a mod was tested on](../../lifecycle/check-the-game-version/overview.md)).
5. Tell users to delete the old folder before unzipping.

## See also

- [Deploy a mod on every build and debug it from an IDE](../deploy-a-mod-on-every-build/overview.md), [Generate mod.toml and StarMap dependencies from the project file](../generate-mod-toml-from-msbuild/overview.md), [Build a mod in CI when the game DLLs are private](../build-in-ci-without-the-game-installed/overview.md),
  [Reference the game's assemblies from a mod project without shipping them](../reference-game-assemblies-from-a-mod-project/overview.md)
- [Build and deploy targets](../../../../mods/ksaero/features/mod-deploy-targets.md), [Shared MSBuild deploy targets, release zip and SpaceDock publishing](../../../../mods/ksa-deltavmap/features/mod-deploy-targets-and-release.md),
  [Release packaging and deploy scripts](../../../../mods/realatmospheres/features/release-packaging-and-deploy.md), [MSBuild deploy and packaging for a StarMap mod with native payload](../../../../mods/ksa-advancedflightcomputer/features/mod-deploy-msbuild-targets.md),
  [Build, deploy, package and release pipeline](../../../../mods/ksacars/features/package-deploy-release.md), [Packaging, deploy and release pipeline](../../../../mods/ksagolf/features/packaging-and-release.md),
  [Mod root and packaging](../../../../mods/ohmystars/features/mod-root-and-packaging.md), [Shared MSBuild deploy targets, release zip and SpaceDock publishing](../../../../mods/ksa-measuretools/features/mod-deploy-targets-and-release.md),
  [One shipped mod folder from thirty-plus projects, with dist validation and rolling GitHub releases](../../../../mods/unscience/features/unscience-build-and-release.md), [One-zip cross-platform build, deploy and release pipeline](../../../../mods/purrtty/features/cross-platform-build-and-release.md),
  [Release packaging, StarMap.API NuGet and Windows installer](../../../../mods/starmap/features/release-packaging-and-installer.md)
