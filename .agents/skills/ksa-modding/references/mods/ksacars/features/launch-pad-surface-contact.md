---
type: Mod Feature
title: Launch pad surface contact
description: Rebuild a body's launch-pad colliders from their StaticObjectTemplate data, posed the way ConstraintSim poses them, and drop a ray from each wheel hub onto those boxes and cylinders so a car drives on the pad deck
mod: ksacars
category: celestials
techniques: [custom-physics, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types:
  - KSA.Celestial
  - KSA.CelestialTemplate
  - KSA.LandmarkReference
  - KSA.LocationReference
  - KSA.StaticObject
  - KSA.StaticObjectTemplate
  - KSA.StaticSubObjectInstance
  - KSA.ColliderModule
  - KSA.BoxColliderTemplate
  - KSA.CylinderColliderTemplate
  - KSA.ConstraintSim
tags: [launch-pad, static-objects, colliders, raycast, terrain, wheels]
---

The terrain height field knows nothing about launch pads: a pad stands on a levelled disc of
ground, and the grate a craft is launched from is about 1.75 m above it. A car whose springs read
only the height field would sink into the pad deck.[^ksacars-claude] KSACars rebuilds each pad's
colliders from the game's static-object data, poses them exactly as the engine poses the pad's
Bepu static, and drops a line from every wheel hub onto them; the wheel uses whichever is higher,
the pad or the terrain.[^ksacars-launchpads]

## How it works

1. `LaunchPads.On(body)` reads a body's pads once and caches them per `Celestial`. It walks
   `body.BodyTemplate.Locations` for `LandmarkReference { IsLaunchPad: true }` and asks each for
   `GetStaticObject()`.
2. For every pad it collects solids from `StaticObject.Template.Colliders` and from each
   `StaticSubObjectInstance.GetTemplate().Colliders`, placed by the sub-object's `Transform`.
   Only `BoxColliderTemplate` (half lengths from `LengthX/Y/Z`) and `CylinderColliderTemplate`
   (radius, half `LengthY`, along its own Y) are used; other shapes are skipped. Each collider's
   centre is `LocationAsmb + ShapeOffsetCollider` rotated by `Collider2Asmb` (XYZ radians), the same
   composition `ColliderTemplate` uses.[^ksa-collidertemplate]
3. The pad frame is `landmark.GetAxesCcf(out up, out east, out north)` with the origin at
   `up * (MeanRadius + GetTerrainHeightFromDirCcf(up) + pad.GroundOffset)`: +X up, +Y east, +Z
   north, matching `ConstraintSim.UpdateStaticObjectCollider`.[^ksa-constraintsim][^ksa-locationreference]
4. Each frame, for each hub, `TryHeightOver(atCcf)` skips pads further than 300 m (the same
   `90000.0` squared range the engine uses to choose the pad collider), converts the hub into the
   pad frame, starts 0.5 m above it and casts straight down (`-X`) up to 30 m with
   `PadSurface.TryDrop`.[^ksacars-launchpads]
5. `PadSurface.TryDrop` is a slab test against each oriented box and a slab-plus-disc test against
   each cylinder, returning the nearest entry distance.[^ksacars-padsurface] The hub height over
   the pad is that distance minus the 0.5 m step, so a surface up to half a metre above a hub still
   counts as under the wheel; anything higher is a roof.
6. `Buggies.Step` takes `Math.Min(terrainHubHeight, padHubHeight)`.[^ksacars-buggies]

## Lifecycle and timing

The pad cache is filled lazily the first time a car on that body is stepped, inside the
`Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`, main thread), and logged once per body.
Queries run in the same prefix, per wheel, every frame. Everything they read is immutable template
data plus the body's terrain, so the main-thread prefix is a safe place for them; nothing touches
Bepu. The engine's own pad collider is a Bepu static that `ConstraintSim` adds to each physics
bubble's simulation (`UpdateStaticObjectCollider`, called from its terrain update); the mod does not
query that simulation at all and reimplements the drop on plain data instead.

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/LaunchPads.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/LaunchPads.cs) | Reads pads and their colliders, poses them like the engine, answers `TryHeightOver` |
| [src/KSACars/Sim/PadSurface.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/PadSurface.cs) | KSA-free ray drop against oriented boxes and Y-axis cylinders |
| [tests/KSACars.Tests/PadSurfaceTests.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tests/KSACars.Tests/PadSurfaceTests.cs) | Headless tests of the drop[^ksacars-padsurface-tests] |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L657-L658) | Uses the lesser of terrain and pad height |

## Snippets

```csharp
// src/KSACars/Ksa/LaunchPads.cs L72-L98
    private static LaunchPads Read(Celestial body)
    {
        List<Pad> pads = [];
        if (body.BodyTemplate is not { } template) return None;

        foreach (LocationReference location in template.Locations)
        {
            if (location is not LandmarkReference { IsLaunchPad: true } landmark) continue;
            if (landmark.GetStaticObject() is not { } pad) continue;

            List<PadSolid> solids = [];
            AddSolids(solids, pad.Template.Colliders, double3.Zero, doubleQuat.Identity);
            foreach (StaticSubObjectInstance instance in pad.Template.SubObjectInstances)
            {
                AddSolids(solids, instance.GetTemplate().Colliders,
                          instance.Transform?.PositionValue ?? double3.Zero,
                          instance.Transform?.RotationValue ?? doubleQuat.Identity);
            }
            if (solids.Count == 0) continue;

            landmark.GetAxesCcf(out double3 up, out double3 east, out double3 north);
            double3 origin = up * (body.MeanRadius + body.GetTerrainHeightFromDirCcf(up) + pad.GroundOffset);
            pads.Add(new Pad(origin, up, east, north, new PadSurface([.. solids])));
        }

        return pads.Count == 0 ? None : new LaunchPads([.. pads]);
    }
```

```csharp
// current/decomp/KSA/ConstraintSim.cs L609-L623
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
```

## Game APIs touched

| API | Purpose |
|---|---|
| `Celestial.BodyTemplate.Locations` | Every location on the body, including landmarks |
| `LandmarkReference.IsLaunchPad`, `LocationReference.GetStaticObject`, `GetAxesCcf` ([current/decomp/KSA/LocationReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LocationReference.cs#L117-L160)) | Which landmarks are pads, their static object and up/east/north axes |
| `StaticObject.Template`, `GroundOffset` ([current/decomp/KSA/StaticObject.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObject.cs#L23-L35)) | The pad's template and height above the terrain[^ksa-staticobject] |
| `StaticObjectTemplate.Colliders`, `SubObjectInstances` ([current/decomp/KSA/StaticObjectTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectTemplate.cs#L10-L16)) | Collider groups of the pad and its sub-objects[^ksa-staticobjecttemplate] |
| `StaticSubObjectInstance.GetTemplate()`, `.Transform` | Sub-object placement |
| `ColliderTemplate.LocationAsmb`, `Collider2Asmb`, `ShapeOffsetCollider` ([current/decomp/KSA/ColliderTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ColliderTemplate.cs#L13-L21)) | Collider placement inside its template |
| `BoxColliderTemplate`, `CylinderColliderTemplate` | Shape sizes |
| `ConstraintSim.ResolveLaunchPads` / `UpdateStaticObjectCollider` ([current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L550-L628)) | The engine's own pose and 300 m pad selection, mirrored by the mod |
| `Celestial.GetTerrainHeightFromDirCcf` | Terrain height at the landmark |

## Pitfalls and version notes

- **The engine poses pads from `landmark.ForwardCcf`**, the mod from the `up` axis returned by
  `GetAxesCcf`; `GetAxesCcf` sets `upCcf = ForwardCcf`, so the two agree by construction in this
  build.[^ksa-constraintsim][^ksa-locationreference]
- **Only boxes and cylinders are read.** Capsule, sphere and convex-hull pad colliders (all valid
  `ColliderModule` shapes) are ignored, so a future pad built from a convex hull would vanish for
  the wheels.
- **The cache never refreshes.** Pads are read once per `Celestial` per session; a pad added later
  (or a body whose pads failed to read, which logs a warning) stays as first read.
- `Vehicle.TeleportToLocation` stands a craft on a pad through a private `GetLaunchPadHeightAtDirCcf`
  using declared `GroundOffset + SurfaceHeight` within `FootprintRadius`; the mod's notes describe
  that path. The car's wheels use the colliders instead, so they also find ramps and edges.[^ksacars-terrain-doc]

## See also

- [Terrain contact under each wheel](./terrain-contact-under-wheels.md)
- [Wheel, suspension and tyre model](./wheel-suspension-tyre-model.md)
- [Headless simulation tests](./headless-sim-tests.md)
- [Rebuild box and cylinder colliders from template data and drop a ray per wheel](../../../howto/physics/aero-and-ground/sample-the-ground-under-each-wheel/sample-the-ground-under-each-wheel-3.md) - how-to article demonstrating this feature

[^ksacars-launchpads]: src/KSACars/Ksa/LaunchPads.cs
[^ksacars-padsurface]: src/KSACars/Sim/PadSurface.cs
[^ksacars-padsurface-tests]: tests/KSACars.Tests/PadSurfaceTests.cs
[^ksacars-buggies]: src/KSACars/Ksa/Buggies.cs
[^ksacars-claude]: CLAUDE.md
[^ksacars-terrain-doc]: docs/KSA-TERRAIN.md
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled)
[^ksa-locationreference]: KSA.LocationReference (decompiled)
[^ksa-staticobject]: KSA.StaticObject (decompiled)
[^ksa-staticobjecttemplate]: KSA.StaticObjectTemplate (decompiled)
[^ksa-collidertemplate]: KSA.ColliderTemplate (decompiled)
