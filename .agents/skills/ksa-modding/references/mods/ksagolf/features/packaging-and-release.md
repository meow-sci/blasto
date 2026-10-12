---
type: Mod Feature
title: Packaging, deploy and release pipeline
description: One portable net10.0 archive for Windows and Linux with gates against shipping symbols or game DLLs, a deploy script that installs and registers the mod, and semantic-release plus SpaceDock publishing driven by Conventional Commits
mod: ksagolf
category: testing-and-tooling
techniques: [ci-checks, packaging]
hooks: []
lifecycle: []
game_types: [KSA.ModLibrary]
tags: [packaging, release, semantic-release, spacedock, ci, deploy, conventional-commits, cross-platform]
---

KSAGolf ships one zip, `KSAGolf-<version>.zip`, containing a `KSAGolf/` folder with the DLL,
`mod.toml`, the asset XML, `Meshes/`, `Textures/`, `Sounds/`, `VERSION`, `README.md` and `LICENSE`.
Because the mod is a portable `net10.0` assembly that only references the game's own DLLs, the same
archive works on Windows, Linux and Proton. Locally, `deploy.sh` installs the build into the KSA user
folder and registers it; in CI, semantic-release versions from commit messages, a second job builds
and attaches the archive, and a reusable workflow publishes it to SpaceDock.

## How it works

**Package (`tools/package.sh`).**[^golf-package]

| Gate | Why |
|---|---|
| Clean Release build with `-p:Version=` | No stale Debug artefact can ride along |
| Copy DLL, `mod.toml`, `KSAGolf*.xml`, and every asset folder discovered in the build output | A hand-listed folder set ships XML that references files not in the archive |
| Fail on any `.pdb` | Release has `DebugType none`; a player gains nothing from symbols[^golf-csproj] |
| Fail on any DLL other than `KSAGolf.dll` | Game assemblies are referenced `Private="false"` and must never be redistributed |
| Zip with Python's `shutil.make_archive` | `zip(1)` is missing on bare runners and some WSL images |

**Deploy (`tools/deploy.sh`).** Builds, runs `validate-parts.py --offline` and refuses to deploy on
failure, copies into `<user dir>/mods/KSAGolf` (detecting the "KSA holds the DLL open" case and
saying "close the game"), writes the `developer` marker that enables the bridge, removes a stale
`Assets/` copy, and appends a `[[mods]] id = "KSAGolf"` entry to `manifest.toml` with a backup.[^golf-deploy]
KSA only discovers mods that are in that manifest and enabled; it looks in `Content/<id>` and then
`<Documents>/My Games/Kitten Space Agency/mods/<id>`.[^ksa-modlibrary]

**Find the user folder (`tools/ksa-user-dir.sh`).** WSL `/mnt/c/Users/*/Documents/My Games/...`
first, then Linux candidates in the same order `Log.cs` uses; `KSA_USER_DIR` overrides.[^golf-userdir]
`run.sh` builds, deploys, launches `StarMap.exe` from WSL and follows the mod log.[^golf-run]

**Version and release.**

| Piece | Behaviour |
|---|---|
| `.releaserc.json` | Conventional Commits; `feat`, `fix`, `perf`, `build`, `revert` cut a patch; `docs`, `test`, `chore`, `ci`, `style`, `refactor` cut nothing; a minor is tagged by hand for milestones[^golf-releaserc] |
| `tools/set-version.sh` | semantic-release's `prepareCmd` writes the semver into the csproj `<Version>`[^golf-set-version] |
| `release.yml` | Job 1 creates a draft release and changelog; job 2 checks the assembly lock, builds, tests, validates parts, runs `checkmesh.py` over every glb, packages, uploads with `gh release upload`, appends install notes and publishes; then calls `spacedock.yml`[^golf-release-yml] |
| `spacedock.yml` | Logs in with repository secrets, claims compatibility with the `build` line of the release's `ksa-assemblies.lock`, and uploads; `spacedock-changelog.py` trims notes to SpaceDock's 10,000-character limit[^golf-spacedock-yml][^golf-spacedock-changelog] |
| `ci.yml` | `commits` (message lint over the push range), `tooling` (`check-all.sh`, no game needed), `build` (assemblies from a private mirror via deploy key; lock check, build, tests, part validation, API surface check, package, artifact)[^golf-ci] |
| Local hooks | `commit-msg` runs `check-commit-msg.sh`; `pre-push` runs `check-all.sh`, the same script CI runs[^golf-check-commit-msg][^golf-check-all] |
| `tools/doctor.sh` | Checks a fresh clone can build, test and run, and prints the fixing command for each failure[^golf-doctor] |

Assemblies for building are resolved by `Directory.Build.props` (`KSA_DLL_DIR`, `Import/`, a sibling
`ksa-game-assemblies`, then Windows, Steam-on-Linux, `~/Games`, `~`, WSL install paths).[^golf-buildprops]

## Lifecycle and timing

Build and release time only. The single runtime-visible effect is the `developer` marker file:
present after `deploy.sh`, absent from every packaged archive, read once by `Build.Developer` at
assembly load (see [File command bridge](file-command-bridge.md)).

## Key code

| Path | Role |
|---|---|
| [tools/package.sh](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/package.sh) | Release archive with symbol and foreign-DLL gates |
| [tools/deploy.sh](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/deploy.sh) | Local install, developer marker, manifest registration |
| [.github/workflows/release.yml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/.github/workflows/release.yml) | Draft, build, attach, publish |
| [.github/workflows/spacedock.yml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/.github/workflows/spacedock.yml) | SpaceDock upload |
| [.releaserc.json](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/.releaserc.json) | Release rules |
| [.github/workflows/ci.yml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/.github/workflows/ci.yml) | Commit, tooling and build jobs |

## Snippets

```bash
# tools/package.sh L54-L78
# Every asset folder the build produced, discovered rather than listed. A named list ships an
# archive whose XML declares files that are not in it, and an asset resolving to null is reported
# by nobody - see the same note in deploy.sh. The stray-DLL gate below is what keeps this honest.
for path in "$BUILD"/*/; do
    [[ -d "$path" ]] || continue
    dir="$(basename "$path")"

    mkdir -p "$STAGE/$dir"
    cp "$path"/* "$STAGE/$dir/"
done

# Refuse to ship debug symbols even if the build somehow produced them.
if compgen -G "$STAGE/*.pdb" >/dev/null || compgen -G "$BUILD/*.pdb" >/dev/null; then
    echo "error: a .pdb reached the release build -- check DebugType in the csproj" >&2
    exit 1
fi

# Game assemblies are referenced with Private=false and must never be redistributed. This is
# the last gate before an archive leaves the machine.
for stray in "$STAGE"/*.dll; do
    case "$(basename "$stray")" in
        KSAGolf.dll) ;;
        *) echo "error: $stray is not ours and must not ship" >&2; exit 1 ;;
    esac
done
```

```bash
# tools/deploy.sh L102-L114
# KSA discovers mods through manifest.toml, and StarMap walks the same list to find code mods.
# Dropping the folder in is not enough -- without an entry here nothing loads.
MANIFEST="$(dirname "$MODS_DIR")/manifest.toml"

if [[ ! -f "$MANIFEST" ]]; then
    echo "warning: no manifest at $MANIFEST -- register the mod by hand" >&2
elif grep -q '^[[:space:]]*id[[:space:]]*=[[:space:]]*"KSAGolf"' "$MANIFEST"; then
    echo "already registered in $(basename "$MANIFEST")"
else
    cp "$MANIFEST" "$MANIFEST.bak"
    printf '\n[[mods]]\nid = "KSAGolf"\nenabled = true\n' >> "$MANIFEST"
    echo "registered in $(basename "$MANIFEST") (backup: $(basename "$MANIFEST").bak)"
fi
```

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `ModLibrary.PrepareAll`, `LocalModsFolderPath`, `LocalManifestPath` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs) | Where the installed folder and the manifest entry are read |

## Pitfalls and version notes

- On Linux every file and folder name is case-sensitive; the README tells players to unzip rather
  than retype, and `validate-parts.py` compares paths against real directory listings.
- Release builds start the log at Info; the panel's Verbose log brings Debug back without a
  different build (see [Mod log file with rotation](mod-log-file-with-rotation.md)).
- The repo's notes list three release traps: branch protection on `main` blocks the release
  commit unless the token can bypass it, a shallow checkout makes every push look like a first
  release (`fetch-depth: 0`), and semantic-release continues from the newest visible tag.[^golf-claude-md]
- Without the private assembly mirror secret, CI still versions and drafts the release but cannot
  build the archive; `tools/publish-release.sh` exists to attach it from a machine with KSA.[^golf-release-yml]

## See also

- [Content-plus-code mod layout](content-and-code-mod-layout.md)
- [API surface record and assembly lock](api-surface-and-assembly-lock.md)
- [GLB authoring pipeline and mesh checks](glb-authoring-and-mesh-checks.md)
- [Headless simulation tests behind a Sim/Ksa boundary](headless-sim-tests.md)
- [File command bridge and MCP server](file-command-bridge.md)
- [Verify the release zip against the declared payload file by file](../../../howto/getting-started/build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-2.md) - how-to article demonstrating this feature

[^golf-package]: tools/package.sh
[^golf-deploy]: tools/deploy.sh
[^golf-run]: tools/run.sh
[^golf-userdir]: tools/ksa-user-dir.sh
[^golf-releaserc]: .releaserc.json
[^golf-release-yml]: .github/workflows/release.yml
[^golf-spacedock-yml]: .github/workflows/spacedock.yml
[^golf-spacedock-changelog]: tools/spacedock-changelog.py
[^golf-set-version]: tools/set-version.sh
[^golf-ci]: .github/workflows/ci.yml
[^golf-check-all]: tools/check-all.sh
[^golf-check-commit-msg]: tools/check-commit-msg.sh
[^golf-doctor]: tools/doctor.sh
[^golf-buildprops]: Directory.Build.props
[^golf-csproj]: src/KSAGolf/KSAGolf.csproj
[^golf-claude-md]: CLAUDE.md
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
