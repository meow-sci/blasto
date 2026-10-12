---
type: Mod Feature
title: Thin static providers that funnel game-state access through one library
description: Static wrappers for vessels, celestials, sim time, part trees and user-data paths, plus reflection and easing helpers, so game-state access goes through one library and a game update breaks one place instead of thirty features
mod: unscience
category: getting-started
techniques: [reflection]
hooks: []
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Program
  - KSA.Universe
  - KSA.CelestialSystem
  - KSA.Vehicle
  - KSA.Celestial
  - KSA.IOrbiter
  - KSA.Part
  - KSA.PartTree
  - KSA.UniverseTime
  - KSA.LookupCollection
tags: [architecture, vehicles, celestials, sim-time, reflection, paths, easing]
---

Most unscience features start with the same questions: which vessels exist, which one is
controlled, which bodies exist, what time is it in the simulation, where is the user data folder.
Instead of each feature reaching into `Universe` and `Program`, `ksa-abstractions.lib` provides a
handful of static one-liners, and the authors track their game touchpoints in one scope table, so
a rename (for example 5261's `SimTime` -> `UniverseTime`) is fixed once.[^abstractions-readme][^scope-architecture]

Mechanism in one line: static wrappers over `Universe.CurrentSystem.All` (filtered with `OfType<T>()`),
`Program.ControlledVehicle`, `Universe.GetElapsedTime()` and `Part.SubParts`, plus two pure-C#
helpers (reflection field access, easing curves) and the `.unscience` data-directory path.

## How it works

| Helper | What it does | Notable decision |
|---|---|---|
| `VehicleProvider.GetControlledVehicle()` | `Program.ControlledVehicle` | |
| `VehicleProvider.GetAllVehicles(includeDebris = false)` | `Universe.CurrentSystem?.All.UnsafeAsList().OfType<Vehicle>()`, skipping `IsDebris` | Since KSA 5402 structural failure sheds fragments as real `Vehicle` objects flagged `IsDebris`; they would flood every picker, so they are excluded by default[^vehicle-provider][^ksa-vehicle] |
| `VehicleProvider.FindVehicle(id)` | Linear search including debris | Returns `null` when two vehicles share the id: "Ambiguous identities must never retarget saved edits" |
| `CelestialProvider.GetAllCelestials()` / `GetAllOrbiters()` | `OfType<Celestial>()` / `OfType<IOrbiter>()` on the same collection | Orbiters include vessels and bodies (used by welding targets)[^celestial-provider] |
| `SimTimeProvider.GetElapsedTime()` | `Universe.GetElapsedTime()` returning `UniverseTime` | Absorbed the 5261 rename from `GetElapsedSimTime`/`SimTime`[^sim-time-provider][^ksa-universe] |
| `PartHelpers.GetAllParts(vehicle)` / `GetPartsWhere` | Depth-first walk of `vehicle.Parts.Parts` and each `Part.SubParts` | Includes sub-parts, which most stock enumerations do not[^part-helpers][^ksa-part] |
| `KsaPaths.UserDataDir` / `ModDataDir` | `MyDocuments/My Games/Kitten Space Agency` and its `.unscience` subfolder | Single root for every preset, library and state file[^ksa-paths] |
| `ReflectionHelpers.GetFieldValue/SetFieldValue` | Instance field access by name with public+non-public flags, null-safe | Generic fallback; hot paths use Harmony `FieldRefAccess` instead[^reflection-helpers] |
| `EasingHelper.ApplyEasing(t, type, powerStart, powerEnd)` | Linear / EaseIn / EaseOut / EaseInOut power curves on clamped `t` | Shared by Zippo light animations, Garry's Torch weld animations and camera animations[^easing-helper] |

## Lifecycle and timing

The providers do no patching and have no timing of their own; they read main-thread game state and
are called from ImGui handlers and `ISubmod.Update` (`frame:draw-ui`) or from the PrepareFrame
handoff. Two consequences:

- `Universe.CurrentSystem.All` and `Program.ControlledVehicle` are main-thread state; calling the
  providers from a worker-thread patch is not safe.
- `UnsafeAsList()` exposes the collection's backing list; `GetAllVehicles` copies it with `ToList()`,
  so callers can keep the result across frames, but the vessels inside can be disposed later (check
  `Vehicle.IsDisposed`, as Dent Wizard and Iron Man do).[^ksa-celestial-system]

## Key code

| Path | Role |
|---|---|
| [ksa-abstractions.lib/VehicleProvider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/VehicleProvider.cs) | Vessel listing and id lookup |
| [ksa-abstractions.lib/CelestialProvider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/CelestialProvider.cs) | Bodies and orbiters |
| [ksa-abstractions.lib/SimTimeProvider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/SimTimeProvider.cs) | Simulation time |
| [ksa-abstractions.lib/PartHelpers.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PartHelpers.cs) | Recursive part enumeration |
| [ksa-abstractions.lib/KsaPaths.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/KsaPaths.cs) | User-data paths |
| [ksa-abstractions.lib/ReflectionHelpers.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/ReflectionHelpers.cs) | Field access by name |
| [ksa-abstractions.lib/EasingHelper.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/EasingHelper.cs) | Easing curves |

## Snippets

```csharp
// ksa-abstractions.lib/VehicleProvider.cs L22-L40
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
```

```csharp
// ksa-abstractions.lib/EasingHelper.cs L15-L28
    public static double ApplyEasing(double t, EasingType easingType,
        double powerStart = 3.0, double powerEnd = 3.0)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return easingType switch
        {
            EasingType.EaseIn  => Math.Pow(t, powerStart),
            EasingType.EaseOut => 1.0 - Math.Pow(1.0 - t, powerEnd),
            EasingType.EaseInOut => t < 0.5
                ? Math.Pow(2 * t, powerStart) / 2.0
                : 1.0 - Math.Pow(2 * (1 - t), powerEnd) / 2.0,
            _ => t
        };
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Universe.CurrentSystem` / `CelestialSystem.All` (`LookupCollection<Astronomical>`) | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L66) | Every astronomical object in the loaded system |
| `Program.ControlledVehicle` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502) | Player-controlled vessel |
| `Vehicle.IsDebris`, `Vehicle.Parts`, `Vehicle.Id` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L393) | Debris filter, part tree, identity |
| `Universe.GetElapsedTime()` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2399) | Simulation clock as `UniverseTime` |
| `Part.SubParts` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1127) | Recursive enumeration |

## Pitfalls and version notes

- `IsDebris` filtering hides fragments from pickers; features that must follow a vessel through a
  breakup (saved targets) search with `includeDebris: true`.
- Vessel ids are not guaranteed unique; `FindVehicle` fails closed rather than guessing. Stable save
  identity uses a stronger scheme (see [save part identity](./lib-save-part-identity.md)).
- `ReflectionHelpers` looks up the field on every call; fine for UI, too slow for per-frame or per-part loops.
- `KsaPaths` uses `Environment.SpecialFolder.MyDocuments`; on macOS and Linux that resolves to the home
  `Documents` folder, matching where KSA keeps `My Games/Kitten Space Agency` (not independently verified
  for every platform).

## See also

- [XKCD color catalog](./lib-xkcd-color-catalog.md)
- [Shared file library](./lib-shared-file-library.md) (built on `KsaPaths.ModDataDir`)
- [Zippo light animation queue](./zippo-light-animation-queue.md) (uses `EasingHelper`)
- [Mod overview](../overview.md)
- [Centralize game-state access behind a static provider library](../../../howto/getting-started/build/build-a-mod-without-game-references/build-a-mod-without-game-references-3.md) - how-to article demonstrating this feature

[^abstractions-readme]: ksa-abstractions.lib/README.md
[^scope-architecture]: scope/00-architecture-and-abstractions.md (per-helper integration points)
[^vehicle-provider]: ksa-abstractions.lib/VehicleProvider.cs
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^celestial-provider]: ksa-abstractions.lib/CelestialProvider.cs
[^sim-time-provider]: ksa-abstractions.lib/SimTimeProvider.cs
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^part-helpers]: ksa-abstractions.lib/PartHelpers.cs
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-paths]: ksa-abstractions.lib/KsaPaths.cs
[^reflection-helpers]: ksa-abstractions.lib/ReflectionHelpers.cs
[^easing-helper]: ksa-abstractions.lib/EasingHelper.cs
[^ksa-celestial-system]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
