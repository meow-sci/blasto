---
type: How-To
title: Per-bubble static handles from a BeginStaticObjectPass postfix
description: Add a Bepu Box or two-sided Mesh static to every nearby surface bubble at the start of each substep, and transpile the stock contact filter so only those handles in that exact simulation count as ground
category: physics
group: colliders
topic: add-static-colliders-to-physics-bubbles
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/unscience/features/sphinx-static-colliders
  - mods/unscience/features/sphinx-glb-statics-placement
techniques: [harmony-postfix, harmony-transpiler, bepu-physics]
hooks:
  - harmony-postfix KSA.ConstraintSim.BeginStaticObjectPass
  - harmony-postfix KSA.ConstraintSim.UpdateSimForSnappedOrigin
  - harmony-postfix KSA.ConstraintSim.IsGroundSurfaceFor
  - harmony-transpiler KSA.NarrowPhaseCallbacks.AllowContactGeneration
  - harmony-prefix KSA.ConstraintSim.TryResetForPool
lifecycle: [physics:vehicle-solver, physics:bepu, frame:draw-ui]
game_types:
  - KSA.ConstraintSim
  - KSA.NarrowPhaseCallbacks
  - KSA.PhysicsBubble
  - BepuPhysics.Statics
  - BepuPhysics.Collidables.Box
  - BepuPhysics.Collidables.Mesh
tags: [physics, bepu, statics, colliders, harmony, transpiler, contact-filter, box-vs-mesh, unscience]
---

Outcome: placed models are solid. Vessels can land on a roof, EVA kittens can walk through an imported doorway.
unscience's Sphinx does this: in Auto mode a model that is exactly a closed axis-aligned box gets a cheap fitted
`Box`; anything else keeps its triangles as a two-sided Bepu `Mesh`, so openings stay open. Prerequisites: StarMap and
Harmony. The shapes themselves are made on the main thread with the solvers idle
([registry approach 2](../mutate-the-shared-bepu-shape-registry/mutate-the-shared-bepu-shape-registry-2.md));
this article is about getting them into a bubble's simulation and past the contact filter.

## Background: how the game does it

Stock `AllowContactGeneration` lets a body collide with a static only if the static is a clutter static, a terrain
block, or `BepuHandles.IsGroundSurface` (the vehicle's own terrain patch or `StaticObjectCollider`); any other static
never generates contacts:[^ksa-narrowphasecallbacks]

```csharp
// current/decomp/KSA/NarrowPhaseCallbacks.cs L24-L57
	public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
	{
		if (a.Mobility != CollidableMobility.Dynamic && b.Mobility != CollidableMobility.Dynamic)
		{
			return false;
		}
		CollidableReference collidableReference;
		CollidableReference collidableReference2;
		if (a.Mobility == CollidableMobility.Static)
		{
			collidableReference = a;
			collidableReference2 = b;
		}
		else
		{
			if (b.Mobility != CollidableMobility.Static)
			{
				return true;
			}
			collidableReference2 = a;
			collidableReference = b;
		}
		BubbleClutterStatics clutterStatics = Sim.ClutterStatics;
		if (clutterStatics != null && clutterStatics.IsClutterStatic(collidableReference.StaticHandle))
		{
			return true;
		}
		bool flag = Sim.TerrainBlocks?.IsTerrainBlock(collidableReference.StaticHandle) ?? false;
		if (!Sim.HandleToState.TryGetValue(collidableReference2.BodyHandle, out VehicleUpdateState value))
		{
			return Sim.ClutterStatics?.IsClutterBody(collidableReference2.BodyHandle) ?? false;
		}
		return flag || value.BepuHandles.IsGroundSurface(collidableReference.StaticHandle);
	}
```

`ConstraintSim.IsGroundSurfaceFor` repeats the same three tests, and is what kitten ground friction and terrain-contact
bookkeeping ask.[^ksa-constraintsim] So a mod needs both: statics in the bubble, and both checks taught about
them. `BeginStaticObjectPass` runs on the bubble's worker at the start of each contact pass of a constrained substep (and when the Bepu states are populated from the kinematic state), which is the natural point to
bring the bubble's statics up to date.

## Lifecycle and timing

| Work | Thread and moment | Lifecycle |
|---|---|---|
| Shape build, replace, dispose | Main thread in `ISubmod.Update` after solver waits | `frame:draw-ui` |
| `Sync` (add, move, remove handles) | Bubble worker, start of each substep and after origin snaps | `physics:vehicle-solver`, `physics:bepu` |
| Contact filter, ground checks | Bepu narrow-phase workers | `physics:bepu` |
| Handle cleanup | `TryResetForPool` / `Dispose` prefixes | `physics:vehicle-solver` |

Each `ConstraintSim` is stepped by one worker at a time, so a bubble's map is only mutated by its own worker.
Narrow-phase callbacks only read the handle sets. Running `Sync` every substep keeps poses correct across origin
snaps and vessels moving in and out of range without a separate timer, and it is cheap when nothing changed.
See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Patch five `ConstraintSim` methods and one `NarrowPhaseCallbacks` method. `BeginStaticObjectPass` and
   `UpdateSimForSnappedOrigin` get a postfix running `Sync`; `IsGroundSurfaceFor` a postfix OR-ing in ownership;
   `TryResetForPool` and `Dispose` a prefix that drops the bubble's handles before KSA clears or reuses the
   simulation; `AllowContactGeneration` a transpiler:

```csharp
// sphinx.lib/SphinxPhysicsPatches.cs L15-L32
    public static void Apply(Harmony harmony)
    {
        Patch(typeof(ConstraintSim), nameof(ConstraintSim.BeginStaticObjectPass), nameof(Sync));
        Patch(typeof(ConstraintSim), nameof(ConstraintSim.UpdateSimForSnappedOrigin), nameof(Sync));
        Patch(typeof(ConstraintSim), nameof(ConstraintSim.IsGroundSurfaceFor), nameof(Ground));
        Patch(typeof(ConstraintSim), nameof(ConstraintSim.TryResetForPool), nameof(Clear), prefix: true);
        Patch(typeof(ConstraintSim), nameof(ConstraintSim.Dispose), nameof(Clear), prefix: true);
        var callback = typeof(ConstraintSim).Assembly.GetType("KSA.NarrowPhaseCallbacks", throwOnError: true)!;
        var filter = AccessTools.Method(callback, "AllowContactGeneration",
            new[] { typeof(int), typeof(CollidableReference), typeof(CollidableReference), typeof(float).MakeByRefType() })
            ?? throw new MissingMethodException(callback.FullName, "AllowContactGeneration");
        harmony.Patch(filter, transpiler: new HarmonyMethod(typeof(SphinxPhysicsPatches), nameof(ContactFilter)));

        void Patch(Type type, string name, string hook, bool prefix = false)
        {
            var target = AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);
            var method = new HarmonyMethod(typeof(SphinxPhysicsPatches), hook);
            harmony.Patch(target, prefix: prefix ? method : null, postfix: prefix ? null : method);
```

```csharp
// sphinx.lib/SphinxPhysicsPatches.cs L46-L49
    private static void Sync(ConstraintSim __instance) => SphinxSubmod.Instance?.SyncCollision(__instance);
    private static void Clear(ConstraintSim __instance) => SphinxPhysics.Clear(__instance);
    private static void Ground(ConstraintSim __instance, StaticHandle handle, ref bool __result)
        => __result |= SphinxPhysics.Owns(__instance, handle);
```

2. Choose the shape conservatively. `IsBox` classifies every vertex as one of the eight bounds corners within a
   relative tolerance of 1e-5; any vertex that is not a corner, any degenerate triangle, or any triangle not lying on
   one face fails; each face must hold exactly two distinct triangles that share the face diagonal, not an edge. A
   missing face or an interior vertex keeps mesh collision:

```csharp
// sphinx.lib/CollisionGeometry.cs L15-L42
    public static bool IsBox(IReadOnlyList<CollisionTriangle> triangles, Vector3 min, Vector3 max)
    {
        var size = max - min;
        if (size.X <= 0 || size.Y <= 0 || size.Z <= 0) return false;
        var tolerance = Vector3.Max(size * .00001f, new Vector3(.000001f));
        var faces = Enumerable.Range(0, 6).Select(_ => new HashSet<int>()).ToArray();
        foreach (var triangle in triangles)
        {
            int a = Corner(triangle.A), b = Corner(triangle.B), c = Corner(triangle.C);
            if (a < 0 || b < 0 || c < 0 || a == b || b == c || a == c) return false;
            int face = -1;
            for (int axis = 0; axis < 3; axis++)
                if ((a & (1 << axis)) == (b & (1 << axis)) && (a & (1 << axis)) == (c & (1 << axis)))
                    face = axis * 2 + ((a >> axis) & 1);
            if (face < 0) return false;
            faces[face].Add((1 << a) | (1 << b) | (1 << c));
            if (faces[face].Count > 2) return false;
        }
        foreach (var face in faces)
        {
            if (face.Count != 2) return false;
            var pair = face.ToArray();
            int shared = pair[0] & pair[1];
            var corners = Enumerable.Range(0, 8).Where(i => (shared & (1 << i)) != 0).ToArray();
            // Two triangles must share the face diagonal, not an edge (which could overlap).
            if (corners.Length != 2 || BitOperations.PopCount((uint)(corners[0] ^ corners[1])) != 2) return false;
        }
        return true;
```

3. Sync per bubble. Map each simulation to its handles in a `ConcurrentDictionary<ConstraintSim, Bubble>`; read the
   bubble origin from any vessel in `HandleToState`; a bubble whose frame is not `BubbleFrame.Ccf` is cleared. Compute
   the pose in Ccf, subtract the double-precision `origin.PositionBub` **before** converting to Bepu floats, and keep
   the entry only if some bubble vessel is within collider radius + 2 km. Add with `Statics.Add` or move with
   `ApplyDescription`, compare pose and shape first so an idle pass is cheap, and remove statics for entries no longer wanted:

```csharp
// sphinx.lib/SphinxPhysics.cs L24-L64
    public static void Sync(ConstraintSim sim, IReadOnlyList<SphinxEntry> entries)
    {
        if (entries.Count == 0) { Clear(sim); return; }
        var state = sim.HandleToState.Values.FirstOrDefault();
        if (state == null || state.Origin.BubFrame != BubbleFrame.Ccf) { Clear(sim); return; }
        var origin = state.Origin;
        var bubble = Bubbles.GetOrAdd(sim, _ => new Bubble());
        var wanted = new HashSet<SphinxEntry>();
        foreach (var entry in entries)
        {
            if (!entry.Visible || entry.Collider is not { } collider || !ReferenceEquals(entry.Anchor.Body, origin.Parent)) continue;
            var frame = GroundPlacement.FrameCcf(entry.Anchor, entry.Align);
            var center = collider.Center;
            var localCenter = new double3(center.X, center.Y, center.Z);
            var orientation = doubleQuat.CreateFromRotationMatrix(frame);
            var position = entry.Anchor.PositionCcf + localCenter.Transform(orientation);
            // Include actual collider reach; a large building's anchor can be far from its nearest wall.
            if (!sim.HandleToState.Values.Any(v =>
                (position - origin.PositionBub - v.GetReadOnlyStates().Kinematic.PositionPhys).Length() <= collider.Radius + 2000)) continue;
            wanted.Add(entry);
            var r = collider.Rotation;
            var localRotation = new doubleQuat(r.X, r.Y, r.Z, r.W);
            var description = new StaticDescription
            {
                Pose = new RigidPose((position - origin.PositionBub).ToBepu(), localRotation.Concatenate(orientation).ToBepu()),
                Shape = collider.Shape
            };
            if (bubble.Entries.TryGetValue(entry, out var handle))
            {
                var current = sim.Simulation.Statics[handle];
                if (current.Pose.Position != description.Pose.Position || current.Pose.Orientation != description.Pose.Orientation || current.Shape != description.Shape)
                    sim.Simulation.Statics.ApplyDescription(handle, in description);
            }
            else
            {
                handle = sim.Simulation.Statics.Add(in description);
                bubble.Entries.Add(entry, handle); bubble.Handles.Add(handle);
            }
        }
        foreach (var entry in bubble.Entries.Keys.Where(e => !wanted.Contains(e)).ToArray()) Remove(sim, bubble, entry);
    }
```

4. Transpile the filter. Replace the single stock `BepuHandles.IsGroundSurface` call with one that also checks your
   handle set, scoped to the exact simulation running the callback by loading the callback struct's own `Sim` field
   (no thread-local state, no per-contact reflection). Throw unless exactly one call site was replaced, so a game update
   that moves the call disables the hook loudly:

```csharp
// sphinx.lib/SphinxPhysicsPatches.cs L51-L71
    private static IEnumerable<CodeInstruction> ContactFilter(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
    {
        var original = AccessTools.Method(typeof(BepuHandles), nameof(BepuHandles.IsGroundSurface));
        var replacement = AccessTools.Method(typeof(SphinxPhysicsPatches), nameof(IsSurface));
        var sim = AccessTools.Field(__originalMethod.DeclaringType, "Sim")
            ?? throw new MissingFieldException("KSA.NarrowPhaseCallbacks", "Sim");
        int replaced = 0;
        foreach (var instruction in input)
        {
            if (!instruction.Calls(original)) { yield return instruction; continue; }
            var load = new CodeInstruction(OpCodes.Ldarg_0);
            load.MoveLabelsFrom(instruction); load.MoveBlocksFrom(instruction);
            yield return load;
            yield return new CodeInstruction(OpCodes.Ldfld, sim);
            yield return new CodeInstruction(OpCodes.Call, replacement);
            replaced++;
        }
        if (replaced != 1) throw new InvalidOperationException("Sphinx expected one stock ground filter call; collision hooks need a game-update review.");
    }
    private static bool IsSurface(ref BepuHandles handles, StaticHandle handle, ConstraintSim sim)
        => handles.IsGroundSurface(handle) || SphinxPhysics.Owns(sim, handle);
```

5. Place the model with the same matrix as the visible one (`PlacementMath.GroundedLocal`) relative to the
   transformed bounds centre so coordinates stay small, and check it: the managed tests compare 200 random XYZ
   transforms for collider and render agreement.
6. Retire safely: detach from every bubble before freeing the shape (see the registry article).

## Complete example

| File | Role |
|---|---|
| [sphinx.lib/CollisionGeometry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/CollisionGeometry.cs) | Closed-box detection, triangle transform, limits |
| [sphinx.lib/StaticCollider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticCollider.cs) | Shape build and disposal |
| [sphinx.lib/SphinxPhysics.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxPhysics.cs) | Per-simulation handle maps, `Sync`, `Owns`, `Detach`, `Clear` |
| [sphinx.lib/SphinxPhysicsPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxPhysicsPatches.cs) | The five patches and the contact-filter transpiler |
| [scope/statics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/statics.md) | The authors' physics contract and per-build verification |

## Variations and gotchas

- Removal uses Bepu's default awakening, so bodies resting on a removed static wake.
- Texture alpha never cuts holes; collision follows triangles.
- Limits: 100,000 triangles per entry, 500,000 total, 20 km scaled diagonal, 100 km local-centre offset.
- The transpiler throws if its target changed; the patch set must be reviewed on game updates.
- The filter check is scoped per simulation, so a handle in one bubble does not become ground in another.
- Statics are not added at the main-thread sync point because each bubble's simulation is rented lazily inside the
  step and bubbles merge and split inside `VehicleUpdateTask.Run`.

## See also

- [Overview and comparison](overview.md)
- [Worker prefixes with statics registered as ground clutter](add-static-colliders-to-physics-bubbles-2.md)
- Mod features: [Sphinx static colliders](../../../../mods/unscience/features/sphinx-static-colliders.md), [Sphinx GLB statics placement](../../../../mods/unscience/features/sphinx-glb-statics-placement.md)
- [Create and free shapes in the shared registry](../mutate-the-shared-bepu-shape-registry/overview.md)

[^ksa-narrowphasecallbacks]: KSA.NarrowPhaseCallbacks (decompiled, build 2026.10.10.5554)
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
