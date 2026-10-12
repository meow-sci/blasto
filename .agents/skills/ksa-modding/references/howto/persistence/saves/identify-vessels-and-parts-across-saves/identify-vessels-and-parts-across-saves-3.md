---
type: How-To
title: Anchor a saved window layout on Vehicle.Id and accept best-effort part ids
description: Why a layout file can only store a vehicle id, a part id and a sub-part id, how the first match is chosen after a restart, and where this approach breaks - the counter-example that motivates structural part addresses
category: persistence
group: saves
topic: identify-vessels-and-parts-across-saves
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/saved-terminal-layouts]
techniques: [toml-config]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, event:load]
game_types: [KSA.Vehicle, KSA.Part, KSA.Universe]
tags: [saves, identity, vehicle-id, part-id, layouts, anchors, best-effort]
---

Outcome: a saved layout that puts a terminal panel on a particular part of a rocket and finds it again after the game restarts. purrTTY stores three strings (vehicle id, part id, sub-part id) and resolves them on apply; it is the honest minimal approach and the counter-example that shows why [Reference a specific part with tree paths and a topology hash, resolving to null instead of guessing](identify-vessels-and-parts-across-saves-4.md) exists. Demonstrated by [Saved terminal layouts with startup commands](../../../../mods/purrtty/features/saved-terminal-layouts.md). The layout files themselves are covered in [Persist ImGui window state and layouts](../../user-data/persist-imgui-window-state/overview.md).

## Background: how the game does it

Only `Vehicle.Id` is a reliable key (see the [overview](./overview.md#engine-background)). `Part.Id` is the instance name if the instance has one, else the template id, so identical parts on one craft share it ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1485-L1495)), and `Part.InstanceId` is a counter reset on every load ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L574), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2463)). A layout written to disk can therefore name a part only by an id that may match several.

## Lifecycle and timing

Layouts are applied only on request: a button in the layout window, drawn from `LayoutManagerUI.Draw()` in `[StarMapAfterGui]` (`frame:draw-ui`), because creating an in-world instance allocates GPU resources and needs an active ImGui frame. Per frame, each in-world quad re-resolves its anchor in its own draw path. Nothing runs on load; after a load the already-open quads resolve again against the rebuilt vehicles on their next draw. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Store three ids on the record.** The in-world terminal record keeps the target vehicle id, part id and an optional sub-part id; an empty vehicle id means "the controlled vehicle" ([purrTTY.GameMod/InWorld/Settings/InWorldTerminalRecord.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Settings/InWorldTerminalRecord.cs#L49-L55)).

2. **Resolve the vehicle by id, falling back to the controlled vehicle.** The walk is over `Universe.CurrentSystem.All`; if the vehicle is gone the quad attaches to whatever is flown:

   ```csharp
   // purrTTY.GameMod/InWorld/VehicleLookup.cs L24-L44
   public static Vehicle? Resolve(string? id)
   {
       if (string.IsNullOrEmpty(id))
       {
           return Program.ControlledVehicle;
       }

       var system = Universe.CurrentSystem;
       if (system != null)
       {
           foreach (var vehicle in system.All.UnsafeAsList().OfType<Vehicle>())
           {
               if (vehicle.Id == id)
               {
                   return vehicle;
               }
           }
       }

       return Program.ControlledVehicle;
   }
   ```

3. **Resolve the part by first match.** The top-level part is the first one whose `Id` equals the stored id; a sub-part is the first sub-part of that part with a matching id; with no pick the first part is used so the panel is visible:

   ```csharp
   // purrTTY.GameMod/InWorld/Display/InWorldQuad.cs L393-L436
   private Part? ResolvePart(Vehicle vehicle)
   {
       Part? topLevel = ResolveTopLevelPart(vehicle);
       if (topLevel == null)
       {
           return null;
       }

       // A selected sub-part anchors to it instead of the whole part.
       if (!string.IsNullOrEmpty(_settings.TargetSubPartId))
       {
           foreach (Part sub in topLevel.SubParts)
           {
               if (sub.Id == _settings.TargetSubPartId)
               {
                   return sub;
               }
           }
       }

       return topLevel;
   }

   private Part? ResolveTopLevelPart(Vehicle vehicle)
   {
       if (!string.IsNullOrEmpty(_settings.TargetPartId))
       {
           foreach (Part p in vehicle.Parts.Parts)
           {
               if (p.Id == _settings.TargetPartId)
               {
                   return p;
               }
           }
       }

       // Fallback: the first top-level part so the quad is visible before a pick.
       foreach (Part p in vehicle.Parts.Parts)
       {
           return p;
       }

       return null;
   }
   ```

4. **Follow the part by object identity during the session.** Once resolved, the quad caches the `Part` so it tracks it across a decouple or dock (KSA moves the same `Part` into the new vehicle). The cache is session-only; it is dropped when the ids on the settings change:

   ```csharp
   // purrTTY.GameMod/InWorld/Display/InWorldQuad.cs L301-L315
   // Follow a specifically-targeted part by object identity so the anchor tracks it
   // when its vessel decouples/docks (KSA moves the same Part into the new vehicle).
   // The current owning vehicle (found by the search) drives the transform below, so a
   // decoupled part is placed in its new vessel's frame automatically.
   if (_anchoredPart != null && !string.IsNullOrEmpty(_settings.TargetPartId))
   {
       Vehicle? owner = VehicleLookup.FindContaining(_anchoredPart);
       if (owner != null)
       {
           return ComputePartTransform(camera, owner, _anchoredPart, out model);
       }

       // The tracked part is gone from every vehicle (destroyed) — drop it, re-resolve.
       _anchoredPart = null;
   }
   ```

   The code comment says it plainly: "Identity is session-only (the running id / object is per-run), so a persisted anchor still re-resolves from vehicle + part id on load" ([purrTTY.GameMod/InWorld/VehicleLookup.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/VehicleLookup.cs#L46-L54)).

5. **Skip name clashes on apply; never auto-apply.** `LayoutManager.Apply` skips any terminal whose name collides with a live one and logs it, and a layout is never applied by itself on load ([purrTTY.GameMod/Layouts/LayoutManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Layouts/LayoutManager.cs#L71-L110)).

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/InWorld/VehicleLookup.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/VehicleLookup.cs) | Vehicle lookup with the controlled-vehicle fallback |
| [purrTTY.GameMod/InWorld/Display/InWorldQuad.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/InWorldQuad.cs#L287-L436) | Anchor resolution and follow-by-identity |
| [purrTTY.GameMod/InWorld/Settings/InWorldTerminalRecord.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Settings/InWorldTerminalRecord.cs#L40-L60) | Record fields |
| [purrTTY.GameMod/Layouts/LayoutManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Layouts/LayoutManager.cs#L57-L131) | Apply with collision skip |

## Variations and gotchas

- **First match is a guess.** Two parts with the same template share `Part.Id`; the stored id resolves to the first in `vehicle.Parts.Parts` order, which can be a different physical part after the craft is rebuilt or edited.
- **A missing vehicle falls back to the flown craft**, which can put the panel on the wrong vessel without any warning. Structural references return `null` instead ([Reference a specific part with tree paths and a topology hash, resolving to null instead of guessing](identify-vessels-and-parts-across-saves-4.md)).
- **Renames break the anchor** because nothing follows `Vehicle.SetName` (compare [Key mod data on Vehicle.Id and follow renames and disposals with postfixes](identify-vessels-and-parts-across-saves-1.md)).
- **Acceptable for a UI convenience.** A wrong anchor moves a panel; it does not corrupt data, which is why this trade-off is reasonable here and not for sidecar state that edits the world.

## See also

- [Key mod data on Vehicle.Id and follow renames and disposals with postfixes](identify-vessels-and-parts-across-saves-1.md), [Reference a specific part with tree paths and a topology hash, resolving to null instead of guessing](identify-vessels-and-parts-across-saves-4.md), [overview](./overview.md)
- [Saved terminal layouts with startup commands](../../../../mods/purrtty/features/saved-terminal-layouts.md)
- [Persist ImGui window state and layouts](../../user-data/persist-imgui-window-state/overview.md), [Keeping an anchor on a part across staging, docking, editor changes and saves](../../../vehicles-and-parts/part-geometry/anchor-to-parts-across-vehicle-changes/overview.md)
