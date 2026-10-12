---
type: How-To
title: Spawn a one-part vehicle built in code
description: Create a Vehicle from a single PartTemplate with new Part and CreateOwnTree, with no craft file, and set it on the ground
category: vehicles-and-parts
group: vehicles
topic: spawn-a-vehicle-at-runtime
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksastructures/features/office-furniture-craft]
techniques: [xml-asset, starmap-lifecycle]
hooks: [starmap StarMapAfterOnFrame]
lifecycle: [frame:after-on-frame]
game_types: [KSA.Vehicle, KSA.Part, KSA.PartTree, KSA.PartTemplate, KSA.ModLibrary, KSA.Orbit, KSA.ConstraintSim]
tags: [spawn, single-part, furniture, part-template]
---
Outcome: put a craft made of one part (a chair, a monitor, a prop) into the world with nothing but the
part's Id. KSAStructures does this for office furniture, so the furniture is a separate selectable
vehicle that kittens can sit on, instead of geometry baked into a building. No craft save is shipped.

Prerequisites: StarMap, and a part defined in a game-data bundle (see [Define a part with its own game data and GLB model](../../parts/define-a-part-with-game-data/overview.md)).
Demonstrated by KSAStructures [ksastructures/office-furniture-craft](../../../../mods/ksastructures/features/office-furniture-craft.md).

## Background: how the game does it

- `ModLibrary.Get<PartTemplate>(id)` throws `NullReferenceException` for an unknown id and does handle
  `PartTemplate`; `ModLibrary.TryGet<T>` has no `PartTemplate` branch, so for parts it answers false
  for every id [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L776-L1060)[^ksa-modlibrary]. Use `Get`.
- `new Part(name, template)` creates a part instance (a new running instance id, a resolved runtime id)
  [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1490-L1500)[^ksa-part]; `Part.CreateOwnTree()` wraps it, with its subparts, in a tree via
  `PartTree.CreateFromNewSinglePart` [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1456-L1459) [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L273-L289)[^ksa-parttree].
- Everything after that is approach 1: orbit, shapes lock, `CreateVehicle`, `Children.Add`,
  `RecomputeAllDerivedData`, `UpdateAfterPartTreeModification`, `UpdatePerFrameData`, `TeleportToLocation`.

## Lifecycle and timing

The mod places furniture from a throttled check (`EnsureChair`, every 3 simulated seconds) that runs from
its `StarMapAfterOnFrame` handler, one craft per pass, because building a craft waits out the vehicle
step [src/KSAStructures/Ksa/OfficeFurniture.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeFurniture.cs#L66-L77)[^ksastructures-officefurniture]. The wait is the
`UnlockShapesBlocking` call; the teleport is applied in the next frame's `ApplyInputEvents`
(see [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md)).

## Steps

1. Define the part in a game-data XML bundle and list it in `mod.toml`; the part Id is the only coupling
   the C# code has (here `KSArmory_Prefab_OfficeMonitor`; see the `Furniture` table):

```csharp
// src/KSAStructures/Ksa/OfficeFurniture.cs L18-24
    // What stands in the office as craft of its own: metres east of the tower's landmark, and the
    // height of what it stands on above the tower's ground.
    private static readonly (string Id, string Part, double East, double Height)[] Furniture =
    [
        (ChairId, ChairPart, 6.7, OfficeFloor + 0.03),
        (MonitorId, "KSArmory_Prefab_OfficeMonitor", 7.76, OfficeFloor + 0.56 + 0.01),
    ];
```

2. Make the spawner. It lends the flown craft's frame for a throw-away orbit exactly as in approach 1,
   then builds the root part from the template:

```csharp
// src/KSAStructures/Ksa/CraftSpawner.cs L14-51
    public static Vehicle? SpawnPartParked(Vehicle platform, string partId, string id, Celestial body,
                                           double latitudeDeg, double longitudeDeg)
    {
        try
        {
            if (Universe.CurrentSystem is not { } system || platform.Parent is not { } parent) return null;

            // Get, not TryGet: TryGet has no PartTemplate case and answers false for every part.
            PartTemplate template = ModLibrary.Get<PartTemplate>(partId);

            double3 spawnEcl = KsaWorld.PositionEcl(platform) + KsaWorld.LocalUp(platform) * 500.0;
            if (!ToParentInertial(parent, spawnEcl, KsaWorld.VelocityEcl(platform), out double3 posCci, out double3 velCci))
            {
                return null;
            }

            Orbit orbit = Orbit.CreateFromStateCci(parent, Universe.GetElapsedTime(), posCci, velCci,
                                                   new byte4(255, 200, 80, 255));

            using ShapesUnlock shapes = ConstraintSim.UnlockShapesBlocking();
            var root = new Part(id, template);
            root.CreateOwnTree();
            Vehicle craft = Vehicle.CreateVehicle(system, platform.Body2Cce, bodyRates: default, parent, id, root, orbit);
            parent.Children.Add(craft);
            craft.Parts.RecomputeAllDerivedData();
            craft.UpdateAfterPartTreeModification();
            craft.UpdatePerFrameData();
            craft.TeleportToLocation(body, latitudeDeg, longitudeDeg);

            Log.Info($"parked '{id}' ({partId}) at {latitudeDeg:F5}, {longitudeDeg:F5} on {body.Id}");
            return craft;
        }
        catch (Exception e)
        {
            Log.Error("single-part spawn failed", e);
            return null;
        }
    }
```

3. Call it once per missing piece and avoid duplicates by id. The mod skips ids already present among
   the live vehicles, and builds only one per pass:

```csharp
// src/KSAStructures/Ksa/OfficeFurniture.cs L66-80
    // One chair per world: a save that already holds it is left alone.
    private static void EnsureChair(double dtSim)
    {
        _sinceChairCheck += dtSim;
        if (_chairFailed || _sinceChairCheck < 3.0 || _tower?.GetStaticObject() is not { } tower) return;
        _sinceChairCheck = 0.0;

        if (!KsaWorld.InFlightScene || KsaWorld.ControlledVehicle is not { } flown || !KsaWorld.IsAlive(flown)) return;
        if (KsaWorld.ParentBody(flown) is not { } body || !ReferenceEquals(body.BodyTemplate, _earth)) return;

        // One a pass: building a craft waits out the engine's worker.
        foreach ((string id, string part, double east, double height) in Furniture)
        {
            bool there = false;
            foreach (Vehicle v in KsaWorld.Vehicles) there |= v.Id == id;
```

4. Expect `parked '<id>' (<partId>) at <lat>, <lon> on <body>` in the log. A failure logs
   `single-part spawn failed` and the mod stops retrying (`_chairFailed`).

## Complete example

- [src/KSAStructures/Ksa/CraftSpawner.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/CraftSpawner.cs)[^ksastructures-craftspawner] - the spawner
- [src/KSAStructures/Ksa/OfficeFurniture.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeFurniture.cs) - the placement table, duplicate check and save filter
- [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L18)[^ksastructures-ksastructuresoffice] - the editor tag that makes the part a valid root

## Variations and gotchas

- **Part Ids are API.** Rename a part and old saves that contain the vehicle fail to load; see
  [Change a shipped part without breaking saves](../../parts/evolve-a-shipped-part-without-breaking-saves/overview.md).
- **Mod vehicles end up in the save.** KSAStructures removes its furniture from the loaded universe data
  (`system.Vehicles.RemoveAll`, lines 55-58 of `OfficeFurniture.cs`) so the mod re-spawns them cleanly; see
  `keep-mod-vehicles-out-of-saves`.
- **A root part needs more than a template.** To be a complete craft the part must carry a connector and a
  control module and the editor tag must allow it as a root; see [Ship whole-vehicle presets and single-part craft with a part mod](../../parts/ship-ready-made-craft/overview.md).
- **No craft file means no library entry.** The player cannot launch it from the vehicle library;
  that is the point for furniture.

## See also

- [Overview](overview.md), [Approach 1](spawn-a-vehicle-at-runtime-1.md), [Approach 2](spawn-a-vehicle-at-runtime-2.md)
- [ksastructures/office-furniture-craft](../../../../mods/ksastructures/features/office-furniture-craft.md)
- [Define a part with its own game data and GLB model](../../parts/define-a-part-with-game-data/overview.md), [Add crew seats and EVA doors to a part](../../parts/add-crew-seats-and-eva-doors/overview.md)

[^ksa-modlibrary]: current/decomp/KSA/ModLibrary.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: current/decomp/KSA/PartTree.cs (decompiled, build 2026.10.10.5554)
[^ksastructures-craftspawner]: KSAStructures: src/KSAStructures/Ksa/CraftSpawner.cs
[^ksastructures-officefurniture]: KSAStructures: src/KSAStructures/Ksa/OfficeFurniture.cs
[^ksastructures-ksastructuresoffice]: KSAStructures: src/KSAStructures/KSAStructuresOffice.xml
