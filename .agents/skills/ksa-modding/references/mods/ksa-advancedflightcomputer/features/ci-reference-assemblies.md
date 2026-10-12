---
type: Mod Feature
title: Build a KSA mod in CI from private reference assemblies
description: Strip the installed game's managed DLLs to API-only reference assemblies with Refasmer, keep them in a private bucket keyed by game build, fetch them in CI by the TestedGameVersion constant, and publish nightlies that are promoted to releases without rebuilding
mod: ksa-advancedflightcomputer
category: testing-and-tooling
techniques: [reference-assemblies, github-actions]
hooks: []
lifecycle: []
game_types: [KSA.VersionInfo]
tags: [ci, github-actions, reference-assemblies, refasmer, release, nightly, game-version]
---

A KSA mod compiles against `KSA.dll` and the game's other assemblies, which cannot be committed or
downloaded publicly. AFC still builds every pull request, every push to `main` and every release in
GitHub Actions. The trick is reference assemblies: the game's public API surface with every method body
stripped, stored privately and keyed by the exact game build the mod declares it was tested against.

## How it works

### One version constant

`Mod.TestedGameVersion` (for example `"v2026.10.7.5541"`) is compared with `VersionInfo.Current.VersionString`
at load for a warning (see [feature-gated patch sets](feature-gated-patch-sets.md)), and the same string is
read by the CI scripts with a regex, so the code, the reference assemblies CI compiles against and the
release notes all name one build ([AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L21), [build/ci/fetch-ksa-refs.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/ci/fetch-ksa-refs.ps1)).

### Publishing references (`build/publish-ksa-refs.ps1`)

- Reads the installed `KSA.dll` `FileVersion`; does nothing if `<prefix><version>.zip` is already in the
  bucket.
- Copies every managed DLL in the game folder root (native DLLs detected by `BadImageFormatException` from
  `AssemblyName.GetAssemblyName`, and the bundled .NET runtime listed under the runtimepack in
  `KSA.deps.json`, are skipped) and runs Refasmer with `--omit-non-api-members=true`, which keeps the API
  surface and strips method bodies and non-API members; a DLL Refasmer cannot process is left out unless a
  project references it through `$(KsaDir)`, in which case nothing is uploaded.
- Zips and uploads to a private Backblaze B2 bucket using the B2 native API with an application key
  restricted to that bucket ([build/publish-ksa-refs.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/publish-ksa-refs.ps1#L1-L31)).

### Fetching in CI

The `mod` job (skipped for pull requests from forks, which get no secrets) runs `build/ci/fetch-ksa-refs.ps1`
with `KSAREFS_ID`/`KSAREFS_KEY` secrets, downloads and expands the zip for `TestedGameVersion` into the
runner's temp folder, and builds Release with `-p:KsaDir=<temp>/ksa-refs`; the references are never
uploaded or cached, and the package check keeps game assemblies out of the artifact
([.github/workflows/ci.yml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/.github/workflows/ci.yml#L67-L130)).

### Keeping the bucket current

`build/hooks/pre-push` (enabled with `git config core.hooksPath build/hooks`) reads `TestedGameVersion` from
each pushed commit and runs the publish script for that version: it uploads if the build is installed and
missing, warns otherwise, and never blocks the push ([build/hooks/pre-push](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/hooks/pre-push)).

### Nightlies and promotion

`nightly.yml` builds `main` nightly at 03:23 UTC when it moved, runs the same jobs as `ci.yml`, and publishes
a pre-release `v<next-patch>-nightly.<date>` with the zip, `SHA256SUMS.txt` and a build provenance
attestation. `promote.yml` turns a nightly into a stable draft release after checking that its commit is on
`main`, the version is new, and the zip matches its checksum and attestation, so a release carries exactly
the tested bytes; publishing the draft by hand triggers `spacedock-publish.yml`
([docs/releasing.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/releasing.md), [.github/workflows/nightly.yml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/.github/workflows/nightly.yml), [.github/workflows/promote.yml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/.github/workflows/promote.yml)).
`build/ci/write-release-notes.sh` writes notes including the KSA build from `TestedGameVersion`.

## Lifecycle and timing

Not part of the game frame. The only runtime touchpoint is the `[StarMapAllModsLoaded]` version check
against `VersionInfo.Current.VersionString` ([current/decomp/KSA/VersionInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionInfo.cs)).

## Key code

| Path | Role |
|---|---|
| [build/publish-ksa-refs.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/publish-ksa-refs.ps1) | Refasmer and B2 upload |
| [build/ci/fetch-ksa-refs.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/ci/fetch-ksa-refs.ps1) | Download by `TestedGameVersion` |
| [build/hooks/pre-push](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/hooks/pre-push) | Keep the bucket in step with pushed versions |
| [.github/workflows/ci.yml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/.github/workflows/ci.yml) | Native jobs and the Release mod job |
| [.github/workflows/nightly.yml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/.github/workflows/nightly.yml) | Nightly pre-releases |
| [.github/workflows/promote.yml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/.github/workflows/promote.yml) | Promotion without rebuild |
| [.github/workflows/spacedock-publish.yml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/.github/workflows/spacedock-publish.yml) | SpaceDock upload of stable releases |
| [build/ci/write-release-notes.sh](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/ci/write-release-notes.sh) | Release notes |
| [docs/releasing.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/releasing.md) | Release process |

## Snippets

```powershell
# build/publish-ksa-refs.ps1 L266-L268

    # --omit-non-api-members=true drops private members that are not part of the API, which is all a
    # compile needs. File names rather than full paths keep the command line well under its limit.
```

## Game APIs touched

| Member | Use |
|---|---|
| `VersionInfo.Current.VersionString` ([current/decomp/KSA/VersionInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionInfo.cs)) | Runtime build check |
| `KSA.dll` file version | Key of the reference zip |

## Pitfalls and version notes

- Reference assemblies compiled with `--omit-non-api-members` lack private members, which is fine for
  compiling because reflection targets are strings; it also means compile success says nothing about
  reflection handles (that is what the [headless harness](headless-harness-test-mod.md) checks).
- Do not redistribute the game's assemblies, even stripped; keep them in private storage and never cache or
  upload them as CI artifacts.
- Until the zip for a new `TestedGameVersion` is uploaded, the `mod` job fails by design.

## See also

- [Mod deploy MSBuild targets](mod-deploy-msbuild-targets.md)
- [Native solver build and ABI check](native-solver-build-and-abi-check.md)
- [Headless harness test mod](headless-harness-test-mod.md)
- [Build against Refasmer reference assemblies stored per game build](../../../howto/getting-started/build/build-in-ci-without-the-game-installed/build-in-ci-without-the-game-installed-2.md) - how-to article demonstrating this feature

