---
type: Mod Feature
title: Office furniture as single-part craft, kept out of saves
description: Sets an office chair with an IVA seat and an EVA door, and a monitor, down in the building as one-part craft built in code, and strips them from every save so a save never depends on the mod
mod: ksastructures
category: vehicles-and-parts
techniques: [xml-asset, harmony-postfix, glb-model, save-filter]
hooks: [harmony-postfix KSA.Universe.SerializeSystems, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, event:save, event:board]
game_types:
  - KSA.PartTemplate
  - KSA.EVADoor
  - KSA.IVASeat
  - KSA.EditorTagDefinition
  - KSA.Part
  - KSA.Vehicle
  - KSA.Orbit
  - KSA.ConstraintSim
  - KSA.ModLibrary
  - KSA.KittenEva
  - KSA.Universe
  - KSA.CelestialSystemData
  - KSA.StaticObjectTemplate
tags: [seat, eva-door, iva, spawn, part-xml, save, serialization, furniture]
---

A kitten can only sit in a seat that belongs to a part, so the office chair is not part of the
building: it is a craft of one part, `KSArmory_Prefab_OfficeChair`, with an `IVASeat` and an EVA
door. The desk's monitor is a one-part craft too (a part's shader has an emissive term, a static
object's does not; see [viewport image on the monitor](./viewport-image-on-monitor.md)). The mod
builds both in code and sets them down at the desk whenever they are missing from the world, and it
removes them from every save, so a save never names a part template that only this mod provides.[^mod-furniture][^mod-office-xml]

## How it works

1. **Seat and door in part XML.** The chair `Part` has a body `SubPart` (mesh plus `MeshView`) and a
   hidden door `SubPart` placed beside the seat. Its `PartGameData` declares `<Control />`, an
   `<IVASeat Id="KSAOffice_Chair_Seat">` with eye position and axes, a mass and colliders; a
   `SubPartGameData` gives the door `<EVADoor SeatId="KSAOffice_Chair_Seat" />`.[^mod-office-xml]
   `PartTemplate` maps `EVADoor` and `Control` as XML elements, and `EVADoor.ResolveAlignedSeats`
   pairs a door with the `IVASeat` whose template id equals its `SeatId` on the same part.[^ksa-parttemplate][^ksa-evadoor]
2. **Sitting down is the stock board action.** `KittenEva.BoardVehicle` adds the kitten as crew in
   the door's aligned seat, disposes the EVA kitten, makes the camera follow the host and sets
   `Program.ControlledVehicle = host`.[^ksa-kitteneva] From then on "seated" means "the flown craft
   is the chair", which is how [the computer](./computer-flies-nearest-craft.md) detects it.
3. **An editor tag that can start a craft.** `<EditorTagDef Id="Structures" RootPartWhitelist="true"
   ...>` lists both parts in the editor; the comment in the XML notes that without
   `RootPartWhitelist` a part cannot be the root of a craft.[^mod-office-xml][^ksa-editortagdef]
4. **Building a one-part craft in code.** `CraftSpawner.SpawnPartParked` gets the template with
   `ModLibrary.Get<PartTemplate>` (`TryGet` has no `PartTemplate` branch in this build and returns
   false for every part[^ksa-modlibrary]), builds an orbit 500 m above the flown craft with
   `Orbit.CreateFromStateCci`, takes the shapes lock with `ConstraintSim.UnlockShapesBlocking`
   (which spin-waits while the vehicle update is stepping[^ksa-constraintsim]), creates
   `new Part(id, template)`, `CreateOwnTree()`, `Vehicle.CreateVehicle(system, body2Cce, default,
   parent, id, root, orbit)`, adds it to the parent's children, recomputes derived data and finally
   calls `TeleportToLocation(body, lat, lon)`.[^mod-spawner][^ksa-vehicle][^ksa-part]
5. **Standing it on the office floor, 62 m up.** Placement by latitude and longitude stands a craft
   on the terrain plus the `SurfaceHeight` of the launch pad whose footprint holds the point. For the
   length of the spawn, `EnsureChair` sets the tower template's `SurfaceHeight` to the floor height
   (plus how far the ground under the chair is dug) and sets `_furnishing` so the general
   [teleport roof lending](./terrain-height-roof-override.md) stays out of the way; a `finally`
   puts it back to zero.[^mod-furniture][^mod-terrain]
6. **Once per world, one per pass.** `EnsureChair` runs from the lift's step every 3 s of simulated
   time while a craft is flown on Earth, checks each piece by vehicle `Id` (`"Office chair"`,
   `"Office computer"`), spawns at most one missing piece per pass, and gives up for the session on
   a failed spawn.[^mod-furniture][^mod-lift]
7. **Out of saves.** A postfix on `Universe.SerializeSystems` removes every `VehicleData` whose `Id`
   is a furniture id from each `CelestialSystemData.Vehicles`, unless it is the craft being flown
   (removing that would remove the player). `UniverseData.Create` calls `SerializeSystems` when a
   save is written.[^mod-furniture][^ksa-universedata] On load the pieces are simply missing and the
   next pass sets them down again.
8. **Ids that must never change.** The part ids keep a `KSArmory_` prefix from where they first
   shipped: a save that names a missing part template cannot be loaded, so renaming them would break
   saves that captured them while flown.[^mod-claude] `validate-parts.py` treats that prefix as the
   mod's own.[^mod-validate]

## Lifecycle and timing

| Phase | Hook | Work |
|---|---|---|
| `startup:asset-load` | `mod.toml` `assets` | Parts, editor tag and materials load from `KSAStructuresOffice.xml` |
| `frame:draw-ui` (the mod's step) | `Office.Update` -> `EnsureChair` | Spawn missing furniture. The step runs after this frame's vehicle solvers were dispatched, so `UnlockShapesBlocking` may wait for the worker to finish |
| next frame `frame:input` | engine (`InputEvents.ApplyInputEvents`) | The queued teleport lands |
| `event:board` | engine (`KittenEva.BoardVehicle`) | The kitten becomes crew; the chair becomes the controlled vehicle |
| `event:save` | postfix on `Universe.SerializeSystems` | Strip the furniture from the save data |

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeFurniture.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeFurniture.cs#L10-L24) | Furniture ids, parts and positions |
| [src/KSAStructures/Ksa/OfficeFurniture.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeFurniture.cs#L36-L64) | `KeepOutOfSaves` and the `SerializeSystems` postfix |
| [src/KSAStructures/Ksa/OfficeFurniture.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeFurniture.cs#L66-L104) | `EnsureChair` |
| [src/KSAStructures/Ksa/CraftSpawner.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/CraftSpawner.cs#L14-L51) | `SpawnPartParked` |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L14-L19) | `EditorTagDef Structures` |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L6106-L6181) | Chair part, seat and door |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L6189-L6254) | Monitor part with an emissive material |

## Snippets

A part a kitten can sit in:

```xml
<!-- src/KSAStructures/KSAStructuresOffice.xml L6125-L6151, L6179-L6181 -->
  <Part Id="KSArmory_Prefab_OfficeChair">
    <SubPart Id="KSAOffice_Chair_Body" InstanceOf="KSAOffice_ChairBody_SubPart" />
    <SubPart Id="KSAOffice_Chair_Door" InstanceOf="KSAOffice_ChairDoor_SubPart">
      <Transform>
        <Position X="0.35000" Y="0.45000" Z="0.00000" />
      </Transform>
    </SubPart>
    <Connector Id="_chairConnectorBase">
      <Transform>
        <Position X="0.00000" />
        <Rotation X="3.14159" Z="3.14159" />
      </Transform>
    </Connector>
  </Part>

  <PartGameData Id="KSArmory_Prefab_OfficeChair" DisplayName="Office Chair">
    <EditorTag Value="Structures" />
    <Diameter M="1" />
    <Connector Id="_chairConnectorBase" />
    <Control />

    <!-- The seat top is 0.32 m up; Position is the eye, 0.63 m over the seated kitten's origin, whose hips sit 0.19 m above that origin. -->
    <IVASeat Id="KSAOffice_Chair_Seat">
      <Position X="0.76" Y="0" Z="0" />
      <ForwardAxis Y="1" />
      <UpAxis X="1" />
    </IVASeat>
  <!-- ... -->
  <SubPartGameData Id="KSAOffice_ChairDoor_SubPart">
    <EVADoor SeatId="KSAOffice_Chair_Seat" />
  </SubPartGameData>
```

Building a one-part craft and setting it down:

```csharp
// src/KSAStructures/Ksa/CraftSpawner.cs L19-L41
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
```

Lending the floor height for the spawn, then putting it back:

```csharp
// src/KSAStructures/Ksa/OfficeFurniture.cs L77-L103
        foreach ((string id, string part, double east, double height) in Furniture)
        {
            bool there = false;
            foreach (Vehicle v in KsaWorld.Vehicles) there |= v.Id == id;
            if (there) continue;

            // The launch menu's own placement stands a craft on the pad surface of the landmark it
            // is within the footprint of, so for this one call the tower's surface is whatever this
            // stands on.
            // Over the levelled ground, whatever is dug under it: the basement lies under the office.
            double least = LeastDug(body, east - 3.0, 0.0, 0.4, 0.4);
            tower.Template.SurfaceHeight = new DistanceReference(height + least);
            _furnishing = true;
            try
            {
                _chairFailed = CraftSpawner.SpawnPartParked(flown, part, id, body, LatitudeDeg,
                                                          TowerLongitudeDeg + (east / 97730.0)) is null;
                if (_chairFailed) Log.Warn($"office: the {id.ToLowerInvariant()} could not be set down");
            }
            finally
            {
                _furnishing = false;
                tower.Template.SurfaceHeight = new DistanceReference(GroundSurface);
            }

            return;
        }
```

Keeping the furniture out of saves:

```csharp
// src/KSAStructures/Ksa/OfficeFurniture.cs L51-L64
    private static void AfterSerialize(List<CelestialSystemData> __result)
    {
        try
        {
            string? flown = KsaWorld.ControlledVehicle?.Id;
            foreach (CelestialSystemData system in __result)
            {
                system.Vehicles.RemoveAll(v => v.Id != flown && Array.Exists(Furniture, piece => piece.Id == v.Id));
            }
        }
        catch
        {
        }
    }
```

## Game APIs touched

| API | Decomp | Use |
|---|---|---|
| `PartTemplate.EVADoor`, `PartTemplate.Control` | [current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L95-L99) | XML for door and control |
| `EVADoor.SeatId`, `AlignedSeat`, `ResolveAlignedSeats` | [current/decomp/KSA/EVADoor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EVADoor.cs#L10-L60) | Door-to-seat pairing |
| `KittenEva.BoardVehicle` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L925-L974) | Sitting down |
| `EditorTagDefinition.RootPartWhitelist` | [current/decomp/KSA/EditorTagDefinition.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EditorTagDefinition.cs) | Root-capable parts |
| `ModLibrary.Get<T>` vs `TryGet<T>` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1059) | Part template lookup |
| `Vehicle.CreateVehicle(CelestialSystem, doubleQuat, double3, IParentBody, string, Part, Orbit)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1426) | Vehicle from a part tree |
| `Part(string, PartTemplate, ...)`, `Part.CreateOwnTree` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1456-L1490) | One-part tree |
| `ConstraintSim.UnlockShapesBlocking` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L135) | Shapes registry lock |
| `Universe.SerializeSystems`, `CelestialSystemData.Vehicles` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2480) | Save data filter point |

## Pitfalls and version notes

- **Saves record part ids.** Anything a player can fly or sit in that only your mod defines will
  make saves depend on the mod unless you strip it on save and recreate it on load.
- **Do not strip the flown craft.** The postfix keeps the furniture piece that is the controlled
  vehicle, because the seated kitten is crew inside it.
- **`ModLibrary.TryGet<PartTemplate>` returns false** in 2026.10.10.5554; use `Get`.
- **Spawning from the GUI pass waits for the vehicle worker** through `UnlockShapesBlocking`; the
  mod limits itself to one spawn per pass.
- **The teleport is deferred one frame**, so the craft exists in orbit for a frame before it lands.

## See also

- [Computer flies the nearest craft](./computer-flies-nearest-craft.md) - what the seated kitten can do
- [Viewport image on the monitor](./viewport-image-on-monitor.md) - why the monitor is a part
- [Terrain height roof override](./terrain-height-roof-override.md) - the shared `SurfaceHeight` lending
- [Asset and mesh validation tools](./asset-and-mesh-validation-tools.md) - id and path checks for these parts
- [Sit in a chair to fly another craft - swap ControlledVehicle, borrow a camera window and restore everything on log-off](../../../howto/kittens/animation/animate-and-draw-seated-kitten-crew/animate-and-draw-seated-kitten-crew-3.md) - how-to article demonstrating this feature
- [Filter your own craft out of Universe.SerializeSystems](../../../howto/persistence/saves/keep-mod-vehicles-out-of-saves/keep-mod-vehicles-out-of-saves.md) - how-to article demonstrating this feature
- [Build one-part craft in code and set them on the ground](../../../howto/physics/kinematics/place-a-vessel-on-a-surface-at-latitude-and-longitude/place-a-vessel-on-a-surface-at-latitude-and-longitude-4.md) - how-to article demonstrating this feature
- [Give a chair a seat and a door with the minimum XML](../../../howto/vehicles-and-parts/parts/add-crew-seats-and-eva-doors/add-crew-seats-and-eva-doors-2.md) - how-to article demonstrating this feature
- [Define furniture as a minimal static part that doubles as a craft](../../../howto/vehicles-and-parts/parts/define-a-part-with-game-data/define-a-part-with-game-data-3.md) - how-to article demonstrating this feature
- [Make a furniture part a one-part craft that is never built upon](../../../howto/vehicles-and-parts/parts/ship-ready-made-craft/ship-ready-made-craft-3.md) - how-to article demonstrating this feature
- [Spawn a one-part vehicle built in code](../../../howto/vehicles-and-parts/vehicles/spawn-a-vehicle-at-runtime/spawn-a-vehicle-at-runtime-3.md) - how-to article demonstrating this feature

[^mod-furniture]: src/KSAStructures/Ksa/OfficeFurniture.cs
[^mod-spawner]: src/KSAStructures/Ksa/CraftSpawner.cs
[^mod-terrain]: src/KSAStructures/Ksa/OfficeTerrain.cs
[^mod-lift]: src/KSAStructures/Ksa/OfficeLift.cs
[^mod-office-xml]: src/KSAStructures/KSAStructuresOffice.xml
[^mod-claude]: KSAStructures CLAUDE.md (asset ids section)
[^mod-validate]: tools/validate-parts.py
[^ksa-parttemplate]: KSA.PartTemplate (decompiled, build 2026.10.10.5554)
[^ksa-evadoor]: KSA.EVADoor (decompiled, build 2026.10.10.5554)
[^ksa-kitteneva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
[^ksa-editortagdef]: KSA.EditorTagDefinition (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-universedata]: KSA.UniverseData (decompiled, build 2026.10.10.5554)
