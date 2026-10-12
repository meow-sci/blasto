---
type: How-To
title: Concurrent registries keyed by reference
description: Let a UI edit a set of vehicles while worker-thread patches read it, using a ConcurrentDictionary with reference equality, a disposed check on read and a per-frame prune on the main thread
category: physics
group: solver
topic: write-worker-thread-safe-solver-patches
approach: 3
difficulty: intermediate
prerequisites: [harmony]
demonstrated_by:
  - mods/unscience/features/kitchen-sink-g-load-protection
  - mods/unscience/features/the-tick-vessel-indestructibility
techniques: [reflection, harmony-transpiler, harmony-prefix, harmony-postfix]
hooks:
  - harmony-transpiler KSA.PhysicsBubble.DetectStructuralFailure
  - harmony-prefix KSA.Part.CrashTolerancePascals
  - harmony-postfix KSA.PhysicsBubble.DetectStructuralFailure
lifecycle: [physics:vehicle-solver, frame:draw-ui, frame:dispatch-solvers]
game_types:
  - KSA.PhysicsBubble
  - KSA.Vehicle
  - KSA.VehicleUpdateState
  - KSA.Part
  - KSA.StructuralLoad
  - KSA.VehicleUpdateTask
tags: [physics, worker-thread, concurrentdictionary, identity, vehicle, g-load, destruction]
---

Outcome: the player picks vessels in an ImGui panel at any time and a patch on a worker thread honours the
choice on its very next call, with no handoff, no lock and no allocation on the hot path. Prerequisite:
Harmony. unscience's Kitchen Sink exempts chosen vessels from KSA's whole-vehicle G-load kill; The Tick makes
chosen vessels indestructible by intercepting the part and whole-vehicle failure systems.

## Background: how the game does it

Destruction is decided on the worker and applied on the main thread a frame later. At the end of each bubble
step the worker runs `PartFailure.Detect` and then `PhysicsBubble.DetectStructuralFailure`
[current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1010-L1030), which writes the step's `NewStructuralLoad` telemetry and then compares
the G-load fraction with 1.0:

```csharp
// current/decomp/KSA/PhysicsBubble.cs L1010-L1021
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
```

The resulting `VehicleDestructionEvent` is applied at the next frame's `Universe.ApplyVehicleSolvers` (via
`ApplyRenderEventsToVehicles`). Changing the comparison input on the worker means no event is ever created,
so there is nothing to race on the main thread and no flag to restore.[^ksa-physicsbubble]

## Lifecycle and timing

| Code | Thread | When |
|---|---|---|
| `FilterGLoadFraction` (inside the transpiled detector) | vehicle worker | `physics:vehicle-solver`, end of each bubble's step; several workers at once |
| `Part.CrashTolerancePascals` prefix (The Tick) | workers, and the main thread for debug rows | `PartFailure.Detect` and contact-load processing after the Bepu timestep (`physics:bepu`) |
| Registry Add, Delete | main | ImGui panel, `frame:draw-ui`, while the next step is already running on workers |
| `Prune` | main | Once per frame from the submod update (`[StarMapBeforeGui]` or the hidden-HUD replay) |
| Save capture, reset, replay | main | At the deferred world-change handoff in `PrepareFrame`, before the solvers are dispatched |

The UI edits the set during `frame:draw-ui`, after `ExecuteNextVehicleSolvers` started the step, so a
concurrent collection is the simplest correct structure: readers are lock-free, nothing allocates on read, and
a change simply applies to whichever worker read comes next.

## Steps

1. Key the registry by object identity, not by `Vehicle.Id`. A new vessel that reuses a protected name must not
   be protected, and `Contains` must reject a disposed vessel even before the next prune:

```csharp
// kitchen-sink.lib/GLoadProtection.cs L8-L28
/// <summary>Live vehicle identities shared by the UI and physics workers; save replay rebinds them.</summary>
internal static class GLoadProtection
{
    private static readonly ConcurrentDictionary<Vehicle, byte> Vehicles =
        new(ReferenceEqualityComparer.Instance);

    public static Vehicle[] Snapshot() => Vehicles.Keys.ToArray();
    public static bool Contains(Vehicle vehicle) => !vehicle.IsDisposed && Vehicles.ContainsKey(vehicle);
    public static bool Add(Vehicle vehicle) => !vehicle.IsDisposed && Vehicles.TryAdd(vehicle, 0);
    public static bool Remove(Vehicle vehicle) => Vehicles.TryRemove(vehicle, out _);
    public static void Clear() => Vehicles.Clear();

    public static void Prune(IEnumerable<Vehicle> liveVehicles)
    {
        if (Vehicles.IsEmpty) return;
        var live = new HashSet<Vehicle>(liveVehicles, ReferenceEqualityComparer.Instance);
        foreach (var vehicle in Vehicles.Keys)
            if (vehicle.IsDisposed || !live.Contains(vehicle))
                Remove(vehicle);
    }
}
```

2. Prune on the main thread every frame, against the live vehicle list including debris (a vessel that split
   or left the system drops out of the registry). The Tick does it from its submod `Update`:

```csharp
// the-tick.lib/TheTickSubmod.cs L18
    public void Update(double dt) => TickProtection.Prune(VehicleProvider.GetAllVehicles(includeDebris: true));
```

3. Rewrite the single read the decision depends on, not the whole detector. Kitchen Sink's transpiler finds the
   one call to the `GLoadFraction` getter, throws on zero or several matches, and inserts a call that returns
   `0.0` for registered vessels. The telemetry fields were stored earlier, so the HUD still shows the real G:

```csharp
// kitchen-sink.lib/GLoadProtectionPatches.cs L42-L69
    internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        var getter = AccessTools.PropertyGetter(typeof(StructuralLoad), nameof(StructuralLoad.GLoadFraction));
        int match = -1;
        for (int i = 0; i < code.Count; i++)
        {
            if (!code[i].Calls(getter)) continue;
            if (match >= 0)
                throw new InvalidOperationException("KSA structural-failure detector has multiple G-load comparisons.");
            match = i;
        }
        if (match < 0)
            throw new InvalidOperationException("KSA structural-failure detector G-load comparison was not found.");

        // Change only the value consumed by the destruction decision, preserving
        // telemetry, pressure checks, part damage and flight-computer limits.
        code.InsertRange(match + 1, new[]
        {
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(GLoadProtectionPatches), nameof(FilterGLoadFraction)))
        });
        return code;
    }

    private static double FilterGLoadFraction(double fraction, VehicleUpdateState state) =>
        GLoadProtection.Contains(state.ReadOnlyVehicle) ? 0.0 : fraction;
```

4. For a getter that does not receive the vehicle, find the owner through the object itself. The Tick's prefix on
   `Part.CrashTolerancePascals` resolves the owner through the part tree and returns `double.MaxValue` (not
   infinity, so the dent ratio stays finite). It never edits the shared `PartTemplate`, which would harden
   every vessel that uses that part:

```csharp
// the-tick.lib/TheTickPatches.cs L74-L84
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
```

5. When an event may already exist, undo only what you caused. The Tick's prefix records whether a destruction
   event was present, and its postfix clears the event only if this call created it and the vessel is
   protected, so another mod's event is never discarded:

```csharp
// the-tick.lib/TheTickPatches.cs L86-L96
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

6. Validate private targets at install and roll back a half install. Both mods resolve the private static detector
   by name and exact parameter list, check it is static and `void`, and unpatch the first patch if the second
   fails. In the log, expect the mod's install line; a game change shows the `InvalidOperationException` text
   ("G-load comparison was not found") and the feature disables itself.

## Complete example

| File | Role |
|---|---|
| [kitchen-sink.lib/GLoadProtectionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/GLoadProtectionPatches.cs) | Signature-checked install, single-site transpiler, worker filter |
| [kitchen-sink.lib/GLoadProtection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/GLoadProtection.cs) | Concurrent identity registry and pruning |
| [the-tick.lib/TheTickPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.lib/TheTickPatches.cs) | Getter prefix and detector prefix/postfix |
| [the-tick.lib/TickProtection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.lib/TickProtection.cs) | Registry used by The Tick |

## Variations and gotchas

- `ConcurrentDictionary.ContainsKey` is lock-free; adds and removes take internal locks and must come from
  the main thread (UI or prune), not from the worker.
- A save load replaces every `Vehicle`. Drop the registry on reset and rebind saved ids to the reconstructed
  objects afterwards; capture throws if an id does not round-trip to the same object, rather than writing an
  id that cannot be restored uniquely.
- The `DestructionEvent` field is on the worker's `VehicleUpdateState`; clearing it in the same call that
  created it is the only race-free place.
- `Part.CrashTolerancePascals` is also read on the main thread by the contact-load debug view, so the prefix
  must be safe on both.
- After the veto, the native pressure branch and cause classification still run: a protected vessel over
  200 kPa dies of pressure, not G-load.
- Hold the registry for identity, not state. Anything that must be read as a plain value belongs in an
  immutable record swapped by reference (see [approach 6](write-worker-thread-safe-solver-patches-6.md)).

## See also

- [Overview and comparison](overview.md)
- [Approach 2: read-only tables](write-worker-thread-safe-solver-patches-2.md), [approach 4: volatile snapshot handoff](write-worker-thread-safe-solver-patches-4.md)
- Mod features: [Kitchen Sink G-load protection](../../../../mods/unscience/features/kitchen-sink-g-load-protection.md), [The Tick vessel indestructibility](../../../../mods/unscience/features/the-tick-vessel-indestructibility.md)
- [Safe vehicle destruction and part failure from a frame hook](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-4.md) for the main-thread side of destruction

[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
