---
type: How-To
title: Pin the KSA build with a hashed assemblies lock, stamp it into the mod and watch for new builds
description: Commit SHA-256 digests of the referenced game DLLs plus the build number so local builds, a private CI mirror and the running game agree on one KSA build, and poll the version endpoint on a schedule to learn of a new build early
category: testing-and-tooling
group: game-updates
topic: pin-the-game-build-with-a-hashed-lock
difficulty: intermediate
prerequisites: [bash, msbuild, github-actions]
demonstrated_by:
  - mods/ksarmory/features/ksa-assemblies-lock
  - mods/ksagolf/features/api-surface-and-assembly-lock
  - mods/ksacars/features/api-surface-pinning-and-diff
  - mods/ksastructures/features/headless-tests-and-ci
techniques: [msbuild, assembly-metadata, ci-pipeline, version-check]
hooks: [starmap StarMapImmediateLoad]
lifecycle: [startup:immediate-load]
game_types: [KSA.Vehicle, KSA.VersionInfo]
tags: [game-update, lock-file, sha256, msbuild, ci, version-check, assemblies]
---

Outcome: one committed text file, `ksa-assemblies.lock`, that names the KSA build and holds a digest for each game DLL your projects reference; scripts that compare whatever folder the build resolved with it; a CI job that refuses to compile against a private mirror that has drifted; the build number stamped into the mod so the running mod can compare it with the game that loaded it; and a daily job that tells you when a new build is published.
Demonstrated by KSArmory ([ksa-assemblies.lock](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/ksa-assemblies.lock#L1-L5)), with the same scripts in KSACars, KSAGolf and KSAStructures. Prerequisites: the assembly resolution from [Reference the game assemblies from a mod project](../../../getting-started/build/reference-game-assemblies-from-a-mod-project/overview.md).

## Background: how the game does it

KSA's assemblies are not redistributable and change meaning between pre-release builds, so a mod built on one machine, compiled again in CI and run in a player's game can silently target three different games. The game publishes its own version in two places this pattern uses. The running game's version is its assembly version (`typeof(KSA.Vehicle).Assembly.GetName().Version`). The published version comes from RocketWerkz's master server: `VersionInfo` holds `MASTER_URL` and `VERSION_URI = MASTER_URL + "/version"` ([current/decomp/KSA/VersionInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionInfo.cs#L25-L27)), and the response deserialises into `VersionMetaInfo` with `Version` and `Url` ([current/decomp/KSA/VersionMetaInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionMetaInfo.cs)). The game itself compares the master's version with its own to show an update popup ([current/decomp/KSA/VersionInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionInfo.cs#L58-L80)).

## Lifecycle and timing

Resolution, hashing and the CI gate run outside the game. At run time the entry class logs the stamped and the running builds in its `[StarMapImmediateLoad]` method, which StarMap calls per mod before systems and asset bundles load (`startup:immediate-load`); the static properties are computed on first touch there. `Assembly.GetName()` is safe this early because KSA.dll is already loaded by the time any mod is. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Create the lock file.** A `build` line then one SHA-256 digest per referenced DLL. It holds only names and digests, so it is safe in a public repository:

   ```
   # ksa-assemblies.lock L1-L8
   # KSA assemblies this repository is known to build against.
   # Regenerate with ./tools/check-assemblies.sh <dir> --update after a KSA update,
   # and refresh the private ksa-game-assemblies repo to match - see CLAUDE.md.
   build 2026.10.7.5541
   fd77b88724f4104440df0cf979a851d35eec75ea3a7e86297d04abe47c71aff6  0Harmony.dll
   e0a1528ded4ef8efcb8002b99704cb8614b7db840d2cb39b5e45eda52d63bc68  BepuUtilities.dll
   29323e1ab5aa142f9367733a0a5d45aeccaec04d7adbbed60f91360b544f633d  Brutal.Concurrency.dll
   4a375812c157d1af7e03b7c97df04ae8cf43d02a152a76643cccdb7d0e9b8392  Brutal.Core.Common.dll
   ```

2. **Resolve the assemblies folder in one place, first match wins.** `KSA_DLL_DIR` (what CI uses), `Import/` (filled by a sync script), a sibling `ksa-game-assemblies` checkout, then the game install; a target errors with the list of places it looked when none exists:

   ```xml
   <!-- Directory.Build.props L24-L26 -->
       <KsaDllDir Condition="'$(KsaDllDir)' == '' AND '$(KSA_DLL_DIR)' != ''">$([MSBuild]::EnsureTrailingSlash('$(KSA_DLL_DIR)'))</KsaDllDir>
       <KsaDllDir Condition="'$(KsaDllDir)' == '' AND Exists('$(MSBuildThisFileDirectory)Import/KSA.dll')">$(MSBuildThisFileDirectory)Import/</KsaDllDir>
       <KsaDllDir Condition="'$(KsaDllDir)' == '' AND Exists('$(MSBuildThisFileDirectory)../ksa-game-assemblies/current/dll/KSA.dll')">$(MSBuildThisFileDirectory)../ksa-game-assemblies/current/dll/</KsaDllDir>
   ```

3. **Derive the list of DLLs to hash from the csproj files**, so the lock cannot fall behind the references. In `--game` mode compare only what the install ships (`StarMap.API.dll` comes from the loader, not the game):

   ```bash
   # tools/check-assemblies.sh L65-L78
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
   ```

4. **Compare, and print remediation.** Hash each DLL, diff against the lock's digest lines, and on mismatch print what was expected and found plus the update commands. `--update` rewrites the lock from a folder; `--game` checks the installed game instead of `Import/`, because `Import/` is a copy and cannot detect a game update:

   ```bash
   # tools/check-assemblies.sh L80-L103
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

   if (( UPDATE )); then
       build="$(grep -oP '(?<=^build ).*' "$LOCK" 2>/dev/null || echo unknown)"
       {
           echo "# KSA assemblies this repository is known to build against."
           echo "# Regenerate with ./tools/check-assemblies.sh <dir> --update after a KSA update,"
           echo "# and refresh the private ksa-game-assemblies repo to match - see CLAUDE.md."
           echo "build $build"
           digests
       } > "$LOCK"
       echo "recorded ${#NAMES[@]} assemblies from $DIR"
       echo "update the 'build' line in $(basename "$LOCK") if the game version changed"
       exit 0
   fi
   ```

5. **Make CI check the private mirror before compiling against it.** The step is guarded on the secret so forks skip it:

   ```yaml
   # .github/workflows/ci.yml L112-L117
         - name: Assemblies match the lock
           # Guards against CI compiling against a different KSA build from the developer who
           # wrote the code. ksa-assemblies.lock records the expected digests; a KSA update that
           # refreshed one side and not the other fails here instead of silently.
           if: env.HAVE_KEY == 'true'
           run: ./tools/check-assemblies.sh .ksa-assemblies/current/dll
   ```

6. **Stamp the lock's build into the assembly.** A property function reads the `build` line and emits `AssemblyMetadata("KsaBuild", ...)`; every game reference is `$(KsaDllDir)<Name>.dll` with `Private="false"` (compile-time only, never copied):

```xml
<!-- src/KSArmory/KSArmory.csproj L125-L140 -->
  <!--
    The KSA build this was compiled against, taken from ksa-assemblies.lock rather than written
    down a second time. The panel compares it to the build actually running.
  -->
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

7. **Compare at run time.** Read the attribute and the game's assembly version, normalise a trailing `.0`, and treat an unknown value on either side as supported so missing information never removes a feature. KSArmory uses the result to hide its in-game bug-report buttons when the build differs, and logs both builds at load:

   ```csharp
   // src/KSArmory/Ksa/Build.cs L47-L76
       /// </summary>
       public static string? KsaRunning { get; } = ResolveKsaRunning();

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
   ```

   ```csharp
   // src/KSArmory/Sim/ReportDraft.cs L52-L57
       public static bool GameIsSupported(string? builtFor, string? running)
       {
           if (string.IsNullOrWhiteSpace(builtFor) || string.IsNullOrWhiteSpace(running)) return true;

           return Normalise(builtFor) == Normalise(running);
       }
   ```

   ```csharp
   // src/KSArmory/Ksa/KSArmoryMod.cs L142-L146
           // panel offers reporting. Without it, buttons missing from the panel is a mystery from
           // the outside and unanswerable from a log.
           bool supported = ReportDraft.GameIsSupported(Build.KsaBuild, Build.KsaRunning);
           Log.Info($"KSArmory {Build.Version} built for KSA {Build.KsaBuild ?? "?"}, "
                    + $"running {Build.KsaRunning ?? "?"} - reporting {(supported ? "on" : "off")}");
   ```

8. **Learn of a new build before it is installed.** A script asks the same master server the game does, compares the response with the lock's `build` line and exits non-zero if they differ; an unreachable server is "not a reason to fail". It is deliberately not wired into the build, because a network call per build is slow and the first thing anyone would disable:

   ```bash
   # tools/check-ksa-version.sh L25-L54
   pinned="$(grep -oP '(?<=^build ).*' "$LOCK" 2>/dev/null || true)"
   [[ -n "$pinned" ]] || { echo "error: no 'build' line in $LOCK" >&2; exit 1; }

   # A master server that is down, moved, or slow is not a reason to fail a build or a scheduled
   # job. Report and succeed.
   if ! response="$(curl -sS --max-time 20 "$ENDPOINT" 2>&1)"; then
       (( QUIET )) || echo "could not reach $ENDPOINT; skipping" >&2
       exit 0
   fi

   latest="$(printf '%s' "$response" | python3 -c '
   import json, sys
   try:
       print(json.load(sys.stdin).get("Version", ""))
   except Exception:
       pass
   ' 2>/dev/null || true)"

   if [[ -z "$latest" ]]; then
       (( QUIET )) || {
           echo "could not read a version from $ENDPOINT; skipping" >&2
           printf '%s\n' "$response" | head -5 >&2
       }
       exit 0
   fi

   if [[ "$latest" == "$pinned" ]]; then
       (( QUIET )) || echo "KSA $latest — up to date with ksa-assemblies.lock"
       exit 0
   fi
   ```

9. **Run it daily and open one issue.** A scheduled workflow turns "the answer changed" into an issue (deduplicated by title) rather than a red build on an unrelated commit:

   ```yaml
   # .github/workflows/ksa-version.yml L15-L22
   on:
     schedule:
       - cron: "17 7 * * *"    # daily, a few minutes off the hour so as not to pile onto :00
     workflow_dispatch:

   permissions:
     contents: read
     issues: write
   ```

   ```yaml
   # .github/workflows/ksa-version.yml L51-L66
         - name: Open an issue
           if: steps.check.outputs.moved == 'true'
           env:
             GH_TOKEN: ${{ github.token }}
             PUBLISHED: ${{ steps.check.outputs.published }}
             PINNED: ${{ steps.check.outputs.pinned }}
           run: |
             title="KSA $PUBLISHED is out (pinned to $PINNED)"

             # The schedule fires daily, so without this it would file the same issue every
             # morning until someone acted on it. This is what makes a daily cadence tolerable.
             if gh issue list --state open --search "$title in:title" --json title \
                 -q '.[].title' | grep -Fxq "$title"; then
               echo "already tracked: $title"
               exit 0
             fi
   ```

10. **The update loop** the scripts print is four commands: refresh `Import/` (`sync-import.sh`, which also runs the check and warns that CI is still on the old set), refresh the private mirror and push there (`sync-assemblies.sh`), re-record the lock (`check-assemblies.sh --update`), then edit the `build` line and commit. The full procedure is in [Upgrade a mod to a new KSA build](../upgrade-a-mod-to-a-new-ksa-build/overview.md).

## Complete example

| File | Role |
|---|---|
| [ksa-assemblies.lock](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/ksa-assemblies.lock) | The pin: build line and digests |
| [tools/check-assemblies.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-assemblies.sh) | Resolution mirror, digest list from the csproj files, `--update`, `--game` |
| [Directory.Build.props](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/Directory.Build.props#L22-L42) | Tiered `KsaDllDir` resolution |
| [src/KSArmory/KSArmory.csproj](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmory.csproj#L125-L140) | Lock build line stamped as `AssemblyMetadata("KsaBuild")` |
| [src/KSArmory/Ksa/Build.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Build.cs#L38-L78) | `KsaBuild` and `KsaRunning` at run time |
| [src/KSArmory/Sim/ReportDraft.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ReportDraft.cs#L52-L57) | `GameIsSupported`, the comparison |
| [tools/check-ksa-version.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-ksa-version.sh) | Compare the published build with the lock |
| [.github/workflows/ksa-version.yml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.github/workflows/ksa-version.yml) | Daily watch that opens an issue |

## Variations and gotchas

- **Digest drift is the point.** If CI and a developer hash different DLLs for the same build number, the check fails; that is the mismatch you want to find.
- **The version endpoint is the game's own.** It is a plain HTTP GET against RocketWerkz's master server (overridable with `KSA_VERSION_URL`); expect it to change or disappear, and keep the check non-fatal.
- **The lock is a promise, not a proof.** It says which build you tested against, not that the mod works on it; run the tests ([Upgrade a mod to a new KSA build](../upgrade-a-mod-to-a-new-ksa-build/overview.md)).
- **Companion record.** The generated API surface ([Track the game API your mod uses](../track-the-game-api-your-mod-uses/overview.md)) is the same kind of committed safe artefact; the lock answers "which game", the surface answers "which members".
- **Game updates.** This topic is the update plumbing: when the build moves, the lock, the private mirror, `Import/` and the stamp must all move in one commit series.

## See also

- [Overview](overview.md)
- [Upgrade a mod to a new KSA build](../upgrade-a-mod-to-a-new-ksa-build/overview.md), [Track the game API your mod uses](../track-the-game-api-your-mod-uses/overview.md), [Keep a searchable decompiled copy of the game next to your mod](../decompile-the-game-for-reference/overview.md)
- [Build in CI without the game installed](../../../getting-started/build/build-in-ci-without-the-game-installed/overview.md), [Check the game version](../../../getting-started/lifecycle/check-the-game-version/overview.md)
- Mod features: [KSArmory KSA assemblies lock](../../../../mods/ksarmory/features/ksa-assemblies-lock.md), [KSAGolf API surface and assembly lock](../../../../mods/ksagolf/features/api-surface-and-assembly-lock.md), [KSACars API surface pinning and diff](../../../../mods/ksacars/features/api-surface-pinning-and-diff.md)
