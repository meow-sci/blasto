---
type: Mod Feature
title: ComputeDrag call-site transpiler
description: A Harmony transpiler on PhysicsStates.ComputeDerivatives that retargets only the atmospheric ComputeDrag call to a same-signature replacement, found by scanning the call's argument IL for the AtmosphericDensity load
mod: ksaero
category: patching
techniques: [harmony-transpiler, reflection, call-site-rewrite, il-argument-scan]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-transpiler KSA.PhysicsStates.ComputeDerivatives
  - reflection KSA.PhysicsStates.ComputeDrag
lifecycle: [startup:all-mods-loaded, physics:vehicle-solver]
game_types: [KSA.PhysicsStates, KSA.PhysicsEnvironment, KSA.ReadOnlyPhysicsStates]
tags: [harmony, transpiler, il, call-site, aerodynamics, inlining]
---

KSAero changes the aerodynamic force on every vehicle without patching the aerodynamic function. It
transpiles the caller, `PhysicsStates.ComputeDerivatives`, and changes the operand of exactly one
`call` instruction: the `PhysicsStates.ComputeDrag` call that is passed
`PhysicsEnvironment.AtmosphericDensity` now calls `AirForcePatch.ComputeAirForces`, a static method with
the identical parameter list. Gravity, thrust, buoyancy, parachutes and the ocean `ComputeDrag` call in
the same method keep running stock code.[^ksaero-airforcepatch] [^ksaero-readme]

The technique generalises to any "replace one helper call inside a big game method" problem: resolve
both methods exactly, identify the call by what its arguments load, swap the operand, and refuse if the
match count is not exactly one.

## How it works

1. **Resolve the targets exactly.** `ComputeDerivatives` exists three times in the game: a static
   10-parameter version and two instance forwarders on `PhysicsStates` and `ReadOnlyPhysicsStates`
   that call it ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L951-L956),
   [current/decomp/KSA/ReadOnlyPhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ReadOnlyPhysicsStates.cs#L116-L119)).[^ksa-readonlyphysicsstates]
   KSAero passes the full static parameter list to `AccessTools.Method`, with `MakeByRefType()` for
   every `in` and `ref` parameter, so the static body is the one patched and both forwarders reach it.
   `ComputeDrag` is `private static` and is resolved the same way; `AtmosphericDensity` (a `float`
   field) through `AccessTools.Field`
   ([KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L32-L48)).
2. **Install.** `Apply` throws `MissingMemberException` if any lookup returned null, builds the stock
   delegate (see [Stock ComputeDrag delegate fallback](stock-drag-delegate-fallback.md)), then calls
   `harmony.Patch(ComputeDerivatives, transpiler: ...)`
   ([KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L52-L59)).
3. **Find the call.** The transpiler copies the instructions to a list and tests every instruction with
   `CodeInstruction.Calls(ComputeDrag)` plus `PassesAtmosphericDensity`
   ([KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L61-L81)).
4. **Argument-window scan.** `PassesAtmosphericDensity` walks backwards from the call for at most
   `ArgumentWindow = 12` instructions. A `LoadsField(AtmosphericDensity)` means a match; reaching any
   `call` or `callvirt` first means the scan has left this call's argument list and the answer is no
   ([KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L24-L26),
   [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L83-L93)).
5. **Swap the operand only.** `code[i].operand = replacement`. Opcode, labels and exception blocks on
   the instruction are untouched, and because the replacement has the same parameter types and return
   type, the evaluation stack before and after the call is unchanged. No IL is inserted or removed.
6. **Count.** Anything other than one replacement throws `InvalidOperationException`, which aborts the
   patch (see [Patch refusal and rollback](patch-refusal-and-rollback.md)).

### What the scan sees in build 2026.10.10.5554

`ComputeDerivatives` calls `ComputeDrag` twice inside its `InPhysicsRadius` branch: once for the ocean
with `environment.OceanDensity`, once for the air with `environment.AtmosphericDensity`
([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L993-L1017)).[^ksa-physicsstates]
Disassembling [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll) gives these argument sequences
(instructions counted backwards from the call):[^ksa-dll]

| Back | Atmospheric call (`IL_02cf`) | Ocean call (`IL_022c`) |
|---:|---|---|
| 1 | `ldarg.s dt` | `ldarg.s dt` |
| 2 | `ldloc.s 28` (surface area above water) | `ldfld float64 PhysicsEnvironment::OceanSurfaceArea` |
| 3 | `ldloc.s 23` (stock CdA) | `ldarg.3` |
| 4 | `conv.r8` | `ldloc.s 23` |
| 5 | `ldfld float32 PhysicsEnvironment::AtmosphericDensity` (match) | `conv.r8` |
| 6 | | `ldfld float32 PhysicsEnvironment::OceanDensity` |
| 7-12 | | `ldarg.3`, `ldloc.s 21`, `ldloca.s 0`, `ldarg.3`, `ldarg.2`, `ldarg.1` (window ends, no match) |

The density load sits five instructions before the atmospheric call, which is what the source comment
says. Matching on the field load rather than on an instruction index or a fixed offset keeps the patch
working if the compiler reorders locals, inserts `nop`s or adds a conversion, and the call-boundary stop
keeps a density load that belongs to an earlier call from being attributed to the wrong one. In 5554
that case does not arise: the atmospheric `ComputeBuoyancy` call loads `AtmosphericDensity` at
`IL_02db`, after the drag call, and no `ComputeDrag` call follows it. It would matter if a later build
added a `ComputeDrag` call behind that `ComputeBuoyancy`.

### Why the call site and not ComputeDrag

- **Inlining.** `ComputeDrag` is declared `[MethodImpl(MethodImplOptions.AggressiveInlining)]`
  ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1089-L1090)) and the
  shipped IL carries the `aggressiveinlining` implementation flag. A Harmony patch redirects a method's
  entry point; a caller into which the JIT has already copied the callee's body never goes through
  that entry, so a prefix on `ComputeDrag` can be skipped silently. Rewriting the caller makes
  `ComputeDerivatives` itself call the replacement, inlined or not.[^ksaero-airforcepatch]
- **Selectivity.** Patching `ComputeDrag` would also change ocean drag. Only the atmospheric call site
  is rewritten.
- **No stack surgery.** Because the replacement mirrors the stock signature, the change is a single
  operand.

## Lifecycle and timing

- **Install time: `startup:all-mods-loaded`.** `Mod.OnFullyLoaded` is `[StarMapAllModsLoaded]`
  ([KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L16-L36)), which StarMap fires from a Harmony postfix on
  `ModLibrary.LoadAll` ([StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L17-L28)).
  KSA calls `ModLibrary.LoadAll` from the `Program` constructor
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L942)), before a universe is
  loaded and therefore before the first `Universe.ExecuteNextVehicleSolvers` dispatches a vehicle step;
  `JobSystems.Initialize`, which creates the `VehicleSolver` scheduler and `VehicleWorkerPool`, runs
  later in the same constructor ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L974)).
  The method body is swapped while no worker thread can be executing it. Patching mid-flight would
  replace the method while vehicle worker threads may be inside it.[^starmap-modlibrarypatches] [^ksa-program]
- **Run time: `physics:vehicle-solver`.** The rewritten call executes wherever `ComputeDerivatives`
  runs: on `VehicleWorkerPool` threads (or the `VehicleSolver` runner helping out), once per vehicle per
  constrained substep or twice per Verlet substep, for every vehicle in air. RK8 long steps also call
  `ComputeDerivatives`, but only outside the physics radius, where the drag branch is skipped. The full call chain and its constraints are in
  [Body-of-revolution aerodynamics](body-of-revolution-aerodynamics.md#lifecycle-and-timing).

## Key code

| Path | Role |
|---|---|
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L10-L18) | Doc comment stating the call-site rationale |
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L32-L48) | Exact `AccessTools` lookups for both methods and the density field |
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L52-L59) | `Apply`: null checks, delegate, `harmony.Patch` with a transpiler |
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L61-L93) | Transpiler and the argument-window scan |
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L99-L101) | Replacement signature, identical to stock `ComputeDrag` |
| [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L16-L36) | Install point and rollback |

## Snippets

```csharp
// KSAero/AirForcePatch.cs L32-L59
    private static readonly MethodInfo? ComputeDerivatives = AccessTools.Method(typeof(PhysicsStates), "ComputeDerivatives", new[]
    {
        typeof(BubbleOrigin).MakeByRefType(), typeof(KinematicStates).MakeByRefType(),
        typeof(VehicleProperties).MakeByRefType(), typeof(PhysicsEnvironment).MakeByRefType(),
        typeof(double), typeof(double), typeof(double3), typeof(double3),
        typeof(ReadOnlySpan<ActiveNozzle>), typeof(ReadOnlySpan<ActiveChute>),
    });

    private static readonly MethodInfo? ComputeDrag = AccessTools.Method(typeof(PhysicsStates), "ComputeDrag", new[]
    {
        typeof(BubbleOrigin).MakeByRefType(), typeof(KinematicStates).MakeByRefType(),
        typeof(VehicleProperties).MakeByRefType(), typeof(PhysicsEnvironment).MakeByRefType(),
        typeof(Disturbances).MakeByRefType(), typeof(double3),
        typeof(double), typeof(double), typeof(double), typeof(double),
    });

    private static readonly FieldInfo? AtmosphericDensity = AccessTools.Field(typeof(PhysicsEnvironment), nameof(PhysicsEnvironment.AtmosphericDensity));

    private static ComputeDragFn? _stockDrag;

    internal static void Apply(Harmony harmony)
    {
        if (ComputeDerivatives == null || ComputeDrag == null || AtmosphericDensity == null)
            throw new MissingMemberException("PhysicsStates.ComputeDerivatives, PhysicsStates.ComputeDrag or PhysicsEnvironment.AtmosphericDensity not found.");

        _stockDrag = AccessTools.MethodDelegate<ComputeDragFn>(ComputeDrag);
        harmony.Patch(ComputeDerivatives, transpiler: new HarmonyMethod(typeof(AirForcePatch), nameof(Transpiler)));
    }
```

```csharp
// KSAero/AirForcePatch.cs L61-L93
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        MethodInfo replacement = AccessTools.Method(typeof(AirForcePatch), nameof(ComputeAirForces));
        int replaced = 0;

        for (int i = 0; i < code.Count; i++)
        {
            if (code[i].Calls(ComputeDrag!) && PassesAtmosphericDensity(code, i))
            {
                code[i].operand = replacement;
                replaced++;
            }
        }

        // Anything else means the method has changed shape, and stock aero is the safe answer.
        if (replaced != 1)
            throw new InvalidOperationException($"Expected one atmospheric ComputeDrag call in ComputeDerivatives, found {replaced}.");

        return code;
    }

    private static bool PassesAtmosphericDensity(List<CodeInstruction> code, int callIndex)
    {
        for (int j = callIndex - 1; j >= Math.Max(0, callIndex - ArgumentWindow); j--)
        {
            if (code[j].LoadsField(AtmosphericDensity!))
                return true;
            if (code[j].opcode == OpCodes.Call || code[j].opcode == OpCodes.Callvirt)
                return false;
        }
        return false;
    }
```

```csharp
// current/decomp/KSA/PhysicsStates.cs L1002-L1017
				if (environment.OceanVolume > 0.0 || environment.OceanSurfaceArea > 0.0)
				{
					ComputeBuoyancy(in kinematic, ref disturbances2, gravitationBub, environment.OceanDensity, environment.OceanVolume);
					ComputeDrag(in origin, in kinematic, in props, in environment, ref disturbances2, double8, environment.OceanDensity, num6, environment.OceanSurfaceArea, dt);
				}
				if (environment.AtmosphericDensity > 0f && num6 > 0.0)
				{
					double num7 = Math.Max(0.0, (double)props.TotalVolume - environment.OceanVolume);
					double num8 = Math.Max(0.0, (double)props.TotalSurfaceArea - environment.OceanSurfaceArea);
					if (num8 > 0.0 || num7 > 0.0)
					{
						ComputeDrag(in origin, in kinematic, in props, in environment, ref disturbances2, double8, environment.AtmosphericDensity, num6, num8, dt);
						ComputeBuoyancy(in kinematic, ref disturbances2, gravitationBub, environment.AtmosphericDensity, num7);
					}
				}
			}
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `PhysicsStates.ComputeDerivatives` (static, 10 parameters) | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L956-L1087) | Transpiler target; the per-substep force function |
| `PhysicsStates.ComputeDerivatives` (instance) | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L951-L954) | Forwarder used by the integrators; reaches the patched body |
| `ReadOnlyPhysicsStates.ComputeDerivatives` | [current/decomp/KSA/ReadOnlyPhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ReadOnlyPhysicsStates.cs#L116-L119) | Forwarder used by the constrained step; reaches the patched body |
| `PhysicsStates.ComputeDrag` | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1089-L1111) | Call to retarget; `AggressiveInlining`, private static |
| `PhysicsEnvironment.AtmosphericDensity` | [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L24) | `float` field whose load marks the atmospheric call[^ksa-physicsenvironment] |
| `PhysicsEnvironment.OceanDensity` | [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L26) | Marks the ocean call, which is left alone |

## Pitfalls and version notes

- **Overloads.** Looking up `ComputeDerivatives` by name alone is ambiguous. Pass the parameter types,
  and use `MakeByRefType()` for `in`, `ref` and `out` parameters: in IL they are all by-reference.
- **The replacement must match exactly.** Same parameter count, order, types and by-reference-ness,
  and the same return type, or the patched method's stack is invalid. KSAero's replacement is
  `internal static` in the mod assembly.
- **Refuse rather than guess.** A count of zero means the game or another mod changed the method; a
  count of two means the scan matched something it should not. Both throw.
- **Window size.** 12 is a budget, not an offset: it only has to reach the density load (five back
  today) without running into the previous call's arguments, and the call-boundary check is what
  actually stops it.
- **Debug-compiled game assembly.** In build 2026.10.10.5554, `KSA.dll` carries
  `DebuggableAttribute` with `DebuggingModes` value `0x107` (which includes `DisableOptimizations`), and
  its IL contains `nop`s.[^ksa-dll] On CoreCLR that normally means KSA's own methods are JIT-compiled
  without optimisation, in which case the stock `ComputeDerivatives` may not inline `ComputeDrag` in
  this build at all (runtime behaviour not verified in game). The call-site rewrite is correct either
  way, and it does not depend on how a future build is compiled.
- **Other transpilers.** Harmony rebuilds a patched method from its original IL, running every
  registered transpiler in priority order, each time any mod patches it. A transpiler that runs before
  KSAero's and rewrites the same call leaves KSAero with zero matches. If KSAero patched first, its
  transpiler runs again when the other mod patches `ComputeDerivatives` later, and the throw then
  surfaces from that mod's `Patch` call rather than from KSAero's (see
  [Patch refusal and rollback](patch-refusal-and-rollback.md)).
- **Build drift.** The mod was tested on 5541. On 5554 the atmospheric call is at
  [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1013) and the
  scan still finds exactly one match.

## See also

- [Body-of-revolution aerodynamics](body-of-revolution-aerodynamics.md): what the replacement computes, and the worker-thread call chain
- [Patch refusal and rollback](patch-refusal-and-rollback.md)
- [Stock ComputeDrag delegate fallback](stock-drag-delegate-fallback.md)
- [Game version check](game-version-check.md)
- [Run code inside the physics step on worker threads](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md) - how-to article demonstrating this feature
- [Anchors that one mod can consume (and what the next transpiler then sees)](../../../howto/patching/harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-2.md) - how-to article demonstrating this feature
- [Explicit parameter arrays with MakeByRefType for overloaded and by-reference targets](../../../howto/patching/internals/patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-3.md) - how-to article demonstrating this feature
- [Rewrite the one caller's call site instead of the inlined callee](../../../howto/patching/internals/patch-around-aggressive-inlining/patch-around-aggressive-inlining-1.md) - how-to article demonstrating this feature
- [Throw on a bad match, UnpatchAll(own id), publish the instance on success](../../../howto/patching/safety/refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-1.md) - how-to article demonstrating this feature
- [Scan backwards through the argument loads to the previous call](../../../howto/patching/transpilers/identify-a-call-by-its-argument-il/identify-a-call-by-its-argument-il-1.md) - how-to article demonstrating this feature
- [Same-signature static replacement, operand-only edit](../../../howto/patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-1.md) - how-to article demonstrating this feature
- [Retarget the operand of one call (same signature)](../../../howto/patching/transpilers/write-a-harmony-transpiler/write-a-harmony-transpiler-1.md) - how-to article demonstrating this feature
- [Call-site transpiler on ComputeDerivatives writing the Disturbances accumulator](../../../howto/physics/aero-and-ground/replace-the-stock-aerodynamic-force-model/replace-the-stock-aerodynamic-force-model-1.md) - how-to article demonstrating this feature

[^ksaero-airforcepatch]: KSAero/AirForcePatch.cs
[^ksaero-readme]: KSAero/README.md
[^ksa-readonlyphysicsstates]: KSA.ReadOnlyPhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-dll]: KSA.dll (build 2026.10.10.5554), IL and assembly attributes
[^starmap-modlibrarypatches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
