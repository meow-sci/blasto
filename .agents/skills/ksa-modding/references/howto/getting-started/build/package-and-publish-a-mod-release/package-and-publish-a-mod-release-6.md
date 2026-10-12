---
type: How-To
title: Release RC builds, a NuGet API package and a Windows installer for a launcher-style tool
description: StarMap's own release pipeline - RC prereleases from dev, label-driven versions on main, a framework-dependent win-x64 zip without pdb and xml, StarMap.API pushed to NuGet when changed, and an Inno Setup installer that writes StarMapConfig.json
category: getting-started
group: build
topic: package-and-publish-a-mod-release
approach: 6
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/starmap/features/release-packaging-and-installer]
techniques: [github-actions, msbuild]
hooks: [github-actions pull_request closed]
lifecycle: []
game_types: []
tags: [github-actions, nuget, installer, inno-setup, launcher, starmap, release]
---

Most mods are a folder of files. StarMap is a *tool*: an executable with a project graph, a NuGet package other mods compile
against, and an installer. Its pipeline shows the other end of release engineering: RC builds from `dev`, a labelled
release from `main`, a framework-dependent zip without symbols or XML docs, `StarMap.API` pushed to NuGet when its files
change, and an Inno Setup installer that asks for the KSA folder and writes the config. Use it as a model if your mod is a
launcher-style tool or ships a library other mods reference. For ordinary mods see
[Release target that zips the deployed folder](./package-and-publish-a-mod-release-1.md) to [Rolling releases from a multi-assembly mod](./package-and-publish-a-mod-release-5.md).

## Background: how the game does it

`StarMap.exe` requires `0Harmony.dll` and `StarMap.Core.dll` next to it (it loads them by `./` paths) and reads
`./StarMapConfig.json`, whose `GameLocation` must point at the KSA folder or `KSA.dll`.[StarMap/LoaderConfig.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/LoaderConfig.cs#L7-L51) The
release therefore has to contain the launcher's whole project graph and a config the player does not have to type: the
installer writes it. No KSA or Tomlet assembly ships, because both are referenced with runtime assets excluded.
Being framework-dependent, the folder needs a matching .NET 10 runtime on the machine, and KSA runs on that runtime
because it is hosted in StarMap's process (inferred from the publish flags and the hosting model;
[Host process, contexts and hook binding walkthrough](../../loader/how-starmap-launches-ksa/how-starmap-launches-ksa-1.md)).

## Lifecycle and timing

Build and release time only; the single run-time consequence is the `StarMapConfig.json` the installer writes, which
`LoaderConfig` reads on every launch.

## Steps

1. **Pick channels.** `rc-build.yml` on a push to `dev`: version = latest semver tag with patch + 1, RC id
   `X.Y.Z-rc.<run>+<sha7>`, force-moves the `rc` tag, publishes win-x64, pushes the RC `StarMap.API` package to GitHub
   Packages, attaches `StarMap-RC.zip` and `version.txt`. `release.yml` on a PR closed into `main`: bumps by `major` /
   `minor` label (else patch), tags, publishes, attaches `StarMap-<ver>.zip`, and builds an installer. `pr-version.yml`
   comments the next version on each PR; `redirect-external-pr.yml` retargets fork PRs to `feature/external/pr-<n>-<branch>`.
2. **Publish a framework-dependent win-x64 build, stamping every version property:**[.github/workflows/release.yml](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/.github/workflows/release.yml#L84-L102)

   ```yaml
   # .github/workflows/release.yml L86-L96
                 - name: Build standalone
                   run: |
                       dotnet publish ${{ env.PROJECT }} \
                         -c Release \
                         -o ${{ env.OUTPUT_PATH }} \
                         -r win-x64 \
                         --self-contained false \
                         /p:PackageVersion=${{ steps.version.outputs.new_version }} \
                         /p:Version=${{ steps.version.outputs.new_version }} \
                         /p:AssemblyVersion=${{ steps.version.outputs.new_version }} \
                         /p:FileVersion=${{ steps.version.outputs.new_version }}
   ```

   The zip is made from the publish folder excluding `*.pdb` and `*.xml`.
3. **Push the API package to NuGet only when its files changed** since the previous tag
   (`StarMap.API` is what mods compile against; its C# surface has not changed since 0.3.6, which is why mods still
   reference that package version).
4. **Build the installer** with Inno Setup from the publish folder:[.github/workflows/release.yml](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/.github/workflows/release.yml#L230-L236)

   ```yaml
   # .github/workflows/release.yml L235
                     ISCC.exe installer\WindowsInstaller.iss /dAppVersion=${{ needs.build.outputs.new_version }} /dOutputName=StarMap-${{ needs.build.outputs.new_version }}
   ```

5. **Make the installer write the config.** It installs to `{pf}\StarMap`, adds a wizard page "KSA Install Location (folder
   with KSA.dll)" and, after install, writes `StarMapConfig.json` if none exists:[installer/WindowsInstaller.iss](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/installer/WindowsInstaller.iss#L40-L75)

   ```pascal
   // installer/WindowsInstaller.iss L69-L73
           EscapedDir := SelectedInstallDir; 
           StringChangeEx(EscapedDir, '\', '\\', True); 
           
           JSONText := '{' + '"GameLocation": "' + EscapedDir + '",' + '"RepositoryLocation": ""' + '}'; 
           SaveOK := SaveStringToFile(ConfigFilePath, JSONText, False);
   ```

   Uninstall deletes `StarMapConfig.json`.
6. **Document the Linux alternative.** Releases are `win-x64` only; the launcher has no Windows-specific code path, and
   other projects tell Linux users to run `dotnet StarMap.dll` (KSAGolf's README).

## Complete example

[.github/workflows/release.yml](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/.github/workflows/release.yml) (publish, NuGet, installer jobs), [installer/WindowsInstaller.iss](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/installer/WindowsInstaller.iss) (wizard and config writer), [StarMap/LoaderConfig.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/LoaderConfig.cs)
(what the installer feeds).

## Variations and gotchas

- **Building from source needs the org feed.** Release and SourceNuget configurations restore `RocketWerkz.KSA*` packages
  through an authenticated GitHub Packages source; without it use Debug with an `Import` folder holding `KSA.dll` and
  `Brutal.*.dll`.
- **CI observations, not verified by running:** `pr-build.yml` sets `DOTNET_VERSION: 9.0` while the projects target
  `net10.0`; `release.yml` triggers on every closed PR into `main` without checking it was merged; its NuGet job
  condition compares the string output `api_changed` with the boolean `true`, which may never be true.
- **The installer defaults to Program Files**, but `StarMap.exe` writes `./StarMapConfig.json` only when missing and the
  installer already created it, so normal launches need no write access there.
- **win-x64 only**; macOS and Linux users build from source.

## See also

- [Rolling releases from a multi-assembly mod](./package-and-publish-a-mod-release-5.md), [Package and publish a mod release (zip, CI publishing, installer)](./overview.md)
- [Release packaging, StarMap.API NuGet and Windows installer](../../../../mods/starmap/features/release-packaging-and-installer.md), [Host KSA in-process from a launcher executable](../../../../mods/starmap/features/host-process-launch.md)
- [How StarMap launches KSA and loads your mod](../../loader/how-starmap-launches-ksa/overview.md), [Deploy a mod on every build and debug it from an IDE](../deploy-a-mod-on-every-build/overview.md)
