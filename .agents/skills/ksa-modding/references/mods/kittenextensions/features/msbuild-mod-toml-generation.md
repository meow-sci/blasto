---
type: Mod Feature
title: MSBuild-generated mod.toml and deploy
description: CopyOutput.targets regenerates mod.toml on every build from MSBuild properties and items, then replaces Release/<ModName> and the installed mod folder, while KSADLLs.props globs the game DLLs as non-private references
mod: kittenextensions
category: getting-started
techniques: [msbuild-targets, mod-packaging, ci-release]
hooks: []
lifecycle: []
game_types: []
tags: [msbuild, build, deploy, mod-toml, ci, github-actions, spacedock, packaging]
---

KittenExtensions keeps no `mod.toml` in git. Every build writes one from MSBuild data and copies the
complete mod folder to two places: a `Release/<ModName>/` staging folder in the repo and the
developer's KSA mods folder. The same `CopyOutput.targets` file is shared, unchanged apart from
whitespace, by AMPW's ShaderExtensions and MPFX, so it doubles as a reusable template for any StarMap
mod that wants its manifest, asset lists and StarMap dependencies declared in the project
file.[^kx-copyoutput] [^sx-copyoutput] [^mpfx-copyoutput]

## How it works

1. **Game references by glob.** `KSADLLs.props` collects `$(KSADir)Brutal*.dll`, `KSA.dll`,
   `Planet*.dll` and `Tomlet.dll` and turns each into a `<Reference>` with a `HintPath` and
   `Private="False"`, so nothing from the game is copied to the output
   ([KittenExtensions/KSADLLs.props](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KSADLLs.props#L1-L9)). StarMap.API comes from
   `$(KSAModsDir)StarMap/StarMap.API.dll`, also non-private; Harmony is the NuGet package
   `Lib.Harmony` 2.4.2; XPathPatch is a project reference to the submodule
   ([KittenExtensions/KittenExtensions.csproj](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KittenExtensions.csproj#L53-L61)).[^kx-ksadlls] [^kx-csproj]
2. **Per-developer paths.** `KittenExtensions.csproj.user` sets `KSADir`, `KSAModsDir` and adds a
   second `CopyDestination` of `$(KSAModsDir)$(ModName)/`; its `ActiveDebugProfile` points at a launch
   profile that starts `StarMap.exe` from the mods folder, so F5 runs the game through the loader
   ([KittenExtensions/KittenExtensions.csproj.user](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KittenExtensions.csproj.user#L6-L15),
   [KittenExtensions/Properties/launchSettings.json](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Properties/launchSettings.json#L1-L9)).[^kx-csproj-user] [^kx-launchsettings]
3. **One copy per destination.** `CopyBuildOutput` runs after `Build` and calls the internal
   `_CopyToSingleDestination` target once per `CopyDestination` item through MSBuild batching
   (`%(CopyDestination.Identity)`); the default destination is `../Release/$(ModName)/`
   ([KittenExtensions/CopyOutput.targets](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/CopyOutput.targets#L1-L8)).
4. **Collect manifest data.** `None` items with `ModManifestArray` metadata are grouped by that
   metadata value; for each group `_CreateModManifestArrayLine` emits `name = [ "a", "b" ]` with
   backslashes turned into forward slashes ([KittenExtensions/CopyOutput.targets](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/CopyOutput.targets#L15-L29),
   [KittenExtensions/CopyOutput.targets](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/CopyOutput.targets#L64-L76)). `StarMapModDependency` items become
   `[[StarMap.ModDependencies]]` blocks ([KittenExtensions/CopyOutput.targets](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/CopyOutput.targets#L31-L35),
   [KittenExtensions/CopyOutput.targets](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/CopyOutput.targets#L78-L98)); see
   [Continued fork with a new mod id](continued-fork-identity.md) for how dependents use it.
5. **Replace the folder.** `RemoveDir`, `MakeDir`, copy `$(OutputPath)**\*` preserving structure,
   then `WriteLinesToFile` writes `mod.toml`: `name`, `version = "v$(Version)"`, `author`,
   `description`, the array lines, `[StarMap]`, `EntryAssembly = "$(AssemblyName)"` and the dependency
   blocks ([KittenExtensions/CopyOutput.targets](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/CopyOutput.targets#L37-L61)). KittenExtensions itself declares no
   `ModManifestArray` or `StarMapModDependency` items, so its manifest has only the scalar keys and
   `EntryAssembly = "KittenExtensions"`.
6. **Version stamp.** `SetVersionMetadata` runs `git rev-parse --short HEAD` before
   `GetAssemblyVersion` and sets `InformationalVersion` to `v$(VersionPrefix).<hash>` with `-debug`
   for Debug builds ([KittenExtensions/KittenExtensions.csproj](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KittenExtensions.csproj#L63-L76)).
7. **CI and release.** The GitHub Actions workflow checks out submodules recursively, installs .NET
   10, downloads `StarMap.API.dll` from the latest StarMap release, clones a private repository of
   KSA DLLs using a GitHub App token, reads `KSA_VERSION.txt`, builds with `/p:KSADir` and
   `/p:KSAModsDir`, and uploads `Release/`. On `main`, when the csproj `VersionPrefix` differs from the
   latest release tag, it creates a GitHub release whose body is "Built for KSA version ..." and posts
   the zip to SpaceDock ([.github/workflows/build.yml](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/.github/workflows/build.yml#L13-L435)).[^kx-build-yml]

## Lifecycle and timing

Build time only; nothing here runs inside the game. The generated `mod.toml` is what KSA's
`ModLibrary` and StarMap read at startup (`startup:starmap-before-main` for StarMap's `[StarMap]`
table, `startup:asset-load` for `assets` and the other content keys).

## Key code

| Path | Role |
|---|---|
| [KittenExtensions/KSADLLs.props](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KSADLLs.props#L1-L9) | Game DLL references by glob |
| [KittenExtensions/CopyOutput.targets](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/CopyOutput.targets) | Manifest generation and deploy |
| [KittenExtensions/KittenExtensions.csproj](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KittenExtensions.csproj#L63-L78) | Version metadata target and targets import |
| [KittenExtensions/KittenExtensions.csproj.user](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KittenExtensions.csproj.user#L6-L15) | Local paths, extra destination, debug profile |
| [.github/workflows/build.yml](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/.github/workflows/build.yml) | CI build, GitHub release, SpaceDock upload |
| [MPFX/MPFX.csproj](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.csproj#L83-L87) | A consumer tagging an asset file as `assets` |

## Snippets

```xml
<!-- KittenExtensions/KSADLLs.props L1-L9 -->
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <ItemGroup>
    <_KsaAssemblies Include="$(KSADir)Brutal*.dll" Path="%(FullPath)" />
    <_KsaAssemblies Include="$(KSADir)KSA.dll" Path="%(FullPath)" />
    <_KsaAssemblies Include="$(KSADir)Planet*.dll" Path="%(FullPath)" />
    <_KsaAssemblies Include="$(KSADir)Tomlet.dll" Path="%(FullPath)" />
    <Reference Include="@(_KsaAssemblies->'%(FileName)')" HintPath="%(Path)" Private="False" />
  </ItemGroup>
</Project>
```

```xml
<!-- KittenExtensions/CopyOutput.targets L37-L61 -->
        <!-- Remove existing directory -->
        <RemoveDir Directories="$(SingleCopyDestination)" />

        <!-- Recreate -->
        <MakeDir Directories="$(SingleCopyDestination)" />

        <!-- Copy preserving structure -->
        <Copy SourceFiles="@(FilesToCopy)" DestinationFiles="@(FilesToCopy->'$(SingleCopyDestination)%(RecursiveDir)%(Filename)%(Extension)')" />

        <ItemGroup>
            <ModManifestLines Include="name = &quot;$(ModName)&quot;" />
            <ModManifestLines Include="version = &quot;v$(Version)&quot;" />
            <ModManifestLines Include="author = &quot;$(Authors)&quot;" />
            <ModManifestLines Include="description = &quot;$(Description)&quot;" />
            <ModManifestLines Include="@(ModManifestArrayLines)">
                <Separator>$([System.Environment]::NewLine)</Separator>
            </ModManifestLines>
            <ModManifestLines Include="[StarMap]">
                <Separator>$([System.Environment]::NewLine)</Separator>
            </ModManifestLines>
            <ModManifestLines Include="EntryAssembly = &quot;$(AssemblyName)&quot;" />
            <ModManifestLines Include="@(StarMapModDependencyLines)" />
        </ItemGroup>

        <WriteLinesToFile File="$(SingleCopyDestination)mod.toml" Lines="@(ModManifestLines->'%(Separator)%(Identity)')" Overwrite="true" />
```

```xml
<!-- KittenExtensions/CopyOutput.targets L64-L76 -->
    <Target Name="_CreateModManifestArrayLine" Returns="@(ModManifestArrayLine)">
        <ItemGroup>
            <ModManifestArrayFiles Include="@(None)" Condition="'%(None.ModManifestArray)' == '$(ModManifestArrayName)'">
                <ManifestPath>$([System.String]::Copy('%(Identity)').Replace('\', '/'))</ManifestPath>
            </ModManifestArrayFiles>
        </ItemGroup>
        <PropertyGroup>
            <ModManifestArrayValues>@(ModManifestArrayFiles->'&quot;%(ManifestPath)&quot;', ', ')</ModManifestArrayValues>
        </PropertyGroup>
        <ItemGroup>
            <ModManifestArrayLine Include="$(ModManifestArrayName) = [ $(ModManifestArrayValues) ]" />
        </ItemGroup>
    </Target>
```

```xml
<!-- MPFX/MPFX.csproj L83-L87 -->
	<ItemGroup>
		<None Update="$(ModName)Assests.xml">
			<CopyToOutputDirectory>Always</CopyToOutputDirectory>
			<ModManifestArray>assets</ModManifestArray>
		</None>
```

## Game APIs touched

None at run time. The build references the game assemblies `KSA.dll`, `Brutal*.dll`, `Planet*.dll`
and `Tomlet.dll` from the install folder.

## Pitfalls and version notes

- **Every build deletes the destination folder.** `RemoveDir` wipes `mods/<ModName>/` before copying,
  so anything a mod writes into its own folder at run time is lost on the next build. MPFX, which uses
  this file, keeps its profile directory at `mods/MPFX/Profiles`
  ([MPFX/MPFXProfile.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXProfile.cs#L15)).[^mpfx-profile]
- **The shipped manifest is not in git.** Read the targets and csproj items to know what `mod.toml`
  contains.
- **`Private=False` everywhere.** Game DLLs, StarMap.API and (in dependents) `KittenExtensions.dll`
  are never copied; the copies already loaded by the game and StarMap are used at run time.
- **Committed local paths.** The checked-in `.csproj.user` contains the author's Windows paths;
  override `KSADir` and `KSAModsDir` on the command line or in your own user file.
- **CI needs secrets.** Forks cannot build in CI without their own KSA DLL repository and GitHub App
  credentials.
- `Directory.Build.Rsp` passes `-tl:off` to MSBuild ([Directory.Build.Rsp](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/Directory.Build.Rsp)), turning off the
  terminal logger.[^kx-rsp]

## See also

- [Continued fork with a new mod id and the old assembly name](continued-fork-identity.md)
- [XML patch files and XPathPatch operations](xpath-patch-operations.md)
- [Fetch a private DLL repository in CI with a GitHub App token](../../../howto/getting-started/build/build-in-ci-without-the-game-installed/build-in-ci-without-the-game-installed-1.md) - how-to article demonstrating this feature
- [Generate mod.toml and StarMap dependencies from the project file](../../../howto/getting-started/build/generate-mod-toml-from-msbuild/generate-mod-toml-from-msbuild.md) - how-to article demonstrating this feature
- [Share one KSADLLs.props across a family of mods and point CI at a private DLL checkout](../../../howto/getting-started/build/reference-game-assemblies-from-a-mod-project/reference-game-assemblies-from-a-mod-project-3.md) - how-to article demonstrating this feature

[^kx-copyoutput]: KittenExtensions/CopyOutput.targets
[^sx-copyoutput]: ShaderExtensions/CopyOutput.targets
[^mpfx-copyoutput]: MPFX/CopyOutput.targets
[^kx-ksadlls]: KittenExtensions/KSADLLs.props
[^kx-csproj]: KittenExtensions/KittenExtensions.csproj
[^kx-csproj-user]: KittenExtensions/KittenExtensions.csproj.user
[^kx-launchsettings]: KittenExtensions/Properties/launchSettings.json
[^kx-build-yml]: .github/workflows/build.yml
[^mpfx-profile]: MPFX/MPFXProfile.cs
[^kx-rsp]: Directory.Build.Rsp
