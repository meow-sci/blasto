---
type: How-To
title: Protect chosen vessels with a tolerance getter prefix and a detector pair
description: Keep a worker-safe registry of vessels and patch Part.CrashTolerancePascals and PhysicsBubble.DetectStructuralFailure so only those vessels cannot break, with no template mutation
category: vehicles-and-parts
group: vehicles
topic: make-a-vehicle-indestructible
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/the-tick-vessel-indestructibility]
techniques: [harmony-prefix, harmony-postfix]
hooks:
  - harmony-prefix KSA.Part.CrashTolerancePascals
  - harmony-prefix KSA.PhysicsBubble.DetectStructuralFailure
  - harmony-postfix KSA.PhysicsBubble.DetectStructuralFailure
lifecycle: [physics:vehicle-solver, physics:bepu, frame:apply-solvers, frame:draw-ui]
game_types:
  - KSA.Part
  - KSA.PhysicsBubble
  - KSA.VehicleUpdateState
  - KSA.Vehicle
  - KSA.PartStructuralLimits
  - KSA.FxDeformation
tags: [crash, registry, concurrent, worker-safe, indestructible]
---
Outcome: the player marks individual vessels as indestructible: they still collide, bounce and spin, and
telemetry still reports the loads, but no impact, g-load or dynamic pressure breaks a part or destroys the
craft. Other vessels built from the same parts are unaffected. unscience's The Tick does it with two small
patches and a concurrent registry.

Prerequisites: StarMap, Harmony. Demonstrated by [unscience/the-tick-vessel-indestructibility](../../../../mods/unscience/features/the-tick-vessel-indestructibility.md). Because the
patched code runs on worker threads, read [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md) first.

## Background: how the game does it

- `Part.CrashTolerancePascals` is an expression-bodied getter [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L858)[^ksa-part]. Its value feeds
  `PartFailure.Detect` on the worker and `FxDeformation.ReportContact` [current/decomp/KSA/PartFailure.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartFailure.cs#L45-L75)[^ksa-partfailure]
  [current/decomp/KSA/FxDeformation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FxDeformation.cs#L318-L370)[^ksa-fxdeformation].
- `PhysicsBubble.DetectStructuralFailure(VehicleUpdateState)` is private static void. It only acts when
  `vehicleState.DestructionEvent == null`, writes telemetry, and creates the event
  [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L999-L1038)[^ksa-physicsbubble].
- Why not edit `PartTemplate.CrashTolerance`: `Part.Template` is one shared `PartTemplate` per part type, so
  editing it would harden every vessel using that part and need restoring on removal.
- Owner lookup: `part.Tree?.OwningVehicle ?? part.FullPart.Tree?.OwningVehicle`; sub-parts get their `Tree`
  from `PartTree.TransferPart` too.

## Lifecycle and timing

| Code | Thread | When |
|---|---|---|
| tolerance prefix | vehicle worker threads | `PartFailure.Detect` in `FullPhysicsEndFrame`, and `FxDeformation.ReportContact` after `ConstraintSim.Simulate` (`physics:bepu`) |
| same | main | the "Show Part Contact Load" debug view |
| detector prefix/postfix | vehicle worker | end of each bubble's step (`physics:vehicle-solver`) |
| event application (prevented) | main, next frame | `ApplyVehicleSolvers` -> `ApplyRenderEventsToVehicles` -> `DestroyVehicleFromEvent` |
| registry add/remove/prune | main, `frame:draw-ui` | concurrent with workers |

Destruction is decided on the worker and applied on the main thread a frame later. Clearing the event on the
worker, in the same call that created it, means the main thread never sees it. Both patches run on several
workers at once, so they do field reads and a concurrent-dictionary lookup only: no allocation, no locks, no
ImGui.

## Steps

1. A static registry keyed by object identity, readable from workers while the UI edits it:

```csharp
// the-tick.lib/TickProtection.cs L12-38
public static class TickProtection
{
    /// <summary>
    /// Effective crash tolerance reported for every part of a protected vehicle. The largest finite
    /// double keeps downstream arithmetic (dent depth = pressure / tolerance) finite instead of NaN.
    /// </summary>
    public const double UnbreakableTolerancePascals = double.MaxValue;

    private static readonly ConcurrentDictionary<Vehicle, byte> Vehicles =
        new(ReferenceEqualityComparer.Instance);

    public static Vehicle[] Snapshot() => Vehicles.Keys.ToArray();
    public static bool Contains(Vehicle vehicle) => !vehicle.IsDisposed && Vehicles.ContainsKey(vehicle);
    public static bool Add(Vehicle vehicle) => !vehicle.IsDisposed && Vehicles.TryAdd(vehicle, 0);
    public static bool Remove(Vehicle vehicle) => Vehicles.TryRemove(vehicle, out _);
    public static void Clear() => Vehicles.Clear();

    /// <summary>Drops disposed vehicles and vehicles no longer present in the live world.</summary>
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

2. Patch the tolerance getter. Return `double.MaxValue`, not infinity, so `FxDeformation`'s
   `pressure / tolerance` dent ratio stays finite (effectively zero) and protected vessels stop denting too:

```csharp
// the-tick.lib/TheTickPatches.cs L74-84
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

3. Patch the detector with a prefix that remembers whether an event already existed and a postfix that
   discards only the event this call created. The telemetry written before the event survives, and a
   destruction queued by something else earlier is never discarded:

```csharp
// the-tick.lib/TheTickPatches.cs L86-96
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

4. Install with signature validation, and unpatch the first patch if the second fails:

```csharp
// the-tick.lib/TheTickPatches.cs L31-58
    public static void Apply(Harmony harmony)
    {
        if (IsApplied) return;
        var getter = AccessTools.PropertyGetter(typeof(Part), nameof(Part.CrashTolerancePascals))
            ?? throw new MissingMethodException(typeof(Part).FullName, nameof(Part.CrashTolerancePascals));
        if (getter.ReturnType != typeof(double) || getter.IsStatic)
            throw new InvalidOperationException("Unexpected KSA part crash-tolerance getter signature.");
        var detector = AccessTools.Method(typeof(PhysicsBubble), "DetectStructuralFailure",
            new[] { typeof(VehicleUpdateState) })
            ?? throw new MissingMethodException(typeof(PhysicsBubble).FullName, "DetectStructuralFailure");
        if (!detector.IsStatic || detector.ReturnType != typeof(void))
            throw new InvalidOperationException("Unexpected KSA structural-failure detector signature.");

        harmony.Patch(getter, prefix: new HarmonyMethod(ToleranceMethod));
        try
        {
            harmony.Patch(detector, prefix: new HarmonyMethod(RememberMethod), postfix: new HarmonyMethod(SuppressMethod));
        }
        catch
        {
            harmony.Unpatch(getter, ToleranceMethod);
            throw;
        }
        _toleranceGetter = getter;
        _detector = detector;
        IsApplied = true;
        Console.WriteLine("the-tick: indestructibility patches applied.");
    }
```

5. Prune disposed vessels from the main thread each frame (`Prune`); `Contains` already rejects disposed
   vessels, so protection ends the moment one is disposed.
6. Expect on apply: `the-tick: indestructibility patches applied.`

## Complete example

- [the-tick.lib/TheTickPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.lib/TheTickPatches.cs)[^unscience-thetickpatches] - installation and the three patch bodies
- [the-tick.lib/TickProtection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.lib/TickProtection.cs)[^unscience-tickprotection] - registry

## Variations and gotchas

- **Protected vessels do not dent.** The `double.MaxValue` tolerance makes the dent ratio zero.
- **Identity, not name.** `ReferenceEqualityComparer` means a new vessel reusing a protected name is not
  protected; the other side is that the registry must be rebuilt after a load (article 3 shows how).
- **Two patches on one method** (prefix + postfix) share state through `__state`; do not use a static for it,
  the detector runs on several workers.
- **Kittens.** `PartFailure.Detect` returns early for kittens and for vehicles on rails
  [current/decomp/KSA/PartFailure.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartFailure.cs#L45-L50), so protection is moot there.
- **Updates.** The private detector is resolved by name with its parameter list; a rename throws at install and
  the mod logs it.

## See also

- [Overview](overview.md), [Article 1](make-a-vehicle-indestructible-1.md), [Article 3](make-a-vehicle-indestructible-3.md)
- [unscience/the-tick-vessel-indestructibility](../../../../mods/unscience/features/the-tick-vessel-indestructibility.md)
- [write-worker-thread-safe-solver-patches](../../../physics/solver/write-worker-thread-safe-solver-patches/overview.md)

[^ksa-fxdeformation]: current/decomp/KSA/FxDeformation.cs (decompiled, build 2026.10.10.5554)
[^unscience-thetickpatches]: unscience: the-tick.lib/TheTickPatches.cs
[^ksa-physicsbubble]: current/decomp/KSA/PhysicsBubble.cs (decompiled, build 2026.10.10.5554)
[^unscience-tickprotection]: unscience: the-tick.lib/TickProtection.cs
[^ksa-partfailure]: current/decomp/KSA/PartFailure.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
