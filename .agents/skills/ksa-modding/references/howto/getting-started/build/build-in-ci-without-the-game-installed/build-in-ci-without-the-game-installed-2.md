---
type: How-To
title: Build against Refasmer reference assemblies stored per game build
description: Strip the installed game DLLs to API-only reference assemblies, keep them in a private bucket keyed by build, and fetch the build named by the mod's TestedGameVersion constant
category: getting-started
group: build
topic: build-in-ci-without-the-game-installed
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/ci-reference-assemblies]
techniques: [github-actions, msbuild]
hooks: [github-actions fetch-ksa-refs]
lifecycle: []
game_types: []
tags: [ci, github-actions, refasmer, reference-assemblies, b2, tested-version, nightly]
---

KSA-AdvancedFlightComputer builds its CI against **reference assemblies**, not the game: Refasmer strips each installed
managed DLL to its API surface (no method bodies, no non-API members), the result is zipped per game build and kept in a
private bucket, and CI downloads the zip for the build named in the mod's `TestedGameVersion` constant. Compile success
then proves the API still matches; reflection targets are strings, so a headless harness covers those. It is stricter
than a private DLL repository ([Private DLL repository fetched with a GitHub App token](./build-in-ci-without-the-game-installed-1.md)): pinned per build, nothing but API ships to
the runner, and the one constant drives code, CI and release notes.

## Background: how the game does it

The game folder contains managed assemblies (`KSA.dll`, `Brutal.*`, ...), native DLLs (no metadata, skipped) and the
bundled .NET runtime listed in `KSA.deps.json` (skipped because CI's SDK provides it). `VersionInfo.Current` reports the
running build at run time, and the same constant is compared there for a warning ([Runtime VersionInfo compare at AllModsLoaded](../../lifecycle/check-the-game-version/check-the-game-version-1.md)).
Reference assemblies keep the type and member signatures compilation needs. Because private members are omitted, they
cannot catch a renamed private field the mod reaches by reflection.

## Lifecycle and timing

Build and release time only. The only run-time touchpoint is the `[StarMapAllModsLoaded]` version check against
`VersionInfo.Current.VersionString`.

## Steps

1. **One version constant**, read by code and scripts: `Mod.TestedGameVersion` (`"v2026.10.7.5541"`) is compared at load
   and parsed by the CI scripts with a regex, so the code, the references CI compiles against and the release notes
   name one build:[AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L21)

   ```csharp
   // AdvancedFlightComputer/Mod.cs L21
       private const string TestedGameVersion = "v2026.10.7.5541";
   ```

2. **Publish references from a machine with the game.** `build/publish-ksa-refs.ps1` reads the installed `KSA.dll`
   `FileVersion`, does nothing if `<Prefix><version>.zip` is already in the bucket, runs Refasmer with
   `--omit-non-api-members=true` over every managed game DLL, and uploads the zip to a private Backblaze B2 bucket with
   an application key restricted to that bucket:[build/publish-ksa-refs.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/publish-ksa-refs.ps1#L262-L275)

   ```powershell
   # build/publish-ksa-refs.ps1 L266-L269
       # --omit-non-api-members=true drops private members that are not part of the API, which is all a
       # compile needs. File names rather than full paths keep the command line well under its limit.
       $refasmerArgs = @("--continue", "--omit-non-api-members=true", "--outputdir=$refsDir") + $managed
   ```

   A managed DLL Refasmer cannot process is reported and left out, unless a project references it through `$(KsaDir)`, in
   which case nothing is uploaded.
3. **Keep the bucket current automatically.** A pre-push hook (`git config core.hooksPath build/hooks`) reads
   `TestedGameVersion` from each pushed commit and runs the publish script for that version: it uploads if the build is
   installed and missing, warns otherwise, and never blocks the push:[build/hooks/pre-push](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/hooks/pre-push#L1-L10)

   ```sh
   # build/hooks/pre-push L2-L9
   # Makes sure the KSA reference assemblies CI needs are in the B2 bucket before a push.
   #
   # CI's mod job fetches the refs for TestedGameVersion in Mod.cs. For each pushed branch or tag this
   # reads that version from the pushed commit and runs build/publish-ksa-refs.ps1 -Version on it,
   # which uploads the refs if they are missing and that build is installed, and warns otherwise.
   # It never blocks a push: a missing zip only fails the mod job, which says what to do.
   ```

4. **Fetch in CI** with a read-only key, into the runner's temp folder, and build with `-p:KsaDir=`:[.github/workflows/ci.yml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/.github/workflows/ci.yml#L99-L112)

   ```yaml
   # .github/workflows/ci.yml L101-L111
         # The reference assemblies stay in the runner's temp folder: they must never be uploaded
         # as an artifact or cached, since both can be read from a public repository.
         - name: Fetch KSA reference assemblies
           shell: pwsh
           env:
             B2_APPLICATION_KEY_ID: ${{ secrets.KSAREFS_ID }}
             B2_APPLICATION_KEY: ${{ secrets.KSAREFS_KEY }}
             B2_BUCKET: ksarefs
           run: >-
             ./build/ci/fetch-ksa-refs.ps1
             -Destination '${{ runner.temp }}/ksa-refs'
   ```

   `fetch-ksa-refs.ps1` defaults the version to `TestedGameVersion`, downloads `<Prefix><version>.zip`, checks it
   against the SHA-256 recorded at upload and unpacks it. The `mod` job is skipped for fork pull requests, which get no
   secrets.
5. **Build and package, verifying the artifact holds no game assemblies:**[.github/workflows/ci.yml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/.github/workflows/ci.yml#L114-L127)

   ```yaml
   # .github/workflows/ci.yml L118-L125
         - name: Build and package the mod
           run: >-
             dotnet build AdvancedFlightComputer/AdvancedFlightComputer.csproj
             --configuration Release
             --no-restore
             -p:DeployToMods=false
             -p:GuidanceNativeRids=win-x64%3Blinux-x64
             "-p:KsaDir=${{ runner.temp }}/ksa-refs"
   ```

   The release `PackageRelease` target and `VerifyGuidancePackage` diff the zip against the declared payload
   ([Explicit payload list shared by deploy and packaging](../deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-3.md), [Payload list verified file by file](../package-and-publish-a-mod-release/package-and-publish-a-mod-release-2.md)).
6. **Nightlies and promotion.** `nightly.yml` builds `main` nightly when it moved and publishes a pre-release with the zip,
   `SHA256SUMS.txt` and a build provenance attestation; `promote.yml` turns a nightly into a stable draft only after
   checking the commit is on `main`, the version is new and the zip matches its checksum and attestation, so a release
   carries exactly the tested bytes.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs) | The version constant |
| [build/publish-ksa-refs.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/publish-ksa-refs.ps1), [build/hooks/pre-push](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/hooks/pre-push) | Create and refresh the references in the bucket |
| [build/ci/fetch-ksa-refs.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/ci/fetch-ksa-refs.ps1), [.github/workflows/ci.yml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/.github/workflows/ci.yml) | Fetch and build in CI |

## Variations and gotchas

- **Compile success proves less.** Reference assemblies lack private members; reflection handles are strings, so
  success says nothing about them. AFC's headless harness mod checks those at run time.
- **Do not redistribute the game's assemblies, even stripped.** Keep them in private storage; never cache or upload
  them as CI artifacts.
- **Until the zip for a new `TestedGameVersion` is uploaded the `mod` job fails by design**, with a message saying what to
  do.
- **PowerShell 7 rejects B2's authorization token as a header** unless header validation is skipped (the script handles
  this).
- **Pins cost upkeep:** bump the constant and publish new refs per game build.

## See also

- [Private DLL repository fetched with a GitHub App token](./build-in-ci-without-the-game-installed-1.md), [Compile-gated game seam](./build-in-ci-without-the-game-installed-3.md), [Build a mod in CI when the game DLLs are private](./overview.md)
- [Build a KSA mod in CI from private reference assemblies](../../../../mods/ksa-advancedflightcomputer/features/ci-reference-assemblies.md)
- [Warn when the running game build differs from the one a mod was tested on](../../lifecycle/check-the-game-version/overview.md), [Package and publish a mod release (zip, CI publishing, installer)](../package-and-publish-a-mod-release/overview.md), [Reference the game's assemblies from a mod project without shipping them](../reference-game-assemblies-from-a-mod-project/overview.md)
