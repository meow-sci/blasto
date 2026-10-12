---
type: Mod Feature
title: CI build against private game DLLs with GitHub and SpaceDock releases
description: GitHub Actions workflow that pulls the latest StarMap and KittenExtensions DLLs from their releases, clones private KSA DLLs with a GitHub App token, builds Debug for PRs and Release for main, and publishes to GitHub and SpaceDock when VersionPrefix changes
mod: shaderextensions
category: testing-and-tooling
techniques: [github-actions, release-automation]
hooks: []
lifecycle: []
game_types: []
tags: [ci, github-actions, release, spacedock, packaging, build]
---

Game DLLs cannot be redistributed, yet the mod must compile against them in CI. The workflow solves
that with a private repository of KSA DLLs reached through a GitHub App token, pulls the two mod
dependencies from their public releases, and turns a version bump in the csproj into a published
release on both GitHub and SpaceDock.[^sx-buildyml]

## How it works

| Step | What happens | Code |
|---|---|---|
| Trigger | `push` to `main` and every `pull_request`; `contents: write` permission | [.github/workflows/build.yml](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/.github/workflows/build.yml#L1-L10) |
| Configuration | `Debug` for pull requests, `Release` otherwise; runner `ubuntu-slim`, .NET 10 | [.github/workflows/build.yml](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/.github/workflows/build.yml#L13-L30) |
| Mod dependencies | Inline Python calls the GitHub API for the latest release of `StarMapLoader/StarMap` and `AMPW-german/KittenExtensions`, downloads the zip asset and extracts `StarMap.API.dll` into `deps/StarMap/` and `KittenExtensions.dll` into `deps/KittenExtensionsContinued/`, the layout the csproj hint paths expect | [.github/workflows/build.yml](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/.github/workflows/build.yml#L37-L161) |
| Private game DLLs | Secret `KSADLLRepo` names an `owner/repo`; `actions/create-github-app-token` mints a token from `APP_ID` and `APP_PRIVATE_KEY` scoped to that repo; a shallow clone copies the root files into `deps/ksa/` and fails if no DLL is present | [.github/workflows/build.yml](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/.github/workflows/build.yml#L163-L203) |
| Game version | `KSA_VERSION.txt` from the DLL repo becomes a job output | [.github/workflows/build.yml](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/.github/workflows/build.yml#L205-L216) |
| Mod version | `VersionPrefix` is grepped out of the csproj and prefixed with `v` | [.github/workflows/build.yml](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/.github/workflows/build.yml#L218-L224) |
| Release gate | On push to `main` only, compares that tag with the latest release's `tag_name`; a different or missing tag means release | [.github/workflows/build.yml](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/.github/workflows/build.yml#L226-L256) |
| Build | `dotnet build` with `/p:KSADir=deps/ksa/ /p:KSAModsDir=deps/`; `CopyOutput.targets` fills `Release/ShaderExtensions/` including `mod.toml` | [.github/workflows/build.yml](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/.github/workflows/build.yml#L258-L269) |
| Artifact | Named `ShaderExtensions-vX.Y.Z`, with `-<sha7>` for non-release builds and `-debug` for PRs; uploads `Release/` | [.github/workflows/build.yml](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/.github/workflows/build.yml#L271-L294) |
| GitHub release | Zips the artifact, creates a draft release (tag = version, body "Built for KSA version ..."), uploads the zip, publishes, captures the generated notes | [.github/workflows/build.yml](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/.github/workflows/build.yml#L296-L407) |
| SpaceDock | Logs in with `SPACEDOCKUSERNAME` / `SPACEDOCKAUTH` and posts the zip, changelog and game version to mod 4164 | [.github/workflows/build.yml](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/.github/workflows/build.yml#L409-L432) |

Release layout: the zip contains the `Release/` folder's content, so it unpacks to a single
`ShaderExtensions/` folder holding the DLL, its symbols and the generated `mod.toml`; the README tells
players to extract it into the game's `Content` folder and enable `ShaderExtensions` in
`manifest.toml`.[^sx-readme] [^sx-copyoutput]

## Lifecycle and timing

Not part of the game loop. A release happens exactly when a push to `main` carries a
`VersionPrefix` that differs from the latest release tag, so bumping the version in
[ShaderExtensions/ShaderExtensions.csproj](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderExtensions.csproj#L21) is the
release switch.[^sx-csproj]

## Key code

| Path | Role |
|---|---|
| [.github/workflows/build.yml](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/.github/workflows/build.yml#L1-L432) | The whole pipeline |
| [ShaderExtensions/ShaderExtensions.csproj](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderExtensions.csproj#L21-L24) | Version source |
| [ShaderExtensions/CopyOutput.targets](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/CopyOutput.targets#L1-L60) | Release folder and manifest |
| [.github/pull_request_template.md](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/.github/pull_request_template.md) | PR checklist |

## Snippets

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

```yaml
# .github/workflows/build.yml L261-L269
      - name: Build ShaderExtensions
        shell: bash
        run: |
          rm -rf Release/ShaderExtensions
          mkdir -p Release/ShaderExtensions

          deps_dir="$(pwd)/deps/"
          ksa_dir="$deps_dir/ksa/"
          dotnet build ShaderExtensions/ShaderExtensions.csproj -c "$CONFIGURATION" --no-restore /p:KSADir="$ksa_dir" /p:KSAModsDir="$deps_dir"
```

```yaml
# .github/workflows/build.yml L418-L432
        run: |
          printf '%s' "$RELEASE_CHANGELOG" > spacedock-changelog.md

          curl --form-string "username=$SPACEDOCK_USERNAME" \
               --form-string "password=$SPACEDOCK_PASSWORD" \
               -c ./cookies \
               https://spacedock.info/api/login

          curl -b ./cookies \
               --form-string "version=$RELEASE_VERSION" \
               -F "changelog=<spacedock-changelog.md" \
               --form-string "game-version=$KSA_VERSION" \
               --form-string "notify-followers=yes" \
               -F "zipball=@$ZIPBALL" \
               https://spacedock.info/api/mod/4164/update
```

## Game APIs touched

None.

## Pitfalls and version notes

- **Dependencies float.** The workflow always takes the *latest* StarMap and KittenExtensions
  releases, so a breaking dependency release changes what CI compiles against without a commit here.
- **Forks cannot build.** Pull requests from forks get no access to `KSADLLRepo`, `APP_ID` or
  `APP_PRIVATE_KEY`, so the private-DLL step fails for them.
- **The game version label comes from the DLL repo,** not from the DLLs; keeping `KSA_VERSION.txt`
  accurate is a manual duty of whoever updates that repository.
- **No tests run.** The pipeline compiles and packages only; the pull request template's checklist
  mentions `dotnet test`, but the solution contains no test project.[^sx-prtemplate]
- **The release body is the only game-version record.** "Built for KSA version ..." comes from
  `KSA_VERSION.txt`; the README states no tested build.

## See also

- [mod.toml generated by MSBuild with a StarMap dependency](generated-mod-toml.md)
- [Fetch a private DLL repository in CI with a GitHub App token](../../../howto/getting-started/build/build-in-ci-without-the-game-installed/build-in-ci-without-the-game-installed-1.md) - how-to article demonstrating this feature

[^sx-buildyml]: ShaderExtensions/.github/workflows/build.yml
[^sx-readme]: ShaderExtensions/README.md
[^sx-copyoutput]: ShaderExtensions/CopyOutput.targets
[^sx-csproj]: ShaderExtensions/ShaderExtensions.csproj
[^sx-prtemplate]: ShaderExtensions/.github/pull_request_template.md
