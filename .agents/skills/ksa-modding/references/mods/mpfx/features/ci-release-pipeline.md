---
type: Mod Feature
title: Generated mod.toml and CI release pipeline
description: MSBuild items generate mod.toml with the assets array and StarMap ModDependencies on every build, and a GitHub Actions workflow builds against KSA DLLs from a private repo via a GitHub App token, then publishes GitHub and SpaceDock releases
mod: mpfx
category: testing-and-tooling
techniques: [toml-config, ci-release, msbuild-targets]
hooks: []
lifecycle: []
game_types: []
tags: [build, msbuild, ci, github-actions, release, spacedock, mod-toml, starmap]
---

MPFX never checks in a `mod.toml`. The manifest is generated from the csproj on every build. The same
`CopyOutput.targets` pattern appears in KittenExtensions Continued (see
[MSBuild-generated mod.toml and deploy](../../kittenextensions/features/msbuild-mod-toml-generation.md)), and
MPFX extends it with StarMap dependency tables. A single workflow then turns a `VersionPrefix` bump on
`main` into a GitHub release and a SpaceDock update. It needs no public copy of the game DLLs.[^mpfx-copyoutput] [^mpfx-ci]

## How it works

1. **Manifest data lives in the csproj.** `<ModName>`, `<VersionPrefix>` and `<Authors>` become `name`,
   `version` (prefixed `v`) and `author`. Any `None` item with `<ModManifestArray>assets</ModManifestArray>`
   becomes an entry in `assets = [ ... ]`. Each `<StarMapModDependency Include="ModId">` with
   `<ImportedAssemblies>` (and optional `<Optional>`) becomes a `[[StarMap.ModDependencies]]` table
   ([MPFX/MPFX.csproj](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.csproj#L74-L91)).[^mpfx-csproj]
2. **Generation.** After `Build`, `_CopyToSingleDestination` deletes and recreates `../Release/MPFX/`,
   copies the output, and builds the lines through two helper targets called with `MSBuild` task
   batching (`_CreateModManifestArrayLine`, `_CreateStarMapModDependencyLines`). It then writes
   `mod.toml` with `WriteLinesToFile`, using a per-item `Separator` metadata to insert blank lines
   before tables ([MPFX/CopyOutput.targets](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/CopyOutput.targets#L10-L97)). `Optional` defaults to `false`
   and is lower-cased, and `ImportedAssemblies` are split on `;`.
3. **Game references.** [MPFX/KSADLLs.props](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/KSADLLs.props) globs `Brutal*.dll`, `KSA.dll`, `Planet*.dll`
   and `Tomlet.dll` under `$(KSADir)` into non-private references. `StarMap.API.dll` and
   `KittenExtensions.dll` come from `$(KSAModsDir)`.[^mpfx-ksadlls]
4. **CI dependencies.** An inline Python step reads the latest GitHub release of `StarMapLoader/StarMap`
   and `AMPW-german/KittenExtensions`, extracts the zip and copies the one DLL each into
   `deps/<ModFolder>/` ([.github/workflows/build.yml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/.github/workflows/build.yml#L40-L160)). ShaderExtensions is not fetched, because
   MPFX has no compile-time reference to it.
5. **Private game DLLs.** A `KSADLLRepo` secret names an `owner/repo`. `actions/create-github-app-token`
   mints a token for that one repository, and the workflow shallow-clones it and copies the root files
   into `deps/ksa/`. A `KSA_VERSION.txt` there becomes the release's game version. The build passes
   `/p:KSADir` and `/p:KSAModsDir` ([.github/workflows/build.yml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/.github/workflows/build.yml#L162-L269)).
6. **Release gate.** On a push to `main`, the step compares `v<VersionPrefix>` with the latest release
   tag and sets `release_needed`. Pull requests build Debug artifacts. The release job zips the
   artifact, creates a draft release with generated notes, uploads the zip, publishes the release, and
   posts the same zip and changelog to SpaceDock mod 4165 through its login and update API
   ([.github/workflows/build.yml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/.github/workflows/build.yml#L296-L432)).

## Lifecycle and timing

Build time only. Nothing here runs inside KSA. At game start, the generated `mod.toml` is what StarMap
reads: `EntryAssembly`, plus `ModDependencies` that force KittenExtensions Continued and ShaderExtensions
to load first and expose their assemblies to MPFX's load context. The game reads the `assets` list
during `startup:asset-load`.

## Key code

| Path | Role |
|---|---|
| [MPFX/CopyOutput.targets](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/CopyOutput.targets) | Release folder refresh and `mod.toml` generation |
| [MPFX/MPFX.csproj](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.csproj) | Manifest data as MSBuild properties and items, git short hash in `InformationalVersion` |
| [.github/workflows/build.yml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/.github/workflows/build.yml) | Build, release gate, GitHub and SpaceDock publish |
| [MPFX/Properties/launchSettings.json](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Properties/launchSettings.json) | Debug profile that launches `StarMap.exe`[^mpfx-launch] |

## Snippets

```xml
<!-- MPFX/MPFX.csproj L74-L91 -->
<ItemGroup>
    <StarMapModDependency Include="KittenExtensionsContinued">
        <ImportedAssemblies>KittenExtensions</ImportedAssemblies>
    </StarMapModDependency>
    <StarMapModDependency Include="ShaderExtensions">
        <ImportedAssemblies>ShaderExtensions</ImportedAssemblies>
    </StarMapModDependency>
</ItemGroup>

<ItemGroup>
    <None Update="$(ModName)Assests.xml">
        <CopyToOutputDirectory>Always</CopyToOutputDirectory>
        <ModManifestArray>assets</ModManifestArray>
    </None>
    <None Update="Shaders\*">
        <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
</ItemGroup>
```

```xml
<!-- MPFX/CopyOutput.targets L44-L60 -->

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

```yaml
# .github/workflows/build.yml L183-L203
- name: Create GitHub App token for KSA DLL repo
  id: app-token
  uses: actions/create-github-app-token@v3
  with:
    client-id: ${{ secrets.APP_ID }}
    private-key: ${{ secrets.APP_PRIVATE_KEY }}
    owner: ${{ steps.ksa-repo.outputs.owner }}
    repositories: ${{ steps.ksa-repo.outputs.repo }}

- name: Download private KSA DLLs from repository
  shell: bash
  env:
    GITHUB_TOKEN: ${{ steps.app-token.outputs.token }}
  run: |
    git clone --depth 1 "https://x-access-token:${GITHUB_TOKEN}@github.com/${KSA_DLL_REPOSITORY}.git" deps/ksa-repo
    find deps/ksa-repo -maxdepth 1 -type f -exec cp {} deps/ksa/ \;
    dll_count=$(find deps/ksa -maxdepth 1 -type f -name '*.dll' | wc -l)
    if [ "$dll_count" -eq 0 ]; then
      echo "No DLL files found in root of ${KSA_DLL_REPOSITORY}." >&2
      exit 1
    fi
```

## Game APIs touched

None. The workflow reads only the KSA DLLs as compile references.

## Pitfalls and version notes

- `Description` is never set in the csproj, so the generated `description = ""` is empty.
- `AssemblyVersion` is pinned at 0.3.1.0 while `VersionPrefix` is 0.3.6. Only the file and informational
  versions track releases.
- `CopyDestination` only targets `../Release/MPFX/`. Unlike KittenExtensions' variant, MPFX does not copy
  into the game's mods folder, so local testing needs a manual copy or symlink.
- ModMenu is required at runtime according to the README but is not a `StarMapModDependency`, so it is
  missing from the generated `mod.toml`.

## See also

- [Effect chain declared as RenderPassId-ordered post-process assets](effect-chain-assets.md)
- [Uniform buffer and push constant structs bound by attribute name](duck-typed-buffer-structs.md)
- [Fetch a private DLL repository in CI with a GitHub App token](../../../howto/getting-started/build/build-in-ci-without-the-game-installed/build-in-ci-without-the-game-installed-1.md) - how-to article demonstrating this feature
- [Generate mod.toml and StarMap dependencies from the project file](../../../howto/getting-started/build/generate-mod-toml-from-msbuild/generate-mod-toml-from-msbuild.md) - how-to article demonstrating this feature

[^mpfx-copyoutput]: MPFX/CopyOutput.targets
[^mpfx-ci]: .github/workflows/build.yml
[^mpfx-csproj]: MPFX/MPFX.csproj
[^mpfx-ksadlls]: MPFX/KSADLLs.props
[^mpfx-launch]: MPFX/Properties/launchSettings.json
