---
type: Game Feature
title: Spawn an EVA kitten from code
description: How a mod creates a real KittenEva with a backpack part, a chosen character and an orbit beside another vessel, registers it with its parent body and avoids racing the vehicle solver
category: kittens
group: characters
topic: spawn-an-eva-kitten-from-code
demonstrated_by: [mods/unscience/features/doh-kitten-spawning, mods/ksarmory/features/spawn-and-teleport-test-craft]
lifecycle: [frame:draw-ui, frame:input]
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
  - KSA.InputEvents
  - KSA.Vehicle
  - KSA.StateVectors
  - KSA.IParentBody
  - KSA.Universe
  - KSA.LookupCollection
tags: [kitten, spawn, eva, orbit, vehicle, solver, registry]
---
The game creates an EVA kitten in exactly one place: the private `EVADoor.CreateKittenEva`, called when the player presses an EVA door button. There is no public "spawn a kitten" API. A mod that wants kittens on demand (for a test range, a crowd, a rescue scenario) therefore replicates that method with the public eight-argument `KittenEva` constructor, or builds one from a saved craft that names a `Character`. The two reference mods differ in what they start from, and in how they keep the vehicle solver thread from corrupting the shape registry during construction. This topic also covers placing the kitten relative to another vessel, picking a character, and tracking and despawning spawned kittens safely.

## Engine background

| Type or member | Role | Source |
|---|---|---|
| `EVADoor.CreateKittenEva(vehicle, seat, rosterEntry)` (private) | Backpack part, `new KittenEva(...)`, orbit from the host's state vectors, `Teleport`, `Parent.Children.Add`, `UpdatePerFrameData` | [current/decomp/KSA/EVADoor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EVADoor.cs#L196-L229) |
| `EVADoor.GetBackPackPart` (private) | `KittenBackPackPart` template, `new Part`, `CreateOwnTree`, tank setup, `RefillConsumables` | [current/decomp/KSA/EVADoor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EVADoor.cs#L295-L312) |
| `KittenEva(CelestialSystem, string characterId, doubleQuat body2Cce, double3 bodyRates, IParentBody, string id, Part root, Orbit)` | Public constructor; creates the `KittenRenderable` after the base `Vehicle` constructor has registered the vehicle | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L78-L83) |
| `KittenEva.CreateKittenEva(system, VehicleTemplate, parent, id)` | Public static factory used for a saved craft that names a `Character` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L85-L98) |
| `Orbit.CreateFromStateCci` | Builds the spawn orbit from a parent, a state time, a CCI position and velocity | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1611) |
| `EVADoor` spawn buffering | The stock door queues an `EvaSpawnData` in `InputEvents.EvaSpawnBuffer`, drained by `ApplyInputEvents` in the input phase | [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L35-L51), [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1114-L1122) |
| `ConstraintSim.UnlockShapesBlocking`, `JobSystems.VehicleSolver.Wait` | Two ways a mod can wait out the vehicle worker, which holds the Bepu shape registry for its whole step | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L135-L153), [current/decomp/KSA/JobSystems.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/JobSystems.cs#L14-L40) |
| `Vehicle.Dispose`, `CelestialSystem.Deregister` | Removal; `Deregister` moves the last list element into the freed slot | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3770-L3798), [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L93-L100) |

Three engine facts shape every approach:

- **Construction touches physics.** `new Vehicle(...)` (and so `new KittenEva(...)`) mutates the shared Bepu shape registry, which the vehicle worker locks for the whole of its step. A spawn from the UI pass or a post-frame hook can land inside that step, so it must either wait for the worker or stage the spawn to the frame sync point ([change vehicles without racing the solver](../../../vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/overview.md)).
- **Registration happens early.** The `Astronomical` base constructor registers the vehicle in the system before `KittenEva` finishes building its renderable; if the renderable then throws, a half-built kitten stays registered and must be removed again.
- **A kitten is only simulated once it has an orbit and a parent.** The orbit comes from a CCI state vector at a state time, and `parent.Children.Add` puts it in the parent's child list so the system walks it each frame.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. Replicate `EVADoor.CreateKittenEva` with the public constructor, place it beside a vehicle, track and despawn it](spawn-an-eva-kitten-from-code-1.md) | unscience (doh) | `reflection` | `frame:draw-ui` | Reflects `ModLibrary.AllParts` and `AllCharacters`; waits on `JobSystems.VehicleSolver`; full registry and cleanup |
| [2. Build a `KittenEva` from a saved craft that names a `Character`](spawn-an-eva-kitten-from-code-2.md) | KSArmory | `vehicle-spawn` | `frame:draw-ui` | Backpack and tanks come from the save; holds `ConstraintSim.UnlockShapesBlocking` around construction |

## See also

- [Read and extend the character asset XML](../define-a-kitten-character-in-asset-xml/overview.md), [Reach a kitten's avatar, skeleton, bones and animation catalog](../reach-a-kittens-avatar-and-skeleton/overview.md)
- [Change vehicles without racing the solver](../../../vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/overview.md), [Spawn a vehicle at runtime](../../../vehicles-and-parts/vehicles/spawn-a-vehicle-at-runtime/overview.md), [Add crew seats and EVA doors](../../../vehicles-and-parts/parts/add-crew-seats-and-eva-doors/overview.md)
- [Teleport a vessel by building a new orbit](../../../physics/kinematics/teleport-a-vessel-by-building-a-new-orbit/overview.md), [Understand KSA coordinate frames and epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md), [Make a vehicle indestructible](../../../vehicles-and-parts/vehicles/make-a-vehicle-indestructible/overview.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Tracking which craft carry your part, classifying them and detecting staging and loss](../../../vehicles-and-parts/craft-tracking/track-and-classify-craft/overview.md) - related topic

