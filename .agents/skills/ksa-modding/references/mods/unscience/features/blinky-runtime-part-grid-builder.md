---
type: Mod Feature
title: Build NxM grids of real engine parts on a live vessel at runtime
description: Blinky creates hundreds of engine parts from a PartTemplate, wires them under the vessel root by hand through TreeParent and TreeChildren, and rebuilds the vessel with one PartTree.CreateFromNewPartTree call instead of merging part by part
mod: unscience
submod: blinky
category: vehicles-and-parts
techniques: [runtime-part-creation, part-tree-rebuild]
hooks:
  - "starmap StarMapAfterGui (via unscience ISubmod.RenderContent: Create button)"
  - "starmap StarMapBeforeGui (via unscience ISubmod.Update: deferred destroy queue)"
lifecycle: [frame:draw-ui, frame:prepare, event:load]
game_types:
  - KSA.Part
  - KSA.PartTree
  - KSA.PartTemplate
  - KSA.Vehicle
  - KSA.ModLibrary
  - KSA.EngineController
  - KSA.DerivedData
tags: [parts, part-tree, runtime-parts, engines, grids, vessels]
---

Blinky turns any loaded vessel into a dot-matrix display. The player picks a vessel, a grid name,
columns, rows, spacing, offset, an engine template and a flat or cylindrical layout, and Blinky adds
`columns x rows x 2` real engine parts to that vessel while it flies. Each pixel is an a/b pair of
engines mounted back to back so their thrust cancels; a pixel "lights" when its engines fire and you
see the plumes. The engine meshes are scaled to about 1% and can be hidden entirely.[^blinky-readme]

Mechanism in one line: instantiate `Part` objects from a `PartTemplate`, position them in the
root's assembly frame, append them to `root.TreeChildren`, then replace `vehicle.Parts` with
`PartTree.CreateFromNewPartTree(root)` and call `vehicle.UpdateVehicleConfiguration()` once.

## How it works

`LcdGridBuilder.BuildGrid(vehicle, gridName, LcdGridConfig)` does the whole build in one call and
logs a timing table for each step:[^lcd-grid-builder][^lcd-grid-config]

1. **Template lookup.** `ModLibrary.Get<PartTemplate>(config.EnginePartId)` (default
   `CorePropulsionA_Prefab_EngineA3`; the UI offers EngineA2 to A6). A missing id throws, so the
   call is wrapped and the build returns `null`.[^ksa-modlibrary]
2. **Instantiation.** For every cell, `CreatePixelPartInstance` calls
   `new Part($"pixel_{gridName}_{row}_{col}_{slot}", template)` for slots `a` and `b`, sets
   `PositionParentAsmb`, `Asmb2ParentAsmb` (a Y-axis quaternion) and a uniform `Scale`.[^ksa-part]
   - Flat: columns along +X, rows down -Y; `a` is yawed -90 degrees and `b` +90 degrees so the
     nozzles fire along -X and +X and cancel.
   - Cylinder: radius `= Width * Spacing / 2pi`, column angle `theta = col * 2pi / Width`, and the
     pair is yawed by `theta +/- 90` degrees so it fires radially in and out.
3. **Manual tree wiring.** Every new part gets `TreeParent = root` and is added to
   `root.TreeChildren`. Nothing touches the live `PartTree` yet.
4. **Propellant feed and stage alignment** (see [feed wiring](./blinky-engine-pixel-feed-wiring.md)),
   then `EngineController.MinimumThrottle = 0.0001` on every new controller. The comment explains
   why this happens before the rebuild: rocket-control recomputation folds every controller's
   minimum into `PartTree.EngineThrottleMin`, which clamps the vessel's manual throttle.[^ksa-enginecontroller]
5. **One rebuild.** `vehicle.Parts = PartTree.CreateFromNewPartTree(root)` walks the whole
   `TreeChildren` hierarchy, transfers every part and module into a new tree and marks all derived
   data dirty once. The source comment contrasts this with per-part `PartTree.Merge`, which would
   recompute derived data N times.
6. **Solver state.** `vehicle.UpdateVehicleConfiguration()` calls
   `_threadWorkerUpdateState.InitializeUpdateData()` before `UpdateAfterPartTreeModification`. The
   comment records the crash this avoids: without it the worker's flight-computer output lists keep
   the old gimbal count and `UpdateTvcParams` throws `ArgumentOutOfRangeException` on the next
   step.[^ksa-vehicle]
7. **Registration.** `PixelGrid.ScanFromVehicle(vehicle, gridName)` parses the `pixel_*` ids back
   into a `(row, col) -> (a, b)` map and caches each cell's `EngineController[]`; the result is
   wrapped in a `BlinkyPixelGrid` that remembers the parts it owns.[^pixel-grid][^blinky-pixel-grid]

Grids are keyed by `(vehicleId, gridName)` in the static `BlinkyGridManager`, so one vessel can carry
several independent grids. Grid names are restricted to letters, digits and `-` because `_` is the id
delimiter.[^blinky-grid-manager]

**Destroy** mirrors the build: disconnect every connection on the pixel parts, remove them from their
`TreeParent.TreeChildren`, rebuild once from the untouched root, and call
`UpdateVehicleConfiguration()` again. The UI does this in two deferred steps: turn the grid off
immediately, then remove the parts 2 s later (`ScheduleDeferred` queue drained from
`Update(dt)`).[^blinky-submod]

**Discovery.** `BlinkyGridManager.ScanAllVehicles` (Debug menu "Scan for blinky grids") calls
`PixelGrid.ScanAllFromVehicle`, which groups every `pixel_{name}_{row}_{col}_{a|b}` part by name and
registers each grid as "Scanned" (not owned). `LcdGridBuilder.ScanExistingGrid` is a fallback for
parts whose names were lost: it matches the template id and a scale below 0.5, pairs parts closer
than 0.1 m, and derives rows from distinct Y and columns from distinct X (flat) or `Atan2` angle
around the centroid (cylinder). It has no caller in this checkout; the README refers to an external
RPC API.

**Saves.** KSA saves the created parts but not their ordinary `pixel_*` names, so a scan cannot
recover a saved grid. The `blinky` save participant stores each cell's part paths
(`SavedPartReference`), ownership, active mask, render flag and scroll state, and on load rebinds the
native parts with `PixelGrid.BuildFromPartGroups`, re-runs `RepairFuelFeeds`, and never respawns
parts.[^blinky-persistence]

## Lifecycle and timing

| Step | Where it runs | Lifecycle |
|---|---|---|
| Create / Repair Feed / Scan / Diagnose buttons | `BlinkySubmod.RenderContent`, called from `[StarMapAfterGui]` (postfix of `Program.OnDrawUiViewports`, `Program.cs` L2268) | `frame:draw-ui` |
| Deferred destroy (turn off, then remove after 2 s) | `BlinkySubmod.Update(dt)`, from `[StarMapBeforeGui]` or the hidden-HUD replay | `frame:draw-ui` |
| Derived data of the new tree (mass, rocket controls, resource groups) | `PartTree.FlushDirtyDerived` / `FlushDirtyResourceManagers` in the next `PrepareFrame` (`Program.cs` L2218-L2219), unless forced earlier with `EnsureDerived` | `frame:prepare` |
| Save replay (rebind and repair, no rebuild) | Scene save coordinator restore | `event:load` |

Since rev 5464 `CreateFromNewPartTree` only marks derived data dirty
(`RecomputeAllDerivedData` -> `MarkDerivedDirty(DerivedData.All)`, `PartTree.cs` L475-L478), and the
next frame's flush rebuilds it before `ExecuteNextVehicleSolvers` (L2220).[^ksa-parttree][^ksa-program]
Blinky forces `DerivedData.ResourceGroups` right away only because its post-build propellant check
reads the resource managers.

The build itself runs from an ImGui click, which is after `ExecuteNextVehicleSolvers` dispatched the
next vehicle step to the worker threads. Nothing in `BuildGrid` or `DestroyGrid` waits for
`JobSystems.VehicleSolver` or routes through the [physics frame hook](./lib-physics-frame-hook.md),
so the tree swap and `InitializeUpdateData()` happen while a worker may be stepping the same vessel.
The game's own tree surgery (`Vehicle.Split` from `Decoupler`, docking merges) is driven from
`InputEvents.ApplyInputEvents` (L2181), after the solvers are joined and before the next
dispatch.[^ksa-decoupler] Treat the UI-time rebuild as a known risk rather than a pattern to copy;
moving the call into `PhysicsFrameHook.Enqueue` gives the same result at a settled point.

## Key code

| Path | Role |
|---|---|
| [blinky.lib/LcdGridBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/LcdGridBuilder.cs#L28-L193) | `BuildGrid`: lookup, instantiation, wiring, one rebuild, verification |
| [blinky.lib/LcdGridBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/LcdGridBuilder.cs#L201-L257) | `DestroyGrid`: disconnect, unlink, one rebuild |
| [blinky.lib/LcdGridBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/LcdGridBuilder.cs#L358-L410) | `CreatePixelPartInstance`: id, flat/cylinder placement, scale |
| [blinky.lib/LcdGridBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/LcdGridBuilder.cs#L516-L610) | `ScanExistingGrid`: spatial recovery of unnamed grids |
| [blinky.lib/LcdGridConfig.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/LcdGridConfig.cs) | Layout, size, spacing, offset, scale, engine id |
| [blinky.lib/PixelGrid.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/PixelGrid.cs) | Id parsing, controller cache, `BuildFromPartGroups` |
| [blinky.lib/BlinkyPixelGrid.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/BlinkyPixelGrid.cs) | Owned vs scanned grid wrapper |
| [blinky.lib/BlinkyGridManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/BlinkyGridManager.cs#L276-L309) | Registry keyed by `(vehicleId, gridName)`, global scan |
| [blinky.lib/BlinkySubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/BlinkySubmod.cs#L492-L593) | Create button, global scan, two-step deferred destroy |
| [blinky.lib/BlinkySubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/BlinkySubmod.Persistence.cs#L90-L124) | Save replay that rebinds native parts |

## Snippets

Wiring, single rebuild and solver resync:

```csharp
// blinky.lib/LcdGridBuilder.cs L99-L171
        // ── Tree wiring — TreeParent / TreeChildren ──────────────────────────────
        sw.Restart();
        foreach (var part in createdParts)
        {
            part.TreeParent = root;
            root.TreeChildren.Add(part);
        }
// ...
        sw.Restart();
        SetMinimumThrottle(createdParts, 0.0001f);
        timings.Add(("SetMinimumThrottle", sw.ElapsedMilliseconds));

        // ── PartTree rebuild — CreateFromNewPartTree ─────────────────────────────
        // Walks full TreeChildren hierarchy from root, registers all modules/states,
        // and calls RecomputeAllDerivedData exactly once.
        sw.Restart();
        vehicle.Parts = PartTree.CreateFromNewPartTree(root);
// ...
        vehicle.UpdateVehicleConfiguration();
// ...
        vehicle.Parts.EnsureDerived(DerivedData.ResourceGroups);
        VerifyPropellantFeeds(createdParts);
```

Per-pixel part creation and placement (flat branch shown):

```csharp
// blinky.lib/LcdGridBuilder.cs L363-L401
            string partId = $"pixel_{gridName}_{row}_{col}_{slot}";
            var part = new Part(partId, template);
// ...
            if (config.Layout == GridLayout.Cylinder)
            {
                // Cylinder: columns wrap around circumference, rows stack along Y.
                // Circumference = Width * Spacing  →  radius = (Width * Spacing) / (2π)
                double radius = (config.Width * config.Spacing) / (2.0 * System.Math.PI);
                double theta = col * 2.0 * System.Math.PI / config.Width;
// ...
            }
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
```

Removal is the same trick in reverse:

```csharp
// blinky.lib/LcdGridBuilder.cs L224-L250
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
// ...
        var root = vehicle.Parts.Root;
        sw.Restart();
        vehicle.Parts = PartTree.CreateFromNewPartTree(root);
// ...
        vehicle.UpdateVehicleConfiguration();
```

Grid discovery from part ids:

```csharp
// blinky.lib/PixelGrid.cs L65-L78
        foreach (var part in PartHelpers.GetAllParts(vehicle))
        {
            if (!part.Id.StartsWith("pixel_")) continue;

            var segments = part.Id.Split('_');
            if (segments.Length != 5) continue;
            if (segments[1] != gridName) continue;
            if (!int.TryParse(segments[2], out int row)) continue;
            if (!int.TryParse(segments[3], out int col)) continue;

            var key = (row, col);
            if (segments[4] == "a") partA[key] = part;
            else if (segments[4] == "b") partB[key] = part;
        }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `ModLibrary.Get<T>(string)` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1059) | Engine `PartTemplate` lookup; throws for unknown ids |
| `Part(string, PartTemplate, PartInstance?, Part?)` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1490) | Creates a pixel part; `Id` is `init`-only, so the name must go through the constructor |
| `Part.Tree` / `TreeParent` / `TreeChildren` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L662-L668) | Manual hierarchy wiring; `Tree` stays null until the rebuild |
| `Part.PositionParentAsmb` / `Asmb2ParentAsmb` / `Scale` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L756-L819) | Placement in the root's assembly frame |
| `PartTree.CreateFromNewPartTree(Part)` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L291-L297) | Builds a new tree from the root subtree, then `RecomputeAllDerivedData` |
| `PartTree.RecomputeAllDerivedData` / `FlushDirtyDerived` / `EnsureDerived(DerivedData)` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L475-L535) | Lazy derived data and the forced early build |
| `Vehicle.Parts` (property, setter assigns `OwningVehicle`) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L605-L616) | Swaps in the rebuilt tree |
| `Vehicle.UpdateVehicleConfiguration()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1877-L1892) | `ReinitializeDerivedValues`, `InitializeUpdateData`, `UpdateAfterPartTreeModification` |
| `EngineController.MinimumThrottle` | [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L38) | Lowered so pixels do not raise the vessel's minimum throttle |
| `Program.PrepareFrame` flush calls | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2216-L2221) | Where the dirty tree is rebuilt before dispatch |
| `Decoupler` activation -> `Vehicle.Split` | [current/decomp/KSA/Decoupler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Decoupler.cs#L159-L167) | Stock tree surgery, driven from the input buffer |

## Pitfalls and version notes

- `CorePropulsionA_Prefab_EngineA1` is gone from the game content (since before 5117); the presets
  and the default now start at A2/A3. `ModLibrary.Get` throws for missing ids rather than returning
  null, so `TryGet`-style probing does not work.[^scope-pixel-grids]
- The source comment says `CreateFromNewPartTree` "calls RecomputeAllDerivedData exactly once". Since
  rev 5464 that call only marks data dirty; anything read right after the build (resource managers,
  `EngineThrottleMin`) is stale until the next `PrepareFrame` flush or an explicit `EnsureDerived`.
- The replaced `PartTree` is abandoned, not disposed. The authors flag a narrow window where a flush
  could still rebuild resource managers on the orphan if something dirtied it earlier in the
  frame.[^scope-pixel-grids]
- Since 5482 `new Part(...)` no longer creates a throwaway tree; `Part.Tree` is null until the
  rebuild, so do not read it on a fresh pixel.
- Build cost grows quickly: every pixel is two full engine parts with their sub-parts and modules.
  The builder distributes fuel connections across all tank parts to keep resource-graph searches
  manageable (see [feed wiring](./blinky-engine-pixel-feed-wiring.md)).
- Native saves drop the `pixel_*` names, so scanning only works within a session; persistence relies
  on part paths in the Unscience sidecar.[^blinky-persistence]
- The tree rebuild happens on the UI thread after the vehicle solver was dispatched (see Lifecycle).
  The authors list the build and Repair Feed under a large grid while thrusting as pending in-game
  checks.[^scope-pixel-grids]

## See also

- [Engine pixel feed wiring](./blinky-engine-pixel-feed-wiring.md)
- [Scroll animation, patterns and pixel control](./blinky-scroll-animation-and-patterns.md)
- [It's So Shiny light-part grids](./its-so-shiny-light-part-grids.md) (same builder with `LightPart`)
- [Part render filter](./lib-part-render-filter.md) (hides the pixel meshes)
- [Physics frame hook](./lib-physics-frame-hook.md)
- [Save part identity](./lib-save-part-identity.md)
- [Blinky sub-mod](../submods/blinky.md)
- [Mod overview](../overview.md)
- [Build an N x M grid of engines on a live vessel with a single part-tree rebuild](../../../howto/vehicles-and-parts/modules-and-resources/add-parts-to-a-live-vessel/add-parts-to-a-live-vessel-1.md) - how-to article demonstrating this feature

[^lcd-grid-builder]: blinky.lib/LcdGridBuilder.cs
[^lcd-grid-config]: blinky.lib/LcdGridConfig.cs
[^pixel-grid]: blinky.lib/PixelGrid.cs
[^blinky-pixel-grid]: blinky.lib/BlinkyPixelGrid.cs
[^blinky-grid-manager]: blinky.lib/BlinkyGridManager.cs
[^blinky-submod]: blinky.lib/BlinkySubmod.cs
[^blinky-persistence]: blinky.lib/BlinkySubmod.Persistence.cs
[^blinky-readme]: blinky/README.md
[^scope-pixel-grids]: scope/pixel-grids-and-render.md (authors' integration analysis)
[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-enginecontroller]: KSA.EngineController (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-decoupler]: KSA.Decoupler (decompiled, build 2026.10.10.5554)
