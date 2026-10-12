---
type: Mod Feature
title: Ground clutter kept off the launch pads nearest the camera
description: A postfix on the private GroundClutterPlacementData.PopulateLaunchPadExclusionZones replaces the engine's first-four-pads-in-list-order clutter exclusion with the four launch pads nearest the main camera, so a mod-added pad gets its grass and trees cleared
mod: ksastructures
category: celestials
techniques: [harmony-postfix, starmap-lifecycle]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-postfix KSA.GroundClutterPlacementData.PopulateLaunchPadExclusionZones
lifecycle: [startup:all-mods-loaded, startup:game-loaded]
game_types:
  - KSA.GroundClutterPlacementData
  - KSA.ClutterCubeCellGrid
  - KSA.ClutterEcotypeRenderData
  - KSA.ClutterEcotypePhysicalData
  - KSA.GroundClutterRenderer
  - KSA.PlanetRenderer
  - KSA.LandmarkReference
  - KSA.StaticObject
  - KSA.Celestial
  - KSA.Camera
  - KSA.Universe
tags: [ground-clutter, launch-pad, exclusion-zone, landmark, harmony, private-method]
---

KSA clears grass, rocks and trees around launch pads, but only around four of them per body, and it
picks the first four in the body template's `Locations` list. Earth already has five Core launch pads,
so a launch-pad landmark a mod appends never gets a zone, and clutter grows across its site (in
KSAStructures' case, out of the floor of the dug garage). The mod postfixes the private method that
fills the four zones and refills them with the four pads nearest the main camera.[^mod-clutter]

## How it works

1. **The engine's rule.** `GroundClutterPlacementData.BuildEcotypeData` calls the private
   `PopulateLaunchPadExclusionZones(ref ClutterCubeCellGrid.ExclusionZoneArray zones)`. It walks
   `_celestial.BodyTemplate.Locations` in order and, for the first four `LandmarkReference`s with
   `IsLaunchPad` whose resolved `StaticObject` has a positive `FootprintRadius`, writes
   `float4(ForwardCcf, (FootprintRadius + 50 m) / MeanRadius)`: a direction and an angular radius.
   Unused slots are zeroed.[^ksa-clutterplacement]
2. **Why a mod's pad loses.** Core declares five Earth launch pads (LC-39A, VSFB LC-4, Starbase OLM A,
   GSC ELA-4, Mahia LC-1A), all with the `CoreLaunchPadA` static object and its 108.3 m footprint,
   so the first four slots are always Core's.[^ksa-core-astronomicals][^ksa-core-launchpad]
   KSAStructures appends its two landmarks after Core's on purpose (see
   [launch-pad landmarks](./office-landmarks-static-objects.md)); its tower's `FootprintRadius` of
   230 m exists for this zone, and its lift landmark has a footprint of 0, so it is never a
   candidate.[^mod-office][^mod-office-xml]
3. **The postfix.** `ClutterZones.Nearest` takes the same `ref` array plus the private `_celestial`
   field through Harmony's triple-underscore injection (`Celestial ____celestial`). It turns the main
   camera's position into a body-fixed direction, keeps the four launch pads with a positive footprint
   whose `ForwardCcf` is closest to it, and writes their zones with the engine's own formula. If there
   is no camera or the camera is within 1 m of the body's centre it returns and leaves the engine's
   four.[^mod-clutter]
4. **Installed at load.** `Office.Place` installs it with the other office patches from
   `[StarMapAllModsLoaded]`, by name through `AccessTools.Method`, because the target is private; a
   missing method is logged once and clutter stays as the engine places it.[^mod-office][^mod-clutter]

## Lifecycle and timing

The zones are not evaluated per frame. They are baked into each ecotype's uniform data when the
ground clutter renderer builds its buffers:

| When | Call chain | Result |
|---|---|---|
| `startup:all-mods-loaded` | `[StarMapAllModsLoaded]` -> `Office.Place` -> `ClutterZones.Install` | Postfix installed before any clutter renderer exists |
| `startup:game-loaded` (inside the `Program` constructor, after `Universe.LoadDefaultSystem`) | `new PlanetRenderer` -> `new GroundClutterRenderer` + `BuildFrameResources` -> `CreateRenderData` -> `ClutterEcotypeRenderData.BuildRenderResources` and `ClutterEcotypePhysicalData.BuildFrameResources` -> `PopulateEcotypeDataBuffer` -> `BuildEcotypeData` -> postfix | Zones copied through a staging buffer into each ecotype's `EcotypeInfoBuffer`, for drawing and for clutter collisions alike |
| Only if ground clutter is switched on after it was off | `Program.RebuildRenderer` -> `PlanetRenderer.RebuildFrameResources` -> `RebuildGroundClutterResources` creates a new `GroundClutterRenderer` | Zones re-evaluated against the camera of that moment |
| Clutter debug UI edits | `GroundClutterRenderer.OnDrawUI` repopulates one ecotype's buffer | Same, for that ecotype |

The first build runs at Program line 1158, after `Universe.LoadDefaultSystem` (lines 1066-1074)
has run `Universe.OnLoaded`, which executes `follow <home body>` (or `follow <start vehicle>` in a
testing start). `Camera.SetFollow` puts the camera 2.5 body radii out along its current forward
vector.[^ksa-program][^ksa-universe][^ksa-camera] So "nearest to the camera" means nearest to that
load-time direction, not to wherever the player later flies, and loading a save does not rebuild the
zones. On Earth there are six candidates for four slots (the five Core pads and the tower). The tower
and LC-39A are 190 m apart and rank together, so both lose only when they are the two pads farthest
from the load-time camera direction.[^ksa-clutterrenderer][^ksa-planetrenderer][^ksa-ecotyperenderdata][^ksa-ecotypephysicaldata]

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/ClutterZones.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/ClutterZones.cs#L19-L67) | Install and the `Nearest` postfix |
| [src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L75-L86) | Installed with the other office patches |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L6089-L6093) | The tower's 230 m `FootprintRadius` |

## Snippets

The postfix, with the private field injected by name:

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

The engine's version, which the postfix overwrites:

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

## Game APIs touched

| API | Decomp | Use |
|---|---|---|
| `GroundClutterPlacementData.BuildEcotypeData`, `PopulateLaunchPadExclusionZones` (private), `_celestial` (private) | [current/decomp/KSA/GroundClutterPlacementData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterPlacementData.cs#L338-L376) | Patched; four zones per ecotype |
| `ClutterEcotypeRenderData.PopulateEcotypeDataBuffer` | [current/decomp/KSA/ClutterEcotypeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ClutterEcotypeRenderData.cs#L582-L597) | Copies the ecotype data (zones included) to the GPU once |
| `ClutterEcotypePhysicalData.PopulateEcotypeDataBuffer` | [current/decomp/KSA/ClutterEcotypePhysicalData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ClutterEcotypePhysicalData.cs#L670-L685) | The same for clutter collisions |
| `GroundClutterRenderer.BuildFrameResources`, `CreateRenderData` | [current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L600-L639) | Where the ecotype buffers are built |
| `PlanetRenderer` constructor, `RebuildGroundClutterResources` | [current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L1725-L1743) | Creates the clutter renderer at load, or when clutter is re-enabled |
| `Universe.OnLoaded`, `FollowBody` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2534-L2547) | Where the camera is when the zones are first built |
| `Camera.SetFollow` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L602-L618) | Puts the camera 2.5 radii out along its forward vector |

## Pitfalls and version notes

- **Four zones per body, shared by every mod.** A second mod patching the same method, or adding
  pads, competes for the same four slots; the last postfix to run wins.
- **Evaluated at load, not live.** Moving the camera to the site does not re-select the zones; the
  selection is fixed for the session unless ground clutter is toggled off and on. A pad far from the
  load-time camera direction can still end up without a zone.
- **Clutter grows from the dug terrain.** Clutter generation runs every terrain modifier's compute
  pipeline over the clutter cells' heights (`GroundClutterRenderer` loops
  `TerrainModifiersRenderData.NumModifiers`), so a pit without a zone gets clutter on its floor
  ([current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L1413-L1429)).
- **Private target.** `PopulateLaunchPadExclusionZones` and `_celestial` are found by name; a rename
  turns the feature off with one warning.
- **Built against 2026.10.7.5541**; the method is unchanged in 2026.10.10.5554.

## See also

- [Launch-pad landmarks](./office-landmarks-static-objects.md) - the landmarks whose footprints this clears
- [Terrain decal pits](./terrain-decal-pits.md) - the dug ground the clutter would grow on
- [Harmony postfix on the private PopulateLaunchPadExclusionZones with the celestial injected](../../../howto/celestials/terrain/exclude-or-replace-ground-clutter/exclude-or-replace-ground-clutter-1.md) - how-to article demonstrating this feature
- [Reading a private instance field with a triple-underscore parameter](../../../howto/patching/internals/patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-6.md) - how-to article demonstrating this feature

[^mod-clutter]: src/KSAStructures/Ksa/ClutterZones.cs
[^mod-office]: src/KSAStructures/Ksa/Office.cs
[^mod-office-xml]: src/KSAStructures/KSAStructuresOffice.xml
[^ksa-clutterplacement]: KSA.GroundClutterPlacementData (decompiled, build 2026.10.10.5554)
[^ksa-ecotyperenderdata]: KSA.ClutterEcotypeRenderData (decompiled, build 2026.10.10.5554)
[^ksa-ecotypephysicaldata]: KSA.ClutterEcotypePhysicalData (decompiled, build 2026.10.10.5554)
[^ksa-clutterrenderer]: KSA.GroundClutterRenderer (decompiled, build 2026.10.10.5554)
[^ksa-planetrenderer]: KSA.PlanetRenderer (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-core-astronomicals]: Core Astronomicals.xml (Earth launch-pad landmarks)
[^ksa-core-launchpad]: Core CoreLaunchPadAGameData.xml (launch pad footprint)
