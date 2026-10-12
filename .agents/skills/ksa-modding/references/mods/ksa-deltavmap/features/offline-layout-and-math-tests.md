---
type: Mod Feature
title: Offline test executable that links the mod's game-free kernels without KSA.dll
description: A console test exe source-links the mod's game-free layout engine and math kernels without KSA.dll, asserts overlaps and formulas against synthetic trees and stock values, and writes SVG dumps
mod: ksa-deltavmap
category: testing-and-tooling
techniques: [offline-unit-tests, source-linking, svg-dump]
hooks: []
lifecycle: []
game_types: []
tags: [testing, unit-tests, offline, layout, svg, ci, pure-functions]
---

Most of DeltaVMap's logic can be tested without starting the game: a console program builds the
layout engine and every closed-form kernel straight from the mod's own source files, runs them on
synthetic trees and on hand-checked stock values, writes SVG and text dumps to eyeball, and returns
a non-zero exit code if anything fails. Mechanism: the test csproj has no project reference and no
`KSA.dll`; it pulls individual `.cs` files from `..\DeltaVMap\` with `<Compile Include>` and
excludes the two Layout files that touch game types. That only works because the mod keeps those
kernels free of game types by design.[^tests-csproj][^tests-program]

## How it works

**Source linking.** `DeltaVMap.Tests.csproj` is an `Exe` targeting `net10.0` with these compile
items:

| Included | Why it is game-free |
|---|---|
| `..\DeltaVMap\Layout\*.cs` except `VisualTreeAdapter.cs` and `LayoutDump.cs` | The engine works on `LayoutNode` / `LayoutEdge`, never on KSA types; the adapter and the in-game dump are the only bridges |
| `Core\DebugConfig.cs`, `Core\Format.cs` | Shared dump directory and number formats |
| `Dv\DeltaVCalculator.cs` | Closed-form delta-v kernel taking doubles |
| `Dv\TransferWindow.cs` | Lead angle, synodic rate, countdown, ejection angle |
| `Dv\InterstellarMath.cs`, `Dv\RocketPropellant.cs`, `Dv\CruiseSpeed.cs` | Interstellar chain, propellant estimate, cruise-speed state machine |
| `Model\ForestRules.cs` | Body roles, minor-body rule, root order |

`DeltaVMap.slnx` contains the mod and this test project (the harness test project is built
separately).[^dvmap-slnx] `SceneChecks.cs` is compiled here and in the harness tests but removed
from the mod with `<Compile Remove="Layout\SceneChecks.cs" />`; `SyntheticTree.cs` and
`OverlapCheck.cs` do ship in the mod because the in-game layout dump uses them.[^dvmap-csproj]

**Layout cases.** `RunCase(tree, cfg, variant, outDir, minNodes)` runs `LayoutEngine.Run` (with no
`measureText`, so widths come from the character-count estimate), then `OverlapCheck.Run`, prints
node, subtree, label, band and bus violations, writes `<tag>.svg` and `<tag>.txt` through
`LayoutDumpFormat`, and enforces a minimum node count. Cases cover every `SyntheticTree` shape
(a 100+ node moon root with a three-hub bus, a deliberately tight grid that forces snap collisions,
a stock-like planet root, cruise roots, a wide fan, the arrival model, a dense aggregated system,
barycenter and orbiting-star roots) in all three modes, plus a roughly 1,800-node spring stress tree
for the Barnes-Hut path, with no timing assertion because it would be machine dependent.[^dvmap-synthetictree]
`OverlapCheck` verifies no dot overlaps, no sibling-subtree overlaps (tidy-tree guarantee on
pre-snap X), no label overlaps, no child at or above its parent's band and no collapsed hub bus.[^dvmap-overlapcheck]

**Kernel checks.**

| Group | What it locks |
|---|---|
| `RunMathChecks` | Comet velocity-match transfer reduces to exact Hohmann for bound endpoints; intercept capture/circularize split; atmospheric landing fraction across the stock density range |
| `RunTransferWindowChecks` | Earth to Mars lead about +44 deg, Earth to Venus about -54 deg, Earth/Mars synodic period about 2.14 yr, zero countdown at alignment and a full period just before it, ejection geometry, retrograde sum rate, a hand evaluation of the `AlignmentTime` formula |
| `RunInterstellarChecks`, `RunCaptureChecks` | Departure chain, capture at 1 AU around the Alpha Centauri barycenter, coast time, fastest cruise for a budget, arrival chains at Barnard b and Proxima b, from independently evaluated stock-content values |
| `RunPropellantChecks` | Rocket-equation propellant share and shortfall |
| `RunForestChecks` | Barycenter root with stars, a star whose only children are MinorBody exoplanets, Ceres staying minor beside Sol's planets |
| `RunCruiseSpeedChecks` | MAX follows a moving budget, falls back when it cannot pay, yields to a custom speed |
| `RunSceneChecks` | `SceneComposer` in all three modes on two home maps: ego layout unchanged, strip and channel rules from `SceneChecks` at 1600x1000 and 1300x860, opening and closing a system moves nothing it should not, wrapped rows |
| `RunFormatChecks` | Every displayed number format and the cache keys that decide when a string is rebuilt |

The output directory defaults to `DebugConfig.LayoutDumpDir` (a hardcoded local path) and falls back
to `./layout-dumps` when that cannot be created, which `.gitignore` excludes.[^dvmap-gitignore]

## Lifecycle and timing

None: this is build-time tooling that never runs inside KSA, so `lifecycle` and `hooks` are empty.
The code under test is the same code the mod runs on the main thread inside `[StarMapAfterGui]`;
the offline run substitutes the character-count text estimate for `ImGui.CalcTextSize`, so it
validates topology and overlap logic but not exact on-screen label widths.

## Key code

| Path | Role |
|---|---|
| [DeltaVMap.Tests/DeltaVMap.Tests.csproj](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.Tests/DeltaVMap.Tests.csproj) | Source-linked compile items, no game references |
| [DeltaVMap.Tests/Program.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.Tests/Program.cs) | Top-level test program, cases and kernel checks |
| [DeltaVMap/Layout/SyntheticTree.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/SyntheticTree.cs) | Deterministic synthetic trees from index arithmetic |
| [DeltaVMap/Layout/OverlapCheck.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/OverlapCheck.cs) | Layout acceptance invariants |
| [DeltaVMap/Layout/SceneChecks.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/SceneChecks.cs) | Composed-scene rules (test-only) |
| [DeltaVMap/Layout/LayoutDumpFormat.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/LayoutDumpFormat.cs) | SVG, fit-preview SVG and text renderings |

## Snippets

```xml
<!-- DeltaVMap.Tests/DeltaVMap.Tests.csproj L19-L47 -->
<ItemGroup>
  <Compile Include="..\DeltaVMap\Layout\*.cs"
           Exclude="..\DeltaVMap\Layout\VisualTreeAdapter.cs;..\DeltaVMap\Layout\LayoutDump.cs" />
  <!-- Shared debug config (pure, no game deps) so the harness defaults to the same
       dump directory as the in-game LayoutDump. -->
  <Compile Include="..\DeltaVMap\Core\DebugConfig.cs" />
  <!-- ... -->
  <Compile Include="..\DeltaVMap\Core\Format.cs" />
  <!-- ... -->
  <Compile Include="..\DeltaVMap\Dv\DeltaVCalculator.cs" />
  <!-- ... -->
  <Compile Include="..\DeltaVMap\Dv\TransferWindow.cs" />
  <!-- ... -->
  <Compile Include="..\DeltaVMap\Dv\InterstellarMath.cs" />
  <Compile Include="..\DeltaVMap\Dv\RocketPropellant.cs" />
  <!-- ... -->
  <Compile Include="..\DeltaVMap\Dv\CruiseSpeed.cs" />
  <!-- ... -->
  <Compile Include="..\DeltaVMap\Model\ForestRules.cs" />
</ItemGroup>
```

```csharp
// DeltaVMap.Tests/Program.cs L118-L139
static int RunCase(LayoutTree tree, LayoutConfig cfg, string variant, string outDir, int minNodes)
{
    LayoutResult result = LayoutEngine.Run(tree, cfg);
    OverlapReport report = OverlapCheck.Run(result);

    string tag = variant == "default" ? tree.Name : tree.Name + "-" + variant;
    Console.WriteLine($"[{tag}] {report.Summary()}");
    PrintIssues("node", report.NodeOverlaps);
    PrintIssues("subtree", report.SubtreeOverlaps);
    PrintIssues("label", report.LabelOverlaps);
    PrintIssues("band", report.BandViolations);
    PrintIssues("bus", report.BusViolations);

    File.WriteAllText(Path.Combine(outDir, tag + ".svg"), LayoutDumpFormat.ToSvg(result));
    File.WriteAllText(Path.Combine(outDir, tag + ".txt"), LayoutDumpFormat.ToText(result));

    bool nodeCountOk = tree.Nodes.Count >= minNodes;
    if (!nodeCountOk)
        Console.WriteLine($"  FAIL: {tree.Nodes.Count} nodes < required {minNodes}");

    return report.Ok && nodeCountOk ? 0 : 1;
}
```

```csharp
// DeltaVMap.Tests/Program.cs L1190-L1204
// Lead angle: Earth (inner) to Mars (outer) leads by ~+44 deg; Earth to Venus (inner) by
// ~-54 deg. The single formula gives the sign from the wrap.
{
    double mars = TransferWindow.TargetPhaseAngle(aEarth, aMars, false) * rad2deg;
    double venus = TransferWindow.TargetPhaseAngle(aEarth, aVenus, false) * rad2deg;
    fails += Approx("Earth->Mars target phase ~ +44 deg", mars, 44.0, 0.05);
    fails += Approx("Earth->Venus target phase ~ -54 deg", venus, -54.0, 0.05);
}

// Synodic period Earth/Mars is ~2.14 yr (sanity, approximate). Compared in Julian years.
{
    double julianYear = 3.15576e7;
    double synodicYears = TransferWindow.SynodicPeriod(tEarth, tMars, false) / julianYear;
    fails += Approx("Earth/Mars synodic period ~2.14 yr", synodicYears, 2.14, 0.02);
}
```

## Game APIs touched

None. The executable deliberately has no reference to any game assembly. The kernels it tests
reproduce a few stock formulas locally (for example `MathEx.ToDeviationAngle` for the
`OrbitalTransfers.AlignmentTime` cross-check), and the reference values are stock content numbers
typed in as constants.

## Pitfalls and version notes

- The design constraint is what makes this possible: keep math and layout in files that take
  primitives, and put every game read in a thin adapter. One stray `using KSA;` in a kernel breaks
  the offline build.
- Layout passes mutate node positions, so every case builds a fresh tree.
- Offline text widths are estimates; a label overlap that only appears with the real font will not
  be caught here (the in-game dump and the harness scene test cover that side).
- `Program.cs` calls GravityWell "the in-game default" in one comment, while `MapWindow` defaults to
  `LayoutMode.CumulativeDown`; the comment is stale, the tests run both modes either way.

## See also

- [Headless harness tests](./headless-harness-tests.md)
- [In-game validation dumps and perf scopes](./in-game-validation-dumps-and-perf.md)
- [Three layout modes](./three-layout-modes.md)
- [Transfer windows and phase clock](./transfer-windows-and-phase-clock.md)
- [Patched-conic transfer model](./patched-conic-transfer-model.md)
- [Latched dumps from StarMapAfterGui behind a DebugConfig flag](../../../howto/external-io/telemetry/record-telemetry-and-diagnostics-to-files/record-telemetry-and-diagnostics-to-files-3.md) - how-to article demonstrating this feature
- [Check a model with a console test executable that returns a verdict as its exit code](../../../howto/testing-and-tooling/offline-tests/unit-test-mod-logic-without-game/unit-test-mod-logic-without-game-2.md) - how-to article demonstrating this feature

[^tests-csproj]: DeltaVMap.Tests/DeltaVMap.Tests.csproj
[^tests-program]: DeltaVMap.Tests/Program.cs
[^dvmap-synthetictree]: DeltaVMap/Layout/SyntheticTree.cs
[^dvmap-overlapcheck]: DeltaVMap/Layout/OverlapCheck.cs
[^dvmap-csproj]: DeltaVMap/DeltaVMap.csproj
[^dvmap-slnx]: DeltaVMap.slnx
[^dvmap-gitignore]: .gitignore
