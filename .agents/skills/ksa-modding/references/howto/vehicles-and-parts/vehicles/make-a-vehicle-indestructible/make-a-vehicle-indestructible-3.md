---
type: How-To
title: Filter only the g-load read with a guarded transpiler and persist it by vessel identity
description: Insert a static filter after the single StructuralLoad.GLoadFraction getter call in DetectStructuralFailure, and rebind the per-vessel setting after a save load without ambiguity
category: vehicles-and-parts
group: vehicles
topic: make-a-vehicle-indestructible
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/kitchen-sink-g-load-protection]
techniques: [harmony-transpiler, save-sidecar]
hooks: [harmony-transpiler KSA.PhysicsBubble.DetectStructuralFailure]
lifecycle: [physics:vehicle-solver, frame:apply-solvers, frame:draw-ui, event:save, event:load]
game_types: [KSA.PhysicsBubble, KSA.StructuralLoad, KSA.VehicleUpdateState, KSA.Vehicle, KSA.GameSave]
tags: [transpiler, g-load, persistence, identity, workers]
---
Outcome: selected vessels are exempt from exactly one kill, KSA's whole-vehicle g-load verdict. Bepu still
collides them, every part still breaks at its own crash tolerance, dynamic pressure still destroys them and
the HUD load readout still shows the real g. unscience's Kitchen Sink built it for multi-vessel machines (an
axle riding in a cradle) that were destroyed by the g spike of their own contacts. The second half of this
article is the general recipe for keeping a per-vessel setting across saves.

Prerequisites: StarMap, Harmony transpilers. Demonstrated by [unscience/kitchen-sink-g-load-protection](../../../../mods/unscience/features/kitchen-sink-g-load-protection.md).

## Background: how the game does it

At the end of each bubble step `DetectStructuralFailure` writes telemetry into `UpdateData.NewStructuralLoad`,
then evaluates `newStructuralLoad.GLoadFraction >= 1.0` and a separate `DynamicPressureFraction >= 1.0`,
creating a `VehicleDestructionEvent` whose cause derives from both flags and the contact situation
[current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L999-L1038)[^ksa-physicsbubble]. The g limit is `VehicleStructuralLimits.EffectiveMaxGLoad(radius)`, times 2.5
for kittens; part crash tolerance plays no role in it. The getter is read exactly once in the method, after the
telemetry fields are stored, so a filter on that one value changes only the comparison.

## Lifecycle and timing

| Code | Thread | When |
|---|---|---|
| `FilterGLoadFraction` (inside the transpiled detector) | vehicle worker | `physics:vehicle-solver`, per bubble end step |
| event application (prevented) | main, next frame | `ApplyVehicleSolvers` -> `ApplyRenderEventsToVehicles` |
| registry `Prune`, add, delete | main | `frame:draw-ui` ImGui panel and `StarMapBeforeGui` |
| save capture / reset / replay | main | native save transaction; reset and replay at the deferred world-change handoff in `PrepareFrame`, before the next solvers are dispatched |

Changing the comparison input on the worker means no event is ever created, so nothing races on the main
thread and no flag needs restoring. The registry is concurrent for the same reason as in article 2.
See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md).

## Steps

1. Write the transpiler to require exactly one call to the getter and throw otherwise, so a game update that
   changes the method shape disables the feature instead of corrupting it. Insert two instructions after the
   call: `ldarg.0` (the `VehicleUpdateState`) and a call to your filter:

```csharp
// kitchen-sink.lib/GLoadProtectionPatches.cs L42-69
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

2. Resolve the private target by name with its exact parameter list and check it is static and `void`:

```csharp
// kitchen-sink.lib/GLoadProtectionPatches.cs L19-32
    public static void Apply(Harmony harmony)
    {
        if (IsApplied) return;
        var original = AccessTools.Method(typeof(PhysicsBubble), "DetectStructuralFailure",
            new[] { typeof(VehicleUpdateState) })
            ?? throw new MissingMethodException(typeof(PhysicsBubble).FullName, "DetectStructuralFailure");
        if (!original.IsStatic || original.ReturnType != typeof(void))
            throw new InvalidOperationException("Unexpected KSA structural-failure detector signature.");

        harmony.Patch(original, transpiler: new HarmonyMethod(TranspilerMethod));
        _original = original;
        IsApplied = true;
        Console.WriteLine("kitchen-sink: G-load protection patch applied.");
    }
```

3. Key the registry on object identity (`ReferenceEqualityComparer`), never on `Vehicle.Id`:

```csharp
// kitchen-sink.lib/GLoadProtection.cs L9-28
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

4. Persist by stable id and rebind after load. Capture refuses to write an id that does not round-trip
   through `VehicleProvider.FindVehicle` to the same object (zero or several matches), validates the list, and
   sorts ids ordinally; replay resolves each id again and warns on missing or ambiguous ones. A vanilla save
   restores with no protection:

```csharp
// kitchen-sink.lib/KitchenSinkSubmod.Saves.cs L24-59
        new SaveParticipant<string[]>("kitchen-sink-g-load", CaptureGLoadVehicles,
            ResetGLoadProtection, (state, context) =>
            {
                context.Require(state.Length == 0 || GLoadProtectionPatches.IsApplied,
                    "G-load protection patch is unavailable; saved protection could not be restored.");
                foreach (string id in state)
                {
                    var vehicle = VehicleProvider.FindVehicle(id);
                    if (vehicle == null || vehicle.IsDisposed)
                    {
                        context.Warn($"Missing or ambiguous G-load protection vehicle '{id}'.");
                        continue;
                    }
                    GLoadProtection.Add(vehicle);
                }
            }, order: 20, validate: ValidateGLoadVehicles)
    };

    private static string[] CaptureGLoadVehicles()
    {
        var vehicles = GLoadProtection.Snapshot().Where(vehicle => !vehicle.IsDisposed).ToArray();
        // Avoid producing an apparently valid save that cannot uniquely restore its targets.
        foreach (var vehicle in vehicles)
            if (!ReferenceEquals(vehicle, VehicleProvider.FindVehicle(vehicle.Id)))
                throw new InvalidOperationException($"G-load protection vehicle '{vehicle.Id}' is missing or ambiguous.");
        var ids = vehicles.Select(vehicle => vehicle.Id).Order(StringComparer.Ordinal).ToArray();
        ValidateGLoadVehicles(ids);
        return ids;
    }

    private static void ValidateGLoadVehicles(string[] ids)
    {
        if (ids.Length > 10000 || ids.Any(string.IsNullOrWhiteSpace)
            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new InvalidOperationException("Invalid saved G-load protection vehicles.");
    }
```

   The `SaveParticipant<T>` type is unscience's `ksa-abstractions.lib` persistence layer (see
   `choose-where-a-mod-stores-its-files`); any sidecar file keyed on the vessel id with the same
   "must round-trip" rule works.
5. Gate the UI's Add button on `GLoadProtectionPatches.IsApplied`, so users cannot register vessels when the
   patch failed.

## Complete example

- [kitchen-sink.lib/GLoadProtectionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/GLoadProtectionPatches.cs)[^unscience-gloadprotectionpatches], [kitchen-sink.lib/GLoadProtection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/GLoadProtection.cs)[^unscience-gloadprotection]
- [kitchen-sink.lib/KitchenSinkSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/KitchenSinkSubmod.Saves.cs#L24-L59)[^unscience-kitchensinksubmod-saves] - persistence
- [kitchen-sink.lib/KitchenSinkSubmod.GLoadProtection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/KitchenSinkSubmod.GLoadProtection.cs)[^unscience-kitchensinksubmod-gloadprotection] - the picker UI holding a `Vehicle` object, not an index

## Variations and gotchas

- **What stays stock.** With the g flag forced false, the native pressure branch and cause classification still
  run: a protected vessel over 200 kPa still dies as `GroundImpact`, `HydrodynamicForces` or `AerodynamicForces`.
- **Telemetry stays honest.** The filter changes a value on the IL evaluation stack only; the saved
  `NewStructuralLoad` keeps the real peak and limit.
- **Hold the selection as an object**, and drop it when the vessel leaves the list; indices move when vehicles
  are removed.
- **Ids can collide.** If two vehicles share an id (a mod spawned a duplicate), capture throws rather than save
  something that cannot be restored uniquely.
- **Updates.** The transpiler fails loudly (zero or several getter calls). Combine with article 2 if you also
  want part failure suppressed.

## See also

- [Overview](overview.md), [Article 2](make-a-vehicle-indestructible-2.md), [Article 4](make-a-vehicle-indestructible-4.md)
- [unscience/kitchen-sink-g-load-protection](../../../../mods/unscience/features/kitchen-sink-g-load-protection.md)
- [write-worker-thread-safe-solver-patches](../../../physics/solver/write-worker-thread-safe-solver-patches/overview.md), `persist-vessel-references-across-save-and-load`

[^unscience-gloadprotectionpatches]: unscience: kitchen-sink.lib/GLoadProtectionPatches.cs
[^unscience-gloadprotection]: unscience: kitchen-sink.lib/GLoadProtection.cs
[^ksa-physicsbubble]: current/decomp/KSA/PhysicsBubble.cs (decompiled, build 2026.10.10.5554)
[^unscience-kitchensinksubmod-gloadprotection]: unscience: kitchen-sink.lib/KitchenSinkSubmod.GLoadProtection.cs
[^unscience-kitchensinksubmod-saves]: unscience: kitchen-sink.lib/KitchenSinkSubmod.Saves.cs
