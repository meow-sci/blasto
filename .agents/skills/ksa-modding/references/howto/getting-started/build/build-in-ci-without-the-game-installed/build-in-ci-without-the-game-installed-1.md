---
type: How-To
title: Fetch a private DLL repository in CI with a GitHub App token
description: Keep the KSA assemblies in a private repository, mint a short-lived App token scoped to that one repo in the workflow, copy the DLLs into deps/ and pass KSADir on the command line
category: getting-started
group: build
topic: build-in-ci-without-the-game-installed
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/kittenextensions/features/msbuild-mod-toml-generation
  - mods/mpfx/features/ci-release-pipeline
  - mods/shaderextensions/features/ci-build-and-release
techniques: [github-actions, msbuild]
hooks: [github-actions actions/create-github-app-token]
lifecycle: []
game_types: []
tags: [ci, github-actions, github-app, private-repository, ksadir, spacedock]
---

A public mod repository cannot contain the game's DLLs, but its CI must compile against them. The approach used by
KittenExtensions, MPFX and ShaderExtensions (all AMPW): a **private repository** holds the KSA assemblies and a
`KSA_VERSION.txt`; the workflow reads its name from a secret, uses `actions/create-github-app-token` to mint a token
scoped to just that repository, shallow-clones it into `deps/ksa/`, and builds with `/p:KSADir` and `/p:KSAModsDir`
pointing there. The same props file local developers use ([Shared KSADLLs.props glob for several mods](../reference-game-assemblies-from-a-mod-project/reference-game-assemblies-from-a-mod-project-3.md)) therefore
works unchanged. Variants: stripped reference assemblies in object storage ([Refasmer reference assemblies stored per game build](./build-in-ci-without-the-game-installed-2.md))
and not needing the game at all for most of the code ([Compile-gated game seam](./build-in-ci-without-the-game-installed-3.md)).

## Background: how the game does it

Nothing game-specific happens at run time. The legal constraint is the reason: KSA's assemblies are RocketWerkz's
copyrighted files, so they may sit in a *private* repository (or bucket) under the author's control but never in a
public repo, artifact or cache. The build only needs the files at compile time (`Private=False` references), and the
result contains none of them ([Reference the game's assemblies from a mod project without shipping them](../reference-game-assemblies-from-a-mod-project/overview.md)).

## Lifecycle and timing

Build and release time on a GitHub-hosted runner. The workflow also derives the game version for the release and the
SpaceDock listing from `KSA_VERSION.txt` in the private repository, so the release notes name the exact build compiled
against.

## Steps

1. **Create the private DLL repository** containing the managed DLLs at its root (`KSA.dll`, `Brutal.*`, `Planet*`,
   `Tomlet`, ...) and a `KSA_VERSION.txt` with the build string.
2. **Create a GitHub App** with read access to that repository only; store `APP_ID`, `APP_PRIVATE_KEY` and a
   `KSADLLRepo` secret (`owner/repo`) in the mod repository.
3. **Parse the secret, mint the token, clone:**[.github/workflows/build.yml](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/.github/workflows/build.yml#L170-L205)

   ```yaml
   # .github/workflows/build.yml L185-L192
         - name: Create GitHub App token for KSA DLL repo
           id: app-token
           uses: actions/create-github-app-token@v3
           with:
             client-id: ${{ secrets.APP_ID }}
             private-key: ${{ secrets.APP_PRIVATE_KEY }}
             owner: ${{ steps.ksa-repo.outputs.owner }}
             repositories: ${{ steps.ksa-repo.outputs.repo }}
   ```

   ```yaml
   # .github/workflows/build.yml L194-L205
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

4. **Fetch the non-game dependencies** (StarMap.API.dll, and in dependents KittenExtensions.dll) from the latest GitHub
   releases of their repositories with a small Python step; `StarMap.API.dll` is not part of the game. MPFX does the same
   for StarMap and KittenExtensions but not ShaderExtensions, which it does not reference at compile time.
5. **Read the game version for the release:**[.github/workflows/build.yml](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/.github/workflows/build.yml#L206-L215)

   ```yaml
   # .github/workflows/build.yml L207-L216
         - name: Collect KSA version
           id: ksa-version
           shell: bash
           run: |
             ksa_version_txt="deps/ksa/KSA_VERSION.txt"
             if [ ! -f "$ksa_version_txt" ]; then
               echo "KSA_VERSION.txt not found in deps/ksa. Cannot determine KSA version." >&2
               exit 1
             fi
             echo "version=$(cat "$ksa_version_txt")" >> "$GITHUB_OUTPUT"
   ```

6. **Build with the properties on the command line:**

   ```bash
   # .github/workflows/build.yml L271-L274
             deps_dir="$(pwd)/deps/"
             ksa_dir="$deps_dir/ksa/"
             dotnet build KittenExtensions/KittenExtensions.csproj -c "$CONFIGURATION" --no-restore /p:KSADir="$ksa_dir" /p:KSAModsDir="$deps_dir"
   ```

7. **Gate releases on a version change:** on a push to `main`, compare the csproj `VersionPrefix` with the latest release
   tag; only a change creates a GitHub release (draft, notes "Built for KSA version ...") and posts the zip to SpaceDock
   with the game version from step 5 ([Workflow reading the tested game version from Mod.cs](../package-and-publish-a-mod-release/package-and-publish-a-mod-release-4.md)).

## Complete example

| File | Role |
|---|---|
| [.github/workflows/build.yml](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/.github/workflows/build.yml) | The full workflow: dependency download, App token, version detection, build, release, SpaceDock |
| [.github/workflows/build.yml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/.github/workflows/build.yml) | The same pattern for MPFX (inline Python for latest releases, SpaceDock mod 4165) |
| [KittenExtensions/KittenExtensions.csproj](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KittenExtensions.csproj) | Properties and `VersionPrefix` the workflow reads |

## Variations and gotchas

- **Forks cannot build in CI** without their own private DLL repository and App credentials; pull requests from forks
  get no secrets.
- **Never upload or cache the DLLs.** Keep them in a folder outside anything uploaded as an artifact, since artifacts and
  caches can be read from a public repository.
- **The token is short-lived and scoped to one repository**, which is the point of using an App rather than a personal
  access token.
- **`ShaderExtensions.props` is not imported** by the project (it duplicates `KSADLLs.props` plus Harmony).
- **Local defaults.** Without secrets, local builds use a user file or the command line for `KSADir` and `KSAModsDir`.
- **Version drift.** The private repo's DLLs are whatever was last pushed; releases are compiled against that build, not
  a pinned one, unless you pin it ([Refasmer reference assemblies stored per game build](./build-in-ci-without-the-game-installed-2.md) pins by build).

## See also

- [Refasmer reference assemblies stored per game build](./build-in-ci-without-the-game-installed-2.md), [Compile-gated game seam](./build-in-ci-without-the-game-installed-3.md), [Build a mod in CI when the game DLLs are private](./overview.md)
- [MSBuild-generated mod.toml and deploy](../../../../mods/kittenextensions/features/msbuild-mod-toml-generation.md), [Generated mod.toml and CI release pipeline](../../../../mods/mpfx/features/ci-release-pipeline.md),
  [CI build against private game DLLs with GitHub and SpaceDock releases](../../../../mods/shaderextensions/features/ci-build-and-release.md)
- [Reference the game's assemblies from a mod project without shipping them](../reference-game-assemblies-from-a-mod-project/overview.md), [Generate mod.toml and StarMap dependencies from the project file](../generate-mod-toml-from-msbuild/overview.md), [Package and publish a mod release (zip, CI publishing, installer)](../package-and-publish-a-mod-release/overview.md)
