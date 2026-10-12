---
type: Mod Feature
title: Authored clutter colliders mapped onto native collider templates with a private convex-hull subclass
description: Pebbles maps box, sphere, capsule, cylinder and hull collider recipes onto KSA ColliderTemplate types, including a private OwnedHull with stock-matching centroid and volume, so native clutter physics builds, scales and frees the Bepu shapes
mod: unscience
submod: pebbles
category: physics
techniques: [bepu-physics, harmony-prefix, reflection, subclass-game-type]
hooks:
  - harmony-prefix KSA.Universe.ExecuteNextClothSolvers (colliders are built inside the Pebbles clutter commit)
  - subclass KSA.ColliderTemplate (private OwnedHull overrides CreateShapeInto, ShapeOffsetCollider and VolumeCubicMetres)
  - reflection KSA.ClutterEcotypePhysicalData._primitiveShapes and _compoundShapes (shape cleanup after a failed build)
lifecycle: [frame:dispatch-solvers, physics:vehicle-solver, physics:bepu]
game_types:
  - KSA.ColliderTemplate
  - KSA.BoxColliderTemplate
  - KSA.SphereColliderTemplate
  - KSA.CapsuleColliderTemplate
  - KSA.CylinderColliderTemplate
  - KSA.ConvexHullColliderTemplate
  - KSA.MeshColliderTemplate
  - KSA.ClutterObjectTemplate
  - KSA.ClutterEcotypeReference
  - KSA.ClutterEcotypePhysicalData
  - KSA.BubbleClutterStatics
  - KSA.PhysicsBubble
  - KSA.ConstraintSim
  - KSA.ShapesUnlock
  - KSA.MeshReference
  - KSA.QuaternionEx
  - BepuPhysics.Collidables.ConvexHull
  - BepuPhysics.Collidables.ConvexHullHelper
tags: [ground-clutter, colliders, bepu, convex-hull, compound, mass, scaling, physics]
---

Replacement clutter in Pebbles can be solid: the player fits boxes, spheres, capsules and cylinders
around an imported rock or tree in the [collider workshop](./pebbles-workshop-editor.md), and after
**Apply to planet** vessels and kittens collide with every instance, knock it loose and send it
tumbling exactly like stock clutter. Clutter types that are not replaced keep their stock colliders,
including the multi-hull compounds of stock rocks, with stock mass distribution.[^scope-ground-clutter]

Mechanism in one line: Pebbles never builds Bepu statics itself. It converts each detached
`ColliderRecipe` into one of KSA's own `ColliderTemplate` subclasses (or a private `OwnedHull`
subclass for convex hulls), puts those into a private `ClutterObjectTemplate`, and lets the native
`ClutterEcotypePhysicalData` constructor create, scale, compound, install and later free the Bepu
shapes, while Pebbles widens the physics bounding radii to the colliders' real reach.[^clutter-colliders]

## How it works

**1. Recipe schema** ([pebbles.lib/Models/PebblesRecipe.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/PebblesRecipe.cs#L25-L43)).
A `ColliderRecipe` holds an id, a `ColliderKind` (Box, Sphere, Capsule, Cylinder, ConvexHull),
enabled and visible flags, a position and XYZ rotation in degrees in the object's already-transformed
space, `Dimensions` and, for hulls, a `HullMeshId` plus `HullScale`. Dimensions are always "full"
sizes a user can read off the preview: box = full XYZ lengths, sphere X = diameter, capsule and
cylinder X = diameter, Y = total height. Each `ObjectRecipe` carries a `CollisionPolicy`:
`KeepOriginal` (stock colliders), `None` or `Custom`.[^pebbles-recipe]

**2. Recipe to native template** ([pebbles.lib/Runtime/ClutterColliders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterColliders.cs#L13-L30)).
`Build` maps the full sizes onto the stock templates' fields:

| Kind | Native template | Field mapping | Bepu shape created by the template |
|---|---|---|---|
| Box | `BoxColliderTemplate` | `LengthX/Y/Z` = full lengths | `Box(LengthX*s, LengthY*s, LengthZ*s)` |
| Sphere | `SphereColliderTemplate` | `Radius` = X / 2 | `Sphere(Radius*s)` |
| Capsule | `CapsuleColliderTemplate` | `Radius` = X / 2, `LengthY` = max(0, Y - X) | `Capsule(Radius*s, LengthY*s)`; Bepu's length is the inner segment, so total height = segment + diameter |
| Cylinder | `CylinderColliderTemplate` | `Radius` = X / 2, `LengthY` = Y | `Cylinder(Radius*s, LengthY*s)` |
| ConvexHull | private `OwnedHull : ColliderTemplate` | points from the hull mesh x `HullScale` | new `ConvexHull` from the scaled points |

`LocationAsmb` gets the position and `Collider2Asmb` the rotation converted to radians; the engine
turns that into a quaternion with `QuaternionEx.CreateFromXyzRadians` (X first, then Y, then Z), the
same order the workshop's `WorkshopMath.Rotation` builds from `Rx * Ry * Rz` row-vector matrices, so
editor and physics agree.[^ksa-box-collider-template][^ksa-sphere-collider-template][^ksa-capsule-collider-template][^ksa-cylinder-collider-template][^ksa-quaternion-ex]
`ClutterCapture.Collider` is the inverse mapping (stock template to recipe), used to capture stock
objects and to rebuild their colliders
([pebbles.lib/Runtime/ClutterCapture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterCapture.cs#L77-L90)).

**3. The private hull (`OwnedHull`).** KSA's own `ConvexHullColliderTemplate` derives from
`MeshColliderTemplate` and needs a registered `MeshReference` loaded through XML. Pebbles instead
subclasses the abstract `ColliderTemplate` directly and reproduces the stock hull's observable
behavior from any CPU mesh, including imported GLBs:[^ksa-collider-template][^ksa-convex-hull-collider-template]

- Collect positions from every primitive of the hull mesh (or only the first primitive when rebuilding
  a stock hull, because stock reads `Mesh.HostMesh`, which is `HostPrimitives[0]`), multiply by
  `HullScale`, deduplicate, and require 4 to 65,536 unique points.[^ksa-mesh-collider-template][^ksa-mesh-reference]
- Recentre the points on their axis-aligned bounds centre (`HullMath.BoundsCentre`) for float
  precision, exactly as stock `EnsureBaseHull` does.
- Build a temporary Bepu hull with `ConvexHullHelper.CreateShape` under `ConstraintSim.UnlockShapes()`;
  record `ShapeOffsetCollider` = hull centroid + bounds centre and `VolumeCubicMetres` = the face-fan
  volume (`HullMath.Volume`, a port of stock `ComputeHullVolume`), then dispose the temporary hull.
- `CreateShapeInto(unlock, scale, out handle)` builds a fresh hull from the points times `scale` and
  adds it to the global shape registry.[^hull-math][^bepu-convex-hull-helper][^bepu-convex-hull]

The two overrides matter because the base class returns zero for both. `PopulateUnitChildMasses`
weights the children of a multi-collider compound by `VolumeCubicMetres` (all zero means equal masses
and a "no collider volume" warning), and `ColliderPosition` adds the rotated `ShapeOffsetCollider` to
`LocationAsmb` so the hull sits where its points were. The authors added both for KSA 5447, when stock
rock compounds of 2 to 27 hulls started getting volume-weighted mass and centre of mass.[^ksa-clutter-physical-data][^scope-ground-clutter]

**4. Policy, mass and reach** ([pebbles.lib/Runtime/ClutterGraph.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterGraph.cs#L73-L82)).
While building the private reference graph, each private `ClutterObjectTemplate` receives:

- `KeepOriginal`: private copies of the stock colliders, re-derived from the live stock templates with
  `stockHull: true` (stock templates are never shared or edited).
- `Custom`: one template per enabled recipe collider. `None`: an empty list (the native constructor
  logs a warning and the object simply has no statics).[^pebbles-source-map]
- A positive-mass check for collidable ecotypes, and a uniform-XYZ placement-scale check. KSA's own
  `ClutterEcotypeReference.IsValid` and `Resolve` only flag these (an error log and `false`, or a
  warning); Pebbles throws before allocating anything.[^ksa-clutter-ecotype-reference]
- `ClutterColliders.Reach` per collider: centre distance plus the shape's bounding radius (box half
  diagonal, sphere radius, capsule radius + half segment, cylinder half diagonal, hull max point
  distance). The per-object maximum, times the larger placement scale, becomes the ecotype's physical
  radius.

`ClutterResources.Build` then passes `max(visual radius, collider reach)` both per object and per
ecotype into the `ClutterEcotypePhysicalData` constructor, which sizes its vessel cell grid with that
radius. A tiny replacement mesh with large retained colliders therefore still loads collision cells
early enough
([pebbles.lib/Runtime/ClutterResources.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterResources.cs#L72-L96)).[^clutter-resources]
The shadow-culling radius stays the visual one.

**5. What the engine does with the templates.** The physical-data constructor calls
`PopulateColliderPrimitives`, which for every object and each of 16 scale buckets
(`GetScale(j)` = lerp of `MinScale.X` to `MaxScale.X`) calls `CreateScaledShape(unlock, scale)`.
One collider becomes a primitive shape; several become a kinematic `Compound` whose inertia uses the
volume-weighted child masses. At cell sync, `BubbleClutterStatics.AddCell` adds one Bepu static per
`ColliderPrimitiveData` at the instance pose; on a strong hit `TryDisplaceInstance` swaps the static for
a dynamic body using the same shape, `UnitInertia` and `MassKg * scale^3`.[^ksa-clutter-physical-data][^ksa-bubble-clutter-statics][^ksa-clutter-object-template]

**6. Authoring-side scale and switches.** `ClutterAuthoring.SetScale` multiplies collider offsets
(about the mesh origin), dimensions and hull scale by the ratio of new to old uniform mesh scale, so
"scale resizes mesh and colliders together". `Replace` copies the colliders into every variant of the
checked types, forces placement `MinScale = MaxScale = 1`, sets `MassKg` to at least 1, and switches
the ecotype to `PrimitiveList` collision when any enabled custom collider exists, demoting
`SurfaceNormalSmooth` orientation to `SurfaceNormal`, which native collision rejects
([pebbles.lib/Models/ClutterAuthoring.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/ClutterAuthoring.cs#L21-L66)).[^clutter-authoring]
`RecipeValidation.Object` rejects duplicate collider ids, non-positive or non-finite dimensions and
hull scales, capsules shorter than their diameter and hulls without a mesh
([pebbles.lib/Models/RecipeValidation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/RecipeValidation.cs#L60-L67)).[^recipe-validation]

**7. Release.** The registered shapes belong to the native physical data: Pebbles retires a bundle by
calling `ClutterEcotypePhysicalData.Dispose()`, which removes `_compoundShapes` recursively and
`_primitiveShapes` from the registry. If a build throws half way, `ClutterRetirement.RemoveShapes`
reads those two private lists by reflection and frees whatever was already registered
([pebbles.lib/Runtime/ClutterRetirement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterRetirement.cs#L59-L71)).
`OwnedHull` itself keeps only managed points; its temporary hull is disposed in the constructor.[^clutter-retirement]

## Lifecycle and timing

| Step | Where | Thread and frame point |
|---|---|---|
| Edit, scale, validate recipes | Workshop and main form (`ISubmod.RenderFloatingWindows` / `RenderContent`) | Main thread, `frame:draw-ui`; no physics objects exist yet |
| `ClutterColliders.Build`, `OwnedHull` constructor, `Reach` | `ClutterGraph.Build` inside `ClutterController.Apply`, inside the `Universe.ExecuteNextClothSolvers` prefix (`Program.PrepareFrame` L2217) after `Quiesce()` | Main thread, `frame:dispatch-solvers`, vehicle and cloth jobs finished |
| Scaled shape creation (`CreateShapeInto` x 16 buckets) | `ClutterEcotypePhysicalData` constructor -> `PopulateColliderPrimitives` in `ClutterResources.Build` | Same prefix |
| Statics installed | `Universe.ExecuteNextVehicleSolvers` (L2220, same `PrepareFrame`) -> `SyncGroundClutter` -> `PhysicsBubble.SyncGroundClutterStatics` -> `BubbleClutterStatics.Update`/`AddCell` | Main thread, `frame:dispatch-solvers` |
| Contacts, hits, displacement | `ConstraintSim` steps and `ApplyPendingHits` | Vehicle worker, `physics:vehicle-solver`, `physics:bepu` |
| Shape release | `ClutterResources.Dispose` on restore, replacement, renderer disposal or unload, after `Quiesce()` | Main thread |

Why this point: both the hull constructor and `PopulateColliderPrimitives` take
`ConstraintSim.UnlockShapes()`, the non-blocking variant that throws "cannot be mutated while the vehicle
update is stepping" whenever `VehicleUpdateTask.Run` holds the registry (`BeginVehicleUpdate` sets the
state to -1). Pebbles only builds after waiting `JobSystems.VehicleSolver` and `ClothSolvers`, so the
lock is free. `OwnedHull` takes its own short unlock in its constructor (during `ClutterGraph.Build`,
before any native object exists); its `CreateShapeInto` later runs inside the native constructor's
unlock and only uses the `ShapesUnlock` it is handed.[^ksa-constraint-sim][^ksa-shapes-unlock] The
build must also precede `ChuteEnvironmentSnapshot` and the vehicle kick, which read clutter collider
shapes; see [ground clutter replacement](./pebbles-ground-clutter-replacement.md) for that ordering.
Statics only appear while `GameSettings.GetGroundClutterCollisions()` is on; otherwise
`SyncGroundClutterStatics` clears them.[^ksa-physics-bubble][^ksa-universe]

## Key code

| Path | Role |
|---|---|
| [pebbles.lib/Runtime/ClutterColliders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterColliders.cs) | `Build`, `Reach`, private `OwnedHull` |
| [pebbles.lib/Runtime/HullMath.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/HullMath.cs) | Bounds centre and face-fan volume, game-independent |
| [pebbles.lib/Runtime/ClutterGraph.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterGraph.cs#L73-L82) | Collision policy, mass and uniform-scale checks, reach radii |
| [pebbles.lib/Runtime/ClutterResources.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterResources.cs#L72-L96) | Physical radii = max(visual, collider reach) |
| [pebbles.lib/Runtime/ClutterCapture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterCapture.cs#L77-L90) | Stock template to recipe |
| [pebbles.lib/Runtime/ClutterRetirement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterRetirement.cs#L59-L71) | Registry cleanup after a failed build |
| [pebbles.lib/Models/PebblesRecipe.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/PebblesRecipe.cs#L25-L43) | `ColliderKind`, `CollisionPolicy`, `ColliderRecipe` |
| [pebbles.lib/Models/RecipeValidation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/RecipeValidation.cs#L46-L68) | Detached collider and object rules |
| [pebbles.lib/Models/ClutterAuthoring.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/ClutterAuthoring.cs) | Uniform scale propagation, `PrimitiveList` switch |
| [pebbles.tests/HullChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.tests/HullChecks.cs) | Volume and recentring checks on known polyhedra |
| [pebbles.tests/PebblesChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.tests/PebblesChecks.cs#L28-L31) | Capsule height and duplicate-id rejection |

## Snippets

Recipe to native template:

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

The private hull subclass:

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
// ...
        protected override void CreateShapeInto(in ShapesUnlock unlock, double scale, out TypedIndex handle)
        {
            var points = _points.Select(p => p * (float)scale).ToArray();
            if (!ConvexHullHelper.CreateShape(points, unlock.BufferPool, out _, out var hull)) throw new InvalidOperationException("Scaled convex hull is degenerate.");
            handle = unlock.Shapes.Add(in hull);
        }
```

Policy, mass check and reach while building the private graph:

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

How the engine consumes any `ColliderTemplate` (game, build 5554):

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `ColliderTemplate.CreateShapeInto` (abstract), `CreateScaledShape`, `ShapeOffsetCollider`, `VolumeCubicMetres`, `LocationAsmb`, `Collider2Asmb` | [current/decomp/KSA/ColliderTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ColliderTemplate.cs#L7-L66) | Base of `OwnedHull`; fields set by `Build` |
| `BoxColliderTemplate.LengthX/Y/Z` | [current/decomp/KSA/BoxColliderTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BoxColliderTemplate.cs#L27-L31) | Box shape per scale |
| `SphereColliderTemplate.Radius` | [current/decomp/KSA/SphereColliderTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SphereColliderTemplate.cs#L19-L23) | Sphere shape per scale |
| `CapsuleColliderTemplate.Radius`, `LengthY` | [current/decomp/KSA/CapsuleColliderTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CapsuleColliderTemplate.cs#L24-L28) | Capsule (segment length, not total height) |
| `CylinderColliderTemplate.Radius`, `LengthY` | [current/decomp/KSA/CylinderColliderTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CylinderColliderTemplate.cs#L24-L28) | Cylinder shape per scale |
| `ConvexHullColliderTemplate.EnsureBaseHull`, `ComputeHullVolume`, `CreateShapeInto` | [current/decomp/KSA/ConvexHullColliderTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConvexHullColliderTemplate.cs#L38-L108) | Stock behavior `OwnedHull` mirrors |
| `MeshColliderTemplate.ScaledPoints`, `MeshReference.HostMesh` | [current/decomp/KSA/MeshColliderTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshColliderTemplate.cs#L53-L71) | Stock hulls read only the first primitive ([current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs#L52)) |
| `ClutterObjectTemplate.Colliders`, `MassKg`, `GetMass` | [current/decomp/KSA/ClutterObjectTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ClutterObjectTemplate.cs#L33-L54) | Collider list and `MassKg * scale^3` |
| `ClutterEcotypeReference.Collideable`, `IsValid`, `Resolve`, `ToParameters` | [current/decomp/KSA/ClutterEcotypeReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ClutterEcotypeReference.cs#L35-L71) | Smooth-normal rejection; uniform-scale and mass checks Pebbles turns into errors (L182-L214) |
| `ClutterEcotypePhysicalData` constructor, `PopulateColliderPrimitives`, `PopulateUnitChildMasses`, `ColliderPosition`, `GetScale`, `Dispose` | [current/decomp/KSA/ClutterEcotypePhysicalData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ClutterEcotypePhysicalData.cs#L196-L364) | Shape creation, compounds, radii; frees shapes at L1389-L1405 |
| `BubbleClutterStatics.AddCell`, `TryDisplaceInstance` | [current/decomp/KSA/BubbleClutterStatics.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BubbleClutterStatics.cs#L790-L829) | Statics per collider; dynamic body on hit (L550-L580) |
| `PhysicsBubble.SyncGroundClutterStatics` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L351-L370) | Collision setting gate and per-bubble install |
| `Universe.SyncGroundClutter` (private) | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2157-L2201) | Calls the bubble sync from `ExecuteNextVehicleSolvers` |
| `ConstraintSim.UnlockShapes`, `UnlockShapesBlocking`, `BeginVehicleUpdate`, `ShapesUnlock.Shapes/BufferPool` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L111-L165) | Registry lock; see also [current/decomp/KSA/ShapesUnlock.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShapesUnlock.cs#L9-L19) |
| `QuaternionEx.CreateFromXyzRadians` | [current/decomp/KSA/QuaternionEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/QuaternionEx.cs#L179-L191) | Collider rotation convention |
| `ConvexHullHelper.CreateShape(Span<Vector3>, BufferPool, out Vector3, out ConvexHull)` | [current/decomp/BepuPhysics.Collidables/ConvexHullHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/BepuPhysics.Collidables/ConvexHullHelper.cs#L726) | Hull build and centroid |
| `ConvexHull.FaceToVertexIndicesStart`, `GetVertexIndicesForFace`, `GetPoint`, `Dispose(BufferPool)` | [current/decomp/BepuPhysics.Collidables/ConvexHull.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/BepuPhysics.Collidables/ConvexHull.cs#L49-L243) | Face walk for the volume; temporary hull disposal |

## Pitfalls and version notes

- **Not a byte-for-byte port of 5554.** Stock `ConvexHullColliderTemplate` builds one base hull under
  `UnlockShapesBlocking` and serves each scale with `ConvexHullHelper.CreateTransformedCopy`; it also logs
  an error when the hull centre falls outside the points' bounds. `OwnedHull` re-runs `CreateShape` on
  scaled points for every one of the 16 buckets and has no centre check. The geometry is equivalent for
  uniform scale, but a 65,536-point hull costs 16 full hull builds per collider during the commit.
- **Non-blocking unlock.** `OwnedHull`'s constructor uses `UnlockShapes()`, so constructing one from UI
  code while the vehicle task runs throws. Only build it at a quiesced main-thread point.
- **Dimension conventions.** Recipe Y is total capsule height; Bepu and KSA store the inner segment.
  Capturing a stock capsule adds `2 * Radius` back. Mixing the two conventions makes capsules a diameter
  too tall or short.
- **Only `.X` of placement scale is used** by `ClutterEcotypePhysicalData.GetScale`, which is why
  collidable ecotypes must have uniform XYZ scale. Replaced types use scale 1, so all 16 buckets are
  identical but still allocated.
- **Hull rules differ by origin.** Rebuilt stock hulls use the first primitive only (stock rule); custom
  hulls combine all primitives of the chosen mesh. At this ref the workshop's **Add fitted shape** list
  offers only the four primitives and `AssignMesh` clears colliders, so new custom hulls have no UI path;
  the runtime, inspector and wireframe code still handle hull entries already present in a recipe.
- **`Reach` has a `MeshColliderTemplate` branch** that `Build` never produces (stock hull templates are
  always rebuilt as `OwnedHull`); unknown template types throw.
- **Collision setting.** With ground clutter collisions disabled in game settings, no statics are
  installed and authored colliders are inert.
- **Reflection on `_primitiveShapes`/`_compoundShapes`** is used only on the failure path; a rename
  surfaces as a retirement fault, not at patch time.
- **Build drift.** The authors verified this against 5482 (hull volume added for 5447). Every member
  used here exists unchanged in 5554.[^scope-ground-clutter]
- Managed tests cover only the game-independent parts (`HullMath`, recipe rules); shape creation,
  contacts and displacement need in-game acceptance.[^hull-checks][^pebbles-checks]

## See also

- [Per-celestial ground clutter replacement](./pebbles-ground-clutter-replacement.md) (the commit that builds these)
- [Collider workshop editor](./pebbles-workshop-editor.md) (where colliders are authored)
- [Removed and displaced clutter persistence](./pebbles-clutter-state-persistence.md) (what happens after a hit)
- [Sphinx static colliders](./sphinx-static-colliders.md) (mod-owned statics without native clutter)
- [Godzilla collider scaling](./godzilla-collider-scaling.md) (scaling part colliders through the same templates)
- [Pebbles sub-mod](../submods/pebbles.md) and [mod overview](../overview.md)
- [Subclass ColliderTemplate with an owned convex hull](../../../howto/physics/colliders/give-ground-clutter-custom-colliders/give-ground-clutter-custom-colliders-1.md) - how-to article demonstrating this feature
- [Add and free statics' shapes only while the solver is idle](../../../howto/physics/colliders/mutate-the-shared-bepu-shape-registry/mutate-the-shared-bepu-shape-registry-2.md) - how-to article demonstrating this feature

[^scope-ground-clutter]: scope/ground-clutter.md (authors' integration notes)
[^clutter-colliders]: pebbles.lib/Runtime/ClutterColliders.cs
[^pebbles-recipe]: pebbles.lib/Models/PebblesRecipe.cs
[^ksa-box-collider-template]: KSA.BoxColliderTemplate (decompiled, build 2026.10.10.5554)
[^ksa-sphere-collider-template]: KSA.SphereColliderTemplate (decompiled, build 2026.10.10.5554)
[^ksa-capsule-collider-template]: KSA.CapsuleColliderTemplate (decompiled, build 2026.10.10.5554)
[^ksa-cylinder-collider-template]: KSA.CylinderColliderTemplate (decompiled, build 2026.10.10.5554)
[^ksa-quaternion-ex]: KSA.QuaternionEx (decompiled, build 2026.10.10.5554)
[^ksa-collider-template]: KSA.ColliderTemplate (decompiled, build 2026.10.10.5554)
[^ksa-convex-hull-collider-template]: KSA.ConvexHullColliderTemplate (decompiled, build 2026.10.10.5554)
[^ksa-mesh-collider-template]: KSA.MeshColliderTemplate (decompiled, build 2026.10.10.5554)
[^ksa-mesh-reference]: KSA.MeshReference (decompiled, build 2026.10.10.5554)
[^hull-math]: pebbles.lib/Runtime/HullMath.cs
[^bepu-convex-hull-helper]: BepuPhysics.Collidables.ConvexHullHelper (decompiled, build 2026.10.10.5554)
[^bepu-convex-hull]: BepuPhysics.Collidables.ConvexHull (decompiled, build 2026.10.10.5554)
[^ksa-clutter-physical-data]: KSA.ClutterEcotypePhysicalData (decompiled, build 2026.10.10.5554)
[^pebbles-source-map]: plans/PEBBLES_SOURCE_MAP.md (authors' native source investigation, build 5402)
[^ksa-clutter-ecotype-reference]: KSA.ClutterEcotypeReference (decompiled, build 2026.10.10.5554)
[^clutter-resources]: pebbles.lib/Runtime/ClutterResources.cs
[^ksa-bubble-clutter-statics]: KSA.BubbleClutterStatics (decompiled, build 2026.10.10.5554)
[^ksa-clutter-object-template]: KSA.ClutterObjectTemplate (decompiled, build 2026.10.10.5554)
[^clutter-authoring]: pebbles.lib/Models/ClutterAuthoring.cs
[^recipe-validation]: pebbles.lib/Models/RecipeValidation.cs
[^clutter-retirement]: pebbles.lib/Runtime/ClutterRetirement.cs
[^ksa-constraint-sim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-shapes-unlock]: KSA.ShapesUnlock (decompiled, build 2026.10.10.5554)
[^ksa-physics-bubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^hull-checks]: pebbles.tests/HullChecks.cs
[^pebbles-checks]: pebbles.tests/PebblesChecks.cs
