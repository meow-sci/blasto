---
type: Mod Feature
title: Build, deploy and CI
description: An MSBuild deploy target that assembles the complete mod folder on every build without deleting the player's config, NUnit suites that run game-free by default and boot a real VM when GATOS_IT=1, and GitHub workflows for testing under KVM and publishing per-OS release zips
mod: gatos
category: testing-and-tooling
techniques: [msbuild-conditions, ci-pipeline, headless-tests]
hooks: []
lifecycle: []
game_types: []
tags: [msbuild, deploy, nunit, testing, ci, github-actions, integration-tests, packaging]
---

gatOS is a 21-project solution with a guest OS, a bundled emulator and a strict zero-warning
policy. Its build and CI setup is a good template for any large KSA mod: the mod folder is always
deployable from `dotnet build`, everything except the game-coupled glue is unit-tested on a bare
host, and integration tests that need real infrastructure are opt-in locally and mandatory in CI.

## How it works

**Shared build settings** ([Directory.Build.props](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/Directory.Build.props)): `net10.0`, C# 13, nullable, warnings as
errors (including MSBuild warnings such as reference-version conflicts), `KSAFolder` resolution
(`KSA_DLL_DIR`, a sibling assemblies checkout, or the default install path) and the deploy folder
(`GATOS_DIST_DIR`, else the user's KSA mods folder).

**Deploy on build** ([gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj#L43-L112)): the `CopyCustomContent`
target runs after every build of the mod project and:

- wipes the managed payload of the previous deploy so stale DLLs never linger in the mod's load
  context, but excludes `gatos.toml` because on Windows the install and data folders coincide;
- copies `mod.toml` and the `deps.json` (StarMap's per-mod `AssemblyDependencyResolver` reads it);
- copies the template `gatos.default.toml`, third-party licenses, every output DLL except the ones
  StarMap supplies to all mods (`0Harmony.dll`, `StarMap.API.dll`);
- copies `guest/out/**` and `vendor/qemu/win-x64/**` incrementally with `SkipUnchangedFiles`
  (about 59 MB and 90 MB respectively), warning loudly when the guest image is missing.

**Tests.** Every library has an NUnit project; `gatOS.GameMod` has none by design (it is the
game-coupled glue). Integration tests self-skip with `Assert.Ignore` unless `GATOS_IT=1`
([gatOS.Vm.Tests/TestEnv.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm.Tests/TestEnv.cs#L11-L35)); with it set, missing prerequisites become failures.
`gatOS.SimFs.Tests` mounts the real `/sim` in a booted guest and checks the file models end to end;
game-free physics, camera math, paint rules and protocol codecs are unit-tested directly.

**CI** ([.github/workflows/build.yml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/.github/workflows/build.yml)): checks out the private game assemblies with a token
(the project still builds without them), installs QEMU, enables `/dev/kvm` for the runner user via
a udev rule, fetches the pinned guest, then runs `dotnet build` and `dotnet test` with `GATOS_IT=1`
and publishes TRX results. `mod-release.yml` builds two zips per release, `gatOS-windows-<v>`
(payload, guest and bundled QEMU) and `gatOS-linux-<v>` (payload and guest; system QEMU), as a
prerelease from `main` or a release from `release/<v>`. `guest-image.yml` builds the guest, and
`site-deploy.yml` publishes the Astro/Starlight documentation site.

## Lifecycle and timing

Build-time and CI only.

## Key code

| Path | Role |
|---|---|
| [Directory.Build.props](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/Directory.Build.props) | shared settings, `KSAFolder`, deploy folder |
| [gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj#L43-L112) | `CopyCustomContent` deploy target |
| [gatOS.Vm.Tests/TestEnv.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm.Tests/TestEnv.cs) | `GATOS_IT` gating helpers |
| [.github/workflows/build.yml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/.github/workflows/build.yml) | build and full test gate under KVM |
| [.github/workflows/mod-release.yml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/.github/workflows/mod-release.yml) | per-OS release zips |
| [tools/fetch-qemu.sh](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/tools/fetch-qemu.sh) | pinned portable QEMU for Windows bundles |

## Snippets

```xml
<!-- gatOS.GameMod/gatOS.GameMod.csproj L43-L56 -->
    <Target Name="CopyCustomContent" AfterTargets="AfterBuild">

        <!-- Wipe the managed payload (top-level files + licenses dir) so a redeploy never
             leaves stale DLLs from a previous version in the game's mod ALC. The guest/ and
             qemu/ subdirs are deliberately NOT wiped: they hold large, content-stable blobs
             (~59 MB guest image, ~90 MB win-x64 QEMU) that are instead copied with
             SkipUnchangedFiles, keeping the per-build deploy cost near zero. -->
        <!-- gatos.toml is excluded: on Windows the install dir and the runtime data dir are the same
             folder, so the live config lives here. Wiping it would discard the player's settings on
             every rebuild/redeploy. The shipped default is the separate gatos.default.toml below. -->
        <ItemGroup>
            <StaleDistFile Include="$(DistDir)*" Exclude="$(DistDir)gatos.toml"/>
        </ItemGroup>
        <Delete Files="@(StaleDistFile)"/>
```

## Game APIs touched

None.

## Pitfalls and version notes

- Never wipe the deploy folder wholesale if user data can live there.
- Do not ship assemblies the loader already provides (`0Harmony`, `StarMap.API`); a second copy in
  the mod's load context leads to type identity problems.
- `deps.json` must list every private dependency, including vendored ones, for StarMap's resolver.
- The repo records about 1650 passing tests at the 5482 pass; in-game validation checklists are kept
  separately in `docs/VALIDATION.md` and many are still marked not run.

## See also

- [Compile-gated game seam](compile-gated-game-seam.md)
- [KSA anchor churn tracking](ksa-anchor-churn-tracking.md)
- [Alpine guest image pipeline](alpine-guest-image.md)
- [purrTTY custom shell contract](purrtty-custom-shell-contract.md)
- [gatOS overview](../overview.md)
- [Build and test in CI with no game at all using a compile-gated game seam](../../../howto/getting-started/build/build-in-ci-without-the-game-installed/build-in-ci-without-the-game-installed-3.md) - how-to article demonstrating this feature
- [Add a plain post-build copy with an overridable KSAGameDir](../../../howto/getting-started/build/deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-4.md) - how-to article demonstrating this feature
- [Compile-gate the game-facing half with partial methods and gate slow tests behind an opt-in switch](../../../howto/testing-and-tooling/offline-tests/unit-test-mod-logic-without-game/unit-test-mod-logic-without-game-5.md) - how-to article demonstrating this feature

