---
type: How-To
title: Build an N x M grid of engines on a live vessel with a single part-tree rebuild
description: Create parts from a template, place them, wire TreeParent and TreeChildren by hand, rebuild the tree once and reinitialise the solver state so the vessel keeps flying
category: vehicles-and-parts
group: modules-and-resources
topic: add-parts-to-a-live-vessel
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/blinky-runtime-part-grid-builder]
techniques: [runtime-part-creation]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:prepare, event:load]
game_types: [KSA.Part, KSA.PartTree, KSA.Vehicle, KSA.PartTemplate, KSA.ModLibrary, KSA.EngineController]
tags: [runtime-parts, part-tree, rebuild, engines, grid, solver-state]
---
Outcome: real engine parts appear on a vessel that is already flying, in a layout of your choosing (flat or wrapped
round a cylinder), without opening the editor. unscience's Blinky adds `columns x rows x 2` engines to a vessel to make a
dot-matrix display (animated in [Switch engines on and off, set throttle and RCS commands, and choose which RCS jets fire](../command-engines-throttle-and-rcs-from-code/overview.md) approach 3). The mechanism is
general: instantiate, place, wire, rebuild once, tell the solver.

Prerequisites: StarMap. Demonstrated by [unscience/blinky-runtime-part-grid-builder](../../../../mods/unscience/features/blinky-runtime-part-grid-builder.md). Feeding the new engines is
[article 2](add-parts-to-a-live-vessel-2.md), and it is not optional.

## Background: how the game does it

See the [overview](overview.md). In short:

- `ModLibrary.Get<PartTemplate>(id)` throws for an unknown id; `TryGet<PartTemplate>` has no part branch and always
  answers false [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L776-L1060)[^ksa-modlibrary] [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1119-L1125).
- `PartTree.CreateFromNewPartTree(root)` transfers every part reachable through `TreeChildren` and marks all derived data
  dirty once; per-part `Part.AddChild` (which calls `PartTree.Merge` and recomputes by default) would recompute N times [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L291-L297)[^ksa-parttree] [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2007-L2010)[^ksa-part].
- `Vehicle.UpdateVehicleConfiguration()` re-initialises the worker's update data before
  `UpdateAfterPartTreeModification`; without it the worker's output lists keep the old gimbal count and
  `UpdateTvcParams` throws `ArgumentOutOfRangeException` on the next step [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1877-L1892)[^ksa-vehicle].
- `EngineController.MinimumThrottle` of every controller is folded into `PartTree.EngineThrottleMin`, which clamps the
  vessel's manual throttle; set it on the new parts before the rebuild.

## Lifecycle and timing

| Step | Where | Frame |
|---|---|---|
| create / repair / destroy | an ImGui click in the submod's content, from `StarMapAfterGui` | `frame:draw-ui` |
| derived data of the new tree | `PartTree.FlushDirtyDerived` / `FlushDirtyResourceManagers` in the next `PrepareFrame` (L2218-2219), unless forced with `EnsureDerived` | `frame:prepare` |
| save replay (rebind, repair, no rebuild) | the scene-save coordinator | `event:load` |

**Known risk:** a click runs after `ExecuteNextVehicleSolvers` dispatched the next step, and Blinky neither waits for
`JobSystems.VehicleSolver` nor routes through a settled seam, so the tree swap and `InitializeUpdateData()` can happen
while a worker steps the same vessel. Stock tree surgery runs from `ApplyInputEvents`. Treat the UI-time rebuild as a
risk, not a pattern: queue the build and run it in the settled window (a prefix on `Universe.ExecuteNextVehicleSolvers`
or a seam as in [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md)). See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Look up the template and get the attachment root:**

```csharp
// blinky.lib/LcdGridBuilder.cs L28-58
    public static BlinkyPixelGrid? BuildGrid(Vehicle vehicle, string gridName, LcdGridConfig config)
    {
        if (vehicle == null) throw new ArgumentNullException(nameof(vehicle));
        if (config == null) throw new ArgumentNullException(nameof(config));

        var swTotal = Stopwatch.StartNew();
        var timings = new List<(string Label, long Ms)>();

        // ── Find attachment root ─────────────────────────────────────────────────
        var root = vehicle.Parts.Root;
        if (root == null)
        {
            Console.WriteLine("blinky: vehicle has no root part — cannot build grid");
            return null;
        }

        // ── Lookup PartTemplate ──────────────────────────────────────────────────
        // NOTE: TryGet<PartTemplate> is NOT supported by ModLibrary — only Get<PartTemplate> works.
        // Get throws NullReferenceException if the id is unknown, so we catch that.
        PartTemplate? template;
        var sw = Stopwatch.StartNew();
        try
        {
            template = ModLibrary.Get<PartTemplate>(config.EnginePartId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"blinky: PartTemplate '{config.EnginePartId}' not found in ModLibrary: {ex.Message}");
            return null;
        }
        timings.Add(("ModLibrary.Get<PartTemplate>", sw.ElapsedMilliseconds));
```

2. **Instantiate parts**, one `new Part(id, template)` each, then place, rotate and scale. A pair of engines mounted back
   to back (yawed -90 and +90 degrees) cancels thrust; on a cylinder the pair yaws `theta +/- 90` so it fires radially:

```csharp
// blinky.lib/LcdGridBuilder.cs L363-364, L384-403
            string partId = $"pixel_{gridName}_{row}_{col}_{slot}";
            var part = new Part(partId, template);
// ...
            else
            {
                // Flat: columns along +X, rows down along -Y.
                px = config.OffsetX + col * config.Spacing;
                py = config.OffsetY - row * config.Spacing;
                pz = config.OffsetZ;

                // a rotates Y=-90°, b rotates Y=+90° (nozzles fire ±X, cancelling thrust).
                rotAngle = slot == "b" ? System.Math.PI / 2.0 : -System.Math.PI / 2.0;
            }

            part.PositionParentAsmb = new double3(px, py, pz);

            double halfAngle = rotAngle / 2.0;
            part.Asmb2ParentAsmb = new doubleQuat(0, System.Math.Sin(halfAngle), 0, System.Math.Cos(halfAngle));

            // Scale down to match blinken's convention (blinken XML uses Scale=0.1).
            part.Scale = new double3(config.PartScale, config.PartScale, config.PartScale);

            return part;
```

3. **Wire the tree by hand.** Nothing touches the live tree yet:

```csharp
// blinky.lib/LcdGridBuilder.cs L99-106
        // ── Tree wiring — TreeParent / TreeChildren ──────────────────────────────
        sw.Restart();
        foreach (var part in createdParts)
        {
            part.TreeParent = root;
            root.TreeChildren.Add(part);
        }
        timings.Add(($"Tree wiring ({createdParts.Count} parts)", sw.ElapsedMilliseconds));
```

4. **Feed and stage-align the parts** (article 2), then set `MinimumThrottle` **before** the rebuild:

```csharp
// blinky.lib/LcdGridBuilder.cs L136-142
        // ── SetMinimumThrottle — iterate EngineControllers ──────────────────────
        // Must run BEFORE the tree rebuild: RecomputeRocketControls folds every MinimumThrottle into
        // PartTree.EngineThrottleMin when the new tree's derived data is computed (lazily since KSA 5482,
        // by the next frame's flush at the latest), and that is what clamps the vehicle's manual throttle.
        sw.Restart();
        SetMinimumThrottle(createdParts, 0.0001f);
        timings.Add(("SetMinimumThrottle", sw.ElapsedMilliseconds));
```

5. **Rebuild once and reinitialise the solver state:**

```csharp
// blinky.lib/LcdGridBuilder.cs L144-163
        // ── PartTree rebuild — CreateFromNewPartTree ─────────────────────────────
        // Walks full TreeChildren hierarchy from root, registers all modules/states,
        // and calls RecomputeAllDerivedData exactly once.
        sw.Restart();
        vehicle.Parts = PartTree.CreateFromNewPartTree(root);
        timings.Add(("PartTree.CreateFromNewPartTree", sw.ElapsedMilliseconds));

        // ── UpdateVehicleConfiguration — reinitialise solver state + resync FlightComputer ──
        // UpdateAfterPartTreeModification alone only calls FlightComputer.ReadUpdatedVehicleConfiguration,
        // which rebuilds VehicleConfig.Gimbals from the new PartTree. But
        // _threadWorkerUpdateState (which holds FlightComputerOutput, including the
        // Gimbals StateList accessed by UpdateTvcParams on the worker thread) is not
        // resized for the new gimbal count. UpdateVehicleConfiguration calls
        // _threadWorkerUpdateState.InitializeUpdateData() first, which reallocates all
        // output state lists to match the new PartTree, then calls
        // UpdateAfterPartTreeModification. Without this the solver crashes with
        // ArgumentOutOfRangeException in UpdateTvcParams on the first frame after build.
        sw.Restart();
        vehicle.UpdateVehicleConfiguration();
        timings.Add(("UpdateVehicleConfiguration", sw.ElapsedMilliseconds));
```

6. **Destroy mirrors build:** disconnect every connection on the parts, remove them from their parents' `TreeChildren`,
   rebuild once from the untouched root and call `UpdateVehicleConfiguration()` again; Blinky does it in two steps (turn
   the grid off, remove the parts 2 s later):

```csharp
// blinky.lib/LcdGridBuilder.cs L208-251
        int partCount = partsToRemove.Count;
        Console.WriteLine($"blinky: destroying grid — removing {partCount} parts (owned={grid.IsOwned})");
        var swTotal = Stopwatch.StartNew();
        var timings = new List<(string Label, long Ms)>();
        var sw = Stopwatch.StartNew();

        // ── Disconnect fuel connections ──────────────────────────────────────────
        foreach (var part in partsToRemove)
        {
            foreach (var conn in part.Connections.ToArray())
            {
                try { conn.Disconnect(); } catch { }
            }
        }
        timings.Add(($"Disconnect fuel connections ({partCount} parts)", sw.ElapsedMilliseconds));

        // ── Manually unlink all pixel parts from the tree ────────────────────────
        sw.Restart();
        foreach (var part in partsToRemove)
        {
            var parent = part.TreeParent;
            if (parent != null)
            {
                parent.TreeChildren.Remove(part);
                part.TreeParent = null;
            }
        }
        timings.Add(($"Tree unlink ({partCount} parts)", sw.ElapsedMilliseconds));

        // ── Rebuild vehicle PartTree once from the now-clean root ────────────────
        // CreateFromNewPartTree walks only the remaining (non-pixel) tree children,
        // so the pixel parts are simply gone, and recompute runs exactly once.
        var root = vehicle.Parts.Root;
        sw.Restart();
        vehicle.Parts = PartTree.CreateFromNewPartTree(root);
        timings.Add(("PartTree.CreateFromNewPartTree", sw.ElapsedMilliseconds));

        // ── UpdateVehicleConfiguration — reinitialise solver state + resync FlightComputer ──
        // Same reason as BuildGrid — must use UpdateVehicleConfiguration (not just
        // UpdateAfterPartTreeModification) so _threadWorkerUpdateState output state lists
        // are resized for the reduced gimbal count after pixel parts are removed.
        sw.Restart();
        vehicle.UpdateVehicleConfiguration();
        timings.Add(("UpdateVehicleConfiguration", sw.ElapsedMilliseconds));
```

7. **Name your parts with a delimiter you control** and parse them back when you need to rediscover them
   (`pixel_{grid}_{row}_{col}_{a|b}`); but a native save drops these names, so persist part paths yourself if you need
   them after a load.

## Complete example

- [blinky.lib/LcdGridBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/LcdGridBuilder.cs#L28-L196)[^unscience-lcdgridbuilder] - `BuildGrid`; [blinky.lib/LcdGridBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/LcdGridBuilder.cs#L201-L260) - `DestroyGrid`
- [blinky.lib/PixelGrid.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/PixelGrid.cs)[^unscience-pixelgrid] - scan and cache of engine controllers per cell

## Variations and gotchas

- **Performance.** With N engines connected to one tank each resource manager's graph search visits all N+1 nodes with
  list visited-checks, so build cost is about N^3; spread engines round-robin over K tanks to cut it by about K^2
  (article 2).
- **Order matters.** Min throttle before the rebuild; stage alignment before the rebuild; `UpdateVehicleConfiguration`
  after.
- **Do not use `UpdateAfterPartTreeModification` alone** after adding gimbal-bearing parts.
- **A save keeps the parts but not their names.** Re-bind by path on load and re-run the feed repair; never respawn.

## See also

- [Overview](overview.md), [Article 2](add-parts-to-a-live-vessel-2.md), [Article 3](add-parts-to-a-live-vessel-3.md)
- [unscience/blinky-runtime-part-grid-builder](../../../../mods/unscience/features/blinky-runtime-part-grid-builder.md)
- [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md), [Spawn a vehicle from code (from the vehicle library or a hand-built part)](../../vehicles/spawn-a-vehicle-at-runtime/overview.md)

[^ksa-modlibrary]: current/decomp/KSA/ModLibrary.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^unscience-lcdgridbuilder]: unscience: blinky.lib/LcdGridBuilder.cs
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: current/decomp/KSA/PartTree.cs (decompiled, build 2026.10.10.5554)
[^unscience-pixelgrid]: unscience: blinky.lib/PixelGrid.cs
