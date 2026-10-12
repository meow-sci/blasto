---
type: How-To
title: Publish rolling releases from a multi-assembly mod with a stamped version and a validated folder
description: Stamp a version into mod.toml in CI, build the whole solution, validate the single-mod dist folder with a script, zip it and publish a tip or release channel with retention - for unscience and purrTTY
category: getting-started
group: build
topic: package-and-publish-a-mod-release
approach: 5
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/unscience/features/unscience-build-and-release
  - mods/purrtty/features/cross-platform-build-and-release
techniques: [github-actions, msbuild]
hooks: [github-actions push to main]
lifecycle: []
game_types: []
tags: [github-actions, release, rolling, version-stamp, retention, unscience, purrtty]
---

unscience (31 feature libraries, one shipped mod) and purrTTY (three OS builds of a native core) release continuously
instead of by hand. Their workflows share a shape: derive a version and channel from the branch, stamp it into
`mod.toml` with `sed`, build everything, **validate** the produced folder with a script, zip the one mod folder and
publish it, then prune old releases per channel. This article is that pipeline. The zip and its verification are
[Release target that zips the deployed folder](./package-and-publish-a-mod-release-1.md) and [Payload list verified file by file](./package-and-publish-a-mod-release-2.md); the private-assembly access it
relies on is [Private DLL repository fetched with a GitHub App token](../build-in-ci-without-the-game-installed/build-in-ci-without-the-game-installed-1.md).

## Background: how the game does it

The release content is what StarMap and KSA read: a folder whose `mod.toml` has `[StarMap] EntryAssembly`, the entry DLL
and its `deps.json`, and private dependencies ([Package NuGet dependencies with deps.json](../../dependencies/ship-third-party-dlls-with-a-mod/ship-third-party-dlls-with-a-mod-2.md)). KSA ignores `version` in
`mod.toml`, so stamping it with `sed` is safe and gives users and tools a readable version. Players are told to delete the
old folder first, because unzipping over it leaves removed files behind.

## Lifecycle and timing

Release time. Channels in unscience: push to `main` publishes `1.<run number>.0` (keep the newest 10), push to
`feature/*` publishes a `-beta` prerelease (all beta branches share the newest 5), any other ref builds only.

## Steps

1. **Compute version, tag and channel from the branch**, validate them against a strict pattern, and pass them through
   `env:` rather than interpolating into scripts, because the job has write permission.
2. **Stamp the version into `mod.toml`:**[.github/workflows/release.yml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/.github/workflows/release.yml#L80-L88)

   ```yaml
   # .github/workflows/release.yml L82-L88
         - name: Stamp mod version
           if: steps.meta.outputs.publish == 'true'
           env:
             MODVERSION: ${{ steps.meta.outputs.modversion }}
           run: |
             sed -i -E "s/^version = \".*\"/version = \"$MODVERSION\"/" unscience/mod.toml
             grep '^version' unscience/mod.toml
   ```

3. **Build the whole solution, deploying only the shipped mod** (the deploy target lives on exactly one project, see
   [Package NuGet dependencies with deps.json](../../dependencies/ship-third-party-dlls-with-a-mod/ship-third-party-dlls-with-a-mod-2.md)), then **validate the dist**:[.github/workflows/release.yml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/.github/workflows/release.yml#L90-L96)

   ```yaml
   # .github/workflows/release.yml L90-L96
         # Compile every project; only Unscience deploys under $UNSCIENCE_DIST_DIR.
         - name: Build solution
           run: dotnet build ksa-mod-experiments.slnx -c Release --nologo -v quiet

         - name: Validate single-mod distribution
           run: python3 scripts/check-dist.py "$UNSCIENCE_DIST_DIR"
   ```

   The script asserts the dist holds only `unscience/`, one `mod.toml` naming the right `EntryAssembly`, the runtime
   files, a `MeowSci.*.dll` set equal to the transitive `ProjectReference` closure, and no `KSA.dll`.[scripts/check-dist.py](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scripts/check-dist.py)
4. **Zip the one folder and list it:**[.github/workflows/release.yml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/.github/workflows/release.yml#L98-L106)

   ```yaml
   # .github/workflows/release.yml L97-L105
         - name: Package
           if: steps.meta.outputs.publish == 'true'
           env:
             VERSION: ${{ steps.meta.outputs.version }}
           run: |
             cd "$UNSCIENCE_DIST_DIR"
             test -f unscience/MeowSci.Unscience.dll
             zip -r "unscience-$VERSION.zip" unscience
             unzip -l "unscience-$VERSION.zip"
   ```

5. **Create the release unless the tag already exists** (reruns keep their release), then run a retention job that
   deletes older releases per channel; serialize cleanup per channel so beta branches share one pool. purrTTY cancels
   superseded runs except on `release/*` refs, where a cancel between delete and create would leave the release
   missing.
6. **Multi-OS native payload (purrTTY).** The vendored binding copies all three native libraries on every build, so a
   build on any OS produces the same dist; `test` runs on `ubuntu-latest`, `windows-latest` and `macos-14`, and the
   release job builds once and zips `purrTTY/` ([Flat per-OS file names with a libc mapping](../../dependencies/ship-native-libraries-with-a-mod/ship-native-libraries-with-a-mod-1.md)). Its channels are a `tip-<UTC
   stamp>` prerelease from `main` and `v<v>` from `release/<v>`.
7. **Tell users how to update:** delete the old mod folder first; release notes say so.

## Complete example

| File | Role |
|---|---|
| [.github/workflows/release.yml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/.github/workflows/release.yml) | Channels, version stamp, build, validate, package, publish, retention |
| [scripts/check-dist.py](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scripts/check-dist.py) | Dist validation |
| [unscience/unscience.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/unscience.csproj) | The single deploy target ([Package NuGet dependencies with deps.json](../../dependencies/ship-third-party-dlls-with-a-mod/ship-third-party-dlls-with-a-mod-2.md)) |

## Variations and gotchas

- **CI compiles against the newest assemblies.** unscience checks out `ksa-game-assemblies` at `main`, so a release always
  builds against whatever was last pushed there, not a pinned build ([Refasmer reference assemblies stored per game build](../build-in-ci-without-the-game-installed/build-in-ci-without-the-game-installed-2.md) pins).
- **`TreatWarningsAsErrors`** is on repo-wide, so a game update that only adds obsolete warnings breaks the build.
- **Version policy is fixed**: major 1, patch 0, minor = run number; it signals recency, not compatibility.
- **The README states its tested build** (2026.10.7.5541 with the last full audit at 2026.9.22.5482); the bundle's
  engine claims were checked against 2026.10.10.5554.
- **Untested merges:** forks and `fix/*`/`chore/*` refs build but never release.

## See also

- [Payload list verified file by file](./package-and-publish-a-mod-release-2.md), [Release RC builds, NuGet and a Windows installer](./package-and-publish-a-mod-release-6.md), [Package and publish a mod release (zip, CI publishing, installer)](./overview.md)
- [One shipped mod folder from thirty-plus projects, with dist validation and rolling GitHub releases](../../../../mods/unscience/features/unscience-build-and-release.md), [One-zip cross-platform build, deploy and release pipeline](../../../../mods/purrtty/features/cross-platform-build-and-release.md)
- [Ship third-party DLLs next to your mod](../../dependencies/ship-third-party-dlls-with-a-mod/overview.md), [Build a mod in CI when the game DLLs are private](../build-in-ci-without-the-game-installed/overview.md)
