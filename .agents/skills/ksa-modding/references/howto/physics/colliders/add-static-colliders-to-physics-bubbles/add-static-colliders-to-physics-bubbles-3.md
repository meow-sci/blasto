---
type: How-To
title: Declare XML static objects and let the engine collide the nearest pad
description: Stand a walkable building on a planet by declaring StaticObject XML and appending launch-pad landmarks to the planet template at AllModsLoaded, so the engine places and collides the nearest pad's colliders for every body
category: physics
group: colliders
topic: add-static-colliders-to-physics-bubbles
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksastructures/features/office-landmarks-static-objects]
techniques: [xml-asset, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, physics:bepu, frame:render]
game_types: [KSA.StaticObject, KSA.LandmarkReference, KSA.CelestialTemplate, KSA.ConstraintSim, KSA.KittenEva]
tags: [physics, statics, xml, launch-pad, landmark, static-object, colliders, kittens, ksastructures]
---

Outcome: a building a kitten can walk around and stand on, with no physics code. KSAStructures' tower is a static
object declared in XML the way Core declares its launch pads and stood on Earth by two landmarks the mod adds to the
Earth template while the game loads. A kitten can only walk on what the engine calls ground, and a launch-pad
landmark's static object is ground where a craft's colliders are not. Prerequisites: StarMap (the
`StarMapAllModsLoaded` attribute).

## Background: how the game does it

`ConstraintSim.ResolveLaunchPads` places each resolved launch pad at
`ForwardCcf * (MeanRadius + terrain height)`; `UpdateStaticObjectCollider` then adds `up * GroundOffset` and gives
each body exactly **one** static-object collider: the nearest resolved launch pad whose point is within 300 m
(squared distance under 90000):[^ksa-constraintsim]

```csharp
// current/decomp/KSA/ConstraintSim.cs L584-L633
	public void UpdateStaticObjectCollider(ReadOnlyPhysicsStates states, ref BepuHandles handles)
	{
		List<LaunchPadPose> list = ResolveLaunchPads(states);
		if (list.Count <= 0)
		{
			RemoveStaticObjectCollider(ref handles);
			return;
		}
		int num = -1;
		double num2 = 90000.0;
		double3 double5 = states.Origin.PositionBub + states.Kinematic.PositionPhys;
		for (int i = 0; i < list.Count; i++)
		{
			double num3 = (list[i].PositionCcf - double5).LengthSquared();
			if (!(num3 >= num2))
			{
				num2 = num3;
				num = i;
			}
		}
		if (num < 0)
		{
			RemoveStaticObjectCollider(ref handles);
			return;
		}
		LaunchPadPose launchPadPose = list[num];
		StaticObject staticObject = launchPadPose.StaticObject;
		double3 positionCcf = launchPadPose.PositionCcf;
		double3 upCcf = launchPadPose.UpCcf;
		double3 eastCcf = launchPadPose.EastCcf;
		double3 northCcf = launchPadPose.NorthCcf;
		StaticDescription description = new StaticDescription
		{
			Pose = new RigidPose
			{
				Position = (positionCcf + upCcf * staticObject.GroundOffset - states.Origin.PositionBub).ToBepu(),
				Orientation = doubleQuat.CreateFromRotationMatrix(new double4x4(upCcf.X, upCcf.Y, upCcf.Z, 0.0, eastCcf.X, eastCcf.Y, eastCcf.Z, 0.0, northCcf.X, northCcf.Y, northCcf.Z, 0.0, 0.0, 0.0, 0.0, 1.0)).ToBepu()
			},
			Shape = staticObject.CollisionShape
		};
		StaticHandle? staticObjectCollider = handles.StaticObjectCollider;
		if (!staticObjectCollider.HasValue)
		{
			handles.StaticObjectCollider = Simulation.Statics.Add(in description, ref StaticsShouldntAwakenBodies.Shared);
		}
		else
		{
			Simulation.Statics.ApplyDescription(handles.StaticObjectCollider.Value, in description, ref StaticsShouldntAwakenBodies.Shared);
		}
	}
```

Because `StaticObjectCollider` is `BepuHandles.IsGroundSurface`, the stock contact filter accepts it for the
body and kitten ground checks do too, which is why this needs no filter patch. `StaticObject.ResolveAll`
(the last act of `ModLibrary.AttachGameData`) builds one `StaticObject` per template, with render models and a Bepu
`BigCompound` of every collider, and calls `ResolveStaticObject` on every location of every celestial template.[^ksa-staticobject]

## Lifecycle and timing

| When | Hook | What runs |
|---|---|---|
| `startup:all-mods-loaded` | `StarMapAllModsLoaded` (postfix on `ModLibrary.LoadAll`) | Add the landmarks to the Earth template; templates are loaded but static objects are not resolved yet, so new landmarks are resolved like Core's |
| First simulated step | the mod's step | `StaticObject.Find` for any lookup; it only answers after `AttachGameData` |
| `physics:bepu` | engine | `UpdateStaticObjectCollider` picks the nearest pad per body; read-only for the mod |
| `frame:render` | postfix on `LocationReference.UpdateStaticObjectRenderData` | Draw what the engine culled |

Landmarks added at `StarMapAllModsLoaded` are resolved by the later `AttachGameData`. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Declare the building in XML: `StaticSubObject`s (a `PartModel` naming a mesh from the `MeshAtlas` and a
   `PbrMaterial`, plus a `Collider` of `Box`es) and a `StaticObject` that instances them as `SubObject`s with a
   `Transform`, and sets `GroundOffset`, `SurfaceHeight` and `FootprintRadius`. The asset frame is +X up, +Y east,
   +Z north (the axis order of `LocationReference.GetAxesCcf`):

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

   Both landmarks stand on undug ground 7.6 m south of the building, so every `SubObject` is moved back north by
   `Position Z="7.6"`.
2. At `StarMapAllModsLoaded`, get the planet's `CelestialTemplate` with `ModLibrary.TryGet`, build a
   `LandmarkReference` with `IsLaunchPad = true`, a `StaticObjectId`, and `Latitude`/`Longitude` as `RadianReference`s, call
   `OnDataLoad(Mod.Empty)` so `ForwardCcf` is computed, and append it **after** Core's landmarks. Nothing is written to Core's XML:

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

3. Use the 300 m nearest-pad rule as a feature. A second landmark 3 m away, with a collider-only static object,
   makes a doorway: past the plane halfway between them a kitten is nearer the second landmark and stands on its
   colliders alone (this is the base of the [moving platform](../build-a-moving-platform-kittens-can-ride/overview.md)).
4. Draw what the engine declines to draw. `UpdateStaticObjectRenderData` only draws when `Camera.IsVisibleTo`
   says the camera is above the tangent plane at the landmark, and `IsVisibleTo` samples terrain with
   `accurate: false`. The mod's postfix repeats the engine's matrix maths and draws the building itself when the
   engine declined.
5. Mind the order in the landmark list: craft placement by latitude and longitude adds the `GroundOffset +
   SurfaceHeight` of the first launch pad whose footprint holds the point, so appending after Core's keeps a launch
   from a Core pad standing on its own pad.

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml) | The building, sub-objects, colliders, lift objects |
| [src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L55-L107) | `Place`, `Landmark`: add the landmarks to Earth |

## Variations and gotchas

- Only the nearest launch pad within 300 m collides per body: a building bigger than that, or two buildings, cannot
  all be solid at once; use [approach 1 or 2](add-static-colliders-to-physics-bubbles-1.md) for that.
- Ground clutter clearing considers only the first four launch pads in the list and Earth has five Core pads ahead of
  the mod's; a separate patch is needed to clear clutter.
- The static object's `GroundOffset` is template state shared by every user of that static object.
- `StaticObject.Find` is only answered after `AttachGameData`, so look up lazily.

## See also

- [Overview and comparison](overview.md)
- [Build a moving platform or elevator kittens can ride](../build-a-moving-platform-kittens-can-ride/overview.md)
- Mod feature: [KSAStructures office landmarks static objects](../../../../mods/ksastructures/features/office-landmarks-static-objects.md)
- [Declare a part collider at the hull and move it](../scale-or-move-a-vessels-colliders-at-runtime/overview.md)

[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-staticobject]: KSA.StaticObject (decompiled, build 2026.10.10.5554)
