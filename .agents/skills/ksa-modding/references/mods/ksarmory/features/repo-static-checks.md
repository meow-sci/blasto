---
type: Mod Feature
title: One check script shared by the pre-push hook and CI
description: One check-all script runs about twenty textual and offline checks (Sim/ boundary, network egress, planet-agnostic code, XML, settings reachability, shaders, documented numbers, tracked artefacts) from a git pre-push hook and from CI, skipping game-dependent ones with a notice
mod: ksarmory
category: testing-and-tooling
techniques: [textual-guard, git-hooks, ci-without-game-assemblies]
hooks: []
lifecycle: []
game_types: []
tags: [ci, git-hooks, lint, shellcheck, glslang, conventional-commits]
---

KSArmory keeps one ordered list of checks in
[tools/check-all.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-all.sh)
and calls it from both the git `pre-push` hook and the CI "Tooling and assets" job, so "what I ran
before pushing" and "what CI runs" cannot drift. Most checks are textual or offline and need no
game; the ones that need KSA's assemblies (build, tests, API surface, lock) are skipped with a
notice when none are found, which lets forks without the private assemblies still get a
meaningful CI run.[^check-all][^ci]

## How it works

`run "<name>" <command>` records pass/fail per check and keeps going; `skip` records why a check
did not run; `--list` prints the plan; the summary distinguishes "passed" from "passed what it
could". `have_assemblies` mirrors the build's resolution (`KSA_DLL_DIR`, `Import/`, a sibling
`ksa-game-assemblies` checkout).[^check-all]

| Check | What it guards | Why it is textual or offline |
|---|---|---|
| `check-boundary.sh` | `Sim/` names no KSA type | Stand-in for the test-build guard where no assemblies exist |
| [tools/check-network.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-network.sh) | Only `Ksa/FeedbackClient.cs` may use networking types, it must have a `Send()` entry point, and it may make one request[^check-network] | Egress a player did not ask for is a firewall surprise |
| [tools/check-bodies.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-bodies.sh) | No planet named in a string literal; Earth-valued constants (sound speed, scale height, tropopause) only where an allow-list states why[^check-bodies] | A custom solar system should need a data line, not code |
| [tools/check-xml.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-xml.sh) | Every shipped asset XML parses[^check-xml] | KSA reports a malformed file as the part not existing |
| `validate-parts.py --offline` | Asset Ids, texture paths, launch geometry against the mesh | Bad Ids fail silently in game |
| [tools/check-tunables.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-tunables.py) | Every field of `SensorProfile`, `MunitionProfile`, `SystemConfig`, `OpticConfig`, `IcbmConfig`, `Config` is reached by a control under `Ksa/Ui`, or exempt with a reason[^check-tunables] | The panel enumerates controls by hand and the test project cannot see `Ksa/` |
| `pack-api.py --check`, `check-studies.py --check` | Generated records (pack API, study list) are current | Changes show up in the author's diff |
| [tools/check-shaders.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-shaders.sh) | The `.comp` shaders compile with Khronos `glslangValidator` against the install's shader library; skips without an install[^check-shaders] | KSA compiles mod shaders only at load, so an error otherwise costs a launch |
| [tools/check-comments.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-comments.sh) | No history in comments (hashes, "used to"), no XML docs on private members; reports comment ratios[^check-comments] | The mechanically detectable part of the comment rules |
| [tools/check-docs.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-docs.sh) | Numbers copied into prose (test count, API counts, layout table, KSA build) match what produces them[^check-docs] | A stale figure in docs is worse than none |
| `spacedock-changelog.py --check`, `listing.py --check` | Release notes and store listing are publishable | Release-time failures caught early |
| [tools/check-tracked.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-tracked.sh) | No `.dll`/`.pdb` or build artefacts tracked[^check-tracked] | A committed game DLL republishes copyrighted files |
| Model checks | `checkmesh.py` (z-fighting, zero-area UVs) on every atlas, palette and smoke sprite reproducible, thruster rings steer on their axial pair; drive sweep with `--with-sweep` | Need neither Blender nor the game |
| Build, test, API surface, lock | Only with assemblies | Skipped with a notice otherwise |

Commit messages are checked separately by
[tools/check-commit-msg.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-commit-msg.sh)
from both the `commit-msg` hook and a CI job, because semantic-release silently ignores a message
it cannot parse.[^check-commit-msg] [tools/install-hooks.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/install-hooks.sh)
points `core.hooksPath` at the committed `.githooks/`.[^install-hooks]

## Lifecycle and timing

Development time only: `pre-push` (local, bypass with `--no-verify`) and the CI tooling job. None
of this code runs in the game, so it has no frame position.[^pre-push]

## Key code

| Path | Role |
|---|---|
| [tools/check-all.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-all.sh#L46-L70) | `have_assemblies`, `run`, `skip` |
| [tools/check-all.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-all.sh#L93-L106) | The ordered check list |
| [.githooks/pre-push](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.githooks/pre-push) | Runs the same script before a push |
| [.github/workflows/ci.yml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.github/workflows/ci.yml#L52-L86) | Tooling job calling the same script |

## Snippets

```bash
# tools/check-all.sh L93-L106
run "Sim/ is free of KSA types"     ./tools/check-boundary.sh
run "No unasked-for network"        ./tools/check-network.sh
run "No planet named in code"       ./tools/check-bodies.sh
run "Part XML is well formed"       ./tools/check-xml.sh
run "Asset paths resolve"           ./tools/validate-parts.py --offline
run "Every setting is reachable"    ./tools/check-tunables.py
run "Pack API surface"              ./tools/pack-api.py --check
run "The study set is as recorded"  ./tools/check-studies.py --check
run "Shaders compile"               ./tools/check-shaders.sh
run "Comment rules"                 ./tools/check-comments.sh
run "Documented facts"              ./tools/check-docs.sh
run "Changelogs fit SpaceDock"      ./tools/spacedock-changelog.py --check
run "Store listing is publishable"  ./tools/listing.py --check
run "No artefacts tracked"          ./tools/check-tracked.sh
```

```bash
# tools/check-network.sh L37-L51
# The allowed file must still only send when asked. A request issued from a frame hook or a
# constructor is not something a player clicked.
if [[ -f "$ALLOWED" ]]; then
    if ! grep -q "public void Send(" "$ALLOWED"; then
        echo "  $ALLOWED no longer has a Send() entry point" >&2
        FAIL=1
    fi
    # One place that actually posts. More than one means a second path to audit.
    posts=$(grep -cE "PostAsJsonAsync|PostAsync|SendAsync|GetAsync|GetStringAsync" "$ALLOWED")
    if (( posts > 1 )); then
        echo "  $ALLOWED makes $posts requests; there should be one, from Send()" >&2
        FAIL=1
    fi
fi
```

## Game APIs touched

None. `check-shaders.sh` reads the game's shader library from a local install to resolve the
mod's include, but calls no KSA code.[^check-shaders]

## Pitfalls and version notes

- **Textual checks are heuristics.** They catch the easy mistakes; the real guards are the test
  build (for `Sim/`) and the synthetic-body tests (for planet-agnostic code), as the scripts say.
- **Skips are not passes.** Without assemblies the build, tests, API surface and lock checks do
  not run; the summary lists them so a green run is read correctly.[^check-all]
- **Include paths differ between compilers.** `glslangValidator` takes the Core shader directory
  as a search path while KSA's shaderc gets an absolute include written at load; the script writes
  its own relative header to match.[^check-shaders]
- GNU tools are assumed (`grep -P` elsewhere, `shellcheck` in CI).

## See also

- [Headless tests enforced by a game-free Sim/ source boundary](./sim-boundary-headless-tests.md)
- [Record the exact KSA API a mod binds to](./api-surface-tracking.md)
- [Pin the KSA build with a hashed assemblies lock file](./ksa-assemblies-lock.md)
- [Core shader library include](./core-shader-library-include.md)
- [In-game bug reports](./in-game-bug-reports.md)
- [Build, deploy, package and release](./build-deploy-package-release.md)
- [Check shaders offline: check-shaders.sh in a pre-push hook and CI, skipped without an install](../../../howto/shaders/glsl-workflow/check-mod-shaders-offline-with-glslang/check-mod-shaders-offline-with-glslang-1.md) - how-to article demonstrating this feature
- [Gather about twenty textual and offline checks into one script that skips with a notice](../../../howto/testing-and-tooling/offline-tests/run-repo-checks-from-a-pre-push-hook-and-ci/run-repo-checks-from-a-pre-push-hook-and-ci-1.md) - how-to article demonstrating this feature

[^check-all]: tools/check-all.sh
[^pre-push]: .githooks/pre-push
[^install-hooks]: tools/install-hooks.sh
[^check-network]: tools/check-network.sh
[^check-bodies]: tools/check-bodies.sh
[^check-tunables]: tools/check-tunables.py
[^check-xml]: tools/check-xml.sh
[^check-shaders]: tools/check-shaders.sh
[^check-comments]: tools/check-comments.sh
[^check-docs]: tools/check-docs.sh
[^check-tracked]: tools/check-tracked.sh
[^check-commit-msg]: tools/check-commit-msg.sh
[^ci]: .github/workflows/ci.yml
