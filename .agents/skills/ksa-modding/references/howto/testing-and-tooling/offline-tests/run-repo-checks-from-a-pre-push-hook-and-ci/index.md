# Game Feature

* [Run one check script from the pre-push hook and from CI](overview.md) - Gather boundary checks and other textual guards, tests, API-surface and lock checks, shader compiles, asset validators and CI smoke checks of bundled native libraries and guest images into one check-all script that a git hook and the CI workflow both call

# How-To

* [Build a guest image with a smoke boot in CI and fetch it by pinned release with checksums](run-repo-checks-from-a-pre-push-hook-and-ci-4.md) - Assemble a Linux guest image from pinned bootstrap packages, boot it in QEMU as the last build step, publish it as a versioned release and have consumers download and verify it instead of building
* [Build native solver libraries with pinned toolchains and prove their ABI in per-platform CI jobs](run-repo-checks-from-a-pre-push-hook-and-ci-3.md) - Build a Rust C library with cargo and a C library with zig cc under hash-pinned compilers, then compare sizeof and offsetof from a C probe with Marshal.SizeOf and OffsetOf on the managed bindings, with fixed expected counts so a removed binding cannot pass
* [Gather about twenty textual and offline checks into one script that skips with a notice](run-repo-checks-from-a-pre-push-hook-and-ci-1.md) - Write a single check-all script of ordered run and skip steps, call it from the git pre-push hook and from the CI tooling job, and add small textual guards for the rules a compiler cannot see
* [Pair check-all with a test script and a CI that builds against a private assemblies repo](run-repo-checks-from-a-pre-push-hook-and-ci-2.md) - Run the same check script in the pre-push hook and in CI, keep game-free checks in a job that needs no secret, and build and test against a private mirror of the game assemblies with a read-only deploy key that forks simply skip
