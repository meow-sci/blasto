---
type: How-To
title: Retarget the operand of one call (same signature)
description: "The smallest useful Harmony transpiler: read the real IL from KSA.dll, resolve the target and replacement exactly, change only the operand of the one matching call, count the matches and throw unless there is exactly one"
category: patching
group: transpilers
topic: write-a-harmony-transpiler
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksaero/features/computedrag-call-site-transpiler]
techniques: [harmony-transpiler, il-argument-scan]
hooks: [starmap StarMapAllModsLoaded, harmony-transpiler KSA.PhysicsStates.ComputeDerivatives]
lifecycle: [startup:all-mods-loaded, physics:vehicle-solver]
game_types: [KSA.PhysicsStates, KSA.PhysicsEnvironment]
tags: [harmony, transpiler, il, call-site, operand, vocabulary]
---

A transpiler is a method Harmony calls at patch time with the original method's IL as a list of `CodeInstruction`s and expects
back a list that becomes the patched method's body. KSAero uses the smallest useful one: it retargets one `call` instruction in
`PhysicsStates.ComputeDerivatives` to a replacement method with the identical signature. Nothing is inserted or removed; only an
operand changes. This article walks through the whole recipe on that example, so the vocabulary and discipline carry to the other
three approaches in this topic.

Prerequisites: a StarMap mod with `0Harmony`, a way to read IL, and the decompiled game. Demonstrated by KSAero in
[ComputeDrag call-site transpiler](../../../../mods/ksaero/features/computedrag-call-site-transpiler.md).

## Vocabulary

| Term | Meaning |
|---|---|
| Opcode | The instruction kind (`call`, `ldfld`, `ldloc.s`). `CodeInstruction.opcode` |
| Operand | The argument of the instruction: a `MethodInfo` for `call`, a `FieldInfo` for `ldfld`, an index for `ldloc.s`. `CodeInstruction.operand` |
| Evaluation stack | Instructions pop their inputs and push results; a `call` pops its arguments and pushes its return value, so the stack shape before and after a swapped call must match |
| Labels | Jump targets attached to an instruction (`CodeInstruction.labels`); a branch into the instruction lands on whatever carries its label |
| Blocks | Exception-block markers (`CodeInstruction.blocks`) for the begin or end of a `try`, `catch` or `finally` |

## Background: how the game does it

Read the real IL. The shipped `current/dll/KSA.dll` is compiled with `DebuggableAttribute` modes `0x107` (which includes
`DisableOptimizations`, checked by reading the assembly's custom attribute blob) and its IL contains `nop`s, so what
`ilspycmd -il` prints is close to the C# in the decompile. `PhysicsStates.ComputeDerivatives` calls the private static
`ComputeDrag` twice, once for the ocean and once for the air:

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

Source: [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1002-L1017)[^ksa-physicsstates]

The IL for the air call, with `call` parameter lists shortened (the full signature is in `KSA.dll`):

```text
// KSA.dll IL via ilspycmd -il: KSA.PhysicsStates::ComputeDerivatives (parameter lists shortened)
IL_02ba: ldarg.0
IL_02bb: ldarg.1
IL_02bc: ldarg.2
IL_02bd: ldarg.3
IL_02be: ldloca.s 0
IL_02c0: ldloc.s 21
IL_02c2: ldarg.3
IL_02c3: ldfld float32 KSA.PhysicsEnvironment::AtmosphericDensity
IL_02c8: conv.r8
IL_02c9: ldloc.s 23
IL_02cb: ldloc.s 28
IL_02cd: ldarg.s dt
IL_02cf: call void KSA.PhysicsStates::ComputeDrag(...)
IL_02d4: nop
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)[^ksa-dll]

The argument `ldfld float32 PhysicsEnvironment::AtmosphericDensity` at `IL_02c3` is what tells the two calls apart; the ocean call at
`IL_022c` loads `OceanDensity` at `IL_021c` instead. (That discrimination is the subject of [Scan backwards through the argument loads to the previous call](../identify-a-call-by-its-argument-il/identify-a-call-by-its-argument-il-1.md).)
`ComputeDrag` is `[MethodImpl(AggressiveInlining)]` ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1089-L1090)); rewriting the caller works whether or not the JIT inlined
it ([Rewrite the one caller's call site instead of the inlined callee](../../internals/patch-around-aggressive-inlining/patch-around-aggressive-inlining-1.md)).

## Lifecycle and timing

The transpiler runs at patch time, not when the method runs. KSAero patches from `[StarMapAllModsLoaded]`
(`startup:all-mods-loaded`), after StarMap's postfix on `ModLibrary.LoadAll` and before any universe is loaded, so no vehicle worker
can be executing `ComputeDerivatives` while its body is swapped. The rewritten call then runs in `physics:vehicle-solver`: on the vehicle
worker pool, once per vehicle per substep, for every vehicle in air. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Run code inside the physics step on worker threads](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md).

Harmony rebuilds a patched method from its **original** IL each time *any* patch on it is added or removed, re-running every registered
transpiler in priority order, so your transpiler can run again later and must give the same answer from the same input (checked against
Lib.Harmony 2.4.2; see [Anchors that one mod can consume (and what the next transpiler then sees)](../../harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-2.md)).

## Steps

1. **Resolve the target and the helper exactly.** `ComputeDerivatives` exists three times in the game (the static 10-parameter body and an
   instance forwarder on `PhysicsStates`, plus another instance forwarder on `ReadOnlyPhysicsStates`), so pass the full parameter list with `MakeByRefType()` for every `in`/`ref` parameter, and do the same for the private
   `ComputeDrag`. `AccessTools.Field` finds the density field:

```csharp
// KSAero/AirForcePatch.cs L32-L48
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
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L32-L48)[^ksaero-airforcepatch]

2. **Install with a transpiler, after checking the lookups.** Throw if anything is null so a broken lookup never becomes a patch that does
   nothing:

```csharp
// KSAero/AirForcePatch.cs L52-L59
internal static void Apply(Harmony harmony)
{
    if (ComputeDerivatives == null || ComputeDrag == null || AtmosphericDensity == null)
        throw new MissingMemberException("PhysicsStates.ComputeDerivatives, PhysicsStates.ComputeDrag or PhysicsEnvironment.AtmosphericDensity not found.");

    _stockDrag = AccessTools.MethodDelegate<ComputeDragFn>(ComputeDrag);
    harmony.Patch(ComputeDerivatives, transpiler: new HarmonyMethod(typeof(AirForcePatch), nameof(Transpiler)));
}
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L52-L59)

3. **Copy the instructions to a list and walk it.** A transpiler receives an enumerable that may be lazy; materialise it first so you can
   count and index:

```csharp
// KSAero/AirForcePatch.cs L61-L65
private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
{
    List<CodeInstruction> code = instructions.ToList();
    MethodInfo replacement = AccessTools.Method(typeof(AirForcePatch), nameof(ComputeAirForces));
    int replaced = 0;
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L61-L65)

4. **Test each instruction with `CodeInstruction.Calls` and a context check.** `Calls(method)` is true for a `call` or `callvirt` to that
   method. The scan walks backwards from the call, up to a window of instructions, succeeding when it sees the field load that identifies
   the air call and failing on the first other `call`, which is where this call's argument list begins:

```csharp
// KSAero/AirForcePatch.cs L24-L26, L67-L74, L83-L93
// The density argument's load sits five instructions before the call. The window only has to
// reach it without running into the previous call's arguments.
private const int ArgumentWindow = 12;
// ...
    for (int i = 0; i < code.Count; i++)
    {
        if (code[i].Calls(ComputeDrag!) && PassesAtmosphericDensity(code, i))
        {
            code[i].operand = replacement;
            replaced++;
        }
    }
// ...
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

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L24-L93)

5. **Change only the operand.** In the loop above, `code[i].operand = replacement;` keeps the opcode, labels and blocks, and because the
   replacement has the same parameter types and return type, the evaluation stack before and after the call is unchanged. The replacement is
   an `internal static` method with the stock signature:

```csharp
// KSAero/AirForcePatch.cs L99-L101
internal static void ComputeAirForces(in BubbleOrigin origin, in KinematicStates kinematic, in VehicleProperties props,
    in PhysicsEnvironment environment, ref Disturbances disturbances, double3 airVelocityBub, double density,
    double dragCdA, double surfaceArea, double dt)
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L99-L101)

6. **Count and refuse.** Zero means the method changed shape or another mod rewrote the call first; two means the scan matched something it
   should not. Both throw `InvalidOperationException`, which aborts the patch ([Throw on a bad match, UnpatchAll(own id), publish the instance on success](../../safety/refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-1.md)):

```csharp
// KSAero/AirForcePatch.cs L76-L80
// Anything else means the method has changed shape, and stock aero is the safe answer.
if (replaced != 1)
    throw new InvalidOperationException($"Expected one atmospheric ComputeDrag call in ComputeDerivatives, found {replaced}.");

return code;
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L76-L80)

7. **Prove it.** Run a transpiler test headlessly against `PatchProcessor.GetOriginalInstructions(method)` (see approach 2) or compare the
   patched IL with `ilspycmd`; in game the symptom of success is the mod's own behaviour (KSAero's readout and drag).

## Complete example

| File | Role |
|---|---|
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs) | Lookups, `Apply`, transpiler, argument-window scan, replacement signature |
| [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L16-L36)[^ksaero-mod] | Install point and rollback |

## Variations and gotchas

- **The replacement must match exactly:** same parameter count, order, types and by-reference-ness, and the same return type, or the patched
  method's stack is invalid. Instance calls pass the instance as the first stack value.
- **Window size is a budget, not an offset.** 12 only has to reach the density load (five back today) without running into the previous call's
  arguments; the call-boundary stop is what keeps the scan correct.
- **Other transpilers.** A transpiler that runs before KSAero's and rewrites the same call leaves it with zero matches; KSAero then refuses.
- **Build drift.** The mod was tested on 5541; on 5554 the atmospheric call is at the cited line and the scan still finds exactly one match.
- **Four common shapes.** This article is shape 1 (retarget a call). Shape 2 inserts a call before an anchor ([Insert a call before a validated anchor](write-a-harmony-transpiler-2.md)); shape 3 replaces an
  instruction run with a helper ([Replace an instruction run with a helper that takes a local by reference](write-a-harmony-transpiler-3.md)); shape 4 filters a value read ([Filter one value read through a registry check](write-a-harmony-transpiler-4.md)).

## See also

- [Insert a call before a validated anchor](write-a-harmony-transpiler-2.md), [Replace an instruction run with a helper that takes a local by reference](write-a-harmony-transpiler-3.md), [Filter one value read through a registry check](write-a-harmony-transpiler-4.md)
- [Writing a Harmony transpiler for a KSA method](overview.md)
- [ComputeDrag call-site transpiler](../../../../mods/ksaero/features/computedrag-call-site-transpiler.md)
- [Swapping one call inside a game method with a transpiler](../swap-one-call-inside-a-game-method-with-a-transpiler/overview.md), [Identifying the right call in a method that makes several similar calls](../identify-a-call-by-its-argument-il/overview.md), [Inserting calls into a game method at an IL anchor](../insert-calls-into-a-game-method-at-an-il-anchor/overview.md)
- [Refusing a patch and rolling back when the game changes](../../safety/refuse-a-patch-and-roll-back-when-the-game-changes/overview.md)

[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-dll]: KSA.dll (build 2026.10.10.5554), IL read with ilspycmd
[^ksaero-airforcepatch]: KSAero/AirForcePatch.cs
[^ksaero-mod]: KSAero/Mod.cs
