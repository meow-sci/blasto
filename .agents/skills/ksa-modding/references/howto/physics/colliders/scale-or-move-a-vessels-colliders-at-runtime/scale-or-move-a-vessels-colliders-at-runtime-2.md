---
type: How-To
title: Write PositionPartAsmb and NeedsColliderUpdate
description: Declare collider boxes in part XML stowed inside the hull and move or toggle them at runtime by writing the module's part-frame position, rotation and NeedsColliderUpdate, with a subpart shrunk to 0.001 as the visibility switch
category: physics
group: colliders
topic: scale-or-move-a-vessels-colliders-at-runtime
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/scoop-colliders-and-loose-clutter]
techniques: [bepu-physics, xml-asset]
hooks: [starmap StarMapAfterGui]
lifecycle: [startup:asset-load, frame:draw-ui, frame:dispatch-solvers, physics:bepu]
game_types: [KSA.ColliderModule, KSA.Part, KSA.ConstraintSim, KSA.Vehicle]
tags: [physics, bepu, colliders, move, toggle, xml, subpart, scoop, ksacars]
---

Outcome: a vehicle gets a collider that appears and disappears (or changes size and place) on demand. KSACars' Eldorado
carries a bulldozer scoop in three sizes chosen from a panel: the scoop's collider boxes are declared in the part's
XML but stowed inside the hull's own box, and the mod moves them out to the blade at runtime the way KSA's animated
parts move theirs. Prerequisites: StarMap and an XML part asset
(the assets topics in other batches cover part XML).

## Background: how the game does it

`ColliderModule` has public `PositionPartAsmb` and `Collider2PartAsmb` fields and a `NeedsColliderUpdate` flag; the flag is
what KSA itself sets when a collider is rescaled (`ColliderModule.SetScale`) or a keyframe animation moves a part
(`KeyframeAnimationModule`).[^ksa-collidermodule] `ColliderModule.TemplateId` is the `<Box Id>` from XML.
`ConstraintSim.UpdateShape` tests the flag on every collider during `PushBodyStateToSim` on the worker and rebuilds the
vehicle's compound with `Vehicle.UpdateColliderCompound`, which recomputes each child's local pose from
`PositionVehicleAsmb` and `Collider2VehicleAsmb` and clears the flag:

```csharp
// current/decomp/KSA/ConstraintSim.cs L290-L306
	private void PushBodyStateToSim(VehicleUpdateState vehicleState, ReadOnlyPhysicsStates physicsStates, ref BepuHandles handles, ref BodyReference bodyRef)
	{
		bool flag = !bodyRef.Awake;
		float num = (flag ? Vector3.Distance(bodyRef.Pose.Position, physicsStates.Kinematic.PositionPhys.ToBepu()) : 0f);
		bool flag2 = flag && (num > 0f || bodyRef.Pose.Orientation != physicsStates.Kinematic.Body2Phys.ToBepu());
		physicsStates.Kinematic.CopyToBepu(ref bodyRef);
		physicsStates.Props.CopyToBepu(ref bodyRef);
		ReadOnlySpan<ColliderModule> colliders = ReadOnlySpan<ColliderModule>.Empty;
		if (Module<ColliderModule>.TryGetFrom(vehicleState.ReadOnlyVehicle.Parts.Modules, out Module<ColliderModule>.List typeList))
		{
			colliders = typeList.Modules;
		}
		bool flag3 = UpdateShape(in physicsStates.Props, colliders, ref handles);
		if (flag && (flag2 || flag3))
		{
			bodyRef.UpdateBounds();
		}
```

## Lifecycle and timing

| Piece | Where | Thread |
|---|---|---|
| Box declarations | Part XML (`startup:asset-load`) | main |
| `SetScoop` writes | `StarMapAfterGui` (or `StarMapAfterOnFrame` with the UI hidden) -> `StepOnce` -> `Buggies.Sync` | main |
| Blade shrink and grow | same frame, `Buggies.Pose` | main |
| Collider compound rebuilt | vehicle step, `PushBodyStateToSim` -> `UpdateShape` | vehicle-solver worker |

`Buggies.Sync` runs in the GUI-pass hook, after the frame's physics step has already been dispatched, so the writes happen while the
worker may be reading `NeedsColliderUpdate` and `PositionVehicleAsmb`. Nothing synchronises the two threads: a step that
is already rebuilding the compound (the centre of mass moved, for example) can read a half-written position, or clear
the flag the mod has just set and leave a box at its old pose until the next scoop change. This is read from the
code, not observed. The race-free place for the write is a `Vehicle.PrepareWorker` prefix, before the step starts (see
[the PrepareWorker window](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-1.md)).
See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Declare every box the part could ever need under the part's `<Collider>` at a stowed location inside the hull's
   own box, where it touches nothing:

```xml
<!-- src/KSACars/KSACarsGameData.xml L196-L208 -->
      <!--
        The scoop's blade: a flat middle and two wings swept forward. Declared stowed, inside the hull
        box, where they touch nothing; the mod moves them out to the blade while the scoop is on. Stowed
        they sit high, clear of the ground under the hull. The middle one is deep enough to reach back
        to the hull's own box, so a rock cannot get in between the blade and the nose.
      -->
      <Box Id="KSACars_EldoScoopMidCollider">
        <LocationAsmb X="0.75" Y="-0.18" Z="0" />
        <Collider2Asmb X="0" Y="0" Z="0" />
        <LengthX M="0.82" />
        <LengthY M="0.52" />
        <LengthZ M="1.14" />
      </Box>
```

2. Describe each configuration in code: a profile per scoop with the blade subpart suffix, the stowed point and, per
   box, where it goes when on and an optional turn. Boxes too tall to stow upright are declared lying down and turned
   120 degrees about (1,1,1) to swap axes, then swung by a yaw for a swept wing.
3. On change, walk the part's subtree modules, match by `TemplateId`, write the position (and rotation) and raise the flag:

```csharp
// src/KSACars/Ksa/Buggies.cs L313-L335
    // Puts the scoop's colliders out on the blade or back inside the hull, once each time it changes.
    private static void SetScoop(Entry e)
    {
        ScoopProfile[] scoops = e.Drive.Profile.Scoops;
        if (scoops.Length == 0 || e.ScoopSet == e.Scoop) return;
        e.ScoopSet = e.Scoop;

        foreach (ColliderModule collider in e.Part.SubtreeModules.Get<ColliderModule>())
        {
            for (int k = 0; k < scoops.Length; k++)
            {
                foreach (ScoopCollider box in scoops[k].Colliders)
                {
                    if (collider.TemplateId != box.Id) continue;
                    bool on = k == e.Scoop;
                    collider.PositionPartAsmb = on ? box.Deployed : scoops[k].Stowed;
                    if (box.DeployedTurn is { } turn) collider.Collider2PartAsmb = on ? turn : doubleQuat.Identity;
                    collider.NeedsColliderUpdate = true;
                }
            }
        }
        Log.Info($"{KsaWorld.DisplayName(e.Craft)}: {(e.ScoopOn ? scoops[e.Scoop].Name + " on" : "scoop off")}");
    }
```

4. Hide and show the visual separately. A subpart has no hidden switch, so shrink an unused blade to scale 0.001 at its
   stowed point inside the hull and put the chosen one at the origin with scale 1 (`Part.Scale`, then
   `ResetCachedPosMatrixValues`).
5. Do it once per change, not per frame, and ideally from a `PrepareWorker` prefix.

## Complete example

| File | Role |
|---|---|
| [src/KSACars/KSACarsGameData.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsGameData.xml#L196-L260) | The stowed box declarations |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L313-L335) | `SetScoop` |
| [src/KSACars/Sim/BuggyProfile.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/BuggyProfile.cs#L27-L40) | Per-scoop blade and box data |

## Variations and gotchas

- The XXL scoop's boxes are 1.9 m tall and the Mega's 3.3 m, in two tiers, because lying down a tier is the most that fits across the hull.
- A box moved out of the hull overlaps nothing until deployed; stowed boxes sit high, clear of the ground under the hull.
- The colliders move with the vehicle's compound, so the centre of mass and inertia are not recomputed from them.
- A rock actually being pushed has not been seen in game (see [clutter](../give-ground-clutter-custom-colliders/give-ground-clutter-custom-colliders-3.md)).

## See also

- [Overview and comparison](overview.md)
- [Override SetScale](scale-or-move-a-vessels-colliders-at-runtime-1.md)
- Mod feature: [KSACars scoop colliders and loose clutter](../../../../mods/ksacars/features/scoop-colliders-and-loose-clutter.md)

[^ksa-collidermodule]: KSA.ColliderModule (decompiled, build 2026.10.10.5554)
