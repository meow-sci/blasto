---
type: How-To
title: Per-grid memory with AND-merge, public-API snapshot and replay, native plus sidecar saves
description: Remember knocked-out and displaced clutter instances per ecotype and grid spacing, replay them into whichever placement is installed, and save stock grids natively and override-only grids in a sidecar
category: celestials
group: terrain
topic: exclude-or-replace-ground-clutter
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/pebbles-clutter-state-persistence]
techniques: [save-sidecar, harmony-postfix]
hooks:
  - harmony-postfix KSA.GroundClutterRenderer.SerializeSave
  - harmony-prefix KSA.Universe.ExecuteNextClothSolvers
lifecycle: [frame:dispatch-solvers, event:save, event:load]
game_types:
  - KSA.GroundClutterPlacementData
  - KSA.GroundClutterRenderer
  - KSA.BubbleClutterStatics
  - KSA.PhysicsBubble
  - KSA.Universe
  - KSA.GameSave
tags: [ground-clutter, persistence, displaced, exclusion, sidecar, save]
---
Outcome: rocks and trees the player knocked out or sent flying stay gone (or stay in flight) when you swap a body's clutter models, and the holes survive saves. Demonstrated by unscience's Pebbles submod ([Pebbles clutter state persistence](../../../../mods/unscience/features/pebbles-clutter-state-persistence.md)). Read [approach 2](exclude-or-replace-ground-clutter-2.md) first; this is the sub-step of carrying removals across the swap and across saves.

## Background: how the game does it

Since KSA 5447 hitting clutter knocks it loose as a dynamic Bepu body instead of deleting it, and the placement records each removed or displaced instance. A clutter grid is one ecotype at one object separation (the separation decides the cube-sphere resolution, so subcell keys mean nothing on another spacing). Each cell holds `GroundClutterRenderer.ExclusionData`, a bit per candidate subcell where a cleared bit hides that instance ([current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L165-L175)); displaced instances additionally have a `GroundClutterPlacementData.DisplacedObject` record. The public surface is `SerializeSave(ClutterEcotypeSaveData)`, `GetExclusionData`, `ExcludeCell`, `AddDisplacedObject`, `RemoveDisplacedObject` and `DeserializeDisplacedObject` ([current/decomp/KSA/GroundClutterPlacementData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterPlacementData.cs#L122-L160), [current/decomp/KSA/GroundClutterPlacementData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterPlacementData.cs#L391)). Native saves are keyed only by celestial id and ecotype id, with the grid resolution always written as 0.

## Lifecycle and timing

- Drain, remember and replay run inside the same `Universe.ExecuteNextClothSolvers` prefix as the swap, with all solvers and the GPU idle. Draining mirrors `Universe.SyncGroundClutter` for the body's bubbles, because pending hits refer to the old arrays' indices ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2157-L2200)).
- Snapshot **before** `BubbleClutterStatics.Clear`: `Clear` settles displaced bodies, so the snapshot keeps in-flight poses and velocities and they stay loose in the new graph.
- Native save write: `CelestialSystem.SerializeSave` calls `GroundClutterRenderer.SerializeSave`; a postfix edits the list in place right after the game filled it ([current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L1912)).
- Load: the mod's save participant restores originals synchronously before the game's `Universe.DeserializeSave` resets clutter; after native load it reapplies recipes and seeds the override grids. See [save mod state with the scene](../../../persistence/saves/save-mod-state-with-the-scene/overview.md) and the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Keep the memory rules game-independent.** Key by ecotype name plus separation; AND incoming masks into the stored ones (a removed instance is never resurrected); replace displaced records only when the source is authoritative (a placement the mod replayed into); clear the slot bit of every displaced record so the resting instance is not drawn twice:

```csharp
// pebbles.lib/Models/ClutterGridMemory.cs L34-L48
public void Remember(ClutterGridState state, bool authoritative)
{
    var key = state.GridKey();
    if (!_grids.TryGetValue(key, out var grid)) _grids[key] = grid = new Grid(state.Ecotype, state.Separation);
    foreach (var cell in state.Cells) And(grid, (cell.X, cell.Y, cell.Face), cell.Words);
    if (authoritative) grid.Displaced.Clear();
    foreach (var d in state.Displaced)
    {
        grid.Displaced[(d.X, d.Y, d.Face, d.SubCell)] = Copy(d);
        if (d.SubCell >= ClutterCellMask.WordCount * 32) continue;
        var words = ClutterCellMask.AllIncluded();
        words[d.SubCell / 32] &= ~(1u << (int)(d.SubCell % 32));
        And(grid, (d.X, d.Y, d.Face), words);
    }
}
```

2. **Snapshot and replay through public APIs only.** `Snapshot` reads a placement via `SerializeSave`; `Replay` ANDs saved words into `GetExclusionData`/`ExcludeCell`, queues `QueueExclusionUpload` on the render and physical objects, and re-adds remembered displaced records with `DeserializeDisplacedObject(key, record, loose: !Settled)`, skipping slots that no longer exist:

```csharp
// pebbles.lib/Runtime/ClutterGridNative.cs L18-L38
public static ClutterGridState Snapshot(ClutterEcotypeReference ecotype, GroundClutterPlacementData placement)
{
    var native = new ClutterEcotypeSaveData();
    placement.SerializeSave(native);
    var state = new ClutterGridState { Ecotype = ecotype.Name, Separation = ecotype.Placement.ObjectSeparation.InMeters() };
    foreach (var cell in native.Excluded)
    {
        var words = new uint[ClutterCellMask.WordCount];
        var mask = cell.ExclusionData;
        for (var i = 0; i < words.Length; i++) words[i] = mask[i];
        state.Cells.Add(new ClutterCellMask { X = cell.X, Y = cell.Y, Face = cell.FaceId, Words = words });
    }
    foreach (var d in native.DynamicObjects)
        state.Displaced.Add(new ClutterDisplacedState
        {
            X = d.X, Y = d.Y, Face = d.FaceId, SubCell = d.SubCellId, ObjectId = d.ObjectId, ScaleId = d.ScaleId, PackedColor = d.PackedColor, Settled = d.Settled,
            Position = Array(d.PositionCcf), Rotation = Array(d.RotationCcf), RestPosition = Array(d.PositionCcf + d.RestOffsetCcf),
            RestRotation = Array(d.RestRotationCcf), Velocity = Array(d.VelocityCcf), AngularVelocity = Array(d.AngularVelocityCcf)
        });
    return state;
}
```

```csharp
// pebbles.lib/Runtime/ClutterGridNative.cs L41-L71
public static void Replay(ClutterGridState state, KeyHash body, int index, ClutterEcotypeReference ecotype,
    GroundClutterPlacementData placement, ClutterEcotypeRenderData render, ClutterEcotypePhysicalData physics)
{
    foreach (var saved in state.Cells)
    {
        var cell = new CubeCellGrid.Cell(saved.X, saved.Y, saved.Face);
        var mask = placement.GetExclusionData(cell);
        for (var i = 0; i < ClutterCellMask.WordCount; i++) mask[i] &= saved.Words[i];
        placement.ExcludeCell(cell, mask);
        render.QueueExclusionUpload(cell); physics.QueueExclusionUpload(cell);
    }
    if (!HostsObjects(ecotype)) return;
    var existing = new ClutterEcotypeSaveData();
    placement.SerializeSave(existing);
    foreach (var d in existing.DynamicObjects) placement.RemoveDisplacedObject(InstanceKey(body, index, d.X, d.Y, d.FaceId, d.SubCellId));
    foreach (var d in state.Displaced)
    {
        if (d.ObjectId >= ecotype.ClutterObjects.Count || d.ScaleId >= GroundClutterRenderer.MAX_UNIQUE_SCALES)
        {
            Console.WriteLine($"pebbles: skipped displaced {ecotype.Name} object {d.ObjectId}/{d.ScaleId}; its slot no longer exists.");
            continue;
        }
        var record = new GroundClutterPlacementData.DisplacedObject
        {
            ObjectId = d.ObjectId, ScaleId = d.ScaleId, PackedColor = d.PackedColor, Settled = d.Settled,
            PositionCcf = Vector(d.Position), RotationCcf = Quat(d.Rotation), RestPositionCcf = Vector(d.RestPosition),
            RestRotationCcf = Quat(d.RestRotation), VelocityCcf = Vector(d.Velocity), AngularVelocityCcf = Vector(d.AngularVelocity)
        };
        placement.DeserializeDisplacedObject(InstanceKey(body, index, d.X, d.Y, d.Face, d.SubCell), in record, loose: !d.Settled);
    }
}
```

3. **Order the transition:** drain pending hits, snapshot the outgoing placement, replay into the incoming one, then clear the bubble statics.

4. **Split saving by grid.**

| Grid | Saved by | How |
|---|---|---|
| Stock spacing, override enabled | Native save as-is | Same slot count, so a vanilla load shows stock meshes in those slots |
| Override at a different spacing, or disabled | Native save, rewritten | The `SerializeSave` postfix replaces those entries with the remembered **stock**-grid state; on any error it clears them rather than punch unrelated holes |
| Override-only grids | `pebbles.clutter-state` sidecar | Exports memory minus stock keys; restore seeds only unknown grids after the recipe record has reapplied the recipes, then replays matching live grids |

5. **Validate the sidecar.** Bound bodies, grids per body, cells and displaced records per grid, faces 0-5, subcells under 256, scale ids under 16, finite vectors and unique keys.

## Complete example

| File | Role |
|---|---|
| [pebbles.lib/Runtime/ClutterController.Grids.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterController.Grids.cs) | Drain, remember, replay, native save rewrite, sidecar capture and restore |
| [pebbles.lib/Runtime/ClutterGridNative.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterGridNative.cs) | Native placement and save entry adapter |
| [pebbles.lib/Models/ClutterGridMemory.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/ClutterGridMemory.cs) | Game-independent carry-over rules |
| [pebbles.lib/Models/ClutterGridState.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/ClutterGridState.cs) | Sidecar DTOs and validation |
| [pebbles.lib/PebblesSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/PebblesSubmod.Saves.cs) | The two save participants |

## Variations and gotchas

- **Separation changes the grid.** Never replay a mask from one spacing into another; the key includes the separation.
- **A renderer recreation must not erase memory.** A fresh or disabled placement only merges; only a replayed one is authoritative.
- **No reflection on the private exclusion cache.** The 5482 port removed it in favour of the public methods above; keep to the public surface so an update breaks less.
- **Unknown slots are skipped with a log line** rather than failing the replay.

## See also

- [Overview](overview.md); [approach 2](exclude-or-replace-ground-clutter-2.md)
- [Pebbles clutter state persistence](../../../../mods/unscience/features/pebbles-clutter-state-persistence.md)
- [Detect save and load](../../../persistence/saves/detect-save-load/overview.md), [Save mod state with the scene](../../../persistence/saves/save-mod-state-with-the-scene/overview.md)
