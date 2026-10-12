---
type: How-To
title: Build one-part craft in code and set them on the ground
description: Build a single-part craft in code with ModLibrary.Get<PartTemplate>, Vehicle.CreateVehicle and the registry lock, lend the floor height around TeleportToLocation, and strip the furniture from saves
category: physics
group: kinematics
topic: place-a-vessel-on-a-surface-at-latitude-and-longitude
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksastructures/features/office-furniture-craft]
techniques: [harmony-prefix, harmony-postfix, xml-asset]
hooks: [starmap StarMapAfterGui, harmony-postfix KSA.Universe.SerializeSystems]
lifecycle: [frame:draw-ui, frame:input, event:save]
game_types: [KSA.Vehicle, KSA.Part, KSA.PartTemplate, KSA.ModLibrary, KSA.Orbit, KSA.Universe]
tags: [physics, kinematics, spawn, surface, furniture, part, orbit, save, ksastructures]
---

Outcome: an office chair a kitten can sit in and a monitor, each a one-part craft the mod builds in code and sets down on the
office floor 62 m up, whenever they are missing from the world, and strips from every save so a save never names a part
template only this mod provides. Prerequisites: StarMap and Harmony. The XML part with an `IVASeat` and `EVADoor` is the asset
half; this article covers building and placing it.

## Background: how the game does it

`PartTemplate`s load from mod assets; `ModLibrary.Get<PartTemplate>(id)` returns one, but `ModLibrary.TryGet` has no `PartTemplate`
branch in this build and answers false for every part.[^ksa-modlibrary] A craft is built from a root `Part` with
`Vehicle.CreateVehicle` and put in the world by adding it to its parent's `Children`; placing it uses `TeleportToLocation`, whose
start state stands on terrain plus the launch-pad `SurfaceHeight` under the box (see [approach 3](place-a-vessel-on-a-surface-at-latitude-and-longitude-3.md)).
Building mutates the shapes registry, so the build takes `UnlockShapesBlocking`:

```csharp
// current/decomp/KSA/ConstraintSim.cs L135-L153
	public static ShapesUnlock UnlockShapesBlocking()
	{
		int currentManagedThreadId = Environment.CurrentManagedThreadId;
		SpinWait spinWait = default(SpinWait);
		while (true)
		{
			if (Volatile.Read(in _shapesState) == currentManagedThreadId || Interlocked.CompareExchange(ref _shapesState, currentManagedThreadId, 0) == 0)
			{
				_shapesUnlockDepth++;
				return default(ShapesUnlock);
			}
			if (Volatile.Read(in _shapesState) != -1)
			{
				break;
			}
			spinWait.SpinOnce();
		}
		throw new InvalidOperationException("The shapes registry is already unlocked by another thread; shapes mutation is single-threaded");
	}
```

## Lifecycle and timing

| Phase | Hook | Work |
|---|---|---|
| `startup:asset-load` | `mod.toml` assets | Parts, editor tag and materials load from the XML |
| `frame:draw-ui` (the mod's step) | `Office.Update` -> `EnsureChair` | Spawn missing furniture; the step runs after this frame's solvers were dispatched, so `UnlockShapesBlocking` may wait for the worker |
| next frame `frame:input` | engine `ApplyInputEvents` | The queued teleport lands |
| `event:save` | postfix on `Universe.SerializeSystems` | Strip the furniture from the save data |

See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Build the craft. Get the template, build an orbit 500 m above the flown craft (`Orbit.CreateFromStateCci`), take the registry
   lock, create the part and its tree, create the vehicle, add it to its parent, recompute derived data, update per-frame data and
   call `TeleportToLocation`:

```csharp
// src/KSAStructures/Ksa/CraftSpawner.cs L14-L51
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

2. Stand it on the floor. Placement by latitude and longitude stands a craft on the terrain plus the `SurfaceHeight` of the pad
   whose footprint holds the point, so for the length of the spawn set the tower template's `SurfaceHeight` to the floor height
   plus how far the ground under the piece is dug, set a flag so the general teleport lending stays out of the way, and put it
   back in a `finally`:

```csharp
// src/KSAStructures/Ksa/OfficeFurniture.cs L70-L105
        if (_chairFailed || _sinceChairCheck < 3.0 || _tower?.GetStaticObject() is not { } tower) return;
        _sinceChairCheck = 0.0;

        if (!KsaWorld.InFlightScene || KsaWorld.ControlledVehicle is not { } flown || !KsaWorld.IsAlive(flown)) return;
        if (KsaWorld.ParentBody(flown) is not { } body || !ReferenceEquals(body.BodyTemplate, _earth)) return;

        // One a pass: building a craft waits out the engine's worker.
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
    }
}
```

3. Run once per world, one piece per pass: every 3 s of simulated time while a craft is flown on the right body, check each piece
   by vehicle `Id`, spawn at most one missing piece, and give up for the session on a failed spawn. Building a craft waits out the worker.
4. Keep ids stable. The part ids keep their original `KSArmory_` prefix: a save that names a missing part template cannot be
   loaded, so renaming them would break saves that captured them while flown.
5. Strip from saves with a postfix on `Universe.SerializeSystems` that removes every `VehicleData` whose `Id` is a furniture id from
   each `CelestialSystemData.Vehicles`, unless it is the craft being flown (that would remove the player). On load the pieces are
   simply missing and the next pass sets them down again.

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/CraftSpawner.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/CraftSpawner.cs) | `SpawnPartParked` and `ToParentInertial` |
| [src/KSAStructures/Ksa/OfficeFurniture.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeFurniture.cs) | `EnsureChair`: floor height lending, once per pass, save stripping |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L6125-L6181) | The chair part, seat and door |

## Variations and gotchas

- A kitten can only sit in a seat that belongs to a part, so the chair is not part of the building. Sitting is the stock board action (`KittenEva.BoardVehicle`).
- `RootPartWhitelist` on the editor tag lets a part start a craft in the editor.
- A part's shader has an emissive term and a static object's does not, which is why the monitor is a craft.
- `UnlockShapesBlocking` stalls the main thread until the in-flight step finishes: one hitch per spawn.

## See also

- [Overview and comparison](overview.md)
- [Lend the pad template's SurfaceHeight](place-a-vessel-on-a-surface-at-latitude-and-longitude-3.md)
- Mod feature: [KSAStructures office furniture craft](../../../../mods/ksastructures/features/office-furniture-craft.md)
- [UnlockShapesBlocking around vehicle construction](../../colliders/mutate-the-shared-bepu-shape-registry/mutate-the-shared-bepu-shape-registry-3.md)
- The topic spawn-a-craft-on-top-of-a-structure is merged here; spawn-a-vehicle-at-runtime (other batch) covers spawning in full

[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
