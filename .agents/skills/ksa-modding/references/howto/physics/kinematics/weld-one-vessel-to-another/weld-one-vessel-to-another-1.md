---
type: How-To
title: BeforePhysics callback at the PrepareFrame handoff
description: Teleport the welded source onto a freshly computed orbit at the PrepareFrame handoff, stamped with SimStep.PreviousTime, composing the part anchor and offset in Cci, ordering welds topologically and suppressing contacts between welded vessels
category: physics
group: kinematics
topic: weld-one-vessel-to-another
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/unscience/features/garrys-torch-vehicle-welding
  - mods/unscience/features/garrys-torch-scoped-collision-suppression
  - mods/unscience/features/garrys-torch-weld-animation-and-presets
techniques: [harmony-transpiler, kinematic-teleport]
hooks: [harmony-transpiler KSA.Program.PrepareFrame, harmony-prefix KSA.ConstraintSim.DetectCollisions]
lifecycle: [frame:prepare]
game_types: [KSA.Vehicle, KSA.Orbit, KSA.SimStep, KSA.Universe]
tags: [physics, kinematics, weld, teleport, handoff, orbit, topological-sort, unscience, garrys-torch]
---

Outcome: one vessel (the source) is held at a position and rotation offset relative to a chosen part of another (the target),
follows it through orbit, landing and robotics motion, and can be scaled and animated. unscience's Garry's Torch does it.
Prerequisites: StarMap and Harmony. The shared handoff hook is the subject of
[mutate approach 3](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-3.md); this article
is the weld that runs inside it.

## Background: how the game does it

The update runs in `Program.PrepareFrame` at the `Universe.GetJobSimStep` call that the hook rewrites (`Program.cs` L2216 in
build 5554). At that point every solver worker is joined, `ApplyVehicleSolvers` has committed every bubble's results, input is
applied and the nearest-orbit job is re-queued. A teleport made earlier in the frame, from the UI, while the worker was stepping,
took the source out of `_vehicleStates`, so the next `ApplyResultsToVehicles` never called `UpdateFromTaskResultsUnsynchronized` for
it and the worker's module progress (for example `KeyframeAnimationModule.TimeCurrent`) was discarded:

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

At the handoff those results are already committed, so robotics on a welded craft keep moving. The timestamp is
`SimStep.PreviousTime`, the time of the state just applied and the start of the step about to run.[^ksa-universe]

## Lifecycle and timing

| Before the weld update | Weld update | After |
|---|---|---|
| Solvers joined; `ApplyVehicleSolvers` committed; input applied; nearest-orbit job re-queued | Queued edits first, then `BeforePhysics` callbacks in subscription order | `ExecuteNextClothSolvers`, `FlushDirty*`, `ExecuteNextVehicleSolvers` (`PrepareVehicleWorkers` snapshots every vessel) |

- **Orbit hover race.** The nearest-orbit job queued before the handoff can be reading the source's cached orbit points while
  `Teleport` -> `SetFlightPlanUnsynchronized` disposes them; `PhysicsFrameHook.JoinOrbitReaders()` waits for it once per frame.
- **Re-entry into the step.** The teleported source has no bubble, so `PrepareVehicleWorkers` hands its update state over as an orphan
  and `VehicleUpdateTask.Run` assigns it a bubble on the worker before stepping.
- **HUD.** The hook is independent of the GUI phase, so welds keep working with the HUD hidden. Editing a weld's offsets in
  the UI only changes fields; they take effect at the next handoff.

See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Register with the shared hook once and publish the collision-suppression set in a `finally`, so a throw cannot leave stale
   suppression:

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

2. Per frame: remove welds whose source or target was disposed (applying the last step's results can destroy vessels), advance
   animations (the eased offset, rotation and scale queues are the subject of [approach 3](weld-one-vessel-to-another-3.md) and the
   [weld animation and presets feature](../../../../mods/unscience/features/garrys-torch-weld-animation-and-presets.md)), then update each weld in topological order. Kahn's algorithm orders welds so a vessel that is both a target and a source
   is placed before its own sources; on a cycle the order is left unchanged.
3. Compute the pose in Cci. With a target part, anchor to the part's live pose instead of the vessel centre of mass: this avoids
   drift as fuel burns and makes the weld follow robotics-moved parts:

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

4. Source position = anchor + `Position` rotated by the anchor orientation; source velocity = target velocity. With
   `LockRotation`, orientation = Euler offset composed with the anchor and body rates = the target's; unlocked, keep the
   source's own orientation and rates (NaN rates reset to 0). Drop the weld if the parents differ (no cross-body welds), and skip a
   frame on NaN target state.
5. Build the orbit at `stateTime`, join the orbit readers, teleport and refresh:

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

6. Suppress contacts between welded vessels: they are held overlapping, which otherwise gives violent contact impulses, dents
   and structural part failure. See [suppress collisions for one vessel](../../colliders/suppress-collisions-for-one-vessel/suppress-collisions-for-one-vessel.md).
   The set is published here and read by workers.

## Complete example

| File | Role |
|---|---|
| [garrys-torch.lib/WeldEngine.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldEngine.cs#L16-L196) | `UpdateWeld`, anchor and offset, topological sort |
| [garrys-torch.lib/GarrysTorchPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/GarrysTorchPatches.cs#L10-L44) | Handoff registration and publish |
| [garrys-torch.lib/GarrysTorchSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/GarrysTorchSubmod.cs#L74-L91) | Per-frame update order |
| [garrys-torch.lib/WeldCollisionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldCollisionPatches.cs#L19-L104) | Scoped collision suppression |
| [garrys-torch.lib/WeldEntry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldEntry.cs) | Weld state |

## Variations and gotchas

- A weld is a teleport, not a joint: the source gets the target's centre-of-mass velocity, not the rotational velocity at its
  offset, and within a step it moves ballistically. On a spinning target the pose is corrected once per frame.
- Each update is a full `Teleport`: the source leaves its bubble, its tank particle effects are reset, and a planet update
  is scheduled, every frame.
- `Teleport` applies the attitude and rates even when the new trajectory is rejected (it logs "Failed to compute trajectory");
  the weld does not check for rejection.
- Structural part failure can destroy a source held overlapping its target; collisions are off by default and destroyed vessels
  are unwelded at the next handoff.
- A parent-body change unwelds immediately.
- If the handoff transpiler fails to install, welds do not run at all rather than falling back to the UI.
- 5554 matches the shapes the authors verified: `Vehicle.Teleport` (L2241), `Orbit.CreateFromStateCci` (L1611).

## See also

- [Overview and comparison](overview.md)
- [Wait then teleport stamped with the next step](weld-one-vessel-to-another-2.md), [animation and presets](weld-one-vessel-to-another-3.md)
- Mod features: [Garry's Torch vehicle welding](../../../../mods/unscience/features/garrys-torch-vehicle-welding.md), [scoped collision suppression](../../../../mods/unscience/features/garrys-torch-scoped-collision-suppression.md), [weld animation and presets](../../../../mods/unscience/features/garrys-torch-weld-animation-and-presets.md)

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
