---
type: How-To
title: Transpile the distance comparison into a ranking call that favours a body within 25 radii
description: Rewrite the surface-versus-centre comparison in CelestialSystem.GetNearbyAstronomical with a transpiler that shape-checks the IL, calls a static ranking method that favours a body the camera is within 25 radii of, and returns the original IL on any mismatch
category: celestials
group: atmospheres-and-rings
topic: choose-which-body-the-game-renders-in-detail
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/realatmospheres/features/nearby-body-transpiler]
techniques: [harmony-transpiler]
hooks: [starmap StarMapBeforeMain, harmony-transpiler KSA.CelestialSystem.GetNearbyAstronomical]
lifecycle: [startup:starmap-before-main, frame:celestials, frame:render]
game_types: [KSA.CelestialSystem, KSA.Astronomical, KSA.Program, KSA.Camera]
tags: [nearby-celestial, transpiler, il, flicker, guarded]
---
Outcome: above Mars, Mars stays the detailed body; within 282 km of Phobos, where it spans 4.6 degrees, Phobos takes over; no flicker at any time warp. Demonstrated by Real Atmospheres ([Nearby body transpiler](../../../../mods/realatmospheres/features/nearby-body-transpiler.md)). Prerequisites: StarMap, Harmony, and the basics in [write a Harmony transpiler](../../../patching/transpilers/write-a-harmony-transpiler/overview.md).

## Background: how the game does it

The loop body is, in effect:

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

`num2 - MeanRadius < num` compares a body's surface distance against `num`, which after the first hit holds the previous pick's **centre** distance. A moon whose surface is nearer than the planet's centre therefore wins: Phobos does that once per orbit above Mars. Comparing surface with surface would not end it, since Phobos also passes nearer than Mars's surface. So the rank must put "the camera is close to this body in its own radii" ahead of raw distance.

## Lifecycle and timing

Install at `startup:starmap-before-main` (`[StarMapBeforeMain]`); the transpiler runs once when the patch is applied (`Patched` records whether the rewrite happened and the log says so). `GetNearbyAstronomical` runs every frame on the main thread: from `Program.OnFrameCelestials` to set `camera.NearbyCelestial` (`frame:celestials`), from `Program.UpdateShaderData` for the shader data's celestial parent (`frame:render`), and from `Program.FindNearbyCelestial`. A transpiler keeps the per-frame cost to one static call per body, where a prefix would re-run the whole loop. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Write the ranking as a plain static method.** `Take` keeps the game's own limit (a surface within `nearbyDistance`), ranks a body by its surface distance if the camera is within `NearRadii = 25` of its radii and by surface distance plus `range` otherwise (so every "near" body beats every "far" one), and writes `best` by reference:

```csharp
// NearbyBody.cs L56-L66
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

2. **Shape-check the IL before touching it.** Find the single `call`/`callvirt get_MeanRadius` returning `double`; require `sub` and `clt` around a local load; read the local slots for distance, body and best; find the `result = body; best = dist;` store sequence after it; refuse if a label or exception block would land in the replaced range. On any mismatch return the original instructions:

```csharp
// NearbyBody.cs L74-L99
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
    int dist = LoadedLocal(code[at - 2]), body = LoadedLocal(code[at - 1]), best = LoadedLocal(code[at + 2]);
    if (dist < 0 || body < 0 || best < 0)
        return code;
    // result = body; best = dist;
    int keep = -1;
    for (int i = at + 4; i + 3 < code.Count && keep < 0; i++)
        if (LoadedLocal(code[i]) == body && StoredLocal(code[i + 1]) >= 0
            && LoadedLocal(code[i + 2]) == dist && StoredLocal(code[i + 3]) == best)
            keep = i;
    if (keep < 0)
        return code;
    int result = StoredLocal(code[keep + 1]);
    // nothing may jump into what is replaced
    if (new[] { at + 1, at + 2, at + 3, keep + 2, keep + 3 }.Any(i => code[i].labels.Count > 0 || code[i].blocks.Count > 0))
        return code;
```

3. **Rewrite in place.** `sub; ldloc best; clt` becomes `ldloca.s best; ldarg.s nearbyDistance; ldloc.s result; ldnull; ceq; call Take`, and the original `best = dist` store becomes two `nop`s because `Take` now writes `best` itself. Find the argument slot of `nearbyDistance` by parameter name and type, plus one for `this`:

```csharp
// NearbyBody.cs L100-L114

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
```

4. **Install with a visible outcome.** Resolve the type by name, check the method and its parameter, patch, and log whether the rewrite applied; if not, say so, because the game then keeps the original behaviour:

```csharp
// NearbyBody.cs L29-L50
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

## Complete example

| File | Role |
|---|---|
| [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L29-L50) | Install with name and shape checks and a result log |
| [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L56-L66) | `Take`: the new ranking |
| [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L74-L115) | `Transpiler` |
| [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L117-L142) | Argument slot by name, local slot decoding |

## Variations and gotchas

- **Verified against the IL of build 2026.10.10.5554.** The expected sequence is `ldloc.s 5` (distance), `ldloc.s 4` (body), `callvirt get_MeanRadius`, `sub`, `ldloc.1` (best), `clt`, then `ldloc.s 4; stloc.0; ldloc.s 5; stloc.1`, with `nearbyDistance` as `ldarg.3`; short and long local forms are both decoded.
- **A game update breaks it quietly.** The shape check is the safety net; the log line is the only symptom, so surface it to players.
- **Callers differ.** `UpdateShaderData` passes a much larger `nearbyDistance` and a different filter; the same rewrite applies to all three because it is inside the method.
- **Other detailed-body rules** (terrain streaming, atmosphere slots) follow `camera.NearbyCelestial`, so changing the choice changes all of them at once.

## See also

- [Overview](overview.md)
- [Nearby body transpiler](../../../../mods/realatmospheres/features/nearby-body-transpiler.md)
- [Write a Harmony transpiler](../../../patching/transpilers/write-a-harmony-transpiler/overview.md), [Branch per body inside shared game shaders](../../../shaders/stock-shaders/branch-per-body-inside-shared-game-shaders/overview.md)
