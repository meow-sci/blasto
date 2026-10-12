---
type: Game Feature
title: Scale or move a vessel's colliders at runtime
description: How a mod resizes collision geometry independently of render and mass, or stows part colliders in the hull and moves or toggles them, by overriding what ColliderModule receives or by writing its part-frame position and NeedsColliderUpdate
category: physics
group: colliders
topic: scale-or-move-a-vessels-colliders-at-runtime
demonstrated_by:
  - mods/unscience/features/godzilla-collider-scaling
  - mods/unscience/features/godzilla-vessel-scaling
  - mods/ksacars/features/scoop-colliders-and-loose-clutter
lifecycle:
  - frame:prepare
  - physics:vehicle-solver
  - physics:bepu
  - startup:asset-load
  - frame:dispatch-solvers
  - frame:draw-ui
game_types: [KSA.ColliderModule, KSA.Vehicle, KSA.ConstraintSim, KSA.Part]
tags: [physics, bepu, colliders, scale, collider-module, compound, hitbox]
---

A vessel's collision shape is a Bepu compound built from its `ColliderModule`s. The modules take their size from
the part scale (`IRescale`) and their position from `PositionPartAsmb`, and the compound is rebuilt on the worker when
any module says `NeedsColliderUpdate`. A mod can steer that in two ways: change the numbers the module is given, so
shape size is independent of render and mass, or write the module's own position and rotation and raise the flag, which
moves or toggles a collider without touching its part.

## Engine background

```csharp
// current/decomp/KSA/ColliderModule.cs L44-L59
	public double3 PositionVehicleAsmb => PositionPartAsmb.Transform(base.Parent.Asmb2VehicleAsmb) + base.Parent.PositionVehicleAsmb;

	public doubleQuat Collider2VehicleAsmb => Collider2PartAsmb.Concatenate(base.Parent.Asmb2VehicleAsmb);

	public void SetScale(in ScaleFactors scale)
	{
		if (!scale.Scale.Equals(_scale))
		{
			_scale = scale.Scale;
			PositionPartAsmb = scale.ScaleOffset(_template.ComputeAuthoredLocationPartAsmb());
			TypedIndex handle = (scale.IsIdentity ? _template.GetShape() : _template.CreateOwnedShape(scale.Scale));
			ReleaseOwnedShape();
			Handle = handle;
			NeedsColliderUpdate = true;
		}
	}
```

| Piece | Role |
|---|---|
| `ColliderModule.SetScale(in ScaleFactors)` | Computes `PositionPartAsmb` from the authored location, creates an owned shape for the scale (or uses the template's shared shape for identity), releases the old one, sets `NeedsColliderUpdate` |
| `ColliderModule.PositionVehicleAsmb`, `Collider2VehicleAsmb` | The pose the compound uses, derived from the part's matrices |
| `Vehicle.UpdateCollisionGeometry` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1944-L1960) | Private; rebuilds the compound and recomputes bounding box, geometric centre and bounding sphere; takes `ConstraintSim.UnlockShapes()` |
| `ConstraintSim.UpdateShape` and `PushBodyStateToSim` [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L290-L306) | On the worker: returns true when any collider has `NeedsColliderUpdate`, updates the compound, and, for a sleeping body, calls `bodyRef.UpdateBounds()` so the broad phase sees the new size |
| `PhysicsBubble` | Uses `BoundingSphereRadiusBody` for bubble membership and envelope; a collider-only change should not change it |

Two timings result. Anything that creates or releases shapes (`SetScale`, `UpdateCollisionGeometry`) needs the shape
registry free ([registry topic](../mutate-the-shared-bepu-shape-registry/overview.md)), so it runs at the PrepareFrame
handoff. Writing `PositionPartAsmb` and the flag is cheap but has a race on the worker if done mid-step. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Trade-off |
|---|---|---|---|
| [1: Override `ColliderModule.SetScale` and call `UpdateCollisionGeometry`](scale-or-move-a-vessels-colliders-at-runtime-1.md) | unscience (Godzilla) | harmony-prefix | Collision size independent of mass and render; patches a string-named private method and keeps bubble bounds unchanged |
| [2: Write `PositionPartAsmb` and `NeedsColliderUpdate`](scale-or-move-a-vessels-colliders-at-runtime-2.md) | KSACars | bepu-physics | No patches, XML-declared boxes stowed in the hull and moved out at runtime; the write races the worker if done outside the dispatch window |

## See also

- [Use ground clutter as colliders](../give-ground-clutter-custom-colliders/overview.md)
- [Mutate vehicles at a physics-safe point](../../solver/mutate-vehicles-at-a-physics-safe-point/overview.md)
- The topics scale-parts-at-runtime and define-and-animate-subparts (other batches) cover the visual and mass side
