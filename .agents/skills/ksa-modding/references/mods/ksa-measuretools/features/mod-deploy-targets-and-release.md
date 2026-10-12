---
type: Mod Feature
title: Shared MSBuild deploy targets, release zip and SpaceDock publishing
description: An imported ModDeploy.targets deploys the DLL and mod.toml with a locked-file check that names the running game, a generated StarMap launch profile and a deploy stamp; a Release zip is published to SpaceDock by a workflow
mod: ksa-measuretools
category: getting-started
techniques: [msbuild-targets, github-actions]
hooks: []
lifecycle: []
game_types: []
tags: [build, msbuild, deploy, release, spacedock, ci, worktrees, tooling]
---

Building the project in Debug or Release drops `MeasureTools.dll` and `mod.toml` straight into
`Documents\My Games\Kitten Space Agency\mods\MeasureTools\`; a Release build also produces
`MeasureTools.zip`, and publishing a GitHub release uploads that zip to SpaceDock with the compatible
game build read from the source.[^mt-csproj][^mt-spacedock] The targets file is byte-identical to the
one in the same author's KSA-DeltaVMap, so it is a shared template.

## How it works

### Project references

`MeasureTools.csproj` targets `net10.0`, `x64`, nullable on, with NuGet `Lib.Harmony` 2.4.2 and
`StarMap.API` 0.3.6. Game assemblies are referenced by `HintPath` to `..\..\..\refs\game\*.dll` with
`Private=false` (not copied): `KSA`, `Brutal.Core.Numerics`, `Brutal.Core.Logging`, `Brutal.ImGui`,
`Brutal.Glfw`, `BepuUtilities`, `Brutal.Core.Common` and `Planet.Render.Core`. The comments explain
the less obvious ones: `BepuUtilities` because `Vehicle.CenterOfMassAsmb` and `GetMatrixAsmb2Ego`
surface types from `OffsetMassProperties` (`Symmetric3x3`), `Brutal.Core.Common` for the `Ptr` type
in `ImInputString`, and `Planet.Render.Core` for `Core.SpanBuilder`, used for multi-part labels and,
per the comment, part of every `ContextReference.ToNearest` overload set (`DistanceReference` derives
from `ContextReference`).[^mt-csproj]

### build/ModDeploy.targets

Imported at the end of the csproj. Inputs with defaults: `ModDeployName` (assembly name),
`PatchFile` items (none here), `DeployToMods` (true), `GenerateLaunchProfile` (true).[^mt-deploy-targets]

| Target | When | What it does |
|---|---|---|
| `EnsureLaunchProfile` | Before `Build`, only if `Properties\launchSettings.json` is missing | Writes a Visual Studio launch profile that runs `StarMap.exe` from `..\..\..\refs\starmap`, because the file holds absolute paths, is gitignored, and is therefore missing in every fresh git worktree |
| `CopyToMods` | After `Build` | Copies the DLL with `Retries="0"` and `ContinueOnError`; if nothing was copied, runs PowerShell `Get-Process StarMap,KSA` and fails with "The game is running (...) and holds the deployed DLL open" or a path error; then copies `mod.toml` and any patch files into `Patches\` |
| `StampDeploySlot` | After `CopyToMods` | Writes `.deployed-from` (checkout path, branch, short commit, UTC time) into the deploy folder and prints "deploy slot taken over" when the previous stamp came from a different checkout |

The locked-file check exists because the running game holds the deployed DLL open and the default ten
copy retries spend ten seconds to fail with a message that never mentions the game. The stamp exists
because several worktrees of one mod deploy into the same folder and the last build silently wins.

### Release packaging

`PackageRelease` (after `Build`, Release only) copies the DLL and `mod.toml` into
`release\MeasureTools\`, zips it to `$(OutputPath)MeasureTools.zip`, and deletes the staging folder.
The zip contains only those two files: no Harmony (StarMap carries it) and no assets.

### SpaceDock workflow

`.github/workflows/spacedock-publish.yml` runs on `release: published` or manually with a tag. It
strips the leading `v` from the tag for the version, greps the first `Mod.cs` for
`private const string TestedGameVersion = "..."` to get the compatible KSA build, downloads the single
`.zip` release asset with `gh release download`, logs into SpaceDock with repository secrets, posts to
`/api/mod/<id>/update` with the release body as changelog, and deletes the cookie jar in an
`always()` step.[^mt-spacedock][^mt-mod]

## Lifecycle and timing

Build time only; nothing here runs inside the game. The game picks the copied files up at the next
start, through StarMap reading `mod.toml` (`name = "MeasureTools"`, `[StarMap] EntryAssembly =
"MeasureTools"`).[^mt-modtoml]

## Key code

| Path | Role |
|---|---|
| [build/ModDeploy.targets](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/build/ModDeploy.targets) | Deploy, launch profile, deploy stamp |
| [MeasureTools/MeasureTools.csproj](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/MeasureTools.csproj) | References, import, `PackageRelease` |
| [.github/workflows/spacedock-publish.yml](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/.github/workflows/spacedock-publish.yml) | Release upload to SpaceDock |
| [MeasureTools.slnx](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools.slnx) | Single-project solution |

## Snippets

```xml
<!-- build/ModDeploy.targets L66-L91 -->
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
```

## Game APIs touched

None. The build only references game assemblies for compilation.

## Pitfalls and version notes

- `ModDeployDir` is built from `$(USERPROFILE)` and the PowerShell check assumes Windows; the targets
  do not work unchanged on Linux or macOS build hosts.
- The `..\..\..\refs\game` and `..\..\..\refs\starmap` paths assume a sibling checkout layout outside
  this repository (a "KSA Modding" tree, per the targets comment), which is not part of the repo.
- The workflow uploads exactly one zip per release and fails on zero or several.
- The csproj's `Brutal.Glfw` reference has no comment; it is needed for `GlfwMouseButton`,
  `GlfwButtonAction` and `GlfwModifier` in the mouse prefix.

## See also

- [Overview](../overview.md)
- [Resilient patching and diagnostics](./resilient-patching-and-diagnostics.md)
- [KSA-DeltaVMap: the same deploy targets and workflow](../../ksa-deltavmap/features/mod-deploy-targets-and-release.md)
- [Import a shared ModDeploy.targets that diagnoses a locked DLL](../../../howto/getting-started/build/deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-1.md) - how-to article demonstrating this feature
- [Publish from CI with a workflow that reads the tested game version from Mod.cs](../../../howto/getting-started/build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-4.md) - how-to article demonstrating this feature

[^mt-deploy-targets]: build/ModDeploy.targets
[^mt-csproj]: MeasureTools/MeasureTools.csproj
[^mt-spacedock]: .github/workflows/spacedock-publish.yml
[^mt-modtoml]: MeasureTools/mod.toml
[^mt-mod]: MeasureTools/Mod.cs
