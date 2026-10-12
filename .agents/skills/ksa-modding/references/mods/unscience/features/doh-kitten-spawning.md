---
type: Mod Feature
title: Spawn EVA kittens anywhere by replicating EVADoor.CreateKittenEva
description: doh builds KittenEva vessels beside a chosen vehicle by copying the stock EVA door sequence (backpack part with its own tree, eight-argument constructor, CreateFromStateCci orbit, Teleport) after waiting out the vehicle solver
mod: unscience
submod: doh
category: kittens
techniques: [reflection, imgui-window, vehicle-spawning]
hooks:
  - starmap StarMapAfterGui (Spawn and Despawn buttons in ISubmod.RenderContent)
  - starmap StarMapBeforeGui (ISubmod.Update prunes kittens the game removed)
  - reflection KSA.ModLibrary.AllParts
  - reflection KSA.ModLibrary.AllCharacters
lifecycle: [frame:draw-ui]
game_types:
  - KSA.KittenEva
  - KSA.EVADoor
  - KSA.Vehicle
  - KSA.Astronomical
  - KSA.CelestialSystem
  - KSA.LookupCollection
  - KSA.Part
  - KSA.PartTree
  - KSA.Tank
  - KSA.SubstanceLibrary
  - KSA.MixtureReaction
  - KSA.ModLibrary
  - KSA.SerializedCollection
  - KSA.CharacterReference
  - KSA.Orbit
  - KSA.StateVectors
  - KSA.Universe
  - KSA.JobSystems
  - Brutal.Concurrency.Jobs.JobScheduler
  - KSA.ConstraintSim
  - KSA.VehicleUpdateTask
  - KSA.InputEvents
tags: [kittens, eva, spawning, kitteneva, backpack, orbit, teleport, vehicle-solver, bepu, imgui]
---

doh ("Dynamically Originating Hominids") puts new EVA kittens into the world on demand: pick a
vehicle, a character (or random), a body-frame offset and a count of 1 to 20, and the kittens appear
in a chain in front of that vehicle, each a real `KittenEva` with a backpack full of MMH/NTO that the
player can take control of. Individual kittens or all of them can be despawned again. Optional
per-kitten colour is a separate mechanism, see
[doh runtime GPU materials](./doh-runtime-gpu-materials.md).[^doh-lib-readme]

Mechanism in one line: `KittenSpawner` performs the same steps as the private
`EVADoor.CreateKittenEva` (backpack `Part` with its own tree, the public eight-argument `KittenEva`
constructor, `Orbit.CreateFromStateCci`, `Teleport`, `Parent.Children.Add`, `UpdatePerFrameData`),
but takes the position, orientation, character and name from a `SpawnRequest` instead of an IVA seat,
and blocks on `JobSystems.VehicleSolver.Wait()` first because it runs from the UI pass.[^kitten-spawner][^ksa-evadoor]

## How it works

1. **Request.** `SpawnRequest` has two positioning modes: relative to `ReferenceVehicle` (or an
   unambiguous `ReferenceVehicleId`) with `OffsetBodyFrame` in metres (X right, Y up, Z forward,
   default 10 m ahead), or absolute `PositionCci`/`VelocityCci` around a named `ParentBodyName`.
   `Count` (1 to 100 in the library, 1 to 20 in the UI), `CharacterId` (null = random),
   `TintColor`, `UniqueMaterialsPerKitten` and `PerKittenColors` complete it.[^spawn-request]
2. **Positioning** ([doh.lib/Spawning/KittenSpawner.Positioning.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.Positioning.cs#L21-L48)).
   For the vehicle mode it reads `Orbit.StateVectors` once and keeps `StateTime`, `PositionCci` and
   `VelocityCci` from that same struct, transforms the offset with `Vehicle.GetAsmb2Cci()`, copies
   `Body2Cce` and `BodyRates` (NaN rates become zero) and the parent body. Pairing the position with
   a separately read clock would shift the kitten by velocity times the time difference; `EVADoor`
   also uses `stateVectors.StateTime` (L212, L224).[^kitten-spawner-positioning][^ksa-evadoor]
   The absolute mode builds a temporary orbit at `Universe.GetElapsedTime()` and uses an identity
   orientation.
3. **Character.** `ModLibrary.AllCharacters` is `internal static`, so the spawner reflects the field
   and calls `SerializedCollection.GetList()`; a random entry is picked when none is requested.[^kitten-spawner-catalog][^ksa-modlibrary]
4. **Budget preflight.** Tinted requests are refused up front if their material sets would not fit
   the GPU material pool, and each iteration needs at least two free slots before a `KittenEva` is
   built (its avatar allocates a fur material). Details in
   [doh runtime GPU materials](./doh-runtime-gpu-materials.md).[^kitten-spawner]
5. **Per kitten**, in [doh.lib/Spawning/KittenSpawner.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.cs#L174-L256):
   - a unique id `Kitten_doh_N`, skipping ids already in `CelestialSystem.All`;
   - a backpack: `KittenBackPackPart` looked up with `AllParts.Find(KeyHash)` by reflection,
     `new Part(id, template)`, `CreateOwnTree()`, `ReinitializeDerivedValues()`, every `Tank`
     configured for the `MMH_NTO` mixture at its default ratio, then `RefillConsumables()`;
   - chain offset `OffsetCci * (i + 1)`, so kitten 1 is one offset away, kitten 2 two offsets;
   - `WaitForVehicleSolverIdle()`, then `new KittenEva(system, characterId, body2Cce, bodyRates,
     parent, id, backpackPart, referenceOrbit)` inside a `try`;
   - `Orbit.CreateFromStateCci(parent, stateTime, posCci, velCci, colour)`, `Teleport(orbit, null,
     null)`, `parent.Children.Add(kitten)`, `UpdatePerFrameData()`, exactly the tail of
     `EVADoor.CreateKittenEva` (L224-L227);
   - optional material cloning, then `SpawnedKittenRegistry.Register`.
6. **Results.** `SpawnResult` lists every kitten spawned, also when a batch stops part-way, and puts
   "spawned but untinted" problems in `Warning` rather than `Error`.[^spawn-result]
7. **Registry** ([doh.lib/Spawning/SpawnedKittenRegistry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/SpawnedKittenRegistry.cs#L11-L86)).
   A dictionary keyed by kitten id that also keeps the runtime `Vehicle` object, so a kitten renamed
   in game still resolves. `GetDisposed()` allocates only when something was disposed, because it is
   polled every frame.[^spawned-kitten-registry]
8. **Despawn and prune.** `Despawn` prefers the stored `KittenEva` object (never a non-kitten that
   happens to share the id), waits for the solver, calls `CelestialSystem.Deregister` and
   `Vehicle.Dispose`, then drops the entry and releases its material set if no other kitten uses it.
   `PruneDisposed` runs every frame from `DohSubmod.Update` and cleans up kittens the game destroyed
   itself, for example by boarding.[^kitten-spawner][^doh-submod]
9. **Target selection held as an object.** The UI stores the selected `Vehicle`, never a list index.
   `CelestialSystem.Deregister` goes through `LookupCollection.Deregister`, which moves the last element
   into the freed slot, and `VehicleProvider.GetAllVehicles()` also filters debris, so an index
   silently moved onto another vehicle (often a freshly spawned kitten) whenever anything was
   removed. A selection that disappears is cleared, not replaced.[^doh-submod][^ksa-lookupcollection][^vehicle-provider]
10. **Scene saves.** Native KSA saves own the kittens. The doh save record only rebinds registry
    ownership and cloned materials to the natively restored `KittenEva` objects by vehicle id and
    never spawns replacements; it is described with the material code in
    [doh runtime GPU materials](./doh-runtime-gpu-materials.md).[^doh-persistence]

## Lifecycle and timing

| Work | Runs from | Frame phase |
|---|---|---|
| Spawn, "I'm Feeling Lucky", Despawn, Despawn All | Button handlers in `DohSubmod.RenderContent`, drawn from `[StarMapAfterGui]` (unscience window or standalone F8 window) | `frame:draw-ui`, postfix of `OnDrawUiViewports` (`Program.cs` L2268) |
| `PruneDisposed`, stale material release | `DohSubmod.Update` from `[StarMapBeforeGui]`, or the hidden-HUD replay in unscience | `frame:draw-ui`, prefix of `OnDrawUiFrame` (L2267) |

Why the explicit wait: by the time the UI runs, `Program.PrepareFrame` has already called
`Universe.ExecuteNextVehicleSolvers` (L2220), so `VehicleUpdateTask.Run` may be stepping on the
`VehicleSolver` thread. `Run` starts with `ConstraintSim.BeginVehicleUpdate()`, which sets the shapes
lock to -1, and `ConstraintSim.UnlockShapes()` throws "The shapes registry cannot be mutated while the
vehicle update is stepping" while it is held.[^ksa-program][^ksa-vehicleupdatetask][^ksa-constraintsim]
Both directions hit that lock:

- constructing a vehicle: `Vehicle` constructor (L1511) -> `UpdateVehicleConfiguration` ->
  `UpdateAfterPartTreeModification` -> `UpdateCollisionGeometry`, which opens `UnlockShapes()` at
  L1946;[^ksa-vehicle]
- disposing one: `Vehicle.Dispose` -> `VehicleUpdateState.Dispose` (L676) ->
  `BepuHandles.Dispose`, which opens `UnlockShapes()` at L146.[^ksa-vehicleupdatestate][^ksa-bepuhandles]

`JobScheduler.Wait()` blocks until every runner of the scheduler is idle; `PrepareFrame` waits on the
same scheduler at L2158 at the start of the next frame, and nothing re-queues the solver before then,
so the wait only moves an existing stall earlier.[^brutal-jobscheduler][^ksa-jobsystems] The stock EVA
button avoids the race differently: it adds an `EvaSpawnData` to `InputEvents.EvaSpawnBuffer`
(`EVADoor.cs` L84, L145), which `ApplyInputEvents` drains at L2181, after the solver results were applied
and before the next step is dispatched.[^ksa-inputevents]

Consequences of spawning from the UI pass:

- The new kitten is not part of the step already in flight; it is first snapshotted by the next
  `ExecuteNextVehicleSolvers`.
- `Program.RefreshVehiclesInFrame` runs only in `PrepareFrame` (L2189), so the kitten is likely first
  drawn in the following frame.
- All `KittenSpawner` methods are main-thread only; the library README says so explicitly.[^doh-lib-readme]

## Key code

| Path | Role |
|---|---|
| [doh.lib/Spawning/KittenSpawner.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.cs#L139-L267) | `SpawnInternal`: validation, preflight, per-kitten loop |
| [doh.lib/Spawning/KittenSpawner.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.cs#L54-L112) | `Despawn`, `DespawnAll`, `PruneDisposed` |
| [doh.lib/Spawning/KittenSpawner.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.cs#L293-L344) | Orphan cleanup, solver wait, unique names |
| [doh.lib/Spawning/KittenSpawner.Positioning.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.Positioning.cs) | Vehicle-relative and absolute spawn frames |
| [doh.lib/Spawning/KittenSpawner.Catalog.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.Catalog.cs) | Backpack part, `MMH_NTO` mix, reflected part and character catalogs |
| [doh.lib/Spawning/SpawnRequest.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/SpawnRequest.cs) | Input DTO |
| [doh.lib/Spawning/SpawnResult.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/SpawnResult.cs) | Result DTOs with partial-batch reporting |
| [doh.lib/Spawning/SpawnedKittenRegistry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/SpawnedKittenRegistry.cs) | Tracking, shared-set use test, disposed scan |
| [doh.lib/DohSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/DohSubmod.cs#L232-L273) | Vehicle picker that holds the `Vehicle` object |
| [doh.lib/DohSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/DohSubmod.cs#L466-L527) | `DoSpawn`, `DoFeelingLucky`, `GetSpawnTarget` |
| [ksa-abstractions.lib/VehicleProvider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/VehicleProvider.cs#L22-L38) | Debris-filtered list and unambiguous id lookup |

## Snippets

The per-kitten core, the same call order as `EVADoor.CreateKittenEva`:

```csharp
// doh.lib/Spawning/KittenSpawner.cs L188-L225
            // Calculate this kitten's position offset
            double3 chainOffset = pos.OffsetCci * (i + 1);
            double3 kittenPosCci = pos.BasePositionCci + chainOffset;
            double3 kittenVelCci = pos.VelocityCci;

            // Create KittenEva. The Vehicle ctor mutates the BepuPhysics shapes registry
            // (BepuHandles.Create -> ConstraintSim.UnlockShapes), which throws if the
            // background vehicle physics step is still running. Wait for it to go idle.
            WaitForVehicleSolverIdle();
            KittenEva kittenEva;
            try
            {
                kittenEva = new KittenEva(
                    system,
                    characterId,
                    pos.Body2Cce,
                    pos.BodyRates,
                    pos.Parent!,
                    kittenName,
                    backpackPart,
                    pos.ReferenceOrbit!);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"doh: KittenEva construction failed for '{kittenName}': {ex}");
                RemoveOrphanedKitten(system, kittenName);
                return Stopped(results, request.Count, $"Kitten construction failed: {ex.Message}");
            }

            // Create orbit and teleport
            var orbitColor = pos.ReferenceOrbit?.OrbitLineColor ?? new byte4(255, 200, 0, 255);
            var orbit = Orbit.CreateFromStateCci(
                pos.Parent!, pos.StateTime, kittenPosCci, kittenVelCci, orbitColor);
            kittenEva.Teleport(orbit, null, null);

            // Register with parent body
            pos.Parent!.Children.Add(kittenEva);
            kittenEva.UpdatePerFrameData();
```

The backpack, with the tree the `Part` constructor no longer builds:

```csharp
// doh.lib/Spawning/KittenSpawner.Catalog.cs L12-L32
    private Part? CreateBackpackPart()
    {
        var partTemplate = FindPartTemplate("KittenBackPackPart");
        if (partTemplate == null) return null;

        var part = new Part(partTemplate.Id, partTemplate);
        // KSA 5482 no longer builds a tree in the Part constructor; mirror EVADoor.GetBackPackPart.
        var tree = part.CreateOwnTree();
        tree.ReinitializeDerivedValues();

        var mix = TryGetReactantMix("MMH_NTO");
        if (mix != null)
        {
            var tanks = part.SubtreeModules.Get<Tank>();
            for (int i = 0; i < tanks.Length; i++)
                tanks[i].ConfigureFor(mix);
        }

        tree.RefillConsumables();
        return part;
    }
```

The stock version in build 5554, which now auto-assigns tank mixes instead of naming one:

```csharp
// current/decomp/KSA/EVADoor.cs L295-L312
	private Part? GetBackPackPart()
	{
		PartTemplate partTemplate = ModLibrary.AllParts.Find(KeyHash.Make("KittenBackPackPart".AsSpan()));
		if (partTemplate == null)
		{
			return null;
		}
		Part part = new Part(partTemplate.Id, partTemplate);
		PartTree partTree = part.CreateOwnTree();
		partTree.ReinitializeDerivedValues();
		partTree.RecreateResourceManagers();
		if (partTree.ReconfigureTanks())
		{
			partTree.RecreateResourceManagers();
		}
		partTree.RefillConsumables();
		return part;
	}
```

Removing a kitten whose constructor threw after the base constructor registered it:

```csharp
// doh.lib/Spawning/KittenSpawner.cs L298-L311
    private static void RemoveOrphanedKitten(CelestialSystem system, string kittenName)
    {
        if (!system.All.TryGet(kittenName, out Astronomical? orphan) || orphan == null) return;

        try { system.Deregister(orphan); }
        catch (Exception ex) { Console.WriteLine($"doh: Failed to deregister orphaned '{kittenName}': {ex.Message}"); }

        if (orphan is Vehicle vehicle)
        {
            try { vehicle.Dispose(); }
            catch (Exception ex) { Console.WriteLine($"doh: Failed to dispose orphaned '{kittenName}': {ex.Message}"); }
        }
        Console.WriteLine($"doh: Removed partially constructed kitten '{kittenName}'");
    }
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `EVADoor.CreateKittenEva` / `GetBackPackPart` (private) | [current/decomp/KSA/EVADoor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EVADoor.cs#L196-L312) | The sequence being replicated (not called) |
| `KittenEva(CelestialSystem, string, doubleQuat, double3, IParentBody, string, Part, Orbit)` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L78-L83) | Public constructor; builds `_renderable` after the base constructor |
| `Astronomical` constructor (`register: true`) | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L201-L220) | Registers the kitten in `System.All` before `KittenEva` finishes |
| `Vehicle` constructor, `UpdateVehicleConfiguration`, `UpdateCollisionGeometry` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1431-L1514) | Construction path that opens the shapes lock (L1946) |
| `Vehicle.Teleport`, `UpdatePerFrameData`, `GetAsmb2Cci`, `Body2Cce`, `BodyRates`, `Dispose` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2241) | Placement and removal (`Dispose` at L3775-L3798 also deregisters) |
| `Orbit.CreateFromStateCci`, `Orbit.StateVectors`, `OrbitLineColor` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1611) | Spawn orbit from one consistent state |
| `CelestialSystem.All`, `Register`, `Deregister` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L66-L107) | Unique names, orphan lookup, despawn |
| `LookupCollection<T>.Deregister` | [current/decomp/KSA/LookupCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LookupCollection.cs#L148-L161) | Swap-remove that invalidates list indices |
| `Part(string, PartTemplate, ...)`, `Part.Tree`, `Part.CreateOwnTree` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1456-L1459) | Backpack root part with its own tree (`Tree` is nullable, L662) |
| `PartTree.ReinitializeDerivedValues`, `RefillConsumables`, `ReconfigureTanks` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L857-L885) | Backpack setup (stock uses `ReconfigureTanks`) |
| `Tank.ConfigureFor(ReactantMix, bool)` | [current/decomp/KSA/Tank.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Tank.cs#L861-L888) | Fills the tank recipe and recreates resource managers |
| `SubstanceLibrary.TryGetReaction`, `MixtureReaction.AtMixtureRatio`, `DefaultMixtureRatio` | [current/decomp/KSA/MixtureReaction.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MixtureReaction.cs#L9-L11) | `MMH_NTO` mix (`TryGetReaction` in [current/decomp/KSA/SubstanceLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SubstanceLibrary.cs#L218)) |
| `ModLibrary.AllParts`, `AllCharacters` (internal static) | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L86-L100) | Reflected catalogs |
| `SerializedCollection<T>.Find(KeyHash)`, `GetList()` | [current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L57-L62) | Invoked by reflection on those catalogs |
| `ConstraintSim.BeginVehicleUpdate`, `UnlockShapes`, `UnlockShapesBlocking` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L111-L153) | The lock the solver wait avoids |
| `VehicleUpdateTask.Run` | [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L261-L263) | Takes the lock for the whole step |
| `JobSystems.VehicleSolver`, `JobScheduler.Wait` | [current/decomp/Brutal.Concurrency.Jobs/JobScheduler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Concurrency.Jobs/JobScheduler.cs#L53-L59) | Blocking wait (`JobSystems.cs` L14) |
| `InputEvents.EvaSpawnBuffer` | [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1115-L1122) | Stock staging alternative |
| `Program.PrepareFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2158-L2220) | Wait, apply, input, dispatch order |

## Pitfalls and version notes

- **5482: `Part` no longer creates a tree.** Without `CreateOwnTree()` every spawn threw a null
  reference in `ConfigureFor`, and the `Vehicle` constructor adopts `root.Tree`; never suppress the
  nullable `Part.Tree` with `!`.[^scope-character]
- **5554 drift:** stock `GetBackPackPart` now calls `RecreateResourceManagers` and
  `PartTree.ReconfigureTanks` (automatic mix assignment) where doh still names `MMH_NTO` and calls
  `Tank.ConfigureFor` per tank (which recreates resource managers itself). Both fill the tanks; a
  future backpack with a different propellant would only follow the stock path.
- **5018: reaction ids lost the ratio.** The old combustion id `MMH_NTO_1.6` resolves to nothing;
  the mix comes from `MixtureReaction.AtMixtureRatio(DefaultMixtureRatio)` (`Core/Reactions.xml`
  defines `MMH_NTO` as a `MixtureReaction`).[^kitten-spawner-catalog]
- **Reflection that could be typed.** `ModLibrary.Get<PartTemplate>("KittenBackPackPart")` is public
  and would replace the `AllParts` reflection, but it throws `NullReferenceException` for a missing id
  instead of returning null (`ModLibrary.cs` L1119-L1125). Character enumeration has no public
  equivalent, so `AllCharacters` stays reflected.[^ksa-modlibrary]
- **Half-built kittens.** Registration happens in the `Astronomical` base constructor; if
  `new KittenRenderable(...)` then throws (the classic case was a full GPU material pool), the
  registered kitten has no renderable and is updated every frame. `RemoveOrphanedKitten` handles
  it, and `Vehicle.Dispose` deregisters again, harmlessly, because `LookupCollection.Deregister` is a
  no-op for a missing id.[^ksa-astronomical][^ksa-kitteneva]
- **Never index into vehicle lists across frames.** Any mod that stores an index into
  `GetAllVehicles()` has the same swap-remove hazard doh fixed.
- **Large batches.** The library accepts up to 100 per request. The authors flag that the 5474
  per-view mesh bucket limit (1024 instances, 256 draws per view and pass) could be exceeded in one
  view by a very large batch (not checked in game here).[^scope-character]
- **Spawned kittens are controllable** (`KittenEva.IsControllable => true`, L63). They are never
  added to `Universe.KittenRoster`, so the `KittenRoster.Find(Id)?.EndMission()` call in
  `Vehicle.Dispose` should find nothing for a `Kitten_doh_N` id (inferred from the code, not checked
  in game).

## See also

- [doh runtime GPU materials](./doh-runtime-gpu-materials.md) (tints, pool budget, scene saves)
- [Thin game-state providers](./lib-game-state-providers.md) (`VehicleProvider`)
- [Named XKCD colour catalog](./lib-xkcd-color-catalog.md) (the colour combo and "I'm Feeling Lucky")
- [Physics-safe vessel mutation at the PrepareFrame solver handoff](./lib-physics-frame-hook.md) (the staging alternative to blocking on the solver)
- [Iron Man kitten rocket flight](./iron-man-kitten-rocket-flight.md) (another mod that manipulates live `KittenEva` vessels)
- [doh sub-mod](../submods/doh.md) and the [mod overview](../overview.md)
- [Compose a Character from the shared asset blocks](../../../howto/kittens/characters/define-a-kitten-character-in-asset-xml/define-a-kitten-character-in-asset-xml.md) - how-to article demonstrating this feature
- [Replicate EVADoor.CreateKittenEva with the public KittenEva constructor, place the kitten beside a vessel, then track and despawn it](../../../howto/kittens/characters/spawn-an-eva-kitten-from-code/spawn-an-eva-kitten-from-code-1.md) - how-to article demonstrating this feature
- [Wait on JobSystems.VehicleSolver before mutating vehicles](../../../howto/vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/change-vehicles-without-racing-the-solver-2.md) - how-to article demonstrating this feature

[^kitten-spawner]: doh.lib/Spawning/KittenSpawner.cs
[^kitten-spawner-positioning]: doh.lib/Spawning/KittenSpawner.Positioning.cs
[^kitten-spawner-catalog]: doh.lib/Spawning/KittenSpawner.Catalog.cs
[^spawn-request]: doh.lib/Spawning/SpawnRequest.cs
[^spawn-result]: doh.lib/Spawning/SpawnResult.cs
[^spawned-kitten-registry]: doh.lib/Spawning/SpawnedKittenRegistry.cs
[^doh-submod]: doh.lib/DohSubmod.cs
[^doh-persistence]: doh.lib/DohSubmod.Persistence.cs
[^doh-lib-readme]: doh.lib/README.md
[^vehicle-provider]: ksa-abstractions.lib/VehicleProvider.cs
[^scope-character]: scope/character-and-materials.md
[^ksa-evadoor]: KSA.EVADoor (decompiled, build 2026.10.10.5554)
[^ksa-kitteneva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-lookupcollection]: KSA.LookupCollection (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatetask]: KSA.VehicleUpdateTask (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatestate]: KSA.VehicleUpdateState (decompiled, build 2026.10.10.5554)
[^ksa-bepuhandles]: KSA.BepuHandles (decompiled, build 2026.10.10.5554)
[^ksa-jobsystems]: KSA.JobSystems (decompiled, build 2026.10.10.5554)
[^brutal-jobscheduler]: Brutal.Concurrency.Jobs.JobScheduler (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
