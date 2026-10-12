---
type: How-To
title: Pair check-all with a test script and a CI that builds against a private assemblies repo
description: Run the same check script in the pre-push hook and in CI, keep game-free checks in a job that needs no secret, and build and test against a private mirror of the game assemblies with a read-only deploy key that forks simply skip
category: testing-and-tooling
group: offline-tests
topic: run-repo-checks-from-a-pre-push-hook-and-ci
approach: 2
difficulty: intermediate
prerequisites: [bash, git, github-actions]
demonstrated_by:
  - mods/ksastructures/features/headless-tests-and-ci
  - mods/ksacars/features/headless-sim-tests
  - mods/ksagolf/features/headless-sim-tests
  - mods/ksarmory/features/sim-boundary-headless-tests
techniques: [git-hooks, ci-pipeline, deploy-key]
hooks: []
lifecycle: []
game_types: []
tags: [testing, ci, git-hooks, private-assemblies, deploy-key, github-actions, forks]
---

Outcome: a CI workflow with three jobs: commit-message checks, a tooling job that runs every check that does not need the game, and a build-and-test job that compiles against a private mirror of the game assemblies. A fork without the secret still gets a meaningful run, because the third job turns into a notice. The pre-push hook runs the same script as the tooling job.
Demonstrated by KSAStructures ([.github/workflows/ci.yml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/.github/workflows/ci.yml#L3-L69)) and, with identical structure, by KSACars, KSAGolf and KSArmory. Prerequisites: a private repository holding a copy of the game assemblies you are licensed to keep, and a GitHub deploy key on it.

## Background: how the game does it

KSA's assemblies are not redistributable. A hosted runner cannot download them, so `Import/` is gitignored; the C# tests need them too, because they reference `Brutal.Core.Numerics` for `double3` (see [Unit-test mod logic without launching the game](../unit-test-mod-logic-without-game/unit-test-mod-logic-without-game-1.md)). The workflow keeps a licensed copy in a private repository and checks it out with a read-only deploy key. Keeping your own licensed copy privately is fine; publishing it is not, which is why it is not in the mod's repository.
The mirror holds the same layout the corpus's assemblies repository uses: DLLs under `current/dll/`.

## Lifecycle and timing

Development and CI time only. The same script runs at `pre-push` locally and in the tooling job on a hosted runner; the build job runs where the secret exists.

## Steps

1. **Create `tools/check-all.sh`** with `run` and `skip` helpers and an ordered list, as in [approach 1](run-repo-checks-from-a-pre-push-hook-and-ci-1.md). The doc comment states the contract: this is the script the hook runs and the one CI calls, and checks needing the assemblies are skipped with a notice rather than failing:

   ```bash
   # tools/check-all.sh L1-L15
   #!/usr/bin/env bash
   #
   # Runs every check CI runs, in the same order, so a push is not the first time you find out.
   #
   #     ./tools/check-all.sh              # everything
   #     ./tools/check-all.sh --list       # name the checks and exit
   #
   # This is the script `.githooks/pre-push` runs and the one `ci.yml` calls, so the two cannot
   # drift. That is the same one-script-two-callers discipline check-commit-msg.sh uses: one list,
   # so "before you open a PR" and "what CI runs" cannot mean different things.
   #
   # Checks needing the game assemblies are skipped with a notice rather than failing: a contributor
   # without KSA can still run most of this, and saying which ones were skipped is the difference
   # between "it passed" and "it passed what it could".
   set -uo pipefail
   ```

2. **Order game-free checks first and game-dependent ones last.** KSAStructures's list: Sim boundary, part XML, asset paths, comment rules, shader compiles, tracked artefacts, mesh checks (z-fighting and degenerate UVs, only when an atlas exists), then build, test and the lock check only if the assemblies are found:

   ```bash
   # tools/check-all.sh L79-L101
   run "Sim/ is free of KSA types"     ./tools/check-boundary.sh
   run "Part XML is well formed"       ./tools/check-xml.sh
   run "Asset paths resolve"           ./tools/validate-parts.py --offline
   run "Comment rules"                 ./tools/check-comments.sh
   run "Shader compiles"               ./tools/check-shaders.sh
   run "No artefacts tracked"          ./tools/check-tracked.sh

   # With the proximity advisory off and the two hard checks on: a mesh authored in the Blender UI has
   # no modelling skin, so the advisory reports every deliberate panel step rather than a defect.
   if (( LIST )) || (( ${#DRAWN_ATLASES[@]} )); then
       run "Meshes have no z-fighting or degenerate UVs" \
           ./tools/model/checkmesh.py "${DRAWN_ATLASES[@]}" --near-max 0
   else
       skip "Meshes have no z-fighting or degenerate UVs" "no atlas in src/KSAStructures/Meshes/"
   fi

   if (( LIST )) || have_assemblies; then
       run "Build"                 ./tools/build.sh
       run "Test"                  ./tools/test.sh
       run "Assemblies match lock" ./tools/check-assemblies.sh
   else
       skip "Build and test" "KSA assemblies not found - see tools/sync-import.sh"
   fi
   ```

3. **Add the pre-push hook.** It is five lines: find the script, run it, and on failure say what to do. `--no-verify` is the right move when you push a branch precisely to see what CI says:

   ```
   // .githooks/pre-push L1-L20
   #!/usr/bin/env bash
   #
   # Runs every check CI runs, before the push rather than after it.
   #
   # Bypass with `git push --no-verify` -- which is the right move when you are pushing a branch
   # precisely to see what CI says about it.
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
   ```

4. **Explain the workflow's shape at the top of `ci.yml`.** Say why the assemblies are private and what happens without the secret, so the next maintainer does not "fix" the skip:

   ```yaml
   # .github/workflows/ci.yml L3-L14
   # Why this looks the way it does
   # ------------------------------
   # Building this mod needs KSA's own assemblies (KSA.dll, Brutal.Core.Numerics.dll and friends).
   # They are not redistributable, so Import/ is gitignored and a GitHub-hosted runner has no way
   # to obtain them. The C# tests need them too - they reference Brutal.Core.Numerics for double3.
   #
   # They are kept in the private repository LaurensDeV/ksa-game-assemblies and checked out here
   # with a read-only deploy key (the KSA_ASSEMBLIES_KEY secret). Keeping your own licensed copy
   # privately is fine; publishing it is not, which is why it is not in this repository.
   #
   # Without that secret - a fork, say - the build job is skipped and the tooling job still runs,
   # so CI stays meaningful rather than failing on something a contributor cannot fix.
   ```

5. **Make the tooling job run the script and nothing else.** It needs only Python and shellcheck; the assemblies are absent, so the build half skips with a notice:

   ```yaml
   # .github/workflows/ci.yml L51-L69
     tooling:
       name: Tooling and assets
       runs-on: ubuntu-latest
       steps:
         - uses: actions/checkout@v4

         - uses: actions/setup-python@v5
           with:
             python-version: "3.12"

         - name: Install shellcheck
           run: sudo apt-get update -qq && sudo apt-get install -y -qq shellcheck

         # One script, two callers -- the same discipline check-commit-msg.sh uses. Everything the
         # tooling job checks lives in tools/check-all.sh, so a contributor's pre-push hook and this
         # job cannot disagree about what "the checks" are. The assemblies are absent here, so the
         # build half is skipped with a notice and covered by the build job below.
         - name: Everything that does not need the game
           run: ./tools/check-all.sh
   ```

6. **Guard the build job on the secret.** `secrets.*` cannot be used directly in a job-level `if:`, so surface it through `env` first; then check out the private repository with the deploy key, verify the digests against the committed lock, and build:

   ```yaml
   # .github/workflows/ci.yml L71-L111
     build:
       name: Build and test
       runs-on: ubuntu-latest
       # secrets.* cannot be used directly in a job-level if:, so surface it through env first.
       env:
         HAVE_KEY: ${{ secrets.KSA_ASSEMBLIES_KEY != '' }}
       steps:
         - uses: actions/checkout@v4

         - name: Check out the KSA assemblies
           if: env.HAVE_KEY == 'true'
           uses: actions/checkout@v4
           with:
             repository: LaurensDeV/ksa-game-assemblies
             path: .ksa-assemblies
             ssh-key: ${{ secrets.KSA_ASSEMBLIES_KEY }}
             persist-credentials: false

         - name: Skip notice
           if: env.HAVE_KEY != 'true'
           run: |
             echo "::notice::KSA_ASSEMBLIES_KEY is not set, so the mod cannot be compiled here."
             echo "The tooling job still ran. See CLAUDE.md for why the assemblies are not public."

         - name: Assemblies match the lock
           # Guards against CI compiling against a different KSA build from the developer who
           # wrote the code. ksa-assemblies.lock records the expected digests; a KSA update that
           # refreshed one side and not the other fails here instead of silently.
           if: env.HAVE_KEY == 'true'
           run: ./tools/check-assemblies.sh .ksa-assemblies/current/dll

         - uses: actions/setup-dotnet@v4
           if: env.HAVE_KEY == 'true'
           with:
             dotnet-version: "10.0.x"

         - name: Build
           if: env.HAVE_KEY == 'true'
           env:
             KSA_DLL_DIR: ${{ github.workspace }}/.ksa-assemblies/current/dll
           run: dotnet build src/KSAStructures/KSAStructures.csproj -c Release --nologo
   ```

7. **Run tests through the script**, so the configuration cannot differ from what a push is gated on, then validate parts and package the mod as an artifact:

   ```yaml
   # .github/workflows/ci.yml L113-L135
         - name: Test
           if: env.HAVE_KEY == 'true'
           env:
             KSA_DLL_DIR: ${{ github.workspace }}/.ksa-assemblies/current/dll
           # Through the script, so the configuration cannot differ from what a push is gated on.
           run: ./tools/test.sh

         - name: Validate parts
           if: env.HAVE_KEY == 'true'
           run: ./tools/validate-parts.py --offline

         - name: Package
           if: env.HAVE_KEY == 'true'
           env:
             KSA_DLL_DIR: ${{ github.workspace }}/.ksa-assemblies/current/dll
           run: ./tools/package.sh

         - uses: actions/upload-artifact@v4
           if: env.HAVE_KEY == 'true'
           with:
             name: KSAStructures-mod
             path: dist/*.zip
             if-no-files-found: error
   ```

8. **Add a textual boundary check next to the compile-time one** (`tools/check-boundary.sh`) so the tooling job, which has no assemblies, still enforces the Sim split ([tools/check-boundary.sh](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/check-boundary.sh)).

## Complete example

| File | Role |
|---|---|
| [tools/check-all.sh](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/check-all.sh) | The one list of checks |
| [.githooks/pre-push](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/.githooks/pre-push) | Local gate |
| [.github/workflows/ci.yml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/.github/workflows/ci.yml) | Commit messages, tooling, build and test, package |
| [tools/check-assemblies.sh](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/check-assemblies.sh) | Digest check against the committed lock |
| [tools/test.sh](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/test.sh) | Test runner shared by hook and CI |

## Variations and gotchas

- **Forks.** Without `KSA_ASSEMBLIES_KEY` the build job prints a notice and the tooling job still runs; do not make the missing secret a failure.
- **Lock mismatch.** CI compiling against a different KSA build from the developer is a silent hazard; the lock check fails the job instead (see [Pin the KSA build with a hashed lock](../../game-updates/pin-the-game-build-with-a-hashed-lock/overview.md)).
- **Persist no credentials.** The checkout step uses `persist-credentials: false` so the deploy key does not stay in the workspace.
- **Read-only key.** The deploy key should only read the private mirror.
- **Game updates.** Update the private mirror first, then the lock, then the mod (see [Upgrade a mod to a new KSA build](../../game-updates/upgrade-a-mod-to-a-new-ksa-build/overview.md)).

## See also

- [Overview](overview.md); [approach 1](run-repo-checks-from-a-pre-push-hook-and-ci-1.md), [approach 3](run-repo-checks-from-a-pre-push-hook-and-ci-3.md)
- [Build in CI without the game installed](../../../getting-started/build/build-in-ci-without-the-game-installed/overview.md), [Package and publish a mod release](../../../getting-started/build/package-and-publish-a-mod-release/overview.md)
- Mod features: [KSAStructures headless tests and CI](../../../../mods/ksastructures/features/headless-tests-and-ci.md), [KSACars](../../../../mods/ksacars/features/headless-sim-tests.md), [KSAGolf](../../../../mods/ksagolf/features/headless-sim-tests.md), [KSArmory](../../../../mods/ksarmory/features/sim-boundary-headless-tests.md)
