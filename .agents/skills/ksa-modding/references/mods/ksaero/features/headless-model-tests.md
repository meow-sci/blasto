---
type: Mod Feature
title: Headless model tests
description: A console project that source-links the game-free AeroModel.cs, checks physical invariants of the aero model over thousands of attitudes and Mach numbers, and prints coefficient tables, all without KSA installed
mod: ksaero
category: testing-and-tooling
techniques: [headless-harness, source-linking, invariant-testing]
hooks: []
lifecycle: []
game_types: []
tags: [testing, headless, invariants, dotnet, tooling]
---

`dotnet run --project KSAero.Tests` runs the whole aerodynamic model on a desktop with no game, no
StarMap and no Harmony. The project is a plain console app (no test framework) that compiles
`KSAero/AeroModel.cs` into itself, asserts the model's physical invariants, prints `PASS` or a failure
count, prints the coefficient tables the README quotes, and returns exit code 0 or 1.[^ksaero-tests-program] [^ksaero-readme]

What makes this possible is a design rule in the mod: the math file has no game types. Everything that
touches KSA (bounding box to cylinder, frames, `Disturbances`) lives in
[KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs), and the model takes plain doubles.[^ksaero-airforcepatch]

## How it works

- **Source link, not project reference.** The test csproj has no game or mod references; it includes
  the model file directly with `<Compile Include="..\KSAero\AeroModel.cs" Link="AeroModel.cs" />`
  ([KSAero.Tests/KSAero.Tests.csproj](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero.Tests/KSAero.Tests.csproj#L8-L11)). Referencing
  `KSAero.csproj` instead would drag in `KSA.dll` and friends from `KsaDir`.[^ksaero-tests-csproj]
- **Game-free model.** [KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs) has no `using` directives at all
  and only calls `System.Math`; `AeroBody`, `AeroCoefficients`, `AeroParameters` and `AeroModel` are
  `public`.[^ksaero-aeromodel]
- **Harness.** A local `Check(bool, string)` counts and prints failures; `At(body, alphaDeg, mach,
  rollDeg)` builds the unit velocity direction for a nose pitched up by alpha and rolled by phi;
  `StockCd` re-implements the stock box model for side-by-side tables
  ([KSAero.Tests/Program.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero.Tests/Program.cs#L4-L35)). The stock formula it mirrors is
  `VehicleProperties.RecomputeAerodynamicProperties` plus `ComputeDrag`'s `0.1 * surfaceArea` term
  ([current/decomp/KSA/VehicleProperties.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleProperties.cs#L115-L130)).[^ksa-vehicleproperties]
- **Bodies.** A slender launcher (50 m by 3.7 m), a stubby lander (6 by 3), a capsule (3 by 4) and a
  needle (80 by 2) ([KSAero.Tests/Program.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero.Tests/Program.cs#L37-L40)).
- **Checked while writing this page.** Run from a scratch copy of the two files on macOS with the
  .NET SDK 10.0.100, it printed `PASS` and reproduced the README's launcher table (for example C_D
  0.435 against stock 7.38 at zero alpha).
- **Output.** Tables per body at Mach 0.5 against stock at roll 0 and 45, and a Mach sweep of
  `C_D` at 0 and 180 degrees and `C_L` at 5, 30 and 60 degrees
  ([KSAero.Tests/Program.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero.Tests/Program.cs#L154-L180)).

### Invariants checked

Line numbers refer to [KSAero.Tests/Program.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero.Tests/Program.cs).

| Invariant | Lines |
|---|---|
| Zero alpha: no lift, `C_D = Cd0 + Cf * 4 L/D`, force straight back | `L44-L49` |
| Tail-first: `C_D = CdTail + Cf * 4 L/D`, flagged tail-first, force along +X | `L51-L53` |
| Axisymmetry: roll changes nothing but direction, every 0.25 degrees of alpha at five Mach numbers | `L56-L68` |
| The force vector decomposes into exactly `-C_D` along the airspeed and `C_L` along the lift axis; no side force in the pitch plane; drag never negative | `L70-L80` |
| Subsonic drag never falls as alpha grows (to 90 degrees for L/D 5 and up, 50 otherwise, 0.5% tolerance) | `L83-L91` |
| Continuity in alpha and in Mach (0.001 steps to Mach 8) | `L93-L109` |
| Transonic peak above both subsonic and supersonic drag | `L112-L116` |
| A short body loses lift through the stall | `L118-L123` |
| Nose up gives lift up | `L126` |
| Engine-first lift rises monotonically for L/D 2 to 20 at ten Mach numbers | `L128-L144` |
| Capsule heat-shield-first L/D between -0.45 and -0.1 | `L146-L152` |

## Lifecycle and timing

Not part of the game frame: the project runs at development time under `dotnet run`, on any machine
with the .NET 10 SDK. Both projects are in [KSAero.sln](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero.sln), so an IDE builds them
together, but nothing deploys the test project.[^ksaero-sln] The runtime counterpart of what it tests
runs in `physics:vehicle-solver` (see [Body-of-revolution aerodynamics](body-of-revolution-aerodynamics.md)),
which is exactly why testing it in-game is impractical: the code runs on worker threads, per substep,
with no UI.

## Key code

| Path | Role |
|---|---|
| [KSAero.Tests/KSAero.Tests.csproj](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero.Tests/KSAero.Tests.csproj) | Console app that source-links the model |
| [KSAero.Tests/Program.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero.Tests/Program.cs#L4-L35) | Harness helpers and the stock reference |
| [KSAero.Tests/Program.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero.Tests/Program.cs#L42-L152) | Invariant checks |
| [KSAero.Tests/Program.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero.Tests/Program.cs#L154-L180) | Verdict, tables, exit code |

## Snippets

```xml
<!-- KSAero.Tests/KSAero.Tests.csproj L1-L12 -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <!-- The model has no game references, so it is compiled in directly and runs without KSA. -->
    <Compile Include="..\KSAero\AeroModel.cs" Link="AeroModel.cs" />
  </ItemGroup>
</Project>
```

```csharp
// KSAero.Tests/Program.cs L4-L23
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

int failures = 0;
void Check(bool ok, string what)
{
    if (!ok)
    {
        failures++;
        Console.WriteLine($"FAIL  {what}");
    }
}

const double Deg = Math.PI / 180.0;

// Nose pitched up by alpha above the airspeed, rolled by phi about the body axis.
AeroCoefficients At(AeroBody body, double alphaDeg, double mach, double rollDeg = 0.0)
{
    double a = alphaDeg * Deg, p = rollDeg * Deg;
    return AeroModel.Evaluate(in body, Math.Cos(a), Math.Sin(a) * Math.Sin(p), -Math.Sin(a) * Math.Cos(p), mach);
}
```

```csharp
// KSAero.Tests/Program.cs L63-L80
            // Axisymmetric: rolling the vehicle changes nothing but the direction.
            for (double roll = 15.0; roll < 360.0; roll += 37.0)
            {
                var r = At(body, alpha, mach, roll);
                Check(Math.Abs(r.Lift - c.Lift) < 1e-12 && Math.Abs(r.Drag - c.Drag) < 1e-12, $"{name}: roll changes coefficients at a={alpha} r={roll}");
            }

            // The force vector carries exactly C_L and C_D. Velocity is u, lift axis is e_L.
            double ux = Math.Cos(a), uz = -Math.Sin(a);
            double sign = ux < 0 ? -1.0 : 1.0;
            double cosLead = Math.Abs(ux), sinLead = Math.Abs(uz);
            double ex = sign * sinLead, ez = cosLead;
            double alongU = c.ForceX * ux + c.ForceZ * uz;
            double alongL = c.ForceX * ex + c.ForceZ * ez;
            Check(Math.Abs(alongU + c.Drag) < 1e-9, $"{name}: force along airspeed != -C_D at a={alpha} M={mach}");
            Check(Math.Abs(alongL - c.Lift) < 1e-9, $"{name}: force along lift axis != C_L at a={alpha} M={mach}");
            Check(Math.Abs(c.ForceY) < 1e-12, $"{name}: side force in the pitch plane at a={alpha}");
            Check(c.Drag >= 0.0, $"{name}: negative drag at a={alpha} M={mach}");
```

## Game APIs touched

None at run time; that is the point. `StockCd` mirrors
`VehicleProperties.RecomputeAerodynamicProperties`
([current/decomp/KSA/VehicleProperties.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleProperties.cs#L115-L130))
by hand, so a game change to the stock coefficients would not be noticed by the tests.

## Pitfalls and version notes

- **Keep the model file free of game types.** One `using KSA;` or a `double3` in `AeroModel.cs` breaks
  the test build. Put adapters in the patch file.
- **Invariant culture.** The program sets `CultureInfo.CurrentCulture` to invariant so the tables print
  the same everywhere.
- **No test framework.** The exit code is the CI contract; wire `dotnet run --project KSAero.Tests` into
  a pipeline as-is.
- **The integration is untested.** The transpiler, frames and the force application in
  `ComputeAirForces` are not covered; only the coefficient model is.

## See also

- [Body-of-revolution aerodynamics](body-of-revolution-aerodynamics.md)
- [Build and deploy targets](mod-deploy-targets.md)
- [Check a model with a console test executable that returns a verdict as its exit code](../../../howto/testing-and-tooling/offline-tests/unit-test-mod-logic-without-game/unit-test-mod-logic-without-game-2.md) - how-to article demonstrating this feature

[^ksaero-tests-program]: KSAero.Tests/Program.cs
[^ksaero-readme]: KSAero/README.md
[^ksaero-airforcepatch]: KSAero/AirForcePatch.cs
[^ksaero-tests-csproj]: KSAero.Tests/KSAero.Tests.csproj
[^ksaero-aeromodel]: KSAero/AeroModel.cs
[^ksa-vehicleproperties]: KSA.VehicleProperties (decompiled, build 2026.10.10.5554)
[^ksaero-sln]: KSAero.sln
