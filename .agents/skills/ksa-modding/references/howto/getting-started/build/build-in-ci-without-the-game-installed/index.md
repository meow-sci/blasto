# Game Feature

* [Build a mod in CI when the game DLLs are private](overview.md) - How to compile and test mods on public CI without redistributing KSA - a private DLL repository with an App token, stripped reference assemblies per build, or a compile-gated game seam

# How-To

* [Build against Refasmer reference assemblies stored per game build](build-in-ci-without-the-game-installed-2.md) - Strip the installed game DLLs to API-only reference assemblies, keep them in a private bucket keyed by build, and fetch the build named by the mod's TestedGameVersion constant
* [Build and test in CI with no game at all using a compile-gated game seam](build-in-ci-without-the-game-installed-3.md) - Make the PAT for the private assemblies optional - the game-free projects, the entry project and the unit tests still build and run when KSA.dll is absent - and add the real game when the secret is present
* [Fetch a private DLL repository in CI with a GitHub App token](build-in-ci-without-the-game-installed-1.md) - Keep the KSA assemblies in a private repository, mint a short-lived App token scoped to that one repo in the workflow, copy the DLLs into deps/ and pass KSADir on the command line
