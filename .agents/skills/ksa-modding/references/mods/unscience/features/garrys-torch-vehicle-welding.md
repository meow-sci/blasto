---
type: Mod Feature
title: Part-anchored vehicle welding by per-frame teleport at the physics handoff
description: Garry's Torch holds a source vessel at a fixed offset from a part on a target vessel by teleporting it every frame from a PhysicsFrameHook.BeforePhysics callback stamped with SimStep.PreviousTime
mod: unscience
submod: garrys-torch
category: physics
techniques: [harmony-transpiler, custom-physics, inter-mod-api]
hooks:
  - harmony-transpiler KSA.Program.PrepareFrame (shared PhysicsFrameHook.BeforePhysics)
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent, weld editing UI)
lifecycle: [frame:prepare, frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.Part
  - KSA.Orbit
  - KSA.IParentBody
  - KSA.SimStep
  - KSA.PhysicsBubble
  - KSA.JobSystems
  - KSA.VehicleUpdateTask
tags: [weld, teleport, orbit, part-anchor, physics-handoff, threading, api]
---

Garry's Torch "welds" one vessel (the source) to another (the target): the source is held at a
position and rotation offset relative to a chosen part of the target, follows it through orbit,
landing and robotics motion, and can be scaled and animated. KSA has no joint between separate
vessels, so the weld is kinematic: every frame the source is teleported onto a freshly computed orbit
that puts it at the welded pose.[^garrys-torch-readme]

Mechanism in one line: `GarrysTorchPatches` subscribes to the shared `PhysicsFrameHook.BeforePhysics`
event, and for each weld `WeldEngine.UpdateWeld` reads the target's committed CCI state, composes the
part anchor and offset, builds `Orbit.CreateFromStateCci(..., stateTime, ...)` with
`stateTime = SimStep.PreviousTime`, and calls `Vehicle.Teleport` before the next step is snapshotted.

## How it works

**Weld state** ([garrys-torch.lib/WeldEntry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldEntry.cs)): source, target, optional target `Part`,
`Position` offset (metres), `Rotation` (Euler degrees), XYZ `Scale`, `LockRotation`, `Collisions`
(default off) and `WeldEnabled`.

**Per-frame update** ([garrys-torch.lib/GarrysTorchSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/GarrysTorchSubmod.cs#L74-L91)):

1. Remove welds whose source or target was disposed. Applying the last step's results can destroy
   vessels (crashes, part failure), so this runs before anything touches their parts.
2. Advance weld animations with the player delta (`dt` from the handoff).
3. For each weld, in topological order, call `WeldEngine.UpdateWeld(weld, stateTime)`; a `false`
   return unwelds.
4. In `GarrysTorchPatches.Update`'s `finally`, publish the immutable collision-suppression set for the
   workers (see [scoped collision suppression](./garrys-torch-scoped-collision-suppression.md)).

**Pose computation** ([garrys-torch.lib/WeldEngine.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldEngine.cs#L16-L124)):

- Drop the weld on disposal or when `Source.Parent != Target.Parent` (no cross-body welds); skip a
  frame on NaN target position or velocity or a zero quaternion.
- Anchor: with a target part, `anchor = targetPosCci + (part.PositionVehicleAsmb - target.CenterOfMassAsmb)`
  rotated by the target's `GetBody2Cci()`, orientation `part.Asmb2VehicleAsmb` composed with it.
  Anchoring to a part instead of the vessel centre of mass avoids drift as fuel burns and makes the
  weld follow robotics-moved parts. Without a part (API only) the vessel body frame is used.
- Source position = anchor + `Position` rotated by the anchor orientation; source velocity = target
  velocity.
- `LockRotation`: orientation = Euler offset composed with the anchor, body rates = target's
  `BodyRates`. Unlocked: keep the source's current orientation and body rates (NaN rates reset to 0).
- `Orbit.CreateFromStateCci(Source.Parent, stateTime, pos, vel, Source.Orbit.OrbitLineColor)`, then
  `PhysicsFrameHook.JoinOrbitReaders()`, `Source.Teleport(orbit, body2Cce, bodyRates)` and
  `Source.UpdatePerFrameData()`.

**Chains** ([garrys-torch.lib/WeldEngine.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldEngine.cs#L150-L196)): welds are sorted with Kahn's
algorithm so a vessel that is both a target and a source is placed before its own sources; on a
cycle the order is left unchanged. The list is re-sorted on every create and restore.

**Public API** ([garrys-torch.lib/GarrysTorchSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/GarrysTorchSubmod.cs#L536-L697)) on the static
`GarrysTorchSubmod.Instance`: `CreateWeld` (ids, offsets, `float3` or legacy uniform `float` scale,
lock, optional part, collisions), `FindWeld`, `ModifyWeld` (only non-null fields change),
`RemoveWeld`, `AnimateWeld` and the preset methods. Each source can be welded once; creating a weld
acquires the source's scale ownership and captures its part scales before any edit. The UI requires a
target part; the API accepts none.

## Lifecycle and timing

The update runs inside `Program.PrepareFrame` at the `Universe.GetJobSimStep` call that
[`lib-physics-frame-hook`](./lib-physics-frame-hook.md) rewrites (L2216 in build 5554):[^ksa-program]

| Before the weld update | Weld update | After |
|---|---|---|
| All solver workers joined (L2151-L2161); `ApplyVehicleSolvers` committed every bubble's results (L2167); input applied; nearest-orbit job re-queued (L2196-L2200) | Queued edits (Godzilla) first, then `BeforePhysics` callbacks in subscription order; Garry's Torch subscribes last in the supermod | `ExecuteNextClothSolvers` snapshots chute cloth with the welded pose; `FlushDirtyDerived`; `ExecuteNextVehicleSolvers` snapshots and steps |

Why here, and what earlier placements broke (each checked against the 5554 decomp):

- **Lost actuator progress.** The weld used to run from the UI update, while the vehicle worker was
  stepping. `Teleport` -> `RemoveFromCurrentBubble` -> `PhysicsBubble.RemoveVehicle` took the source
  out of `_vehicleStates`, so the next `ApplyResultsToVehicles` never called
  `UpdateFromTaskResultsUnsynchronized` for it, `PartTree.UpdateFromTaskResults` was skipped, and the
  worker's module progress (for example `KeyframeAnimationModule` `TimeCurrent`) was discarded when
  the next `StateUpdater.Prepare` reset the updater.[^ksa-physics-bubble] At the handoff those results
  are already committed, so robotics on a welded craft keep moving.[^vehicle-physics]
- **Timestamp.** `SimStep.PreviousTime` is `_lastSimStep.NextTime`, the time of the state just
  applied and the start of the step about to run. The authors note the former `NextTime` stamp is
  wrong at this earlier point.[^ksa-universe]
- **Orbit hover race.** The nearest-orbit job queued at L2198 can be reading the source's cached
  orbit points while `Teleport` -> `SetFlightPlanUnsynchronized` disposes them;
  `JoinOrbitReaders()` waits for it once per frame. The authors list this race as a plausible cause
  of long-standing weld error spam.[^vehicle-physics]
- **Re-entry into the step.** The teleported source has no bubble, so `PrepareVehicleWorkers` hands
  its update state over as an orphan and `VehicleUpdateTask.Run` assigns it a bubble on the worker
  before stepping.[^ksa-vehicle-update-task]
- **Ordering with Godzilla.** Godzilla's scale edits are queued with `PhysicsFrameHook.Enqueue`,
  which drains before `BeforePhysics`, so a weld anchored to a part on a freshly scaled target uses
  the refreshed geometry the same frame.

The hook is independent of the GUI phase, so welds keep working with the HUD hidden (F2). Editing a
weld's offsets in the UI only changes `WeldEntry` fields; they take effect at the next handoff. Scale
edits from the UI are applied immediately (see
[per-axis part scaling](./garrys-torch-per-axis-part-scaling.md)).

## Key code

| Path | Role |
|---|---|
| [garrys-torch.lib/GarrysTorchPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/GarrysTorchPatches.cs) | Installs collision patches and the shared handoff; per-frame callback |
| [garrys-torch.lib/WeldEngine.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldEngine.cs) | Pose computation, teleport, topological sort |
| [garrys-torch.lib/GarrysTorchSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/GarrysTorchSubmod.cs) | Weld list, UI, public API, removal and ownership release |
| [garrys-torch.lib/WeldEntry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldEntry.cs) | Weld record |
| [garrys-torch.lib/GarrysTorchSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/GarrysTorchSubmod.Persistence.cs) | Scene-save record (`garrys-torch`, restore order 60) |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L78) | Supermod installs `GarrysTorchPatches` |
| [garrys-torch.tests/GameLoopFixture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.tests/GameLoopFixture.cs) | Managed frame-loop fixture: result retention, one update per frame, `PreviousTime` stamps |
| [garrys-torch/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch/README.md) | Player-facing feature description |
| [garrys-torch.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/README.md) | Authors' runtime integration notes |
| [scope/vehicle-physics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/vehicle-physics.md) | Authors' scope: result-retention fix, timing invariant, 5482 orbit-reader join |

## Snippets

Registration with the shared handoff and the per-frame callback:

```csharp
// garrys-torch.lib/GarrysTorchPatches.cs L10-L44
    public static void Apply(Harmony harmony)
    {
        WeldCollisionPatches.Apply(harmony);
        try
        {
            PhysicsFrameHook.Apply(harmony);
            PhysicsFrameHook.BeforePhysics += Update;
        }
        catch
        {
            WeldCollisionPatches.Remove(harmony);
            throw;
        }
    }

    public static void Remove(Harmony harmony)
    {
        PhysicsFrameHook.BeforePhysics -= Update;
        PhysicsFrameHook.Remove(harmony);
        WeldCollisionPatches.Remove(harmony);
    }

    private static void Update(double dt, UniverseTime stateTime)
    {
        var submod = GarrysTorchSubmod.Instance;
        try
        {
            submod?.UpdateBeforeVehicleSolvers(dt, stateTime);
        }
        finally
        {
            if (submod == null) WeldCollisionPatches.Clear();
            else WeldCollisionPatches.Publish(submod.Welds);
        }
    }
```

Part anchor and offset in CCI:

```csharp
// garrys-torch.lib/WeldEngine.cs L58-L83
        // Determine the anchor position and orientation.
        // When a target part is specified, anchor to that part's live CCI position and orientation.
        // This avoids the CoM drift issue (vehicle CoM shifts as fuel burns) and allows
        // the weld to track robotics-moved parts naturally.
        double3 anchorPosCci;
        doubleQuat anchorBody2Cci;

        if (entry.TargetPart != null)
        {
            // Offset from vehicle CoM to the target part, in vehicle assembly space
            double3 partOffset = entry.TargetPart.PositionVehicleAsmb - entry.Target.CenterOfMassAsmb;
            anchorPosCci = tgtPosCci + partOffset.Transform(tgtBody2Cci);
            // Compose part-in-vehicle rotation with vehicle-to-CCI to get part world orientation
            anchorBody2Cci = doubleQuat.Concatenate(entry.TargetPart.Asmb2VehicleAsmb, tgtBody2Cci).NormalizedOrZero();
            if (anchorBody2Cci == default) anchorBody2Cci = tgtBody2Cci;
        }
        else
        {
            // Legacy path: anchor to vehicle CoM
            anchorPosCci = tgtPosCci;
            anchorBody2Cci = tgtBody2Cci;
        }

        double3 offsetCci = new double3(entry.Position.X, entry.Position.Y, entry.Position.Z).Transform(anchorBody2Cci);
        double3 newSrcPosCci = anchorPosCci + offsetCci;
        double3 newSrcVelCci = tgtVelCci;
```

The teleport, stamped with the committed state time:

```csharp
// garrys-torch.lib/WeldEngine.cs L112-L123
        Orbit newOrbit = Orbit.CreateFromStateCci(
            entry.Source.Parent,
            stateTime,
            newSrcPosCci,
            newSrcVelCci,
            entry.Source.Orbit.OrbitLineColor
        );

        PhysicsFrameHook.JoinOrbitReaders(); // Teleport disposes orbit points the hover job may read.
        entry.Source.Teleport(newOrbit, newSrcBody2Cce, newBodyRates);
        entry.Source.UpdatePerFrameData();
        return true;
```

Why an off-handoff teleport lost results: the commit only visits vessels still in the bubble (game,
build 5554):

```csharp
// current/decomp/KSA/PhysicsBubble.cs L881-L903
	public void ApplyResultsToVehicles()
	{
		if (!IsResultsReady)
		{
			throw new Exception("Cannot apply PhysicsBubble results until they are ready");
		}
		if (_vehicleStates.Count <= 0)
		{
			return;
		}
		Vehicle readOnlyVehicle = _vehicleStates[0].ReadOnlyVehicle;
		SpanOwner<Vehicle> spanOwner = SpanOwner<Vehicle>.Allocate(_vehicleStates.Count);
		try
		{
			Span<Vehicle> span = spanOwner.Span.Slice(0, _vehicleStates.Count);
			for (int i = 0; i < _vehicleStates.Count; i++)
			{
				span[i] = _vehicleStates[i].ReadOnlyVehicle;
			}
			foreach (VehicleUpdateState vehicleState in _vehicleStates)
			{
				vehicleState.ReadOnlyVehicle.UpdateFromTaskResultsUnsynchronized(in vehicleState.UpdateData, in Origin, readOnlyVehicle, span);
			}
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `Vehicle.Teleport(Orbit?, doubleQuat?, double3?)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2241-L2290) | Moves the source; leaves its bubble; overwrites physics state |
| `Vehicle.RemoveFromBubble` / `RemoveFromCurrentBubble` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2312-L2330) | Why timing matters for result application |
| `PhysicsBubble.ApplyResultsToVehicles` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L881-L903) | Result commit per bubble member |
| `Vehicle.GetPositionCci`, `GetVelocityCci`, `GetBody2Cci`, `BodyRates`, `CenterOfMassAsmb`, `Parent`, `Orbit` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2570-L2630) | Target and source state |
| `Part.PositionVehicleAsmb`, `Part.Asmb2VehicleAsmb` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L708-L730) | Part anchor |
| `IParentBody.GetCci2Cce()` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L51) | Orientation into the teleport's CCE frame |
| `Orbit.CreateFromStateCci(...)` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1611) | Orbit at `stateTime` |
| `Program.PrepareFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2140-L2221) | Host of the handoff |
| `Universe.GetJobSimStep` / `SimStep.PreviousTime` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2615-L2627) | Timestamp source |
| `VehicleUpdateTask.Run` (`AssignOrphanVehicles`) | [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L261-L297) | Worker re-homes the teleported source |
| `JobSystems.NearestOrbitAndPerformanceWorker` | [current/decomp/KSA/JobSystems.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/JobSystems.cs#L12) | Joined before teleport |

## Pitfalls and version notes

- A weld is a teleport, not a joint: the source gets the target's centre-of-mass velocity, not the
  rotational velocity at its offset, and within a step it moves ballistically. On a spinning target
  the pose is corrected once per frame.
- Each update is a full `Teleport`: the source leaves its bubble, its tank particle effects are reset,
  physics state is overwritten from the orbit and a planet update is scheduled, every frame.[^ksa-vehicle]
- `Teleport` applies the attitude and rates even when the new trajectory is rejected (it logs
  "Failed to compute trajectory"); the weld does not check for rejection.
- Structural part failure (since 5402) can destroy a source held overlapping its target; source
  collisions are therefore off by default, and destroyed vessels are unwelded at the next handoff.
- A parent-body change (one vessel enters another SOI) unwelds immediately.
- The handoff transpiler is validated strictly; if it fails to install, welds do not run at all
  rather than falling back to the UI.
- 5554 matches the 5482 shapes the authors verified: `Vehicle.Teleport` (L2241), `UpdatePerFrameData`
  (L2645), `Orbit.CreateFromStateCci` (L1611, moved from L1581) and the nearest-orbit queue before the
  handoff.

## See also

- [Physics-safe vessel mutation at the PrepareFrame solver handoff](./lib-physics-frame-hook.md)
- [Per-axis part scaling](./garrys-torch-per-axis-part-scaling.md)
- [Scoped collision suppression](./garrys-torch-scoped-collision-suppression.md)
- [Weld animation and presets](./garrys-torch-weld-animation-and-presets.md)
- [Dent Wizard vessel launcher](./dent-wizard-vessel-launcher.md) and [Kiwi's Marbles celestial welding](./kiwis-marbles-celestial-welding.md)
- [Godzilla vessel scaling](./godzilla-vessel-scaling.md)
- [Garry's Torch sub-mod](../submods/garrys-torch.md) and [mod overview](../overview.md)
- [BeforePhysics callback at the PrepareFrame handoff](../../../howto/physics/kinematics/weld-one-vessel-to-another/weld-one-vessel-to-another-1.md) - how-to article demonstrating this feature
- [Run a callback at the PrepareFrame handoff with a GetJobSimStep transpiler](../../../howto/physics/solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-3.md) - how-to article demonstrating this feature

[^garrys-torch-readme]: garrys-torch/README.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-physics-bubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^vehicle-physics]: scope/vehicle-physics.md
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-vehicle-update-task]: KSA.VehicleUpdateTask (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
