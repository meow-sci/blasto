---
type: How-To
title: Harmony postfix on the private PopulateLaunchPadExclusionZones with the celestial injected
description: Refill the four ground-clutter exclusion zones with the launch pads nearest the camera by postfixing the private PopulateLaunchPadExclusionZones, using the engine's own formula
category: celestials
group: terrain
topic: exclude-or-replace-ground-clutter
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksastructures/features/ground-clutter-exclusion-zones]
techniques: [harmony-postfix, reflection]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-postfix KSA.GroundClutterPlacementData.PopulateLaunchPadExclusionZones
lifecycle: [startup:all-mods-loaded, startup:game-loaded]
game_types:
  - KSA.GroundClutterPlacementData
  - KSA.ClutterCubeCellGrid
  - KSA.LandmarkReference
  - KSA.StaticObject
  - KSA.Celestial
  - KSA.Camera
tags: [ground-clutter, exclusion-zone, launch-pad, harmony-postfix, private-method]
---
Outcome: grass, rocks and trees stay off a mod-added launch-pad site even though Earth already has five Core pads ahead of it. Demonstrated by KSAStructures ([Ground clutter exclusion zones](../../../../mods/ksastructures/features/ground-clutter-exclusion-zones.md)). Prerequisites: StarMap, Harmony and a landmark with a positive `FootprintRadius` (see [place a static object](../place-static-objects-on-a-planet-surface/place-static-objects-on-a-planet-surface-1.md)).

## Background: how the game does it

`PopulateLaunchPadExclusionZones(ref ClutterCubeCellGrid.ExclusionZoneArray zones)` is private and fills four slots from the first four launch-pad landmarks in `Locations` order whose resolved `StaticObject` has a positive footprint:

```csharp
// current/decomp/KSA/GroundClutterPlacementData.cs L348-L376
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
}
```

Core declares five Earth pads (all with the `CoreLaunchPadA_Prefab_LaunchPadA` static object, [current/Content/Core/Astronomicals.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Astronomicals.xml#L1873-L1889)), so the first four slots are always Core's and a mod's pad appended after them never gets a zone. Zones are not evaluated per frame: `BuildEcotypeData` copies them into each ecotype's uniform data when the renderer builds its buffers ([current/decomp/KSA/GroundClutterPlacementData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterPlacementData.cs#L338-L346)).

## Lifecycle and timing

| When | What |
|---|---|
| `startup:all-mods-loaded` | `[StarMapAllModsLoaded]` installs the postfix, before any clutter renderer exists |
| `startup:game-loaded` (inside the `Program` constructor) | `new PlanetRenderer` builds `GroundClutterRenderer` and its ecotype data, which calls `BuildEcotypeData` and runs the postfix against the main camera of that moment |
| Only if clutter is switched on later | `Program.RebuildRenderer` creates a new `GroundClutterRenderer` and re-evaluates the zones |

At first build the camera has just followed the home body or start vehicle, so "nearest to the camera" means nearest to that load-time direction, not where the player later flies; loading a save does not rebuild the zones. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Find the private method by name** with `AccessTools.Method` and patch it with a postfix; log once and carry on if it is missing:

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

2. **Take the same `ref` array and inject the private `_celestial` field** with Harmony's triple-underscore convention (`Celestial ____celestial`).

3. **Rank launch pads by closeness to the camera.** Turn the main camera's position into a body-fixed direction, keep the four candidate pads with a positive footprint whose `ForwardCcf` is nearest, and write their zones with the engine's own formula. Return without changes if there is no camera or it is within 1 m of the body's centre:

```csharp
// src/KSAStructures/Ksa/ClutterZones.cs L34-L63
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
```

4. **Give the pad a footprint.** The tower's static object declares `FootprintRadius M="230"` so the zone reaches past the dug garage and its grass cover; a landmark with a footprint of 0 (the lift) is never a candidate.

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/ClutterZones.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/ClutterZones.cs#L19-L67) | Install and the `Nearest` postfix |
| [src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L75-L86) | Installed with the other patches at `StarMapAllModsLoaded` |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L6089-L6093) | The tower's 230 m `FootprintRadius` |

## Variations and gotchas

- **Ranking is a one-off.** With six candidates for four slots, the two pads farthest from the load-time camera direction lose their zones.
- **Private names are not API.** Both the method name and the `_celestial` field can change in an update; log and degrade.
- **The margin is the engine's 50 m**, and the zone is an angular radius `(footprint + 50) / MeanRadius`.
- **Empty `catch`.** The postfix runs in renderer construction; the mod swallows exceptions rather than stop clutter from building.
- **Build 5554.** The members above exist in 2026.10.10.5554.

## See also

- [Overview](overview.md); [approach 2](exclude-or-replace-ground-clutter-2.md)
- [Ground clutter exclusion zones](../../../../mods/ksastructures/features/ground-clutter-exclusion-zones.md)
- [Patch a private game method by name](../../../patching/internals/patch-a-private-game-method-by-name/overview.md), [Place a static object on a planet surface](../../../celestials/terrain/place-static-objects-on-a-planet-surface/overview.md)
