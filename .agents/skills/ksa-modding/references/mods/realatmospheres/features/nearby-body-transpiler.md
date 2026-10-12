---
type: Mod Feature
title: Nearby-body choice transpiler
description: A Harmony transpiler rewrites the comparison in CelestialSystem.GetNearbyAstronomical so a body within 25 of its radii ranks first, stopping a passing small moon from stealing its planet's detailed render
mod: realatmospheres
category: patching
techniques: [harmony-transpiler, reflection]
hooks: [starmap StarMapBeforeMain, harmony-transpiler KSA.CelestialSystem.GetNearbyAstronomical]
lifecycle: [startup:starmap-before-main, frame:celestials, frame:render]
game_types: [KSA.CelestialSystem, KSA.Program, KSA.Camera, KSA.Astronomical]
tags: [patching, harmony, transpiler, il, celestials, rendering, time-warp, flicker]
---

The game draws one body per camera in detail (terrain, volumetric clouds, full atmosphere) and the
rest as distant bodies. It picks that body with `CelestialSystem.GetNearbyAstronomical`, which walks all
bodies and takes each whose surface distance is smaller than the previous pick's centre distance
([current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L565-L584)).[^ksa-celestialsystem]
From 8,400 km of Mars's centre, Phobos comes nearer than Mars's centre once an orbit, and Mars drops to
a distant render for that stretch; at x14400 warp that is a flicker every 2 seconds
([NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L7-L21)). The mod replaces the comparison in place.[^ra-nearbybody]

## How it works

- **New rank.** `Take(centreDistance, radius, ref best, range, none)` keeps the game's own limit (a
  surface within `nearbyDistance`) but ranks a body by its surface distance if the camera is within
  `NearRadii = 25` of its radii (where it spans about 4.6 degrees) and by surface distance plus `range`
  otherwise, so every "near" body beats every "far" one; among equals the nearer surface wins
  ([NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L24-L66)). Above Mars, Mars stays; within 282 km of Phobos, Phobos
  takes over.
- **IL shape check.** The transpiler finds the single `call/callvirt get_MeanRadius` returning
  `double`, requires `sub` and `clt` around a local load, reads the local slots for distance, body and
  best, then finds the `result = body; best = dist;` store sequence after it. It bails out, returning
  the original IL, on any mismatch or if a label or exception block would land in the replaced range
  ([NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L74-L99)).
- **Rewrite.** `sub; ldloc best; clt` becomes `ldloca.s best; ldarg.s nearbyDistance; ldloc.s result;
  ldnull; ceq; call Take`, and the original `best = dist` store becomes two `nop`s, because `Take` now
  writes `best` by reference ([NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L101-L114)). The argument slot of
  `nearbyDistance` is found by parameter name and type, plus one for `this`
  ([NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L117-L123)).
- **Verified against the IL of build 2026.10.10.5554.** Disassembling `KSA.dll` for this page shows the
  exact expected sequence: `ldloc.s 5` (distance), `ldloc.s 4` (body), `callvirt get_MeanRadius`, `sub`,
  `ldloc.1` (best), `clt`, then `ldloc.s 4; stloc.0; ldloc.s 5; stloc.1`, with `nearbyDistance` as
  `ldarg.3`.

## Lifecycle and timing

- **Install at `startup:starmap-before-main`** ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L144-L145)). The transpiler
  runs once at patch time; `Patched` records whether the rewrite happened, and the log says so
  ([NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L29-L50)).
- **Runs every frame, main thread.** `GetNearbyAstronomical` is called from `Program.OnFrameCelestials`
  to set `camera.NearbyCelestial` (`frame:celestials`), from `Program.UpdateShaderData` to pick the
  celestial parent for the shader data (`frame:render`), and from `Program.FindNearbyCelestial`, which
  `Camera` uses as a fallback
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2701-L2715),
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2927-L2931),
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5228-L5231)).[^ksa-program]
  A transpiler keeps the per-frame cost to one static call per body instead of a prefix that would
  re-run the whole loop.

## Key code

| Path | Role |
|---|---|
| [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L29-L50) | Install with name/shape checks and a result log |
| [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L56-L66) | `Take`: the new ranking |
| [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L74-L115) | `Transpiler` |
| [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L117-L142) | Argument slot by name, local slot decoding |

## Snippets

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

```csharp
// NearbyBody.cs L76-L114
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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `CelestialSystem.GetNearbyAstronomical` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L565-L584) | Transpiled |
| `Astronomical.MeanRadius` | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L110) | The IL anchor (`get_MeanRadius`) |
| `Program.OnFrameCelestials`, `UpdateShaderData`, `FindNearbyCelestial` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2701-L2715) | Callers, every frame |

## Pitfalls and version notes

- **Short-form opcodes only.** The rewrite emits `ldloca.s`, `ldarg.s` and `ldloc.s`, so it refuses
  local or argument indices above 255 ([NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L137-L142)); a debug build of
  the game with many locals could exceed that.
- **Affects every caller.** The same rule also changes the shader-data parent choice with its larger
  37.4e9 m range; the ranking is consistent there, but other mods that call `GetNearbyAstronomical`
  inherit it too.
- **Silent fallback.** If the comparison has changed the transpiler returns the original IL and the
  log notes that a small moon can still take its planet's place.

## See also

- [Reflection-only mod build](reflection-only-mod-build.md)
- [Atmosphere uniform side channel](atmosphere-uniform-side-channel.md)
- [Transpile the distance comparison into a ranking call that favours a body within 25 radii](../../../howto/celestials/atmospheres-and-rings/choose-which-body-the-game-renders-in-detail/choose-which-body-the-game-renders-in-detail.md) - how-to article demonstrating this feature
- [Replace an instruction run with a helper that takes a local by reference](../../../howto/patching/transpilers/write-a-harmony-transpiler/write-a-harmony-transpiler-3.md) - how-to article demonstrating this feature

[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ra-nearbybody]: NearbyBody.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
