---
type: Mod Feature
title: Shared MSBuild deploy targets, release zip and SpaceDock publishing
description: A shared ModDeploy.targets deploys into the mods folder with a locked-DLL guard, launch-profile generation and a deploy stamp, and a GitHub workflow publishes release zips to SpaceDock
mod: ksa-deltavmap
category: getting-started
techniques: [msbuild-targets, github-actions, toml-config]
hooks: []
lifecycle: []
game_types: []
tags: [build, msbuild, deploy, release, ci, github-actions, spacedock, worktrees]
---

Building the mod in Visual Studio deploys it straight into the game's mods folder, and publishing a
GitHub release pushes the same zip to SpaceDock. The interesting parts are the guard rails: a locked
DLL produces one readable error that names the running game instead of ten silent retries, a fresh
git worktree gets a working StarMap launch profile without manual setup, and the deployed copy
records which checkout it came from so two worktrees deploying into one folder are visible.
Mechanism: a reusable `build/ModDeploy.targets` imported by both the mod and its harness-test
project, a `PackageRelease` target in the mod csproj, and `.github/workflows/spacedock-publish.yml`.[^dvmap-deploytargets][^dvmap-csproj][^dvmap-workflow]

## How it works

**Install footprint.** `DeltaVMap/mod.toml` is four lines: `name = "DeltaVMap"` and a `[StarMap]`
table with `EntryAssembly = "DeltaVMap"`. The csproj marks it `CopyToOutputDirectory=Always`. The
installed mod is exactly `mods/DeltaVMap/DeltaVMap.dll` plus `mod.toml`; no assets.[^dvmap-modtoml]

**References.** `DeltaVMap.csproj` targets `net10.0` / x64 with NuGet `Lib.Harmony 2.4.2` and
`StarMap.API 0.3.6`, and game assemblies by `HintPath` from `..\..\..\refs\game\` (`KSA.dll`,
`Brutal.Core.Numerics`, `Brutal.Core.Common`, `Brutal.Core.Logging`, `Brutal.ImGui`,
`CommunityToolkit.HighPerformance`, `Planet.Render.Core`), all `Private=false` so none is copied.
The release zip carries no Harmony DLL either; StarMap itself references `Lib.Harmony 2.4.2`, and the
README lists StarMap as the only runtime dependency.[^starmap-core-csproj][^dvmap-readme]

**`ModDeploy.targets`** (import near the end of a csproj; inputs `ModDeployName`, `PatchFile`,
`DeployToMods`, `GenerateLaunchProfile`, all defaulted):

| Target | When | What |
|---|---|---|
| `EnsureLaunchProfile` | `BeforeTargets="Build"`, only if `Properties\launchSettings.json` is missing | Writes a `StarMap` executable profile pointing at `..\..\..\refs\starmap\StarMap.exe` (resolved from the targets file's own directory), because the file holds absolute paths, is gitignored, and so a new worktree would have no way to launch |
| `CopyToMods` | `AfterTargets="Build"` when `DeployToMods` | Copies the DLL to `$(USERPROFILE)\Documents\My Games\Kitten Space Agency\mods\<name>\` with `Retries="0"` and `ContinueOnError`; only if nothing was copied it runs one PowerShell `Get-Process -Name StarMap,KSA` and fails with "The game is running (...) and holds the deployed DLL open" or a path/permission message; then copies `mod.toml` and any `PatchFile` items into `Patches\` |
| `StampDeploySlot` | `AfterTargets="CopyToMods"` | Reads the previous `.deployed-from` stamp, gets branch and short commit with `git -C <repo> rev-parse`, prints "deploy slot taken over" when another checkout deployed last, and rewrites the stamp (checkout path, branch, commit, UTC time) |

**Release zip.** `PackageRelease` runs after `Build` in `Release` only: it copies the DLL and
`mod.toml` into `bin\...\release\DeltaVMap\`, zips the `release` folder (so the zip contains a
`DeltaVMap/` folder ready to extract into `mods/`), and deletes the staging folder.

**SpaceDock publish.** On `release: published` (or manual dispatch with a tag) the workflow strips
the leading `v` from the tag for the mod version, validates the `SPACEDOCK_MOD_ID` variable and the
username/password secrets, checks out the tag, greps the first `Mod.cs` for
`private const string TestedGameVersion = "..."` to get the compatible KSA build (so bumping the
constant in code is the only step), downloads exactly one zip from the release with `gh release
download`, uses the release body as changelog, logs in to `https://spacedock.info/api/login` with a
cookie jar, posts to `/api/mod/<id>/update`, and always deletes the cookie file.[^dvmap-mod]

## Lifecycle and timing

Build and CI tooling has no place in the KSA frame, so `lifecycle` and `hooks` are empty. The runtime
counterpart is the manifest: StarMap discovers `mods/DeltaVMap/mod.toml`, loads the
`EntryAssembly` into the mod's load context and finds the `[StarMapMod]` class, after which the
lifecycle hooks in [Map window and menu entry](./map-window-and-menu-entry.md) apply. The deploy
targets run only at build time and refuse to overwrite a DLL the running game holds open, so a
rebuild never half-replaces a loaded mod.

## Key code

| Path | Role |
|---|---|
| [build/ModDeploy.targets](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/build/ModDeploy.targets) | Launch profile, guarded copy, deploy-slot stamp |
| [DeltaVMap/DeltaVMap.csproj](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/DeltaVMap.csproj) | References, `InternalsVisibleTo`, `SceneChecks` removal, `PackageRelease` |
| [DeltaVMap/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/mod.toml) | StarMap manifest |
| [.github/workflows/spacedock-publish.yml](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/.github/workflows/spacedock-publish.yml) | Release to SpaceDock upload |

## Snippets

```xml
<!-- build/ModDeploy.targets L66-L96 -->
<Target Name="CopyToMods" AfterTargets="Build" Condition="'$(DeployToMods)' == 'true'">
  <MakeDir Directories="$(ModDeployDir)" />

  <!-- One attempt rather than the default ten retries: the only thing that ever locks this file
       is the running game, and retrying only delays that verdict by ten seconds. -->
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
  <MakeDir Directories="$(ModDeployDir)Patches\" Condition="'@(PatchFile)' != ''" />
  <Copy SourceFiles="@(PatchFile)" DestinationFolder="$(ModDeployDir)Patches\" Condition="'@(PatchFile)' != ''" />
</Target>
```

```xml
<!-- DeltaVMap/DeltaVMap.csproj L67-L79 -->
<Target Name="PackageRelease" AfterTargets="Build" Condition="'$(Configuration)' == 'Release'">
  <PropertyGroup>
    <ReleaseDir>$(OutputPath)release\$(AssemblyName)\</ReleaseDir>
    <ZipPath>$(OutputPath)$(AssemblyName).zip</ZipPath>
  </PropertyGroup>
  <RemoveDir Directories="$(OutputPath)release" />
  <MakeDir Directories="$(ReleaseDir)" />
  <Copy SourceFiles="$(OutputPath)$(AssemblyName).dll" DestinationFolder="$(ReleaseDir)" />
  <Copy SourceFiles="mod.toml" DestinationFolder="$(ReleaseDir)" />
  <ZipDirectory SourceDirectory="$(OutputPath)release" DestinationFile="$(ZipPath)" Overwrite="true" />
  <RemoveDir Directories="$(OutputPath)release" />
  <Message Importance="high" Text="Release packaged: $(ZipPath)" />
</Target>
```

```yaml
# .github/workflows/spacedock-publish.yml L80-L100
      - name: Extract tested game version from Mod.cs
        id: gamever
        run: |
          set -euo pipefail
          # Find the first Mod.cs that defines TestedGameVersion. The
          # project folder is normally <ModName>/Mod.cs from repo root.
          line=$(grep -rEh --include='Mod.cs' \
                   'private +const +string +TestedGameVersion *= *"' \
                   . | head -1 || true)
          if [ -z "$line" ]; then
            echo "::error::Could not find TestedGameVersion in any Mod.cs"
            exit 1
          fi
          # Strip everything except the quoted value, then strip leading 'v'
          version=$(printf '%s' "$line" | sed -E 's/.*"v?([^"]+)".*/\1/')
          if [ -z "$version" ]; then
            echo "::error::Failed to parse game version from line: $line"
            exit 1
          fi
          echo "game_version=$version" >> "$GITHUB_OUTPUT"
          echo "Extracted tested game version: $version"
```

## Game APIs touched

None at runtime. The same `TestedGameVersion` constant the workflow reads is compared at startup
with `VersionInfo.Current.VersionString` in `Mod.OnFullyLoaded` (see
[Map window and menu entry](./map-window-and-menu-entry.md)).

## Pitfalls and version notes

- The deploy path uses `$(USERPROFILE)` and `powershell`, so these targets are Windows-only as
  written; set `DeployToMods=false` elsewhere.
- The game references come from a `refs/` tree three levels above the project (outside this
  repository); a fresh clone must recreate that layout or change the HintPaths.
- Keep the deployed copy in exactly one place per mod name. Several worktrees share one deploy
  folder; the stamp only makes the last writer visible, it does not prevent it.
- The workflow requires exactly one `.zip` asset per release and a `TestedGameVersion` constant in
  a file named `Mod.cs`.

## See also

- [Map window and menu entry](./map-window-and-menu-entry.md)
- [Headless harness tests](./headless-harness-tests.md)
- [Offline layout and math tests](./offline-layout-and-math-tests.md)
- [Overview](../overview.md)
- [Import a shared ModDeploy.targets that diagnoses a locked DLL](../../../howto/getting-started/build/deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-1.md) - how-to article demonstrating this feature
- [Generate a launchSettings profile so Rider or Visual Studio runs StarMap and hits mod breakpoints](../../../howto/getting-started/build/deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-5.md) - how-to article demonstrating this feature
- [Zip the deployed folder with a Release MSBuild target](../../../howto/getting-started/build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-1.md) - how-to article demonstrating this feature
- [Publish from CI with a workflow that reads the tested game version from Mod.cs](../../../howto/getting-started/build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-4.md) - how-to article demonstrating this feature

[^dvmap-deploytargets]: build/ModDeploy.targets
[^dvmap-csproj]: DeltaVMap/DeltaVMap.csproj
[^dvmap-modtoml]: DeltaVMap/mod.toml
[^dvmap-workflow]: .github/workflows/spacedock-publish.yml
[^dvmap-mod]: DeltaVMap/Mod.cs
[^dvmap-readme]: README.md
[^starmap-core-csproj]: StarMap.Core/StarMap.Core.csproj
