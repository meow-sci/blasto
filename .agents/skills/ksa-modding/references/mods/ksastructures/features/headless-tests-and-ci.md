---
type: Mod Feature
title: Headless tests over a KSA-free Sim folder, assembly resolution and CI
description: Splits the code into a Sim folder with no KSA types that a test project links wholesale, resolves the game's assemblies from several places with a digest lock, and runs one check script from both the pre-push hook and CI, which builds against a private assemblies repository
mod: ksastructures
category: testing-and-tooling
techniques: [headless-tests, msbuild-props, ci-pipeline]
hooks: []
lifecycle: []
game_types: []
tags: [testing, xunit, ci, github-actions, msbuild, assemblies, lock-file, git-hooks, packaging]
---

Almost everything in KSAStructures is a claim about engine behaviour that only the game can check,
but the maths behind it (the deep ramp's floors, the step gate, the frame latch, the camera roll,
the head look, the bridge's parser) is tested headlessly. The repository makes that possible by
construction and wraps it in tooling that also resolves the game's non-redistributable assemblies
and keeps CI and the developer's machine on the same KSA build.[^mod-claude]

## How it works

1. **`Sim/` cannot touch KSA.** Code under `src/KSAStructures/Sim/` uses only `Brutal.Numerics`
   types; everything that binds to the game is under `Ksa/`. The test project links
   `..\..\src\KSAStructures\Sim\**\*.cs` wholesale and references only `Brutal.Core.Numerics.dll` and
   `Brutal.Core.Common.dll`, so a `using KSA;` under `Sim/` fails the test build, and a new `Sim/` file is
   compiled into the tests the moment it exists.[^mod-tests-csproj]
2. **A textual stand-in where the assemblies are absent.** `tools/check-boundary.sh` greps `Sim/` for
   `using KSA` and `KSA.` references outside comments, so CI without the assemblies can still enforce
   the split.[^mod-boundary]
3. **The tests.** xUnit tests for `BridgeCommand`, `FrameLatch`, `HeadLook`, `OfficeDeep`,
   skipped-frame integration, `StableUp` and `StepGate` live in `tests/KSAStructures.Tests/`.
4. **Finding the game's assemblies.** `Directory.Build.props` resolves `KsaDllDir`, first match wins:
   `KSA_DLL_DIR`, `Import/` (filled by `tools/sync-import.sh`), a sibling `ksa-game-assemblies`
   checkout, then Windows, Steam-on-Linux, home-folder and WSL install paths. A target that runs
   before `CoreCompile` replaces "type not found" walls with one message listing where it looked.[^mod-props][^mod-sync]
5. **Compile-only references.** Every game and loader DLL is referenced with `Private="false"`, so no
   second copy is shipped into another load context, and the KSA build in `ksa-assemblies.lock` is
   stamped into the assembly as `AssemblyMetadata("KsaBuild", ...)` for the log to compare against the
   running game.[^mod-csproj][^mod-lock]
6. **A digest lock.** `ksa-assemblies.lock` records the KSA build and the SHA-256 of each referenced DLL;
   `tools/check-assemblies.sh` compares a folder (`Import/`, the install with `--game`, CI's checkout)
   against it and `--update` re-records it after a game update.[^mod-checkassemblies][^mod-lock]
7. **One script, two callers.** `tools/check-all.sh` runs the Python compile check, shellcheck,
   the boundary check, XML well-formedness, `validate-parts.py --offline`, the comment rules, the
   shader compile, tracked-artefact check and the mesh z-fighting check, then build, tests and the lock
   check when assemblies are present. `.githooks/pre-push` runs it, and CI's tooling job runs it.[^mod-checkall][^mod-prepush]
8. **CI with private assemblies.** The workflow has three jobs: Conventional Commit messages, the
   tooling job, and a build job that checks out a private `ksa-game-assemblies` repository with a
   deploy key, verifies it against the lock, builds, tests, validates parts, packages and uploads the
   zip. Without the secret (a fork) the build job is skipped with a notice.[^mod-ci]
9. **House rules as scripts.** `check-comments.sh` fails on history in comments and on XML doc
   blocks over private members; `check-commit-msg.sh` enforces Conventional Commits as a hook and in
   CI.[^mod-comments][^mod-commitmsg]
10. **Packaging and deploy.** `tools/package.sh` builds a release zip with no symbols and no game
    DLLs; `tools/deploy.sh` installs into the KSA user mods folder and writes the `developer` marker
    that enables the [bridge](./dev-file-bridge-and-mcp.md). `tools/env.sh` points at a .NET 10 SDK,
    since the mod targets `net10.0`.[^mod-package][^mod-deploy][^mod-env]

## Lifecycle and timing

None in game: this is build-time and CI tooling. The tests run against `Sim/` code that the mod
calls from its step and its Harmony patches; see [frame step plumbing](./frame-step-plumbing.md).

## Key code

| Path | Role |
|---|---|
| [tests/KSAStructures.Tests/KSAStructures.Tests.csproj](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tests/KSAStructures.Tests/KSAStructures.Tests.csproj) | Links `Sim/**`, no KSA reference |
| [tools/check-boundary.sh](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/check-boundary.sh) | Textual boundary check |
| [Directory.Build.props](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/Directory.Build.props) | Assembly folder resolution and error target |
| [src/KSAStructures/KSAStructures.csproj](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructures.csproj) | `Private=false` references, content copy, `KsaBuild` stamp |
| [ksa-assemblies.lock](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/ksa-assemblies.lock) | Build and digests |
| [tools/check-assemblies.sh](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/check-assemblies.sh) | Lock check and update |
| [tools/sync-import.sh](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/sync-import.sh) | Copy game DLLs into `Import/` |
| [tools/check-all.sh](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/check-all.sh) | Every check, in CI order |
| [.github/workflows/ci.yml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/.github/workflows/ci.yml) | Commits, tooling and build jobs |

## Snippets

The test project's whole trick:

```xml
<!-- tests/KSAStructures.Tests/KSAStructures.Tests.csproj L28-L36 -->
  <ItemGroup>
    <Compile Include="..\..\src\KSAStructures\Sim\**\*.cs" Link="Linked\%(RecursiveDir)%(Filename)%(Extension)" />
  </ItemGroup>

  <!-- double3 lives here. Runtime assets included: the tests actually execute this. -->
  <ItemGroup>
    <Reference Include="$(KsaDllDir)Brutal.Core.Numerics.dll" />
    <Reference Include="$(KsaDllDir)Brutal.Core.Common.dll" />
  </ItemGroup>
```

First-match-wins assembly resolution:

```xml
<!-- Directory.Build.props L23-L26 -->
  <PropertyGroup>
    <KsaDllDir Condition="'$(KsaDllDir)' == '' AND '$(KSA_DLL_DIR)' != ''">$([MSBuild]::EnsureTrailingSlash('$(KSA_DLL_DIR)'))</KsaDllDir>
    <KsaDllDir Condition="'$(KsaDllDir)' == '' AND Exists('$(MSBuildThisFileDirectory)Import/KSA.dll')">$(MSBuildThisFileDirectory)Import/</KsaDllDir>
    <KsaDllDir Condition="'$(KsaDllDir)' == '' AND Exists('$(MSBuildThisFileDirectory)../ksa-game-assemblies/current/dll/KSA.dll')">$(MSBuildThisFileDirectory)../ksa-game-assemblies/current/dll/</KsaDllDir>
```

Stamping the KSA build into the assembly from the lock file:

```xml
<!-- src/KSAStructures/KSAStructures.csproj L82-L94 -->
  <PropertyGroup>
    <KsaLockPath>$(MSBuildThisFileDirectory)../../ksa-assemblies.lock</KsaLockPath>
    <KsaBuildLine Condition="Exists('$(KsaLockPath)')">$([System.Text.RegularExpressions.Regex]::Match($([System.IO.File]::ReadAllText('$(KsaLockPath)')), 'build[ \t]+[0-9][^\r\n]*').Value)</KsaBuildLine>
    <KsaBuild Condition="'$(KsaBuildLine)' != ''">$(KsaBuildLine.Substring(6).Trim())</KsaBuild>
  </PropertyGroup>

  <ItemGroup Condition="'$(KsaBuild)' != ''">
    <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
      <_Parameter1>KsaBuild</_Parameter1>
      <_Parameter2>$(KsaBuild)</_Parameter2>
    </AssemblyAttribute>
  </ItemGroup>

```

The check list shared by the hook and CI:

```bash
# tools/check-all.sh L79-L101
run "Sim/ is free of KSA types"     ./tools/check-boundary.sh
run "Part XML is well formed"       ./tools/check-xml.sh
run "Asset paths resolve"           ./tools/validate-parts.py --offline
run "Comment rules"                 ./tools/check-comments.sh
run "Shader compiles"               ./tools/check-shaders.sh
run "No artefacts tracked"          ./tools/check-tracked.sh

# With the proximity advisory off and the two hard checks on: a mesh authored in the Blender UI has
# no modelling skin, so the advisory reports every deliberate panel step rather than a defect.
if (( LIST )) || (( ${#DRAWN_ATLASES[@]} )); then
    run "Meshes have no z-fighting or degenerate UVs" \
        ./tools/model/checkmesh.py "${DRAWN_ATLASES[@]}" --near-max 0
else
    skip "Meshes have no z-fighting or degenerate UVs" "no atlas in src/KSAStructures/Meshes/"
fi

if (( LIST )) || have_assemblies; then
    run "Build"                 ./tools/build.sh
    run "Test"                  ./tools/test.sh
    run "Assemblies match lock" ./tools/check-assemblies.sh
else
    skip "Build and test" "KSA assemblies not found - see tools/sync-import.sh"
fi
```

## Game APIs touched

None directly. The build references `KSA.dll`, `StarMap.API.dll`, the `Brutal.*` libraries,
`Planet.Render.Core.dll`, `BepuUtilities.dll` and `0Harmony.dll` (shipped with StarMap) as
compile-only references.[^mod-csproj]

## Pitfalls and version notes

- **Game DLLs are not redistributable.** The repository never commits them; CI uses a private mirror.
- **A KSA update changes `Import/` but not CI's mirror.** `sync-import.sh` runs the lock check and
  says so, instead of leaving it to be discovered as behaviour nobody can reproduce.[^mod-sync]
- **Keep `Private="false"`** on every game reference; a copied `KSA.dll` would load into the mod's
  context and its types would not match the running game's.
- **The lock pins 2026.10.7.5541**, two revisions before the decomp this corpus checks against.

## See also

- [Asset and mesh validation tools](./asset-and-mesh-validation-tools.md) - the asset half of `check-all.sh`
- [Frame step plumbing](./frame-step-plumbing.md) - `FrameLatch` and `StepGate` in the game
- [Terrain height roof override](./terrain-height-roof-override.md) - `OfficeDeep.SurfaceUnder` in the game
- [Pin the KSA build with a hashed assemblies lock, stamp it into the mod and watch for new builds](../../../howto/testing-and-tooling/game-updates/pin-the-game-build-with-a-hashed-lock/pin-the-game-build-with-a-hashed-lock.md) - how-to article demonstrating this feature
- [Pair check-all with a test script and a CI that builds against a private assemblies repo](../../../howto/testing-and-tooling/offline-tests/run-repo-checks-from-a-pre-push-hook-and-ci/run-repo-checks-from-a-pre-push-hook-and-ci-2.md) - how-to article demonstrating this feature
- [Link a game-free Sim folder wholesale into an xUnit project](../../../howto/testing-and-tooling/offline-tests/unit-test-mod-logic-without-game/unit-test-mod-logic-without-game-1.md) - how-to article demonstrating this feature

[^mod-tests-csproj]: tests/KSAStructures.Tests/KSAStructures.Tests.csproj
[^mod-props]: Directory.Build.props
[^mod-csproj]: src/KSAStructures/KSAStructures.csproj
[^mod-lock]: ksa-assemblies.lock
[^mod-checkall]: tools/check-all.sh
[^mod-boundary]: tools/check-boundary.sh
[^mod-checkassemblies]: tools/check-assemblies.sh
[^mod-sync]: tools/sync-import.sh
[^mod-ci]: .github/workflows/ci.yml
[^mod-prepush]: .githooks/pre-push
[^mod-package]: tools/package.sh
[^mod-deploy]: tools/deploy.sh
[^mod-comments]: tools/check-comments.sh
[^mod-commitmsg]: tools/check-commit-msg.sh
[^mod-env]: tools/env.sh
[^mod-claude]: KSAStructures CLAUDE.md (layout and commands)
