---
type: Mod Feature
title: Headless simulation tests and the Sim/Ksa boundary
description: Split the mod into a KSA-free Sim folder and a KSA-facing Ksa folder, link Sim wholesale into an xUnit project that references no game assembly, and drive the car model on a rigid-body rig so physics changes are tested without the game, with CI and hooks running the same checks
mod: ksacars
category: testing-and-tooling
techniques: [headless-tests, ci]
hooks: []
lifecycle: []
game_types: []
tags: [testing, xunit, architecture, boundary, ci, git-hooks, headless]
---

KSACars keeps everything that can be expressed without the game in `src/KSACars/Sim/` (the car
model, profiles, steering-wheel grip, righting, rockets, hatches, headlights, terrain-ray maths,
picking, the bridge's command parser) and everything that touches KSA in `src/KSACars/Ksa/`. The
test project compiles `Sim/**` directly and references no KSA assembly, so a `using KSA;` under
`Sim/` breaks the test build: the boundary enforces itself, and a new `Sim/` file is tested the
moment it exists.[^ksacars-tests-csproj][^ksacars-claude]

## How it works

**The test project.** `tests/KSACars.Tests/KSACars.Tests.csproj` (xUnit, net10.0) adds
`<Compile Include="..\..\src\KSACars\Sim\**\*.cs" Link="Linked\..." />` and references only
`Brutal.Core.Numerics.dll` and `Brutal.Core.Common.dll` (for `double3` and friends), with runtime
assets, because the tests execute the maths.[^ksacars-tests-csproj]

**The rig.** `BuggyDriveTests.Rig` is a minimal rigid body: position, velocity, attitude, spin, the
same mass and inertia KSA derives from the GameData XML (a solid sphere for the buggy, a solid
cuboid for the Eldorado) and the same centres of mass. Each step it builds `WheelContact`s against
flat ground, calls `BuggyDrive.Step`, applies the impulse, adds gravity and integrates. A step-length
pattern can be supplied to mimic uneven frames.[^ksacars-drivetests]

**What is tested.** Settling and bouncing on the springs at several `dt`, pulling away straight and
upright, steering without rolling, the Eldorado's floaty settle, a yaw kick dying away rather than
rocking (fails against a sphere's inertia), a full-lock flick at speed turning rather than scrubbing,
a steering reversal at speed without lifting a wheel, braking, arm/coil/wheel pose geometry; plus
`SteeringGripTests`, `RightingTests`, `LiftTests`, `HoverTests`, `BoostTests`, `DownforceTests`,
`HatchTests`, `HeadlightsTests`, `ScoopTests`, `PadSurfaceTests`, `TerrainRayTests`,
`TerrainMaskTests`, `CursorAimTests`, `PickingTests`, `DrawAnchorTests`, `FrameLatchTests` and
`BridgeCommandTests`.

**Checks around it.**

| Check | What it enforces |
|---|---|
| `tools/check-boundary.sh` | Textual stand-in for the test-build guard on hosted CI without the game: no `using KSA` or `KSA.X` outside comments under `Sim/`[^ksacars-checkboundary] |
| `tools/check-comments.sh`, `tools/check-docs.sh` | Comment rules (no history in comments) and documented facts (layout table, API counts, KSA build) against reality[^ksacars-checkcomments][^ksacars-checkdocs] |
| `tools/check-all.sh` | Every CI check in order; game-dependent ones (build, test, API surface, lock) skipped with a notice when the assemblies are missing[^ksacars-checkall] |
| `.githooks/pre-push` | Runs `check-all.sh` before every push[^ksacars-prepush] |
| `tools/check-commit-msg.sh` | Conventional Commits, as a `commit-msg` hook and a CI job[^ksacars-commitmsg] |
| `.github/workflows/ci.yml` | Commit messages; tooling and assets (no game needed); build, test, validate parts, API surface and package against a private assemblies checkout[^ksacars-ci] |

`tools/test.sh` runs the suite in Release, the configuration the shipped mod compiles `Sim/` in.[^ksacars-test-sh]

## Lifecycle and timing

Build and CI time only. The tests never load the game; they run anywhere `dotnet test` and the two
Brutal numerics DLLs are available.

## Key code

| Path | Role |
|---|---|
| [tests/KSACars.Tests/KSACars.Tests.csproj](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tests/KSACars.Tests/KSACars.Tests.csproj) | Links `Sim/**`, references no KSA assembly |
| [tests/KSACars.Tests/BuggyDriveTests.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tests/KSACars.Tests/BuggyDriveTests.cs#L224-L283) | The rigid-body rig |
| [tools/check-boundary.sh](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/check-boundary.sh) | Boundary check for CI without the game |
| [tools/check-all.sh](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/check-all.sh) | One list of checks for hooks and CI |
| [.github/workflows/ci.yml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/.github/workflows/ci.yml) | CI jobs |
| [CHECKLIST.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/CHECKLIST.md) | What has and has not been driven in game[^ksacars-checklist] |

## Snippets

```xml
<!-- tests/KSACars.Tests/KSACars.Tests.csproj L19-L36 -->
  <!--
    Everything under src/KSACars/Sim is free of KSA types by construction, so the whole
    folder is linked in and exercised headlessly on any platform.

    The wildcard is the point. This project references no KSA assembly, so the moment a file
    under Sim/ reaches for one, *the test build fails* - the boundary enforces itself instead
    of relying on someone remembering a rule. It also means a new simulation file is tested the
    moment it exists, with no build plumbing to forget.
  -->
  <ItemGroup>
    <Compile Include="..\..\src\KSACars\Sim\**\*.cs" Link="Linked\%(RecursiveDir)%(Filename)%(Extension)" />
  </ItemGroup>

  <!-- double3 lives here. Runtime assets included: the tests actually execute this. -->
  <ItemGroup>
    <Reference Include="$(KsaDllDir)Brutal.Core.Numerics.dll" />
    <Reference Include="$(KsaDllDir)Brutal.Core.Common.dll" />
  </ItemGroup>
```

```csharp
// tests/KSACars.Tests/BuggyDriveTests.cs L259-L283
        public void Step(DriveInput input)
        {
            double dt = Dt;
            doubleQuat toBody = doubleQuat.Conjugate(Attitude);
            double3 comWorld = Position;
            double3 upBody = toBody * new double3(1, 0, 0);
            double3 velBody = toBody * Velocity;

            WheelContact[] contacts = new WheelContact[Hubs.Length];
            for (int i = 0; i < Hubs.Length; i++)
            {
                double3 world = comWorld + (Attitude * Hubs[i]);
                contacts[i] = new WheelContact(true, world.X, upBody, velBody + Vec.Cross(Spin, Hubs[i]));
            }

            DriveImpulse j = Drive.Step(input, contacts, Hubs, new double3(1, 0, 0), new double3(0, 1, 0),
                                        RigMass, Gravity, 1.225, dt);
            Velocity += Attitude * (j.Linear / RigMass);
            Spin += new double3(j.Angular.X / Inertia.X, j.Angular.Y / Inertia.Y, j.Angular.Z / Inertia.Z);

            Velocity += new double3(-Gravity * dt, 0, 0);
            Position += Velocity * dt;
            double rate = Vec.Len(Spin);
            if (rate > 0.0) Attitude *= doubleQuat.CreateFromAxisAngle(Spin / rate, rate * dt);
        }
```

## Game APIs touched

None. The point of the split is that the tested code touches no KSA type; only
`Brutal.Core.Numerics` value types (`double3`, `doubleQuat`) cross the boundary.

## Pitfalls and version notes

- **A passing suite is not a verified behaviour.** CLAUDE.md requires a behaviour fix to be seen in
  game before it is committed as a fix, and a regression test to fail against the old code;
  `CHECKLIST.md` records what has been driven. Nothing has been seen in game since the repository
  split.[^ksacars-claude]
- **The rig is not KSA's integrator.** It applies the impulse and gravity once per step; the engine's
  sub-stepping, contacts and rails are not modelled, so failures in that gap only appear in game.
- **Bugs in `Ksa/` are unreachable by tests** by design (the modding notes give an example of a
  pattern match against `Celestial` that compiles and silently returns nothing).
- Hosted CI has no game DLLs; the boundary script is the only guard there, and the full test run
  needs the private assemblies checkout.

## See also

- [Wheel, suspension and tyre model](./wheel-suspension-tyre-model.md)
- [API surface pinning and KSA upgrade diffing](./api-surface-pinning-and-diff.md)
- [Package, deploy and release](./package-deploy-release.md)
- [File-drop command bridge](./file-drop-command-bridge.md) (in-game testing)
- [Fly a rigid-body rig through the mod's own drive step with the engine's mass and inertia](../../../howto/testing-and-tooling/offline-tests/run-mod-simulation-in-a-headless-rig/run-mod-simulation-in-a-headless-rig-1.md) - how-to article demonstrating this feature
- [Pair check-all with a test script and a CI that builds against a private assemblies repo](../../../howto/testing-and-tooling/offline-tests/run-repo-checks-from-a-pre-push-hook-and-ci/run-repo-checks-from-a-pre-push-hook-and-ci-2.md) - how-to article demonstrating this feature
- [Link a game-free Sim folder wholesale into an xUnit project](../../../howto/testing-and-tooling/offline-tests/unit-test-mod-logic-without-game/unit-test-mod-logic-without-game-1.md) - how-to article demonstrating this feature

[^ksacars-tests-csproj]: tests/KSACars.Tests/KSACars.Tests.csproj
[^ksacars-drivetests]: tests/KSACars.Tests/BuggyDriveTests.cs
[^ksacars-checkboundary]: tools/check-boundary.sh
[^ksacars-test-sh]: tools/test.sh
[^ksacars-checkall]: tools/check-all.sh
[^ksacars-prepush]: .githooks/pre-push
[^ksacars-commitmsg]: tools/check-commit-msg.sh
[^ksacars-ci]: .github/workflows/ci.yml
[^ksacars-checkcomments]: tools/check-comments.sh
[^ksacars-checkdocs]: tools/check-docs.sh
[^ksacars-checklist]: CHECKLIST.md
[^ksacars-claude]: CLAUDE.md
