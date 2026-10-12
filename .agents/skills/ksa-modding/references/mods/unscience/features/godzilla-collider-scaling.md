---
type: Mod Feature
title: Collider-only scaling through native owned shapes with nominal bounds preserved
description: Godzilla's collider channel overrides the scale ColliderModule.SetScale receives and the collider centre getter, then rebuilds the collision compound through the private Vehicle.UpdateCollisionGeometry while keeping the nominal physics bounds
mod: unscience
submod: godzilla
category: physics
techniques: [harmony-prefix, harmony-postfix, harmony-finalizer, reflection, bepu-physics]
hooks:
  - harmony-prefix KSA.ColliderModule.SetScale
  - harmony-finalizer KSA.ColliderModule.SetScale
  - harmony-postfix KSA.ColliderModule.PositionVehicleAsmb (getter)
  - harmony-prefix KSA.Vehicle.UpdateCollisionGeometry (private, by name)
  - harmony-finalizer KSA.Vehicle.UpdateCollisionGeometry (private, by name)
  - reflection KSA.Vehicle.UpdateCollisionGeometry (open delegate)
lifecycle: [frame:prepare, physics:vehicle-solver, physics:bepu]
game_types:
  - KSA.ColliderModule
  - KSA.ScaleFactors
  - KSA.Vehicle
  - KSA.Part
  - KSA.ConstraintSim
  - KSA.VehicleProperties
  - KSA.PhysicsBubble
tags: [colliders, bepu, scaling, broad-phase, finalizer, reflection, threading]
---

When Godzilla's two physical checkboxes disagree, collision geometry has to follow one choice while
mass, inertia, aero and the physics bubble follow the other: "collider-only" gives a normal-mass craft
giant collision shapes, "physics-only" gives a heavy giant its original hitboxes. Collision size is
changed, not whether collisions happen, and contacts still push and damage the vessel.[^godzilla-lib-readme]

Mechanism in one line: a per-vessel `ColliderScaleState` answers "what scale and centre should this
collider have", Harmony patches inject those answers into KSA's own `ColliderModule.SetScale` and
`ColliderModule.PositionVehicleAsmb`, and a rebuild through the private `Vehicle.UpdateCollisionGeometry`
regenerates the Bepu compound while the vessel's nominal bounding box, geometric centre and bounding
sphere are put back afterwards.

## How it works

**State** ([godzilla.lib/ColliderScaleState.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/ColliderScaleState.cs)), built from the vessel's
`VesselScaleSnapshot`, the requested factor and which side colliders follow:

| Mode | Shape scale (`GetScale`) | Centre (`GetPosition`) |
|---|---|---|
| Collider-only (physics off, colliders on) | `ScaleFactors(Parent.ScaleTotal).Scale * max(fx, fy, fz)` | Original centre (shape multiplier divided out of `PositionPartAsmb`) scaled per axis about the live `CenterOfMassAsmb`, so centres follow the visual XYZ transform |
| Physics-only (physics on, colliders off) | `ScaleFactors(original ScaleTotal)` reconstructed from the snapshot | `PositionPartAsmb` placed with the original part matrix, rebuilt from captured full-part scales and positions plus live child rotations |

Shapes use KSA's largest-axis approximation: `ScaleFactors(double3)` keeps `max(x, y, z)`. Scales that
are non-finite, non-positive or beyond `float.MaxValue` are rejected before any shape is replaced.

**Patches** ([godzilla.lib/ColliderScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/ColliderScalePatches.cs#L22-L40)):

- `ColliderModule.SetScale(in ScaleFactors)` prefix: for a registered module, save the incoming value
  in `__state` and overwrite the `in` argument with the state's answer; KSA then creates or releases the
  owned Bepu shape itself (`CreateOwnedShape` / `ReleaseOwnedShape`). Finalizer: write the saved value
  back. `Part.RefreshScale` passes one local `ScaleFactors` by reference to every `IRescale` module on the
  part, so without the restore the collider override would leak into the following mass and tank
  modules.[^ksa-part]
- `ColliderModule.PositionVehicleAsmb` getter postfix: return the state's centre.
- `Vehicle.UpdateCollisionGeometry()` (private; found by name) prefix: when the vessel is registered or a
  Godzilla rebuild is in progress (`[ThreadStatic] _preserveBounds`), copy
  `GetPhysicsStatesMutable().Props.{BoundingBoxAsmb, GeometricCenterAsmb, BoundingSphereRadiusBody}`;
  finalizer: write them back, also on exceptions.
- The rebuild itself is invoked through `AccessTools.MethodDelegate<Action<Vehicle>>(rebuild)`, created
  after patching so the open delegate enters the patched method.

**Set and clear** ([godzilla.lib/ColliderScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/ColliderScalePatches.cs#L51-L101)). `Set` registers the
vessel and every collider module, calls `RefreshShapes` (preflight every scale, then `SetScale` and
`NeedsColliderUpdate = true` per module) and rebuilds. `Clear` restores native
`ScaleFactors(module.Parent.ScaleTotal)` only for modules still in the live tree, rebuilds, and only
then forgets the vessel, so a failed native rebuild leaves restoration state for a retry. After every
rebuild all collider flags are set again, because the native compound rebuild clears them.

**Order with the physics channel** ([godzilla.lib/VesselScaleSnapshot.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VesselScaleSnapshot.cs#L75-L110)): clear
old overrides, complete the physical edit or restore first (which establishes the nominal bounds through
the stock `UpdateAfterPartTreeModification`), then `Set` the collider state.

## Lifecycle and timing

| Work | Where | Thread |
|---|---|---|
| `Set` / `Clear` / topology checks | Godzilla's queued actions and `BeforePhysics` callback at the `PhysicsFrameHook` handoff in `Program.PrepareFrame` | Main, `frame:prepare`, no vehicle worker running |
| `SetScale` prefix and finalizer | Inside `RefreshShapes`, `Part.RefreshScale` and native refreshes | Main thread at the handoff |
| `UpdateCollisionGeometry` patches | Inside Godzilla's rebuild and the stock `UpdateAfterPartTreeModification` | Main thread |
| `PositionVehicleAsmb` postfix | Native compound builds, the worker's `ConstraintSim.UpdateShape` -> `Vehicle.UpdateColliderCompound`, the parachute cloth environment snapshot, physics debug drawing | Main thread and vehicle workers (`physics:vehicle-solver`) |

Why the handoff (5554 decomp): `UpdateCollisionGeometry` takes `ConstraintSim.UnlockShapes()`, which
throws while `VehicleUpdateTask.Run` holds the shapes registry, and `SetScale` creates and disposes owned
shapes under `UnlockShapesBlocking()`, which spins until the step ends.[^ksa-constraint-sim] Registrations
are plain `Dictionary` objects: they are only mutated at the handoff, and workers only read them through
the getter postfix during the following step.

How the change reaches Bepu: on the next step the worker's `PushBodyStateToSim` calls
`ConstraintSim.UpdateShape`, which returns true when any collider has `NeedsColliderUpdate`, updates the
compound, and, for a sleeping body, triggers `bodyRef.UpdateBounds()` so the broad phase sees the new size
even though the body did not move. That is why the flags are re-published after every rebuild.[^ksa-constraint-sim]

Why the bounds are preserved: `PhysicsBubble` uses `BoundingSphereRadiusBody` for bubble membership,
envelope radius, atmosphere checks and impact search. Keeping it at the physics-channel value keeps
bubble and terrain-patch coverage tied to mass and aero, as the mode promises.[^ksa-physics-bubble]

## Key code

| Path | Role |
|---|---|
| [godzilla.lib/ColliderScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/ColliderScalePatches.cs) | Patches, registration, rebuild delegate, bounds preservation |
| [godzilla.lib/ColliderScaleState.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/ColliderScaleState.cs) | Per-mode scale and centre answers |
| [godzilla.lib/VesselScaleSnapshot.Colliders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VesselScaleSnapshot.Colliders.cs) | Original scale and part-matrix reconstruction |
| [godzilla.lib/VesselScaleSnapshot.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VesselScaleSnapshot.cs#L60-L110) | Validation (authored colliders required) and channel ordering |
| [godzilla.tests/ColliderScaleChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.tests/ColliderScaleChecks.cs) | Managed checks: four modes, shared readonly input, nominal bounds, dirty flags, retry |
| [godzilla.tests/ColliderFixture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.tests/ColliderFixture.cs) | Native-shaped collider fixture with real Bepu structs |
| [godzilla.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/README.md) | Authors' mode table and collider limits |

## Snippets

Overriding and restoring the shared `in` argument, the centre postfix, and the bounds guard:

```csharp
// godzilla.lib/ColliderScalePatches.cs L113-L148
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

Rebuild through the patched private method, then re-flag the colliders:

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

The native method whose input is overridden (build 5554):

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

The worker-side refresh that the re-published flags trigger:

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

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `ColliderModule.SetScale(in ScaleFactors)` | [current/decomp/KSA/ColliderModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ColliderModule.cs#L48-L59) | Prefix/finalizer; native owned-shape lifecycle |
| `ColliderModule.PositionVehicleAsmb`, `PositionPartAsmb`, `NeedsColliderUpdate` | [current/decomp/KSA/ColliderModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ColliderModule.cs#L30-L44) | Centre override and refresh flag |
| `ColliderTemplate.CreateOwnedShape` | [current/decomp/KSA/ColliderTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ColliderTemplate.cs#L62-L66) | Shape creation under `UnlockShapesBlocking` |
| `Part.RefreshScale` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1674-L1693) | Shares one `ScaleFactors` local across `IRescale` modules |
| `ScaleFactors(double3)` | [current/decomp/KSA/ScaleFactors.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScaleFactors.cs) | Largest-axis collapse |
| `Vehicle.UpdateCollisionGeometry()` (private) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1944-L1962) | Compound rebuild; writes the three preserved props |
| `Vehicle.GetPhysicsStatesMutable()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L922) | Access to `Props` |
| `Vehicle.UpdateColliderCompound` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2028-L2050) | Worker-side reader of `PositionVehicleAsmb` |
| `ConstraintSim.PushBodyStateToSim` / `UpdateShape` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L290-L306) | Broad-phase refresh for sleeping bodies |
| `ConstraintSim.UnlockShapes` / `UnlockShapesBlocking` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L124-L153) | Why edits are staged to the handoff |
| `PhysicsBubble` (`BoundingSphereRadiusBody` users) | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L257-L270) | Bubble membership stays nominal |

## Pitfalls and version notes

- Mixed modes need authored `ColliderModule`s; vessels that rely on KSA's fallback box collider are
  rejected before any mutation (the stock EVA kitten has an authored backpack capsule).
- Large colliders with small nominal bounds can miss contacts across bubbles or outside the generated
  terrain patch, and planet-sized shapes can still overwhelm collision detection.
- XYZ factors produce uniformly scaled shapes (largest axis) at XYZ-scaled centres; unequal axes are not
  exact fits.
- `Vehicle.UpdateCollisionGeometry` is a string-named private target; a rename makes `Apply` throw and the
  channel unavailable. It is present in 5554 at L1944 with the same body shape the authors described.
- Since KSA 5465 sleeping bodies skip terrain updates, so a resting landed vessel whose colliders grow
  keeps its old terrain patch until woken.[^ksa-constraint-sim]
- Detached modules keep their last shape size until their new owner refreshes or disposes them.

## See also

- [Godzilla vessel scaling](./godzilla-vessel-scaling.md) and [Godzilla visual scaling](./godzilla-visual-scaling.md)
- [Scoped collision suppression](./garrys-torch-scoped-collision-suppression.md) (removing shapes during a pass instead)
- [Pebbles clutter colliders](./pebbles-clutter-colliders.md) and [Sphinx static colliders](./sphinx-static-colliders.md)
- [Godzilla sub-mod](../submods/godzilla.md) and [mod overview](../overview.md)
- [Override ColliderModule.SetScale and call UpdateCollisionGeometry](../../../howto/physics/colliders/scale-or-move-a-vessels-colliders-at-runtime/scale-or-move-a-vessels-colliders-at-runtime-1.md) - how-to article demonstrating this feature

[^godzilla-lib-readme]: godzilla.lib/README.md
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-constraint-sim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-physics-bubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
