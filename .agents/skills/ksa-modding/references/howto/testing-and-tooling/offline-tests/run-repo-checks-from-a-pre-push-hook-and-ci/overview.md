---
type: Game Feature
title: Run one check script from the pre-push hook and from CI
description: Gather boundary checks and other textual guards, tests, API-surface and lock checks, shader compiles, asset validators and CI smoke checks of bundled native libraries and guest images into one check-all script that a git hook and the CI workflow both call
category: testing-and-tooling
group: offline-tests
topic: run-repo-checks-from-a-pre-push-hook-and-ci
demonstrated_by:
  - mods/ksarmory/features/repo-static-checks
  - mods/ksarmory/features/sim-boundary-headless-tests
  - mods/ksastructures/features/headless-tests-and-ci
  - mods/ksacars/features/headless-sim-tests
  - mods/ksagolf/features/headless-sim-tests
  - mods/ksa-advancedflightcomputer/features/native-solver-build-and-abi-check
  - mods/gatos/features/alpine-guest-image
lifecycle: [thread:background]
game_types: []
tags: [testing, ci, git-hooks, pre-push, check-all, textual-guards, github-actions]
---

A mod repository accumulates rules that no compiler enforces: the pure folder must not name KSA types, only one file may touch the network, no planet may be named in code, asset ids must resolve, shaders must compile, documented numbers must match reality, no game DLL may be committed. Each rule is a small script. The practice these mods share is to list them all in one `check-all` script and to make both the git `pre-push` hook and the CI workflow call that script, so a contributor finds out before pushing what CI would say.

Game assemblies are not redistributable, so any step that needs them skips with a printed notice when they are absent. A fork or a hosted runner without the private mirror still gets a meaningful run, and the summary says which checks were skipped.

## Engine background

The game is not involved at run time; two engine-adjacent facts explain why these checks exist.

| Fact | Consequence |
|---|---|
| KSA reports a malformed or unresolvable asset as "the part does not exist", and compiles mod shaders only at load | Offline validators for XML, asset ids and GLSL turn a launch-time failure into a script failure |
| The game assemblies cannot be published | CI needs a private mirror (or skips), and a lock file of digests keeps local and CI builds on the same KSA build |
| Frame and lifecycle behaviour | Not applicable to these scripts; see [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for what the code under test does at run time |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: about twenty textual and offline checks with skip-with-notice](run-repo-checks-from-a-pre-push-hook-and-ci-1.md) | ksarmory ([tools/check-all.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-all.sh#L93-L106)) | `git-hooks` | none (repo and CI time) | Catches easy mistakes cheaply; textual checks are heuristics |
| [2: check-all plus a test script and a CI that builds against a private assemblies repo](run-repo-checks-from-a-pre-push-hook-and-ci-2.md) | ksastructures ([.github/workflows/ci.yml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/.github/workflows/ci.yml#L3-L69)) | `git-hooks`, `ci-pipeline` | none (repo and CI time) | Real build and tests in CI; needs a private mirror and a deploy key, and forks skip the build |
| [3: cargo and zig cc builds plus a C layout probe in CI](run-repo-checks-from-a-pre-push-hook-and-ci-3.md) | ksa-advancedflightcomputer ([build/ci/test-guidance.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/ci/test-guidance.ps1#L127-L142)) | `github-actions` | `thread:background` | Proves native ABI per platform with pinned compilers; one job per runtime and a heavier pipeline |
| [4: guest image build with a smoke boot, release and fetch by pin](run-repo-checks-from-a-pre-push-hook-and-ci-4.md) | gatos ([guest/build-image.sh](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/build-image.sh#L323-L362)) | `ci-pipeline` | none (repo and CI time) | A large artefact is proven bootable before publishing; needs QEMU and KVM or slow TCG |

Design rules the four share: one script with two callers; cheap, game-free checks first; skip with a reason instead of failing when a prerequisite is absent; fixed expected counts where a removed check would otherwise pass unnoticed; and keep the slow checks (drive sweeps, studies) out of the push loop but in CI.

## See also

- [Unit-test mod logic without launching the game](../unit-test-mod-logic-without-game/overview.md), [Build a mod without game references](../../../getting-started/build/build-a-mod-without-game-references/overview.md), [Build in CI without the game installed](../../../getting-started/build/build-in-ci-without-the-game-installed/overview.md)
- [Check mod shaders offline with glslang](../../../shaders/glsl-workflow/check-mod-shaders-offline-with-glslang/overview.md), [Validate asset XML references](../../../assets/validation/validate-asset-xml-references/overview.md), [Ship native libraries with a mod](../../../getting-started/dependencies/ship-native-libraries-with-a-mod/overview.md), [Package and publish a mod release](../../../getting-started/build/package-and-publish-a-mod-release/overview.md)
- [Pin the KSA build with a hashed lock](../../game-updates/pin-the-game-build-with-a-hashed-lock/overview.md)
- Mod features: [KSArmory repo static checks](../../../../mods/ksarmory/features/repo-static-checks.md), [KSAStructures headless tests and CI](../../../../mods/ksastructures/features/headless-tests-and-ci.md), [AFC native solver build and ABI check](../../../../mods/ksa-advancedflightcomputer/features/native-solver-build-and-abi-check.md), [gatOS Alpine guest image](../../../../mods/gatos/features/alpine-guest-image.md)
