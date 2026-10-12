---
type: Mod Feature
title: Native-free managed test executables that run production patches against stand-in KSA types
description: Eighteen console projects compile production sub-mod files by link, declare small namespace-KSA stand-ins with the game's member names and apply the real Harmony patches to them, so timing, save and patch logic is checked without starting the game
mod: unscience
submod: unscience
category: testing-and-tooling
techniques: [harmony-prefix, harmony-postfix, harmony-transpiler, test-fixture-stand-ins, msbuild-compile-link]
hooks:
  - harmony-transpiler KSA.Program.PrepareFrame (applied to a fixture copy)
  - harmony-prefix KSA.Part.CrashTolerancePascals (applied to a fixture copy)
lifecycle: [frame:prepare, event:save, event:load]
game_types: [KSA.Program, KSA.Universe, KSA.Part, KSA.SimStep, KSA.UncompressedSave]
tags: [testing, harness, fixtures, harmony, msbuild, regression, ci]
---

The KSA executable cannot be started by a unit-test runner (it needs its native renderer, physics and
content), and the authors also build on macOS ARM where the shipped Windows x64 game cannot run at
all.[^world-readme][^repo-index] Unscience therefore tests its game-facing code with plain console
executables, one per area (`*.tests/`), that link the exact production source files and replace the
game with small managed stand-ins carrying the same namespace, type and member names. Harmony then
patches the stand-ins exactly as it patches the real game, so a test exercises the shipped patch
method, its `AccessTools` lookups, its prefix/postfix/finalizer contract and its save adapter.

Mechanism in one line: `<Compile Include="../feature.lib/File.cs" Link="File.cs" />` plus a
`namespace KSA` fixture file, `RequiresKSAAssemblies=false`, `new Harmony(...)`, and a `Require` helper
that throws on the first failed check and prints one PASS line.

## How it works

**Project shape** ([the-tick.tests/the-tick.tests.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.tests/the-tick.tests.csproj)).
Each test project is an `Exe` with no test framework. It pulls in `Lib.Harmony` (and `Tomlyn` where TOML
is under test) and compiles production files by link instead of referencing the feature `.lib` project,
so it can choose a minimal slice that does not drag in ImGui, Vulkan or `KSA.dll`. The repo-wide
`Directory.Build.props` fails a build early when `KSA.dll` is missing unless a project sets
`RequiresKSAAssemblies=false`, which every native-free test project does.[^build-props][^tick-csproj]

**Stand-in game types** ([the-tick.tests/GameFixture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.tests/GameFixture.cs)).
The fixture declares `namespace KSA` classes (`Vehicle`, `PartTree`, `Part`, `PhysicsBubble`,
`VehicleUpdateState`, ...) whose member names and shapes mirror the game build the patch targets, and
whose bodies reproduce only the decision under test (for example the crash-tolerance resolution and
the structural-failure decision). Methods that will be patched are marked
`[MethodImpl(MethodImplOptions.NoInlining)]` so the JIT cannot inline them away from Harmony.[^tick-fixture]

**Frame-loop fixtures** ([garrys-torch.tests/GameLoopFixture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.tests/GameLoopFixture.cs)).
For the `PrepareFrame` transpiler the fixture contains a `KSA.Program.PrepareFrame` whose body is the
same sequence of solver calls as the game (apply orbit/vehicle/cloth, `GetJobSimStep`, execute
cloth/vehicle/orbit) and a `Universe` that records each call into an event list. The production
transpiler from `PhysicsFrameHook` is applied to it, and the checks assert the callback lands after the
apply calls and before the next step is queued, that missing, duplicated or reordered seams are
rejected, and that timestamps use `SimStep.PreviousTime`.[^gt-loop][^gt-readme]

**Real numerics where layout matters.** Projects that test collider or scale math reference the
game's own `BepuPhysics`, `BepuUtilities` and `Brutal.Core.Numerics` assemblies through `KSAFolder`
HintPaths (types only; nothing native is initialized), so struct layouts and math are the shipped
ones.[^gt-csproj] `world-saves.tests` goes one step further and references the feature libraries and
`KSA.dll` as type references to round-trip real save DTOs, again without initializing the game.[^world-csproj]

**Coverage.** The suites map onto the risky integration points:

| Project | What it pins down |
|---|---|
| `saves.tests` | Production `NativeSaveHooks` against native-signature fixtures: transaction order, worker joins, failed write (`Write()` returns false), unreadable save, callback isolation; storage and coordinator; part identity; material ownership[^saves-readme] |
| `camera-saves.tests`, `world-saves.tests` | Save DTO and recipe round-trips for cameras, rings, canopy, statics, decals |
| `garrys-torch.tests` | Weld timing seam, collisions with real Bepu, scale snapshots, TOML presets, Zippo light animation persistence |
| `godzilla.tests`, `dent-wizard.tests`, `the-tick.tests`, `kitchen-sink.tests` | Scale channels, launch math, indestructibility and G-load patches, IVA camera ownership |
| `iron-man.tests`, `iron-man-flight.tests`, `iron-man-mode.tests` | Connector codec, flight/render patch routing, mode lifecycle |
| `doh.tests`, `byo-music.tests`, `pyro.tests`, `pebbles.tests`, `sphinx.tests`, `save-yourself.tests` | Material-set release, repeat scheduling and file catalogs, plume cycles, GLB parsing and collider math, placement and UV math, autosave naming and settings |
| `ksa-upgrade.tests` | Regression set written during a game-build upgrade: render filter, vehicle paint, propellant feed, canopy restore, Parts Now validation[^upgrade-readme] |

All 18 projects are in the solution file, so `dotnet build` compiles them with everything else; they
are run individually with `dotnet run --project <name>.tests`.[^slnx] The repository's agent rules make
adding or updating these checks mandatory whenever saved behavior changes, and require stating
separately what still needs an in-game check.[^agents]

## Lifecycle and timing

Nothing here runs inside KSA. The harnesses reproduce three game moments in miniature:

| Game moment | Lifecycle | How the fixture reproduces it |
|---|---|---|
| `Program.PrepareFrame` solver handoff | `frame:prepare` | Fixture `PrepareFrame` with the same apply/execute call order; the production transpiler is applied and the recorded event list is asserted |
| Native save | `event:save` | Fixture `UncompressedSave` whose `Write()` can return false or throw, to check that no sidecar callback fires |
| Native load | `event:load` | Fixture load path that fails reading, or reconstructs, to check reset and restore ordering and single unwinding |

What the fixtures cannot show is the real frame: the actual game `PrepareFrame` in build 5554 returns
`OnFrameResult` and contains more calls (dirty-flag flushes, input polling) than the fixture, and
`SimStep` is a `readonly struct` there rather than a record struct.[^ksa-program][^ksa-simstep] A transpiler
that matches by call seams survives such differences; a test that passed against an out-of-date fixture
proves nothing about a new build, which is why each upgrade adds checks to `ksa-upgrade.tests` and
re-reads the decomp.

## Key code

| Path | Role |
|---|---|
| [the-tick.tests/the-tick.tests.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.tests/the-tick.tests.csproj) | Minimal linked-source test project |
| [the-tick.tests/GameFixture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.tests/GameFixture.cs) | Stand-in `KSA` types mirroring the 5482 member shapes |
| [the-tick.tests/Program.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.tests/Program.cs) | Checks: apply real patches, assert, unpatch; `Require` helper |
| [the-tick.lib/TheTickPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.lib/TheTickPatches.cs) | Production patch class under test (`AccessTools` lookups bind to the stand-ins) |
| [garrys-torch.tests/GameLoopFixture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.tests/GameLoopFixture.cs) | `PrepareFrame` call-order fixture for the frame-hook transpiler |
| [garrys-torch.tests/garrys-torch.tests.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.tests/garrys-torch.tests.csproj) | Linked sources plus real Bepu and Brutal numerics |
| [saves.tests/saves.tests.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/saves.tests/saves.tests.csproj) | Whole persistence stack linked without game assemblies |
| [world-saves.tests/world-saves.tests.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/world-saves.tests/world-saves.tests.csproj) | The exception: real libraries and `KSA.dll` as type references |
| [Directory.Build.props](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/Directory.Build.props) | `RequiresKSAAssemblies` switch and the early missing-DLL error |
| [ksa-mod-experiments.slnx](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-mod-experiments.slnx) | Solution listing every test project |

## Snippets

A complete native-free test project:

```xml
<!-- the-tick.tests/the-tick.tests.csproj L1-L19 -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <RequiresKSAAssemblies>false</RequiresKSAAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Lib.Harmony" Version="2.4.2" />
    <Compile Include="../the-tick.lib/TickProtection.cs" Link="TickProtection.cs" />
    <Compile Include="../the-tick.lib/TheTickPatches.cs" Link="TheTickPatches.cs" />
    <Compile Include="../the-tick.lib/TheTickSubmod.Saves.cs" Link="TheTickSubmod.Saves.cs" />
    <Compile Include="../ksa-abstractions.lib/VehicleProvider.cs" Link="VehicleProvider.cs" />
    <Compile Include="../ksa-abstractions.lib/Persistence/ISaveParticipant.cs" Link="ISaveParticipant.cs" />
    <Compile Include="../ksa-abstractions.lib/Persistence/SaveJson.cs" Link="SaveJson.cs" />
    <Compile Include="../ksa-abstractions.lib/Persistence/FiniteNumberConverters.cs" Link="FiniteNumberConverters.cs" />
    <Compile Include="../ksa-abstractions.lib/Persistence/SaveDocument.cs" Link="SaveDocument.cs" />
    <Compile Include="../ksa-abstractions.lib/Persistence/SaveStorage.cs" Link="SaveStorage.cs" />
    <Compile Include="../ksa-abstractions.lib/Persistence/SceneSaveCoordinator.cs" Link="SceneSaveCoordinator.cs" />
  </ItemGroup>
</Project>
```

Stand-ins carry the game's names; patched members are kept out of line:

```csharp
// the-tick.tests/GameFixture.cs L5-L58
namespace KSA;

// Native-free stand-ins for the KSA surfaces the production patches touch. Member names and
// shapes mirror KSA 5482 so the linked production code and Harmony lookups bind unchanged.

public sealed class Vehicle
{
    public Vehicle(string id, int partCount = 1)
    {
        Id = id;
        Parts = new PartTree(this, partCount);
    }

    public string Id { get; }
    public bool IsDisposed { get; set; }
    public bool IsDebris { get; set; }
    public PartTree Parts { get; }
}
// ...
public sealed class Part
{
    /// <summary>Authored template tolerance; NaN derives a fallback the way PartStructuralLimits does.</summary>
    public double AuthoredTolerance = double.NaN;
    public PartTree? Tree;
    public Part? PartParent;
    public bool IsAttachedInternal;
    public Part FullPart => PartParent ?? this;

    public double CrashTolerancePascals
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        get => PartStructuralLimits.ResolveCrashTolerance(AuthoredTolerance, 1000.0, 1.0);
    }
}
```

A frame-loop fixture for a `PrepareFrame` transpiler:

```csharp
// garrys-torch.tests/GameLoopFixture.cs L5-L29
// Small managed fixture of the 5402 result/snapshot lifecycle, not a replacement game simulation.
// The tests link the production Harmony patch and exercise it against this warmed-up caller.
namespace KSA
{
    public readonly record struct UniverseTime(double Seconds);
    public readonly record struct SimStep(UniverseTime PreviousTime, UniverseTime NextTime, double DeltaTime);

    public sealed class Program
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public SimStep RunFrame(double dt) => PrepareFrame(0, dt);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private SimStep PrepareFrame(double currentPlayerTime, double dtPlayer)
        {
            Universe.ApplyOrbitSolvers();
            Universe.ApplyVehicleSolvers();
            Universe.ApplyClothSolvers();
            SimStep step = Universe.GetJobSimStep(dtPlayer);
            Universe.ExecuteNextClothSolvers(dtPlayer, step);
            Universe.ExecuteNextVehicleSolvers(dtPlayer, step);
            Universe.ExecuteNextOrbitSolvers(dtPlayer, step);
            return step;
        }
    }
```

The check helper: fail fast, count, print one PASS line:

```csharp
// the-tick.tests/Program.cs L84-L157
        Require(rover.Parts.Parts[0].CrashTolerancePascals == PartStructuralLimits.DerivedFallback, "unpatching restores native tolerance");
        ExpectCause(new VehicleUpdateState(rover), VehicleDestructionCause.ExcessiveGForce);
        TickProtection.Clear();
        Console.WriteLine($"PASS: {_checks} The Tick indestructibility checks; native collision/UI acceptance remains in-game.");
    }
// ...
    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }
```

## Game APIs touched

None at run time; these are the members the fixtures mirror and must be kept in step with.

| Member | Decomp | Mirrored for |
|---|---|---|
| `Program.PrepareFrame(double, double)` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141) | Frame-hook transpiler seams |
| `Part.CrashTolerancePascals` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L858) | The Tick's prefix target |
| `SimStep` | [current/decomp/KSA/SimStep.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SimStep.cs) | `PreviousTime` timestamp checks |

## Pitfalls and version notes

- A stand-in must reproduce the member's exact name, static-ness and parameter list, or the production
  `AccessTools` lookup fails in the test even though it would bind in game (and vice versa). Fixtures
  are labelled with the build they mirror (5402, 5482); update them when the decomp changes.
- Linking sources means a test can compile a file in a context the shipped assembly never sees.
  Keep linked slices small and free of ImGui or Vulkan calls, which have no stand-ins.
- Type-level references to real game DLLs are fine; anything that touches the native runtime
  (renderer, Bepu simulation buffers, FMOD) is outside the harness and stays an in-game acceptance item.
- `dent-wizard.tests` does not opt out of `RequiresKSAAssemblies`, so it still needs a resolvable
  `KSAFolder` containing `KSA.dll` even though it only references numerics assemblies.
- CI builds the solution (so the tests must compile) but the release workflow does not run them; run the
  relevant project locally after a change.

## See also

- [Build, deploy and release pipeline](./unscience-build-and-release.md)
- [PrepareFrame physics handoff](./lib-physics-frame-hook.md) (the transpiler the frame-loop fixture exercises)
- [Native save and load hooks](./lib-native-save-hooks.md) and [scene save sidecar](./unscience-scene-save-sidecar.md)
- [The Tick indestructibility](./the-tick-vessel-indestructibility.md)
- [Standalone mod template](./fixme-mod-name-standalone-mod-template.md)
- [unscience sub-mod](../submods/unscience.md) and [mod overview](../overview.md)
- [Apply the real patches to stand-in KSA types in a console test](../../../howto/testing-and-tooling/offline-tests/test-harmony-patches-without-the-game/test-harmony-patches-without-the-game-1.md) - how-to article demonstrating this feature

[^world-readme]: world-saves.tests/README.md
[^repo-index]: REPOSITORY_INDEX.md
[^build-props]: Directory.Build.props
[^tick-csproj]: the-tick.tests/the-tick.tests.csproj
[^tick-fixture]: the-tick.tests/GameFixture.cs
[^gt-loop]: garrys-torch.tests/GameLoopFixture.cs
[^gt-readme]: garrys-torch.tests/README.md
[^gt-csproj]: garrys-torch.tests/garrys-torch.tests.csproj
[^world-csproj]: world-saves.tests/world-saves.tests.csproj
[^saves-readme]: saves.tests/README.md
[^upgrade-readme]: ksa-upgrade.tests/README.md
[^slnx]: ksa-mod-experiments.slnx
[^agents]: AGENTS.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-simstep]: KSA.SimStep (decompiled, build 2026.10.10.5554)
