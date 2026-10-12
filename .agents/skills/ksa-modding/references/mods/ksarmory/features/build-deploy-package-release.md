---
type: Mod Feature
title: Deploy into the KSA mods folder, package a clean archive and cut releases from commits
description: Scripts that build and install the mod with its manifest.toml registration and a developer marker, package an archive that refuses any DLL but the mod's own, and a semantic-release pipeline that drafts a GitHub release, attaches the archive and republishes it to SpaceDock
mod: ksarmory
category: testing-and-tooling
techniques: [deploy-script, manifest-registration, release-automation, conventional-commits]
hooks: []
lifecycle: []
game_types: []
tags: [deploy, packaging, release, semantic-release, spacedock, manifest-toml, borea]
---

KSArmory's developer loop and its release pipeline are both scripted end to end, and both encode
KSA-specific install facts a mod author otherwise learns by failure: a mod folder is not loaded
until `manifest.toml` lists it, the game holds the DLL open, asset folders must keep their exact
(case-sensitive) layout next to the XML, and the game's own assemblies must never ship. Releases
are cut by semantic-release from Conventional Commits, built in CI against the private assemblies
mirror, and published to GitHub and SpaceDock with the same bytes.

## How it works

The plain build is [tools/build.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/build.sh):
it sources `env.sh`, runs `check-assemblies.sh --game --quiet` so a KSA update is noticed on the first
build after it, and refuses any configuration other than `Debug` or `Release`, because MSBuild accepts
any string as a configuration and would report success into a stray output folder.[^build-sh]

### Developer install: `tools/deploy.sh`

1. Locate the KSA user directory (`KSA_MODS_DIR`, else
   [tools/ksa-user-dir.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/ksa-user-dir.sh):
   `KSA_USER_DIR`, WSL's `/mnt/c/Users/*/Documents/My Games/Kitten Space Agency`, then XDG and home
   candidates).[^user-dir]
2. `dotnet build` (through [tools/env.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/env.sh),
   which puts a .NET 10 SDK from `~/.dotnet` first on `PATH`), then `validate-parts.py --offline`
   before anything is copied, because a bad asset Id is a silent in-game failure.[^deploy][^env-sh]
3. Copy `KSArmory.dll` (failing with "KSA is still running" when the copy is refused and
   `StarMap.exe`/`KSA.exe` is found), `mod.toml`, `KSArmory*.xml`, and every subfolder of the build
   output (`Meshes/`, `Textures/`, `Sounds/`, `Shaders/`, `KSArmory/`), discovered rather than
   listed.[^deploy]
4. Write an empty `developer` file beside the DLL. `Build.Developer` checks for it at load and
   enables the bridge, the scenario runner and developer-only panel controls; the packaged archive
   never contains it.[^deploy][^build-cs]
5. Append `[[mods]] id = "KSArmory" enabled = true` to `manifest.toml` (one directory above
   `mods/`) if it is missing, keeping a `.bak`.[^deploy]

[tools/run.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/run.sh)
builds, deploys, launches `StarMap.exe` from its own directory (it reads `StarMapConfig.json`
relative to itself) and streams the mod's log; [tools/setup-starmap.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/setup-starmap.sh)
installs StarMap once; [tools/doctor.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/doctor.sh)
checks a machine and prints the fix for each missing prerequisite.[^run-sh][^setup-starmap][^doctor]

### Release archive: `tools/package.sh`

Clean Release build with `-p:Version`, stage the DLL, `mod.toml`, XML and every asset folder,
then two gates: no `.pdb` anywhere, and no `.dll` other than `KSArmory.dll` in the stage. A
`VERSION` file, README and LICENSE are added and the zip is made with Python's `shutil` (no
`zip(1)` dependency).[^package]

### Release pipeline

| Step | Where |
|---|---|
| Push to `main` runs semantic-release: `feat`, `fix`, `perf`, `build`, `revert` cut a patch; minors are tagged by hand; `docs`, `refactor`, `test`, `ci`, `chore`, `style` cut nothing | [.releaserc.json](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.releaserc.json)[^releaserc] |
| `@semantic-release/exec` runs `tools/set-version.sh <version>` to write `<Version>` into the csproj; `@semantic-release/git` commits it with `CHANGELOG.md`; `@semantic-release/github` creates a **draft** release | [tools/set-version.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/set-version.sh)[^set-version] |
| A second job checks out the private assemblies, verifies the lock, runs `test.sh --all`, validates parts and the mesh, packages, uploads the archive and only then publishes the draft (Borea's index treats a release with no archive as broken) | [.github/workflows/release.yml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.github/workflows/release.yml)[^release-yml] |
| A reusable workflow downloads that same archive and uploads it to SpaceDock with the changelog cut to SpaceDock's 10,000-character limit | [.github/workflows/spacedock.yml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.github/workflows/spacedock.yml), [tools/spacedock-changelog.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/spacedock-changelog.py)[^spacedock-yml][^spacedock-changelog] |
| Fallback when CI cannot build: attach an archive from a machine with KSA | [tools/publish-release.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/publish-release.sh)[^publish-release] |
| Store listing text for Borea and SpaceDock, printed to paste and `--check`ed | [tools/listing.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/listing.py)[^listing] |

## Lifecycle and timing

All of this is build and release time, outside the game. The one runtime consequence is the
`developer` marker: `Build.Developer` is a static initialiser read during the mod's
`[StarMapAllModsLoaded]` handler, so the bridge and scenario runner exist only on installs made by
`deploy.sh`.[^build-cs]

## Key code

| Path | Role |
|---|---|
| [tools/deploy.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/deploy.sh#L41-L115) | Validate, copy, mark developer, register in `manifest.toml` |
| [tools/package.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/package.sh#L40-L86) | Clean build, stage, `.pdb` and foreign-DLL gates, zip |
| [.releaserc.json](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.releaserc.json) | Release rules and plugins |
| [.github/workflows/release.yml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.github/workflows/release.yml) | Draft, build, attach, publish |
| [tools/ksa-user-dir.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/ksa-user-dir.sh) | Where KSA keeps `Logs/`, `mods/` and saves |

## Snippets

```bash
# tools/deploy.sh L68-L72
# Marks this as a developer's install, which shows the bridge, the scenario runner and the panel's
# developer controls (Build.Developer). package.sh stages from the build output, never from here, so
# the release archive cannot carry it.
: > "$TARGET/developer"
[[ -f "$OUT/KSArmory.pdb" ]] && cp "$OUT/KSArmory.pdb" "$TARGET/"
```

```bash
# tools/deploy.sh L103-L115
# KSA discovers mods through manifest.toml, and StarMap walks the same list to find code mods.
# Dropping the folder in is not enough -- without an entry here nothing loads.
MANIFEST="$(dirname "$MODS_DIR")/manifest.toml"

if [[ ! -f "$MANIFEST" ]]; then
    echo "warning: no manifest at $MANIFEST -- register the mod by hand" >&2
elif grep -q '^[[:space:]]*id[[:space:]]*=[[:space:]]*"KSArmory"' "$MANIFEST"; then
    echo "already registered in $(basename "$MANIFEST")"
else
    cp "$MANIFEST" "$MANIFEST.bak"
    printf '\n[[mods]]\nid = "KSArmory"\nenabled = true\n' >> "$MANIFEST"
    echo "registered in $(basename "$MANIFEST") (backup: $(basename "$MANIFEST").bak)"
fi
```

```bash
# tools/package.sh L62-L75
# Refuse to ship debug symbols even if the build somehow produced them.
if compgen -G "$STAGE/*.pdb" >/dev/null || compgen -G "$BUILD/*.pdb" >/dev/null; then
    echo "error: a .pdb reached the release build -- check DebugType in the csproj" >&2
    exit 1
fi

# Game assemblies are referenced with Private=false and must never be redistributed. This is
# the last gate before an archive leaves the machine.
for stray in "$STAGE"/*.dll; do
    case "$(basename "$stray")" in
        KSArmory.dll) ;;
        *) echo "error: $stray is not ours and must not ship" >&2; exit 1 ;;
    esac
done
```

## Game APIs touched

None. The scripts manipulate KSA's on-disk layout (`<user dir>/mods/<Mod>/`, `manifest.toml`,
`Logs/`) and StarMap's launcher, not game code.

## Pitfalls and version notes

- **Registration is mandatory.** A folder under `mods/` without a `manifest.toml` entry loads
  nothing and logs nothing; StarMap walks the same list for code mods.[^deploy][^readme]
- **Case matters on Linux.** The XML refers to `Meshes/` and `Textures/` by relative path; a case
  mismatch works on Windows and fails on Linux, so archives are unzipped, never retyped.[^readme]
- **The game locks the DLL.** Deploying while KSA runs fails with a bare permission error; the
  script detects the running process and says so.[^deploy]
- **A draft release is deliberate.** Publishing before the archive is attached makes Borea report
  the release broken.[^release-yml]
- **`feat` is a patch here.** Minor versions are tagged by hand; a `BREAKING CHANGE` footer still
  promotes `0.x` to `1.0.0`.[^readme]
- Release builds carry no `.pdb` and the log starts at INFO; the README's install section still
  lists a `KSArmoryCharacters.xml` that `mod.toml` no longer loads.[^package][^readme]

## See also

- [Own log file and developer switch](./own-log-file-and-developer-switch.md)
- [Pin the KSA build with a hashed assemblies lock file](./ksa-assemblies-lock.md)
- [One check script shared by the pre-push hook and CI](./repo-static-checks.md)
- [Weapon part definitions in XML](./weapon-part-definitions-xml.md)
- [Blender model pipeline and checks](./blender-model-pipeline-and-checks.md)
- [Share one payload list between the deploy copy and the release zip](../../../howto/getting-started/build/deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-3.md) - how-to article demonstrating this feature
- [A file-drop bridge with in/ and out/ folders, atomic replies and multi-frame closures](../../../howto/testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-1.md) - how-to article demonstrating this feature

[^deploy]: tools/deploy.sh
[^package]: tools/package.sh
[^user-dir]: tools/ksa-user-dir.sh
[^build-sh]: tools/build.sh
[^env-sh]: tools/env.sh
[^releaserc]: .releaserc.json
[^set-version]: tools/set-version.sh
[^release-yml]: .github/workflows/release.yml
[^spacedock-yml]: .github/workflows/spacedock.yml
[^spacedock-changelog]: tools/spacedock-changelog.py
[^publish-release]: tools/publish-release.sh
[^listing]: tools/listing.py
[^run-sh]: tools/run.sh
[^setup-starmap]: tools/setup-starmap.sh
[^doctor]: tools/doctor.sh
[^build-cs]: src/KSArmory/Ksa/Build.cs
[^readme]: README.md
