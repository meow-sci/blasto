---
type: How-To
title: Scan backwards through the argument loads to the previous call
description: Tell the atmospheric ComputeDrag call from the ocean one by walking back from each call through its argument loads for the discriminating field, stopping at the previous call, with the real build 5554 IL of both calls
category: patching
group: transpilers
topic: identify-a-call-by-its-argument-il
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksaero/features/computedrag-call-site-transpiler]
techniques: [harmony-transpiler, il-argument-scan]
hooks: [harmony-transpiler KSA.PhysicsStates.ComputeDerivatives]
lifecycle: [startup:all-mods-loaded, physics:vehicle-solver]
game_types: [KSA.PhysicsStates, KSA.PhysicsEnvironment]
tags: [harmony, transpiler, il, argument-scan, discriminate, window]
---

`PhysicsStates.ComputeDerivatives` calls `ComputeDrag` twice, once with the ocean density and once with the air density. The calls are the same method, so
`CodeInstruction.Calls(ComputeDrag)` matches both. KSAero tells them apart by what their arguments load: it walks backwards from each call, a bounded number of
instructions, and succeeds when it meets `ldfld PhysicsEnvironment::AtmosphericDensity` before meeting any other `call`. This article shows the real IL of both calls and why
matching a field load beats a fixed offset.

Prerequisites: a StarMap mod with `0Harmony` and the recipe from [Retarget the operand of one call (same signature)](../write-a-harmony-transpiler/write-a-harmony-transpiler-1.md). Demonstrated by KSAero in
[ComputeDrag call-site transpiler](../../../../mods/ksaero/features/computedrag-call-site-transpiler.md).

## Background: how the game does it

The two calls in the C# source:

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

The IL of the ocean call and of the air call in build 2026.10.10.5554 (`IL_022c` and `IL_02cf`). Arguments are pushed in order, so the density load is the sixth
instruction back for the ocean call and the fifth for the air call:

```text
// KSA.dll IL via ilspycmd -il: KSA.PhysicsStates::ComputeDerivatives (parameter lists shortened)
IL_0213: ldarg.0
IL_0214: ldarg.1
IL_0215: ldarg.2
IL_0216: ldarg.3
IL_0217: ldloca.s 0
IL_0219: ldloc.s 21
IL_021b: ldarg.3
IL_021c: ldfld float32 KSA.PhysicsEnvironment::OceanDensity
IL_0221: conv.r8
IL_0222: ldloc.s 23
IL_0224: ldarg.3
IL_0225: ldfld float64 KSA.PhysicsEnvironment::OceanSurfaceArea
IL_022a: ldarg.s dt
IL_022c: call void KSA.PhysicsStates::ComputeDrag(...)
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)[^ksa-dll]

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
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)

Counting back from each `call` (this table was derived from the listings above):

| Back | Air call (`IL_02cf`) | Ocean call (`IL_022c`) |
|---:|---|---|
| 1 | `ldarg.s dt` | `ldarg.s dt` |
| 2 | `ldloc.s 28` | `ldfld float64 PhysicsEnvironment::OceanSurfaceArea` |
| 3 | `ldloc.s 23` | `ldarg.3` |
| 4 | `conv.r8` | `ldloc.s 23` |
| 5 | `ldfld float32 PhysicsEnvironment::AtmosphericDensity` (match) | `conv.r8` |
| 6 | `ldarg.3` | `ldfld float32 PhysicsEnvironment::OceanDensity` |
| 7-12 | `ldloc.s 21`, `ldloca.s 0`, `ldarg.3`, `ldarg.2`, `ldarg.1`, `ldarg.0` | `ldarg.3`, `ldloc.s 21`, `ldloca.s 0`, `ldarg.3`, `ldarg.2`, `ldarg.1` (window ends, no match) |

## Lifecycle and timing

The scan runs at patch time (`startup:all-mods-loaded`), once per transpiler run; the swapped call then runs in `physics:vehicle-solver`. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Pick the discriminating feature of the arguments.** Here: the density field the call passes. The ocean call loads `OceanDensity`; the air call loads
   `AtmosphericDensity`. Resolve the field with `AccessTools.Field` (`nameof` on the real field):

```csharp
// KSAero/AirForcePatch.cs L24-L26, L45-L48
// The density argument's load sits five instructions before the call. The window only has to
// reach it without running into the previous call's arguments.
private const int ArgumentWindow = 12;
// ...
    typeof(double), typeof(double), typeof(double), typeof(double),
});

private static readonly FieldInfo? AtmosphericDensity = AccessTools.Field(typeof(PhysicsEnvironment), nameof(PhysicsEnvironment.AtmosphericDensity));
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L24-L48)[^ksaero-airforcepatch]

2. **Walk backwards from the call, bounded by a window.** `LoadsField(field)` is true for `ldfld` and `ldsfld` of that field (`ldflda` and `ldsflda` only with `byAddress: true`; checked with a local test against Lib.Harmony 2.4.2;
   `Calls(method)` is true for both `call` and `callvirt`). The scan succeeds on the field load and fails
   on the first `call` or `callvirt`, which means the scan has left this call's argument list:

```csharp
// KSAero/AirForcePatch.cs L83-L93
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

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L83-L93)

3. **Combine the call test and the context test.** The transpiler rewrites a call only when both hold, and counts rewrites:

```csharp
// KSAero/AirForcePatch.cs L67-L80
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
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L67-L80)

4. **Size the window as a budget, not an offset.** It must reach the field load (five back today) without running into the previous call's arguments. 12 is enough
   here; the call-boundary stop is what keeps it correct if a compiler change inserts a conversion or a `nop`.

5. **Know what success and failure look like.** A matching build logs nothing from the transpiler itself; KSAero's installer then logs
   `[KSAero] Loaded. Atmospheric forces now use the body-of-revolution model.` If the scan finds zero or two calls, the transpiler throws
   `Expected one atmospheric ComputeDrag call in ComputeDerivatives, found N.`, `harmony.Patch` surfaces it (Harmony wraps it in a
   `TargetInvocationException`, observed in a local test against Lib.Harmony 2.4.2), and the installer logs `[KSAero] Patching failed, stock aerodynamics stay in place: ...`
   and calls `UnpatchAll(HarmonyId)` ([KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L16-L36)[^ksaero-mod]).

## Complete example

| File | Role |
|---|---|
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L24-L93) | Window constant, transpiler and the backwards scan |

## Variations and gotchas

- **Why a field load and not an index.** An absolute index shifts whenever the compiler reorders locals or adds a `nop`; a field load is a property of the call's
  arguments and survives both.
- **Why the stop at the previous call.** In 5554 the air `ComputeBuoyancy` call loads `AtmosphericDensity` at `IL_02db`, *after* the drag call, and no `ComputeDrag`
  follows it. If a later build added a `ComputeDrag` call behind that one, a scan without the call-boundary stop could attribute a density load that belongs to an earlier call to
  the wrong one.
- **What to do when a later build adds a second match.** The count check throws ([Throw on a bad match, UnpatchAll(own id), publish the instance on success](../../safety/refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-1.md)). Tighten the context test (an
  additional argument load) rather than raising the window.
- **Other mods.** A transpiler that already rewrote the same call leaves this scan with zero matches ([Anchors that one mod can consume (and what the next transpiler then sees)](../../harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-2.md)).

## See also

- [Anchor on a unique neighbouring call, then walk to the target](identify-a-call-by-its-argument-il-2.md), [Anchor on a UTF-8 label literal in the method](identify-a-call-by-its-argument-il-3.md), [Match the instruction that consumes the result](identify-a-call-by-its-argument-il-4.md)
- [Identifying the right call in a method that makes several similar calls](overview.md)
- [ComputeDrag call-site transpiler](../../../../mods/ksaero/features/computedrag-call-site-transpiler.md)
- [Writing a Harmony transpiler for a KSA method](../write-a-harmony-transpiler/overview.md), [Swapping one call inside a game method with a transpiler](../swap-one-call-inside-a-game-method-with-a-transpiler/overview.md)

[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-dll]: KSA.dll (build 2026.10.10.5554), IL read with ilspycmd
[^ksaero-airforcepatch]: KSAero/AirForcePatch.cs
[^ksaero-mod]: KSAero/Mod.cs
