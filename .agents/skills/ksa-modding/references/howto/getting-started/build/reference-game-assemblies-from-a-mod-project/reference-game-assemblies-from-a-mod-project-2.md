---
type: How-To
title: Resolve the game folder in tiers from Directory.Build.props
description: Try an environment variable, a local override, a sibling assemblies checkout and well-known install paths in order, and fail the build with one readable message, so contributors and CI share a project file
category: getting-started
group: build
topic: reference-game-assemblies-from-a-mod-project
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/content-and-code-mod-layout]
techniques: [msbuild]
hooks: [msbuild Directory.Build.props]
lifecycle: []
game_types: []
tags: [msbuild, directory-build-props, ksadlldir, ci, contributors, cross-platform]
---

KSAGolf has one project file that builds on a Windows developer machine, a native-Linux contributor's machine and CI,
with no per-machine edits. The trick is a repository-root `Directory.Build.props` that resolves `$(KsaDllDir)` by trying
a list of locations in order, then stops the build with a single actionable error if none contains `KSA.dll`. The simple
single-property form is [KsaDir property with wildcard Reference items and HintPath](./reference-game-assemblies-from-a-mod-project-1.md); a shared props file is
[Shared KSADLLs.props glob for several mods](./reference-game-assemblies-from-a-mod-project-3.md); CI without the game at all is
[Build a mod in CI when the game DLLs are private](../build-in-ci-without-the-game-installed/overview.md).

## Background: how the game does it

The game's managed assemblies are RocketWerkz's copyrighted files; they cannot be committed or published, so a build
has to find a folder rather than keep copies. The folder is the KSA install directory (the one with `KSA.dll`), or a
copy such as the `current/dll` folder of a `ksa-game-assemblies` checkout. No runtime behavior depends on which one is
used; at run time the game's own loaded copies win over anything in the mod folder.

## Lifecycle and timing

Build time. MSBuild evaluates `Directory.Build.props` before the project, so every project below it sees `KsaDllDir`;
the check target runs `BeforeTargets="CoreCompile"`, only on a real build, not on restore or IDE load.

## Steps

1. **Write the tiered resolution** in `Directory.Build.props`, first match wins. KSAGolf's order: `KSA_DLL_DIR` (property
   or environment variable, what CI uses), `Import/` populated by a sync script, a sibling `ksa-game-assemblies`
   checkout, then the install on Windows, native Linux (Steam homes, `~/Games`, `~/`) and finally WSL:[Directory.Build.props](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/Directory.Build.props#L22-L47)

   ```xml
   <!-- Directory.Build.props L23-L42 -->
     <PropertyGroup>
       <KsaDllDir Condition="'$(KsaDllDir)' == '' AND '$(KSA_DLL_DIR)' != ''">$([MSBuild]::EnsureTrailingSlash('$(KSA_DLL_DIR)'))</KsaDllDir>
       <KsaDllDir Condition="'$(KsaDllDir)' == '' AND Exists('$(MSBuildThisFileDirectory)Import/KSA.dll')">$(MSBuildThisFileDirectory)Import/</KsaDllDir>
       <KsaDllDir Condition="'$(KsaDllDir)' == '' AND Exists('$(MSBuildThisFileDirectory)../ksa-game-assemblies/current/dll/KSA.dll')">$(MSBuildThisFileDirectory)../ksa-game-assemblies/current/dll/</KsaDllDir>

       <!-- Windows -->
       <KsaDllDir Condition="'$(KsaDllDir)' == '' AND Exists('C:\Program Files\Kitten Space Agency\KSA.dll')">C:\Program Files\Kitten Space Agency\</KsaDllDir>

       <!-- Native Linux and macOS: Steam in both its usual homes, then the obvious manual spots. -->
       <KsaDllDir Condition="'$(KsaDllDir)' == '' AND Exists('$(HOME)/.steam/steam/steamapps/common/Kitten Space Agency/KSA.dll')">$(HOME)/.steam/steam/steamapps/common/Kitten Space Agency/</KsaDllDir>
       <KsaDllDir Condition="'$(KsaDllDir)' == '' AND Exists('$(HOME)/.local/share/Steam/steamapps/common/Kitten Space Agency/KSA.dll')">$(HOME)/.local/share/Steam/steamapps/common/Kitten Space Agency/</KsaDllDir>
       <KsaDllDir Condition="'$(KsaDllDir)' == '' AND Exists('$(HOME)/Games/Kitten Space Agency/KSA.dll')">$(HOME)/Games/Kitten Space Agency/</KsaDllDir>
       <KsaDllDir Condition="'$(KsaDllDir)' == '' AND Exists('$(HOME)/Kitten Space Agency/KSA.dll')">$(HOME)/Kitten Space Agency/</KsaDllDir>

       <!-- WSL, last: this path exists only there, so probing it earlier would shadow a real install. -->
       <KsaDllDir Condition="'$(KsaDllDir)' == '' AND Exists('/mnt/c/Program Files/Kitten Space Agency/KSA.dll')">/mnt/c/Program Files/Kitten Space Agency/</KsaDllDir>

       <HaveKsaAssemblies>false</HaveKsaAssemblies>
       <HaveKsaAssemblies Condition="'$(KsaDllDir)' != '' AND Exists('$(KsaDllDir)KSA.dll')">true</HaveKsaAssemblies>
     </PropertyGroup>
   ```

2. **Fail early with advice.** A target that errors only on a real build, for projects that need the game, replaces
   hundreds of missing-type errors with one message listing every tier searched:[Directory.Build.props](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/Directory.Build.props#L52-L71)

   ```xml
   <!-- Directory.Build.props L52-L56 -->
     <Target Name="KSAGolfCheckKsaAssemblies"
             BeforeTargets="CoreCompile"
             Condition="'$(HaveKsaAssemblies)' != 'true' AND '$(KSAGolfNeedsKsa)' != 'false'">
       <Error Text="
   KSA's assemblies were not found, so this project cannot compile.
   ```

3. **Reference through the property with `Private="false"`**, per assembly:[src/KSAGolf/KSAGolf.csproj](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolf.csproj#L56-L77)

   ```xml
   <!-- src/KSAGolf/KSAGolf.csproj L56-L65 -->
     <ItemGroup>
       <Reference Include="$(KsaDllDir)KSA.dll" Private="false" />
       <Reference Include="$(KsaDllDir)StarMap.API.dll" Private="false" />
       <Reference Include="$(KsaDllDir)Brutal.Core.Numerics.dll" Private="false" />
       <Reference Include="$(KsaDllDir)Brutal.Core.Common.dll" Private="false" />
       <Reference Include="$(KsaDllDir)Brutal.Core.Strings.dll" Private="false" />
       <Reference Include="$(KsaDllDir)Brutal.ImGui.dll" Private="false" />
       <Reference Include="$(KsaDllDir)Brutal.ImGui.Abstractions.dll" Private="false" />
       <!-- KeyHash, which keeps the KSA namespace but is defined in this assembly. -->
       <Reference Include="$(KsaDllDir)Planet.Render.Core.dll" Private="false" />
   ```

   Note KSAGolf references `StarMap.API.dll` and `0Harmony.dll` from the same `$(KsaDllDir)` rather than NuGet:
   they ship with the StarMap loader, not the game, so its `tools/sync-import.sh` copies them from `STARMAP_DIR` into
   `Import/` (tier 2) next to the game assemblies.
4. **Make it work for contributors and CI with the same file.** Contributors clone `ksa-game-assemblies` next to the
   repo or export `KSA_DLL_DIR`; CI checks out the private assemblies repo and exports `KSA_DLL_DIR` ([the CI
   approach](../build-in-ci-without-the-game-installed/build-in-ci-without-the-game-installed-1.md)); neither edits tracked files.
5. **Add a doctor script** (KSAGolf has `tools/doctor.sh`) that prints what was found, and use the same resolver in
   shell tooling.

## Complete example

[Directory.Build.props](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/Directory.Build.props) (resolution and error), [src/KSAGolf/KSAGolf.csproj](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolf.csproj) (references, content copy, build stamp), [src/KSAGolf/mod.toml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/mod.toml) (the
mod folder it produces). The assemblies lock and build stamp are in [Warn when the running game build differs from the one a mod was tested on](../../lifecycle/check-the-game-version/overview.md).

## Variations and gotchas

- **Order matters.** The WSL path is last because it exists only there; probing it earlier would shadow a real install.
- **The check is a target, not a plain error**, so `dotnet restore` and IDE load stay quiet.
- **Keep copies out of git and out of the mod folder**; `Private="false"` handles the second, a `.gitignore` the first
  (`Import/` is populated by a script, not committed).
- **Builds against the lock file's build.** KSAGolf's `ksa-assemblies.lock` records `2026.9.22.5482`; a checkout of
  newer DLLs compiles but the stamp then claims an older target.
- **Tier 4 used to assume WSL** for every non-Windows developer; list the native-Linux locations explicitly and keep
  `KSA_DLL_DIR` as the documented escape hatch.

## See also

- [KsaDir property with wildcard Reference items and HintPath](./reference-game-assemblies-from-a-mod-project-1.md), [Shared KSADLLs.props glob for several mods](./reference-game-assemblies-from-a-mod-project-3.md), [Reference the game's assemblies from a mod project without shipping them](./overview.md)
- [Content-plus-code mod layout](../../../../mods/ksagolf/features/content-and-code-mod-layout.md)
- [Build a mod in CI when the game DLLs are private](../build-in-ci-without-the-game-installed/overview.md), [Deploy a mod on every build and debug it from an IDE](../deploy-a-mod-on-every-build/overview.md)
