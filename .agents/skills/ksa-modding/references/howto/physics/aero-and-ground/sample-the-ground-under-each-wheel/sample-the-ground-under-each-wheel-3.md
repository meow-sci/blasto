---
type: How-To
title: Rebuild box and cylinder colliders from template data and drop a ray per wheel
description: Pose a launch pad's box and cylinder colliders from static-object template data exactly as ConstraintSim does, without touching the shape registry lock, and drop a line from each wheel hub onto them to get the hub height over the pad
category: physics
group: aero-and-ground
topic: sample-the-ground-under-each-wheel
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksacars/features/launch-pad-surface-contact]
techniques: [harmony-prefix, custom-physics, ray-cast]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types:
  - KSA.LocationReference
  - KSA.LandmarkReference
  - KSA.StaticObject
  - KSA.StaticObjectTemplate
  - KSA.ColliderTemplate
  - KSA.ConstraintSim
  - KSA.CelestialTemplate
tags: [physics, terrain, launch-pad, static-object, colliders, wheels, raycast]
---

Outcome: for each wheel hub, the height above the launch pad deck beneath it (or "no pad here"), computed on plain data with no
Bepu registry access, so it can be asked per wheel per frame from the same `Vehicle.PrepareWorker` prefix that samples the terrain
([approach 1](sample-the-ground-under-each-wheel-1.md)). Prerequisites: StarMap and Harmony. Demonstrated by KSACars: a car
parked on a pad would otherwise sink into the deck, because the terrain height field answers the levelled ground the pad is built
on (KSACars' notes measure the grate about 1.75 m above it).[^ksacars-claude]

## Background: how the game does it

A launch pad is a `LandmarkReference` with `IsLaunchPad`, whose static object carries the colliders. The engine poses the collider
for each physics bubble in `ConstraintSim.UpdateStaticObjectCollider`: the nearest pad within 300 m of the vehicle (squared range
90000) gets a Bepu static, positioned on the terrain at the landmark, lifted by `GroundOffset`, with up, east and north as the
orientation basis:[^ksa-constraintsim]

```csharp
// current/decomp/KSA/ConstraintSim.cs L593
		double num2 = 90000.0;
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

The same data is available without the registry: `StaticObject.Template.Colliders` and `StaticObjectTemplate.SubObjectInstances`
(each with its own `GetTemplate().Colliders` and a `Transform`), and each `ColliderTemplate` carries `LocationAsmb`,
`Collider2Asmb` and a virtual `ShapeOffsetCollider`.[^ksa-staticobject][^ksa-staticobjecttemplate][^ksa-collidertemplate] The
pad frame is `LocationReference.GetAxesCcf`, which sets `upCcf = ForwardCcf`.[^ksa-locationreference] The engine's own
placement of a vehicle by latitude and longitude, by contrast, adds `GroundOffset + SurfaceHeight` inside the pad's
`FootprintRadius` ([`Vehicle.GetLaunchPadHeightAtDirCcf`](../../kinematics/place-a-vessel-on-a-surface-at-latitude-and-longitude/overview.md)),
which is a footprint, not a surface.[^ksa-vehicle]

## Lifecycle and timing

- **Where: the `Vehicle.PrepareWorker` prefix, `frame:dispatch-solvers`, main thread.** The pad cache is filled lazily the first
  time a car on that body is stepped, and queries run per wheel every frame in the same prefix.[^ksacars-buggies] Everything read
  is immutable template data plus the body's terrain, so the main-thread prefix is a safe place; nothing touches Bepu.
- **Why not query the engine's static.** It lives in each bubble's `Simulation`, which the worker owns for the whole step; the mod
  does not query that simulation at all and reimplements the drop on plain data. Compare approach 2, which copies the shapes at the
  sync point instead. See the
  [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-physics-step).

## Steps

1. **Read the pads once per body and cache them.** Walk `BodyTemplate.Locations` for `LandmarkReference { IsLaunchPad: true }`,
   take `GetStaticObject()`, collect solids from the pad's own colliders and from each sub-object (placed by its `Transform`),
   and pose the pad in the body-fixed frame. A body whose pads cannot be read has none, with a warning:

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

   Expect `Earth: N launch pad(s) with colliders` once per body in the log.[^ksacars-launchpads]

2. **Turn each collider into a solid in the structure's frame.** Only `BoxColliderTemplate` (half lengths from `LengthX/Y/Z`) and
   `CylinderColliderTemplate` (radius, half `LengthY`, along its own Y) are used; every other shape is skipped. A collider's
   centre is `LocationAsmb + ShapeOffsetCollider` rotated by `Collider2Asmb` (XYZ radians), the same composition
   `ColliderTemplate` uses:

```csharp
// src/KSACars/Ksa/LaunchPads.cs L100-L133
    private static void AddSolids(List<PadSolid> solids, List<ColliderModule.Template> colliders,
                                  double3 positionAsmb, doubleQuat sub2Asmb)
    {
        foreach (ColliderModule.Template group in colliders)
        {
            foreach (ColliderTemplate collider in group.Colliders)
            {
                double3 half;
                bool round;
                switch (collider)
                {
                    case BoxColliderTemplate box:
                        half = new double3((double)box.LengthX, (double)box.LengthY, (double)box.LengthZ) * 0.5;
                        round = false;
                        break;
                    case CylinderColliderTemplate cylinder:
                        half = new double3((double)cylinder.Radius, (double)cylinder.LengthY * 0.5, 0.0);
                        round = true;
                        break;
                    default:
                        continue;
                }

                doubleQuat collider2Sub = QuaternionEx.CreateFromXyzRadians(collider.Collider2Asmb.ToDouble3());
                double3 centre = collider.LocationAsmb.ToDouble3() + collider.ShapeOffsetCollider.Transform(collider2Sub);
                solids.Add(new PadSolid(
                    positionAsmb + centre.Transform(sub2Asmb),
                    Axis(new double3(1, 0, 0)), Axis(new double3(0, 1, 0)), Axis(new double3(0, 0, 1)),
                    half, round));

                double3 Axis(double3 own) => own.Transform(collider2Sub).Transform(sub2Asmb);
            }
        }
    }
```

3. **Drop a line onto the solids.** `PadSurface.TryDrop` is a slab test against each oriented box and a slab-plus-disc test
   against each cylinder, and it returns the nearest entry distance along the line:[^ksacars-padsurface]

```csharp
// src/KSACars/Sim/PadSurface.cs L29-L53
    public bool TryDrop(double3 from, double3 along, double reach, out double metres)
    {
        metres = reach;
        bool met = false;

        foreach (PadSolid solid in _solids)
        {
            double3 offset = from - solid.Centre;
            double3 o = new(Vec.Dot(offset, solid.AxisX), Vec.Dot(offset, solid.AxisY), Vec.Dot(offset, solid.AxisZ));
            double3 d = new(Vec.Dot(along, solid.AxisX), Vec.Dot(along, solid.AxisY), Vec.Dot(along, solid.AxisZ));

            double enter = 0.0, leave = metres;
            bool inside = solid.Round
                ? Slab(o.Y, d.Y, solid.Half.Y, ref enter, ref leave) && Disc(o, d, solid.Half.X, ref enter, ref leave)
                : Slab(o.X, d.X, solid.Half.X, ref enter, ref leave)
                  && Slab(o.Y, d.Y, solid.Half.Y, ref enter, ref leave)
                  && Slab(o.Z, d.Z, solid.Half.Z, ref enter, ref leave);
            if (!inside) continue;

            metres = enter;
            met = true;
        }

        return met;
    }
```

4. **Query per hub with the engine's range.** Skip pads further than 300 m (the squared `90000` range the engine uses), convert
   the hub into the pad frame (+X up, +Y east, +Z north), start 0.5 m above it and drop straight down (`-X`) up to 30 m; the hub
   height over the pad is the distance minus the 0.5 m step, so a surface up to half a metre above the hub still counts as
   under the wheel and anything higher is a roof:

```csharp
// src/KSACars/Ksa/LaunchPads.cs L52-L70
    /// <summary>How far a point is above the pad under it (m); negative when it is inside one.</summary>
    public bool TryHeightOver(double3 atCcf, out double metres)
    {
        metres = 0.0;

        foreach (Pad pad in _all)
        {
            double3 offset = atCcf - pad.OriginCcf;
            if (Vec.Dot(offset, offset) > ColliderRangeMetres * ColliderRangeMetres) continue;

            double3 from = new(Vec.Dot(offset, pad.UpCcf) + StepMetres, Vec.Dot(offset, pad.EastCcf), Vec.Dot(offset, pad.NorthCcf));
            if (!pad.Surface.TryDrop(from, new double3(-1, 0, 0), ReachMetres, out double drop)) continue;

            metres = drop - StepMetres;
            return true;
        }

        return false;
    }
```

5. **Take the lesser of terrain and pad.** In the wheel loop, `hubHeight = Math.Min(terrainHubHeight, padHubHeight)` (the line is
   shown in [approach 1](sample-the-ground-under-each-wheel-1.md), step 4):

```csharp
// src/KSACars/Ksa/Buggies.cs L657-L658
            double hubHeight = radius - (body.MeanRadius + height);
            if (pads.TryHeightOver(atCcf, out double overPad)) hubHeight = Math.Min(hubHeight, overPad);
```

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/LaunchPads.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/LaunchPads.cs) | Reads pads and their colliders, poses them like the engine, answers `TryHeightOver` |
| [src/KSACars/Sim/PadSurface.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/PadSurface.cs) | KSA-free ray drop against oriented boxes and Y-axis cylinders |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L657-L658) | Uses the lesser of terrain and pad height |

## Variations and gotchas

- **The engine poses pads from `landmark.ForwardCcf`**, the mod from the `up` axis of `GetAxesCcf`; `GetAxesCcf` sets
  `upCcf = ForwardCcf`, so the two agree by construction in this build.
- **Only boxes and cylinders are read.** Capsule, sphere and convex-hull pad colliders are ignored, so a future pad built from
  a convex hull would vanish for the wheels; approach 2 handles every convex type.
- **No drawn ground.** Unlike approach 2, nothing here covers road and grass that run past the collider.
- **The cache never refreshes.** Pads are read once per `Celestial` per session; a pad added later stays unseen.
- **Terrain-only teleport placement is a different path.** `Vehicle.TeleportToLocation` stands a craft on a pad through
  `GroundOffset + SurfaceHeight` within `FootprintRadius`; wheels use the colliders instead, so they also find ramps and edges.
- **Template-derived poses can diverge if the engine changes its pose.** Re-check `ConstraintSim.UpdateStaticObjectCollider`
  after a game update.

## See also

- [Overview and comparison](overview.md)
- [GetTerrainHeightFromDirCce with accurate true in the PrepareWorker prefix](sample-the-ground-under-each-wheel-1.md)
- [Copy convex children under the shapes lock in an ExecuteNextVehicleSolvers prefix](sample-the-ground-under-each-wheel-2.md)
- Mod feature: [Launch pad surface contact](../../../../mods/ksacars/features/launch-pad-surface-contact.md)
- [Add mod-owned static colliders that vessels and kittens collide with](../../colliders/add-static-colliders-to-physics-bubbles/overview.md)

[^ksacars-claude]: KSACars: CLAUDE.md
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-staticobject]: KSA.StaticObject (decompiled, build 2026.10.10.5554)
[^ksa-staticobjecttemplate]: KSA.StaticObjectTemplate (decompiled, build 2026.10.10.5554)
[^ksa-collidertemplate]: KSA.ColliderTemplate (decompiled, build 2026.10.10.5554)
[^ksa-locationreference]: KSA.LocationReference (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksacars-buggies]: KSACars: src/KSACars/Ksa/Buggies.cs
[^ksacars-launchpads]: KSACars: src/KSACars/Ksa/LaunchPads.cs
[^ksacars-padsurface]: KSACars: src/KSACars/Sim/PadSurface.cs
