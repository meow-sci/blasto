---
type: How-To
title: Reading a private instance field with a triple-underscore parameter
description: Postfix the private GroundClutterPlacementData.PopulateLaunchPadExclusionZones with a ref zones array and a Celestial ____celestial parameter that Harmony fills from the private _celestial field, installed by name with a logged fallback when the method is gone
category: patching
group: internals
topic: patch-a-private-game-method-by-name
approach: 6
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksastructures/features/ground-clutter-exclusion-zones]
techniques: [harmony-postfix, field-injection, accesstools-lookup]
hooks: [harmony-postfix KSA.GroundClutterPlacementData.PopulateLaunchPadExclusionZones]
lifecycle: [startup:all-mods-loaded, startup:game-loaded]
game_types: [KSA.GroundClutterPlacementData, KSA.ClutterCubeCellGrid, KSA.Celestial]
tags: [harmony, postfix, field-injection, private-field, ref-parameter, clutter]
---

Harmony can hand a patch the value of a private field of the instance, without reflection in your code: add a parameter named with three underscores followed by the field's exact name. KSAStructures postfixes a
private method that fills a `ref` array and reads the body from the private field `_celestial`, so its parameter is `Celestial ____celestial`: three underscores plus the name `_celestial`, four in all.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by KSAStructures in [Ground clutter kept off the launch pads nearest the camera](../../../../mods/ksastructures/features/ground-clutter-exclusion-zones.md).

## Background: how the game does it

`GroundClutterPlacementData` keeps its body in a private field, and `PopulateLaunchPadExclusionZones` is a private instance method called while the ecotype data is built:

```csharp
// current/decomp/KSA/GroundClutterPlacementData.cs L44
private Celestial _celestial;
```

Source: [current/decomp/KSA/GroundClutterPlacementData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterPlacementData.cs#L44)[^ksa-groundclutterplacementdata]

```csharp
// current/decomp/KSA/GroundClutterPlacementData.cs L340-L375
	ClutterCubeCellGrid.EcotypeData result = _ecotypeReference.ToParameters();
	result.Placement.AltitudeCurveTextureId = (uint)AltitudeLutBindlessHandle;
	result.PlanetMinTerrainAltitude = (float)_celestial.MinTerrainHeightApprox;
	result.PlanetMaxTerrainAltitude = (float)_celestial.MaxTerrainHeightApprox;
	PopulateLaunchPadExclusionZones(ref result.Placement.ExclusionZones);
	return result;
}

private void PopulateLaunchPadExclusionZones(ref ClutterCubeCellGrid.ExclusionZoneArray zones)
{
	int i = 0;
	CelestialTemplate bodyTemplate = _celestial.BodyTemplate;
	if (bodyTemplate != null)
	{
		foreach (LocationReference location in bodyTemplate.Locations)
		{
			if (i >= 4)
			{
				break;
			}
			if (location is LandmarkReference { IsLaunchPad: not false } landmarkReference)
			{
				StaticObject staticObject = landmarkReference.GetStaticObject();
				if (staticObject != null && !(staticObject.FootprintRadius <= 0.0))
				{
					float w = (float)((staticObject.FootprintRadius + 50.0) / _celestial.MeanRadius);
					zones[i] = new float4(float3.Pack(landmarkReference.ForwardCcf), w);
					i++;
				}
			}
		}
	}
	for (; i < 4; i++)
	{
		zones[i] = float4.Zero;
	}
```

Source: [current/decomp/KSA/GroundClutterPlacementData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterPlacementData.cs#L340-L375)

It writes the first four launch pads (by `Locations` order) into a four-slot `ref ExclusionZoneArray`, as `(direction, (footprint + 50 m) / MeanRadius)`.

## Lifecycle and timing

Install: `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`). Run: the zones are baked when the ground clutter renderer builds its buffers, inside the `Program` constructor (`startup:game-loaded`, Program.cs ~L1158)
and again only if ground clutter is switched off and on. The postfix is not a per-frame hook. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Hook the frame with StarMap lifecycle attributes](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md).

## Steps

1. **Find the method by name, tolerate its absence.** Private, so `AccessTools.Method`; a null result logs once and leaves the game's behaviour unchanged. Any thrown exception is caught and logged too:

```csharp
// src/KSAStructures/Ksa/ClutterZones.cs L19-L32
public static void Install(Harmony harmony)
{
    try
    {
        MethodInfo? target = AccessTools.Method(typeof(GroundClutterPlacementData), "PopulateLaunchPadExclusionZones");
        if (target is null) { Log.Warn("office: no clutter hook; grass and trees grow in what is dug"); return; }

        harmony.Patch(target, postfix: new HarmonyMethod(typeof(ClutterZones).GetMethod(nameof(Nearest), BindingFlags.NonPublic | BindingFlags.Static)!));
    }
    catch (Exception e)
    {
        Log.Warn($"office: could not hook the clutter zones ({e.GetBaseException().Message})");
    }
}
```

Source: [src/KSAStructures/Ksa/ClutterZones.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/ClutterZones.cs#L19-L32)[^ksastructures-clutterzones]

2. **Declare the postfix with the original's `ref` parameter and a field injector.** The parameter `zones` matches the original's name and is `ref` because the original's is. `____celestial` is not a parameter of the
   original; Harmony fills it from the instance field `_celestial` (three underscores, then the field name):

```csharp
// src/KSAStructures/Ksa/ClutterZones.cs L34-L67
private static void Nearest(ref ClutterCubeCellGrid.ExclusionZoneArray zones, Celestial ____celestial)
{
    try
    {
        if (____celestial?.BodyTemplate is not { } template || Program.GetMainCamera() is not { } camera) return;

        double3 eye = (camera.PositionEcl - ____celestial.GetPositionEcl()).Transform(____celestial.GetCce2Ccf());
        if (eye.Length() < 1.0) return;
        eye = eye.Normalized();

        Span<float4> best = stackalloc float4[Zones];
        Span<double> near = stackalloc double[Zones];
        near.Fill(double.MaxValue);

        foreach (LocationReference location in template.Locations)
        {
            if (location is not LandmarkReference { IsLaunchPad: true } pad || pad.GetStaticObject() is not { } pads) continue;
            if (pads.FootprintRadius <= 0.0) continue;

            double away = (pad.ForwardCcf - eye).LengthSquared();
            int worst = 0;
            for (int i = 1; i < Zones; i++) if (near[i] > near[worst]) worst = i;
            if (away >= near[worst]) continue;

            near[worst] = away;
            best[worst] = new float4(float3.Pack(pad.ForwardCcf), (float)((pads.FootprintRadius + CellMargin) / ____celestial.MeanRadius));
        }

        for (int i = 0; i < Zones; i++) zones[i] = near[i] < double.MaxValue ? best[i] : float4.Zero;
    }
    catch
    {
    }
}
```

Source: [src/KSAStructures/Ksa/ClutterZones.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/ClutterZones.cs#L34-L67)

3. **Prove the naming rule on a throwaway type.** A local test with Lib.Harmony 2.4.2 patched a method of a class whose base declares `private int _hidden`:

```text
// observed output of a local test (an experiment project, not part of any mod repository)
P:  HarmonyException: No such field defined in class Derived, ht, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null (Parameter 'hidden')
P2: prefix sees ____hidden=7
```

   `___hidden` looks for a field named `hidden` and fails; `____hidden` finds `_hidden`, including on a base class. Count the underscores of the field itself.

4. **Keep the postfix fault-proof.** It runs while the renderer builds buffers; the mod's body is a `try`/`catch` that swallows, so a failure leaves the engine's own four zones ([Static patch classes that look up the target, log once and stand down](../../safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-3.md)).

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/ClutterZones.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/ClutterZones.cs) | Lookup, install, postfix with `ref` array and field injector |

## Variations and gotchas

- **Field name drift.** If KSA renames `_celestial`, Harmony throws at patch time ("No such field defined in class"), which the mod's `try`/`catch` turns into the logged fallback. With `PatchAll` it would stop the rest of the patch chain ([Prefer __instance and a public member over a field injector that matches nothing](patch-a-private-game-method-by-name-7.md)).
- **Reading and writing.** A plain `___name` parameter receives the field's value; per Harmony's documentation a `ref ___name` parameter also lets the patch write it back. Neither is exercised by KSAStructures, which only reads.
- **Pad ordering is shared.** Four slots per body serve every mod; the last postfix wins ([Keep grass, rocks and trees off a custom site, and replace or preserve a planet's ground clutter](../../../celestials/terrain/exclude-or-replace-ground-clutter/overview.md)).
- **Version.** Built against 2026.10.7.5541; the method is unchanged in 2026.10.10.5554.

## See also

- [HarmonyPatch attribute with a string method name, applied by PatchAll](patch-a-private-game-method-by-name-1.md), [Manual AccessTools lookup with a degrade path when the method is gone](patch-a-private-game-method-by-name-2.md), [Explicit parameter arrays with MakeByRefType for overloaded and by-reference targets](patch-a-private-game-method-by-name-3.md), [Attribute argument types that bind by assignability](patch-a-private-game-method-by-name-4.md), [TargetMethods with per-name AccessTools lookups and the __args array](patch-a-private-game-method-by-name-5.md), [Prefer __instance and a public member over a field injector that matches nothing](patch-a-private-game-method-by-name-7.md)
- [Patching private and overloaded game methods by name](overview.md)
- [Ground clutter kept off the launch pads nearest the camera](../../../../mods/ksastructures/features/ground-clutter-exclusion-zones.md)
- [Reading, writing and calling private game members](../call-private-game-members-through-reflection-and-delegates/overview.md) for reading private fields from code that is not a patch

[^ksa-groundclutterplacementdata]: KSA.GroundClutterPlacementData (decompiled, build 2026.10.10.5554)
[^ksastructures-clutterzones]: KSAStructures/src/KSAStructures/Ksa/ClutterZones.cs
