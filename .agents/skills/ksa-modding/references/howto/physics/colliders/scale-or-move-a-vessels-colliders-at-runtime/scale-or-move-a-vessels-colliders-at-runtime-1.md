---
type: How-To
title: Override ColliderModule.SetScale and call UpdateCollisionGeometry
description: Make collision shapes follow a different scale from mass and render by injecting an answer into ColliderModule.SetScale and the centre getter, rebuilding through the private Vehicle.UpdateCollisionGeometry at the handoff, and restoring the nominal bounds
category: physics
group: colliders
topic: scale-or-move-a-vessels-colliders-at-runtime
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/godzilla-collider-scaling, mods/unscience/features/godzilla-vessel-scaling]
techniques: [harmony-prefix, harmony-postfix, harmony-finalizer, reflection]
hooks:
  - harmony-prefix KSA.ColliderModule.SetScale
  - harmony-finalizer KSA.ColliderModule.SetScale
  - harmony-postfix KSA.ColliderModule.PositionVehicleAsmb
  - harmony-prefix KSA.Vehicle.UpdateCollisionGeometry
lifecycle: [frame:prepare, physics:vehicle-solver, physics:bepu]
game_types: [KSA.ColliderModule, KSA.Vehicle, KSA.ConstraintSim, KSA.Part, KSA.ScaleFactors]
tags: [physics, bepu, colliders, scale, harmony, godzilla, compound, bounds]
---

Outcome: collision geometry follows one scale while mass, inertia, aero and the physics bubble follow another.
unscience's Godzilla offers two checkboxes: "collider-only" gives a normal-mass craft giant collision shapes,
"physics-only" gives a heavy giant its original hitboxes. Collision size is changed, not whether collisions happen.
Prerequisites: StarMap and Harmony, and an understanding of the handoff window
([callback at the PrepareFrame handoff](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-3.md)).

## Background: how the game does it

`ColliderModule.SetScale` is where a module takes its size. It also recomputes the part-frame position from the authored
location and raises `NeedsColliderUpdate`:

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

`Part.RefreshScale` passes one local `ScaleFactors` by reference to every `IRescale` module on the part, so an
override must be undone afterwards. The rebuild is the private `Vehicle.UpdateCollisionGeometry`, which takes
`ConstraintSim.UnlockShapes()` and, among other things, recomputes the vessel's nominal bounds.[^ksa-vehicle] On
the next step the worker's `PushBodyStateToSim` calls `UpdateShape`, which notices the flags:

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

| Work | Where | Thread |
|---|---|---|
| `Set` / `Clear` / topology checks | Queued actions and a `BeforePhysics` callback at the `PhysicsFrameHook` handoff in `Program.PrepareFrame` | Main, `frame:prepare`, no vehicle worker running |
| `SetScale` prefix and finalizer | Inside `RefreshShapes`, `Part.RefreshScale` and native refreshes | Main thread at the handoff |
| `UpdateCollisionGeometry` patches | Inside the mod's rebuild and the stock `UpdateAfterPartTreeModification` | Main thread |
| `PositionVehicleAsmb` postfix | Native compound builds, the worker's `ConstraintSim.UpdateShape` -> `Vehicle.UpdateColliderCompound`, cloth environment snapshot, debug drawing | Main thread and vehicle workers |

Why the handoff: `UpdateCollisionGeometry` takes the non-blocking `UnlockShapes()`, which throws while the vehicle
update holds the registry, and `SetScale` creates and disposes owned shapes under `UnlockShapesBlocking()`, which spins
until the step ends. Registrations are plain `Dictionary` objects only mutated at the handoff, and workers only read
them through the getter postfix during the following step. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Keep a per-vessel state that answers "what scale and centre should this collider have". Collider-only mode: shape scale =
   `ScaleFactors(Parent.ScaleTotal).Scale * max(fx, fy, fz)` (KSA uses the largest axis), centre = original centre scaled
   per axis about the live `CenterOfMassAsmb`. Physics-only mode: reconstruct the original `ScaleFactors` from a snapshot taken
   before the physical edit. Reject non-finite, non-positive or beyond `float.MaxValue` scales before replacing any shape.
2. Patch `ColliderModule.SetScale`: a prefix that saves the incoming value in `__state` and overwrites the `in` argument
   with the state's answer, so KSA creates or releases the owned shape itself; a finalizer that writes the saved value
   back (or the override leaks into the following mass and tank modules). Patch the `PositionVehicleAsmb` getter
   with a postfix that returns the state's centre:

```csharp
// godzilla.lib/ColliderScalePatches.cs L113-L131
    private static void ScalePrefix(ColliderModule __instance, ref ScaleFactors scale, out ScaleFactors? __state)
    {
        __state = null;
        if (!Modules.TryGetValue(__instance, out var state)) return;
        __state = scale;
        scale = state.GetScale(__instance);
    }

    private static void ScaleFinalizer(ref ScaleFactors scale, ScaleFactors? __state)
    {
        // Part.RefreshScale passes one readonly local to every IRescale module. Restoring it is
        // essential: leaking our collider override would rescale the following mass/fuel modules.
        if (__state.HasValue) scale = __state.Value;
    }

    private static void PositionPostfix(ColliderModule __instance, ref double3 __result)
    {
        if (Modules.TryGetValue(__instance, out var state)) __result = state.GetPosition(__instance);
    }
```

3. Preserve the nominal bounds across the rebuild. A prefix on the private `Vehicle.UpdateCollisionGeometry` (found by name)
   copies `GetPhysicsStatesMutable().Props.{BoundingBoxAsmb, GeometricCenterAsmb, BoundingSphereRadiusBody}`; a finalizer
   writes them back, also on exceptions. Keeping the bounding sphere at the physics-channel value keeps bubble and
   terrain-patch coverage tied to mass and aero:

```csharp
// godzilla.lib/ColliderScalePatches.cs L133-L148
    private static void BoundsPrefix(Vehicle __instance, out Bounds? __state)
    {
        __state = null;
        if (!_preserveBounds && !Vehicles.ContainsKey(__instance)) return;
        var props = __instance.GetPhysicsStatesMutable().Props;
        __state = new(props.BoundingBoxAsmb, props.GeometricCenterAsmb, props.BoundingSphereRadiusBody);
    }

    private static void BoundsFinalizer(Vehicle __instance, Bounds? __state)
    {
        if (__state == null) return;
        ref var props = ref __instance.GetPhysicsStatesMutable().Props;
        props.BoundingBoxAsmb = __state.Box;
        props.GeometricCenterAsmb = __state.Center;
        props.BoundingSphereRadiusBody = __state.Radius;
    }
```

4. Rebuild at the handoff through a delegate created **after** patching, and re-raise the flags, because the native
   compound rebuild clears them and the worker's `UpdateShape` needs them to refresh the broad phase for a stationary body:

```csharp
// godzilla.lib/ColliderScalePatches.cs L93-L101
    private static void Rebuild(Vehicle vehicle)
    {
        bool previous = _preserveBounds;
        try { _preserveBounds = true; _rebuild!(vehicle); }
        finally { _preserveBounds = previous; }
        // Rebuilding clears these flags. Republish them so the worker's UpdateShape returns true
        // and BodyReference.UpdateBounds refreshes the broad phase even for a stationary body.
        foreach (var collider in vehicle.Parts.Modules.Get<ColliderModule>()) collider.NeedsColliderUpdate = true;
    }
```

5. Order against the physics channel: clear old overrides, complete the physical edit or restore first (which establishes
   nominal bounds through the stock `UpdateAfterPartTreeModification`), then set the collider state. `Clear` restores native
   scale only for modules still in the live tree, rebuilds, and only then forgets the vessel, so a failed rebuild keeps
   the restoration state for a retry.

## Complete example

| File | Role |
|---|---|
| [godzilla.lib/ColliderScaleState.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/ColliderScaleState.cs) | Per-vessel scale and centre answers |
| [godzilla.lib/ColliderScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/ColliderScalePatches.cs#L22-L148) | Patches, `Set`/`Clear`, `Rebuild` |
| [godzilla.lib/VesselScaleSnapshot.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VesselScaleSnapshot.cs#L75-L110) | Ordering with the physics channel |

## Variations and gotchas

- Mixed modes need authored `ColliderModule`s; vessels relying on KSA's fallback box collider are rejected before any
  mutation (the stock EVA kitten has an authored backpack capsule).
- Large colliders with small nominal bounds can miss contacts across bubbles or outside the generated terrain patch.
- XYZ factors produce uniformly scaled shapes at XYZ-scaled centres; unequal axes are not exact fits.
- `Vehicle.UpdateCollisionGeometry` is a string-named private target; a rename makes the channel unavailable.
  It is present in 5554 at `Vehicle.cs` L1944.
- Since KSA 5465 sleeping bodies skip terrain updates, so a resting landed vessel whose colliders grow keeps its old
  terrain patch until woken.
- Detached modules keep their last shape size until their new owner refreshes or disposes them.

## See also

- [Overview and comparison](overview.md)
- [Write PositionPartAsmb and NeedsColliderUpdate](scale-or-move-a-vessels-colliders-at-runtime-2.md)
- Mod features: [Godzilla collider scaling](../../../../mods/unscience/features/godzilla-collider-scaling.md), [Godzilla vessel scaling](../../../../mods/unscience/features/godzilla-vessel-scaling.md)
- [Create and free shapes in the shared registry](../mutate-the-shared-bepu-shape-registry/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
