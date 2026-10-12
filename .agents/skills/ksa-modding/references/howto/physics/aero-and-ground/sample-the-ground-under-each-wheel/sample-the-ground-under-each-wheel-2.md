---
type: How-To
title: Copy convex children under the shapes lock in an ExecuteNextVehicleSolvers prefix
description: Copy a launch site's convex collider children out of KSA's shared Bepu shape registry at the one main-thread moment it can be read, then ray-test the copy and the site's drawn ground triangles from anywhere to get the surface height under a point
category: physics
group: aero-and-ground
topic: sample-the-ground-under-each-wheel
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksagolf/features/launch-site-surface-raycast]
techniques: [main-thread-sync-point, harmony-prefix, reflection, bepu-physics]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers
  - reflection KSA.StaticObject._models
lifecycle: [startup:all-mods-loaded, frame:dispatch-solvers, frame:draw-ui]
game_types:
  - KSA.ConstraintSim
  - KSA.StaticObject
  - KSA.LocationReference
  - KSA.LandmarkReference
  - KSA.Universe
  - KSA.Celestial
  - BepuPhysics.Collidables.BigCompound
  - BepuPhysics.Collidables.IConvexShape
tags: [physics, terrain, launch-pad, bepu, raycast, shapes-registry, sync-point]
---

Outcome: the height of the top of a launch site (pad, apron, and the drawn road and grass that run past the collider) under any
point, from a copy of the engine's own collider, queryable at any time. Prerequisites: StarMap and Harmony. Demonstrated by
KSAGolf, whose ball rests on the site instead of sinking to the levelled terrain beneath it. The same copy-out pattern is the
general way to read the shared shape registry from a mod; see
[create and free shapes in KSA's global Bepu shape registry](../../colliders/mutate-the-shared-bepu-shape-registry/mutate-the-shared-bepu-shape-registry-1.md).

## Background: how the game does it

The engine builds each launch site's collider as a Bepu `BigCompound` of convex pieces, stores it in the shared `Shapes`
registry, and poses it for each bubble (`ConstraintSim.UpdateStaticObjectCollider`, with the axes from `LocationReference.GetAxesCcf`)[^ksa-locationreference]: body-fixed, at the landmark's direction on the terrain, lifted by `GroundOffset`, with
the landmark's up, east and north as the basis. The registry is guarded by an interlock in `ConstraintSim`:

```csharp
// current/decomp/KSA/ConstraintSim.cs L111-L133
	public static void BeginVehicleUpdate()
	{
		if (Interlocked.CompareExchange(ref _shapesState, -1, 0) != 0)
		{
			throw new InvalidOperationException("Cannot start the vehicle update while the shapes registry is unlocked for mutation");
		}
	}

	public static void EndVehicleUpdate()
	{
		Volatile.Write(ref _shapesState, 0);
	}

	public static ShapesUnlock UnlockShapes()
	{
		int currentManagedThreadId = Environment.CurrentManagedThreadId;
		if (Volatile.Read(in _shapesState) != currentManagedThreadId && Interlocked.CompareExchange(ref _shapesState, currentManagedThreadId, 0) != 0)
		{
			throw new InvalidOperationException((Volatile.Read(in _shapesState) == -1) ? "The shapes registry cannot be mutated while the vehicle update is stepping; stage the change and apply it at the frame sync point" : "The shapes registry is already unlocked by another thread; shapes mutation is single-threaded");
		}
		_shapesUnlockDepth++;
		return default(ShapesUnlock);
	}
```

While the vehicle step runs, `UnlockShapes()` throws, and the step itself refuses to start while anyone holds the unlock.[^ksa-constraintsim]
`BigCompound.Children` and each `CompoundChild`'s `ShapeIndex`, `LocalPosition` and `LocalOrientation` describe the
pieces; a convex shape's own `RayTest` needs no registry:[^bepu-bigcompound][^bepu-compoundchild][^bepu-iconvexshape]

```csharp
// current/decomp/BepuPhysics.Collidables/IConvexShape.cs L13
	bool RayTest(in RigidPose pose, Vector3 origin, Vector3 direction, out float t, out Vector3 normal);
```

The collider stops short of what is drawn: the site also has terrain-tagged models whose triangles run on past the collider. The
private `StaticObject._models` list (a struct `ModelInstance` with `Model` and `Model2Asmb`) holds those instances.[^ksa-staticobject]

## Lifecycle and timing

- **Install: `startup:all-mods-loaded`.** KSAGolf's `[StarMapAllModsLoaded]` method calls `PadSurface.Install()`.[^golf-mod]
- **Copy: `frame:dispatch-solvers`, main thread.** `Program.PrepareFrame` joins the vehicle solver, applies its result, polls
  input and then calls `Universe.ExecuteNextVehicleSolvers`, which queues the next `VehicleUpdateTask`.[^ksa-program][^ksa-universe]
  `VehicleUpdateTask.Run` begins with `ConstraintSim.BeginVehicleUpdate()` and ends it in a `finally`,[^ksa-vehicleupdatetask]
  so the registry is held for the whole step, which covers the GUI hook and the rest of the frame. A prefix on
  `ExecuteNextVehicleSolvers` runs after the previous step has been applied and before the next is queued; the `using` relocks
  before the method body starts the job. The engine uses the same window itself: `SyncGroundClutter`, in the method body,
  creates terrain-block shapes under `UnlockShapes()`.
- **Query: any time, main thread.** KSAGolf asks from its per-frame step[^golf-golf] (`StarMapAfterGui` or its fallbacks,
  `frame:draw-ui`); the copy is the mod's own data, so any thread and any moment is safe.
- **Do not read the registry from a StarMap hook.** Every per-frame hook runs while the vehicle step holds it. See the
  [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-physics-step) and
  [mutate vehicles at a physics-safe point](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-2.md).

## Steps

1. **Install a prefix on `Universe.ExecuteNextVehicleSolvers`.** If the method is missing, log and degrade: the ball then rests
   on terrain alone.[^golf-padsurface]

```csharp
// src/KSAGolf/Ksa/PadSurface.cs L128-L150
    public static void Install()
    {
        if (Installed) return;

        try
        {
            MethodInfo? target = AccessTools.Method(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers));
            if (target is null)
            {
                Log.Warn("balls rest on terrain alone: KSA has no Universe.ExecuteNextVehicleSolvers to copy launch sites in");
                return;
            }

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(target, prefix: new HarmonyMethod(typeof(PadSurface).GetMethod(nameof(BetweenVehicleSteps), BindingFlags.NonPublic | BindingFlags.Static)));
            Installed = true;
            Log.Info("launch sites are copied between vehicle steps, via Universe.ExecuteNextVehicleSolvers");
        }
        catch (Exception e)
        {
            Log.Warn($"balls rest on terrain alone: could not hook the vehicle step ({e.GetBaseException().Message})");
        }
    }
```

2. **Ask for a body, copy in the window.** Callers record the body to copy (`PadSurface.Want(body)`, a `volatile` field); the
   prefix does the copy inside a `using` on `ConstraintSim.UnlockShapes()`. A failure is logged once and retried next frame:

```csharp
// src/KSAGolf/Ksa/PadSurface.cs L222-L243
    // Nothing is stepping here and nothing can start: the last step has been applied and the next is
    // queued after this returns. A refusal all the same leaves the copy for the next frame.
    private static void BetweenVehicleSteps()
    {
        if (_wanted is not { } body) return;

        try
        {
            using ShapesUnlock unlocked = ConstraintSim.UnlockShapes();
            _pads = Copy(body, unlocked.Shapes);
            _body = body;
            _wanted = null;
            Log.Info($"copied {_pads.Length} launch site(s) on {body.Id} for the ball to rest on, "
                     + $"{_pads.Sum(p => p.Pieces.Length)} collider pieces and {_pads.Sum(p => p.Drawn.Triangles)} drawn ground triangles");
        }
        catch (Exception e)
        {
            if (_complained) return;
            _complained = true;
            Log.Warn($"could not copy the launch sites yet, will try again: {e.GetBaseException().Message}");
        }
    }
```

   Expect `copied N launch site(s) on Earth for the ball to rest on, ...` in the log.

3. **Place each site as the engine does, and copy its convex children by value.** Pose from the landmark's forward direction on
   the terrain, `GroundOffset` along up, and a rotation built from the up, east and north rows; then walk
   `BigCompound.Children`:

```csharp
// src/KSAGolf/Ksa/PadSurface.cs L250-L272
        foreach (LocationReference location in template.Locations)
        {
            if (location is not LandmarkReference { IsLaunchPad: true } landmark) continue;
            if (landmark.GetStaticObject() is not { CollisionShape.Exists: true } site) continue;
            if (site.CollisionShape.Type != BigCompound.Id) continue;

            double3 forward = landmark.ForwardCcf;
            landmark.GetAxesCcf(out double3 up, out double3 east, out double3 north);
            double3 at = (forward * (body.MeanRadius + body.GetTerrainHeightFromDirCcf(forward))) + (up * site.GroundOffset);
            doubleQuat turn = doubleQuat.CreateFromRotationMatrix(new double4x4(up.X, up.Y, up.Z, 0.0, east.X, east.Y, east.Z, 0.0,
                                                                                north.X, north.Y, north.Z, 0.0, 0.0, 0.0, 0.0, 1.0));

            ref BigCompound compound = ref shapes.GetShape<BigCompound>(site.CollisionShape.Index);
            List<(RigidPose, IPiece)> pieces = [];
            for (int i = 0; i < compound.Children.Length; i++)
            {
                ref CompoundChild child = ref compound.Children[i];
                if (PieceOf(shapes, child.ShapeIndex) is { } piece) pieces.Add((new RigidPose(child.LocalPosition, child.LocalOrientation), piece));
            }

            pads.Add(new Pad(at, new Quaternion((float)turn.X, (float)turn.Y, (float)turn.Z, (float)turn.W),
                             Math.Max(site.FootprintRadius, 50.0) * 1.5, [.. pieces], DrawnGroundOf(site)));
        }
```

   Each child is copied into a generic `Piece<TShape>` wrapper holding the shape by value, one per convex type
   (`Box`, `Sphere`, `Capsule`, `Cylinder`, `ConvexHull`, `Triangle`):

```csharp
// src/KSAGolf/Ksa/PadSurface.cs L38-L41
    private sealed class Piece<TShape>(TShape shape) : IPiece where TShape : unmanaged, IConvexShape
    {
        public bool Ray(in RigidPose pose, Vector3 origin, Vector3 direction, out float t) => shape.RayTest(pose, origin, direction, out t, out _);
    }
```

```csharp
// src/KSAGolf/Ksa/PadSurface.cs L304-L313
    private static IPiece? PieceOf(Shapes shapes, TypedIndex index) => index.Type switch
    {
        Box.Id => new Piece<Box>(shapes.GetShape<Box>(index.Index)),
        Sphere.Id => new Piece<Sphere>(shapes.GetShape<Sphere>(index.Index)),
        Capsule.Id => new Piece<Capsule>(shapes.GetShape<Capsule>(index.Index)),
        Cylinder.Id => new Piece<Cylinder>(shapes.GetShape<Cylinder>(index.Index)),
        ConvexHull.Id => new Piece<ConvexHull>(shapes.GetShape<ConvexHull>(index.Index)),
        Triangle.Id => new Piece<Triangle>(shapes.GetShape<Triangle>(index.Index)),
        _ => null,
    };
```

4. **Copy the drawn ground too.** The collider ends well short of the drawn road (about 15 m short of where a kitten stands at
   LC-39A, by the repo's measurement). Read the private `StaticObject._models` by reflection, keep instances whose model
   template is tagged `Terrain` and has a `Mesh`, transform each triangle of `MeshReference.PositionsCompare` by the instance's
   `Model2Asmb`, and bucket them into a 4 m grid on the site's east/north plane (the `DrawnGround` class):

```csharp
// src/KSAGolf/Ksa/PadSurface.cs L277-L299
    private static readonly FieldInfo? ModelsField = AccessTools.Field(typeof(StaticObject), "_models");

    // Every triangle of the site's terrain-tagged models, placed as StaticObject.UpdateRenderData places them.
    private static DrawnGround DrawnGroundOf(StaticObject site)
    {
        var ground = new DrawnGround();
        if (ModelsField?.GetValue(site) is not System.Collections.IEnumerable models) return ground;

        foreach (object instance in models)
        {
            Type type = instance.GetType();
            if (type.GetProperty("Model")?.GetValue(instance) is not StaticObjectModel { Template: { Terrain: true, Mesh: { } mesh } }) continue;
            if (type.GetProperty("Model2Asmb")?.GetValue(instance) is not double4x4 toSite) continue;
            if (mesh.PositionsCompare is not { } primitives) continue;

            foreach (double3[] soup in primitives)
            {
                for (int i = 0; i + 2 < soup.Length; i += 3)
                {
                    ground.Add(double3.Transform(soup[i], toSite), double3.Transform(soup[i + 1], toSite), double3.Transform(soup[i + 2], toSite));
                }
            }
        }
```

5. **Query with a downward ray in the site's frame.** Start 30 m above the terrain along the direction from the body centre,
   transform into each nearby site's frame (reach is `max(FootprintRadius, 50) * 1.5`), take the nearest hit over the convex
   pieces and the drawn triangles, and return the radius of the top. A wheel mod takes the lesser hub height of this and the
   terrain sample ([approach 1](sample-the-ground-under-each-wheel-1.md)):

```csharp
// src/KSAGolf/Ksa/PadSurface.cs L192-L219
        double3 point = dirCcf * terrainRadius;
        foreach (Pad pad in _pads)
        {
            if ((point - pad.PositionCcf).Length() > pad.Reach) continue;

            // Into the site's own frame, where each piece sits at its pose within the compound.
            Quaternion toLocal = Quaternion.Conjugate(pad.Orientation);
            double3 from = (dirCcf * (terrainRadius + RayAbove)) - pad.PositionCcf;
            Vector3 origin = Vector3.Transform(new Vector3((float)from.X, (float)from.Y, (float)from.Z), toLocal);
            Vector3 direction = Vector3.Transform(new Vector3((float)-dirCcf.X, (float)-dirCcf.Y, (float)-dirCcf.Z), toLocal);

            float nearest = float.MaxValue;
            foreach ((RigidPose pose, IPiece piece) in pad.Pieces)
            {
                if (piece.Ray(pose, origin, direction, out float t) && t >= 0f && t < nearest) nearest = t;
            }

            double hit = nearest;
            double3 originD = new(origin.X, origin.Y, origin.Z), directionD = new(direction.X, direction.Y, direction.Z);
            if (pad.Drawn.Ray(originD, directionD, out double drawn) && drawn < hit) hit = drawn;

            if (hit == float.MaxValue || hit > RayAbove * 2.0) continue;

            double top = terrainRadius + RayAbove - hit;
            if (top > radius) radius = top;
        }

        return radius > 0.0;
```

   The convex test runs in `float` in the site's local frame (tens of metres), which is fine; never cast in `float` at
   planet-centred magnitudes.

6. **Forget on a save load.** The copy belongs to the old world; KSAGolf drops it (`Forget`) when a save loads and at unload.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/PadSurface.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PadSurface.cs) | Install, the between-steps copy, placement, drawn ground, the query |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L745-L766) | `BodyGround`: the higher of terrain, sea and launch-site top; `Want(world)` each frame |
| [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L39-L54) | `[StarMapAllModsLoaded]` calls `PadSurface.Install()` |

## Variations and gotchas

- **Never hold the unlock across the prefix.** `BeginVehicleUpdate()` throws if the registry is still unlocked; the `using` makes
  the relock unconditional.
- **The copy is a snapshot.** It is right because a launch site's collider does not change once built; do not use this pattern for
  shapes that move or are rebuilt.
- **The collider is not the whole site.** Hence the drawn terrain triangles; KSAGolf's repo measured the apron 10.4 cm above the
  height field and the ray finding it within 1.5 cm of a kitten's feet.
- **Reflection on a private list.** `_models` and its `Model` and `Model2Asmb` properties are resolved by name; a rename degrades
  to "collider only" (the ray still works, just short of the drawn ground).
- **Registry reads need the window.** A hook that is not in the sync point cannot read shapes at all; copy there and query later.
- **Non-compound sites.** Only sites whose `CollisionShape.Type` is `BigCompound` are copied.

## See also

- [Overview and comparison](overview.md)
- [Terrain height in the PrepareWorker prefix](sample-the-ground-under-each-wheel-1.md) and [rebuild colliders from template data](sample-the-ground-under-each-wheel-3.md)
- Mod feature: [KSAGolf launch-site surface raycast](../../../../mods/ksagolf/features/launch-site-surface-raycast.md)
- [Create and free shapes in KSA's global Bepu shape registry](../../colliders/mutate-the-shared-bepu-shape-registry/overview.md)
- [Add mod-owned static colliders that vessels and kittens collide with](../../colliders/add-static-colliders-to-physics-bubbles/overview.md)
- [Mutate vehicles at a physics-safe point](../../solver/mutate-vehicles-at-a-physics-safe-point/overview.md)

[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^bepu-bigcompound]: BepuPhysics.Collidables.BigCompound (decompiled, build 2026.10.10.5554)
[^bepu-compoundchild]: BepuPhysics.Collidables.CompoundChild (decompiled, build 2026.10.10.5554)
[^bepu-iconvexshape]: BepuPhysics.Collidables.IConvexShape (decompiled, build 2026.10.10.5554)
[^ksa-staticobject]: KSA.StaticObject (decompiled, build 2026.10.10.5554)
[^golf-mod]: KSAGolf: src/KSAGolf/Ksa/KSAGolfMod.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatetask]: KSA.VehicleUpdateTask (decompiled, build 2026.10.10.5554)
[^golf-golf]: KSAGolf: src/KSAGolf/Ksa/Golf.cs
[^golf-padsurface]: KSAGolf: src/KSAGolf/Ksa/PadSurface.cs
