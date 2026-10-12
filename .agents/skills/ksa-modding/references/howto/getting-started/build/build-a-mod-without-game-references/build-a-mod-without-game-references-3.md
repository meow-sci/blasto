---
type: How-To
title: Centralize game-state access behind a static provider library
description: Put every read of controlled vehicle, vehicle list, celestials and sim time in one small library of static providers so a game update breaks one file, and let features call the providers instead of game types
category: getting-started
group: build
topic: build-a-mod-without-game-references
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/lib-game-state-providers]
techniques: [reflection]
hooks: [call KSA.Universe.CurrentSystem]
lifecycle: [frame:draw-ui]
game_types: [KSA.Program, KSA.Universe, KSA.Vehicle, KSA.CelestialSystem]
tags: [providers, isolation, game-state, abstraction, unscience, maintainability]
---

unscience has about 30 features that all need "the controlled vehicle", "all vehicles", "the sim time". Instead of each
calling `Universe` and `Program` directly, they go through a small `ksa-abstractions.lib` of static providers. When KSA
changes (a rename, a new debris flag) one provider changes, not thirty features. This is not a reference-free build (the
library references `KSA.dll`), but it is the same insulation idea: **game access has one address**. The truly
reference-free and compile-gated forms are [Reflection-only mod with visible breakage](./build-a-mod-without-game-references-1.md) and
[Game-free half and compile-gated Game half](./build-a-mod-without-game-references-2.md); a per-frame census variant is [Per-frame census from Universe.CurrentSystem.All](./build-a-mod-without-game-references-4.md).

## Background: how the game does it

The state lives in a few places: `Program.ControlledVehicle` (the vehicle the player flies), `Universe.CurrentSystem.All`
(every `Astronomical` in the current system: vehicles, bodies, orbiters), and `Universe.GetElapsedTime()` (simulation
time). All are main-thread state; the providers are not safe from worker-thread patches. A concrete reason to centralize:
KSA 2026.9.7.5402 added structural part failure that sheds fragments as real `Vehicle` objects flagged `IsDebris`,
living in the same collection as crewed craft; one provider change keeps debris out of every picker.
`Universe.GetElapsedSimTime()` was renamed to `GetElapsedTime()` with a `UniverseTime` return in build 2026.8.19.5261,
which `SimTimeProvider` absorbed.[ksa-abstractions.lib/SimTimeProvider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/SimTimeProvider.cs)

## Lifecycle and timing

The providers do no patching and have no timing of their own; they read main-thread game state and are called from ImGui
handlers and `ISubmod.Update` (`frame:draw-ui`) or from a PrepareFrame handoff. `Universe.CurrentSystem.All` and
`Program.ControlledVehicle` must not be read from a worker thread. `GetAllVehicles` copies the backing list with
`ToList()`, so callers may keep the result across frames, but the vessels inside can be disposed later (check
`Vehicle.IsDisposed`). See [StarMap lifecycle attributes](../../lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md).

## Steps

1. **Create a library of static helpers**, one concern per class, with doc comments that record *why* (the debris flag).
   `VehicleProvider` shows the shape:[ksa-abstractions.lib/VehicleProvider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/VehicleProvider.cs#L7-L40)

   ```csharp
   // ksa-abstractions.lib/VehicleProvider.cs L8-L40
   public static class VehicleProvider
   {
       /// <summary>Returns the currently player-controlled vehicle, or null if none.</summary>
       public static Vehicle? GetControlledVehicle() => Program.ControlledVehicle;

       /// <summary>
       /// Returns the vehicles in the current system, or an empty list if unavailable.
       /// </summary>
       /// <param name="includeDebris">
       /// KSA 2026.9.7.5402 added structural part failure, which sheds fragments as real
       /// <see cref="Vehicle"/> objects flagged <c>IsDebris</c>. They live in the same system
       /// collection as crewed craft, so they would otherwise fill every mod's vehicle picker.
       /// They are excluded by default; pass true when debris is a legitimate target.
       /// </param>
       public static List<Vehicle> GetAllVehicles(bool includeDebris = false) =>
           Universe.CurrentSystem?.All.UnsafeAsList()
               .OfType<Vehicle>()
               .Where(v => includeDebris || !v.IsDebris)
               .ToList() ?? new List<Vehicle>();

       /// <summary>Finds a vehicle by id, or null if none or more than one matches. Debris is searched too, so an id
       /// held from before a part failure still resolves.</summary>
       public static Vehicle? FindVehicle(string vehicleId)
       {
           Vehicle? match = null;
           foreach (var vehicle in GetAllVehicles(includeDebris: true))
           {
               if (vehicle.Id != vehicleId) continue;
               if (match != null) return null; // Ambiguous identities must never retarget saved edits.
               match = vehicle;
           }
           return match;
       }
   }
   ```

2. **Absorb renames in one line.** When `Universe.GetElapsedSimTime()` became `GetElapsedTime()` returning a
   `UniverseTime` (backed by 128-bit nanoseconds; `Seconds()` still returns a double), only this wrapper changed:[ksa-abstractions.lib/SimTimeProvider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/SimTimeProvider.cs#L5-L16)

   ```csharp
   // ksa-abstractions.lib/SimTimeProvider.cs L6-L16
   public static class SimTimeProvider
   {
       /// <summary>Returns the current elapsed simulation time as a UniverseTime value.</summary>
       /// <remarks>
       /// KSA build 2026.8.19.5261 (rev 5211) replaced <c>SimTime</c> with <c>UniverseTime</c>,
       /// backed by 128-bit nanoseconds instead of double seconds, and renamed
       /// <c>Universe.GetElapsedSimTime()</c> to <c>Universe.GetElapsedTime()</c>.
       /// <c>Seconds()</c> still returns a double on the new type, so callers are unaffected.
       /// </remarks>
       public static UniverseTime GetElapsedTime() => Universe.GetElapsedTime();
   }
   ```

3. **Provide the other readers the same way:** `CelestialProvider.GetAllCelestials()`/`GetAllOrbiters()`
   (`OfType<Celestial>()` / `OfType<IOrbiter>()` on the same collection), `PartHelpers.GetAllParts(vehicle)` (depth-first
   over `vehicle.Parts.Parts` and each `Part.SubParts`, which most stock enumerations skip), `KsaPaths.UserDataDir`
   (single root for state files; note it uses `SpecialFolder.MyDocuments` and so ignores `-InstancePath`,
   [Manual Harmony prefix on Constants.DocumentsFolderPath](../../loader/how-starmap-launches-ksa/how-starmap-launches-ksa-3.md)).
4. **Keep a generic reflection fallback**, and mark it as slow: instance field access by name with public and
   non-public flags, null-safe:[ksa-abstractions.lib/ReflectionHelpers.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/ReflectionHelpers.cs)

   ```csharp
   // ksa-abstractions.lib/ReflectionHelpers.cs L8-L17
       private static readonly BindingFlags All =
           BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

       /// <summary>Gets the value of a field by name from the given object, or null if not found.</summary>
       public static object? GetFieldValue(object? obj, string fieldName)
       {
           if (obj == null) return null;
           var field = obj.GetType().GetField(fieldName, All);
           return field?.GetValue(obj);
       }
   ```

   Hot paths use Harmony `FieldRefAccess` instead; this looks the field up on every call, fine for UI, too slow for
   per-frame or per-part loops.
5. **Make ambiguity fail closed.** `FindVehicle` returns `null` when two vehicles share an id, so a saved edit is never
   retargeted at the wrong craft.
6. **Review rule:** features call providers; a PR that adds `Universe.CurrentSystem` to a feature is a smell. (The
   library records the rule, it does not enforce it.)

## Complete example

[ksa-abstractions.lib/VehicleProvider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/VehicleProvider.cs), [ksa-abstractions.lib/SimTimeProvider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/SimTimeProvider.cs) and [ksa-abstractions.lib/ReflectionHelpers.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/ReflectionHelpers.cs) are the three files; the other providers follow the same
pattern. The `Update` loop that consumes them is in [Copy and rename the repo template with mkmod.ts (plus: ISubmod contract with a supermod host)](../../loader/create-a-minimal-starmap-mod/create-a-minimal-starmap-mod-2.md).

## Variations and gotchas

- **Debris hides from pickers**; features that must follow a vessel through a breakup search with `includeDebris: true`.
- **Vessel ids are not guaranteed unique**; stable save identity uses a stronger scheme in unscience.
- **Main thread only.** Do not call the providers from worker-thread patches; for those, read state in a PrepareFrame
  prefix and pass snapshots.
- **It still references KSA.** A game update still recompiles this one library; the win is that the break is local.
- **`KsaPaths` is not instance-aware**; prefer `Constants.DocumentsFolderPath`.

## See also

- [Reflection-only mod with visible breakage](./build-a-mod-without-game-references-1.md), [Game-free half and compile-gated Game half](./build-a-mod-without-game-references-2.md), [Per-frame census from Universe.CurrentSystem.All](./build-a-mod-without-game-references-4.md), [Build a KSA mod that references only StarMap.API and Harmony](./overview.md)
- [Thin static providers that funnel game-state access through one library](../../../../mods/unscience/features/lib-game-state-providers.md)
- [Create a minimal StarMap mod](../../loader/create-a-minimal-starmap-mod/overview.md), [Choose and use the right StarMap lifecycle hook](../../lifecycle/choose-a-starmap-startup-hook/overview.md)
