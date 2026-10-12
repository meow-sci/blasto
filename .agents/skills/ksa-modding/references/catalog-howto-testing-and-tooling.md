# Testing and tooling

Testing and tooling for KSA mods - game-free unit tests, headless rigs and in-game HeadlessHarness suites, patch and solver tests, developer file bridges, screenshots and visual regression, unattended scenarios, live tuning and perf measurement, API-surface tracking, pinned builds and upgrade workflows.

## In game testing

Testing and measuring inside a running game: file command bridges, screenshots and visual regression, unattended scenarios, live tuning and per-frame cost.

### [Drive a running game from scripts through a file-drop command bridge](howto/testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/overview.md)

Poll a folder for JSON command files from the frame postfix so a shell script, a test runner or an agent can pause, step, load saves and script gameplay in a live game, with the bridge kept out of player installs

- [A file-drop bridge with in/ and out/ folders, atomic replies and multi-frame closures](howto/testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-1.md) — intermediate
- [Get and set every config field by reflection, and script gameplay as composed commands](howto/testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-2.md) — intermediate
- [A lean bridge with shader hot reload, held camera shots and a UI flag instead of work in the UI pass](howto/testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-3.md) — advanced
- [Gate developer features on a custom key in the player's manifest.toml entry](howto/testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-4.md) — intermediate
- [Keep dumps and perf scopes out of release builds with a DEBUG flag and a runtime switch](howto/testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-5.md) — beginner

### [Tweak layout and animation constants live in the running game](howto/testing-and-tooling/in-game-testing/live-tweak-constants-in-game/overview.md)

Reflect public static fields into ImGui sliders, persist only the values that differ from the defaults and dump the result as C# source to paste back into the code

- [Edit static constants with reflected sliders, keep only the overrides and copy them back as source](howto/testing-and-tooling/in-game-testing/live-tweak-constants-in-game/live-tweak-constants-in-game.md) — intermediate

### [Measure what a mod costs per frame, throttle expensive work and warm up hot code](howto/testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/overview.md)

Wrap hot code in zero-cost-when-off scopes with periodic reports, read a mod pass's GPU cost from the game profiler with a pinned camera, throttle per-frame work on wall-clock intervals and state versions and pre-compile hot paths on a background thread

- [Time hot code with a DEBUG-only readonly ref struct scope and a periodic report](howto/testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/measure-and-budget-mod-performance-1.md) — beginner
- [Always-on single-writer counters shown in a status window](howto/testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/measure-and-budget-mod-performance-2.md) — intermediate
- [Nested budget spans with top-level totals, the worst frame and rate-limited overrun warnings](howto/testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/measure-and-budget-mod-performance-3.md) — intermediate
- [Read a mod pass's GPU cost from KSA's profiler while a pinned camera keeps the runs comparable](howto/testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/measure-and-budget-mod-performance-4.md) — advanced
- [A 45 ms wall-clock throttle with a state-version check for hover picks](howto/testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/measure-and-budget-mod-performance-5.md) — intermediate
- [Pre-compile hot code at load with a Task.Run PrepareMethod sweep](howto/testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/measure-and-budget-mod-performance-6.md) — intermediate

### [Fly scripted scenarios unattended, report PASS or FAIL and A/B test variants](howto/testing-and-tooling/in-game-testing/run-unattended-scenarios-and-report-a-verdict/overview.md)

Run a developer-only state machine from a request file at launch, load a save and play a scripted engagement, log tagged lines that a shell harness turns into an exit code, and compare variants with rank statistics

- [A request file, a state machine, wall-clock and sim-time budgets and tagged SCENARIO lines](howto/testing-and-tooling/in-game-testing/run-unattended-scenarios-and-report-a-verdict/run-unattended-scenarios-and-report-a-verdict-1.md) — advanced
- [Bridge commands that load a save and step an exact number of simulated seconds](howto/testing-and-tooling/in-game-testing/run-unattended-scenarios-and-report-a-verdict/run-unattended-scenarios-and-report-a-verdict-2.md) — intermediate
- [A seeded, interleaved overnight batch with rank-sum verdicts, and within-run variant splits](howto/testing-and-tooling/in-game-testing/run-unattended-scenarios-and-report-a-verdict/run-unattended-scenarios-and-report-a-verdict-3.md) — advanced

### [Take clean screenshots from code, with a composed camera shot held for the capture](howto/testing-and-tooling/in-game-testing/take-screenshots-from-code/overview.md)

Ask the game's own ScreenshotCapture for a UI-free frame, collect the file with a manifest of the instant, and hold the main view at a scripted pose until the warm-up frames settle

- [Reflect Program._screenshotCapture once and call Request with a warm-up, then move the PNG beside a manifest](howto/testing-and-tooling/in-game-testing/take-screenshots-from-code/take-screenshots-from-code-1.md) — intermediate
- [Compose a shot with a held SceneCamera, a 24-frame countdown and a manifest of the craft's screen position](howto/testing-and-tooling/in-game-testing/take-screenshots-from-code/take-screenshots-from-code-2.md) — advanced
- [A SceneCamera shot relative to the craft or a building, held by the dev bridge](howto/testing-and-tooling/in-game-testing/take-screenshots-from-code/take-screenshots-from-code-3.md) — advanced
- [Hold the main view from the bridge as one camera claimant, optionally orbiting on player time](howto/testing-and-tooling/in-game-testing/take-screenshots-from-code/take-screenshots-from-code-4.md) — advanced
- [A capture button that only sets a flag, with player bundles of frames, state and log tail](howto/testing-and-tooling/in-game-testing/take-screenshots-from-code/take-screenshots-from-code-5.md) — intermediate

### [Visual regression testing for a KSA mod](howto/testing-and-tooling/in-game-testing/visual-regression-for-mods/overview.md)

Capture frames at the same simulated instant, then diff variants, build contact sheets and animations and compute a temporal standard-deviation map to separate a real change from render noise

- [Same-instant variant diffs, frame series and temporal noise maps with a vis.py toolbox](howto/testing-and-tooling/in-game-testing/visual-regression-for-mods/visual-regression-for-mods-1.md) — intermediate
- [Return contact sheets, GIFs and temporal-noise maps inline through an MCP server](howto/testing-and-tooling/in-game-testing/visual-regression-for-mods/visual-regression-for-mods-2.md) — advanced

## Offline tests

Tests that run without a live game session: game-free unit tests, headless simulation rigs, HeadlessHarness suites, patch tests, solver harnesses and one check script for hooks and CI.

### [Test a mod against the real game with HeadlessHarness](howto/testing-and-tooling/offline-tests/headless-test-harness-for-game-mods/overview.md)

Ship tests as a second StarMap mod that depends on HeadlessHarness and the mod under test so the real simulation runs without a GPU, swap the clock for simulation time and use the game's own private routines as the oracle

- [Build a consumer mod with its own mod.toml, InternalsVisibleTo and a test base class](howto/testing-and-tooling/offline-tests/headless-test-harness-for-game-mods/headless-test-harness-for-game-mods-1.md) — advanced
- [Demand equality with the game's private routines using a small oracle suite](howto/testing-and-tooling/offline-tests/headless-test-harness-for-game-mods/headless-test-harness-for-game-mods-2.md) — advanced

### [Run the mod's whole simulation in a headless rig behind a sim boundary](howto/testing-and-tooling/offline-tests/run-mod-simulation-in-a-headless-rig/overview.md)

Drive vehicles, rounds or balls through the mod's own step function in a plain test with a rig that supplies mass, inertia and a terrain stand-in, and vary the step to expose phase and epoch errors

- [Fly a rigid-body rig through the mod's own drive step with the engine's mass and inertia](howto/testing-and-tooling/offline-tests/run-mod-simulation-in-a-headless-rig/run-mod-simulation-in-a-headless-rig-1.md) — advanced
- [Share one driver seam between the game and the headless rig and test with varying steps](howto/testing-and-tooling/offline-tests/run-mod-simulation-in-a-headless-rig/run-mod-simulation-in-a-headless-rig-2.md) — advanced
- [Test ball and contact physics with an epoch bookkeeping layer and missed-frame invariance](howto/testing-and-tooling/offline-tests/run-mod-simulation-in-a-headless-rig/run-mod-simulation-in-a-headless-rig-3.md) — advanced

### [Run one check script from the pre-push hook and from CI](howto/testing-and-tooling/offline-tests/run-repo-checks-from-a-pre-push-hook-and-ci/overview.md)

Gather boundary checks and other textual guards, tests, API-surface and lock checks, shader compiles, asset validators and CI smoke checks of bundled native libraries and guest images into one check-all script that a git hook and the CI workflow both call

- [Gather about twenty textual and offline checks into one script that skips with a notice](howto/testing-and-tooling/offline-tests/run-repo-checks-from-a-pre-push-hook-and-ci/run-repo-checks-from-a-pre-push-hook-and-ci-1.md) — intermediate
- [Pair check-all with a test script and a CI that builds against a private assemblies repo](howto/testing-and-tooling/offline-tests/run-repo-checks-from-a-pre-push-hook-and-ci/run-repo-checks-from-a-pre-push-hook-and-ci-2.md) — intermediate
- [Build native solver libraries with pinned toolchains and prove their ABI in per-platform CI jobs](howto/testing-and-tooling/offline-tests/run-repo-checks-from-a-pre-push-hook-and-ci/run-repo-checks-from-a-pre-push-hook-and-ci-3.md) — advanced
- [Build a guest image with a smoke boot in CI and fetch it by pinned release with checksums](howto/testing-and-tooling/offline-tests/run-repo-checks-from-a-pre-push-hook-and-ci/run-repo-checks-from-a-pre-push-hook-and-ci-4.md) — advanced

### [Test a solver or controller offline: closed loop, purity and replay](howto/testing-and-tooling/offline-tests/test-a-solver-or-controller-offline/overview.md)

Build a console harness that solves, integrates dynamics with injected dispersions and re-solves, check determinism and no cross-talk before threading a solve, and replay the exact problem the live game fed the solver

- [Run a closed-loop harness with injected dispersions and check the solve is a pure function of its inputs](howto/testing-and-tooling/offline-tests/test-a-solver-or-controller-offline/test-a-solver-or-controller-offline-1.md) — advanced
- [Dump the exact problem before the solve and replay it offline with full tracing](howto/testing-and-tooling/offline-tests/test-a-solver-or-controller-offline/test-a-solver-or-controller-offline-2.md) — advanced

### [Test Harmony patches without running the game](howto/testing-and-tooling/offline-tests/test-harmony-patches-without-the-game/overview.md)

Apply the real production patch code to stand-in KSA types in a console test, run a patch-apply check over a new game build offline, or install a test-scoped Harmony owner inside a headless run

- [Apply the real patches to stand-in KSA types in a console test](howto/testing-and-tooling/offline-tests/test-harmony-patches-without-the-game/test-harmony-patches-without-the-game-1.md) — advanced
- [Check that every patch class still applies to a new game build with a throwaway console app](howto/testing-and-tooling/offline-tests/test-harmony-patches-without-the-game/test-harmony-patches-without-the-game-2.md) — advanced
- [Run transpilers over the game's real IL and scope test patches to a disposable Harmony owner](howto/testing-and-tooling/offline-tests/test-harmony-patches-without-the-game/test-harmony-patches-without-the-game-3.md) — advanced

### [Unit-test mod logic without launching the game](howto/testing-and-tooling/offline-tests/unit-test-mod-logic-without-game/overview.md)

Choose where the game boundary sits so maths, models, parsers and state machines compile with no KSA assembly, then run them as tests on any machine and in CI

- [Link a game-free Sim folder wholesale into an xUnit project](howto/testing-and-tooling/offline-tests/unit-test-mod-logic-without-game/unit-test-mod-logic-without-game-1.md) — intermediate
- [Check a model with a console test executable that returns a verdict as its exit code](howto/testing-and-tooling/offline-tests/unit-test-mod-logic-without-game/unit-test-mod-logic-without-game-2.md) — beginner
- [Check numerics against an independent implementation with console checks and Python reference fixtures](howto/testing-and-tooling/offline-tests/unit-test-mod-logic-without-game/unit-test-mod-logic-without-game-3.md) — advanced
- [Run NUnit suites with property tests, sentinel flushes and a three-OS CI matrix](howto/testing-and-tooling/offline-tests/unit-test-mod-logic-without-game/unit-test-mod-logic-without-game-4.md) — intermediate
- [Compile-gate the game-facing half with partial methods and gate slow tests behind an opt-in switch](howto/testing-and-tooling/offline-tests/unit-test-mod-logic-without-game/unit-test-mod-logic-without-game-5.md) — advanced

## Game updates

Living with a game that changes under you: decompiled references, API-surface tracking, pinned builds and a repeatable upgrade procedure.

### [Keep a searchable decompiled copy of the game next to your mod](howto/testing-and-tooling/game-updates/decompile-the-game-for-reference/overview.md)

Decompile the game's assemblies with ilspycmd into a gitignored folder stamped with the game version, or into a private repository where two builds produce a readable diff

- [Refresh a gitignored decompiled tree with ilspycmd, stamp its version and point your assistant at it](howto/testing-and-tooling/game-updates/decompile-the-game-for-reference/decompile-the-game-for-reference-1.md) — beginner
- [Decompile every build into a private git repo with one ilspycmd version kept across updates and diff only what your mod touches](howto/testing-and-tooling/game-updates/decompile-the-game-for-reference/decompile-the-game-for-reference-2.md) — advanced

### [Pin the KSA build with a hashed assemblies lock and notice new builds](howto/testing-and-tooling/game-updates/pin-the-game-build-with-a-hashed-lock/overview.md)

Commit SHA-256 digests of the referenced game DLLs plus the build number so local builds, a private CI mirror and the running game agree on one KSA build, stamp that build into the mod and poll the version endpoint from a scheduled job

- [Pin the KSA build with a hashed assemblies lock, stamp it into the mod and watch for new builds](howto/testing-and-tooling/game-updates/pin-the-game-build-with-a-hashed-lock/pin-the-game-build-with-a-hashed-lock.md) — intermediate

### [List exactly which game APIs your mod binds to, and pin Harmony targets at compile time](howto/testing-and-tooling/game-updates/track-the-game-api-your-mod-uses/overview.md)

Read the TypeRef and MemberRef tables of your built DLL into a committed document that CI checks, and turn reflected Harmony targets into compile-time dependencies with never-called pin methods

- [Generate an API surface document from the built DLL and pin reflected Harmony targets](howto/testing-and-tooling/game-updates/track-the-game-api-your-mod-uses/track-the-game-api-your-mod-uses.md) — advanced

### [Upgrade a mod to a new KSA build and degrade gracefully when the API drifts](howto/testing-and-tooling/game-updates/upgrade-a-mod-to-a-new-ksa-build/overview.md)

A repeatable procedure of refreshing the pinned binaries and decompiled corpus, diffing only what the mod touches, classifying each coupling by how it fails, fixing, recording the new build and flying it, plus runtime design that latches a single feature as degraded instead of crashing

- [Upgrade by locking, decompiling both builds, diffing the API surface and updating the lock last](howto/testing-and-tooling/game-updates/upgrade-a-mod-to-a-new-ksa-build/upgrade-a-mod-to-a-new-ksa-build-1.md) — intermediate
- [Review an upgrade against a coupling catalog that classifies how each coupling fails](howto/testing-and-tooling/game-updates/upgrade-a-mod-to-a-new-ksa-build/upgrade-a-mod-to-a-new-ksa-build-2.md) — advanced
- [Annotate every game binding, degrade one accessor on failure and keep an impact catalog in lockstep](howto/testing-and-tooling/game-updates/upgrade-a-mod-to-a-new-ksa-build/upgrade-a-mod-to-a-new-ksa-build-3.md) — advanced
