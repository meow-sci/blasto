---
type: How-To
title: Gather about twenty textual and offline checks into one script that skips with a notice
description: Write a single check-all script of ordered run and skip steps, call it from the git pre-push hook and from the CI tooling job, and add small textual guards for the rules a compiler cannot see
category: testing-and-tooling
group: offline-tests
topic: run-repo-checks-from-a-pre-push-hook-and-ci
approach: 1
difficulty: intermediate
prerequisites: [bash, git, github-actions]
demonstrated_by: [mods/ksarmory/features/repo-static-checks, mods/ksarmory/features/sim-boundary-headless-tests]
techniques: [git-hooks, textual-guards, ci-pipeline]
hooks: []
lifecycle: []
game_types: []
tags: [testing, ci, git-hooks, pre-push, textual-guards, check-all, tooling]
---

Outcome: one `tools/check-all.sh` that names every check in order, runs the ones this machine can run, skips the rest with a printed reason, and is the single script that both `.githooks/pre-push` and the CI tooling job execute. "What I ran before pushing" and "what CI runs" can then never drift.
Demonstrated by KSArmory ([tools/check-all.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-all.sh#L93-L106)). Prerequisites: a repository with a `tools/` folder of small scripts, and a GitHub Actions workflow.

## Background: how the game does it

The game does not take part: this is repository hygiene. Two facts about KSA shape it. The game assemblies are not redistributable, so a CI runner or a fork cannot build the mod, which is why game-dependent steps skip with a notice instead of failing; and KSA reports a malformed asset as "the part does not exist" and compiles mod shaders only at load, so an error found by a script is cheaper than an error found by a launch. Related topics cover [building in CI without the game](../../../getting-started/build/build-in-ci-without-the-game-installed/overview.md) and [checking shaders offline](../../../shaders/glsl-workflow/check-mod-shaders-offline-with-glslang/overview.md).

## Lifecycle and timing

Development and CI time only; nothing runs in a game frame. The hook is `pre-push` (local; bypass with `git push --no-verify`) and the CI job runs the same script on a hosted runner.

## Steps

1. **Write `run` and `skip` helpers.** `run` prints a heading, runs the command and records pass or fail without stopping; `skip` records why a check did not run. `have_assemblies` mirrors the build's resolution of the game DLLs so the same condition decides every game-dependent step:

   ```bash
   # tools/check-all.sh L46-L70
   have_assemblies() {
       [[ -n "${KSA_DLL_DIR:-}" && -f "${KSA_DLL_DIR:-}/KSA.dll" ]] && return 0
       [[ -f "$REPO_ROOT/Import/KSA.dll" ]] && return 0
       [[ -f "$REPO_ROOT/../ksa-game-assemblies/current/dll/KSA.dll" ]] && return 0
       return 1
   }

   run() {
       local name="$1"; shift
       if (( LIST )); then printf '  %s\n' "$name"; return 0; fi

       printf '\n\033[1m== %s\033[0m\n' "$name"
       if "$@"; then
           PASSED=$((PASSED + 1))
       else
           FAILED+=("$name")
       fi
   }

   skip() {
       if (( LIST )); then printf '  %s (conditional)\n' "$1"; return 0; fi
       printf '\n\033[1m== %s\033[0m\n' "$1"
       echo "  skipped: $2"
       SKIPPED+=("$1")
   }
   ```

2. **List the checks in one place.** Order them cheap first, and put the ones that need no game above the assemblies gate on purpose. KSArmory's list covers the Sim boundary, network egress, planet-agnostic code, XML, asset paths, settings reachability, generated records, shaders, comment rules, documented numbers, changelogs, store listing and tracked artefacts:

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

3. **Gate game-dependent steps on the assemblies and say so:**

   ```bash
   # tools/check-all.sh L143-L152
   if (( LIST )) || have_assemblies; then
       run "Build"                 ./tools/build.sh
       # The guards. The ten flight-model studies are left out for the reason the drive sweep above
       # is: they assert no behaviour, and CI runs them in a step of its own.
       run "Test"                  ./tools/test.sh
       run "KSA API surface"       ./tools/api-surface.sh --check
       run "Assemblies match lock" ./tools/check-assemblies.sh
   else
       skip "Build, test and API surface" "KSA assemblies not found - see tools/sync-import.sh"
   fi
   ```

4. **Finish with an honest summary.** A skipped check is reported by name, and the final line says what was not run, because "it passed" and "it passed what it could" are different statements:

   ```bash
   # tools/check-all.sh L154-L171
   (( LIST )) && exit 0

   echo
   if (( ${#SKIPPED[@]} )); then
       echo "skipped ${#SKIPPED[@]}: ${SKIPPED[*]}"
   fi

   if (( ${#FAILED[@]} )); then
       printf '\033[31mFAILED %d of %d:\033[0m %s\n' \
           "${#FAILED[@]}" "$((PASSED + ${#FAILED[@]}))" "${FAILED[*]}" >&2
       exit 1
   fi

   if (( WITH_SWEEP )); then
       printf '\033[32mall %d checks passed\033[0m\n' "$PASSED"
   else
       printf '\033[32mall %d checks passed\033[0m (drive sweep not run; --with-sweep)\n' "$PASSED"
   fi
   ```

5. **Write the textual guards.** Each is a few lines of grep or Python that enforce a rule the compiler cannot. The egress guard proves the only network code is the player's explicit Send button; it also justifies choosing a file bridge over a socket (see [Drive a running game through a file bridge](../../in-game-testing/drive-a-running-game-through-a-file-bridge/overview.md)):

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

   The planet-agnostic guard keeps a custom solar system a data change: no planet named in a string literal, and Earth-valued constants only where an allow-list states why ([tools/check-bodies.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-bodies.sh#L1-L12)). The tunables guard checks that every field of the config types is reached by a control under the UI folder, or exempt with a reason ([tools/check-tunables.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-tunables.py)). The documented-numbers guard checks that figures copied into prose (test count, API counts, KSA build) match what produces them ([tools/check-docs.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-docs.sh)).

6. **Call the same script from the hook.** The hook refuses the push with a message that says what to do next, and leaves the slow drive sweep out because the defect it finds is only reachable by changing the model:

   ```
   // .githooks/pre-push L10-L26
   # where that one belongs.
   set -uo pipefail

   REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

   if [[ ! -x "$REPO_ROOT/tools/check-all.sh" ]]; then
       echo "pre-push: tools/check-all.sh missing or not executable; skipping" >&2
       exit 0
   fi

   if "$REPO_ROOT/tools/check-all.sh"; then
       exit 0
   fi

   echo >&2
   echo "pre-push: refusing to push - the checks above are what CI will run." >&2
   echo "          re-run them with ./tools/check-all.sh, or push anyway with --no-verify" >&2
   ```

7. **Install the hooks with one command** that points git at the tracked directory, so a hook added later arrives with a pull:

   ```bash
   # tools/install-hooks.sh L12-L17

   REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

   git -C "$REPO_ROOT" config core.hooksPath .githooks
   chmod +x "$REPO_ROOT"/.githooks/*
   ```

8. **Call it from CI as well.** The tooling job installs the language runtimes and `shellcheck`, then runs the script; slower checks that the hook leaves out run as separate steps:

   ```yaml
   # .github/workflows/ci.yml L75-L86
         # One script, two callers -- the same discipline check-commit-msg.sh uses. Everything the
         # tooling job checks lives in tools/check-all.sh, so a contributor's pre-push hook and this
         # job cannot disagree about what "the checks" are. It skips nothing here: shellcheck, Pillow
         # and numpy are installed above, and the assemblies are absent, so the build half is skipped
         # with a notice and covered by the build job below.
         - name: Everything that does not need the game
           run: ./tools/check-all.sh

         # Separate because it is ~43 s against the rest of the set's ~7 s, which is why the
         # pre-push hook leaves it out. Here there is no reason not to run it.
         - name: Nothing adrift, standing proud, or passing through anything
           run: ./tools/model/checkswept.py --step 2 --bearing-step 5
   ```

## Complete example

| File | Role |
|---|---|
| [tools/check-all.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-all.sh) | `run`, `skip`, the ordered list and the summary |
| [.githooks/pre-push](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.githooks/pre-push) | Runs the same script before a push |
| [tools/install-hooks.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/install-hooks.sh) | Points `core.hooksPath` at `.githooks/` |
| [.github/workflows/ci.yml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.github/workflows/ci.yml#L51-L86) | Tooling job calling the same script |
| [tools/check-network.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-network.sh) | Egress guard |
| [tools/check-bodies.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-bodies.sh) | Planet-agnostic code guard |
| [tools/check-boundary.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-boundary.sh) | Textual stand-in for the Sim boundary compile guard |

## Variations and gotchas

- **Textual checks are heuristics.** They keep the easy mistakes out; the real guards are the test build (for the Sim boundary) and synthetic-body tests (for planet-agnostic code).
- **`set -u` without `-e`.** The script must keep going after a failure, so it uses `set -uo pipefail` and records failures itself.
- **Commit messages need their own check.** KSArmory checks them from a `commit-msg` hook and a CI job because the release tool silently ignores a message it cannot parse ([tools/check-commit-msg.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-commit-msg.sh)).
- **Keep it fast.** The hook's script is about 45 s, almost all of it the test suite; anything slower belongs in its own CI step.
- **Game updates.** The textual checks do not read a KSA build; the ones that do (API surface, assemblies lock) are covered in [Pin the KSA build with a hashed lock](../../game-updates/pin-the-game-build-with-a-hashed-lock/overview.md).

## See also

- [Overview](overview.md); [approach 2](run-repo-checks-from-a-pre-push-hook-and-ci-2.md)
- [Unit-test mod logic without launching the game](../unit-test-mod-logic-without-game/overview.md), [Validate asset XML references](../../../assets/validation/validate-asset-xml-references/overview.md), [Build in CI without the game installed](../../../getting-started/build/build-in-ci-without-the-game-installed/overview.md)
- Mod features: [KSArmory repo static checks](../../../../mods/ksarmory/features/repo-static-checks.md), [KSArmory sim-boundary headless tests](../../../../mods/ksarmory/features/sim-boundary-headless-tests.md)
