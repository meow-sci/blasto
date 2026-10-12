---
type: How-To
title: Filter one value read through a registry check
description: Insert a filter call after the single StructuralLoad.GLoadFraction getter in PhysicsBubble.DetectStructuralFailure so registered vessels read 0.0 for the destruction comparison only, with a concurrent registry because the detector runs on vehicle workers
category: patching
group: transpilers
topic: write-a-harmony-transpiler
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/kitchen-sink-g-load-protection]
techniques: [harmony-transpiler, il-insert, concurrent-registry]
hooks: [starmap StarMapAllModsLoaded, harmony-transpiler KSA.PhysicsBubble.DetectStructuralFailure]
lifecycle: [startup:all-mods-loaded, physics:vehicle-solver]
game_types: [KSA.PhysicsBubble, KSA.StructuralLoad, KSA.VehicleUpdateState, KSA.Vehicle]
tags: [harmony, transpiler, il, filter, getter, stack, worker-thread]
---

Kitchen Sink's G-load Invincibility panel lets the player exempt chosen vessels from one specific kill: KSA's whole-vehicle G-load verdict.
The mechanism is the fourth transpiler shape in this topic: filter a value that is read from a getter. A transpiler on the private static
`PhysicsBubble.DetectStructuralFailure(VehicleUpdateState)` finds the single call to the `StructuralLoad.GLoadFraction` getter and inserts two
instructions after it, `ldarg.0` and a call to a filter, so the value on the evaluation stack becomes `0.0` for registered vessels. Everything
else stays stock: the telemetry that was stored before the read, the dynamic-pressure check, part failure and contacts.

Prerequisites: a StarMap mod with `0Harmony` and the vocabulary from [Retarget the operand of one call (same signature)](write-a-harmony-transpiler-1.md). Demonstrated by unscience's Kitchen Sink in
[Kitchen Sink G-load protection for exact vessel instances](../../../../mods/unscience/features/kitchen-sink-g-load-protection.md).

## Background: how the game does it

At the end of each bubble step the vehicle worker runs `DetectStructuralFailure` for every vessel. It first writes the step's telemetry into
`UpdateData.NewStructuralLoad`, then evaluates `GLoadFraction >= 1.0` and a separate dynamic-pressure comparison, then creates a destruction event:

```csharp
// current/decomp/KSA/PhysicsBubble.cs L999-L1017
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
```

Source: [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L999-L1017)[^ksa-physicsbubble]

`GLoadFraction` is a computed property: `(MaxGLoad > 0.0) ? (PeakGLoad / MaxGLoad) : 0.0` ([current/decomp/KSA/StructuralLoad.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StructuralLoad.cs#L15)[^ksa-structuralload]). The IL of the read and the
comparison, where `>= 1.0` compiles to `clt.un; ldc.i4.0; ceq`; the filter goes after the getter call at `IL_0092`, before `ldc.r8 1`:

```text
// KSA.dll IL via ilspycmd -il: KSA.PhysicsBubble::DetectStructuralFailure (parameter lists shortened)
IL_0091: ldloc.3
IL_0092: call instance float64 KSA.StructuralLoad::get_GLoadFraction(...)
IL_0097: ldc.r8 1
IL_00a0: clt.un
IL_00a2: ldc.i4.0
IL_00a3: ceq
IL_00a5: stloc.s 4
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)[^ksa-dll]

## Lifecycle and timing

The transpiler runs once at patch time (`startup:all-mods-loaded`, from the supermod's `TryApply`). The filter runs on the vehicle workers
(`physics:vehicle-solver`): `VehicleUpdateTask` dispatches the end-of-step job per bubble in a parallel batch, which reaches
`DetectStructuralFailure` ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1682)). The destruction event is applied on the main thread a frame later, so changing the
comparison input on the worker means no event is ever created and there is nothing to race against. The registry is edited from the UI in
`frame:draw-ui` while the next step is already running on workers, so it must be thread-safe. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Run code inside the physics step on worker threads](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md).

## Steps

1. **Resolve the private method with its exact signature and check its shape.** A rename throws in `Apply`; the supermod's `TryApply` logs it:

```csharp
// kitchen-sink.lib/GLoadProtectionPatches.cs L19-L40
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

public static void Remove(Harmony harmony)
{
    GLoadProtection.Clear();
    if (_original != null) harmony.Unpatch(_original, TranspilerMethod);
    _original = null;
    IsApplied = false;
}
```

Source: [kitchen-sink.lib/GLoadProtectionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/GLoadProtectionPatches.cs#L19-L40)[^unscience-gloadprotectionpatches]

2. **Scan for exactly one call to the getter.** `AccessTools.PropertyGetter(typeof(StructuralLoad), nameof(StructuralLoad.GLoadFraction))`
   returns the getter; throw on zero or on more than one, so a game update that adds a second read (for example a G-based warning inside the
   detector) fails loudly:

```csharp
// kitchen-sink.lib/GLoadProtectionPatches.cs L42-L56
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

```

Source: [kitchen-sink.lib/GLoadProtectionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/GLoadProtectionPatches.cs#L42-L56)

3. **Insert two instructions after the getter.** `ldarg.0` pushes `vehicleState` (the method is static, so `ldarg.0` is its only parameter); the call
   pops the double and the state and pushes the double back. Stack before and after is `[double]`, so the original `ldc.r8 1.0` and compare see the
   filtered value:

```csharp
// kitchen-sink.lib/GLoadProtectionPatches.cs L57-L69
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

Source: [kitchen-sink.lib/GLoadProtectionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/GLoadProtectionPatches.cs#L57-L69)

   | IL after rewrite | Stack effect |
   |---|---|
   | `call StructuralLoad::get_GLoadFraction` (original) | pushes the real fraction |
   | `ldarg.0` (inserted) | pushes `vehicleState` |
   | `call FilterGLoadFraction(double, VehicleUpdateState)` (inserted) | pops both, pushes the real fraction or `0.0` |
   | `ldc.r8 1` and the compare (original) | now sees the filtered value |

4. **Keep telemetry honest.** Because the getter is read only after the telemetry fields are stored, `NewStructuralLoad` keeps the real peak and
   limit; the filter changes only the value on the evaluation stack. With the G flag forced false, the native pressure branch and cause
   classification still run.

5. **Back the filter with a concurrent registry keyed by object identity.** The detector runs on several workers at once while the UI edits the set. A
   `ConcurrentDictionary<Vehicle, byte>` built with `ReferenceEqualityComparer` makes membership identity, never the `Vehicle.Id` string, and
   `Contains` rejects disposed vessels:

```csharp
// kitchen-sink.lib/GLoadProtection.cs L9-L28
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

Source: [kitchen-sink.lib/GLoadProtection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/GLoadProtection.cs#L9-L28)[^unscience-gloadprotection]

6. **Gate the UI on the patch.** `GLoadProtectionPatches.IsApplied` disables the Add button and the save replay when the patch is missing.

## Complete example

| File | Role |
|---|---|
| [kitchen-sink.lib/GLoadProtectionPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/GLoadProtectionPatches.cs) | Apply, Remove, transpiler, filter |
| [kitchen-sink.lib/GLoadProtection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/GLoadProtection.cs) | The concurrent registry |

## Variations and gotchas

- **Scope is deliberately narrow.** Part contact failure, the 200 kPa dynamic-pressure kill, explicit destroy and Bepu contacts are untouched.
- **Semantic drift is not caught by the IL guard.** If KSA moves the G-load decision elsewhere, protection silently stops while the patch still installs.
- **Compared with a prefix and postfix around the same detector** (The Tick), this changes one stack value before the verdict; both can be installed,
  because Harmony runs the transpiled body inside the other mod's prefix and postfix.
- **Debris is a new `Vehicle` and is not protected.** Native debris is already exempt from the G-load verdict but not from pressure.
- **The body was unchanged across several builds** (the authors recorded 5402, 5438, 5482) and is a single read at `PhysicsBubble.cs` L1016 in 5554.

## See also

- [Retarget the operand of one call (same signature)](write-a-harmony-transpiler-1.md), [Insert a call before a validated anchor](write-a-harmony-transpiler-2.md), [Replace an instruction run with a helper that takes a local by reference](write-a-harmony-transpiler-3.md)
- [Writing a Harmony transpiler for a KSA method](overview.md)
- [Kitchen Sink G-load protection for exact vessel instances](../../../../mods/unscience/features/kitchen-sink-g-load-protection.md)
- [Patching game methods that run on worker and pool threads](../../harmony/patch-methods-that-run-on-background-threads/overview.md)
- Related tasks: make-a-vehicle-indestructible, write-worker-thread-safe-solver-patches

[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-structuralload]: KSA.StructuralLoad (decompiled, build 2026.10.10.5554)
[^ksa-dll]: KSA.dll (build 2026.10.10.5554), IL read with ilspycmd
[^unscience-gloadprotectionpatches]: unscience/kitchen-sink.lib/GLoadProtectionPatches.cs
[^unscience-gloadprotection]: unscience/kitchen-sink.lib/GLoadProtection.cs
