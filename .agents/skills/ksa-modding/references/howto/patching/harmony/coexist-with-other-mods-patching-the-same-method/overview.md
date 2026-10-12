---
type: Game Feature
title: Coexisting with other mods that patch the same method
description: How Harmony orders and re-runs patches when several mods target one game method, and how KSA mods order, share and merge patches so that features and neighbouring mods do not break each other
category: patching
group: harmony
topic: coexist-with-other-mods-patching-the-same-method
demonstrated_by:
  - mods/gatos/features/solver-phase-command-drain
  - mods/ksastructures/features/terrain-height-roof-override
  - mods/shaderextensions/features/rendergame-render-pass-transpiler
  - mods/ksaero/features/computedrag-call-site-transpiler
  - mods/unscience/features/lib-part-render-filter
  - mods/unscience/features/unscience-consolidated-patcher
  - mods/unscience/features/lib-physics-frame-hook
  - mods/unscience/features/lib-hotkey-guard
  - mods/ksa-advancedflightcomputer/features/post-solver-vehicle-tick
  - mods/ksa-advancedflightcomputer/features/feature-gated-patch-sets
  - mods/ksa-advancedflightcomputer/features/flight-computer-command-sink
lifecycle:
  - startup:all-mods-loaded
  - frame:dispatch-solvers
  - frame:apply-solvers
  - physics:vehicle-solver
  - frame:prepare
  - frame:render
game_types:
  - KSA.Universe
  - KSA.Program
  - KSA.PhysicsStates
  - KSA.PartTreeRenderData
  - KSA.Vehicle
  - KSA.FlightComputer
tags: [harmony, coexistence, priority, transpiler, shared-seam, patching]
---

Every mod that patches a hot game method shares it with the others. Harmony does the arithmetic of combining the
patches, but the combination has rules mods must design for: prefixes and finalizers run in priority order, a
prefix that returns `false` stops later `bool` prefixes, and every registered transpiler is re-run against the
original IL whenever any patch on the method is added or removed. This topic collects five ways KSA mods handle that:
ordering with `Priority`, anchors that tolerate earlier transpilers, idempotent shared installs, one shared tick
with per-feature flags, and one postfix that merges every writer.

## Engine background

| Type | Member | Why it is shared |
|---|---|---|
| `Universe` | `ExecuteNextVehicleSolvers`, `ApplyVehicleSolvers`, `GetJobSimStep` | The solver handoff in `Program.PrepareFrame` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1950)[^ksa-universe], [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2139)) |
| `Program` | `PrepareFrame`, `RenderGame` | One method per frame with seams between calls ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2222)[^ksa-program], [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4947-L4952)) |
| `Vehicle` | `PrepareWorker` | Snapshots state for the worker ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2398)[^ksa-vehicle]) |
| `PartTreeRenderData` | `Compose`, `ComposeDynamic`, `ComposeGlass` | Appends draw ranges that filters compact ([current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1258-L1264)[^ksa-parttreerenderdata]) |
| `FlightComputer` | `ComputeControl` | Called on solver threads with by-ref arguments ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L400-L404)[^ksa-flightcomputer]) |
| `PhysicsStates` | `ComputeDerivatives` | Two `ComputeDrag` calls ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1002-L1017)[^ksa-physicsstates]) |

Phases from [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md): `PrepareFrame` applies results (`frame:apply-solvers`), polls input and dispatches the next step
(`frame:dispatch-solvers`) on the main thread; `RenderGame` records commands in `frame:render`; the vehicle solver
and its flight-computer calls run on workers in `physics:vehicle-solver`.

## Harmony rules that shape coexistence (checked against Lib.Harmony 2.4.2)

StarMap references Lib.Harmony 2.4.2 ([StarMap.Core/StarMap.Core.csproj](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMap.Core.csproj#L11)[^starmap-starmap-core]). Local tests of the
version showed:

| Question | Observed |
|---|---|
| Prefix order | `Priority.First`, default, `Priority.Last` |
| Finalizer order | the same, `First` to `Last` |
| Equal priority | the prefix registered first ran first; `before = new[] { "other.id" }` on the later patch reversed it |
| A prefix returns `false` | later prefixes that return `bool` did not run (including one that also takes `__runOriginal`); later `void` prefixes, postfixes and finalizers did |
| A second transpiler is added | the first transpiler ran again, on the original IL; the second saw the first's output |
| A transpiler's owner is unpatched by id | the remaining transpilers ran again |

The Harmony-specific consequence is that **ordering is a design decision, not luck**: patches that must precede or
follow everyone say so with priority; transpilers must tolerate being run again and must tolerate that an earlier
transpiler already changed the code they are looking for.

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Priority.First prefixes and Priority.Last finalizers around shared seams](coexist-with-other-mods-patching-the-same-method-1.md) | gatOS, KSAStructures | `Priority.First` prefix and `Priority.Last` finalizer around shared seams | `frame:dispatch-solvers` | Cheap; priority is a convention, not a lock |
| 2 | [Anchors that one mod can consume (and what the next transpiler then sees)](coexist-with-other-mods-patching-the-same-method-2.md) | ShaderExtensions, KSAero | Anchors that one mod can consume, and what the next transpiler sees | `startup:starmap-before-main`, `frame:render` | Fails loudly by name; two mods cannot share one consumed anchor |
| 3 | [Idempotent shared installs with a registration API](coexist-with-other-mods-patching-the-same-method-3.md) | unscience | Idempotent shared installs with a registration API | `startup:all-mods-loaded`, `frame:render` | Needs a shared library; works inside one assembly |
| 4 | [One shared tick with per-feature enable flags set from TryApply results](coexist-with-other-mods-patching-the-same-method-4.md) | KSA-AdvancedFlightComputer | One shared tick, flags set from `TryApply` results | `frame:apply-solvers` | Each feature depends on a flag being honest |
| 5 | [One worker-side postfix merging every writer](coexist-with-other-mods-patching-the-same-method-5.md) | KSA-AdvancedFlightComputer | One worker-side postfix merging every writer | `physics:vehicle-solver` | Worker-thread rules apply to every writer |

## See also

- [Writing a Harmony transpiler for a KSA method](../../transpilers/write-a-harmony-transpiler/overview.md) and [Identifying the right call in a method that makes several similar calls](../../transpilers/identify-a-call-by-its-argument-il/overview.md) for the transpiler side of approach 2
- [Harmony finalizers, cleanup and scoped overrides](../run-cleanup-or-recover-from-exceptions-with-a-finalizer/overview.md) for the finalizers used in approach 1
- [Isolating patch failures and keeping patch bodies from throwing](../../safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md) and [Refusing a patch and rolling back when the game changes](../../safety/refuse-a-patch-and-roll-back-when-the-game-changes/overview.md) for per-feature owners and rollback
- Related tasks: mutate-vehicles-at-a-physics-safe-point, drive-the-flight-computer-and-attitude-modes-from-a-mod, create-a-minimal-starmap-mod
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Mutate vehicles at a physics-safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md) - related topic
- [Drive the flight computer and attitude modes from a mod](../../../flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md) - related topic
- [Create a minimal StarMap mod](../../../getting-started/loader/create-a-minimal-starmap-mod/overview.md) - related topic

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-parttreerenderdata]: KSA.PartTreeRenderData (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^starmap-starmap-core]: StarMap/StarMap.Core/StarMap.Core.csproj
