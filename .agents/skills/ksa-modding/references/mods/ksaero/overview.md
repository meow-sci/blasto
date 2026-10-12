---
type: Mod
title: KSAero
description: Small StarMap mod that replaces stock atmospheric drag with a body-of-revolution lift and drag model by rewriting one call site in the vehicle force function
mod: ksaero
tags: [physics, aerodynamics, harmony, transpiler, imgui, starmap]
repo: KSAero
license: MIT
author: cairn5
loader: starmap
mod_id: KSAero
---

KSAero is a deliberately small aerodynamics mod by cairn5 (the GitHub owner of the repository; the
mod files carry no author field). It replaces the stock atmospheric drag on every vehicle with a
body-of-revolution model that has angle-of-attack drag, lift with a stall, tail-first behaviour and a
transonic drag rise. Rotational dynamics, buoyancy and ocean drag stay stock. The README presents it
as a stand-in until the game ships real aero.[^ksaero-readme] [^ksaero-inner-readme]

The interesting part for modders is how little it touches: one Harmony transpiler swaps one `call`
instruction inside `PhysicsStates.ComputeDerivatives`, the force function every vehicle integrator
calls, and the replacement has the same signature as the stock `PhysicsStates.ComputeDrag` it
displaces. The whole aero model lives in a game-free file that a console test project runs without
KSA installed.

## Install footprint

| Item | Value |
|---|---|
| Mod folder | `Documents\My Games\Kitten Space Agency\mods\KSAero\` (written by the build) |
| Files shipped | `KSAero.dll`, `mod.toml` (nothing else; no assets, no XML, no shaders) |
| `mod.toml` | `name = "KSAero"` plus `[StarMap] EntryAssembly = "KSAero"`[^ksaero-modtoml] |
| Manifest id | `KSAero`; the player enables it in the game's mod list or `manifest.toml`[^ksaero-readme] |
| Harmony id | `ksaero` |

StarMap reads `mod.toml`, takes `EntryAssembly` and loads `<EntryAssembly>.dll` from the mod folder
into a per-mod `ModAssemblyLoadContext` ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L41-L63)).[^starmap-runtimemod]

## Loader model and entry class

StarMap mod. The entry class is `KSAero.Mod`, marked `[StarMapMod]`, in
[KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L8-L52).[^ksaero-mod] KSA calls `ModLibrary.LoadAll` from the
`Program` constructor ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L942)), so
`[StarMapAllModsLoaded]` runs before any universe or vehicle exists.[^ksa-program]

| Hook | Method | Fires | What KSAero does there |
|---|---|---|---|
| `[StarMapAllModsLoaded]` | `OnFullyLoaded()` | Harmony postfix on `ModLibrary.LoadAll`, called from the `Program` constructor before any universe exists ([StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L17-L28)) | Warns on a game version mismatch, installs the transpiler, rolls back on any failure |
| `[StarMapAfterGui]` | `DrawGui(double dt)` | Harmony postfix on `Program.OnDrawUiViewports`, only while the HUD is shown ([StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L26-L36)) | Draws the readout window when the patch is live |
| `[StarMapUnload]` | `Unload()` | `ModLoader.Dispose`, when StarMap shuts down after the game's entry point returns ([StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L143-L151)), right after `StarMapCore.DeInit` has called the id-less `UnpatchAll()` ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)) | `UnpatchAll("ksaero")` and a log line; the unpatch is a no-op because every patch is already gone[^starmap-core] |

The replacement force function itself is not a StarMap hook: it runs wherever the game calls
`PhysicsStates.ComputeDerivatives`, which is inside the vehicle solver on `VehicleWorkerPool` threads,
once or twice per vehicle per physics substep (see [Body-of-revolution aerodynamics](features/body-of-revolution-aerodynamics.md)).
It never runs on the main thread, so it must not touch ImGui, Vulkan or main-thread `Vehicle` objects.

## Dependencies

| Dependency | How it is referenced | How it resolves at runtime |
|---|---|---|
| StarMap (`StarMap.API`) | NuGet `StarMap.API` 0.3.6 ([KSAero/KSAero.csproj](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/KSAero.csproj#L11-L14)) | Not shipped; StarMap's `ModAssemblyLoadContext.Load` returns assemblies already loaded in the default or core context first ([StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L47)) |
| Harmony | NuGet `Lib.Harmony` 2.4.2 | Not shipped; StarMap loads `0Harmony.dll` into the default context before the game ([StarMap/Program.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/Program.cs#L18)) |
| Game assemblies | `KSA`, `Brutal.Core.Numerics`, `Brutal.Core.Logging`, `Brutal.Core.Strings`, `Brutal.ImGui`, `BepuPhysics`, `BepuUtilities` from `$(KsaDir)` with `Private=false` ([KSAero/KSAero.csproj](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/KSAero.csproj#L16-L45)) | The running game's copies |
| KittenExtensions, ShaderExtensions, ModMenu, native libs | None | None |

## Architecture

| Subsystem | File | Thread | Role |
|---|---|---|---|
| Entry and lifecycle | [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs) | Main | Version warning, patch install and rollback, readout gating, unpatch |
| Patch | [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs) | Main (install), vehicle workers (`ComputeAirForces`) | Transpiler, IL argument scan, stock delegate, game-to-model adapter |
| Model | [KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs) | Any | Pure math: `AeroBody`, `AeroCoefficients`, `AeroParameters`, `AeroModel.Evaluate` |
| Readout | [KSAero/AeroReadout.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroReadout.cs) | Main (ImGui) | Re-evaluates the model for the controlled vehicle |
| Tests | [KSAero.Tests/Program.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero.Tests/Program.cs) | Console | Invariant checks and coefficient tables |

Data flow per substep: the vehicle solver calls `PhysicsStates.ComputeDerivatives` with the worker's
copy of the vehicle state; the rewritten call site hands the air velocity, density and surface area to
`AirForcePatch.ComputeAirForces`, which turns the bounding box into a cylinder, evaluates
`AeroModel.Evaluate` and adds the force to the `Disturbances` accumulator the integrator reads. Once
per frame, after the solver results are applied to the main-thread `Vehicle`, the readout evaluates
the same function on the controlled vehicle's state. The stock box model it replaces is built by
`VehicleProperties.RecomputeAerodynamicProperties` and applied in `PhysicsStates.ComputeDrag`
([current/decomp/KSA/VehicleProperties.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleProperties.cs#L115-L130),
[current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1089-L1111)).[^ksa-vehicleproperties] [^ksa-physicsstates]

## Features

| Feature | Category | Lifecycle |
|---|---|---|
| [ComputeDrag call-site transpiler](features/computedrag-call-site-transpiler.md) | patching | startup:all-mods-loaded, physics:vehicle-solver |
| [Patch refusal and rollback](features/patch-refusal-and-rollback.md) | patching | startup:all-mods-loaded, event:starmap-unload |
| [Stock ComputeDrag delegate fallback](features/stock-drag-delegate-fallback.md) | patching | startup:all-mods-loaded, physics:vehicle-solver |
| [Body-of-revolution aerodynamics](features/body-of-revolution-aerodynamics.md) | physics | physics:vehicle-solver |
| [Mach number from the game atmosphere](features/mach-from-game-atmosphere.md) | physics | physics:vehicle-solver, frame:draw-ui |
| [Aero readout window](features/aero-readout-window.md) | ui | frame:draw-ui |
| [Game version check](features/game-version-check.md) | getting-started | startup:all-mods-loaded |
| [Headless model tests](features/headless-model-tests.md) | testing-and-tooling | none (runs outside the game) |
| [Build and deploy targets](features/mod-deploy-targets.md) | getting-started | none (build time) |

## Build, test and tooling

- .NET 10, `x64`, nullable enabled. `dotnet build KSAero` compiles against the DLLs in `KsaDir`
  (default `C:\Program Files\Kitten Space Agency`) and deploys into the mods folder through
  [build/ModDeploy.targets](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/build/ModDeploy.targets); `-p:DeployToMods=false` skips the copy.
  Release builds also produce `KSAero.zip`.[^ksaero-csproj] [^ksaero-moddeploy]
- `dotnet run --project KSAero.Tests` compiles [KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs) into a
  console app with no game references, checks the model's invariants and prints the README's
  tables.[^ksaero-tests-csproj] [^ksaero-tests-program]
- The deploy plumbing is Windows-specific (`%USERPROFILE%`, PowerShell). See
  [Build and deploy targets](features/mod-deploy-targets.md).

## Compatibility

- The README and `Mod.TestedGameVersion` name KSA `v2026.10.7.5541`.[^ksaero-readme] Claims in this
  bundle were checked against the decompiled build 2026.10.10.5554: both `ComputeDrag` calls are still
  in `ComputeDerivatives` and the transpiler's shape check holds. On 5554 the version check logs a
  warning and the patch still installs.
- Breakage pattern to expect: any game change that adds, removes or rewrites a `ComputeDrag` call in
  `ComputeDerivatives`, changes either signature, or renames `PhysicsEnvironment.AtmosphericDensity`.
  The mod then refuses to patch and stock aero runs (see
  [Patch refusal and rollback](features/patch-refusal-and-rollback.md)).
- Another mod transpiling the same call site would make KSAero find zero matching calls and refuse.
- KSA-AdvancedFlightComputer measures its drag table by calling `PhysicsStates.ComputeDerivatives` on a
  copy of the vehicle state ([AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs#L334)),
  so it picks up KSAero's forces with no coupling between the two mods.[^afc-aerosweep]

## Secondary sources

- [README.md](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/README.md): the clearest write-up of the stock model's flaws, the replacement
  equations, a coefficient table against stock, and the hook rationale. Read it first.
- XML doc comments in [KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs) and
  [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs) explain each constant and the call-site
  choice.
- The comment header of [build/ModDeploy.targets](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/build/ModDeploy.targets) documents its inputs.

[^ksaero-readme]: KSAero/README.md
[^ksaero-inner-readme]: KSAero/README.MD
[^ksaero-modtoml]: KSAero/mod.toml
[^starmap-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^starmap-core]: StarMap.Core/StarMapCore.cs
[^ksaero-mod]: KSAero/Mod.cs
[^ksa-vehicleproperties]: KSA.VehicleProperties (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksaero-csproj]: KSAero/KSAero.csproj
[^ksaero-moddeploy]: build/ModDeploy.targets
[^ksaero-tests-csproj]: KSAero.Tests/KSAero.Tests.csproj
[^ksaero-tests-program]: KSAero.Tests/Program.cs
[^afc-aerosweep]: AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
