---
type: How-To
title: Priority.First prefix on ExecuteNextVehicleSolvers builds a fresh orbit from the target's state vectors
description: Make a planet or moon follow another body at an offset by building a new orbit from the target's freshly applied state vectors in a first-priority prefix at the solver handoff, ordering chained updates and restoring the original orbit on unweld
category: celestials
group: systems-and-stars
topic: move-or-reparent-a-celestial-body
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/kiwis-marbles-celestial-welding]
techniques: [harmony-prefix]
hooks:
  - harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers (priority First)
  - harmony-postfix KSA.GameSave.Populate
  - harmony-prefix KSA.Universe.DeserializeSave
  - harmony-postfix KSA.Universe.DeserializeSave
lifecycle: [frame:dispatch-solvers, frame:draw-ui, event:save, event:load]
game_types:
  - KSA.Celestial
  - KSA.Orbit
  - KSA.StateVectors
  - KSA.Universe
  - KSA.CelestialUpdateTask
  - KSA.UniverseTime
  - KSA.GameSave
  - KSA.Vehicle
tags: [celestial, orbit, weld, solver-handoff, state-vectors, topological-sort]
---
Outcome: the source body follows a target (another celestial or a vessel) at an offset until it is unwelded, when it goes back to the orbit it had; several welds can chain (Moon follows Earth, Earth follows a vessel). Demonstrated by unscience's Kiwi's Marbles ([Celestial welding](../../../../mods/unscience/features/kiwis-marbles-celestial-welding.md)).

## Background: how the game does it

Once per sim step each weld reads the target's CCI state vectors, builds a new `Orbit` with `Orbit.CreateFromStateCci` around the target's parent at target position plus offset with the target's velocity ([current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1611)), and swaps it onto the source with `SetOrbit` so that the next `CelestialUpdateTask` propagates the welded orbit instead of the original. The offset is applied directly in the CCI frame of the target's parent and not rotated by any body frame. The timing is the whole trick.

## Lifecycle and timing

Order inside `Program.PrepareFrame` (build 5554, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2150-L2225)):

| Call | What it means for a weld |
|---|---|
| `JobSystems.OrbitSolvers.Wait()` | Previous `CelestialUpdateTask`s have finished |
| `Universe.ApplyOrbitSolvers()` | Each body's `UpdateFromTaskResults` calls `Orbit.UpdatePosition` with the propagated state ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1905)) |
| `Universe.ApplyVehicleSolvers()` | Vessel results committed; ends with `CelestialSystem.UpdatePerFrameData()` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1950)) |
| **`Universe.ExecuteNextVehicleSolvers`** | **The weld prefix runs here** |
| `Universe.ExecuteNextOrbitSolvers` | One `CelestialUpdateTask` per body is prepared and queued |

Why the prefix and not the UI hook: the orbit job reads the body's `Orbit` when the worker runs and the next frame writes the result into whatever `Orbit` the body has then. An orbit swapped from `[StarMapBeforeGui]` (after `ExecuteNextOrbitSolvers`) races the worker, and the welded orbit then receives state vectors propagated from the old one: welds had no visible effect, which the authors record as the original symptom. In the prefix every target position is current, no orbit or vehicle worker is running (`ExecuteNextClothSolvers` has already kicked the parachute cloth step just before, which this code does not touch), the epoch matches (`Universe.GetElapsedTime()` is the end of the step whose results were just applied) and the welded orbit is what the worker propagates next. The prefix is not a UI hook, so it runs with the HUD hidden and while the game is paused. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Install a `Priority.First` prefix** on `Universe.ExecuteNextVehicleSolvers` and call the weld update inside a `try`:

```csharp
// kiwis-marbles.lib/KiwisMarblesPatches.cs L22-L53
public static void Apply(Harmony harmony)
{
    var original = AccessTools.Method(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers));
    var prefixMethod = AccessTools.Method(typeof(KiwisMarblesPatches), nameof(BeforeVehicleSolvers));

    if (original == null)
        throw new MissingMethodException(typeof(Universe).FullName, nameof(Universe.ExecuteNextVehicleSolvers));
    if (prefixMethod == null)
        throw new MissingMethodException(typeof(KiwisMarblesPatches).FullName, nameof(BeforeVehicleSolvers));

    harmony.Patch(original, prefix: new HarmonyMethod(prefixMethod) { priority = Priority.First });
}

public static void Remove(Harmony harmony)
{
    var original = AccessTools.Method(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers));
    var prefixMethod = AccessTools.Method(typeof(KiwisMarblesPatches), nameof(BeforeVehicleSolvers));
    if (original != null && prefixMethod != null)
        harmony.Unpatch(original, prefixMethod);
}

private static void BeforeVehicleSolvers()
{
    try
    {
        KiwisMarblesSubmod.Instance?.UpdateBeforeVehicleSolvers();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"kiwis-marbles: Error in solver prefix: {ex.Message}\n{ex}");
    }
}
```

2. **Update each weld in sorted order:** apply pending restores first, then every weld, removing any whose target vanished:

```csharp
// kiwis-marbles.lib/KiwisMarblesSubmod.cs L52-L73
public void UpdateBeforeVehicleSolvers()
{
    if (_pendingRestores.Count > 0)
    {
        foreach (var entry in _pendingRestores)
            TryRestoreOrbit(entry);
        _pendingRestores.Clear();
    }

    if (_welds.Count == 0) return;

    List<CelestialWeldEntry>? toRemove = null;
    foreach (var weld in _welds)
    {
        if (!CelestialWeldEngine.UpdateWeld(weld))
            (toRemove ??= new List<CelestialWeldEntry>()).Add(weld);
    }

    if (toRemove == null) return;
    foreach (var weld in toRemove)
        RemoveWeld(weld);
}
```

3. **Build the orbit from the target's state vectors** and apply it with the swap and re-parent from [approach 1](move-or-reparent-a-celestial-body-1.md):

```csharp
// kiwis-marbles.lib/CelestialWeldEngine.cs L27-L52
public static bool UpdateWeld(CelestialWeldEntry entry)
{
    if (entry.Source == null || entry.Target == null)
        return false;

    if (entry.Target.Orbit == null || entry.Target.Parent == null)
        return false;

    double3 tgtPosCci = entry.Target.GetPositionCci();
    double3 tgtVelCci = entry.Target.GetVelocityCci();
    IParentBody parent = entry.Target.Parent;

    double3 newSrcPosCci = tgtPosCci + entry.Offset;
    double3 newSrcVelCci = tgtVelCci;

    Orbit newOrbit = Orbit.CreateFromStateCci(
        parent,
        SimTimeProvider.GetElapsedTime(),
        newSrcPosCci,
        newSrcVelCci,
        entry.Source.OrbitColor
    );

    ApplyOrbit(entry.Source, newOrbit);
    return true;
}
```

4. **Order chained welds topologically** (Kahn): an edge x to y exists when x's source is y's target, so the body is moved before anything that follows it reads its position. A cycle leaves the order unchanged and logs. The sort runs on create and on load.

5. **Never touch bodies from the UI.** The UI only edits the managed list or queues a restore; the restore (`RestoreOrbit` re-applying `OriginalOrbit`) runs at the start of the next prefix.

6. **Save and replay** through the scene save: a record per weld with source id, target id, whether the target is a vessel, the offset and the original orbit as parent id plus state time, `PositionCci` and `VelocityCci`, because native KSA saves never serialize celestial orbits. Reset (before KSA rebuilds the world) restores old orbits in reverse list order; replay rebuilds each `OriginalOrbit` with `Orbit.CreateFromStateCci`, rejects any weld whose target sits below its own source, sorts, and applies each weld once while the solvers are idle. Unresolved bodies are warnings, never substitutions.

## Complete example

| File | Role |
|---|---|
| [kiwis-marbles.lib/KiwisMarblesPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/KiwisMarblesPatches.cs#L20-L54) | Apply and remove the prefix |
| [kiwis-marbles.lib/CelestialWeldEngine.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/CelestialWeldEngine.cs) | `UpdateWeld`, `RestoreOrbit`, `ApplyOrbit`, `TopologicalSort` |
| [kiwis-marbles.lib/CelestialWeldEntry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/CelestialWeldEntry.cs) | Weld record |
| [kiwis-marbles.lib/KiwisMarblesSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/KiwisMarblesSubmod.Persistence.cs) | Scene-save capture, reset, validate and replay |

## Variations and gotchas

- **Do not set the orbit from `Update`/draw hooks.** It races the orbit worker.
- **Large offsets:** edit the offset as a `float3` proxy in m/km/Mm/Gm and convert with a `double` factor so Gm offsets keep metre precision in the stored `double3`.
- **Unbound or missing target orbit** (no orbit or parent) removes the weld.
- **Shared prefix ordering.** The supermod applies this after another mod's `Priority.First` prefix on the same method; shared patches need an explicit order (see [coexist with other mods patching the same method](../../../patching/harmony/coexist-with-other-mods-patching-the-same-method/overview.md)).

## See also

- [Overview](overview.md); [approach 1](move-or-reparent-a-celestial-body-1.md)
- [Celestial welding](../../../../mods/unscience/features/kiwis-marbles-celestial-welding.md)
- [Mutate vehicles at a physics-safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md), [Save mod state with the scene](../../../persistence/saves/save-mod-state-with-the-scene/overview.md)
