---
type: Mod Feature
title: Whole-vessel scaling with Smart and Basic modes and three independent channels
description: Godzilla resizes a vessel or EVA kitten from a captured snapshot in Smart (uniform, spaced about the centre of mass) or Basic (raw XYZ) mode, with native module and collider refresh at the physics handoff and independent render, physics and collider channels
mod: unscience
submod: godzilla
category: vehicles-and-parts
techniques: [harmony-transpiler, reflection, custom-physics]
hooks:
  - harmony-transpiler KSA.Program.PrepareFrame (shared PhysicsFrameHook.Enqueue and BeforePhysics)
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent, requests only)
  - reflection KSA.KittenRenderable._characterAvatar
lifecycle: [frame:prepare, frame:draw-ui]
game_types:
  - KSA.Part
  - KSA.PartTree
  - KSA.Vehicle
  - KSA.ColliderModule
  - KSA.ScaleFactors
  - KSA.ConstraintSim
  - KSA.KittenEva
  - KSA.CharacterAvatar
  - KSA.JobSystems
tags: [scaling, part-scale, physics-handoff, colliders, mass, kitten, ownership, threading]
---

Godzilla makes a vessel (or an EVA kitten) bigger or smaller at runtime and puts it back exactly as it
was. Smart mode scales uniformly and moves parts apart about the centre of mass so the craft keeps its
layout; Basic mode writes one raw XYZ scale to every part and sub-part, leaving part spacing alone.
Two global checkboxes decide whether the resize also changes physics (mass, inertia, aero, nominal
bubble size) and colliders; rendering always resizes. That makes four combinations, from purely
visual giants to visually normal craft with planet-sized collision shapes.[^godzilla-lib-readme]

Mechanism in one line: the UI only enqueues work with `PhysicsFrameHook.Enqueue`; at the handoff a
`VesselScaleSnapshot` captured once per vessel writes part scales and positions, then calls the game's
own refresh chain (`Part.RefreshScale`, `UpdateBounds`, `PartTree.RecomputeAllDerivedData`,
`Vehicle.UpdateAfterPartTreeModification`) and registers the visual or collider overrides that the
other two channels need.

## How it works

**Snapshot** ([godzilla.lib/VesselScaleSnapshot.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VesselScaleSnapshot.cs#L24-L42)). On the first edit of a vessel
the snapshot stores, for every full part and every sub-part (recursively), the original `Part.Scale` and
`PositionParentAsmb`, whether it is a full part, the vessel's `CenterOfMassAsmb` as pivot, and for a
kitten the avatar scalar read through the private `_characterAvatar` field. Every later edit is computed
from these originals, never from the last applied size, so edits do not compound.

**Physics channel** ([godzilla.lib/VesselScaleSnapshot.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VesselScaleSnapshot.cs#L75-L110)):

| Mode | Full parts | Sub-parts |
|---|---|---|
| Smart | `PositionParentAsmb = pivot + (original - pivot) * f`; `Scale = original * f` | Untouched (they inherit through `ScaleTotal`); Basic overrides from a previous edit are undone |
| Basic | Original position; `Scale = (fx, fy, fz)` (absolute, not multiplied) | `Scale = (fx, fy, fz)` too, so nested children compound by design |

Then `Refresh()` ([godzilla.lib/VesselScaleSnapshot.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VesselScaleSnapshot.cs#L138-L148)): reset every part's
cached matrices (the native setter only resets the part it is called on), `RefreshScale()` on full
parts (which pushes `ScaleFactors(ScaleTotal)` into every `IRescale` module, connector and sub-part),
`UpdateBounds()`, `Vehicle.Parts.RecomputeAllDerivedData()`, `Vehicle.UpdateAfterPartTreeModification()`
(mass properties, physics state from the orbit, `UpdateCollisionGeometry`, aero, flight computer),
and flag every `ColliderModule.NeedsColliderUpdate`. Kittens get `Core.Scale = original * f.X` plus the
shared `KittenScalePatches` XYZ correction from Garry's Torch.

**Channel selection** ([godzilla.lib/VesselScaleSnapshot.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VesselScaleSnapshot.cs#L60-L110)):

- Physics off: if a physical edit was applied, restore it first, then register a draw-only factor
  with `VisualScalePatches.SetScale` (see [visual scaling](./godzilla-visual-scaling.md)).
- Physics and colliders differ: also register a `ColliderScaleState` so only collision geometry
  follows the other choice (see [collider scaling](./godzilla-collider-scaling.md)). This requires
  authored `ColliderModule`s; vessels with only KSA's fallback box are rejected before any mutation.
- Both on: the physical path alone is the original Godzilla behaviour.

**Sessions and requests** ([godzilla.lib/GodzillaSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/GodzillaSubmod.cs#L34-L152)). A
`Dictionary<Vehicle, Session>` holds snapshot, mode, factor and channel flags. `RequestApply`,
`RequestRestore` and the channel setters only enqueue closures; `ApplyNow` validates first
(`ValidateApply`: positive finite factor, unchanged topology, patches available), acquires
`VehicleScaleOwnership` (shared with Garry's Torch), then mutates. If a mutation throws, the snapshot is
restored in the same handoff, before the next worker can snapshot the half-edited vessel; if restoring
also fails, the session is kept so "Restore" can retry. A channel toggle re-applies every session.

**Session checks** ([godzilla.lib/GodzillaSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/GodzillaSubmod.cs#L135-L152)). A `BeforePhysics`
subscriber drops sessions of vessels that left the world and restores sessions whose part set or
collider set changed (staging, docking, part failure). Detached pieces keep their current size.

## Lifecycle and timing

| Work | Where | State |
|---|---|---|
| UI: Apply, Restore, channel checkboxes | `[StarMapAfterGui]`, `frame:draw-ui` | Only `PhysicsFrameHook.Enqueue(...)`; the vehicle worker is stepping |
| `ApplyNow` / `Restore` / channel re-apply | Queued actions drained in `PhysicsFrameHook.GetStepAndDispatch`, `frame:prepare`, before `BeforePhysics` callbacks | All solver workers joined, results applied, orbit hover job joined |
| `CheckSessions` | `BeforePhysics` callback, same handoff | Runs before Garry's Torch's weld update |
| Dirty derived part-tree data | `PartTree.FlushDirtyDerived()` two lines later (`Program.cs` L2218) | Recomputed before `ExecuteNextVehicleSolvers` snapshots |
| Unload (`Dispose`) | StarMap unload, outside the frame | Explicitly `Wait()`s on `OrbitSolvers`, `VehicleSolver` and `ClothSolvers` before restoring |

Why this must not run from the UI (checked in the 5554 decomp):

- **Shape registry lock.** `Vehicle.UpdateCollisionGeometry` opens `ConstraintSim.UnlockShapes()`, which
  throws "The shapes registry cannot be mutated while the vehicle update is stepping; stage the change and
  apply it at the frame sync point" while `VehicleUpdateTask.Run` holds the registry
  (`BeginVehicleUpdate` / `EndVehicleUpdate`). `ColliderModule.SetScale` creates and releases owned shapes
  through `UnlockShapesBlocking()`, which would spin the main thread until the step ends.[^ksa-constraint-sim]
- **Module state.** `RefreshScale` rescales mass, tank and collider modules that the worker reads for the
  step it is computing.
- **Lazy derived data.** Since KSA 5464, `RecomputeAllDerivedData()` only marks the tree dirty; the
  handoff is before `FlushDirtyDerived`, so the rebuild still happens on the main thread in the same frame
  rather than lazily on a worker.[^ksa-part-tree]
- **Welds.** Queued actions drain before `BeforePhysics`, so a weld anchored to a part of a freshly
  scaled target reads the new geometry in the same frame.

This is also why `Dispose` waits on the solver job systems itself: unload is not at the handoff.

## Key code

| Path | Role |
|---|---|
| [godzilla.lib/GodzillaSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/GodzillaSubmod.cs) | Sessions, request queueing, rollback, channel toggles, session checks, UI, unload |
| [godzilla.lib/VesselScaleSnapshot.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VesselScaleSnapshot.cs) | Capture, Smart/Basic apply, restore, native refresh chain |
| [godzilla.lib/VesselScaleSnapshot.Colliders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VesselScaleSnapshot.Colliders.cs) | Original hierarchy reconstruction used by the collider channel |
| [godzilla.lib/GodzillaSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/GodzillaSubmod.Persistence.cs) | `godzilla` scene record (restore order 50) |
| [godzilla.lib/VesselScaleSnapshot.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VesselScaleSnapshot.Persistence.cs) | Baseline import so reloads do not compound Smart scaling |
| [ksa-abstractions.lib/VehicleScaleOwnership.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/VehicleScaleOwnership.cs) | Exclusive ownership with Garry's Torch |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L79-L81) | Supermod installs kitten, collider and visual patches |
| [godzilla.tests/Fixture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.tests/Fixture.cs) | Managed KSA stand-ins for the checks |
| [godzilla.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/README.md) | Authors' channel table, limits and save notes |
| [scope/vehicle-physics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/vehicle-physics.md) | Authors' integration scope (Godzilla sections) |

## Snippets

Queue from the UI, mutate at the handoff, roll back on failure:

```csharp
// godzilla.lib/GodzillaSubmod.cs L53-L97
    public void RequestApply(Vehicle vehicle, bool smart, float3 factor)
    {
        if (!WeldScale.IsValid(factor)) { _status = "Each scale must be positive and finite."; return; }
        if (smart) factor = new float3(factor.X);
        _status = $"Applying to {vehicle.Id}…";
        bool physics = ScalePhysics, colliders = ScaleColliders;
        PhysicsFrameHook.Enqueue(() => ApplyNow(vehicle, smart, factor, physics, colliders));
    }
// ...
        try
        {
            snapshot = _sessions.TryGetValue(vehicle, out var session) ? session.Snapshot : new(vehicle);
            snapshot.ValidateApply(factor, physics, colliders);
            mutationStarted = true;
            snapshot.Apply(smart, factor, physics, colliders);
            _sessions[vehicle] = new(snapshot, smart, factor, physics, colliders);
// ...
        catch (Exception ex)
        {
// ...
            // Roll back partial mutations before the next worker can snapshot them.
            try
            {
                snapshot?.Restore();
                _sessions.Remove(vehicle);
                VehicleScaleOwnership.Release(vehicle, Owner);
            }
```

Smart versus Basic writes from the captured originals:

```csharp
// godzilla.lib/VesselScaleSnapshot.cs L89-L109
        VisualScalePatches.ClearScale(Vehicle);
        _physicalApplied = true; // Includes partially applied edits so rollback can restore them.
        // Undo Basic's child scales on a mode switch, but do not reset animation-owned child
        // transforms on repeated Smart edits or Smart restore.
        foreach (var original in _originals)
        {
            if (original.FullPart)
            {
                original.Part.PositionParentAsmb = smart
                    ? _pivot + (original.Position - _pivot) * factor.X : original.Position;
                original.Part.Scale = smart ? original.Scale * factor.X : new double3(factor.X, factor.Y, factor.Z);
            }
            else if (!smart)
                original.Part.Scale = new double3(factor.X, factor.Y, factor.Z);
            else if (_basic)
                original.Part.Scale = original.Scale;
        }
        _basic = !smart;
        SetCharacterScale(smart ? new float3(factor.X) : factor);
        Refresh();
        if (independent) ColliderScalePatches.Set(new(this, scaleColliders, factor));
```

The native refresh chain:

```csharp
// godzilla.lib/VesselScaleSnapshot.cs L138-L148
    private void Refresh()
    {
        // Parent setters do not invalidate descendant world-matrix caches in KSA.
        var parts = CurrentParts();
        foreach (var part in parts) part.ResetCachedPosMatrixValues();
        foreach (var part in Vehicle.Parts.Parts) part.RefreshScale();
        foreach (var part in parts) part.UpdateBounds();
        Vehicle.Parts.RecomputeAllDerivedData();
        Vehicle.UpdateAfterPartTreeModification();
        foreach (var collider in Vehicle.Parts.Modules.Get<ColliderModule>()) collider.NeedsColliderUpdate = true;
    }
```

The engine lock that makes the handoff mandatory (build 5554):

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

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `Part.Scale`, `Part.PositionParentAsmb`, `Part.SubParts`, `Part.ScaleTotal` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L756-L831) | Writes and inheritance |
| `Part.ResetCachedPosMatrixValues`, `Part.UpdateBounds` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1241-L1260) | Cache and bounds refresh |
| `Part.RefreshScale` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1674-L1693) | `ScaleFactors(ScaleTotal)` into `IRescale` modules and connectors |
| `ScaleFactors(double3)` | [current/decomp/KSA/ScaleFactors.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScaleFactors.cs) | Largest-axis collapse for module scaling |
| `PartTree.RecomputeAllDerivedData`, `FlushDirtyDerived` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L475-L519) | Lazy derived rebuild before the snapshot |
| `Vehicle.UpdateAfterPartTreeModification` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1894-L1910) | Mass, physics state, collision geometry, aero, flight computer |
| `Vehicle.UpdateCollisionGeometry` (private) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1944-L1962) | Compound rebuild under `UnlockShapes` |
| `ConstraintSim.BeginVehicleUpdate` / `UnlockShapes` / `UnlockShapesBlocking` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L111-L153) | Shape registry ownership |
| `VehicleUpdateTask.Run` | [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L261-L297) | Holds the registry while stepping |
| `ColliderModule.NeedsColliderUpdate` | [current/decomp/KSA/ColliderModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ColliderModule.cs#L36) | Tells the worker's `UpdateShape` to refresh |
| `JobSystems.OrbitSolvers` / `VehicleSolver` / `ClothSolvers` | [current/decomp/KSA/JobSystems.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/JobSystems.cs) | Waited on at unload |
| `Program.PrepareFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2216-L2221) | Handoff and flush order |

## Pitfalls and version notes

- `ScaleFactors(double3)` keeps only the largest axis, so Basic XYZ physics (mass, colliders) is a
  uniform approximation of the visual shape.[^ksa-scale-factors]
- Basic writes absolute scales to every sub-part; nested children multiply the factor at each level.
  The UI warns that "exaggerated child sizes are intentional".
- Growing a landed vessel can push geometry into terrain. Since KSA 5465 a sleeping body skips terrain
  updates (`ConstraintSim.UpdateTerrainForNextStep` checks `Awake`), so a resting vessel scaled up keeps
  its old terrain patch until something wakes it; the authors list `Vehicle.TakeOffRails()` as possible
  hardening (not implemented).[^ksa-constraint-sim]
- The `ConstraintSim` shape-lock behaviour above is what build 5554 enforces; any code that rebuilds
  collision geometry outside the handoff will throw on the main thread.
- `_characterAvatar` is a string-named private field; a rename breaks kitten scaling at capture time.
- Changing a channel checkbox re-applies every scaled vessel at the next handoff.

## See also

- [Godzilla visual scaling](./godzilla-visual-scaling.md) and [Godzilla collider scaling](./godzilla-collider-scaling.md)
- [Physics-safe vessel mutation at the PrepareFrame solver handoff](./lib-physics-frame-hook.md)
- [Garry's Torch per-axis part scaling](./garrys-torch-per-axis-part-scaling.md) (the transform-only alternative and the ownership partner)
- [I Feel Seen render distance override](./i-feel-seen-render-distance-override.md)
- [Don't Stifle Me editor free scaling](./dont-stifle-me-editor-free-scaling.md)
- [Godzilla sub-mod](../submods/godzilla.md) and [mod overview](../overview.md)
- [Override ColliderModule.SetScale and call UpdateCollisionGeometry](../../../howto/physics/colliders/scale-or-move-a-vessels-colliders-at-runtime/scale-or-move-a-vessels-colliders-at-runtime-1.md) - how-to article demonstrating this feature
- [Run a callback at the PrepareFrame handoff with a GetJobSimStep transpiler](../../../howto/physics/solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-3.md) - how-to article demonstrating this feature
- [Scale a whole vessel with separate render, physics and collider channels](../../../howto/vehicles-and-parts/vehicles/scale-a-vessel-or-its-parts-at-runtime/scale-a-vessel-or-its-parts-at-runtime-1.md) - how-to article demonstrating this feature

[^godzilla-lib-readme]: godzilla.lib/README.md
[^ksa-constraint-sim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-part-tree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-scale-factors]: KSA.ScaleFactors (decompiled, build 2026.10.10.5554)
