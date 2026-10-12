---
type: Mod Feature
title: Removed and displaced clutter carried per grid spacing across swaps and saves
description: Pebbles remembers knocked-over and removed clutter per ecotype and exact spacing, replays it into each new placement, rewrites native clutter save entries in a SerializeSave postfix and keeps override grids in a pebbles.clutter-state sidecar record
mod: unscience
submod: pebbles
category: persistence
techniques: [harmony-postfix, save-sidecar, bepu-physics]
hooks:
  - harmony-postfix KSA.GroundClutterRenderer.SerializeSave(List<ClutterEcotypeSaveData>)
  - harmony-prefix KSA.Universe.ExecuteNextClothSolvers (drain, remember and replay during apply and restore)
  - unscience save participants pebbles (order 60) and pebbles.clutter-state (order 61) via the shared native save hooks
lifecycle: [frame:dispatch-solvers, event:save, event:load]
game_types:
  - KSA.GroundClutterRenderer
  - KSA.GroundClutterRenderer.ExclusionData
  - KSA.GroundClutterPlacementData
  - KSA.GroundClutterPlacementData.DisplacedObject
  - KSA.ClutterEcotypeSaveData
  - KSA.BubbleClutterStatics
  - KSA.BubbleClutterStatics.ClutterRemoval
  - KSA.BubbleClutterStatics.ClutterInstanceKey
  - KSA.PhysicsBubble
  - KSA.ClutterEcotypeRenderData
  - KSA.ClutterEcotypePhysicalData
  - KSA.CubeCellGrid
  - KSA.Universe
  - KSA.CelestialSystem
tags: [ground-clutter, saves, sidecar, displaced-clutter, exclusion-masks, bepu, harmony]
---

Since KSA 5447, hitting a rock or tree knocks it loose as a dynamic Bepu body instead of deleting it,
and the game records each removed or displaced instance in its placement data. Pebbles replaces the
placement objects of a body whenever it applies or restores a recipe, which would normally forget
all of that. This feature keeps every hole and every loose object: it remembers them per grid,
replays them into whichever placement uses the same grid, and splits saving between the native
save (stock grids) and a Pebbles sidecar record (grids that only exist while an override is
applied).[^pebbles-readme]

## How it works

**Native model.** A clutter grid is one ecotype at one object separation (the separation decides
the cube-sphere grid resolution, so subcell keys mean nothing on another spacing). Each cell
(face, x, y) holds `GroundClutterRenderer.ExclusionData`, an `InlineArray(8)` of `uint` words, one
bit per 256 candidate subcells; a cleared bit hides that instance. Displaced instances additionally
have a `GroundClutterPlacementData.DisplacedObject` record (object and scale slot, pose, rest pose,
velocities, `Settled`).[^ksa-ground-clutter-renderer][^ksa-placement-data]

**Memory.** Each live record owns a `ClutterGridMemory` keyed by
`ecotype + "|" + separation.ToString("R")`. `Remember` ANDs incoming masks into the stored ones (a
removed instance is never resurrected), replaces displaced records when the source is
authoritative, and clears the slot bit of every displaced record so the resting instance is not
drawn twice. A placement Pebbles replayed into is authoritative for its displaced records; a fresh
or disabled one only merges, so a renderer recreation cannot erase remembered objects
([pebbles.lib/Models/ClutterGridMemory.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/ClutterGridMemory.cs#L8-L48)).

**Native adapter, public API only.** `ClutterGridNative.Snapshot` reads a placement through the
public `GroundClutterPlacementData.SerializeSave(ClutterEcotypeSaveData)`. `Replay` ANDs saved words
into `GetExclusionData`/`ExcludeCell`, queues `QueueExclusionUpload` on both the render and physical
objects, and, if the ecotype hosts objects (biome mask not zero), removes the placement's current
displaced records and re-adds the remembered ones with `DeserializeDisplacedObject(key, record,
loose: !Settled)`; object or scale slots that no longer exist are skipped with a log line. The 5482
port removed the earlier reflection on the private `_exclusionCache`.[^clutter-grid-native][^scope-ground-clutter]

**Transitions.** In every apply or restore, Pebbles first drains the body's pending hits exactly as
`Universe.SyncGroundClutter` does (recording `Displaced` removals with `AddDisplacedObject`), then
snapshots the outgoing placement, replays into the incoming one, and only then clears the bubble
statics. Snapshotting before `BubbleClutterStatics.Clear` is deliberate: `Clear` settles displaced
bodies, so the snapshot keeps in-flight objects' poses and velocities and they stay loose in the new
graph.[^clutter-controller][^ksa-bubble-clutter-statics]

**Saves.** Native saves are keyed only by `CelestialId` and `EcotypeId` (`GridResolution` is always
written as 0), and reload into the stock placement.[^ksa-ground-clutter-renderer] So:

| Grid | Saved by | How |
|---|---|---|
| Stock spacing, override enabled | Native save as-is | Same slot count, so a vanilla load just shows stock meshes in those slots |
| Override at a different spacing, or disabled | Native save, rewritten | Postfix `WriteNativeSave` replaces those entries with the remembered **stock**-grid state; on any error it clears them rather than leave override-grid cells that would punch unrelated holes |
| Override-only grids | `pebbles.clutter-state` sidecar (`ClutterBodyState[]`, order 61) | `CaptureGridStates` exports memory minus stock keys; restore seeds only unknown grids after the `pebbles` record (order 60) has reapplied the recipes, then replays matching live grids |

Validation of the sidecar bounds bodies (256), grids per body (64), cells (262,144) and displaced
records (65,536) per grid, faces 0-5, subcells under 256, scale ids under 16, finite vectors and
unique keys
([pebbles.lib/Models/ClutterGridState.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/ClutterGridState.cs#L55-L93)).[^clutter-grid-state]

## Lifecycle and timing

- **Drain, remember, replay** run inside the `Universe.ExecuteNextClothSolvers` prefix (see
  [ground clutter replacement](./pebbles-ground-clutter-replacement.md)), on the main thread with all
  solvers and the GPU idle; `SyncWindowBubbles` is read there because its getter throws while the
  vehicle task runs.
- **Native save write.** `CelestialSystem.SerializeSave` calls
  `GroundClutterRenderer.SerializeSave(celestialSystemData.GroundClutter)` at L659; the Pebbles
  postfix edits the list in place right after the game filled it, before the save is
  written.[^ksa-celestial-system]
- **Sidecar capture** happens when the shared save hooks capture participants after
  `GameSave.Populate`; capture re-snapshots the live placement first so the record matches the
  native save written in the same operation.[^native-save-hooks]
- **Load.** The `pebbles` reset synchronously restores all originals (which is when the memory
  is discarded with the records) before the game's `Universe.DeserializeSave` resets clutter
  (`GroundClutterRenderer.Reset()`, Universe.cs L2445); after native load the `pebbles` record
  reapplies recipes and `pebbles.clutter-state` seeds and replays the override grids.[^ksa-universe][^pebbles-saves]

## Key code

| Path | Role |
|---|---|
| [pebbles.lib/Runtime/ClutterController.Grids.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterController.Grids.cs) | Drain, remember, replay, native save rewrite, sidecar capture/restore |
| [pebbles.lib/Runtime/ClutterGridNative.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterGridNative.cs) | Native placement and save entry adapter |
| [pebbles.lib/Models/ClutterGridMemory.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/ClutterGridMemory.cs) | Game-independent carry-over rules |
| [pebbles.lib/Models/ClutterGridState.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/ClutterGridState.cs) | Sidecar DTOs and validation |
| [pebbles.lib/PebblesSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/PebblesSubmod.Saves.cs) | The two save participants |
| [pebbles.lib/Runtime/ClutterHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterHooks.cs#L42-L44) | `SerializeSave` postfix registration |
| [pebbles.tests/ClutterStateChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.tests/ClutterStateChecks.cs) | Managed checks for AND masks, authority, spacing isolation, record round trip |

## Snippets

Carry-over rules (AND masks, authoritative displaced sets, cleared slot bits):

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

Replaying a grid through public placement APIs:

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

The native save rewrite postfix body:

```csharp
// pebbles.lib/Runtime/ClutterController.Grids.cs L66-L90
    internal void WriteNativeSave(GroundClutterRenderer renderer, List<ClutterEcotypeSaveData> data)
    {
        foreach (var record in _live.Values)
        {
            if (!ReferenceEquals(record.Owner, renderer) || record.Resources == null || record.OriginalTemplate == null) continue;
            var stock = record.OriginalTemplate.GroundClutterReference!.Ecotypes;
            var live = record.Resources.Graph.Reference.Ecotypes;
            var rewrite = Enumerable.Range(0, stock.Count)
                .Where(i => ClutterGridNative.Key(stock[i]) != ClutterGridNative.Key(live[i]) || !ClutterGridNative.HostsObjects(live[i])).ToArray();
            if (rewrite.Length == 0) continue;
            var entries = rewrite.ToDictionary(i => i, i => data.Where(e => e.CelestialId == record.BodyId && e.EcotypeId == stock[i].Name).ToArray());
            try
            {
                RememberGrids(record, record.Resources.Graph.Reference, record.Resources.Placement, authoritative: true);
                foreach (var (i, matches) in entries)
                    foreach (var entry in matches) ClutterGridNative.WriteNative(record.Memory.Get(ClutterGridNative.Key(stock[i])), entry);
            }
            catch (Exception ex)
            {
                // Never leave override-grid cells in a stock-grid entry: they would punch unrelated holes.
                foreach (var entry in entries.Values.SelectMany(e => e)) { entry.Excluded.Clear(); entry.DynamicObjects.Clear(); }
                Console.WriteLine($"pebbles: {record.BodyId} stock clutter state omitted from native save: {ex.Message}");
            }
        }
    }
```

The two save participants:

```csharp
// pebbles.lib/PebblesSubmod.Saves.cs L15-L24
    public IEnumerable<ISaveParticipant> SaveParticipants
    {
        get
        {
            yield return new SaveParticipant<ClutterSave[]>("pebbles", CaptureClutter, ResetClutter, RestoreClutter, 60, ValidateClutter);
            // Separate v1 record (added for KSA 5482 native clutter saves): removed/displaced clutter on override
            // grids whose spacing differs from stock. Restores after "pebbles" has reapplied the recipes.
            yield return new SaveParticipant<ClutterBodyState[]>("pebbles.clutter-state", _controller.CaptureGridStates, ResetGridState, RestoreGridState, 61, ClutterGridValidation.Validate);
        }
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `GroundClutterRenderer.SerializeSave(List<ClutterEcotypeSaveData>)` | [current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L1912-L1929) | Postfix target; writes one entry per placement with `GridResolution = 0` |
| `GroundClutterRenderer.DeserializeSave`, `Reset` | [current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L1893-L1933) | Load-side counterparts (see pitfalls) |
| `GroundClutterRenderer.ExclusionData`, `ExclusionData.AllIncluded`, `MAX_UNIQUE_SCALES` | [current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L163-L194) | Eight-word cell mask, slot validation |
| `GroundClutterRenderer.ExcludeInstance(...)` | [current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L1445-L1455) | Used by the drain |
| `GroundClutterPlacementData.SerializeSave`, `GetExclusionData`, `ExcludeCell`, `AddDisplacedObject`, `RemoveDisplacedObject`, `DeserializeDisplacedObject` | [current/decomp/KSA/GroundClutterPlacementData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterPlacementData.cs#L111-L391) | Snapshot and replay |
| `ClutterEcotypeSaveData` (`CelestialId`, `EcotypeId`, `Excluded`, `DynamicObjects`, `DisplacedObject.RestOffsetCcf`) | [current/decomp/KSA/ClutterEcotypeSaveData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ClutterEcotypeSaveData.cs#L73-L117) | Native entry layout (rest pose saved as an offset) |
| `Universe.SyncGroundClutter` (private), `Universe.DeserializeSave` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2157-L2201) | Drain logic mirrored; clutter reset on load at L2445 |
| `BubbleClutterStatics.ClutterRemoval`, `ClutterExclusionType.Displaced`, `ClutterInstanceKey`, `Clear` | [current/decomp/KSA/BubbleClutterStatics.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BubbleClutterStatics.cs#L59-L135) | Hit records and statics settling |
| `CelestialSystem.SerializeSave` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L632-L659) | Native caller of the clutter serialiser |

## Pitfalls and version notes

- **5554: the game does not read its own clutter save.** `GroundClutterRenderer.DeserializeSave` has
  an empty body in build 5554 and no game code calls `DeserializeDisplacedObject`; on load
  `Universe.DeserializeSave` calls `GroundClutterRenderer.Reset()` instead. The README's description
  of native restore (written against 5482) therefore does not hold in 5554: stock-grid holes and loose
  objects written to the native save are not restored by the game, while Pebbles' sidecar grids still
  are, because Pebbles replays them itself. Re-check this on every game update.[^ksa-ground-clutter-renderer]
- **Pending hits are lost on save,** as in vanilla: only records already synced into placement data
  are saved; in-flight Bepu bodies are captured only through their native pose and velocity.
- **Spacing isolation.** A grid key includes the separation formatted with `"R"`, so recipes that
  change spacing keep separate memories; returning to the old spacing restores its holes.
- **Disabled ecotypes** (biome mask 0) never receive displaced records and are always rewritten to
  stock state in the native save.
- **Body without a recipe.** Sidecar entries for a body whose recipe did not reapply warn and are
  retained by the save coordinator for the next save.
- Managed tests cover the carry-over rules and the record round trip only; native displacement
  behavior across apply/restore was listed by the authors as needing in-game acceptance.[^clutter-state-checks]

## See also

- [Per-celestial ground clutter replacement](./pebbles-ground-clutter-replacement.md)
- [Native save hooks](./lib-native-save-hooks.md) and [scene save coordinator](./lib-scene-save-coordinator.md)
- [Scene save sidecar](./unscience-scene-save-sidecar.md)
- [Pebbles sub-mod](../submods/pebbles.md) and [mod overview](../overview.md)
- [Per-grid memory with AND-merge, public-API snapshot and replay, native plus sidecar saves](../../../howto/celestials/terrain/exclude-or-replace-ground-clutter/exclude-or-replace-ground-clutter-3.md) - how-to article demonstrating this feature

[^pebbles-readme]: pebbles.lib/README.md
[^ksa-ground-clutter-renderer]: KSA.GroundClutterRenderer (decompiled, build 2026.10.10.5554)
[^ksa-placement-data]: KSA.GroundClutterPlacementData (decompiled, build 2026.10.10.5554)
[^clutter-grid-native]: pebbles.lib/Runtime/ClutterGridNative.cs
[^scope-ground-clutter]: scope/ground-clutter.md (authors' integration notes)
[^clutter-controller]: pebbles.lib/Runtime/ClutterController.cs
[^ksa-bubble-clutter-statics]: KSA.BubbleClutterStatics (decompiled, build 2026.10.10.5554)
[^clutter-grid-state]: pebbles.lib/Models/ClutterGridState.cs
[^ksa-celestial-system]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^native-save-hooks]: ksa-abstractions.lib/Persistence/NativeSaveHooks.cs
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^pebbles-saves]: pebbles.lib/PebblesSubmod.Saves.cs
[^clutter-state-checks]: pebbles.tests/ClutterStateChecks.cs
