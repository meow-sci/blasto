---
type: Mod Feature
title: Per-vessel indestructibility without touching shared part templates
description: The Tick makes registered vessels immune to force-based destruction with a prefix on the Part.CrashTolerancePascals getter and a prefix and postfix pair on PhysicsBubble.DetectStructuralFailure that discards only the event the detector just created
mod: unscience
submod: the-tick
category: physics
techniques: [harmony-prefix, harmony-postfix, reflection]
hooks:
  - harmony-prefix KSA.Part.CrashTolerancePascals (getter)
  - harmony-prefix KSA.PhysicsBubble.DetectStructuralFailure (private static, by name)
  - harmony-postfix KSA.PhysicsBubble.DetectStructuralFailure (private static, by name)
  - starmap StarMapBeforeGui (via unscience ISubmod.Update, registry pruning)
lifecycle: [physics:vehicle-solver, physics:bepu, frame:draw-ui]
game_types:
  - KSA.Part
  - KSA.PartTree
  - KSA.PhysicsBubble
  - KSA.VehicleUpdateState
  - KSA.PartFailure
  - KSA.FxDeformation
  - KSA.PartStructuralLimits
  - KSA.VehicleStructuralLimits
  - KSA.VehicleDestructionEvent
  - KSA.Universe
tags: [damage, crash-tolerance, g-load, structural-failure, worker-thread, concurrency, harmony]
---

The Tick ("nigh-invulnerable") lets the player mark individual vessels as indestructible: they still
collide, bounce and spin, and telemetry still reports the loads, but no impact, G-load or aerodynamic
pressure breaks a part or destroys the craft. Other vessels built from the same parts are
unaffected.[^the-tick-readme]

Mechanism in one line: KSA destroys vessels through two independent systems, and The Tick intercepts
each at the point where it reads its limit or emits its verdict, keyed by exact `Vehicle` identity in a
`ConcurrentDictionary` that physics workers can read while the UI edits it.

## How it works

**Registry** ([the-tick.lib/TickProtection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.lib/TickProtection.cs)). A static
`ConcurrentDictionary<Vehicle, byte>` with `ReferenceEqualityComparer`; `Contains` also requires
`!IsDisposed`. `TheTickSubmod.Update` prunes vessels that were disposed or left the current system.
Other mods may call `TickProtection.Add/Remove` while the patches are installed.

**System 1, part contact failure.** On the vehicle worker, `PartFailure.Detect` compares each contacted
part's accumulated contact pressure against `Part.CrashTolerancePascals`, a computed getter that
resolves the template's `CrashTolerance` (or a mass/volume fallback) through
`PartStructuralLimits.ResolveCrashTolerance`.[^ksa-part-failure] The Tick's prefix on that getter
([the-tick.lib/TheTickPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.lib/TheTickPatches.cs#L74-L84)) finds the owner as
`part.Tree?.OwningVehicle ?? part.FullPart.Tree?.OwningVehicle` and, for a protected owner, returns
`double.MaxValue` and skips the native resolution. `double.MaxValue` rather than infinity keeps
`FxDeformation`'s `pressure / tolerance` dent ratio finite (effectively zero), so protected vessels
also stop denting.

Why not raise `PartTemplate.CrashTolerance`: `Part.Template` is one shared `PartTemplate` per part type,
so editing it would harden every vessel using that part and would need restoring on removal.

**System 2, whole-vehicle structural failure.** After `PartFailure.Detect`,
`PhysicsBubble.FullPhysicsEndFrame` calls the private static `DetectStructuralFailure(VehicleUpdateState)`.
It writes the step's `NewStructuralLoad` telemetry (peak G, size-dependent G limit, peak dynamic
pressure, 200 kPa limit) and, if a limit is exceeded and no event exists yet, sets
`vehicleState.DestructionEvent`.[^ksa-physics-bubble] The Tick patches it with a prefix that records
whether an event was already present (`out bool __state`) and a postfix that clears
`DestructionEvent` only when this call created it and `ReadOnlyVehicle` is protected
([the-tick.lib/TheTickPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.lib/TheTickPatches.cs#L86-L96)). The telemetry written before the event survives,
and a destruction event queued by something else earlier is never discarded.

**Installation** ([the-tick.lib/TheTickPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.lib/TheTickPatches.cs#L31-L58)). The getter is resolved with
`AccessTools.PropertyGetter(typeof(Part), nameof(Part.CrashTolerancePascals))` (compile-checked);
the detector by name with an exact parameter list. Both signatures are validated (instance `double`
getter; static `void` detector) and a failure on the second patch unpatches the first. `Remove` clears
the registry so unload restores native behaviour immediately.

## Lifecycle and timing

| Code | Runs on | When |
|---|---|---|
| `CrashTolerancePascals` prefix | Vehicle worker threads (`JobSystems.VehicleWorkerPool` island jobs) | `PartFailure.Detect` in `PhysicsBubble.FullPhysicsEndFrame`; `FxDeformation.ReportContact` from contact-load processing after `ConstraintSim.Simulate`'s Bepu timestep (`physics:bepu`) |
| same | Main thread | `PartContactLoadDebug` rows ("Show Part Contact Load" debug view) |
| `DetectStructuralFailure` prefix/postfix | Vehicle worker, end of each bubble's step | `physics:vehicle-solver` |
| Event application (prevented) | Main thread, next frame | `Universe.ApplyVehicleSolvers` -> `ApplyResultsToMainThread` -> `ApplyRenderEventsToVehicles` -> `VehicleDestructionEvent.Apply` -> `Universe.DestroyVehicleFromEvent` |
| Registry add/remove/prune | Main thread, GUI phase (`frame:draw-ui`) and save replay | Concurrent with the workers |

The decisive detail is that destruction is decided on the worker and applied on the main thread one
frame later. Clearing the event on the worker, in the same call that created it, means the main thread
never sees it; a main-thread approach would have to race `ApplyRenderEventsToVehicles`.[^ksa-physics-bubble]

Both patches run inside the physics step, possibly on several workers at once, so they only do field
reads and a concurrent dictionary lookup: no allocation, no locks, no ImGui. That is also why the registry
is a `ConcurrentDictionary`: the UI can add or remove a vessel at any time without a handoff, and a change
simply takes effect on whichever worker read comes next. Owner lookup works for sub-parts too, because
`PartTree.TransferPart` sets `Tree` on each sub-part as well as the full part.[^ksa-part-tree]

## Key code

| Path | Role |
|---|---|
| [the-tick.lib/TheTickPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.lib/TheTickPatches.cs) | Signature-validated install, the getter prefix, the detector prefix/postfix |
| [the-tick.lib/TickProtection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.lib/TickProtection.cs) | Concurrent identity registry, pruning |
| [the-tick.lib/TheTickSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.lib/TheTickSubmod.cs) | Submod lifecycle, per-frame prune |
| [the-tick.lib/TheTickSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.lib/TheTickSubmod.Saves.cs) | `the-tick` scene record (vehicle ids, restore order 20) |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L91) | Supermod install |
| [plans/VEHICLE_DESTRUCTION_INVESTIGATION.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/plans/VEHICLE_DESTRUCTION_INVESTIGATION.md) | Authors' trace of both destruction systems (against 5402) |
| [the-tick.tests/Program.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.tests/Program.cs) | Managed checks: per-part and sub-part override, threshold matrix, pre-existing events, unpatch |
| [the-tick/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick/README.md) | Player-facing description and design rationale |

## Snippets

The two interceptions:

```csharp
// the-tick.lib/TheTickPatches.cs L74-L96
    /// <summary>Prefix on <c>Part.CrashTolerancePascals</c>: protected owners skip the native resolution.</summary>
    private static bool OverrideCrashTolerance(Part __instance, ref double __result)
    {
        var owner = OwnerOf(__instance);
        if (owner == null || !TickProtection.Contains(owner)) return true;
        __result = TickProtection.UnbreakableTolerancePascals;
        return false;
    }

    private static Vehicle? OwnerOf(Part part) =>
        part.Tree?.OwningVehicle ?? part.FullPart.Tree?.OwningVehicle;

    /// <summary>Prefix: a destruction event queued before the detector ran is never ours to discard.</summary>
    private static void RememberExistingEvent(VehicleUpdateState vehicleState, out bool __state) =>
        __state = vehicleState.DestructionEvent != null;

    /// <summary>Postfix: drop only the event the detector just created for a protected vehicle.</summary>
    private static void SuppressStructuralDestruction(VehicleUpdateState vehicleState, bool __state)
    {
        if (__state || vehicleState.DestructionEvent == null) return;
        if (!TickProtection.Contains(vehicleState.ReadOnlyVehicle)) return;
        vehicleState.DestructionEvent = null;
    }
```

The concurrent registry the workers read:

```csharp
// the-tick.lib/TickProtection.cs L18-L27
    public const double UnbreakableTolerancePascals = double.MaxValue;

    private static readonly ConcurrentDictionary<Vehicle, byte> Vehicles =
        new(ReferenceEqualityComparer.Instance);

    public static Vehicle[] Snapshot() => Vehicles.Keys.ToArray();
    public static bool Contains(Vehicle vehicle) => !vehicle.IsDisposed && Vehicles.ContainsKey(vehicle);
    public static bool Add(Vehicle vehicle) => !vehicle.IsDisposed && Vehicles.TryAdd(vehicle, 0);
    public static bool Remove(Vehicle vehicle) => Vehicles.TryRemove(vehicle, out _);
    public static void Clear() => Vehicles.Clear();
```

The game's detector: telemetry first, event last (build 5554):

```csharp
// current/decomp/KSA/PhysicsBubble.cs L999-L1036
	private static void DetectStructuralFailure(VehicleUpdateState vehicleState)
	{
		if (vehicleState.DestructionEvent == null)
		{
			ref readonly VehicleProperties readOnlyProps = ref vehicleState.GetReadOnlyProps();
			double num = VehicleStructuralLimits.EffectiveMaxGLoad(readOnlyProps.ComputeBoundingSphereRadiusAsmb());
			if (vehicleState.IsKitten)
			{
				num *= 2.5;
			}
			Situation situation = readOnlyProps.Situation;
			ref StructuralLoad newStructuralLoad = ref vehicleState.UpdateData.NewStructuralLoad;
			newStructuralLoad.PeakGLoad = vehicleState.PeakGLoad;
			newStructuralLoad.MaxGLoad = num;
			newStructuralLoad.PeakDynamicPressure = vehicleState.PeakDynamicPressure;
			newStructuralLoad.MaxDynamicPressure = 200000.0;
			newStructuralLoad.IsPressureHydrodynamic = situation.HasOceanContact();
			bool flag = newStructuralLoad.GLoadFraction >= 1.0;
			bool flag2 = newStructuralLoad.DynamicPressureFraction >= 1.0;
			bool flag3 = situation.HasTerrainContact() || situation.HasOceanContact() || vehicleState.HadVehicleContactThisFrame;
			if (flag && flag3 && vehicleState.PartFailureEvent != null)
			{
				flag = false;
			}
			if (flag && vehicleState.IsDebris)
			{
				flag = false;
			}
			if (flag || flag2)
			{
				VehicleDestructionCause cause = ((!situation.HasTerrainContact()) ? (situation.HasOceanContact() ? (flag ? VehicleDestructionCause.OceanImpact : VehicleDestructionCause.HydrodynamicForces) : ((flag && vehicleState.HadVehicleContactThisFrame) ? VehicleDestructionCause.Collision : ((!flag2) ? VehicleDestructionCause.ExcessiveGForce : VehicleDestructionCause.AerodynamicForces))) : VehicleDestructionCause.GroundImpact);
				vehicleState.DestructionEvent = new VehicleDestructionEvent
				{
					Cause = cause,
					PeakGLoad = (float)vehicleState.PeakGLoad,
					PeakDynamicPressure = (float)vehicleState.PeakDynamicPressure
				};
			}
```

The part-failure consumer of the patched getter (build 5554):

```csharp
// current/decomp/KSA/PartFailure.cs L45-L60
	public static void Detect(VehicleUpdateState vehicleState)
	{
		if (vehicleState.DestructionEvent != null || vehicleState.PartFailureEvent != null || vehicleState.IsKitten || vehicleState.GetReadOnlyProps().Situation.IsOnRails())
		{
			return;
		}
		ref BepuHandles bepuHandles = ref vehicleState.BepuHandles;
		Span<Part> contactParts = bepuHandles.ContactParts;
		Span<double> contactAccumulators = bepuHandles.ContactAccumulators;
		List<Part> list = null;
		for (int i = 0; i < contactParts.Length; i++)
		{
			double peakPressure = PartStructuralLimits.AccumulatedPressure(contactAccumulators[i]);
			if (PartStructuralLimits.HasFailed(peakPressure, contactParts[i].CrashTolerancePascals) && !contactParts[i].IsAttachedInternal)
			{
				if (list == null)
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `Part.CrashTolerancePascals` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L858) | Prefix target (computed getter) |
| `Part.Tree`, `Part.FullPart` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L662) | Owner resolution |
| `PartTree.OwningVehicle` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L46) | Owner resolution |
| `PartTree.TransferPart` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1562-L1583) | Sub-parts receive `Tree` too |
| `PhysicsBubble.DetectStructuralFailure(VehicleUpdateState)` (private static) | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L999-L1036) | Prefix/postfix target |
| `PhysicsBubble.FullPhysicsEndFrame` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1655-L1683) | Calls `PartFailure.Detect` then `DetectStructuralFailure` on the worker |
| `PhysicsBubble.ApplyRenderEventsToVehicles` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L919-L970) | Main-thread application of the event |
| `VehicleUpdateState.DestructionEvent`, `ReadOnlyVehicle` | [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L78) | Cleared field; identity |
| `PartFailure.Detect` | [current/decomp/KSA/PartFailure.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartFailure.cs#L45-L60) | Contact-pressure consumer |
| `FxDeformation.ReportContact` | [current/decomp/KSA/FxDeformation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FxDeformation.cs#L318-L335) | Dent ratio consumer |
| `PartStructuralLimits.ResolveCrashTolerance` | [current/decomp/KSA/PartStructuralLimits.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartStructuralLimits.cs#L41-L44) | Native resolution skipped for protected parts |
| `VehicleStructuralLimits.EffectiveMaxGLoad` | [current/decomp/KSA/VehicleStructuralLimits.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleStructuralLimits.cs#L17-L20) | Size-dependent G limit (5 to 50 g) |
| `Universe.DestroyVehicleFromEvent` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1971) | What a surviving event would call |
| `InputEvents.VehicleExplodeData.Apply` | [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L689-L703) | Explicit explode path, not covered |

## Pitfalls and version notes

- Only force-based destruction is covered. Explicit destroy, explode (`InputEvents.VehicleExplodeData`
  calls `Universe.DestroyVehicleFromEvent` directly), recovery and kitten death paths are untouched.[^ksa-input-events]
- Debris shed by other vessels is a new `Vehicle` and is not protected; protection follows object
  identity, not the vessel id string.
- In 5554 `DetectStructuralFailure` also skips the G-load verdict when the vessel is in contact and already
  has a `PartFailureEvent`, and for debris; with The Tick no part-failure event is created for a protected
  vessel, so such a step may create a G-load event, which the postfix then discards.
- The detector is a string-named private method with an exact signature; a rename or signature change
  makes `Apply` throw (logged, other features unaffected). If KSA ever caches `CrashTolerancePascals` per
  part, or moves the destruction decision elsewhere, protection silently stops: recheck both consumers on
  game updates, as the authors note.
- Kitchen Sink's G-load protection transpiles the same detector; the two compose (Harmony applies the
  transpiler to the body and The Tick's prefix/postfix around it).
- Protected vessels show `double.MaxValue` as their rated pressure in the contact-load debug view.

## See also

- [Kitchen Sink G-load protection](./kitchen-sink-g-load-protection.md)
- [Scoped collision suppression](./garrys-torch-scoped-collision-suppression.md) (removing contacts instead)
- [Dent Wizard vessel launcher](./dent-wizard-vessel-launcher.md)
- [The Tick sub-mod](../submods/the-tick.md) and [mod overview](../overview.md)
- [Concurrent registries keyed by reference](../../../howto/physics/solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-3.md) - how-to article demonstrating this feature
- [Protect chosen vessels with a tolerance getter prefix and a detector pair](../../../howto/vehicles-and-parts/vehicles/make-a-vehicle-indestructible/make-a-vehicle-indestructible-2.md) - how-to article demonstrating this feature

[^the-tick-readme]: the-tick/README.md
[^ksa-part-failure]: KSA.PartFailure (decompiled, build 2026.10.10.5554)
[^ksa-physics-bubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-part-tree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-input-events]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
