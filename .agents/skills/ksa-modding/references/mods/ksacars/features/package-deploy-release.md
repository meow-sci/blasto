---
type: Mod Feature
title: Build, deploy, package and release pipeline
description: Resolve the game's DLLs from a tiered search without committing them, deploy a developer build with a marker file after validating assets, package a symbol-free archive that refuses foreign DLLs, and release with semantic-release, a draft GitHub release and SpaceDock publishing
mod: ksacars
category: testing-and-tooling
techniques: [ci, packaging]
hooks: []
lifecycle: []
game_types: []
tags: [build, msbuild, deploy, packaging, release, semantic-release, spacedock, ci, wsl, starmap]
---

KSACars builds against KSA's own assemblies but must never commit or ship them, runs from WSL
against a Windows game, and releases automatically from Conventional Commits. Its build and release
scripts are a compact template for any StarMap mod with those constraints.[^ksacars-claude]

## How it works

**Finding the game's DLLs (`Directory.Build.props`).** `$(KsaDllDir)` is the first match of:
`KSA_DLL_DIR` (property or env var, what CI sets), `Import/` (filled by `tools/sync-import.sh`), a
sibling `../ksa-game-assemblies/current/dll/` checkout, then a game install (Windows `C:\Program
Files`, Steam on Linux in two locations, `~/Games`, `~`, and last WSL's `/mnt/c/...`). A
`KSACarsCheckKsaAssemblies` target runs before `CoreCompile` and fails with a readable list of
where it looked instead of a wall of "type not found" errors.[^ksacars-buildprops] The csproj
references every game DLL with `Private="false"` and, in Release, ships no debug information
(`DebugType none`).[^ksacars-csproj] `global.json` pins SDK 10.0 with `latestFeature` roll-forward;
`tools/env.sh` puts a .NET 10 SDK from `~/.dotnet` on `PATH` because the distro `dotnet` is 8.0.[^ksacars-globaljson][^ksacars-env]

**Developer loop.**

| Script | What it does |
|---|---|
| `tools/doctor.sh` | Can this machine build, test and run it? Start here[^ksacars-doctor] |
| `tools/build.sh` | Builds the mod with the right SDK and checks the install against the lock[^ksacars-build-sh] |
| `tools/deploy.sh [Debug]` | Builds, runs `validate-parts.py --offline` **before** copying (a bad asset Id is a silent in-game failure), copies the DLL, `mod.toml`, `KSACars*.xml` and every asset folder found in the build output to `<user dir>/mods/FastAndPurrious`, writes the `developer` marker, explains a "Permission denied" as "KSA still has the DLL open", and warns about a stale `Assets/` copy[^ksacars-deploy] |
| `tools/run.sh` | Build, deploy, launch `StarMap.exe` from WSL (it must run from its own folder because it reads `./StarMapConfig.json`), and tail `KSACars.log`; `--attach` only follows the log[^ksacars-run] |
| `tools/setup-starmap.sh` | One-off: download StarMap to the Windows side and write its config pointing at the KSA install[^ksacars-setupstarmap] |
| `tools/ksa-user-dir.sh` | Prints the KSA user folder (WSL or Linux), override with `KSA_USER_DIR`[^ksacars-userdir] |

**Packaging (`tools/package.sh`).** A clean Release build, staged into `dist/FastAndPurrious/`:
`KSACars.dll`, `mod.toml`, the XML, every asset folder discovered from the build output, `VERSION`,
`README.md`, `LICENSE`. It refuses to continue if any `.pdb` exists or any DLL other than
`KSACars.dll` reached the stage, then zips with Python's `shutil.make_archive` (no `zip(1)`
needed) into `FastAndPurrious-<version>.zip` and lists the contents. The archive never contains
the `developer` marker because it stages from the build output, not the deploy folder.[^ksacars-package]

**Releasing.** Work happens on `dev`; merging into `main` triggers `.github/workflows/release.yml`:
semantic-release (`.releaserc.json`) analyses Conventional Commits with custom rules (`feat`,
`fix`, `perf`, `build`, `revert` cut a **patch**; `docs`, `test`, `chore`, `ci`, `style`,
`refactor` cut nothing; minors are tagged by hand), writes `CHANGELOG.md`, runs
`tools/set-version.sh` to update `<Version>` in the csproj, commits, tags and creates a **draft**
GitHub release; a second job checks out the private assemblies with a deploy key, verifies them
against the lock, tests, validates parts, checks meshes, packages and attaches the archive, then
publishes the release.[^ksacars-releaserc][^ksacars-release-workflow][^ksacars-setversion]
`.github/workflows/spacedock.yml` uploads the attached archive to SpaceDock (claiming the build in
`ksa-assemblies.lock`) when its secrets are set, with `tools/spacedock-changelog.py` trimming the
changelog under SpaceDock's 10,000-character limit.[^ksacars-spacedock-workflow][^ksacars-spacedock-changelog]
`tools/publish-release.sh` is the manual counterpart.[^ksacars-publish]

**Install for players** is the zip unpacked into `<KSA user dir>/mods/`, a `[[mods]] id =
"FastAndPurrious"` entry in `manifest.toml`, and launching through StarMap; see
[one folder as both mods](./mod-folder-content-and-code-mod.md).[^ksacars-readme]

## Lifecycle and timing

Build, deploy and CI time only. The one runtime effect is the `developer` file that `deploy.sh`
writes, which `Build.Developer` reads when the assembly loads (see [mod log file](./mod-log-file.md)).

## Key code

| Path | Role |
|---|---|
| [Directory.Build.props](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/Directory.Build.props) | Tiered `KsaDllDir` resolution and the friendly error |
| [src/KSACars/KSACars.csproj](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACars.csproj) | `Private="false"` references, Release without symbols, content copy |
| [tools/deploy.sh](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/deploy.sh) | Validated developer install with marker |
| [tools/package.sh](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/package.sh) | Release archive with the `.pdb` and foreign-DLL gates |
| [.releaserc.json](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/.releaserc.json), [.github/workflows/release.yml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/.github/workflows/release.yml) | semantic-release config and pipeline |
| [.github/workflows/spacedock.yml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/.github/workflows/spacedock.yml) | SpaceDock publishing |

## Snippets

```xml
<!-- Directory.Build.props L23-L28 -->
  <PropertyGroup>
    <KsaDllDir Condition="'$(KsaDllDir)' == '' AND '$(KSA_DLL_DIR)' != ''">$([MSBuild]::EnsureTrailingSlash('$(KSA_DLL_DIR)'))</KsaDllDir>
    <KsaDllDir Condition="'$(KsaDllDir)' == '' AND Exists('$(MSBuildThisFileDirectory)Import/KSA.dll')">$(MSBuildThisFileDirectory)Import/</KsaDllDir>
    <KsaDllDir Condition="'$(KsaDllDir)' == '' AND Exists('$(MSBuildThisFileDirectory)../ksa-game-assemblies/current/dll/KSA.dll')">$(MSBuildThisFileDirectory)../ksa-game-assemblies/current/dll/</KsaDllDir>

    <!-- Windows -->
```

```bash
# tools/package.sh L62-L76
# Refuse to ship debug symbols even if the build somehow produced them.
if compgen -G "$STAGE/*.pdb" >/dev/null || compgen -G "$BUILD/*.pdb" >/dev/null; then
    echo "error: a .pdb reached the release build -- check DebugType in the csproj" >&2
    exit 1
fi

# Game assemblies are referenced with Private=false and must never be redistributed. This is
# the last gate before an archive leaves the machine.
for stray in "$STAGE"/*.dll; do
    case "$(basename "$stray")" in
        KSACars.dll) ;;
        *) echo "error: $stray is not ours and must not ship" >&2; exit 1 ;;
    esac
done
```

```bash
# tools/deploy.sh L64-L67
# Marks this as a developer's install, which starts the bridge and shows the panel's developer
# controls (Build.Developer). package.sh stages from the build output, never from here, so
# the release archive cannot carry it.
: > "$TARGET/developer"
```

## Game APIs touched

None. These are build and release scripts; the only game-facing facts are file locations (the
user `mods` folder and `manifest.toml`) and StarMap's launch requirements.

## Pitfalls and version notes

- **Never commit or publish the game's DLLs.** They live in a private repository CI checks out with
  a read-only deploy key; forks skip the build job with a notice.[^ksacars-claude]
- **Discover asset folders, do not list them.** Both `deploy.sh` and `package.sh` copy every folder
  in the build output, because a hand-maintained list ships XML that declares files the archive
  lacks, and a null asset is reported by nobody.
- **KSA locks the DLL while running**; deploying over a running game fails.
- **StarMap reads `StarMapConfig.json` relative to its working directory**, so launch it from its
  own folder (the README says the same to players).
- **Shallow checkouts break semantic-release** (every push looks like a first release; use
  `fetch-depth: 0`), branch protection on `main` must allow the release commit, and the first
  release needs a tag.[^ksacars-claude]
- The KSA build number is written in seven places; `tools/check-docs.sh` fails on prose that
  disagrees with the lock.

## See also

- [One folder as both a content mod and a code mod](./mod-folder-content-and-code-mod.md)
- [API surface pinning and KSA upgrade diffing](./api-surface-pinning-and-diff.md)
- [Headless simulation tests](./headless-sim-tests.md)
- [Art and audio pipeline tooling](./art-and-audio-pipeline-tooling.md)
- [Mod log file](./mod-log-file.md)
- [Verify the release zip against the declared payload file by file](../../../howto/getting-started/build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-2.md) - how-to article demonstrating this feature

[^ksacars-buildprops]: Directory.Build.props
[^ksacars-csproj]: src/KSACars/KSACars.csproj
[^ksacars-globaljson]: global.json
[^ksacars-build-sh]: tools/build.sh
[^ksacars-env]: tools/env.sh
[^ksacars-doctor]: tools/doctor.sh
[^ksacars-deploy]: tools/deploy.sh
[^ksacars-package]: tools/package.sh
[^ksacars-run]: tools/run.sh
[^ksacars-setupstarmap]: tools/setup-starmap.sh
[^ksacars-userdir]: tools/ksa-user-dir.sh
[^ksacars-setversion]: tools/set-version.sh
[^ksacars-publish]: tools/publish-release.sh
[^ksacars-spacedock-changelog]: tools/spacedock-changelog.py
[^ksacars-releaserc]: .releaserc.json
[^ksacars-release-workflow]: .github/workflows/release.yml
[^ksacars-spacedock-workflow]: .github/workflows/spacedock.yml
[^ksacars-claude]: CLAUDE.md
[^ksacars-readme]: README.md
