---
type: How-To
title: SetOrbit plus manual Children surgery and UpdatePerFrameDataTree, with cycle guards on replay
description: Swap a celestial's orbit and, when the parent differs, move it between the old and new parents' Children lists, then refresh the moved subtree so nothing samples stale frames
category: celestials
group: systems-and-stars
topic: move-or-reparent-a-celestial-body
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/unscience/features/kiwis-marbles-cross-parent-reparenting
  - mods/unscience/features/kiwis-marbles-celestial-welding
techniques: [celestial-reparenting]
hooks:
  - harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers (priority First)
  - harmony-postfix KSA.Universe.DeserializeSave
lifecycle: [frame:dispatch-solvers, event:load]
game_types:
  - KSA.Celestial
  - KSA.IParentBody
  - KSA.IOrbiter
  - KSA.Orbit
  - KSA.CelestialSystem
  - KSA.Vehicle
  - KSA.PatchedConic
  - KSA.FlightPlan
tags: [celestial, reparent, children, setorbit, subtree-refresh]
---
Outcome: a body ends up with a new orbit, possibly around a different parent, and everything that reads the tree (per-frame refresh, encounter search, UI lists) agrees. Demonstrated by unscience's Kiwi's Marbles ([Cross-parent re-parenting](../../../../mods/unscience/features/kiwis-marbles-cross-parent-reparenting.md), [Celestial welding](../../../../mods/unscience/features/kiwis-marbles-celestial-welding.md)). Pair it with the timing in [approach 2](move-or-reparent-a-celestial-body-2.md).

## Background: how the game does it

What the game updates when an orbit is swapped (build 5554):

| State | Updated by `SetOrbit`? | Where it lives |
|---|---|---|
| `Celestial.Orbit` | Yes, and nothing else | `Celestial.SetOrbit` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L149-L152)) |
| `Celestial.Parent` | Yes, implicitly: `Parent => Orbit.Parent` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L69) |
| Old and new `IParentBody.Children` | **No** | `public List<IOrbiter> Children` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L23)) |
| Cached CCI/CCE/ECL transforms | Not until the next `UpdatePerFrameData` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L591-L606) |
| `SphereOfInfluence`, `Rank` | **No**; set in the constructor | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L208-L218) |
| Moons and vessels of the moved body | Follow automatically (their orbits are relative to it) | |

Every consumer below reads `Children`, none re-derives from `Orbit.Parent`: `CelestialSystem.UpdatePerFrameData` walks each root through `Children` (a body listed under its old parent refreshes in the old parent's walk, possibly before its new parent, so its ecliptic position lags); `PatchedConic.ScanForEncounters` and the flight-plan search iterate `PrimaryBody.Children` (vessels around the new parent cannot find an encounter with the moved body); the program menu and `UpdateShaderData` use the tree too.

## Lifecycle and timing

The swap runs only inside the weld engine, in the prefix window of `Universe.ExecuteNextVehicleSolvers` (see approach 2). That matters twice: `Children` is a plain `List<IOrbiter>` that encounter search and the per-frame refresh enumerate, and no orbit or vessel job is running in that window, so the `Remove`/`Add` pair cannot invalidate an enumerator on a worker thread; and `CelestialSystem.UpdatePerFrameData` already ran at the end of `ApplyVehicleSolvers`, so the moved branch would keep last step's cached frames until the next frame unless refreshed immediately. On load the scene save coordinator first resets (restoring every original orbit, and so every original parent, while the old bodies still exist and no worker runs), then replays in the `DeserializeSave` postfix before the next solver dispatch. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Record the old parent, set the orbit, compare parents by reference, re-parent if they differ, refresh the subtree:**

```csharp
// kiwis-marbles.lib/CelestialWeldEngine.cs L73-L95
private static void ApplyOrbit(Celestial body, Orbit newOrbit)
{
    IParentBody? oldParent = body.Parent;
    IParentBody? newParent = newOrbit.Parent;

    body.SetOrbit(newOrbit);

    if (!ReferenceEquals(oldParent, newParent))
        Reparent(body, oldParent, newParent);

    // Refresh the body and its whole subtree (moons, vehicles) so anything sampling positions later
    // this frame sees the welded state rather than the propagated one.
    ((IParentBody)body).UpdatePerFrameDataTree();
}

private static void Reparent(Celestial body, IParentBody? oldParent, IParentBody? newParent)
{
    oldParent?.Children.Remove(body);
    if (newParent != null && !newParent.Children.Contains(body))
        newParent.Children.Add(body);

    Console.WriteLine($"kiwis-marbles: re-parented {body.Id}: {oldParent?.Id ?? "none"} -> {newParent?.Id ?? "none"}");
}
```

2. **Restore with the same path.** `RestoreOrbit` re-applies the captured `OriginalOrbit` through `ApplyOrbit`, whose parent is the original parent, so un-welding moves the body back into its original parent's list.

3. **Skip the list edit on same-parent changes;** only the subtree refresh runs.

4. **Guard against cycles when replaying saves.** A saved weld that puts a body below its own descendant would corrupt the tree: validate self-ancestry, the proposed graph and each step (including cycles through vessel parents that a target-only sort misses) before applying.

5. **Do not trust `SphereOfInfluence` or `Rank` of the moved body** after a cross-parent move: both were set for the old parent. Recompute or accept the mismatch for display purposes.

## Complete example

| File | Role |
|---|---|
| [kiwis-marbles.lib/CelestialWeldEngine.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/CelestialWeldEngine.cs#L63-L95) | `ApplyOrbit` and `Reparent` |
| [kiwis-marbles.lib/KiwisMarblesSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/KiwisMarblesSubmod.Persistence.cs#L74-L135) | Replay with self-ancestor, proposed-graph and per-step cycle checks |

## Variations and gotchas

- **An earlier version assumed `SetOrbit` re-parented;** that was wrong, and the authors' table records it.
- **The game's vessel fix-up** (`ApplyPendingParentChildrenFix`) is the model for this code: remove from the old list, add to the new one.
- **Unload** must restore every weld immediately, because after unpatching there is no later safe window.

## See also

- [Overview](overview.md); [approach 2](move-or-reparent-a-celestial-body-2.md)
- [Cross-parent re-parenting](../../../../mods/unscience/features/kiwis-marbles-cross-parent-reparenting.md)
- [Teleport a vessel by building a new orbit](../../../physics/kinematics/teleport-a-vessel-by-building-a-new-orbit/overview.md)
