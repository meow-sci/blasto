---
type: How-To
title: IsLaunchPad landmarks added at StarMapAllModsLoaded and a render-data postfix for culled objects
description: Append LandmarkReference entries to a body template before the engine resolves static objects so a multi-part XML building gets render models and colliders, and redraw it with a postfix when the engine culls it
category: celestials
group: terrain
topic: place-static-objects-on-a-planet-surface
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksastructures/features/office-landmarks-static-objects]
techniques: [xml-asset, harmony-postfix]
hooks: [starmap StarMapAllModsLoaded, harmony-postfix KSA.LocationReference.UpdateStaticObjectRenderData]
lifecycle: [startup:all-mods-loaded, frame:render, physics:bepu]
game_types:
  - KSA.LandmarkReference
  - KSA.LocationReference
  - KSA.CelestialTemplate
  - KSA.StaticObject
  - KSA.StaticObjectTemplate
  - KSA.StaticSubObjectTemplate
  - KSA.ConstraintSim
  - KSA.ModLibrary
  - KSA.Camera
  - KSA.Celestial
  - KSA.IViewport
tags: [static-object, landmark, launch-pad, culling, harmony-postfix]
---
Outcome: a multi-part building (render models plus Bepu box colliders) stood on a planet at a latitude and longitude, walkable by kittens, and drawn even when the engine's visibility test rejects it. Demonstrated by KSAStructures' office tower ([Office landmarks and static objects](../../../../mods/ksastructures/features/office-landmarks-static-objects.md)). Prerequisites: StarMap, Harmony, a static object declared in your mod's XML.

## Background: how the game does it

A building is an XML `StaticObject` that instances `StaticSubObject`s (a `PartModel` naming a mesh and a `PbrMaterial` plus a `Collider` of `Box`es) and carries `GroundOffset`, `SurfaceHeight` and `FootprintRadius`. A `LandmarkReference` in the body template's `Locations` list stands it on the planet. Facts that drive the steps:

- `ModLibrary.AttachGameData` finishes with `StaticObject.ResolveAll`, which resolves every location of every celestial template ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1863-L1908), [current/decomp/KSA/StaticObject.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObject.cs#L47-L75)); `StaticObject.Find` answers only after that.
- The engine draws at `GetSurfacePositionEclFromCce(ForwardCcf) + up * GroundOffset` ([current/decomp/KSA/LocationReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LocationReference.cs#L156-L180)) and collides at `ForwardCcf * (MeanRadius + GetTerrainHeightFromDirCcf(ForwardCcf))` plus `GroundOffset` ([current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L550-L583), [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L584-L620)).
- Each craft or kitten gets exactly one static-object collider: the nearest launch pad within 300 m. The launch-pad list is built once per celestial.
- The draw only happens when `Camera.IsVisibleTo` is true, and that test samples terrain with `accurate: false` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L445-L453)), so it knows nothing of terrain decals; see [dig a site](../add-terrain-decals-and-dig-a-site/overview.md).

## Lifecycle and timing

`[StarMapAllModsLoaded]` (`startup:all-mods-loaded`) is a postfix on `ModLibrary.LoadAll`; `AttachGameData` runs after it, so landmarks added here are resolved by the engine like Core's. The render postfix runs once per visible viewport inside the engine's render preparation (`frame:render`: `Program.Render` to `UpdateRenderingResources` to `Universe.UpdateRenderData` to `LocationReference.UpdateStaticObjectRenderData`), so instances queued there are drawn this frame, secondary viewports included. Collision is engine-side at `physics:bepu`; the mod only changes `GroundOffset`, which is picked up at the next collider update. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare the object in XML.** A `StaticObject` with `SubObject`s (asset frame +X up, +Y east, +Z north), `GroundOffset`, `SurfaceHeight` and a `FootprintRadius` big enough for the clutter patch. The mod offsets every sub-object 7.6 m north of the landmark so the landmark stands on undug ground:

```xml
<!-- src/KSAStructures/KSAStructuresOffice.xml L6048-L6058 -->
<StaticObject Id="KSAOffice_Tower">
  <SubObject Id="KSAOffice_Shell1" InstanceOf="KSAOffice_Shell">
    <Transform>
      <Position Y="3" Z="7.6" />
    </Transform>
  </SubObject>
  <SubObject Id="KSAOffice_Windows1" InstanceOf="KSAOffice_Windows">
    <Transform>
      <Position Y="3" Z="7.6" />
    </Transform>
  </SubObject>
```

2. **Add landmarks in code at `[StarMapAllModsLoaded]`.** Fetch the body's `CelestialTemplate`, build `LandmarkReference`s with `IsLaunchPad = true`, a `StaticObjectId` and `Latitude`/`Longitude` as `RadianReference`s, call `OnDataLoad(Mod.Empty)` so `ForwardCcf` is computed, and append to `Locations`. Append *after* Core's pads so a launch from a stock pad still stands on its own pad where footprints overlap:

```csharp
// src/KSAStructures/Ksa/Office.cs L55-L70
if (!ModLibrary.TryGet("Earth", out CelestialTemplate? earth) || earth is null)
{
    Log.Warn("office: no Earth template to stand the tower on");
    return;
}

if (earth.Locations.Exists(l => l.Id == TowerLandmark)) return;

Dig(earth);

_tower = Landmark(TowerLandmark, TowerId, TowerLongitudeDeg);
_lift = Landmark(LiftLandmark, LiftId, LiftLongitudeDeg);
// After Core's pads, so a launch from one of them still stands on its own pad where the
// footprints overlap: the first landmark whose footprint holds a place gives its height.
earth.Locations.Add(_tower);
earth.Locations.Add(_lift);
```

```csharp
// src/KSAStructures/Ksa/Office.cs L95-L107
private static LandmarkReference Landmark(string id, string staticObject, double longitudeDeg)
{
    var landmark = new LandmarkReference
    {
        Id = id,
        IsLaunchPad = true,
        StaticObjectId = staticObject,
        Latitude = new RadianReference(LandmarkLatitudeDeg * Math.PI / 180.0),
        Longitude = new RadianReference(longitudeDeg * Math.PI / 180.0),
    };
    landmark.OnDataLoad(Mod.Empty);
    return landmark;
}
```

3. **Use the pad as a doorway.** Only the nearest launch pad within 300 m is a craft's or kitten's static collider. Placing a second landmark (here a lift) 3 m away makes the plane halfway between them the door: past it the kitten is nearer the second landmark and stands on its colliders alone.

4. **Patch the render-data method to redraw when culled.** Find it by name, install a postfix (`Harmony.Patch(target, postfix: ...)`), and in the postfix repeat the engine's matrix maths. Return early when `camera.IsVisibleTo(...)` is true (the engine already drew it); otherwise draw it yourself with the same matrix (`asmb2Ego` = axes plus `up * GroundOffset`):

```csharp
// src/KSAStructures/Ksa/Office.cs L75-L86
MethodInfo? target = AccessTools.Method(typeof(LocationReference), nameof(LocationReference.UpdateStaticObjectRenderData));
MethodInfo postfix = typeof(Office).GetMethod(nameof(AfterRenderData), BindingFlags.NonPublic | BindingFlags.Static)!;
if (target is null) Log.Warn("office: no render hook, the lift car is not drawn");
else
{
    _harmony = new Harmony(HarmonyId);
    _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
    Underground.Install(_harmony);
    OfficeDry.Install(_harmony);
    ClutterZones.Install(_harmony);
    KeepOutOfSaves(_harmony);
}
```

```csharp
// src/KSAStructures/Ksa/Office.cs L122-L160
private static void AfterRenderData(LocationReference __instance, IViewport viewport, Celestial celestial, int frameIndex)
{
    try
    {
        bool tower = ReferenceEquals(__instance, _tower);
        if (!tower && !ReferenceEquals(__instance, _lift)) return;

        Camera camera = viewport.GetCamera();
        doubleQuat ccf2Cce = celestial.GetCcf2Cce();
        double3 forwardCce = __instance.ForwardCcf.Transform(ccf2Cce);
        double3 origin = camera.EclToEgo(celestial.GetSurfacePositionEclFromCce(forwardCce));
        __instance.GetAxesCcf(out double3 upCcf, out double3 eastCcf, out double3 northCcf);
        double3 up = upCcf.Transform(ccf2Cce), east = eastCcf.Transform(ccf2Cce), north = northCcf.Transform(ccf2Cce);

        if (tower)
        {
            Underground.Light(viewport, origin + (east * 3.0) - (north * LandmarkNorth), up, east, north);
            if (camera.IsVisibleTo(forwardCce, celestial)) return;
        }

        StaticObject? drawn = tower ? __instance.GetStaticObject() : _carVisual;
        if (drawn is null) return;

        double car = _drawn;
        if (!tower && _carOnRider > 0 && KsaWorld.ControlledVehicle is { } rider && KsaWorld.IsAlive(rider)
            && Rest.TryGetValue(rider, out double rest))
        {
            car = Math.Clamp(InLiftFrame(rider, celestial).X - rest, LowestStop, OfficeStop);
        }

        double3 at = tower ? origin + (up * drawn.GroundOffset)
                           : origin + (up * (car + _correction)) - (north * LandmarkNorth);

        var asmb2Ego = new double4x4(up.X, up.Y, up.Z, 0.0, east.X, east.Y, east.Z, 0.0,
                                     north.X, north.Y, north.Z, 0.0, at.X, at.Y, at.Z, 1.0);
        drawn.UpdateRenderData(viewport, frameIndex, in asmb2Ego);
    }
    catch { }
}
```

   Compare with the engine's own method, whose matrix you repeat:

```csharp
// current/decomp/KSA/LocationReference.cs L156-L180
public void UpdateStaticObjectRenderData(IViewport viewport, Celestial celestial, int frameIndex)
{
	if (!ShowGroundMarker)
	{
		return;
	}
	StaticObject staticObject = GetStaticObject();
	if (staticObject != null)
	{
		Camera camera = viewport.GetCamera();
		doubleQuat ccf2Cce = celestial.GetCcf2Cce();
		double3 double5 = ForwardCcf.Transform(ccf2Cce);
		if (camera.IsVisibleTo(double5, celestial))
		{
			double3 double6 = camera.EclToEgo(celestial.GetSurfacePositionEclFromCce(double5));
			GetAxesCcf(out var upCcf, out var eastCcf, out var northCcf);
			double3 double7 = upCcf.Transform(ccf2Cce);
			double3 double8 = eastCcf.Transform(ccf2Cce);
			double3 double9 = northCcf.Transform(ccf2Cce);
			double3 double10 = double6 + double7 * staticObject.GroundOffset;
			double4x4 matrixAsmb2Ego = new double4x4(double7.X, double7.Y, double7.Z, 0.0, double8.X, double8.Y, double8.Z, 0.0, double9.X, double9.Y, double9.Z, 0.0, double10.X, double10.Y, double10.Z, 1.0);
			staticObject.UpdateRenderData(viewport, frameIndex, in matrixAsmb2Ego);
		}
	}
}
```

5. **Wrap the postfix in an empty `catch`.** It runs inside the game's render preparation; an exception there is the game's, not yours.

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L51-L107) | `Place` and `Landmark`: add landmarks, install patches |
| [src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L122-L160) | `AfterRenderData`: the render-data postfix |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L6048-L6104) | `KSAOffice_Tower` and `KSAOffice_LiftObject` |
| [src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L42-L47) | `[StarMapAllModsLoaded]` calling `Office.Place` |

## Variations and gotchas

- **Order in `Locations` matters.** Craft placement by latitude and longitude adds `GroundOffset + SurfaceHeight` of the first launch pad in list order whose footprint holds the point.
- **Engine clutter clearance covers only the first four pads.** A fifth landmark gets none; see [exclude ground clutter](../exclude-or-replace-ground-clutter/overview.md).
- **`StaticObject.Find` is empty before `AttachGameData`.** Look it up lazily on the first simulated step.
- **Collision list is cached.** Landmarks added after the first collider update for a celestial are not seen; add them at load.
- **Built against 2026.10.7.5541** upstream; the members above exist in 2026.10.10.5554.

## See also

- [Overview](overview.md); [approach 2](place-static-objects-on-a-planet-surface-2.md); [approach 3](place-static-objects-on-a-planet-surface-3.md)
- [Office landmarks and static objects](../../../../mods/ksastructures/features/office-landmarks-static-objects.md)
- [Add terrain decals and dig a site](../../../celestials/terrain/add-terrain-decals-and-dig-a-site/overview.md), [Exclude or replace ground clutter](../../../celestials/terrain/exclude-or-replace-ground-clutter/overview.md), [Add static colliders to physics bubbles](../../../physics/colliders/add-static-colliders-to-physics-bubbles/overview.md)
