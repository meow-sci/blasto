---
type: How-To
title: Subclass ColliderTemplate with an owned convex hull
description: Convert a collider recipe into KSA's own ColliderTemplate subclasses, or into a private subclass for convex hulls built from any CPU mesh, so the native clutter physical data creates, scales and frees the shapes
category: physics
group: colliders
topic: give-ground-clutter-custom-colliders
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/pebbles-clutter-colliders]
techniques: [subclass-game-type, bepu-physics]
hooks: [harmony-prefix KSA.Universe.ExecuteNextClothSolvers]
lifecycle: [frame:dispatch-solvers, physics:bepu]
game_types:
  - KSA.ColliderTemplate
  - KSA.ClutterObjectTemplate
  - KSA.ClutterEcotypePhysicalData
  - BepuPhysics.Collidables.ConvexHull
  - KSA.ShapesUnlock
tags: [physics, bepu, clutter, colliders, collider-template, convex-hull, unscience, pebbles]
---

Outcome: replacement clutter is solid. The player fits boxes, spheres, capsules and cylinders around an imported rock or
tree, and after applying them vessels and kittens collide with every instance, knock it loose and send it tumbling
exactly like stock clutter. Prerequisites: StarMap, Harmony for the commit hook
([approach 2](give-ground-clutter-custom-colliders-2.md)), and the registry rules from
[the registry topic](../mutate-the-shared-bepu-shape-registry/overview.md). Pebbles never builds Bepu statics itself:
it makes KSA's own `ColliderTemplate` subclasses and lets the native `ClutterEcotypePhysicalData` constructor create,
scale, compound, install and later free the shapes.

## Background: how the game does it

The physical-data constructor calls `PopulateColliderPrimitives`, which for every object and each of 16 scale buckets
calls `CreateScaledShape(unlock, scale)` on each collider template. One collider becomes a primitive shape; several
become a kinematic `Compound` whose inertia uses the volume-weighted child masses:

```csharp
// current/decomp/KSA/ClutterEcotypePhysicalData.cs L283-L299
			for (int j = 0; j < num; j++)
			{
				double scale = GetScale((uint)j);
				int count = list.Count;
				if (colliders.Count == 1)
				{
					ColliderTemplate colliderTemplate = colliders[0];
					TypedIndex typedIndex = colliderTemplate.CreateScaledShape(in unlock, scale);
					_primitiveShapes.Add(typedIndex);
					list.Add(new ColliderPrimitiveData
					{
						Handle = typedIndex,
						LocalPosition = ColliderPosition(colliderTemplate, scale),
						LocalRotation = QuaternionEx.CreateFromXyzRadians(colliderTemplate.Collider2Asmb),
						UnitInertia = ComputeUnitInertia(shapes, typedIndex)
					});
				}
```

`ColliderPosition` places a collider at `(LocationAsmb + ShapeOffsetCollider rotated by Collider2Asmb) * scale`:

```csharp
// current/decomp/KSA/ClutterEcotypePhysicalData.cs L356-L364
	private static double3 ColliderPosition(ColliderTemplate collider, double scale)
	{
		double3 double5 = collider.ShapeOffsetCollider;
		if (double5 != double3.Zero)
		{
			double5 = double5.Transform(QuaternionEx.CreateFromXyzRadians(collider.Collider2Asmb));
		}
		return (collider.LocationAsmb.ToDouble3() + double5) * scale;
	}
```

At cell sync, `BubbleClutterStatics.AddCell` adds one Bepu static per primitive at the instance pose; on a strong
hit `TryDisplaceInstance` swaps the static for a dynamic body using the same shape, `UnitInertia` and
`MassKg * scale^3`.[^ksa-bubbleclutterstatics]

## Lifecycle and timing

| Step | Where | Thread and point |
|---|---|---|
| Edit and validate recipes | `ISubmod.RenderFloatingWindows` | Main thread, `frame:draw-ui`; no physics objects exist yet |
| `ClutterColliders.Build`, `OwnedHull` constructor, shape creation for the 16 buckets | Inside the `Universe.ExecuteNextClothSolvers` prefix, after `Quiesce()` | Main thread, `frame:dispatch-solvers`, solver jobs finished |
| Statics installed | `ExecuteNextVehicleSolvers` -> `SyncGroundClutter` -> `BubbleClutterStatics.Update`/`AddCell` | Main thread |
| Contacts, hits, displacement | `ConstraintSim` steps | Vehicle worker, `physics:bepu` |

Both the hull constructor and `PopulateColliderPrimitives` take `ConstraintSim.UnlockShapes()`, the non-blocking
variant, so building from UI code while the vehicle task runs throws. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Keep a recipe in your own model: kind (Box, Sphere, Capsule, Cylinder, ConvexHull), a position and XYZ rotation in
   degrees in the object's space, and full dimensions a user can read off a preview (box = full lengths, sphere X =
   diameter, capsule and cylinder X = diameter, Y = total height).
2. Map the recipe onto the stock templates' fields. A capsule's `LengthY` is the **inner segment**, so total
   height = segment + diameter; `Collider2Asmb` takes radians and the engine turns it into a quaternion with
   `QuaternionEx.CreateFromXyzRadians` (X, then Y, then Z), so build editor rotations in the same order:

```csharp
// pebbles.lib/Runtime/ClutterColliders.cs L13-L30
    public static ColliderTemplate Build(ColliderRecipe recipe, ClutterAssets assets, bool stockHull = false)
    {
        var d = recipe.Dimensions;
        ColliderTemplate result = recipe.Kind switch
        {
            ColliderKind.Box => new BoxColliderTemplate { LengthX = new(d.X), LengthY = new(d.Y), LengthZ = new(d.Z) },
            ColliderKind.Sphere => new SphereColliderTemplate { Radius = new(d.X / 2) },
            ColliderKind.Capsule => new CapsuleColliderTemplate { Radius = new(d.X / 2), LengthY = new(Math.Max(0, d.Y - d.X)) },
            ColliderKind.Cylinder => new CylinderColliderTemplate { Radius = new(d.X / 2), LengthY = new(d.Y) },
            ColliderKind.ConvexHull => new OwnedHull(assets.ResolveMesh(recipe.HullMeshId), recipe.HullScale, stockHull),
            _ => throw new NotSupportedException("Unknown collider kind.")
        };
        result.Id = recipe.Id;
        result.LocationAsmb = new Vector3Reference(new double3(recipe.Position.X, recipe.Position.Y, recipe.Position.Z));
        var rotation = recipe.RotationDegrees;
        result.Collider2Asmb = new Vector3Reference(new double3(rotation.X, rotation.Y, rotation.Z) * (Math.PI / 180));
        return result;
    }
```

3. For convex hulls, subclass `ColliderTemplate` directly. KSA's `ConvexHullColliderTemplate` derives from
   `MeshColliderTemplate` and needs a registered `MeshReference` loaded through XML; the private subclass takes any CPU
   mesh. Collect positions, scale, deduplicate (4 to 65,536 unique points), recentre on the bounds centre, build a
   temporary hull under `UnlockShapes()` to get the centroid and volume, then dispose it:

```csharp
// pebbles.lib/Runtime/ClutterColliders.cs L51-L95
    private sealed class OwnedHull : ColliderTemplate
    {
        private readonly Vector3[] _points; // Recentred on their bounds centre, as stock hulls are (KSA 5447).
        private readonly double3 _center;
        private readonly double _volume;
        public double Radius { get; }
        public override double3 ShapeOffsetCollider => _center;
        // KSA 5447 weights compound child masses and centre of mass by volume; the base getter returns 0.
        public override double VolumeCubicMetres => _volume;
        public OwnedHull(MeshReference mesh, Vec3 scale, bool stockHull)
        {
            var points = (stockHull ? mesh.HostPrimitives.Take(1) : mesh.HostPrimitives).SelectMany(p => p.GetVertexSpan<float3>(MeshAttribute.Position).ToArray())
                .Select(p => new Vector3(p.X * scale.X, p.Y * scale.Y, p.Z * scale.Z)).Distinct().ToArray();
            if (points.Length is < 4 or > 65536) throw new InvalidOperationException("Convex hulls need 4–65,536 unique points.");
            Radius = points.Max(p => (double)p.Length());
            var boundsCentre = HullMath.BoundsCentre(points);
            _points = points.Select(p => p - boundsCentre).ToArray();
            using var unlock = ConstraintSim.UnlockShapes();
            if (!ConvexHullHelper.CreateShape(_points, unlock.BufferPool, out var center, out var hull))
                throw new InvalidOperationException("Convex hull is degenerate; choose closed geometry with volume.");
            try
            {
                var centroid = center + boundsCentre;
                _center = new double3(centroid.X, centroid.Y, centroid.Z);
                _volume = Volume(in hull);
            }
            finally { hull.Dispose(unlock.BufferPool); }
        }
        private static double Volume(in ConvexHull hull)
        {
            var faces = new Vector3[hull.FaceToVertexIndicesStart.Length][];
            for (var f = 0; f < faces.Length; f++)
            {
                hull.GetVertexIndicesForFace(f, out var indices);
                faces[f] = new Vector3[indices.Length];
                for (var v = 0; v < indices.Length; v++) hull.GetPoint(indices[v], out faces[f][v]);
            }
            return HullMath.Volume(faces);
        }
        protected override void CreateShapeInto(in ShapesUnlock unlock, double scale, out TypedIndex handle)
        {
            var points = _points.Select(p => p * (float)scale).ToArray();
            if (!ConvexHullHelper.CreateShape(points, unlock.BufferPool, out _, out var hull)) throw new InvalidOperationException("Scaled convex hull is degenerate.");
            handle = unlock.Shapes.Add(in hull);
        }
```

   Override `ShapeOffsetCollider` and `VolumeCubicMetres`: the base class returns zero for both. Without them
   `PopulateUnitChildMasses` weights every child equally ("no collider volume" warning) and the hull sits at the wrong place.
4. Apply collision policy and compute reach while building the private reference graph. `KeepOriginal` rebuilds
   private copies of the stock colliders with `stockHull: true`; `Custom` builds one template per enabled recipe
   collider; `None` leaves an empty list. Throw before allocating anything if a collidable ecotype has no positive
   mass or non-uniform scale (native code only flags them):

```csharp
// pebbles.lib/Runtime/ClutterGraph.cs L73-L82
                if (item.Collision == CollisionPolicy.KeepOriginal) foreach (var c in source.Colliders) o.Colliders.Add(ClutterColliders.Build(ClutterCapture.Collider(c), assets, stockHull: true));
                else if (item.Collision == CollisionPolicy.Custom)
                    foreach (var c in item.Colliders.Where(c => c.Enabled)) o.Colliders.Add(ClutterColliders.Build(c, assets));
                if (e.Collideable && o.Colliders.Count > 0 && item.MassKg <= 0) throw new InvalidOperationException("Objects with installed colliders require positive mass.");
                foreach (var c in o.Colliders) PhysicalObjectRadii[ei][oi] = Math.Max(PhysicalObjectRadii[ei][oi], (float)ClutterColliders.Reach(c));
                PhysicalRadii[ei] = Math.Max(PhysicalRadii[ei], PhysicalObjectRadii[ei][oi] * Math.Max(r.Placement.MaxScale.X, r.Placement.MinScale.X));
                e.ClutterObjects.Add(o);
            }
            if (e.Collideable && (r.Placement.MinScale.X != r.Placement.MinScale.Y || r.Placement.MinScale.Y != r.Placement.MinScale.Z || r.Placement.MaxScale.X != r.Placement.MaxScale.Y || r.Placement.MaxScale.Y != r.Placement.MaxScale.Z))
                throw new InvalidOperationException("Collidable ecotypes require uniform XYZ scale.");
```

5. Widen the physics bounding radius to the colliders' real reach (centre distance plus the shape's bounding radius):
   `ClutterResources.Build` passes `max(visual radius, collider reach)` into the physical-data constructor, which sizes its
   vessel cell grid with it, so a tiny replacement mesh with large colliders still loads collision cells early.
6. Switch the ecotype to `PrimitiveList` collision when any enabled custom collider exists, demote
   `SurfaceNormalSmooth` orientation to `SurfaceNormal` (native collision rejects smooth), and force placement
   `MinScale = MaxScale = 1`. Only `.X` of the placement scale is used by `GetScale`, hence uniform XYZ.
7. Release through the native owner: `ClutterEcotypePhysicalData.Dispose()` removes the compound and primitive shapes
   from the registry. On a failed build, read the two private lists by reflection and free what was registered.

## Complete example

| File | Role |
|---|---|
| [pebbles.lib/Models/PebblesRecipe.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/PebblesRecipe.cs#L25-L43) | `ColliderRecipe` schema |
| [pebbles.lib/Runtime/ClutterColliders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterColliders.cs#L13-L95) | Recipe to native template, `OwnedHull` |
| [pebbles.lib/Runtime/ClutterGraph.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterGraph.cs#L21-L86) | Policy, mass check, reach, private graph |
| [pebbles.lib/Runtime/ClutterResources.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterResources.cs#L72-L96) | Physical data with widened radii |
| [pebbles.lib/Runtime/ClutterRetirement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterRetirement.cs#L59-L71) | Failure-path shape release |

## Variations and gotchas

- Not a byte-for-byte port of build 5554: stock hulls build one base hull under `UnlockShapesBlocking` and serve scales with
  `ConvexHullHelper.CreateTransformedCopy`; `OwnedHull` re-runs `CreateShape` on scaled points for each of the 16 buckets, so a
  65,536-point hull costs 16 full hull builds per collider.
- Rebuilt stock hulls use the first mesh primitive only (stock rule); custom hulls combine all primitives.
- Capturing a stock capsule adds `2 * Radius` back to the recipe's height; mixing conventions gives a diameter too tall or short.
- With ground clutter collisions off in settings, no statics are installed and authored colliders are inert.
- Every member used exists unchanged in build 2026.10.10.5554.

## See also

- [Overview and comparison](overview.md)
- [Swap clutter bundles in a cloth-solver prefix](give-ground-clutter-custom-colliders-2.md)
- Mod feature: [Pebbles clutter colliders](../../../../mods/unscience/features/pebbles-clutter-colliders.md)
- [Create and free shapes in the registry](../mutate-the-shared-bepu-shape-registry/mutate-the-shared-bepu-shape-registry-2.md)

[^ksa-bubbleclutterstatics]: KSA.BubbleClutterStatics (decompiled, build 2026.10.10.5554)
