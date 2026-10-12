---
type: Game Feature
title: Changing behaviour of a game method marked AggressiveInlining
description: Why a Harmony prefix or postfix on a KSA method marked AggressiveInlining can silently never run, what a local test shows about when the JIT bypasses a detour, and four patterns the pinned mods use to change behaviour without depending on it
category: patching
group: internals
topic: patch-around-aggressive-inlining
demonstrated_by:
  - mods/ksaero/features/computedrag-call-site-transpiler
  - mods/ohmystars/features/attitude-mode-handoff-patches
  - mods/ksa-advancedflightcomputer/features/post-solver-vehicle-tick
  - mods/ksa-advancedflightcomputer/features/validated-reflection-handles
  - mods/unscience/features/garrys-torch-scoped-collision-suppression
  - mods/unscience/features/sphinx-static-colliders
lifecycle: [startup:all-mods-loaded, physics:vehicle-solver, frame:apply-solvers, physics:bepu]
game_types:
  - KSA.PhysicsStates
  - KSA.FlightComputer
  - KSA.Vehicle
  - KSA.ConstraintSim
  - KSA.NarrowPhaseCallbacks
  - KSA.Universe
  - KSA.VehicleUpdateTask
  - KSA.PhysicsBubble
tags: [harmony, inlining, aggressiveinlining, jit, silent-failure, transpiler, postfix, finalizer]
---

Many hot KSA methods are declared `[MethodImpl(MethodImplOptions.AggressiveInlining)]`: 54 in `PhysicsStates.cs`, 45 in `Vehicle.cs`, 38 in `FlightComputer.cs` in build 2026.10.10.5554. Harmony redirects a method's own entry
point. A caller into which the JIT has already copied the callee's body does not go through that entry, so a prefix or postfix on the callee can be skipped without any error. This topic shows how to tell whether
that applies to a target, and four ways the pinned mods change behaviour without relying on a detour of the inlined method.

## Choosing a form

| Situation | Form | Used in |
|---|---|---|
| The inlined callee has one or two known callers and you need only some of them | Rewrite the caller's call site with a transpiler ([Rewrite the one caller's call site instead of the inlined callee](patch-around-aggressive-inlining-1.md)) | KSAero |
| The inlined callee is reached by several non-inlined public entry points | Patch those entry points too, and list the paths that stay exposed ([Patch the non-inlined entry points that reach the inlined method too](patch-around-aggressive-inlining-2.md)) | Oh My Stars |
| You want to run once per frame after a per-vehicle step that ends in an inlined method | Postfix the single non-inlined outer method ([Postfix the single non-inlined outer method instead of a per-vehicle callee](patch-around-aggressive-inlining-3.md)) | KSA-AdvancedFlightComputer |
| The inlined code is a struct callback inside a larger non-inlined pass | Bracket the enclosing methods with a prefix and a finalizer ([Bracket the non-inlined methods that enclose an inlined struct callback](patch-around-aggressive-inlining-4.md)) | unscience (Garry's Torch) |

## Engine background

**What is marked.** The targets in this topic, with the attribute in the decompiled source:

| Method | Marked | Source |
|---|---|---|
| `PhysicsStates.ComputeDrag` (private static) | yes | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1089-L1090)[^ksa-physicsstates] |
| `FlightComputer.SetNullRot`, `RateHold`, `TrackTarget` | yes | [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L331-L337)[^ksa-flightcomputer], [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1978-L1983), [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1993-L1998) |
| `Vehicle.UpdateFromTaskResultsSynchronized` | yes | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2546-L2547)[^ksa-vehicle] |
| `NarrowPhaseCallbacks.AllowContactGeneration` (internal struct) | yes | [current/decomp/KSA/NarrowPhaseCallbacks.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NarrowPhaseCallbacks.cs#L23-L24)[^ksa-narrowphasecallbacks] |
| `ConstraintSim.IsGroundSurfaceFor` | yes | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L168-L169)[^ksa-constraintsim] |
| `ConstraintSim.DetectCollisions`, `Simulate` | no | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L870), [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L887) |
| `Universe.ApplyVehicleSolvers` (static) | no | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922)[^ksa-universe] |
| `PhysicsStates.ComputeDerivatives` | no | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L956) |

To list the marked methods of a type, grep the decompiled source for the attribute and read the next line (a shell sketch; the decompile lives in `ksa-game-assemblies`):

```bash
grep -n -A1 "AggressiveInlining" current/decomp/KSA/FlightComputer.cs
```

**When does the JIT bypass a detour?** A local experiment with Lib.Harmony 2.4.2 on .NET 10: a callee marked `AggressiveInlining`, a prefix on it, and callers compiled in different ways. "Hits" counts how often the
prefix ran.

```text
// observed output of a local test (an experiment project, not part of any mod repository)
assembly compiled with optimizations
A  caller JIT-ed before patch (AggressiveOptimization): prefix hits = 0
A2 caller JIT-ed before patch (default tiering):       prefix hits = 1
B  AggressiveOptimization caller first run AFTER the patch:   prefix hits = 1
D  round 0: 100 calls of a default-tiering caller after the patch: prefix hits = 100
C  direct call to the patched method:                  prefix hits = 1

assembly compiled with DisableOptimizations (like KSA.dll)
A  caller JIT-ed before patch (AggressiveOptimization): prefix hits = 1
```

Reading it: the prefix was skipped only in case A, where the caller had been compiled with optimizations, and therefore inlined the callee, before the patch was applied. A caller first compiled after the patch (B), a
caller still at the first tier (A2), and a caller that ran 100 times after the patch (D) all reached the prefix. With `DisableOptimizations` in the assembly, as in KSA.dll, even case A reached the prefix. This is
one observation of the runtime, not a guarantee.

**What KSA.dll looks like.** In build 2026.10.10.5554 `KSA.dll` carries a `DebuggableAttribute` with value `0x107`, which includes `DisableOptimizations`, so the JIT normally compiles KSA's own methods without
inlining. The risk is therefore latent in this build and real in any later release-optimized build, which is why each mod below writes its patch to be correct either way. That claim about this build's inlining
behaviour has not been verified in the running game.

**Detecting a silent no-op.** Count the prefix: an `Interlocked.Increment` in the patch body, read by a test or logged once after the first frame, turns "the patch went silent" from invisible into a failing check.
AFC's own note records that a private method later marked `AggressiveInlining` still resolves by reflection, so a null check passes while the patch does nothing, and only a behavioural test catches it
([Postfix the single non-inlined outer method instead of a per-vehicle callee](patch-around-aggressive-inlining-3.md)). Testing patches without the game is covered by the topic test-harmony-patches-without-the-game.

Phases from [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md): installs run at `startup:all-mods-loaded`; the patched work runs in `physics:vehicle-solver` and `physics:bepu` on workers, or at `frame:apply-solvers` on the main thread.

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Rewrite the one caller's call site instead of the inlined callee](patch-around-aggressive-inlining-1.md) | KSAero | Transpile the caller, swap one call operand | `startup:all-mods-loaded`, `physics:vehicle-solver` | Correct inlined or not; must identify the call by its arguments |
| 2 | [Patch the non-inlined entry points that reach the inlined method too](patch-around-aggressive-inlining-2.md) | Oh My Stars | Prefix every non-inlined entry point as well | `startup:all-mods-loaded`, `frame:input` | Cheap; some paths stay exposed and a test must confirm |
| 3 | [Postfix the single non-inlined outer method instead of a per-vehicle callee](patch-around-aggressive-inlining-3.md) | KSA-AdvancedFlightComputer | Pre/postfix on non-inlined `Universe.ApplyVehicleSolvers` | `frame:apply-solvers` | Once per frame, main thread; no per-vehicle detail |
| 4 | [Bracket the non-inlined methods that enclose an inlined struct callback](patch-around-aggressive-inlining-4.md) | unscience | Prefix and finalizer around `DetectCollisions` and `Simulate` | `physics:vehicle-solver`, `physics:bepu` | Coarse; the finalizer guarantees restore |

## See also

- [Swapping one call inside a game method with a transpiler](../../transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/overview.md) and [Writing a Harmony transpiler for a KSA method](../../transpilers/write-a-harmony-transpiler/overview.md) for the technique in [Rewrite the one caller's call site instead of the inlined callee](patch-around-aggressive-inlining-1.md)
- [Harmony finalizers, cleanup and scoped overrides](../../harmony/run-cleanup-or-recover-from-exceptions-with-a-finalizer/overview.md) for the finalizer rules used in [Bracket the non-inlined methods that enclose an inlined struct callback](patch-around-aggressive-inlining-4.md)
- [Patching private and overloaded game methods by name](../patch-a-private-game-method-by-name/overview.md) and [Reading, writing and calling private game members](../call-private-game-members-through-reflection-and-delegates/overview.md) for reaching private targets
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the frame and physics phases
- Related topics: replace-the-stock-aerodynamic-force-model, drive-the-flight-computer-and-attitude-modes-from-a-mod, suppress-collisions-for-one-vessel, test-harmony-patches-without-the-game
- [Replace the stock aerodynamic force model and measure the active one](../../../physics/aero-and-ground/replace-the-stock-aerodynamic-force-model/overview.md) - related topic
- [Drive the flight computer and attitude modes from a mod](../../../flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md) - related topic
- [Disable collisions for one vessel without touching others](../../../physics/colliders/suppress-collisions-for-one-vessel/overview.md) - related topic
- [Test Harmony patches without running the game](../../../testing-and-tooling/offline-tests/test-harmony-patches-without-the-game/overview.md) - related topic

[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-narrowphasecallbacks]: KSA.NarrowPhaseCallbacks (decompiled, build 2026.10.10.5554)
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
