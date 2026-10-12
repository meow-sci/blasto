---
type: How-To
title: Add and free statics' shapes only while the solver is idle
description: Build and dispose Bepu Box and two-sided Mesh shapes from UI edits by waiting on the vehicle and cloth solver pools first, with a per-collider triangle budget and a retire-before-free order
category: physics
group: colliders
topic: mutate-the-shared-bepu-shape-registry
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/sphinx-static-colliders, mods/unscience/features/pebbles-clutter-colliders]
techniques: [bepu-physics, solver-wait]
hooks: [starmap ISubmod.Update, harmony-prefix KSA.Universe.ExecuteNextClothSolvers]
lifecycle: [frame:draw-ui, frame:dispatch-solvers, physics:bepu]
game_types:
  - KSA.ConstraintSim
  - KSA.JobSystems
  - KSA.ShapesUnlock
  - BepuPhysics.Collidables.Box
  - BepuPhysics.Collidables.Mesh
tags: [physics, bepu, shapes, registry, wait, mesh, box, ui-driven, retire]
---

Outcome: a user can place, retime, scale or delete collidable models from an ImGui panel and the shapes behind them
are created and freed safely, even though the edit happens in a UI hook while a step is running. unscience's Sphinx
(placed GLB statics) and Pebbles (replacement clutter) both do it by waiting for the solvers before touching the
registry. Prerequisites: StarMap and Harmony (for the consumers). The mechanism is the plain `UnlockShapes()` call;
the discipline is in what comes before and after.

## Background: how the game does it

`UnlockShapes()` throws while `VehicleUpdateTask.Run` holds the registry (see the
[overview](overview.md)).[^ksa-constraintsim] The cloth solver runs on a separate pool but its snapshot reads
clutter collider shapes, so both pools are waited on. The lock states:

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

A held lock is single-threaded: a second thread gets "already unlocked by another thread". Pass the
`ShapesUnlock` you hold into any helper that needs it so nested calls stay on one thread (a depth counter makes
nested calls on the same thread safe).

## Lifecycle and timing

| Work | Thread and moment | Lifecycle |
|---|---|---|
| Build, replace, detach, dispose shapes | Main thread in `ISubmod.Update` after `JobSystems.VehicleSolver.Wait()` and `ClothSolvers.Wait()` | `frame:draw-ui` |
| Pebbles' build of its private clutter graph | Harmony prefix on `Universe.ExecuteNextClothSolvers`, after `Quiesce()` | `frame:dispatch-solvers` |
| Sync of static handles into a bubble | Bubble worker, at the start of each substep | `physics:vehicle-solver`, `physics:bepu` |
| Contact filter, ground checks | Bepu narrow-phase workers | `physics:bepu` |

The waits are mandatory because KSA locks the registry for the whole vehicle update. Each `ConstraintSim` is stepped
by one worker at a time, so a bubble's map is only mutated by its own worker, while main-thread `Detach` runs only
after the waits. Waiting stalls the main thread for the rest of the step, so wait only when there is pending work.
See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Queue edits from the UI; apply them in `Update`, after the waits and only if something is pending:

```csharp
// sphinx.lib/SphinxSubmod.cs L27-L38
    public void Update(double dt)
    {
        if (_disposed) return;
        if (_pending.Count > 0) WaitForPhysics();
        int count = _pending.Count;
        for (int i = 0; i < count; i++) Attempt(_pending.Dequeue());
        var bodies = CelestialProvider.GetAllCelestials();
        foreach (var entry in _entries.Where(e => !bodies.Contains(e.Anchor.Body)).ToArray())
        { WaitForPhysics(); Remove(entry); }
        _scanTime -= dt;
        if (_scanTime <= 0) { _scanTime = 2; RefreshLibrary(); }
    }
```

```csharp
// sphinx.lib/SphinxSubmod.cs L107-L110
    internal static void WaitForPhysics()
    {
        JobSystems.VehicleSolver.Wait(); JobSystems.ClothSolvers.Wait();
    }
```

   `Update` never waits otherwise, so an idle mod costs nothing. Each `Attempt` catches and reports its own failure.
2. Build the shape under `UnlockShapes()`. Choose the cheapest correct shape first: a closed axis-aligned box is a
   fitted `Box`; anything else keeps its triangles as a `Mesh`. A mesh writes every triangle twice, once per
   winding, into a buffer from `unlock.BufferPool`, because Bepu mesh triangles are one-sided and a placed model
   must be solid from either side:

```csharp
// sphinx.lib/StaticCollider.cs L46-L59
        bool fallback = mode == CollisionMode.Auto && count > CollisionGeometry.MaxTriangles;
        bool box = mode == CollisionMode.Box || fallback || (mode == CollisionMode.Auto && CollisionGeometry.IsBox(triangles, min, max));
        using var unlock = ConstraintSim.UnlockShapes();
        if (box)
        {
            Matrix4x4.Decompose(matrix, out _, out var orientation, out _);
            var shape = new Box(Math.Max(.001f, size.X), Math.Max(.001f, size.Y), Math.Max(.001f, size.Z));
            return new StaticCollider { Shape = unlock.Shapes.Add(in shape), Center = center, Rotation = orientation,
                Radius = size.Length() * .5f, Description = fallback
                    ? "Fitted box fallback: over 100,000 triangles; openings are blocked. Simplify the GLB for mesh collision."
                    : mode == CollisionMode.Auto ? "Auto-detected closed box" : "Fitted box (fills openings)" };
        }
        if (count > CollisionGeometry.MaxTriangles)
            throw new ArgumentException("Mesh collision exceeds 100,000 triangles. Choose Auto, Fitted box or simplify the GLB.");
```

```csharp
// sphinx.lib/StaticCollider.cs L60-L79
        var transformed = CollisionGeometry.Transform(triangles, matrix, center);
        if (transformed.Length > remainingTriangles)
            throw new ArgumentException("Sphinx has reached 500,000 collision triangles. Remove statics or use Fitted box.");
        // Bepu triangles are one-sided. Both windings make imported surfaces solid from either side.
        unlock.BufferPool.Take<Triangle>(checked(transformed.Length * 2), out var buffer);
        Mesh native = default;
        bool ownsMesh = false;
        try
        {
            for (int i = 0; i < transformed.Length; i++)
            {
                var t = transformed[i];
                buffer[i*2] = new Triangle(t.A, t.B, t.C);
                buffer[i*2+1] = new Triangle(t.A, t.C, t.B);
            }
            native = new Mesh(buffer, Vector3.One, unlock.BufferPool); ownsMesh = true;
            var handle = unlock.Shapes.Add(in native);
            return new StaticCollider { Shape = handle, Center = center, Radius = size.Length() * .5f,
                TriangleCount = transformed.Length, Description = $"Mesh: {transformed.Length:N0} triangles (two-sided)" };
        }
```

3. Enforce a budget before the registry does: 100,000 triangles per mesh and 500,000 across all entries; over the
   limit, fall back to a fitted box with a visible warning or reject the edit. Reject degenerate inputs (20 km scaled
   diagonal, 100 km centre offset) in the mod, not in Bepu.
4. Retire in the right order. A shape must not be freed while a bubble's worker could still hold a static that uses
   it. Detach the entry from every bubble first, then free the shape:

```csharp
// sphinx.lib/StaticCollider.cs L86-L92
    public void Dispose()
    {
        if (!Shape.Exists) return;
        using var unlock = ConstraintSim.UnlockShapes();
        unlock.Shapes.RemoveAndDispose(Shape, unlock.BufferPool);
        Shape = default;
    }
```

   Removal uses Bepu's default awakening so bodies resting on a removed static wake. Hide, remove, clear, body loss
   and unload all call `SphinxPhysics.Detach` before freeing.
5. Replace without gaps: a transform or collider-mode edit builds the replacement first (inside the budget minus
   this entry), then detaches the entry from every bubble and disposes the old shape, then assigns.
6. For hulls and clutter colliders (Pebbles), do the same but build inside the cloth-solver prefix after a
   `Quiesce()` that also waits for the GPU. `OwnedHull`'s constructor and `CreateShapeInto` use the unlock they are
   given; building one from UI code while the vehicle task runs throws, so build only at a quiesced main-thread
   point. See [give ground clutter custom colliders](../give-ground-clutter-custom-colliders/give-ground-clutter-custom-colliders-1.md).

## Complete example

| File | Role |
|---|---|
| [sphinx.lib/StaticCollider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticCollider.cs#L23-L92) | Global Box/Mesh shape build and disposal |
| [sphinx.lib/SphinxSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxSubmod.cs#L26-L125) | `Update`, `WaitForPhysics`, `SyncCollision`, `BuildCollider` budget |
| [sphinx.lib/CollisionGeometry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/CollisionGeometry.cs) | Closed-box detection, triangle transform, limits |
| [sphinx.lib/SphinxPhysics.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxPhysics.cs) | Per-simulation handle maps and `Detach` |
| [pebbles.lib/Runtime/ClutterController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterController.cs#L256-L270) | `Quiesce`, template rebinding |

## Variations and gotchas

- A shape removed from the registry while a static still references it is a use-after-free in Bepu: detach first.
- `Shapes.Add` for a `Mesh` consumes buffers from the registry's pool; dispose it with `RemoveAndDispose`, not `Remove`.
- If the mod waits for solvers from a UI thread, make sure the call happens on the main thread (the registry is single-threaded).
- Statics are bubble-relative floats; the shape lives forever in the registry until freed, the static only as long as it is added.
- Pebbles' build also waits `Device.WaitIdle()` because the old render objects own buffers that in-flight frames may read.

## See also

- [Overview and comparison](overview.md)
- [Create shapes in an ExecuteNextVehicleSolvers prefix](mutate-the-shared-bepu-shape-registry-1.md), [UnlockShapesBlocking](mutate-the-shared-bepu-shape-registry-3.md)
- Mod features: [Sphinx static colliders](../../../../mods/unscience/features/sphinx-static-colliders.md), [Pebbles clutter colliders](../../../../mods/unscience/features/pebbles-clutter-colliders.md)
- [Add mod-owned static colliders](../add-static-colliders-to-physics-bubbles/overview.md)

[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
