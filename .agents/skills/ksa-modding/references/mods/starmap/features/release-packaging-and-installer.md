---
type: Mod Feature
title: Release packaging, StarMap.API NuGet and Windows installer
description: GitHub Actions publish StarMap as a framework-dependent win-x64 folder zipped without pdb and xml files, push StarMap.API to NuGet, cut release-candidate builds from dev, and build an Inno Setup installer that writes StarMapConfig.json
mod: starmap
category: testing-and-tooling
techniques: [github-actions, nuget-package, inno-setup, json-config]
hooks: []
lifecycle: []
game_types: []
tags: [starmap, ci, github-actions, nuget, installer, inno-setup, release]
---

StarMap ships two things: the loader itself (a zip and a Windows installer on GitHub Releases) and
the `StarMap.API` NuGet package that mods compile against. The workflows show what a release
contains, which KSA build it was compiled against, and how a player's `StarMapConfig.json` is
created.

## How it works

### Build inputs

| Project | Output | Notes |
|---|---|---|
| `StarMap/StarMap.csproj` | `StarMap.exe` | `net10.0` console app; references `Lib.Harmony` 2.4.2, `StarMap.Core`, `StarMap.Types`[^sm-csproj] |
| `StarMap.Core/StarMap.Core.csproj` | `StarMap.Core.dll` | Harmony 2.4.2, Tomlet 6.1.0 compile-only; KSA from `..\..\Import\KSA.dll` (Debug), `RocketWerkz.KSA` (SourceNuget) or `RocketWerkz.KSA.Ref` 2026.9.10.5438 (Release), all with runtime assets excluded[^sm-core-csproj] |
| `StarMap.API/StarMap.API.csproj` | `StarMap.API.dll` + NuGet package | XML docs on, README packed; same KSA reference scheme, `PrivateAssets=all`[^sm-api-csproj] |
| `StarMap.Types/StarMap.Types.csproj` | `StarMap.Types.dll` | `IStarMapCore` only |

### Workflows

| Workflow | Trigger | What it does |
|---|---|---|
| `pr-build.yml` | PR to `main` or `dev` from the same repo | Adds the org GitHub Packages feed, restores and builds `StarMap.csproj` in Release[^sm-prbuild] |
| `pr-version.yml` | PR to `main` | Comments the next version: latest tag, bumped by `major` / `minor` label, else patch[^sm-prversion] |
| `redirect-external-pr.yml` | Fork PR to `main` or `dev` (`pull_request_target`) | Creates `feature/external/pr-<n>-<branch>` from `dev` and retargets the PR to it[^sm-redirect] |
| `rc-build.yml` | Push to `dev` | Version = latest semver tag with patch + 1, RC id `X.Y.Z-rc.<run>+<sha7>`; force-moves the `rc` tag; publishes win-x64; pushes the RC `StarMap.API` package to GitHub Packages; attaches `StarMap-RC.zip` and `version.txt` to the `rc` prerelease[^sm-rc] |
| `release.yml` | PR closed into `main` | Bumps by label, tags and pushes; publishes win-x64; pushes `StarMap.API` to nuget.org when files under `StarMap.API/` changed since the previous tag; attaches `StarMap-<ver>.zip`; builds `StarMap-<ver>.exe` with Inno Setup on `windows-latest` and attaches it[^sm-release] |

### What a release contains

`dotnet publish StarMap/StarMap.csproj -c Release -r win-x64 --self-contained false`, zipped from
the publish folder with `-x *.pdb *.xml`. That is the launcher and its project graph: `StarMap.exe`,
`StarMap.Core.dll`, `StarMap.API.dll`, `StarMap.Types.dll`, `0Harmony.dll`, and the usual
`.deps.json` / `.runtimeconfig.json` files. No KSA or Tomlet assembly is included, because both are
referenced with runtime assets excluded. The launcher code requires `0Harmony.dll` and
`StarMap.Core.dll` to sit next to `StarMap.exe` (it loads them through `./` paths). Being
framework-dependent, the folder needs a matching .NET 10 runtime on the machine, and KSA runs on
that runtime because it is hosted in StarMap's process (inferred from the publish flags and the
hosting model).

### Installer

`installer/WindowsInstaller.iss` installs the publish folder to `{pf}\StarMap` (directory page
enabled), adds a Start-menu shortcut and an optional desktop icon, and adds a wizard page "KSA
Install Location (folder with KSA.dll)". After install, if no `StarMapConfig.json` exists in the
install folder, it writes `{"GameLocation": "<chosen folder>","RepositoryLocation": ""}` with
backslashes escaped. Uninstall deletes `StarMapConfig.json`.[^sm-installer]

## Lifecycle and timing

Build-time only; nothing here runs inside the game. The one run-time consequence is the
`StarMapConfig.json` the installer writes, which `LoaderConfig` reads on every launch.

## Key code

| Path | Role |
|---|---|
| [.github/workflows/release.yml](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/.github/workflows/release.yml#L86-L242) | Publish, API change check, NuGet, zip, installer |
| [.github/workflows/rc-build.yml](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/.github/workflows/rc-build.yml#L32-L174) | Release candidates from `dev` |
| [installer/WindowsInstaller.iss](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/installer/WindowsInstaller.iss#L1-L75) | Inno Setup script that writes the config |
| [StarMap.Core/StarMap.Core.csproj](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMap.Core.csproj#L10-L37) | KSA reference scheme per configuration |

## Snippets

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

```text
; installer/WindowsInstaller.iss L47-L75
procedure CurStepChanged(CurStep: TSetupStep); 
var 
    ConfigFilePath: String; 
    JSONText: AnsiString; 
    SaveOK: Boolean; 
    ReadBack: AnsiString; 
    EscapedDir: String; 
begin 
    InstallDir := ExpandConstant('{app}'); 
    ConfigFilePath := AddBackslash(InstallDir) + 'StarMapConfig.json'; 

    if (CurStep = ssPostInstall) and (not FileExists(ConfigFilePath)) then 
    begin 
        if (DirPage <> nil) and (DirPage.Values[0] <> '') then 
            SelectedInstallDir := DirPage.Values[0] 
        else 
            SelectedInstallDir := WizardDirValue; 

        SelectedInstallDir := ExpandConstant(SelectedInstallDir); 

        
        
        EscapedDir := SelectedInstallDir; 
        StringChangeEx(EscapedDir, '\', '\\', True); 
        
        JSONText := '{' + '"GameLocation": "' + EscapedDir + '",' + '"RepositoryLocation": ""' + '}'; 
        SaveOK := SaveStringToFile(ConfigFilePath, JSONText, False);
    end; 
end;
```

```xml
<!-- StarMap.Core/StarMap.Core.csproj L22-L37 -->
<ItemGroup Condition="'$(Configuration)' == 'Debug'">
	<Reference Include="..\..\Import\KSA.dll" />
	<Reference Include="..\..\Import\Brutal.*.dll" />
</ItemGroup>

<ItemGroup Condition="'$(Configuration)' == 'SourceNuget'">
	<PackageReference Include="RocketWerkz.KSA" Version="2026.9.10.5438">
		<ExcludeAssets>runtime</ExcludeAssets>
	</PackageReference>
</ItemGroup>

<ItemGroup Condition="'$(Configuration)' == 'Release'">
	<PackageReference Include="RocketWerkz.KSA.Ref" Version="2026.9.10.5438">
		<ExcludeAssets>runtime</ExcludeAssets>
	</PackageReference>
</ItemGroup>
```

## Game APIs touched

None at run time. The build compiles against the KSA reference package `RocketWerkz.KSA.Ref`
2026.9.10.5438 from the StarMapLoader GitHub Packages feed; the members StarMap patches were
checked against decompiled build 2026.10.10.5554 in the other feature pages.

## Pitfalls and version notes

- **The installer defaults to Program Files.** `StarMap.exe` writes `./StarMapConfig.json` only
  when it is missing, and the installer already creates it, so normal launches do not need write
  access to the install folder.
- **Building from source needs the org feed.** Release and SourceNuget configurations restore
  `RocketWerkz.KSA*` packages through an authenticated GitHub Packages source
  (`ORG_PACKAGE_USERNAME` / `ORG_PACKAGE_TOKEN` secrets in CI); without it, use the Debug
  configuration with an `Import` folder holding `KSA.dll` and `Brutal.*.dll` two levels above the
  project.
- **Mods compile against nuget.org `StarMap.API`.** The API's C# surface has not changed since
  0.3.6 apart from a doc comment, which matches the 0.3.6 package version used by KSAero, purrTTY
  and gatOS while the loader is at 0.4.7.
- **CI observations, not verified by running:** `pr-build.yml` sets `DOTNET_VERSION: 9.0` while all
  projects target `net10.0`; `release.yml` triggers on every closed PR into `main` without checking
  that it was merged; its NuGet job condition compares the string output `api_changed` with the
  boolean `true`, which under GitHub's expression coercion may never be true.
- **win-x64 only.** Releases are published for `win-x64`; the source has no Windows-specific code
  path in the launcher itself.

## See also

- [Host KSA in-process from a launcher executable](host-process-launch.md)
- [Discover lifecycle hooks by attribute type name](attribute-hook-discovery.md)
- [Release RC builds, a NuGet API package and a Windows installer for a launcher-style tool](../../../howto/getting-started/build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-6.md) - how-to article demonstrating this feature

[^sm-release]: .github/workflows/release.yml
[^sm-rc]: .github/workflows/rc-build.yml
[^sm-prbuild]: .github/workflows/pr-build.yml
[^sm-prversion]: .github/workflows/pr-version.yml
[^sm-redirect]: .github/workflows/redirect-external-pr.yml
[^sm-installer]: installer/WindowsInstaller.iss
[^sm-core-csproj]: StarMap.Core/StarMap.Core.csproj
[^sm-api-csproj]: StarMap.API/StarMap.API.csproj
[^sm-csproj]: StarMap/StarMap.csproj
