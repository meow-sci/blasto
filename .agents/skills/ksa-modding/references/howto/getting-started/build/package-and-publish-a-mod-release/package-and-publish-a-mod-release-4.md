---
type: How-To
title: Publish from CI with a workflow that reads the tested game version from Mod.cs
description: Make the TestedGameVersion constant the single source for the code guard, the release notes and the SpaceDock game-version field by extracting it with grep in the release workflow
category: getting-started
group: build
topic: package-and-publish-a-mod-release
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksa-deltavmap/features/mod-deploy-targets-and-release
  - mods/ksa-measuretools/features/mod-deploy-targets-and-release
techniques: [github-actions]
hooks: [github-actions release published]
lifecycle: [startup:all-mods-loaded]
game_types: [KSA.VersionInfo]
tags: [github-actions, spacedock, release, tested-version, ci, publishing]
---

KSA-MeasureTools and KSA-DeltaVMap publish a GitHub release to SpaceDock from CI. The SpaceDock listing needs a
"compatible game version". Instead of a repository variable that someone must remember to update, the workflow **greps
the `TestedGameVersion` constant out of `Mod.cs`**, so the code's runtime guard ([Runtime VersionInfo compare at AllModsLoaded](../../lifecycle/check-the-game-version/check-the-game-version-1.md)), the
release and the listing cannot disagree. This article is the publish side; the zip itself is
[Release target that zips the deployed folder](./package-and-publish-a-mod-release-1.md), and the private-DLL build is [Private DLL repository fetched with a GitHub App token](../build-in-ci-without-the-game-installed/build-in-ci-without-the-game-installed-1.md).

## Background: how the game does it

The game plays no part at publish time. The constant it feeds is compared at startup with `VersionInfo.Current.VersionString`
(`v{Major}.{Minor}.{Build}.{Revision}`), which always carries a leading `v`; the workflow strips it for SpaceDock.[current/decomp/KSA/VersionInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionInfo.cs#L124-L145)

## Lifecycle and timing

Release time: the workflow runs on `release: published`. The runtime effect is the `[StarMapAllModsLoaded]` warning when
the running build differs.

## Steps

1. **Put the constant where scripts can find it:** a `private const string TestedGameVersion = "v2026.10.10.5554";` in
   `Mod.cs`, one per repo.[MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs)
2. **Extract it in the workflow** with a regex that finds the first `Mod.cs` defining it, strips everything except the
   quoted value and the leading `v`:[.github/workflows/spacedock-publish.yml](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/.github/workflows/spacedock-publish.yml#L80-L97)

   ```bash
   # .github/workflows/spacedock-publish.yml L84-L94
             # Find the first Mod.cs that defines TestedGameVersion. The
             # project folder is normally <ModName>/Mod.cs from repo root.
             # ... grep -rEh --include='Mod.cs' for the `private const string TestedGameVersion = "` line ...
             if [ -z "$line" ]; then
               echo "::error::Could not find TestedGameVersion in any Mod.cs"
               exit 1
             fi
             # Strip everything except the quoted value, then strip leading 'v'
             # ... sed extracts the quoted value into $version ...
   ```

3. **Send it as the SpaceDock game version.** The release tag (`v0.3.2`) is uploaded as version `0.3.2` (leading `v`
   stripped); the compatible KSA build is the extracted value; the release body becomes the changelog; the release must
   contain exactly one `.zip` asset:[.github/workflows/spacedock-publish.yml](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/.github/workflows/spacedock-publish.yml#L13-L19)

   ```yaml
   # .github/workflows/spacedock-publish.yml L13-L19
   # The release tag (e.g. v0.3.2) is uploaded as version 0.3.2 (the leading
   # 'v' is stripped). The compatible KSA build is read from the
   # `TestedGameVersion` constant in any Mod.cs in the repo, so bumping the
   # game version in code is enough; no repo variable to maintain.
   # Each release must contain exactly one .zip asset. The release body is
   # sent as the SpaceDock changelog.
   ```

4. **Trigger on `release: published`** so the same zip a user downloads from GitHub is what SpaceDock receives; the zip is
   built and attached earlier (approach 1 or the workflow of [Rolling releases from a multi-assembly mod](./package-and-publish-a-mod-release-5.md)).
5. **Alternative game-version source.** KittenExtensions' workflow reads a `KSA_VERSION.txt` from the private DLL
   repository instead, because it compiles against whatever the repository holds; its release body says "Built for KSA
   version ..." and it posts `game-version` to SpaceDock the same way.[.github/workflows/build.yml](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/.github/workflows/build.yml#L206-L215)
6. **Keep secrets minimal:** SpaceDock login credentials as repository secrets; MPFX's workflow does the same login and
   update calls.

## Complete example

[.github/workflows/spacedock-publish.yml](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/.github/workflows/spacedock-publish.yml) (publish workflow) and [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs) (the constant and the runtime check). KSA-DeltaVMap uses the same
shape. For a headline-free alternative that ties the version to the lock file, see
[Stamp the targeted build into assembly metadata](../../lifecycle/check-the-game-version/check-the-game-version-2.md).

## Variations and gotchas

- **Exact-match warning on every update** is intended (see [Runtime VersionInfo compare at AllModsLoaded](../../lifecycle/check-the-game-version/check-the-game-version-1.md)); bumping the constant after
  re-testing bumps the SpaceDock field in the next release.
- **The grep takes the first match** in any `Mod.cs`, so keep one definition per repo.
- **The constant must stay a plain string literal** on one line for the regex to work.
- **Fork builds** cannot publish; they have no secrets.

## See also

- [Release target that zips the deployed folder](./package-and-publish-a-mod-release-1.md), [Rolling releases from a multi-assembly mod](./package-and-publish-a-mod-release-5.md), [Package and publish a mod release (zip, CI publishing, installer)](./overview.md)
- [Shared MSBuild deploy targets, release zip and SpaceDock publishing](../../../../mods/ksa-deltavmap/features/mod-deploy-targets-and-release.md), [Shared MSBuild deploy targets, release zip and SpaceDock publishing](../../../../mods/ksa-measuretools/features/mod-deploy-targets-and-release.md)
- [Warn when the running game build differs from the one a mod was tested on](../../lifecycle/check-the-game-version/overview.md), [Build a mod in CI when the game DLLs are private](../build-in-ci-without-the-game-installed/overview.md)
