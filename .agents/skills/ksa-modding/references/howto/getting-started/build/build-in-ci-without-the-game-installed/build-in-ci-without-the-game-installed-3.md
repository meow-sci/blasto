---
type: How-To
title: Build and test in CI with no game at all using a compile-gated game seam
description: Make the PAT for the private assemblies optional - the game-free projects, the entry project and the unit tests still build and run when KSA.dll is absent - and add the real game when the secret is present
category: getting-started
group: build
topic: build-in-ci-without-the-game-installed
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/compile-gated-game-seam, mods/gatos/features/build-deploy-and-ci]
techniques: [msbuild, github-actions]
hooks: [github-actions actions/checkout]
lifecycle: []
game_types: []
tags: [ci, github-actions, compile-gating, tests, gatos, optional-secret]
---

The strongest answer to "CI cannot have the game" is to need it as little as possible. gatOS has eleven projects; ten
never reference KSA, and the entry project compiles with `Game/**` removed when `KSA.dll` is absent
([Game-free half and compile-gated Game half](../build-a-mod-without-game-references/build-a-mod-without-game-references-2.md)). Its CI therefore treats the private assemblies as **optional**: with the
secret it checks out the private repository and compiles the game-coupled glue against the real DLLs; without it, the
whole solution still builds and all tests (which are game-free) run. This article is the CI half of that design. The
alternatives that always fetch the game are [Private DLL repository fetched with a GitHub App token](./build-in-ci-without-the-game-installed-1.md) and
[Refasmer reference assemblies stored per game build](./build-in-ci-without-the-game-installed-2.md).

## Background: how the game does it

The relevant mechanism is MSBuild, not the game: `KSAFolder` resolves from `KSA_DLL_DIR`, a sibling checkout or the
default install path; a missing folder simply makes `Exists('$(KSAFolder)/KSA.dll')` false, which sets
`KsaAssembliesPresent` and removes `Game/**` from compilation, and each KSA reference carries
`Condition="Exists(...)"`.[gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj#L19-L28)[Directory.Build.props](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/Directory.Build.props) The comment in the workflow records that `dotnet build
-p:KSA_DLL_DIR=/nonexistent` is green.

## Lifecycle and timing

Build and test time on a hosted Linux runner. Nothing runs in the game; the integration tests boot a real QEMU guest on
the runner, not KSA.

## Steps

1. **Point `KSA_DLL_DIR` at where the private checkout would be**, whether or not it exists:[.github/workflows/build.yml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/.github/workflows/build.yml#L43-L65)

   ```yaml
   # .github/workflows/build.yml L43-L52
   jobs:
     build:
       runs-on: ubuntu-latest
       env:
         # Directory.Build.props resolves this before any per-OS default. When the
         # PAT is missing the dir simply doesn't exist and the guarded references
         # drop out (verified: `dotnet build -p:KSA_DLL_DIR=/nonexistent` is green).
         KSA_DLL_DIR: ${{ github.workspace }}/.tmp/ksa-game-assemblies/current/dll
         # secrets.* is not allowed directly in step `if:`; surface it via env.
         KSA_PAT: ${{ secrets.KSA_GAME_ASSEMBLIES_PAT }}
   ```

2. **Make the checkout conditional on the secret** (a fine-grained read-only PAT here; an App token also works, see
   approach 1):

   ```yaml
   # .github/workflows/build.yml L53-L65
       steps:
         - name: Checkout gatOS
           uses: actions/checkout@v6

         - name: Checkout private KSA assemblies
           if: env.KSA_PAT != ''
           uses: actions/checkout@v6
           with:
             repository: meow-sci/ksa-game-assemblies
             path: .tmp/ksa-game-assemblies
             token: ${{ env.KSA_PAT }}
             persist-credentials: false
             ref: main
   ```

3. **Gate the compile in the project** as in [Game-free half and compile-gated Game half](../build-a-mod-without-game-references/build-a-mod-without-game-references-2.md): `Compile Remove="Game\**\*.cs"`
   unless the assemblies exist.
4. **Run the full solution build and the tests in the same job.** Library tests are game-free; integration tests
   self-skip unless `GATOS_IT=1`, which CI sets:

   ```yaml
   # .github/workflows/build.yml L94-L101
         - name: Build
           run: dotnet build gatos.slnx -c Release --nologo -v quiet

         - name: Test
           id: test
           env:
             GATOS_IT: "1"
           run: dotnet test gatos.slnx --nologo -v quiet --logger trx --results-directory .tmp/test-results
   ```

5. **Keep the secret out of fork builds by design**: with the PAT absent (forks), the job still produces a meaningful
   result because the game-free half is real code under test.
6. **Release workflows add the game.** `mod-release.yml` builds two zips per release (Windows with bundled QEMU, Linux
   with system QEMU), as a prerelease from `main` or a release from `release/<v>`; the game DLLs are still never
   packaged ([Package and publish a mod release (zip, CI publishing, installer)](../package-and-publish-a-mod-release/overview.md)).

## Complete example

| File | Role |
|---|---|
| [.github/workflows/build.yml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/.github/workflows/build.yml) | Optional private checkout, build, test, test reporting |
| [gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj) | `KsaAssembliesPresent`, conditional references |
| [Directory.Build.props](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/Directory.Build.props) | `KSAFolder` resolution (`KSA_DLL_DIR` first) |

## Variations and gotchas

- **Without the assemblies, `gatOS.GameMod` compiles only its game-free half**, so type errors in `Game/` are not caught
  until a build with the secret; run both ways before release.
- **Partial-method seams** silently drop calls without the game; a missing implementation is not a build error.
- **Integration tests need prerequisites** (QEMU, the guest image, KVM); with `GATOS_IT=1` missing prerequisites become
  failures instead of skips.
- **Do not run the real game in CI**; it needs a window and GPU. Validation inside KSA stays manual (gatOS keeps a
  checklist).
- **`TreatWarningsAsErrors`** is on repo-wide, so reference-version conflicts also fail the build.

## See also

- [Private DLL repository fetched with a GitHub App token](./build-in-ci-without-the-game-installed-1.md), [Refasmer reference assemblies stored per game build](./build-in-ci-without-the-game-installed-2.md), [Build a mod in CI when the game DLLs are private](./overview.md)
- [Compile-gated game seam](../../../../mods/gatos/features/compile-gated-game-seam.md), [Build, deploy and CI](../../../../mods/gatos/features/build-deploy-and-ci.md)
- [Build a KSA mod that references only StarMap.API and Harmony](../build-a-mod-without-game-references/overview.md), [Reference the game's assemblies from a mod project without shipping them](../reference-game-assemblies-from-a-mod-project/overview.md)
