---
type: Mod Feature
title: Headless simulation tests behind a Sim/Ksa boundary
description: Splitting the mod into a KSA-free Sim folder linked wholesale into an xUnit project and a Ksa folder that binds to the game, so the maths runs headlessly and the boundary enforces itself at build time
mod: ksagolf
category: testing-and-tooling
techniques: [headless-tests, msbuild-linked-sources]
hooks: []
lifecycle: []
game_types: [Brutal.Numerics.double3]
tags: [testing, xunit, headless, architecture, boundary, ci, invariance-tests]
---

KSAGolf keeps 44 source files under `src/KSAGolf/Sim/` that never mention a KSA type: ball physics,
drag, contact sweeps, course grid and surface, camera maths, the frame latch and step gate, bridge
command parsing, terrain ray marching. The test project links that whole folder and references no
`KSA.dll`, so roughly 290 xUnit facts and theories over 34 test files run on any machine with only
two Brutal maths assemblies, without the game running. Anything that needs the engine lives in
`src/KSAGolf/Ksa/` and passes plain values into `Sim/`.

The reusable idea is to make the "no game types in the pure layer" rule a compile error rather than
a convention, and to test the frame-timing logic that is otherwise only visible in flight.

## How it works

| Piece | Mechanism |
|---|---|
| Wildcard link | `<Compile Include="..\..\src\KSAGolf\Sim\**\*.cs" Link=...>`: every new `Sim/` file is compiled into the tests the moment it exists[^golf-tests-csproj] |
| Boundary by construction | The test project references only `Brutal.Core.Numerics.dll` (for `double3`) and `Brutal.Core.Common.dll`; a `using KSA;` under `Sim/` fails the test build |
| Boundary without the game | `tools/check-boundary.sh` greps `Sim/` for `using KSA` or `KSA.<Type>` outside comments, so hosted CI (which has no game assemblies) can still enforce it[^golf-check-boundary] |
| Engine types abstracted | Generic seams such as `StepGate<T> where T : IEquatable<T>` stand in for `UniverseTime`; interfaces (`IGroundTest`, `IHullTest`, `ITerrainHeights`, `IMouseDrag`) let `Ksa/` supply the engine-backed version[^golf-stepgate] |
| Runner | `tools/test.sh` runs `dotnet test` in Release by default, the configuration the shipped mod compiles `Sim/` in[^golf-test-sh] |
| CI | The `build` job runs `tools/test.sh` against the private assemblies checkout; the `tooling` job runs `check-all.sh`, which includes the boundary grep[^golf-ci][^golf-check-all] |

Testing rules the repo states and the suites follow:[^golf-claude-md][^golf-frames-epochs]

- A regression test counts only if it fails against the old code.
- Vary the step: a constant `dt` cannot tell a right phase from a wrong one.
- A `Sim/` entry point takes both frame-carrying terms (`aPos, aVel, bPos, bVel`) and differences
  them itself; test it for invariance (add the same velocity to both, the answer must not move)
  alongside a sensitivity assertion.
- Express failures in the units that matter: `SkippedFrameTests` converts a missed frame into metres
  of drift at 29.8 km/s ecliptic speed.

Suites called out as load-bearing: `DrawAnchorTests` (two sampling instants), `SkippedFrameTests`
(a missed frame is integrated, not lost), `FrameLatchTests` (the latch is always released) and
`ContactSweepTests` (a fast body is swept, not sampled).[^golf-claude-md]

## Lifecycle and timing

Build-time only; nothing here runs in the game. The point is that frame-loop logic which normally
lives in `frame:draw-ui`, `frame:celestials` and `frame:after-on-frame` hooks (see
[Dual-hook frame stepping](dual-hook-frame-stepping.md)) is expressed as pure classes that a test can
drive frame by frame, including frames the hook never sees.[^golf-skippedframe-tests]

## Key code

| Path | Role |
|---|---|
| [tests/KSAGolf.Tests/KSAGolf.Tests.csproj](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/KSAGolf.Tests.csproj) | Wildcard link and minimal references |
| [tools/check-boundary.sh](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/check-boundary.sh) | Textual boundary check for CI without assemblies |
| [tools/test.sh](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/test.sh) | Test runner |
| [tests/KSAGolf.Tests/SkippedFrameTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/SkippedFrameTests.cs) | Missed-frame integration |
| [tests/KSAGolf.Tests/FrameLatchTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/FrameLatchTests.cs) | Latch always released |
| [tests/KSAGolf.Tests/DrawAnchorTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/DrawAnchorTests.cs) | Two-instant draw anchor |

## Snippets

```xml
<!-- tests/KSAGolf.Tests/KSAGolf.Tests.csproj L28-L36 -->
  <ItemGroup>
    <Compile Include="..\..\src\KSAGolf\Sim\**\*.cs" Link="Linked\%(RecursiveDir)%(Filename)%(Extension)" />
  </ItemGroup>

  <!-- double3 lives here. Runtime assets included: the tests actually execute this. -->
  <ItemGroup>
    <Reference Include="$(KsaDllDir)Brutal.Core.Numerics.dll" />
    <Reference Include="$(KsaDllDir)Brutal.Core.Common.dll" />
  </ItemGroup>
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
    echo "Move the KSA-facing part into src/KSAGolf/Ksa/ and keep the maths in Sim/." >&2
    exit 1
fi
```

```csharp
// tests/KSAGolf.Tests/SkippedFrameTests.cs L12-L35
    /// <summary>Nanosecond stamps, the way UniverseTime carries them.</summary>
    private readonly record struct Stamp(long Nanos) : IEquatable<Stamp>;

    private static double Span(Stamp from, Stamp to) => (to.Nanos - from.Nanos) / 1e9;

    private static Stamp At(double seconds) => new((long)(seconds * 1e9));

    [Fact]
    public void AMissedFrameIsIntegratedOnTheNextOne()
    {
        StepGate<Stamp> gate = new();

        // Frame 1 runs: the world stepped 22.5 ms and so do we.
        Assert.Equal(0.0225, gate.Consume(At(0.0225), 0.0225, Span), 9);

        // Frame 2 is the capture -- the hook never runs, so nothing is consumed.

        // Frame 3: the engine reports only the LAST step, 26.5 ms. The world has advanced
        // 22.5 + 26.5 = 49 ms since we integrated, and the missed 22.5 ms is what a moving body
        // must still be given.
        double taken = gate.Consume(At(0.0715), 0.0265, Span);

        Assert.Equal(0.049, taken, 9);
    }
```

## Game APIs touched

None at runtime. The tests reference `Brutal.Numerics.double3` from `Brutal.Core.Numerics.dll`; the
generic `StepGate<T>` is instantiated with `KSA.UniverseTime` only in `Ksa/KsaWorld.cs`.

## Pitfalls and version notes

- The tests still need two game assemblies (Brutal maths), so a fork without the private assembly
  mirror skips the C# tests in CI and only the textual boundary check runs.[^golf-ci]
- The repo is explicit that a behaviour change is unverified until seen in game, whatever the suite
  says; the suite proves the maths and the frame bookkeeping, not what KSA does with them.[^golf-claude-md]
- Keep `Sim/` free of `DEBUG`-conditional code: tests run in Release to match the shipped build.[^golf-test-sh]

## See also

- [Dual-hook frame stepping on simulated time](dual-hook-frame-stepping.md)
- [Frame and epoch draw anchor](frame-epoch-draw-anchor.md)
- [Swept contact against a craft's real hull](swept-hull-contact.md)
- [API surface record and assembly lock](api-surface-and-assembly-lock.md)
- [Packaging, deploy and release pipeline](packaging-and-release.md)
- [Test ball and contact physics with an epoch bookkeeping layer and missed-frame invariance](../../../howto/testing-and-tooling/offline-tests/run-mod-simulation-in-a-headless-rig/run-mod-simulation-in-a-headless-rig-3.md) - how-to article demonstrating this feature
- [Pair check-all with a test script and a CI that builds against a private assemblies repo](../../../howto/testing-and-tooling/offline-tests/run-repo-checks-from-a-pre-push-hook-and-ci/run-repo-checks-from-a-pre-push-hook-and-ci-2.md) - how-to article demonstrating this feature
- [Link a game-free Sim folder wholesale into an xUnit project](../../../howto/testing-and-tooling/offline-tests/unit-test-mod-logic-without-game/unit-test-mod-logic-without-game-1.md) - how-to article demonstrating this feature

[^golf-tests-csproj]: tests/KSAGolf.Tests/KSAGolf.Tests.csproj
[^golf-check-boundary]: tools/check-boundary.sh
[^golf-test-sh]: tools/test.sh
[^golf-check-all]: tools/check-all.sh
[^golf-skippedframe-tests]: tests/KSAGolf.Tests/SkippedFrameTests.cs
[^golf-stepgate]: src/KSAGolf/Sim/StepGate.cs
[^golf-claude-md]: CLAUDE.md
[^golf-frames-epochs]: docs/FRAMES-AND-EPOCHS.md
[^golf-ci]: .github/workflows/ci.yml
