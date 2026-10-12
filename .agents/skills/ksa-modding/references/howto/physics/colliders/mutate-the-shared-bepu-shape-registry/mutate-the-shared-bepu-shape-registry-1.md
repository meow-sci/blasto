---
type: How-To
title: Create shapes in an ExecuteNextVehicleSolvers prefix and cache by size
description: Make Bepu shapes on the main thread in the window between applying the last step and queueing the next, hand them to worker prefixes through a volatile snapshot, and copy convex pieces out so they can be queried at any time
category: physics
group: colliders
topic: mutate-the-shared-bepu-shape-registry
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksagolf/features/course-colliders-as-clutter, mods/ksagolf/features/launch-site-surface-raycast]
techniques: [main-thread-sync-point, harmony-prefix]
hooks:
  - harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers
  - harmony-prefix KSA.ConstraintSim.DetectCollisions
lifecycle: [frame:dispatch-solvers, frame:draw-ui, physics:bepu]
game_types:
  - KSA.Universe
  - KSA.ConstraintSim
  - KSA.ShapesUnlock
  - BepuPhysics.Collidables.Box
  - BepuPhysics.Collidables.BigCompound
tags: [physics, bepu, shapes, registry, sync-point, volatile, cache, harmony-prefix]
---

Outcome: shapes are created exactly when the registry is free, never throw, are shared between all bubbles, and are
cached so the registry does not grow. KSAGolf uses the window to create the `Box` shapes under the minigolf course
and, in a second feature, to copy the launch site's collider shapes out so a ball can be ray-cast against them
at any time. Prerequisites: StarMap and Harmony. The principle in one line: the Bepu shapes registry is only
writable while no vehicle step is running, which on the main thread means the span from the end of `JobSystems.VehicleSolver.Wait()`
in `PrepareFrame` to the moment `ExecuteNextVehicleSolvers` starts the job (a `Vehicle.PrepareWorker` prefix or an `InputEvents`
drain would also be idle). The `ExecuteNextVehicleSolvers` prefix is the simplest place to reach every frame, so do your
registry work there and keep a copy.

## Background: how the game does it

`Program.PrepareFrame` waits on the solvers, calls `Universe.ApplyVehicleSolvers`, handles input, then calls
`Universe.ExecuteNextVehicleSolvers`, whose body queues the next vehicle step.[^ksa-universe]

```csharp
// current/decomp/KSA/Universe.cs L2126-L2139
	public static void ExecuteNextVehicleSolvers(double dtPlayer, SimStep simStep)
	{
		if (CurrentSystem != null)
		{
			PrepareVehicleWorkers(simStep);
			SyncGroundClutter();
			_vehicleUpdateTask.PrepareFrame(CollectionsMarshal.AsSpan(_orphanVehicles), simStep);
			JobSystems.VehicleSolver.Queue(_vehicleUpdateTask, 1);
			_nextPlayerDeltaTime = dtPlayer;
			_nextSimStep = simStep;
			JobSystems.VehicleSolverProfiler.Prepare();
			JobSystems.VehicleSolver.ExecuteJobs();
		}
	}
```

That step runs `VehicleUpdateTask.Run`, which begins with `ConstraintSim.BeginVehicleUpdate()` and sets `_shapesState`
to `-1`; any `UnlockShapes()` in that time throws. A prefix on `ExecuteNextVehicleSolvers` runs after the previous
step has finished and been applied and before the next is queued, and a `using` inside the prefix disposes the
unlock before the method body starts the job, so the step can never find the registry open. The engine uses the same
window for its own clutter and terrain-block shapes.

## Lifecycle and timing

| Hook | Runs in | Thread | Safe to touch |
|---|---|---|---|
| `Want(...)` from the course builder (`StarMapAfterGui`) | `frame:draw-ui` | main | Only the mod's own volatile snapshot |
| Prefix on `Universe.ExecuteNextVehicleSolvers` | `frame:dispatch-solvers`, inside `Program.PrepareFrame` | main | The shared shapes registry, under `UnlockShapes()` |
| Prefix on `ConstraintSim.DetectCollisions` | `PhysicsBubble.FullPhysicsStepUntil`, `physics:vehicle-solver` | vehicle worker | That bubble's own statics (see [add static colliders](../add-static-colliders-to-physics-bubbles/add-static-colliders-to-physics-bubbles-2.md)) |

Bubbles are stepped in parallel on pool workers, and each `ConstraintSim` belongs to one bubble, so a prefix call owns
the simulation it mutates. The shapes themselves are shared and, once created, only read.
Read-only access to shapes is not guaranteed safe at other times either: copy what you need while the window is open.

## Steps

1. Install a Harmony prefix on `Universe.ExecuteNextVehicleSolvers`. Wrap everything in `try/catch`: a throw here
   would break the main loop. KSAGolf patches four methods in one go; the first is the window:

```csharp
// src/KSAGolf/Ksa/CourseCollider.cs L67-L86
        try
        {
            _clutterStatics = AccessTools.Field(typeof(BubbleClutterStatics), "_statics");
            Type? info = _clutterStatics?.FieldType.GetGenericArguments() is [_, var value] ? value : null;
            ConstructorInfo? make = info?.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault(c => c.GetParameters().Length == 2);
            if (_clutterStatics is null || make is null)
            {
                Log.Warn("kittens walk through courses: KSA's ground clutter no longer has the _statics a course registers in");
                return;
            }

            ParameterInfo[] args = make.GetParameters();
            _solidClutter = make.Invoke([Activator.CreateInstance(args[0].ParameterType), float.PositiveInfinity]);

            _harmony = new Harmony(HarmonyId);
            Patch(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers), nameof(BetweenVehicleSteps));
            Patch(typeof(ConstraintSim), nameof(ConstraintSim.DetectCollisions), nameof(BeforeCollisions));
            Patch(typeof(ConstraintSim), nameof(ConstraintSim.Simulate), nameof(BeforeCollisions));
            Patch(typeof(ConstraintSim), nameof(ConstraintSim.TryResetForPool), nameof(BeforeRecycle));
            Installed = true;
```

2. Stage a wish. The builder (in the GUI hook) records what shapes it wants in a volatile snapshot with a version;
   the prefix compares versions, so it does work only when something changed. In the prefix, open the unlock, make
   a shape per **distinct size** (keyed to 0.1 mm), and publish a second volatile snapshot of boxes plus the shape
   indices:

```csharp
// src/KSAGolf/Ksa/CourseCollider.cs L126-L155
    // Main thread, between vehicle steps: the one moment the shared shape registry can be written.
    private static void BetweenVehicleSteps()
    {
        if (_wanted is not { } wanted || _ready?.Version == wanted.Version) return;

        try
        {
            using ShapesUnlock unlocked = ConstraintSim.UnlockShapes();
            Box[] boxes = [.. wanted.Boxes.Select(b => new Box(b.Centre, ToBepu(b.Orientation), ShapeOf(unlocked.Shapes, b.Size)))];
            _ready = new Ready(wanted.Body, boxes, wanted.Version);
            Log.Info($"course is ground: {boxes.Length} slab(s), {ShapesBySize.Count} shape(s) made so far");
        }
        catch (Exception e)
        {
            Complain($"could not make the course's shapes yet, will try again: {e.GetBaseException().Message}");
        }
    }

    // Shapes are shared by every simulation and never freed: a handful of box sizes a session.
    private static TypedIndex ShapeOf(Shapes shapes, double3 size)
    {
        var key = ((int)Math.Round(size.X * 1e4), (int)Math.Round(size.Y * 1e4), (int)Math.Round(size.Z * 1e4));
        if (!ShapesBySize.TryGetValue(key, out TypedIndex index))
        {
            index = shapes.Add(new BepuPhysics.Collidables.Box((float)size.X, (float)size.Y, (float)size.Z));
            ShapesBySize[key] = index;
        }

        return index;
    }
```

3. If the unlock throws (a step is somehow stepping), log once and try again next frame; the stored wish is not
   cleared until it succeeds. The shapes are never freed: a handful of box sizes a session is fine, arbitrary
   geometry is not (see [approach 2](mutate-the-shared-bepu-shape-registry-2.md) for freeing).
4. Use the shapes from worker prefixes by reading the `Ready` snapshot through a volatile field once per call and
   adding statics with `Simulation.Statics.Add(...)`. That is the subject of
   [add static colliders](../add-static-colliders-to-physics-bubbles/add-static-colliders-to-physics-bubbles-2.md).
5. To read shapes, copy at the window. KSAGolf's launch-site feature opens `UnlockShapes()` in the same prefix and copies
   every launch site's `BigCompound` children by value, each convex child into a generic wrapper whose own
   `RayTest(in RigidPose, origin, direction, ...)` needs neither the registry nor a buffer pool:

```csharp
// src/KSAGolf/Ksa/PadSurface.cs L222-L262
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

    private static Pad[] Copy(Celestial body, Shapes shapes)
    {
        List<Pad> pads = [];
        if (body.BodyTemplate is not { } template) return [];

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
```

   The copy is queried from the mod's per-frame step on the main thread, any time, because it is the mod's own
   data. `BigCompound`'s own ray test walks the registry, which is exactly why the children are copied.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/CourseCollider.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseCollider.cs#L63-L155) | Install, `Want`, `BetweenVehicleSteps`, `ShapeOf` |
| [src/KSAGolf/Ksa/PadSurface.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PadSurface.cs#L222-L300) | Launch-site copy at the window and the query on the copy |
| [src/KSAGolf/Sim/CourseColliders.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/CourseColliders.cs) | Slab maths (KSA-free) |
| [tests/KSAGolf.Tests/CourseCollidersTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/CourseCollidersTests.cs) | Headless tests of the slab maths |

## Variations and gotchas

- Shapes are cached per size and never removed; fine for a few sizes.
- A prefix on a small `Universe` method can be skipped if the JIT inlines it into `PrepareFrame`. Neither mod has seen
  this at the pinned commits, but it is unconfirmed in game (the unscience `PhysicsFrameHook` comment warns about it).
- If the target moves, `Install` logs a warning and patches nothing; the cost is the feature, not a crash.
- The prefix runs once per frame on the main thread; keep it cheap when nothing changed.
- `Shapes.Add` takes a value copy of the shape; a `Mesh` needs `BufferPool` allocations from `unlock.BufferPool`.

## See also

- [Overview and comparison](overview.md)
- [Add and free statics' shapes only while the solver is idle](mutate-the-shared-bepu-shape-registry-2.md), [`UnlockShapesBlocking`](mutate-the-shared-bepu-shape-registry-3.md)
- Mod features: [KSAGolf course colliders as clutter](../../../../mods/ksagolf/features/course-colliders-as-clutter.md), [launch-site surface ray cast](../../../../mods/ksagolf/features/launch-site-surface-raycast.md)
- [Run a command queue in an ExecuteNextVehicleSolvers prefix](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-2.md)

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
