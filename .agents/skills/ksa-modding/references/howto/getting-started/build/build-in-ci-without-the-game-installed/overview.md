---
type: Game Feature
title: Build a mod in CI when the game DLLs are private
description: How to compile and test mods on public CI without redistributing KSA - a private DLL repository with an App token, stripped reference assemblies per build, or a compile-gated game seam
category: getting-started
group: build
topic: build-in-ci-without-the-game-installed
demonstrated_by:
  - mods/kittenextensions/features/msbuild-mod-toml-generation
  - mods/mpfx/features/ci-release-pipeline
  - mods/shaderextensions/features/ci-build-and-release
  - mods/ksa-advancedflightcomputer/features/ci-reference-assemblies
  - mods/gatos/features/compile-gated-game-seam
  - mods/gatos/features/build-deploy-and-ci
lifecycle: []
game_types: []
tags: [ci, github-actions, private-assemblies, reference-assemblies, compile-gating]
---

KSA's managed assemblies are copyrighted files that must not be committed, uploaded as artifacts or cached in a public
repository, yet a mod's CI has to compile against them. The corpus has three answers, from "always fetch the game to
compile" to "need it as little as possible". All three keep the files out of the build output
(`Private=false`, [Reference the game's assemblies from a mod project without shipping them](../reference-game-assemblies-from-a-mod-project/overview.md)) and out of the release zip
([Payload list verified file by file](../package-and-publish-a-mod-release/package-and-publish-a-mod-release-2.md)).

## Engine background

No game code runs in CI. The one engine fact a pipeline uses is the version: `VersionInfo.Current.VersionString` at run
time ([current/decomp/KSA/VersionInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionInfo.cs#L124-L145)) is compared with a tested-version constant that CI
also reads ([Runtime VersionInfo compare at AllModsLoaded](../../lifecycle/check-the-game-version/check-the-game-version-1.md)). All approaches are build and release time; there is no lifecycle value
([Game loop and mod lifecycle](../../lifecycle/game-loop-and-lifecycle/overview.md) for run-time behavior).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Private DLL repository fetched with a GitHub App token](./build-in-ci-without-the-game-installed-1.md) | KittenExtensions, MPFX, ShaderExtensions | `github-actions` | build, release | Full-fidelity compile against real DLLs; secrets and an App to maintain; forks cannot build |
| [Refasmer reference assemblies stored per game build](./build-in-ci-without-the-game-installed-2.md) | KSA-AdvancedFlightComputer | `github-actions` | build, release | API-only references pinned to a build named in code; reflection targets are not checked by compiling |
| [Compile-gated game seam](./build-in-ci-without-the-game-installed-3.md) | gatOS | `msbuild` | build, test | Most code builds and tests with no game at all; the game-coupled half is only compiled when the assemblies are available |

## Choosing

- Small mod, want real compile checks: approach 1.
- Mod with reflection-heavy patches and a strict release process: approach 2 (add a headless harness for the reflection
  half).
- Large mod with a testable core: approach 3, optionally combined with 1 or 2 for the glue.

## See also

- [Build a KSA mod that references only StarMap.API and Harmony](../build-a-mod-without-game-references/overview.md), [Generate mod.toml and StarMap dependencies from the project file](../generate-mod-toml-from-msbuild/overview.md), [Package and publish a mod release (zip, CI publishing, installer)](../package-and-publish-a-mod-release/overview.md),
  [Warn when the running game build differs from the one a mod was tested on](../../lifecycle/check-the-game-version/overview.md)
- [MSBuild-generated mod.toml and deploy](../../../../mods/kittenextensions/features/msbuild-mod-toml-generation.md), [Generated mod.toml and CI release pipeline](../../../../mods/mpfx/features/ci-release-pipeline.md),
  [CI build against private game DLLs with GitHub and SpaceDock releases](../../../../mods/shaderextensions/features/ci-build-and-release.md), [Build a KSA mod in CI from private reference assemblies](../../../../mods/ksa-advancedflightcomputer/features/ci-reference-assemblies.md),
  [Compile-gated game seam](../../../../mods/gatos/features/compile-gated-game-seam.md), [Build, deploy and CI](../../../../mods/gatos/features/build-deploy-and-ci.md)
