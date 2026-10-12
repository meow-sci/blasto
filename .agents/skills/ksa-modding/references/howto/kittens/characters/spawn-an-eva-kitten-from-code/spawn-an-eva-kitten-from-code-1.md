---
type: How-To
title: Replicate EVADoor.CreateKittenEva with the public KittenEva constructor, place the kitten beside a vessel, then track and despawn it
description: doh builds the backpack part and the eight-argument KittenEva by hand, derives position and orbit from one StateVectors read of a reference vehicle, waits for the vehicle solver before constructing, and keeps a registry that despawns safely
category: kittens
group: characters
topic: spawn-an-eva-kitten-from-code
approach: 1
difficulty: advanced
prerequisites: [starmap, reflection]
demonstrated_by: [mods/unscience/features/doh-kitten-spawning]
techniques: [reflection, vehicle-spawn]
hooks:
  - starmap StarMapAfterGui
  - starmap StarMapBeforeGui
  - reflection KSA.ModLibrary.AllParts
  - reflection KSA.ModLibrary.AllCharacters
lifecycle: [frame:draw-ui]
game_types:
  - KSA.KittenEva
  - KSA.EVADoor
  - KSA.Part
  - KSA.PartTree
  - KSA.Tank
  - KSA.CharacterReference
  - KSA.ModLibrary
  - KSA.Orbit
  - KSA.CelestialSystem
  - KSA.JobSystems
  - KSA.Vehicle
  - KSA.StateVectors
  - KSA.IParentBody
  - KSA.Universe
  - KSA.LookupCollection
tags: [kitten, spawn, eva, orbit, backpack, registry, despawn]
---
By the end of this article you can create a real, controllable `KittenEva` with a full backpack at a body-frame offset from any vessel, keep a list of the kittens you made, and remove them again without leaving a half-registered vehicle behind. The recipe is unscience's doh sub-mod: it performs the steps of the private `EVADoor.CreateKittenEva` from a UI button. Prerequisites: a StarMap mod with a GUI hook ([create a minimal StarMap mod](../../../getting-started/loader/create-a-minimal-starmap-mod/overview.md)) and reflection for two internal collections. The same spawn also covers placing a vessel at an offset from another vessel and tracking, selecting and despawning the result.

## Background: how the game does it

`EVADoor.CreateKittenEva` does seven things in order ([current/decomp/KSA/EVADoor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EVADoor.cs#L196-L229)):

1. build the backpack `Part` (`GetBackPackPart`: template `KittenBackPackPart`, `new Part`, `CreateOwnTree`, tank setup, `RefillConsumables`);
2. remove the crew member from the host and mark the roster entry as on EVA;
3. choose the orientation (`Body2Cce`) and body rates from the host;
4. call the public constructor `new KittenEva(Universe.CurrentSystem, character, body2Cce, bodyRates, vehicle.Parent, name, backPackPart, vehicle.Orbit)`;
5. take **one** `vehicle.Orbit.StateVectors` struct and build a new orbit with `Orbit.CreateFromStateCci(vehicle.Parent, stateVectors.StateTime, stateVectors.PositionCci + offset, stateVectors.VelocityCci, vehicle.OrbitColor)`;
6. `kittenEva.Teleport(orbit, null, null)` and `vehicle.Parent.Children.Add(kittenEva)`;
7. `kittenEva.UpdatePerFrameData()`.

Steps 1, 4, 5, 6 and 7 are what a mod replicates; 2 and 3 are door and roster business. The door itself defers the whole thing: the button adds an `EvaSpawnData` to `InputEvents.EvaSpawnBuffer`, which `ApplyInputEvents` drains in the input phase, after the previous solver step was applied and before the next is dispatched ([current/decomp/KSA/EVADoor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EVADoor.cs#L84-L91), [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1114-L1122)). A mod running from the UI pass is later in the frame, with the next step already in flight.

## Lifecycle and timing

| Work | Runs from | Lifecycle |
|---|---|---|
| Spawn, despawn, despawn all | button handlers drawn from `[StarMapAfterGui]` | `frame:draw-ui` |
| `PruneDisposed` (forget kittens the game destroyed) | `[StarMapBeforeGui]` update, every frame | `frame:draw-ui` |

By the time the UI pass runs, `Program.PrepareFrame` has already called `Universe.ExecuteNextVehicleSolvers`, so the vehicle worker may be stepping. Its first act is `ConstraintSim.BeginVehicleUpdate()`, which sets the shapes lock to -1; any vehicle construction (`BepuHandles.Create` through `ConstraintSim.UnlockShapes`) or disposal then throws "The shapes registry cannot be mutated while the vehicle update is stepping" ([current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L125-L133)). doh therefore calls `JobSystems.VehicleSolver.Wait()` before `new KittenEva(...)` and before disposal. `PrepareFrame` would wait on the same scheduler next frame anyway, so this only moves an existing stall earlier ([game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-physics-step)). The alternative, used by the stock door, is to stage the spawn for the next input phase ([safe point topic](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md)). Consequences of spawning from the UI pass:

- The new kitten is not in the step already running; the next `ExecuteNextVehicleSolvers` snapshots it.
- `Program.RefreshVehiclesInFrame` runs only in `PrepareFrame`, so the kitten is first drawn the following frame.
- All of this is main thread only.

## Steps

1. **Find the backpack template and build the part.** `ModLibrary.AllParts` is internal, so doh reflects it; the part needs its own tree because the `Part` constructor no longer creates one (a null-reference in tank setup without it). Tanks are then filled; doh names the `MMH_NTO` mix, stock now calls `ReconfigureTanks`:

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

   The stock version this mirrors, in build 5554:

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

2. **Pick a character.** `AllCharacters` is internal too; doh reflects the field, calls `GetList()` and picks a random entry when the caller names none (see [the character XML topic](../define-a-kitten-character-in-asset-xml/overview.md)). The id passed to `KittenEva` must resolve through `ModLibrary.Get<CharacterReference>`, which throws for an unknown id ([doh.lib/Spawning/KittenSpawner.Catalog.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.Catalog.cs#L85-L107), [doh.lib/Spawning/KittenSpawner.Catalog.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.Catalog.cs#L50-L58)).

3. **Resolve the spawn frame from one state read.** Position, velocity and epoch must come from the same `StateVectors` struct, as `EVADoor` does; pairing a position with a separately read clock shifts the kitten by velocity times the time difference. NaN body rates become zero. The body-frame offset is rotated into CCI with `GetAsmb2Cci()` (this is the vehicle's assembly frame, not a camera frame; `SpawnRequest` documents the axes as X right, Y up, Z forward with a default of (0, 0, 10), but in KSA's stock part XML the forward direction is assembly +X (the stock capsule seat in `current/Content/Core/CoreIVASpaceAGameData.xml` declares `ForwardAxis X="1"` and `UpAxis Z="-1"`), so the default (0, 0, 10) is not obviously "ahead"; confirm which axis is which for your craft by watching where the first kitten lands):

```csharp
// doh.lib/Spawning/KittenSpawner.Positioning.cs L31-L47
        var sv = refVehicle.Orbit.StateVectors;
        var body2Cci = refVehicle.GetAsmb2Cci();
        var offsetCci = request.OffsetBodyFrame.Transform(body2Cci);

        return new PositionResult
        {
            // Position, velocity and epoch must come from the same state (as EVADoor.CreateKittenEva
            // does); pairing this position with a different clock time shifts the kitten by v*dt.
            StateTime = sv.StateTime,
            BasePositionCci = sv.PositionCci,
            OffsetCci = offsetCci,
            VelocityCci = sv.VelocityCci,
            Body2Cce = refVehicle.Body2Cce,
            BodyRates = SafeBodyRates(refVehicle.BodyRates),
            Parent = refVehicle.Parent,
            ReferenceOrbit = refVehicle.Orbit
        };
```

   For an absolute spawn (no reference vehicle) doh builds a temporary orbit at `Universe.GetElapsedTime()` with an identity orientation and zero rates ([doh.lib/Spawning/KittenSpawner.Positioning.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.Positioning.cs#L50-L76)). More on frames in [coordinate frames and epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md).

4. **Construct, orbit, teleport, attach.** Wait for the solver, call the public constructor inside a try, then repeat the stock tail. Several kittens spawn in a chain, so kitten *i* is `(i + 1)` offsets away:

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

5. **Clean up a constructor that throws.** The `Vehicle` base constructor has already registered the kitten in `system.All` before `KittenEva` builds its renderable (a full GPU material pool was the classic cause), so a throw leaves a registered kitten with no renderable that the game updates every frame. Deregister and dispose it:

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

6. **Wait helper and unique ids.** The wait is one line; ids are `Kitten_doh_N`, skipping any already in `CelestialSystem.All`:

```csharp
// doh.lib/Spawning/KittenSpawner.cs L331-L344
    private static void WaitForVehicleSolverIdle()
    {
        JobSystems.VehicleSolver?.Wait();
    }

    private string GenerateUniqueName(CelestialSystem system)
    {
        string name;
        do
        {
            name = $"Kitten_doh_{_nextKittenIndex++}";
        } while (system.All.TryGet(name, out _));
        return name;
    }
```

7. **Track by object, not by id or index.** The registry stores the `Vehicle` found at spawn time, so a kitten the player renamed still resolves, and `GetDisposed()` allocates only when something was disposed because it is polled every frame:

```csharp
// doh.lib/Spawning/SpawnedKittenRegistry.cs L57-L70
    /// <summary>
    /// Entries whose kitten the game has already disposed, or null when there are none.
    /// Allocation-free in the common case, so it is safe to call every frame.
    /// </summary>
    public List<SpawnedKittenEntry>? GetDisposed()
    {
        List<SpawnedKittenEntry>? disposed = null;
        foreach (var entry in _kittens.Values)
        {
            if (entry.Vehicle is { IsDisposed: true })
                (disposed ??= new List<SpawnedKittenEntry>()).Add(entry);
        }
        return disposed;
    }
```

8. **Despawn with the same wait,** preferring the stored object over an id lookup and never falling back to a non-kitten that shares the id. `Dispose` releases physics shapes (the same registry lock as spawn):

```csharp
// doh.lib/Spawning/KittenSpawner.cs L61-L72
            // Use the kitten object captured at registration so a renamed kitten still resolves;
            // never fall back to a non-kitten vehicle that merely shares the name.
            var kitten = entry.Vehicle as KittenEva ?? VehicleProvider.FindVehicle(kittenId) as KittenEva;
            if (kitten != null && !kitten.IsDisposed)
            {
                // Match game's two-phase removal (EVADoor ingress / docking pattern).
                // Dispose releases physics shapes (same registry lock as spawn), so the
                // vehicle solver must be idle first.
                WaitForVehicleSolverIdle();
                Universe.CurrentSystem?.Deregister(kitten);
                kitten.Dispose();
            }
```

9. **Select targets as objects.** A UI that remembers "vehicle number 3" breaks, because `CelestialSystem.Deregister` goes through `LookupCollection.Deregister`, which moves the last element into the freed slot ([current/decomp/KSA/LookupCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LookupCollection.cs#L148-L160)). doh stores the `Vehicle` and clears a selection that vanished instead of letting it slide onto whatever took the slot:

```csharp
// doh.lib/DohSubmod.cs L516-L527
    /// <summary>The selected vehicle if it still exists; otherwise clears it and reports why.</summary>
    private Vehicle? GetSpawnTarget()
    {
        if (_selectedVehicle == null || _selectedVehicle.IsDisposed)
        {
            _selectedVehicle = null;
            SetStatus("No vehicle selected (the previous selection no longer exists).", true);
            return null;
        }

        return _selectedVehicle;
    }
```

10. **Check the log.** doh prints `Spawned N kitten(s)` or `Spawn stopped: ... (k of N spawned)`. In game the kitten appears next to the reference vessel one frame later, and can be selected like any vessel.

## Complete example

- [doh.lib/Spawning/KittenSpawner.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.cs#L139-L267) - `SpawnInternal`: validation, positioning, per-kitten loop, results.
- [doh.lib/Spawning/KittenSpawner.Positioning.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.Positioning.cs#L11-L97) - vehicle-relative and absolute frames.
- [doh.lib/Spawning/KittenSpawner.Catalog.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.Catalog.cs#L12-L107) - backpack, propellant mix, reflected part and character catalogs.
- [doh.lib/Spawning/SpawnedKittenRegistry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/SpawnedKittenRegistry.cs#L11-L86) - tracking.
- [ksa-abstractions.lib/VehicleProvider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/VehicleProvider.cs#L22-L40) - debris-filtered vehicle list and unambiguous id lookup.

## Variations and gotchas

- **Half-built kittens.** Always wrap construction and remove an orphan on failure (step 5).
- **Do not index into `GetAllVehicles()` across frames,** or into `system.All`. Removal reorders the list.
- **Backpack drift.** In 5554 the stock path uses `RecreateResourceManagers` plus `ReconfigureTanks` (automatic mix assignment) where doh names `MMH_NTO`; both fill the tanks. A future backpack with a different propellant would only follow the stock path.
- **Reaction ids lost their ratio** in 2026.7.9.5018: `MMH_NTO_1.6` resolves to nothing; use `MixtureReaction.AtMixtureRatio(DefaultMixtureRatio)` (as `TryGetReactantMix` does).
- **Typed instead of reflected.** `ModLibrary.Get<PartTemplate>("KittenBackPackPart")` is public but throws for a missing id; the character list has no public equivalent.
- **Spawned kittens are controllable** (`KittenEva.IsControllable` is true) and are not in `Universe.KittenRoster`; `Vehicle.Dispose` calls `KittenRoster.Find(Id)?.EndMission()`, which finds nothing for a mod id (inferred from the code).
- **Large batches.** The library accepts up to 100 per request, the UI 20. The authors flag per-view mesh bucket limits for very large batches (not verified).
- **Spawned but untinted is a warning.** `SpawnResult` carries `Success`, `Error` and a separate `Warning`; a kitten that spawned fine but whose material cloning failed is counted in `Warning` (`N kitten(s) could not be tinted: ...`, [doh.lib/Spawning/KittenSpawner.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.cs#L258-L265)) rather than `Error`, and the kitten stays in the world with its stock look.
- **Material pool.** A full GPU material pool makes the `KittenEva` constructor throw; doh checks two free slots first ([tint article](../../appearance/tint-and-restyle-kitten-appearance/overview.md)).
- **After a load.** Native saves own the kittens; do not respawn from your own sidecar ([restore after a load](../restore-kitten-customizations-after-a-load/overview.md)).

## See also

- [Spawn an EVA kitten from code](overview.md), [approach 2: build from a saved craft](spawn-an-eva-kitten-from-code-2.md)
- Mod feature: [doh kitten spawning](../../../../mods/unscience/features/doh-kitten-spawning.md), [doh runtime GPU materials](../../../../mods/unscience/features/doh-runtime-gpu-materials.md)
- [Change vehicles without racing the solver](../../../vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/overview.md), [Spawn a vehicle at runtime](../../../vehicles-and-parts/vehicles/spawn-a-vehicle-at-runtime/overview.md), [Add crew seats and EVA doors](../../../vehicles-and-parts/parts/add-crew-seats-and-eva-doors/overview.md), [Make a vehicle indestructible](../../../vehicles-and-parts/vehicles/make-a-vehicle-indestructible/overview.md)
- [Teleport a vessel by building a new orbit](../../../physics/kinematics/teleport-a-vessel-by-building-a-new-orbit/overview.md)
