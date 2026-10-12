---
type: How-To
title: Replace an instruction run with a helper that takes a local by reference
description: "Rewrite the compare-and-branch that picks the nearby body in CelestialSystem.GetNearbyAstronomical: verify the exact IL sequence, read local slots from it, pass the best-so-far local by address to a helper, nop the redundant store, and return the original IL when the shape check fails"
category: patching
group: transpilers
topic: write-a-harmony-transpiler
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/realatmospheres/features/nearby-body-transpiler]
techniques: [harmony-transpiler, il-rewrite]
hooks: [starmap StarMapBeforeMain, harmony-transpiler KSA.CelestialSystem.GetNearbyAstronomical]
lifecycle: [startup:starmap-before-main, frame:celestials, frame:render]
game_types: [KSA.CelestialSystem, KSA.Astronomical, KSA.Program]
tags: [harmony, transpiler, il, comparison, branch, local, ldloca, nop]
---

The shape-1 and shape-2 transpilers in this topic change a call. Sometimes the decision you need to change is not a call at all: it is a
compare-and-branch the compiler inlined. Real Atmospheres replaces the comparison in `CelestialSystem.GetNearbyAstronomical` (which body counts
as "nearby" for the camera) with its own ranking helper. The rewrite removes three instructions (`sub`, a local load and `clt`), inserts a call
that takes the best-so-far local **by reference**, and turns the original store into two `nop`s, because the helper now writes that local.

Prerequisites: a StarMap mod with `0Harmony` and the vocabulary from [Retarget the operand of one call (same signature)](write-a-harmony-transpiler-1.md). Demonstrated by Real Atmospheres in
[Nearby-body choice transpiler](../../../../mods/realatmospheres/features/nearby-body-transpiler.md).

## Background: how the game does it

`GetNearbyAstronomical` walks every body and keeps the one whose surface distance is smaller than the previous pick's *centre* distance. From
8,400 km of Mars's centre, Phobos comes nearer than Mars's centre once an orbit, and Mars drops to a distant render for that stretch (a flicker every
two seconds at x14400 warp, per the mod):

```csharp
// current/decomp/KSA/CelestialSystem.cs L565-L584
public Astronomical? GetNearbyAstronomical(Transform3D origin, Func<Astronomical, bool> validCheck, double nearbyDistance = 80000000.0)
{
	Astronomical result = null;
	double num = nearbyDistance;
	ReadOnlySpan<Astronomical> readOnlySpan = _all.AsSpan();
	for (int i = 0; i < readOnlySpan.Length; i++)
	{
		Astronomical astronomical = readOnlySpan[i];
		if (validCheck(astronomical))
		{
			double num2 = (origin.PositionEcl - astronomical.GetPositionEcl()).Length();
			if (num2 - astronomical.MeanRadius < num)
			{
				result = astronomical;
				num = num2;
			}
		}
	}
	return result;
}
```

Source: [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L565-L584)[^ksa-celestialsystem]

The IL of the comparison in build 2026.10.10.5554. The method is an instance method, so `ldarg.3` is `nearbyDistance`; locals are `4` (the
body), `5` (the centre distance), `1` (best so far) and `0` (the result):

```text
// KSA.dll IL via ilspycmd -il: KSA.CelestialSystem::GetNearbyAstronomical (parameter lists shortened)
IL_0047: stloc.s 7
IL_0049: ldloca.s 7
IL_004b: call instance float64 [Brutal.Core.Numerics]Brutal.Numerics.double3::Length(...)
IL_0050: stloc.s 5
IL_0052: ldloc.s 5
IL_0054: ldloc.s 4
IL_0056: callvirt instance float64 KSA.Astronomical::get_MeanRadius(...)
IL_005b: sub
IL_005c: ldloc.1
IL_005d: clt
IL_005f: stloc.s 8
IL_0061: ldloc.s 8
IL_0063: brfalse.s IL_006d

IL_0065: nop
IL_0066: ldloc.s 4
IL_0068: stloc.0
IL_0069: ldloc.s 5
IL_006b: stloc.1
IL_006c: nop
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)[^ksa-dll]

## Lifecycle and timing

The transpiler runs once at patch time. Real Atmospheres installs it from `[StarMapBeforeMain]` (`startup:starmap-before-main`). The rewritten
code runs every frame on the main thread: `GetNearbyAstronomical` is called from `Program.OnFrameCelestials` to set `camera.NearbyCelestial`
(`frame:celestials`, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2701-L2714)[^ksa-program]), from `Program.UpdateShaderData` to pick the shader parent with a 37.4e9 m range
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2929)), and from `Program.FindNearbyCelestial` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5230)). A transpiler keeps the per-frame cost to one static call
per body instead of a prefix that would re-run the whole loop. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Write down the C# before and after.** The mod documents the target rewrite in a comment:

```csharp
// NearbyBody.cs L68-L73
/// <summary>
/// The game's test and bookkeeping, with dist the body's centre distance and best the pick's:
///     if (dist - body.MeanRadius &lt; best) { result = body; best = dist; }
/// becomes
///     if (Take(dist, body.MeanRadius, ref best, nearbyDistance, result == null)) { result = body; }
/// </summary>
```

Source: [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L68-L73)[^realatmospheres-nearbybody]

2. **Find the anchor by a feature of the sequence, not an index.** The transpiler finds the single `call`/`callvirt` to `get_MeanRadius` returning
   `double`, then checks the shape around it: `sub` right after, `clt` three after:

```csharp
// NearbyBody.cs L74-L84, L125-L127
public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
{
    List<CodeInstruction> code = instructions.ToList();
    int range = RangeArgument(original);
    int[] radius = code.Select((c, i) => IsMeanRadius(c) ? i : -1).Where(i => i >= 0).ToArray();
    if (range < 0 || radius.Length != 1)
        return code;
    int at = radius[0];
    // dist, body, MeanRadius, sub, best, clt
    if (at < 2 || at + 3 >= code.Count || code[at + 1].opcode != OpCodes.Sub || code[at + 3].opcode != OpCodes.Clt)
        return code;
// ...
private static bool IsMeanRadius(CodeInstruction c) =>
    (c.opcode == OpCodes.Callvirt || c.opcode == OpCodes.Call) && c.operand is MethodInfo m
    && m.Name == "get_MeanRadius" && m.ReturnType == typeof(double);
```

Source: [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L74-L127)

3. **Read the local slots from the matched instructions.** The distance, body and best locals are whatever `ldloc` the compiler used (`ldloc.s 5`,
   `ldloc.s 4`, `ldloc.1` here); the transpiler decodes `ldloc.0-3`, `ldloc.s` and `ldloc` with a helper that refuses indices above 255, because the
   replacement is written in short forms:

```csharp
// NearbyBody.cs L85-L87, L129-L142
    int dist = LoadedLocal(code[at - 2]), body = LoadedLocal(code[at - 1]), best = LoadedLocal(code[at + 2]);
    if (dist < 0 || body < 0 || best < 0)
        return code;
// ...
private static int LoadedLocal(CodeInstruction c) =>
    c.opcode == OpCodes.Ldloc_0 ? 0 : c.opcode == OpCodes.Ldloc_1 ? 1 : c.opcode == OpCodes.Ldloc_2 ? 2 : c.opcode == OpCodes.Ldloc_3 ? 3
    : c.opcode == OpCodes.Ldloc_S || c.opcode == OpCodes.Ldloc ? Index(c.operand) : -1;

private static int StoredLocal(CodeInstruction c) =>
    c.opcode == OpCodes.Stloc_0 ? 0 : c.opcode == OpCodes.Stloc_1 ? 1 : c.opcode == OpCodes.Stloc_2 ? 2 : c.opcode == OpCodes.Stloc_3 ? 3
    : c.opcode == OpCodes.Stloc_S || c.opcode == OpCodes.Stloc ? Index(c.operand) : -1;

/// <summary>A local's slot, if it fits the short forms the replacement is written in.</summary>
private static int Index(object? operand)
{
    int index = operand is LocalVariableInfo local ? local.LocalIndex : operand is IConvertible number ? Convert.ToInt32(number) : -1;
    return index is >= 0 and <= byte.MaxValue ? index : -1;
}
```

Source: [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L85-L142)

4. **Find the store sequence that follows** (`result = body; best = dist;`) by matching the same locals, so the right `stloc`s are nopped:

```csharp
// NearbyBody.cs L88-L96
// result = body; best = dist;
int keep = -1;
for (int i = at + 4; i + 3 < code.Count && keep < 0; i++)
    if (LoadedLocal(code[i]) == body && StoredLocal(code[i + 1]) >= 0
        && LoadedLocal(code[i + 2]) == dist && StoredLocal(code[i + 3]) == best)
        keep = i;
if (keep < 0)
    return code;
int result = StoredLocal(code[keep + 1]);
```

Source: [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L88-L96)

5. **Refuse when a jump could land inside the replaced range.** If any replaced instruction carries a label or an exception-block marker, return the
   original IL:

```csharp
// NearbyBody.cs L97-L99
// nothing may jump into what is replaced
if (new[] { at + 1, at + 2, at + 3, keep + 2, keep + 3 }.Any(i => code[i].labels.Count > 0 || code[i].blocks.Count > 0))
    return code;
```

Source: [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L97-L99)

6. **Rewrite.** Nop the two stores, remove `sub; ldloc best; clt`, and insert `ldloca.s best; ldarg.s nearbyDistance; ldloc.s result; ldnull; ceq;
   call Take`. The stack before the removed instructions held the centre distance and the radius (the `get_MeanRadius` result), so `Take(double
   centreDistance, double radius, ref double best, double range, bool none)` receives exactly its five arguments and leaves the `bool` where `clt` left
   it, for the `stloc.s 8; ldloc.s 8; brfalse` that follows:

```csharp
// NearbyBody.cs L101-L115
    code[keep + 2] = new CodeInstruction(OpCodes.Nop);
    code[keep + 3] = new CodeInstruction(OpCodes.Nop);
    code.RemoveRange(at + 1, 3);
    code.InsertRange(at + 1, new[]
    {
        new CodeInstruction(OpCodes.Ldloca_S, (byte)best),
        new CodeInstruction(OpCodes.Ldarg_S, (byte)range),
        new CodeInstruction(OpCodes.Ldloc_S, (byte)result),
        new CodeInstruction(OpCodes.Ldnull),
        new CodeInstruction(OpCodes.Ceq),
        new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(NearbyBody), nameof(Take))),
    });
    Patched = true;
    return code;
}
```

Source: [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L101-L115)

7. **Find the argument slot by name and type**, plus one for `this` on an instance method:

```csharp
// NearbyBody.cs L117-L123
/// <summary>The argument slot of the method's nearbyDistance, or -1 if it has none.</summary>
private static int RangeArgument(MethodBase method)
{
    ParameterInfo[] parameters = method.GetParameters();
    int at = Array.FindIndex(parameters, p => p.Name == "nearbyDistance" && p.ParameterType == typeof(double));
    return at < 0 ? -1 : at + (method.IsStatic ? 0 : 1);
}
```

Source: [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L117-L123)

8. **Record the result.** `Patched` tells the caller whether the rewrite happened so the log states the outcome; on a mismatch the transpiler returned the
   original IL and the log says a small moon can still take its planet's place:

```csharp
// NearbyBody.cs L27-L50
public static bool Patched { get; private set; }

public static void Install(Harmony harmony)
{
    Type? system = AccessTools.TypeByName("KSA.CelestialSystem");
    MethodInfo? nearby = system == null ? null : AccessTools.Method(system, "GetNearbyAstronomical");
    if (nearby == null || nearby.IsStatic || RangeArgument(nearby) < 0)
    {
        ShadowBuilder.Log("WARN: CelestialSystem.GetNearbyAstronomical(.., nearbyDistance) not found; a small moon can still take its planet's place as the nearby body");
        return;
    }
    try
    {
        harmony.Patch(nearby, transpiler: new HarmonyMethod(typeof(NearbyBody), nameof(Transpiler)));
    }
    catch (Exception e)
    {
        ShadowBuilder.Log($"WARN: could not patch the nearby body's choice ({e.GetType().Name}); a small moon can still take its planet's place");
        return;
    }
    ShadowBuilder.Log(Patched
        ? $"nearby body: a body within {NearRadii:0} of its radii counts first, then the nearest surface"
        : "WARN: the nearby body's comparison has changed; a small moon can still take its planet's place");
}
```

Source: [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L27-L50)

9. **Write the helper.** `Take` keeps the game's own limit but ranks a body by surface distance if the camera is within 25 radii of it, otherwise by
   surface distance plus the range, so every near body beats every far one:

```csharp
// NearbyBody.cs L52-L66
/// <summary>Whether a body takes over as the nearby one. <paramref name="best"/> holds the rank of the pick so
/// far, lower being better: the distance to its surface, plus the range for a body the camera is not within
/// NearRadii of, which puts every such body behind every one it is. <paramref name="none"/>: nothing picked
/// yet, when best still holds the game's starting value.</summary>
public static bool Take(double centreDistance, double radius, ref double best, double range, bool none)
{
    double surface = centreDistance - radius;
    if (!(surface < range))
        return false;
    double rank = centreDistance <= NearRadii * radius ? surface : surface + range;
    if (!none && !(rank < best))
        return false;
    best = rank;
    return true;
}
```

Source: [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L52-L66)

## Complete example

| File | Role |
|---|---|
| [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs) | Install, `Take`, transpiler, local decoding, argument-slot lookup |

## Variations and gotchas

- **Short-form opcodes only.** The rewrite emits `ldloca.s`, `ldarg.s` and `ldloc.s`, so it refuses indices above 255; a debug build with many locals
  could exceed that.
- **It affects every caller.** The same ranking applies to the shader-data parent choice with its larger range; other mods that call
  `GetNearbyAstronomical` inherit it too.
- **Silent fallback.** On a mismatch the transpiler returns the original IL; consider throwing instead if the feature is not optional
  ([Refusing a patch and rolling back when the game changes](../../safety/refuse-a-patch-and-roll-back-when-the-game-changes/overview.md)).
- **Comparison rewrite recipe.** Verify the exact sequence; read local slots from it; use `ldloca.s` for the local the helper must update; nop the
  redundant store; bail out if a label or block falls inside the range; set a flag so the log can say the result.

## See also

- [Retarget the operand of one call (same signature)](write-a-harmony-transpiler-1.md), [Insert a call before a validated anchor](write-a-harmony-transpiler-2.md), [Filter one value read through a registry check](write-a-harmony-transpiler-4.md)
- [Writing a Harmony transpiler for a KSA method](overview.md)
- [Nearby-body choice transpiler](../../../../mods/realatmospheres/features/nearby-body-transpiler.md)
- [Identifying the right call in a method that makes several similar calls](../identify-a-call-by-its-argument-il/overview.md)

[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-dll]: KSA.dll (build 2026.10.10.5554), IL read with ilspycmd
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^realatmospheres-nearbybody]: RealAtmospheres/NearbyBody.cs
