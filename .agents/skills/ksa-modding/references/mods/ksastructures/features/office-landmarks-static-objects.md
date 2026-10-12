---
type: Mod Feature
title: Launch-pad landmarks that stand a walkable building on Earth
description: Stands a multi-part static object at a fixed latitude and longitude on Earth by adding IsLaunchPad landmarks to the Earth template at load, then draws it itself whenever the engine culls it
mod: ksastructures
category: celestials
techniques: [starmap-lifecycle, xml-asset, glb-model, harmony-postfix, reflection]
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
  - KSA.Camera
  - KSA.Celestial
  - KSA.Program
  - KSA.ModLibrary
tags: [static-object, landmark, launch-pad, collider, xml, ground-clutter, harmony]
---

The Kessler Systems tower is not a part or a craft: it is a static object, declared in XML the way
Core declares its launch pads, and stood on Earth by two landmarks the mod adds to the Earth
template while the game loads. A kitten can only walk on what the engine calls ground, and a
launch-pad landmark's static object is ground where a craft's colliders are not, so this is what
makes a walkable building possible. The mod also draws the tower itself whenever the engine has
culled it. Keeping grass and trees off the site is a separate patch, described in
[ground clutter exclusion zones](./ground-clutter-exclusion-zones.md).

## How it works

1. **The building is XML.** `KSAStructuresOffice.xml` declares `StaticSubObject`s (a `PartModel`
   naming a mesh from the `MeshAtlas` and a `PbrMaterial`, plus a `Collider` of `Box`es) and a
   `StaticObject` `KSAOffice_Tower` that instances them as `SubObject`s with a `Transform`, and sets
   `GroundOffset`, `SurfaceHeight` and `FootprintRadius`.[^mod-office-xml] The asset frame is +X up,
   +Y east, +Z north, which is the axis order `LocationReference.GetAxesCcf` returns and
   `UpdateStaticObjectRenderData` writes into the model matrix.[^ksa-locationreference] Two more
   static objects exist for the lift: `KSAOffice_LiftObject` (colliders only, the lift's landmark) and
   `KSAOffice_LiftVisual` (mesh only, never attached to a landmark; the mod draws it).
2. **Landmarks are added in code at `[StarMapAllModsLoaded]`.** `Office.Place` gets Earth's
   `CelestialTemplate` with `ModLibrary.TryGet`, builds two `LandmarkReference`s with
   `IsLaunchPad = true`, a `StaticObjectId`, and `Latitude`/`Longitude` as `RadianReference`s, calls
   `OnDataLoad(Mod.Empty)` so `ForwardCcf` is computed from the angles, and appends them to
   `earth.Locations`.[^mod-office] Nothing is written to Core's XML.
3. **The engine resolves them after this hook.** StarMap's `StarMapAllModsLoaded` is a postfix on
   `ModLibrary.LoadAll`; `Program` then calls `ModLibrary.AttachGameData`, whose last act is
   `StaticObject.ResolveAll`: it builds one `StaticObject` per template (render models and a Bepu
   `BigCompound` of every collider) and calls `ResolveStaticObject` on every location of every
   celestial template.[^ksa-program][^ksa-modlibrary][^ksa-staticobject] Landmarks added at
   `StarMapAllModsLoaded` are therefore resolved like Core's. `StaticObject.Find` is only answered
   after that, which is why the lift looks its object up lazily on the first simulated step.[^mod-lift]
4. **A static object stands on the terrain under its landmark's point.** For drawing,
   `UpdateStaticObjectRenderData` places the object at `GetSurfacePositionEclFromCce(ForwardCcf)`
   plus `up * GroundOffset`.[^ksa-locationreference] For collision, `ConstraintSim.ResolveLaunchPads`
   places it at `ForwardCcf * (MeanRadius + GetTerrainHeightFromDirCcf(ForwardCcf))`, then
   `UpdateStaticObjectCollider` adds `up * GroundOffset`.[^ksa-constraintsim] Because the site under
   the tower is dug (see [terrain decal pits](./terrain-decal-pits.md)), both landmarks stand 7.6 m
   south of the building on undug ground, and every `SubObject` is moved back north by
   `Position Z="7.6"`.[^mod-office-xml]
5. **One launch pad per body at a time.** `UpdateStaticObjectCollider` gives each craft or kitten
   exactly one static-object collider: the nearest resolved launch pad whose point is within 300 m
   (it compares squared distance against `90000.0`).[^ksa-constraintsim] The mod turns that limit
   into a doorway: the tower landmark and the lift landmark stand 3 m apart, and the plane halfway
   between them is the lift's door. Past it a kitten is nearer the lift landmark and stands on the
   car's colliders alone (see [the lift](./lift-moving-landmark.md)).
6. **Drawing what the engine declined to draw.** `UpdateStaticObjectRenderData` only draws when
   `Camera.IsVisibleTo` says the camera is above the tangent plane at the landmark's point, and
   `IsVisibleTo` samples the terrain with `accurate: false`, which skips the procedural modifiers,
   so it knows nothing of the levelling and digging.[^ksa-camera][^ksa-celestial] A camera low in the
   garage tunnel is under that plane and the whole site vanished. The mod's postfix on the same
   method repeats the engine's matrix maths, and draws the tower itself when `IsVisibleTo` is false,
   and always draws the lift car's visual static object at the lift's height.[^mod-office] The same
   postfix submits the garage lamps (see [garage spot lights](./garage-spot-lights.md)).
7. **Ground clutter.** The engine clears clutter round only the first four launch pads in the list,
   and Earth has five Core pads ahead of the mod's;[^ksa-core-astronomicals] the tower's 230 m `FootprintRadius` exists for
   the patch that fixes this (see [ground clutter exclusion zones](./ground-clutter-exclusion-zones.md)).
8. **Order in the list matters.** Craft placement by latitude and longitude adds the
   `GroundOffset + SurfaceHeight` of the first launch pad in list order whose footprint holds the
   point.[^ksa-vehicle] The mod appends its landmarks after Core's, so a launch from LC-39A still
   stands on LC-39A's pad where the footprints overlap.[^mod-office]

## Lifecycle and timing

| When | Hook | What runs | Why there |
|---|---|---|---|
| `startup:all-mods-loaded` | `[StarMapAllModsLoaded]` (postfix on `ModLibrary.LoadAll`) | `Office.Place`: dig the pits, add the two landmarks, install the office's Harmony patches | Templates are loaded but static objects are not resolved yet, so new landmarks get resolved by the engine's own `StaticObject.ResolveAll`; it is also the last moment terrain decals can be added |
| first simulated step | the mod's step (see [frame step plumbing](./frame-step-plumbing.md)) | `StaticObject.Find` for the lift's two objects | `StaticObject.Find` answers only after `AttachGameData` |
| `frame:render` | postfix on `LocationReference.UpdateStaticObjectRenderData`, called for every visible viewport from `Program.Render` -> `UpdateRenderingResources` -> `Universe.UpdateRenderData` -> `CelestialSystem.UpdateRenderData` -> `Celestial.UpdateRenderData`, after `OnPreRender` and before `RenderGame` records commands | draw the tower when culled, draw the car, submit lamps | This is where the engine itself queues static-object instances (and, just after, writes the frame's lights), so instances added here are drawn this frame; it runs once per visible viewport, secondary camera windows included |
| `physics:bepu` | none (engine) | `ConstraintSim.UpdateStaticObjectCollider` picks the nearest launch pad per body | Read-only for the mod; `GroundOffset` changes made on the main thread are picked up at the next collider update |

The render-data postfix runs inside the engine's render preparation: the mod wraps it in an empty
`catch` because an exception there is the game's, not the mod's.

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L31-L41) | Site coordinates: latitude, longitude, the 7.6 m landmark offset and the 3 m lift offset |
| [src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L51-L107) | `Place` and `Landmark`: add the landmarks and install patches |
| [src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L122-L160) | `AfterRenderData`: the render-data postfix |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L6048-L6104) | `KSAOffice_Tower` and `KSAOffice_LiftObject` static objects |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L6182-L6187) | `KSAOffice_LiftVisual`, drawn only by the mod |
| [src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L42-L47) | `[StarMapAllModsLoaded]` calling `Office.Place` |

## Snippets

Adding launch-pad landmarks to Earth at load:

```csharp
// src/KSAStructures/Ksa/Office.cs L55-L70, L95-L107
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
    // ...
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

The static object, offset north of its landmark, with a footprint for clutter clearing:

```xml
<!-- src/KSAStructures/KSAStructuresOffice.xml L6048-L6058, L6089-L6104 -->
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
    <!-- ... -->
    <GroundOffset M="0" />
    <SurfaceHeight M="0" />
    <!-- Ground clutter is cleared this far and 50 m more: out past the garage and its grass cover. -->
    <FootprintRadius M="230" />
  </StaticObject>
  <!-- The car alone: its own landmark, whose GroundOffset the mod writes as the lift's height. -->
  <StaticObject Id="KSAOffice_LiftObject">
    <SubObject Id="KSAOffice_Lift1" InstanceOf="KSAOffice_Lift">
      <Transform>
        <Position Z="7.6" />
      </Transform>
    </SubObject>
    <GroundOffset M="0" />
    <SurfaceHeight M="0" />
    <FootprintRadius M="0" />
  </StaticObject>
```

Drawing the tower when the engine's visibility test rejects it, and the lift car always:

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

The engine's own version, for comparison:

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

## Game APIs touched

| API | Decomp | Use |
|---|---|---|
| `LandmarkReference.IsLaunchPad`, `ShowGroundMarker` | [current/decomp/KSA/LandmarkReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LandmarkReference.cs) | Only launch-pad landmarks draw and collide their static object |
| `LocationReference.OnDataLoad`, `ForwardCcf`, `GetAxesCcf`, `GetStaticObject`, `UpdateStaticObjectRenderData` | [current/decomp/KSA/LocationReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LocationReference.cs#L40-L180) | Landmark direction, up/east/north frame, draw path |
| `CelestialTemplate.Locations` | [current/decomp/KSA/CelestialTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialTemplate.cs#L36-L41) | The list landmarks are appended to |
| `ModLibrary.TryGet`, `ModLibrary.AttachGameData` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1863-L1909) | Template lookup; resolution of static objects after `LoadAll` |
| `StaticObject.ResolveAll`, `Find`, `UpdateRenderData`, `Template` | [current/decomp/KSA/StaticObject.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObject.cs#L42-L108) | Build and draw static objects |
| `StaticObjectTemplate.GroundOffset`, `SurfaceHeight`, `FootprintRadius` | [current/decomp/KSA/StaticObjectTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectTemplate.cs#L18-L37) | Height above the landmark point, pad surface, clutter footprint |
| `Camera.IsVisibleTo` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L445-L453) | Engine's tangent-plane cull |
| `Celestial.UpdateRenderData`, `GetSurfacePositionEclFromCce`, `GetCcf2Cce` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L1594-L1604) | Per-viewport loop over locations |
| `ConstraintSim.ResolveLaunchPads`, `UpdateStaticObjectCollider` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L550-L630) | Nearest-pad-within-300-m collider |
| `Vehicle.GetLaunchPadHeightAtDirCcf` (private) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4193-L4217) | First pad in list order gives placement height |
| `Program.UpdateRenderingResources`, `Universe.UpdateRenderData` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4338-L4371) | Per-visible-viewport render-data pass inside `Program.Render` |

## Pitfalls and version notes

- **Only launch-pad landmarks render or collide a static object.** A plain `Landmark` with a
  `StaticObject` attribute is ignored by both paths (`ShowGroundMarker` is false).
- **Nearest pad wins, within 300 m.** A building near another launch pad (the tower is 190 m west
  of LC-39A) only collides for bodies nearer to it than to LC-39A. Splitting one site into several
  landmarks is how the mod gets a second collider set (the lift).
- **The static object's height is the terrain at the landmark point.** Put the landmark on ground
  nothing digs or levels differently, and offset the sub-objects instead.
- **`IsVisibleTo` uses the raw height map.** Anything built below the raw terrain (pits, basements)
  disappears for cameras under that plane unless drawn by hand as here.
- **Clutter zones are chosen once, at load.** See
  [ground clutter exclusion zones](./ground-clutter-exclusion-zones.md) for when the engine builds
  them and what "nearest the camera" means at that moment.
- **Built against 2026.10.7.5541.** The members used here exist unchanged in 2026.10.10.5554.

## See also

- [Terrain decal pits](./terrain-decal-pits.md) - why the landmark points sit on undug ground
- [Lift as a moving landmark](./lift-moving-landmark.md) - the second landmark and its `GroundOffset`
- [Terrain height roof override](./terrain-height-roof-override.md) - placement heights and `SurfaceHeight` lending
- [Garage spot lights](./garage-spot-lights.md) - submitted from the same render-data postfix
- [Generated deep ramp](./generated-deep-ramp.md) - the generated sub-objects and colliders
- [Ground clutter exclusion zones](./ground-clutter-exclusion-zones.md) - keeping clutter off the site
- [IsLaunchPad landmarks added at StarMapAllModsLoaded and a render-data postfix for culled objects](../../../howto/celestials/terrain/place-static-objects-on-a-planet-surface/place-static-objects-on-a-planet-surface-1.md) - how-to article demonstrating this feature
- [Declare XML static objects and let the engine collide the nearest pad](../../../howto/physics/colliders/add-static-colliders-to-physics-bubbles/add-static-colliders-to-physics-bubbles-3.md) - how-to article demonstrating this feature

[^mod-office]: src/KSAStructures/Ksa/Office.cs
[^mod-office-xml]: src/KSAStructures/KSAStructuresOffice.xml
[^mod-lift]: src/KSAStructures/Ksa/OfficeLift.cs
[^ksa-locationreference]: KSA.LocationReference (decompiled, build 2026.10.10.5554)
[^ksa-staticobject]: KSA.StaticObject (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-core-astronomicals]: Core Astronomicals.xml (Earth landmarks and launch-site decals)
