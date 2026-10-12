---
type: How-To
title: Link a game-free Sim folder wholesale into an xUnit project
description: Keep every KSA-free class under one folder, compile that folder into a test project that references no KSA assembly, and let the compiler enforce the boundary
category: testing-and-tooling
group: offline-tests
topic: unit-test-mod-logic-without-game
approach: 1
difficulty: intermediate
prerequisites: [dotnet-sdk, xunit]
demonstrated_by:
  - mods/ksagolf/features/headless-sim-tests
  - mods/ksacars/features/headless-sim-tests
  - mods/ksarmory/features/sim-boundary-headless-tests
  - mods/ksastructures/features/headless-tests-and-ci
techniques: [headless-tests, msbuild-linked-sources]
hooks: []
lifecycle: [event:headless-test-run]
game_types: [Brutal.Numerics.double3, KSA.Universe]
tags: [testing, xunit, headless, sim-boundary, msbuild, ci]
---

Outcome: a `tests/<Mod>.Tests` project that runs a few hundred fast tests on any machine that has the .NET SDK and two Brutal maths assemblies, with no game running, and fails to compile if a class under `Sim/` ever names a KSA type.
Demonstrated by KSAGolf ([tests/KSAGolf.Tests/KSAGolf.Tests.csproj](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/KSAGolf.Tests.csproj#L28-L36)), KSACars, KSArmory and KSAStructures, which all use the same layout. Prerequisites: a mod whose maths and state machines you can move into one folder.

## Background: how the game does it

The game gives you nothing to test against offline, so the boundary is yours to draw. The one engine type the pure side usually needs is `Brutal.Numerics.double3` ([current/decomp/Brutal.Numerics/double3.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/double3.cs)), which ships in `Brutal.Core.Numerics.dll`. Everything else the mod reads from the game arrives through an adapter that passes plain values in.
The typical adapter-side seam is the frame step: `Universe.GetLastSimStep()` returns a `SimStep` with `PreviousTime`, `NextTime` and `DeltaTime` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2381-L2384), [current/decomp/KSA/SimStep.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SimStep.cs)); the adapter converts it to doubles and hands those to `Sim/`.

## Lifecycle and timing

Nothing here runs in the game frame. The value of the layout is that frame-loop logic, which in the game lives in hooks such as `frame:draw-ui` and `frame:after-on-frame`, is expressed as plain classes a test can drive frame by frame, including frames the real hook never sees.
See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for what the adapters wrap.

## Steps

1. **Split the source tree in two.** Everything that names a KSA, StarMap or Harmony type goes under `src/<Mod>/Ksa/` (or `Game/`); everything else goes under `src/<Mod>/Sim/`: physics, drag, contact sweeps, grid and surface models, camera maths, frame bookkeeping, command parsing. Parsing a bridge command is pure text work, so even that is a `Sim/` type.
   Engine types are replaced by generic seams: KSAGolf's `StepGate<T> where T : IEquatable<T>` stands in for `UniverseTime`, and interfaces such as `IGroundTest` and `ITerrainHeights` let `Ksa/` supply the engine-backed version.

2. **Create the test project and link the folder with a wildcard.** The wildcard is the point: a new `Sim/` file is compiled into the tests the moment it exists, with no build plumbing to forget. The project references only the two Brutal maths assemblies:

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

   KSArmory's copy of the same csproj documents the intent in a comment: because the project references no KSA assembly, the moment a file under `Sim/` reaches for one the test build fails ([tests/KSArmory.Tests/KSArmory.Tests.csproj](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/KSArmory.Tests.csproj#L19-L36)).

3. **Resolve `KsaDllDir` once** in `Directory.Build.props` so the maths assemblies come from an environment override (`KSA_DLL_DIR`), a local `Import/` folder, a sibling `ksa-game-assemblies` checkout or the default install path, in that order, with an explicit error when none exists; KSAStructures shows the first three fallbacks ([Directory.Build.props](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/Directory.Build.props#L23-L26)).

4. **Write tests in physical terms.** Rules the KSAGolf repo states and its suites follow: a regression test counts only if it fails against the old code; vary the step, because a constant `dt` cannot tell a right phase from a wrong one; take both frame-carrying terms and difference them inside `Sim/`, then test invariance (add the same velocity to both and the answer must not move) next to a sensitivity assertion. A good example converts a missed frame into integrated time:

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

5. **Add a textual boundary check for CI without assemblies.** Hosted CI usually has no game DLLs, so the compile-time rule cannot run there. A grep over `Sim/` enforces the same thing; comments may name KSA because explaining why a file does not touch it is half their value:

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

6. **Wrap the run in one script.** Release is the default because it is the configuration the mod compiles `Sim/` in; anything unrecognised goes to `dotnet test` unchanged:

   ```bash
   # tools/test.sh L17-L23
   # Only a leading Debug/Release is a configuration; everything else is `dotnet test`'s.
   CONFIG=Release
   case "${1:-}" in
       Debug|Release) CONFIG="$1"; shift ;;
   esac

   dotnet test "$REPO_ROOT/tests/KSAGolf.Tests/KSAGolf.Tests.csproj" -c "$CONFIG" --nologo "$@"
   ```

   Expected output: a green `dotnet test` summary of the whole suite (about 290 tests in KSAGolf), and a non-zero exit code on the first regression.

## Complete example

| File | Role |
|---|---|
| [tests/KSAGolf.Tests/KSAGolf.Tests.csproj](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/KSAGolf.Tests.csproj) | Wildcard link and minimal references |
| [tools/check-boundary.sh](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/check-boundary.sh) | Textual boundary check |
| [tools/test.sh](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/test.sh) | Test runner used by the pre-push hook and CI |
| [tests/KSAGolf.Tests/SkippedFrameTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/SkippedFrameTests.cs) | Missed-frame integration test |
| [tests/KSArmory.Tests/KSArmory.Tests.csproj](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/KSArmory.Tests.csproj) | The same layout with a documented intent |

## Variations and gotchas

- **Two game assemblies are still needed.** The tests reference `Brutal.Core.Numerics.dll` and `Brutal.Core.Common.dll`, so a fork without access to a copy of the assemblies cannot run the C# tests in CI and falls back to the textual boundary check only. Approach 2 avoids the dependency by never touching `double3`.
- **Release versus Debug.** Nothing under `Sim/` may be conditioned on `DEBUG`; otherwise the tests would exercise code that is not what ships.
- **A boundary that erodes.** If a `Sim/` class needs one more engine value, widen the adapter, not the folder: add a parameter or an interface.
- **Game updates.** `Sim/` code is immune to API drift by construction; only the `Ksa/` adapters need review (see [Upgrade a mod to a new KSA build](../../game-updates/upgrade-a-mod-to-a-new-ksa-build/overview.md)).

## See also

- [Overview](overview.md); next approach: [Console test executable with invariant checks](unit-test-mod-logic-without-game-2.md)
- [Run the mod's whole simulation in a headless rig](../run-mod-simulation-in-a-headless-rig/overview.md), [Run one check script from the pre-push hook and from CI](../run-repo-checks-from-a-pre-push-hook-and-ci/overview.md)
- Mod features: [KSAGolf headless sim tests](../../../../mods/ksagolf/features/headless-sim-tests.md), [KSACars](../../../../mods/ksacars/features/headless-sim-tests.md), [KSArmory](../../../../mods/ksarmory/features/sim-boundary-headless-tests.md), [KSAStructures](../../../../mods/ksastructures/features/headless-tests-and-ci.md)
