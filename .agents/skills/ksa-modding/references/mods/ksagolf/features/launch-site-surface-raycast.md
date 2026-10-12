---
type: Mod Feature
title: Launch-site surface by raycast into a copied collider
description: A Harmony prefix on Universe.ExecuteNextVehicleSolvers copies each launch site's Bepu compound collider and terrain-tagged model triangles while the shapes registry is unlocked, so the ball can ray-cast straight down onto the apron, pad and drawn road at any time
mod: ksagolf
category: physics
techniques: [harmony-prefix, reflection, bepu-physics, ray-cast, main-thread-sync-point]
hooks: [harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers, reflection KSA.StaticObject._models]
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types:
  - KSA.Universe
  - KSA.ConstraintSim
  - KSA.ShapesUnlock
  - KSA.LocationReference
  - KSA.LandmarkReference
  - KSA.StaticObject
  - KSA.StaticObjectModel
  - KSA.PartModelModule
  - KSA.MeshReference
  - KSA.CelestialTemplate
  - KSA.Celestial
  - KSA.VehicleUpdateTask
  - BepuPhysics.Collidables.Shapes
  - BepuPhysics.Collidables.BigCompound
  - BepuPhysics.Collidables.CompoundChild
  - BepuPhysics.Collidables.IConvexShape
  - BepuPhysics.Collidables.TypedIndex
  - BepuPhysics.RigidPose
tags: [launch-pad, static-object, bepu, collider, raycast, shapes-registry, harmony, reflection, sync-point]
---

The engine's height field knows only terrain. A launch site is laid over it: an apron a few
centimetres proud, a pad about a metre and a half, and drawn road and grass running on past the
collider. Without help a ball dropped on a pad would sink to the levelled terrain beneath it.
KSAGolf rebuilds the launch site's surface for the ball by copying what the engine itself stands
craft and kittens on, then ray-casting straight down into the copy.[^golf-padsurface]

The trick generalises: **the Bepu shapes registry is only safely readable at one point in the
frame, so copy what you need there and query the copy whenever you like.**

## How it works

1. **Ask.** `PadSurface.Want(body)` records the body to copy. `Golf.Step` calls it each frame for
   the parent body of the kitten carrying the putter, and `TryRadius` calls it when asked about a body it has not copied.[^golf-golf]
2. **Copy, in the sync window.** A Harmony prefix on `Universe.ExecuteNextVehicleSolvers` opens
   `using ShapesUnlock unlocked = ConstraintSim.UnlockShapes()` and copies every launch site of the
   wanted body. A failure is logged once and retried next frame.
3. **Place it as the engine does.** For each `LandmarkReference` with `IsLaunchPad` whose
   `GetStaticObject()` has an existing `CollisionShape` of type `BigCompound`, the site's position is
   `ForwardCcf * (MeanRadius + GetTerrainHeightFromDirCcf(ForwardCcf)) + up * GroundOffset` and its
   orientation is built from the `GetAxesCcf` up, east and north rows, exactly as
   `ConstraintSim.UpdateStaticObjectCollider` places the live collider.[^ksa-constraintsim]
4. **Copy the convex children by value.** `Shapes.GetShape<BigCompound>(index).Children` gives each
   child's `ShapeIndex` and local pose. Each `Box`, `Sphere`, `Capsule`, `Cylinder`, `ConvexHull` or
   `Triangle` is copied into a generic `Piece<TShape>` wrapper. A convex shape's own
   `RayTest(in RigidPose, origin, direction, out t, out normal)` needs no registry and no buffer
   pool, while `BigCompound`'s ray test walks the registry, which is why the children are copied.[^bepu-iconvexshape][^bepu-bigcompound]
5. **Copy the drawn ground too.** The collider stops well short of what is drawn (about 15 m short
   of where a kitten stands at LC-39A, by the repo's measurement). So `DrawnGroundOf` reads the
   private `StaticObject._models` list by reflection, keeps instances whose `StaticObjectModel.Template`
   is tagged `Terrain` and has a `Mesh`, transforms every triangle of `MeshReference.PositionsCompare`
   by the instance's `Model2Asmb`, and buckets them into a 4 m grid on the site's east/north
   plane.[^golf-blocked][^ksa-staticobject][^ksa-meshreference]
6. **Query.** `TryRadius(body, dirCcf, terrainRadius, out radius)` starts a ray 30 m above the
   terrain along `dirCcf`, pointing down, transforms it into each nearby site's frame (reach is
   `max(FootprintRadius, 50) * 1.5`), takes the nearest hit over the convex pieces and the drawn
   triangles (Moller-Trumbore in doubles), and returns `terrainRadius + 30 - hit`. The ball's
   `BodyGround` keeps the higher of that and the terrain or sea radius.[^golf-golf]

## Lifecycle and timing

| Step | Where | Thread | Why there |
|---|---|---|---|
| Install | `[StarMapAllModsLoaded]` (`PadSurface.Install()`) | main | Patches once at startup; if the target is missing the ball rests on terrain alone[^golf-mod] |
| Copy | Harmony prefix on `Universe.ExecuteNextVehicleSolvers`, `frame:dispatch-solvers` | main | The only window in which nothing holds the shapes registry |
| Query | The mod's per-frame step (`[StarMapAfterGui]` or its fallbacks), `frame:draw-ui` | main | Reads only the mod's own copy, so any time is safe |
| Forget | `ForgetTheWorld` on a save load, and `Remove()` at the fault limit (or at process exit from `[StarMapUnload]`) | main | The copy belongs to the old world |

`Program.PrepareFrame` runs `JobSystems.VehicleSolver.Wait()`, then `Universe.ApplyVehicleSolvers`,
input, `ExecuteNextClothSolvers`, then `Universe.ExecuteNextVehicleSolvers`, whose body queues the
next vehicle step and starts the job system.[^ksa-program][^ksa-universe] That step is
`VehicleUpdateTask.Run`, which begins with `ConstraintSim.BeginVehicleUpdate()`: it marks the
registry as held by the vehicle update and throws if anyone has it unlocked; `EndVehicleUpdate()`
in its `finally` releases it.[^ksa-vehicleupdatetask] While the step runs, which covers the GUI hook
and the rest of the frame, `UnlockShapes()` throws "The shapes registry cannot be mutated while the
vehicle update is stepping; stage the change and apply it at the frame sync point".[^ksa-constraintsim]
A prefix on `ExecuteNextVehicleSolvers` runs after the previous step has finished and been applied
and before the next is queued, and the `using` disposes the unlock (`RelockShapes`) before the
method body starts the job, so the step can never find the registry open. The engine uses the same
window for its own registry work: the method body's `SyncGroundClutter` reaches
`BubbleTerrainBlocks.EnsureShapePool`, which creates terrain-block shapes under `UnlockShapes()`.[^ksa-universe]

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Ksa/PadSurface.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PadSurface.cs#L128-L150) | `Install`: prefix on `Universe.ExecuteNextVehicleSolvers` |
| [src/KSAGolf/Ksa/PadSurface.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PadSurface.cs#L222-L275) | `BetweenVehicleSteps` and `Copy`: unlock, place, copy children |
| [src/KSAGolf/Ksa/PadSurface.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PadSurface.cs#L277-L313) | `DrawnGroundOf` (reflection on `_models`) and `PieceOf` (typed shape copy) |
| [src/KSAGolf/Ksa/PadSurface.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PadSurface.cs#L183-L220) | `TryRadius`: the downward ray into pieces and drawn triangles |
| [src/KSAGolf/Ksa/PadSurface.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PadSurface.cs#L45-L112) | `DrawnGround`: 4 m grid buckets and a double-precision ray-triangle test |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L745-L766) | `BodyGround`: the higher of terrain, sea and launch-site top |

## Snippets

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

```csharp
// src/KSAGolf/Ksa/PadSurface.cs L250-L271
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
```

```csharp
// src/KSAGolf/Ksa/PadSurface.cs L192-L217
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
```

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

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `Universe.ExecuteNextVehicleSolvers(double, SimStep)` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2139) | Patched (prefix); the main-thread sync point before the vehicle step is queued |
| `Program.PrepareFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2140-L2236) | Wait, apply, input, then dispatch: the order that makes the prefix safe |
| `ConstraintSim.UnlockShapes()` / `BeginVehicleUpdate()` / `RelockShapes()` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L111-L166) | The registry interlock between main-thread mutation and the vehicle step |
| `ShapesUnlock.Shapes` | [current/decomp/KSA/ShapesUnlock.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShapesUnlock.cs) | The shared `Shapes` registry, held while the struct is undisposed |
| `ConstraintSim.UpdateStaticObjectCollider` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L550-L633) | How the engine places a launch site's collider, reproduced by the copy |
| `VehicleUpdateTask.Run` | [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L261-L299) | Holds the registry for the whole vehicle step |
| `LocationReference.GetStaticObject()` / `ForwardCcf` / `GetAxesCcf` | [current/decomp/KSA/LocationReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LocationReference.cs#L117-L154) | The site's static object and its body-fixed frame |
| `LandmarkReference.IsLaunchPad` | [current/decomp/KSA/LandmarkReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LandmarkReference.cs) | Which locations are launch sites |
| `StaticObject.CollisionShape` / `GroundOffset` / `FootprintRadius` / `_models` (private) | [current/decomp/KSA/StaticObject.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObject.cs#L12-L36) | Collider handle, height above terrain, footprint, and the drawn model instances |
| `StaticObjectModel.Template` / `PartModelModule.Template.Terrain` / `Mesh` | [current/decomp/KSA/PartModelModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelModule.cs#L20-L45) | Which models are drawn as terrain, and their meshes |
| `MeshReference.PositionsCompare` | [current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs#L29) | CPU-side triangle soup per primitive |
| `Shapes.GetShape<T>` | [current/decomp/BepuPhysics.Collidables/Shapes.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/BepuPhysics.Collidables/Shapes.cs#L43-L47) | Typed access into the registry |
| `BigCompound.Children` / `CompoundChild` | [current/decomp/BepuPhysics.Collidables/BigCompound.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/BepuPhysics.Collidables/BigCompound.cs#L77-L79) | The compound's children: `ShapeIndex`, `LocalPosition`, `LocalOrientation` |
| `IConvexShape.RayTest` | [current/decomp/BepuPhysics.Collidables/IConvexShape.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/BepuPhysics.Collidables/IConvexShape.cs#L13) | Self-contained ray test on a copied shape |

## Pitfalls and version notes

- **Do not read the registry from a StarMap hook.** Every StarMap per-frame hook runs while the
  vehicle step holds it; `UnlockShapes()` throws there, and even a read through the public
  `ConstraintSim.ReadOnlyShapes` has no exclusive access. Copy at the sync point instead.[^ksa-constraintsim]
- **Never hold the unlock across the prefix.** `BeginVehicleUpdate()` throws
  "Cannot start the vehicle update while the shapes registry is unlocked for mutation"; the `using`
  makes the relock unconditional.
- **The copy is a snapshot.** It is correct because a launch site's collider does not change once
  built; do not use this pattern for shapes that move or are rebuilt.
- **The collider is not the whole site.** Its compound stops short of the drawn road and grass, and
  the field under the site is decal-levelled; the drawn terrain-tagged triangles fill the gap. The
  repo measured the apron 10.4 cm above the height field and the ray finding it within 1.5 cm of a
  kitten's feet on its test save.[^golf-blocked]
- **Precision.** The convex test runs in `float` in the site's local frame (tens of metres), which is
  fine; never cast in `float` at planet-centred magnitudes.
- **Reflection on a private field.** `StaticObject._models` and the `ModelInstance` properties
  `Model` and `Model2Asmb` are read by name; if they move, the drawn ground silently comes back empty
  and only the collider is used.
- **The cursor does not use this.** `KsaWorld.TryGroundAlong` deliberately resolves the cursor
  against terrain alone; see [Cursor picking and terrain ray](cursor-picking-and-terrain-ray.md).
- **Version.** Built against 2026.9.22.5482. `ConstraintSim.cs`, `StaticObject.cs`,
  `LandmarkReference.cs` and `ShapesUnlock.cs` are unchanged in 2026.10.10.5554, and the
  `ExecuteNextVehicleSolvers` body and its place in `PrepareFrame` are the same.

## See also

- [Course colliders registered as ground clutter](course-colliders-as-clutter.md)
- [Golf ball physics in the body-fixed frame](golf-ball-physics.md)
- [Terrain height queries off the engine's height field](terrain-height-queries.md)
- [Save-load detection](save-load-detection.md)
- [API surface record and assembly lock](api-surface-and-assembly-lock.md)
- [Copy convex children under the shapes lock in an ExecuteNextVehicleSolvers prefix](../../../howto/physics/aero-and-ground/sample-the-ground-under-each-wheel/sample-the-ground-under-each-wheel-2.md) - how-to article demonstrating this feature
- [Create shapes in an ExecuteNextVehicleSolvers prefix and cache by size](../../../howto/physics/colliders/mutate-the-shared-bepu-shape-registry/mutate-the-shared-bepu-shape-registry-1.md) - how-to article demonstrating this feature
- [Drain a command queue in a Priority.First prefix on Universe.ExecuteNextVehicleSolvers](../../../howto/physics/solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-2.md) - how-to article demonstrating this feature

[^golf-padsurface]: src/KSAGolf/Ksa/PadSurface.cs
[^golf-golf]: src/KSAGolf/Ksa/Golf.cs
[^golf-mod]: src/KSAGolf/Ksa/KSAGolfMod.cs
[^golf-blocked]: docs/BLOCKED-ON-KSA.md (where a structure's surface is)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatetask]: KSA.VehicleUpdateTask (decompiled, build 2026.10.10.5554)
[^ksa-staticobject]: KSA.StaticObject (decompiled, build 2026.10.10.5554)
[^ksa-meshreference]: KSA.MeshReference (decompiled, build 2026.10.10.5554)
[^bepu-iconvexshape]: BepuPhysics.Collidables.IConvexShape (decompiled, build 2026.10.10.5554)
[^bepu-bigcompound]: BepuPhysics.Collidables.BigCompound (decompiled, build 2026.10.10.5554)
