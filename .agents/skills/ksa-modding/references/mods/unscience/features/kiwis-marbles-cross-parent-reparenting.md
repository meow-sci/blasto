---
type: Mod Feature
title: Cross-parent celestial welds with explicit IParentBody.Children re-parenting
description: When a weld moves a body under a different parent, Kiwi's Marbles calls Celestial.SetOrbit, then moves the body between the old and new parents' Children lists itself and refreshes the moved subtree, because KSA re-parents only vessels automatically
mod: unscience
submod: kiwis-marbles
category: celestials
techniques: [harmony-prefix, celestial-reparenting]
hooks:
  - harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers (priority First, shared with the weld update)
  - "harmony-postfix KSA.Universe.DeserializeSave (shared NativeSaveHooks: replay with parent-graph checks)"
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
  - KSA.Program
tags: [celestials, orbits, parent-child, soi, per-frame-cache, welding, cycles]
---

A [celestial weld](./kiwis-marbles-celestial-welding.md) builds the source's new orbit around the
**target's** parent, so welding the Moon to the Earth makes the Moon a child of the Sun, and welding a
planet to a vessel in orbit around another planet makes it a child of that planet. KSA has no API that
moves a celestial body to a new parent: `Celestial.SetOrbit` is a bare property assignment, and the
parent-child tree that drives per-frame transforms, encounter search and several UI lists is a set of
plain `List<IOrbiter>` fields. Kiwi's Marbles does the tree surgery itself on every orbit swap, in both
directions (weld and restore), and guards against parent cycles when replaying saves.[^km-engine]

Mechanism in one line: `CelestialWeldEngine.ApplyOrbit` records the old parent, calls
`SetOrbit(newOrbit)`, and when `newOrbit.Parent` differs removes the body from
`oldParent.Children`, adds it to `newParent.Children`, then calls the default interface method
`IParentBody.UpdatePerFrameDataTree()` on the body so it and all its moons and vessels get fresh cached
frames before anything samples them.

## How it works

What the game updates by itself when an orbit is swapped (decomp, build 5554):

| State | Updated by `SetOrbit`? | Where it lives |
|---|---|---|
| `Celestial.Orbit` | Yes, `Orbit = newOrbit` and nothing else | `Celestial.SetOrbit` L149-L152[^ksa-celestial] |
| `Celestial.Parent` | Yes, implicitly: `Parent => Orbit.Parent` | Celestial.cs L69 |
| Old and new `IParentBody.Children` | **No** | `public List<IOrbiter> Children` (Celestial.cs L23) |
| Cached CCI/CCE/ECL transforms | Not until the next `UpdatePerFrameData` | `Celestial.UpdatePerFrameData` L591-L606 |
| `Celestial.SphereOfInfluence` | **No**; set once in the constructor from the template or `ComputeSphereOfInfluenceRadius()` (`Parent.Mass`, `Orbit.SemiMajorAxis`) | Celestial.cs L211-L218, L652-L660 |
| `Celestial.Rank` | **No**; `parent.Rank + 1` in the constructor | Celestial.cs L208 |
| Moons and vessels of the moved body | Follow automatically (their orbits are relative to it) | |

Vessels get the list fix-up from the game: `Vehicle.SetFlightPlan` calls `ApplyPendingParentChildrenFix`,
which removes the vessel from the previous parent's `Children` and adds it to the new one.[^ksa-vehicle]
There is no equivalent for celestials, so the mod mirrors it:

- **Swap** ([kiwis-marbles.lib/CelestialWeldEngine.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/CelestialWeldEngine.cs#L73-L86)).
  Capture `body.Parent`, `SetOrbit`, compare by reference with `newOrbit.Parent`, re-parent if different,
  then `((IParentBody)body).UpdatePerFrameDataTree()`.
- **Re-parent** ([kiwis-marbles.lib/CelestialWeldEngine.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/CelestialWeldEngine.cs#L88-L95)).
  `oldParent?.Children.Remove(body)`, then `Add` to the new parent unless already present, and a log
  line naming both parents.
- **Restore uses the same path** ([kiwis-marbles.lib/CelestialWeldEngine.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/CelestialWeldEngine.cs#L55-L61)).
  `RestoreOrbit` re-applies `OriginalOrbit` through `ApplyOrbit`, whose parent is the original parent, so
  unwelding moves the body back into its original parent's list.
- **Same-parent welds** skip the list edit; only the subtree refresh runs.

Why the `Children` lists matter (every consumer below reads them, none re-derive them from `Orbit.Parent`):

| Consumer | Effect of a stale list |
|---|---|
| `CelestialSystem.UpdatePerFrameData` walks each root's `UpdatePerFrameDataTree` through `Children`, and `Celestial.UpdatePerFrameData` adds `Parent.GetPositionEcl()`[^ksa-celestial-system][^ksa-iparentbody] | A body still listed under its old parent is refreshed in the old parent's walk, possibly before its new parent, so its ecliptic position lags |
| `PatchedConic.ScanForEncounters` and the flight-plan patch search iterate `PrimaryBody.Children`[^ksa-patched-conic][^ksa-flight-plan] | Vessels orbiting the new parent cannot find an SOI encounter with the moved body |
| Program menu `DrawMoveCameraTo`, `DrawOrbitLineOption`, and `UpdateShaderData` (nearby body's parent's children packed into the celestial UBO)[^ksa-program] | Camera-target menu, orbit-line toggles and shader celestial data show the old tree |

The authors' own table notes that an earlier version assumed `SetOrbit` re-parented and that this was
wrong.[^scope-celestial-lights]

## Lifecycle and timing

The swap runs only inside the weld engine, so it inherits the weld's window: the `Priority.First`
prefix on `Universe.ExecuteNextVehicleSolvers` in `Program.PrepareFrame`, after the orbit and vessel
results were applied and before the next orbit and vessel jobs are dispatched. That matters twice here:

- `Children` is a plain `List<IOrbiter>` that encounter search and the per-frame refresh enumerate. No
  orbit or vessel job is running in the prefix window, so the `Remove`/`Add` pair cannot invalidate an
  enumerator on a worker thread. The nearest-orbit hover job, which is queued just before and may still
  run, reads only `Orbit` references according to the authors' 5482 notes.[^scope-celestial-lights]
- The subtree refresh happens after `CelestialSystem.UpdatePerFrameData` already ran at the end of
  `ApplyVehicleSolvers`, so the moved branch would otherwise keep last step's cached frames until the next
  frame. Refreshing immediately means rendering later in the frame, and anything else that reads the
  cached frames before the next refresh, sees the moved body.

On load, the scene save coordinator first calls the participant's reset (which restores every original
orbit, and so every original parent, while the old bodies still exist and no worker runs), then replays
welds in the `DeserializeSave` postfix before the next solver dispatch.[^km-lib-readme]

## Key code

| Path | Role |
|---|---|
| [kiwis-marbles.lib/CelestialWeldEngine.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/CelestialWeldEngine.cs#L63-L95) | `ApplyOrbit` and `Reparent` |
| [kiwis-marbles.lib/KiwisMarblesSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/KiwisMarblesSubmod.Persistence.cs#L74-L135) | Replay with self-ancestor, proposed-graph and per-step cycle checks |
| [kiwis-marbles.lib/KiwisMarblesSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/KiwisMarblesSubmod.cs#L293-L294) | Shows the current `Source parent` in each weld panel |
| [kiwis-marbles/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles/README.md) | "Key Game APIs" notes on `SetOrbit` and `UpdatePerFrameDataTree` |

## Snippets

The swap and the list fix-up:

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

What the game does for vessels, and does not do for celestials:

```csharp
// current/decomp/KSA/Vehicle.cs L638-L647
	private void ApplyPendingParentChildrenFix()
	{
		IParentBody pendingParentChildrenFix = _pendingParentChildrenFix;
		if (pendingParentChildrenFix != null)
		{
			pendingParentChildrenFix.Children.Remove(this);
			Parent.Children.Add(this);
			_pendingParentChildrenFix = null;
		}
	}
```

The recursive refresh the list drives (no visited set, which is why a parent cycle must never exist):

```csharp
// current/decomp/KSA/IParentBody.cs L110-L124
	void UpdatePerFrameDataTree()
	{
		UpdatePerFrameData();
		foreach (IOrbiter child in Children)
		{
			if (child is IParentBody parentBody)
			{
				parentBody.UpdatePerFrameDataTree();
			}
			else
			{
				child.UpdatePerFrameData();
			}
		}
	}
```

Save replay re-checks the live graph before each recursive refresh, because earlier welds in the sorted
chain can change a later target's parent:

```csharp
// kiwis-marbles.lib/KiwisMarblesSubmod.Persistence.cs L116-L134
        foreach (var weld in _welds.ToArray())
        {
            // Earlier dependencies can change this target's parent while the sorted chain applies.
            // Recheck the actual graph before calling the recursive native subtree refresh.
            var ancestors = new HashSet<IParentBody>();
            IParentBody? parent = weld.Target.Parent;
            bool safe = true;
            while (parent != null)
            {
                if (ReferenceEquals(parent, weld.Source) || !ancestors.Add(parent)) { safe = false; break; }
                parent = (parent as IOrbiter)?.Parent;
            }
            if (!safe)
            {
                _welds.Remove(weld);
                context.Warn($"{weld.Source.Id}: a restored dependency would make this body its own ancestor.");
            }
            else if (!CelestialWeldEngine.UpdateWeld(weld)) context.Warn($"{weld.Source.Id}: celestial weld is dormant.");
        }
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `Celestial.SetOrbit(Orbit)` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L149-L152) | Orbit swap; bare assignment |
| `Celestial.Parent`, `Celestial.Children` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L23-L77) | Parent derived from the orbit; child list is a plain field |
| `Celestial.UpdatePerFrameData` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L591-L606) | Frame caches; reads `Parent` position and the orbit-derived frame |
| `Celestial.SphereOfInfluence`, `Rank`, constructor | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L194-L218) | Parent-dependent values computed once |
| `IParentBody.Children`, `UpdatePerFrameDataTree()` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L27-L124) | List surgery and recursive refresh |
| `CelestialSystem.UpdatePerFrameData` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L274-L285) | Root-down refresh through `Children` |
| `Vehicle.ApplyPendingParentChildrenFix` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L631-L647) | The vessel-only equivalent, for comparison |
| `PatchedConic.ScanForEncounters` | [current/decomp/KSA/PatchedConic.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PatchedConic.cs#L1138-L1150) | Encounter candidates come from `PrimaryBody.Children` |
| `FlightPlan` patch search | [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L460) | Same, for flight-plan patches |
| `Orbit` common-ancestor walk | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1755) | Uses `Rank` to decide which side to convert up |
| `Program.DrawMoveCameraTo`, `DrawOrbitLineOption`, `UpdateShaderData` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4039-L4071) | UI and shader data built from `Children` |

## Pitfalls and version notes

- `SphereOfInfluence` and `Rank` keep the values computed for the original parent. A body moved under
  a much heavier or lighter parent keeps its old SOI radius, and the orbit frame-conversion walk compares
  stale ranks (decomp facts; in-game consequences not verified here).[^ksa-celestial][^ksa-orbit]
- `UpdatePerFrameDataTree` recurses without a visited set. A weld that makes a body its own ancestor
  (target orbiting the source, or a chain through vessel parents) would recurse without end. Save replay
  checks for this three ways (self-ancestor at bind, cycles in the proposed parent graph, then the live
  graph before each update); the live "Create Weld" path only rejects duplicate sources and
  source == target.[^km-persistence][^km-submod]
- `Reparent` matches by reference. Old and new parents are compared with `ReferenceEquals`, and the
  `Contains` check keeps the body from being listed twice under the new parent.
- The moved body's own moons are not re-parented: they stay in its `Children` and move with it. Vessels
  in its SOI also stay children of the moved body.
- Vessel bubble eviction after a parent change happens in the vehicle worker since build 5476, so
  vessels near a re-parented body switch bubbles one step later.[^scope-celestial-lights]
- Native load reuses the existing bodies. The reset step restores original orbits, and with them the
  original parents and child lists, before that happens, so the loaded world starts from the authored
  tree.[^km-lib-readme]

## See also

- [Celestial welding by rewriting a body's orbit before every sim step](./kiwis-marbles-celestial-welding.md)
- [Part-anchored vehicle welding](./garrys-torch-vehicle-welding.md) (vessels re-parent through the game's own flight-plan path)
- [Scene save coordinator](./lib-scene-save-coordinator.md)
- [Kiwi's Marbles sub-mod](../submods/kiwis-marbles.md) and [mod overview](../overview.md)
- [SetOrbit plus manual Children surgery and UpdatePerFrameDataTree, with cycle guards on replay](../../../howto/celestials/systems-and-stars/move-or-reparent-a-celestial-body/move-or-reparent-a-celestial-body-1.md) - how-to article demonstrating this feature

[^km-engine]: kiwis-marbles.lib/CelestialWeldEngine.cs
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-celestial-system]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-patched-conic]: KSA.PatchedConic (decompiled, build 2026.10.10.5554)
[^ksa-flight-plan]: KSA.FlightPlan (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^scope-celestial-lights]: scope/celestial-and-lights.md
[^km-lib-readme]: kiwis-marbles.lib/README.md
[^km-persistence]: kiwis-marbles.lib/KiwisMarblesSubmod.Persistence.cs
[^km-submod]: kiwis-marbles.lib/KiwisMarblesSubmod.cs
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
