---
type: Mod Feature
title: Headless tests enforced by a game-free Sim/ source boundary
description: All simulation code lives in a folder that may not reference KSA types, and the xUnit project links that folder wholesale against only the numerics assemblies, so the boundary enforces itself and whole engagements are flown with no game present
mod: ksarmory
category: testing-and-tooling
techniques: [source-linking, architecture-boundary, invariance-tests, headless-harness]
hooks: []
lifecycle: []
game_types: []
tags: [tests, xunit, architecture, boundary, headless, frames]
---

KSArmory's source is split by whether a file can see the game: `src/KSArmory/Sim/` (195 files:
guidance, fuses, drives, threat model, ballistics, registry, blast physics) may not reference a
KSA type, and `src/KSArmory/Ksa/` binds everything to the game.[^readme] The test project compiles
`Sim/**` directly into itself and references only `Brutal.Core.Numerics` and `Brutal.Core.Common`
(for `double3`), so a `using KSA;` under `Sim/` breaks the test build. That makes the rule
self-enforcing, makes every new `Sim/` file testable the moment it exists, and lets the suite fly
whole engagements, ballistic shots and blast sweeps at ecliptic speeds with no game process.

## How it works

| Piece | Mechanism |
|---|---|
| [tests/KSArmory.Tests/KSArmory.Tests.csproj](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/KSArmory.Tests.csproj) | `<Compile Include="..\..\src\KSArmory\Sim\**\*.cs" Link=...>` plus two `Reference`s (no `Private="false"`: the tests execute `double3`); no project reference to the mod[^tests-csproj] |
| [tools/check-boundary.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-boundary.sh) | Textual stand-in for runners without game assemblies: greps `Sim/` for `using KSA;` or `KSA.X` outside comments[^check-boundary] |
| [tools/test.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/test.sh) | Runs the suite in Release (7x faster on the trajectory tests, and the configuration that ships), excluding `kind=study` by default; `--studies` and `--all` select the rest; a caller's own `--filter` replaces the selection because `dotnet test` keeps only the last one[^test-sh] |
| [tools/check-studies.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-studies.py) | Regenerates (or `--check`s) [tests/KSArmory.Tests/STUDIES.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/STUDIES.md), the committed list of tests tagged `[Trait("kind", "study")]`, so tagging a test out of the push loop shows up in review[^check-studies][^studies-md] |
| [.github/workflows/ci.yml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.github/workflows/ci.yml#L130-L144) | Runs the guards and, as a separate step, the studies, so nothing goes unrun before a merge[^ci] |

### What the tests look like

The suite (about 2,400 `[Fact]`/`[Theory]` methods across 312 files at the pinned ref) leans on
a few patterns worth copying for any mod that simulates in KSA's ecliptic frame:

- **Vary the step.** At a constant `dt` a right and a wrong epoch are indistinguishable.
  `OffsetPhaseTests` alternates 16/20 ms frames and drops from 1x to 0.25x to 0.01x simulation
  speed, asserting the drawn round offset never jumps more than 50 m in a frame and never drifts
  along the 29.8 km/s orbital direction.[^offset-phase]
- **Advance the platform sample before the update that uses it,** which is the phase the engine
  has; the comment notes that swapping the two lines makes every assertion pass against code that
  jumps in game.[^offset-phase]
- **Invariance plus sensitivity.** A `Sim/` entry point takes both frame-carrying terms and
  differences them itself; tests add the same arbitrary velocity to both inputs and assert the
  answer does not move, with a second assertion that the relative term still matters.[^frames-doc]
- **Price a term rather than assert it.** `BodySampleInvarianceTests` measures what a known epoch
  offset of the body sample is worth over a flown arc, documenting why a correction was not
  applied.[^body-sample-invariance]
- **Pin a collapse-prone refactor.** `DrawAnchorTests` fails if the two instants `DrawAnchor`
  samples are collapsed into one, which looks like a tidy-up and moves the overlay 500 m.[^draw-anchor-tests]

## Lifecycle and timing

Nothing here runs in the game. The tests execute the same `Sim/` code the mod compiles, under
`dotnet test`, on any OS; `check-boundary.sh` and `check-studies.py --check` run in the
game-free CI tooling job via `tools/check-all.sh`, and the suite itself runs in the CI build job
that has the private assemblies.[^check-all][^ci] The in-game counterpart of the boundary is
that every engine sample a `Sim/` type sees is handed in by `Ksa/` code during `StepSimulation`,
so the tests can reproduce the exact phase relationship (end-of-step samples, start-of-step
rounds) without the engine.

## Key code

| Path | Role |
|---|---|
| [tests/KSArmory.Tests/KSArmory.Tests.csproj](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/KSArmory.Tests.csproj#L13-L33) | Wholesale link of `Sim/**`, numerics-only references |
| [tools/check-boundary.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-boundary.sh#L20-L31) | Grep guard for CI without assemblies |
| [tools/test.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/test.sh#L31-L50) | Configuration and study selection |
| [tests/KSArmory.Tests/OffsetPhaseTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/OffsetPhaseTests.cs) | Step-varying epoch tests |
| [tools/check-studies.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-studies.py) | Committed record of tests excluded from the push loop |

## Snippets

```xml
<!-- tests/KSArmory.Tests/KSArmory.Tests.csproj L19-L36 -->
  <!--
    Everything under src/KSArmory/Sim is free of KSA types by construction, so the whole
    folder is linked in and exercised headlessly on any platform.

    The wildcard is the point. This project references no KSA assembly, so the moment a file
    under Sim/ reaches for one, *the test build fails* - the boundary enforces itself instead
    of relying on someone remembering a rule. It also means a new simulation file is tested the
    moment it exists, with no build plumbing to forget.
  -->
  <ItemGroup>
    <Compile Include="..\..\src\KSArmory\Sim\**\*.cs" Link="Linked\%(RecursiveDir)%(Filename)%(Extension)" />
  </ItemGroup>

  <!-- double3 lives here. Runtime assets included: the tests actually execute this. -->
  <ItemGroup>
    <Reference Include="$(KsaDllDir)Brutal.Core.Numerics.dll" />
    <Reference Include="$(KsaDllDir)Brutal.Core.Common.dll" />
  </ItemGroup>
```

```csharp
// tests/KSArmory.Tests/OffsetPhaseTests.cs L55-L70
        foreach (double dt in steps)
        {
            // The platform sample for frame k, having advanced by exactly the step this frame's
            // update is about to be given. Reversing these two lines encodes the opposite phase,
            // and every assertion below then passes against code that jumps in game.
            platform += OrbitalVelocity * dt;

            round.Update(dt, target: null, gravity: double3.Zero,
                         frameVelocityEcl: OrbitalVelocity, platformEcl: platform,
                         munition: munition);

            offsets.Add(round.OffsetFromPlatform);
        }

        return offsets;
    }
```

```bash
# tools/check-boundary.sh L20-L31
# `using KSA;`, `KSA.Something`, and the loader's attributes. Comments and doc text are allowed
# to name KSA — half the value of those files is explaining why they do not touch it.
offenders="$(grep -rnE '^\s*(using KSA;|using KSA\.)|(^|[^A-Za-z_."/])KSA\.[A-Z]' "$SIM" --include='*.cs' \
    | grep -vE '^\s*[^:]+:[0-9]+:\s*(//|///|\*)' || true)"

if [[ -n "$offenders" ]]; then
    echo "Sim/ must not reference KSA types:" >&2
    echo "$offenders" >&2
    echo >&2
    echo "Move the KSA-facing part into src/KSArmory/Ksa/ and keep the maths in Sim/." >&2
    exit 1
fi
```

## Game APIs touched

None from `KSA.dll` by design. The test project references `Brutal.Core.Numerics` (for
`double3`, `doubleQuat`) and `Brutal.Core.Common` from the game install, which is why even the
headless suite needs the assemblies resolved through `$(KsaDllDir)`.[^tests-csproj]

## Pitfalls and version notes

- **Still needs game DLLs.** The numerics types come from the game, so a fork without the private
  assemblies cannot run the suite; only the textual boundary check runs there.[^check-boundary]
- **The boundary moves maths, not just types.** When a `Ksa/` file turns out to contain real
  arithmetic, the rule is to move it into `Sim/` (the README cites `FireGeometry`, split out of
  `LauncherPart`) rather than leave it untested.[^readme]
- **Constant-step tests prove nothing about epochs.** Write target samples as end-of-step values
  and check every regression test fails against the old code.[^frames-doc]
- **A study tag is unverified.** `check-studies.py` cannot tell a mis-tagged guard from a real
  study; it only makes the tag visible in the diff.[^check-studies]

## See also

- [Ecliptic frames and epochs](./ecliptic-frames-and-epochs.md)
- [Repository static checks](./repo-static-checks.md)
- [Unattended scenario runner](./unattended-scenario-runner.md)
- [Pin the KSA build with a hashed assemblies lock file](./ksa-assemblies-lock.md)
- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Share one driver seam between the game and the headless rig and test with varying steps](../../../howto/testing-and-tooling/offline-tests/run-mod-simulation-in-a-headless-rig/run-mod-simulation-in-a-headless-rig-2.md) - how-to article demonstrating this feature
- [Gather about twenty textual and offline checks into one script that skips with a notice](../../../howto/testing-and-tooling/offline-tests/run-repo-checks-from-a-pre-push-hook-and-ci/run-repo-checks-from-a-pre-push-hook-and-ci-1.md) - how-to article demonstrating this feature
- [Pair check-all with a test script and a CI that builds against a private assemblies repo](../../../howto/testing-and-tooling/offline-tests/run-repo-checks-from-a-pre-push-hook-and-ci/run-repo-checks-from-a-pre-push-hook-and-ci-2.md) - how-to article demonstrating this feature
- [Link a game-free Sim folder wholesale into an xUnit project](../../../howto/testing-and-tooling/offline-tests/unit-test-mod-logic-without-game/unit-test-mod-logic-without-game-1.md) - how-to article demonstrating this feature

[^tests-csproj]: tests/KSArmory.Tests/KSArmory.Tests.csproj
[^check-boundary]: tools/check-boundary.sh
[^test-sh]: tools/test.sh
[^offset-phase]: tests/KSArmory.Tests/OffsetPhaseTests.cs
[^draw-anchor-tests]: tests/KSArmory.Tests/DrawAnchorTests.cs
[^body-sample-invariance]: tests/KSArmory.Tests/BodySampleInvarianceTests.cs
[^check-studies]: tools/check-studies.py
[^studies-md]: tests/KSArmory.Tests/STUDIES.md
[^ci]: .github/workflows/ci.yml
[^check-all]: tools/check-all.sh
[^frames-doc]: docs/FRAMES-AND-EPOCHS.md
[^readme]: README.md
