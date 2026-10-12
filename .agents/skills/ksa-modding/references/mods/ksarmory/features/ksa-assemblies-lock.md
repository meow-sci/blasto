---
type: Mod Feature
title: Pin the KSA build with a hashed assemblies lock file
description: A committed lock file of SHA-256 digests per referenced game DLL keeps local builds, CI's private assemblies mirror and the running game on one KSA build, and stamps that build into the mod for a runtime comparison
mod: ksarmory
category: testing-and-tooling
techniques: [assembly-lock-file, msbuild-property-functions, assembly-metadata, ci-private-dependency]
hooks: [starmap StarMapImmediateLoad]
lifecycle: [startup:immediate-load]
game_types: [KSA.Vehicle]
tags: [build, ci, assemblies, lock-file, msbuild, ksa-update, version-check]
---

KSA's assemblies are not redistributable and change meaning between pre-release builds, so a mod
built on one machine, compiled again in CI and run in a player's game can silently target three
different games. KSArmory closes that with one committed text file, `ksa-assemblies.lock`: a
`build` line naming the KSA build, then a SHA-256 digest per game DLL the projects reference.
Scripts compare whatever folder the build would use against it, CI refuses to compile against a
private mirror that drifted, and MSBuild stamps the `build` line into the mod so the running mod
can compare it with the game that loaded it.

## How it works

| Piece | What it does |
|---|---|
| [ksa-assemblies.lock](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/ksa-assemblies.lock) | `build 2026.10.7.5541` plus `sha256  Name.dll` lines. Only names and digests, so it is safe in a public repository[^lock] |
| [Directory.Build.props](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/Directory.Build.props#L22-L42) | Resolves `$(KsaDllDir)`, first match wins: `KSA_DLL_DIR`, `Import/`, a sibling `../ksa-game-assemblies/current/dll/`, then Windows, Steam-on-Linux, `~/Games`, `~/` and finally WSL's `/mnt/c` install; a `BeforeTargets="CoreCompile"` target errors with the list of places it looked[^build-props] |
| [src/KSArmory/KSArmory.csproj](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmory.csproj#L88-L140) | Every game reference is `$(KsaDllDir)<Name>.dll` with `Private="false"` (compile-time only, never copied into the output); a property function reads the lock's `build` line and emits `[assembly: AssemblyMetadata("KsaBuild", ...)]`[^csproj] |
| [tools/check-assemblies.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-assemblies.sh) | Derives the DLL list from the `$(KsaDllDir)` references in the csproj files, hashes them, diffs against the lock; `--update` rewrites it, `--game` checks the installed game instead of `Import/`[^check-assemblies] |
| [tools/sync-import.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/sync-import.sh) | Copies `KSA.dll`, `Brutal.*.dll` and friends from the install into the gitignored `Import/`, then runs the check and warns that CI is still on the old set[^sync-import] |
| [tools/sync-assemblies.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/sync-assemblies.sh) | Refreshes the private `ksa-game-assemblies` checkout that CI uses[^sync-assemblies] |
| [.github/workflows/ci.yml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.github/workflows/ci.yml#L88-L135) | Checks out the private assemblies repository with a read-only deploy key (`KSA_ASSEMBLIES_KEY`), runs `check-assemblies.sh` on it, then builds and tests with `KSA_DLL_DIR` pointing there; without the secret (forks) the build job prints a notice and only the tooling job runs[^ci] |
| [tools/check-ksa-version.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-ksa-version.sh) + [.github/workflows/ksa-version.yml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.github/workflows/ksa-version.yml) | Asks RocketWerkz's master server for the current build and compares it with the lock's `build` line; a daily scheduled job opens one issue when it moves (deduplicated by title)[^check-ksa-version][^ksa-version-workflow] |
| [.claude/skills/upgrade-ksa/SKILL.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.claude/skills/upgrade-ksa/SKILL.md) | The whole upgrade procedure as an agent skill: confirm, refresh binaries and the decompiled corpus, find what broke (including Core XML diffs), record the new build everywhere, fly it, push the private repository first[^upgrade-skill] |

The update loop the scripts print when they disagree is four commands: `sync-import.sh` (refresh
`Import/`), `sync-assemblies.sh ../ksa-game-assemblies` (refresh the private mirror, commit and push
there), `check-assemblies.sh --update` (re-record the lock), commit the lock here.

### The runtime half

The lock's `build` line reaches the running mod as assembly metadata. `Build.KsaBuild` reads that
attribute and `Build.KsaRunning` reads `typeof(KSA.Vehicle).Assembly.GetName().Version`, the game's
own assembly version.[^build-cs] `ReportDraft.GameIsSupported` compares the two after normalising a
trailing `.0`; an unknown value on either side counts as supported.[^report-draft] The entry class
logs both at load, and the in-game bug report buttons are hidden when they differ.[^entry]

## Lifecycle and timing

- **Build time.** Resolution, hashing and the CI gate run outside the game. Nothing here executes
  in the KSA frame.
- **`startup:immediate-load`.** `KSArmoryMod.OnImmediateLoad` (`[StarMapImmediateLoad]`, fired by
  StarMap's prefix on `Mod.PrepareSystems`) logs `built for KSA <lock build>, running <game build>`
  and whether reporting is on. `Build`'s properties are static initialisers, so they are computed on
  first touch here and never again; reading `Assembly.GetName()` is safe this early because KSA.dll
  is already loaded by the time any mod is.[^entry][^build-cs]

## Key code

| Path | Role |
|---|---|
| [ksa-assemblies.lock](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/ksa-assemblies.lock) | The pin: build line and digests |
| [tools/check-assemblies.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-assemblies.sh#L50-L103) | Resolution mirror, digest list derived from the csproj files, `--update` |
| [Directory.Build.props](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/Directory.Build.props#L22-L71) | Tiered `KsaDllDir` and the explicit error target |
| [src/KSArmory/KSArmory.csproj](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmory.csproj#L124-L140) | Lock build line stamped as `AssemblyMetadata("KsaBuild")` |
| [src/KSArmory/Ksa/Build.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Build.cs#L38-L78) | `KsaBuild` and `KsaRunning` at runtime |
| [src/KSArmory/Sim/ReportDraft.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ReportDraft.cs#L52-L57) | `GameIsSupported`, the comparison |
| [.github/workflows/ci.yml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.github/workflows/ci.yml#L88-L135) | Private mirror checkout and the lock gate |

## Snippets

```bash
# tools/check-assemblies.sh L65-L89
# The set the projects actually reference, from the csproj files, so this cannot fall behind.
mapfile -t NAMES < <(grep -ohP '(?<=\$\(KsaDllDir\))[A-Za-z0-9._]+(?=\.dll)' \
    "$REPO_ROOT"/src/KSArmory/*.csproj "$REPO_ROOT"/tests/*/*.csproj | sort -u)
[[ ${#NAMES[@]} -gt 0 ]] || { echo "error: no \$(KsaDllDir) references found in the csproj files" >&2; exit 1; }

# In --game mode, compare only what the install actually ships. StarMap.API.dll comes from the
# loader rather than the game, so demanding it here would report a KSA update on every run.
if (( GAME )); then
    present=()
    for name in "${NAMES[@]}"; do
        [[ -f "$DIR/$name.dll" ]] && present+=("$name")
    done
    NAMES=("${present[@]}")
fi

digests() {
    for name in "${NAMES[@]}"; do
        local path="$DIR/$name.dll"
        if [[ ! -f "$path" ]]; then
            echo "error: $name.dll missing from $DIR" >&2
            return 1
        fi
        printf '%s  %s.dll\n' "$(sha256sum "$path" | cut -d' ' -f1)" "$name"
    done
}
```

```xml
<!-- src/KSArmory/KSArmory.csproj L129-L140 -->
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

```csharp
// src/KSArmory/Ksa/Build.cs L50-L78
    private static string? ResolveKsaRunning()
    {
        try
        {
            return typeof(KSA.Vehicle).Assembly.GetName().Version?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveKsaBuild()
    {
        try
        {
            foreach (AssemblyMetadataAttribute meta in typeof(Build).Assembly
                         .GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (meta.Key == "KsaBuild") return meta.Value;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `KSA.Vehicle` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs)) | Any public type works as a handle on `KSA.dll`; its assembly version is the running game build[^ksa-vehicle] |

No game member is called at build time; the lock only hashes the files.

## Pitfalls and version notes

- **Two copies drift silently.** `Import/` (or the install) and the CI mirror are separate; a KSA
  update refreshes one. The lock is the only thing that makes the drift visible, which is why
  `sync-import.sh` re-runs the check and prints the remediation.[^sync-import]
- **`--game` only probes `KSA_DIR` and the two Windows/WSL paths**, unlike `Directory.Build.props`,
  which also knows Steam-on-Linux and home-directory installs. On native Linux or macOS set
  `KSA_DIR`.[^check-assemblies]
- **GNU tooling assumed.** The scripts use `grep -P`, `mapfile` and `sha256sum`; stock macOS bash
  3.2 and BSD grep lack them.
- **The `build` line is hand-maintained.** `--update` rewrites the digests but keeps the old build
  string and tells you to edit it; forgetting leaves the runtime comparison wrong.[^check-assemblies]
- **A matching hash says nothing about meaning.** Members keep their names and change behaviour
  between builds; the skill pairs the lock with the decompiled corpus and the API-surface record
  for that reason.[^upgrade-skill] At the pinned ref the lock says `2026.10.7.5541` while this bundle
  was checked against decompiled build `2026.10.10.5554`.
- **Never ship the game DLLs.** `Private="false"` keeps them out of the output; a second copy would
  load into the mod's own load context with non-matching type identities.[^csproj]

## See also

- [API surface tracking](./api-surface-tracking.md)
- [Build, deploy, package and release](./build-deploy-package-release.md)
- [Repository static checks](./repo-static-checks.md)
- [Own log file and developer switch](./own-log-file-and-developer-switch.md)
- [In-game bug reports](./in-game-bug-reports.md)
- [Pin the KSA build with a hashed assemblies lock, stamp it into the mod and watch for new builds](../../../howto/testing-and-tooling/game-updates/pin-the-game-build-with-a-hashed-lock/pin-the-game-build-with-a-hashed-lock.md) - how-to article demonstrating this feature
- [Upgrade by locking, decompiling both builds, diffing the API surface and updating the lock last](../../../howto/testing-and-tooling/game-updates/upgrade-a-mod-to-a-new-ksa-build/upgrade-a-mod-to-a-new-ksa-build-1.md) - how-to article demonstrating this feature

[^lock]: ksa-assemblies.lock
[^check-assemblies]: tools/check-assemblies.sh
[^build-props]: Directory.Build.props
[^csproj]: src/KSArmory/KSArmory.csproj
[^sync-import]: tools/sync-import.sh
[^sync-assemblies]: tools/sync-assemblies.sh
[^check-ksa-version]: tools/check-ksa-version.sh
[^ci]: .github/workflows/ci.yml
[^ksa-version-workflow]: .github/workflows/ksa-version.yml
[^upgrade-skill]: .claude/skills/upgrade-ksa/SKILL.md
[^build-cs]: src/KSArmory/Ksa/Build.cs
[^report-draft]: src/KSArmory/Sim/ReportDraft.cs
[^entry]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
