---
type: Game Feature
title: Teleport a vessel or EVA kitten to an orbit or state vector
description: How a mod moves a vessel, EVA kitten or debris to an arbitrary position and velocity by building an Orbit with Orbit.CreateFromStateCci and calling Vehicle.Teleport at a window where it survives the physics step
category: physics
group: kinematics
topic: teleport-a-vessel-by-building-a-new-orbit
demonstrated_by:
  - mods/gatos/features/debug-cheats-teleport-refuel-warp
  - mods/ksarmory/features/spawn-and-teleport-test-craft
  - mods/unscience/features/dent-wizard-vessel-launcher
lifecycle: [frame:prepare, frame:dispatch-solvers, frame:input, frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.Orbit
  - KSA.Universe
  - KSA.InputEvents
  - KSA.KittenEva
  - KSA.Cursor
  - KSA.Camera
  - KSA.Part
  - KSA.Ray
tags: [physics, kinematics, teleport, orbit, state-vector, cci, vehicle, handoff]
---

KSA has no "set position" call for a vessel. A vessel's authority is its `Orbit` (and, when in a physics bubble, the
bubble's kinematic state, which `Universe.ApplyVehicleSolvers` overwrites from the worker each frame). The engine
way to move one is to build a new `Orbit` from a position and velocity in the parent body's inertial frame (Cci) and give
it to `Vehicle.Teleport`. `Teleport` computes the complete trajectory, removes the vessel from its physics bubble and
installs the flight plan; the next step re-bubbles it. The art is choosing the moment.

## Engine background

```csharp
// current/decomp/KSA/Vehicle.cs L2241-L2275
	public void Teleport(Orbit? orbit, doubleQuat? body2Cce, double3? bodyRates)
	{
		Celestial nearbyCelestial = Program.GetNearbyCelestial();
		bool flag = false;
		if (orbit != null)
		{
			FlightPlan flightPlan = new FlightPlan(orbit, Hash);
			flightPlan.ImpactClearanceMargin = BoundingSphereRadiusBody;
			flightPlan.ComputeCompleteTrajectory(out var errorsEncountered, 100, 16, null, resolveImpactsCompletely: true);
			if (errorsEncountered)
			{
				flag = false;
				LogCategory log = DefaultCategory.Log;
				LogCategory logCategory = log;
				bool shouldAppend;
				LogString<Error> message = new LogString<Error>(78, 0, log, out shouldAppend);
				if (shouldAppend)
				{
					message.AppendLiteral("Failed to compute trajectory for new orbit during teleport, aborting teleport.");
				}
				logCategory.Error(message, "Teleport", "C:\\prototype-planet\\KSA\\Vehicle.cs", 2315);
			}
			else
			{
				RemoveFromCurrentBubble();
				flightPlan.CalculateTargetNodes(Target);
				SetFlightPlan(flightPlan);
				flag = true;
			}
		}
		if (body2Cce.HasValue)
		{
			Body2Cce = body2Cce.Value;
			flag = true;
		}
```

| Piece | Role |
|---|---|
| `Orbit.CreateFromStateCci(parent, stateTime, positionCci, velocityCci, color)` [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1611-L1617) | Builds a conic about `parent` from an inertial state; the epoch is `stateTime`, the time the state represents |
| `Vehicle.Teleport(Orbit?, doubleQuat?, double3?)` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2241-L2243) | Computes the complete trajectory (logs an error and aborts if it cannot), removes the vessel from its bubble, sets the flight plan; null attitude and rates keep the vessel's own orientation and spin |
| `Vehicle.TeleportToLocation(celestial, lat, lon)` | Queues a `TeleportInputData` that `ApplyInputEvents` drains; the surface variant ([place a vessel on a surface](../place-a-vessel-on-a-surface-at-latitude-and-longitude/overview.md)) |
| `InputEvents.TeleportInputBuffer` [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1126-L1140) | Drained by `ApplyInputEvents` early in `PrepareFrame`, before the solver dispatch |

`Teleport` is not safe at an arbitrary moment. It calls `RemoveFromCurrentBubble()`, and bubble ownership lives in
`VehicleUpdateTask`, whose `SyncWindowBubbles` throws unless the task is idle; `Teleport` also disposes cached orbit
points that the frame's `NearestOrbitPointJob` may still be reading. The frame has three usable windows
(see the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)):

| Window | How |
|---|---|
| Queue and let the engine apply: `TeleportInputBuffer` | Only `TeleportToLocation`; drained by `ApplyInputEvents`, before the nearest-orbit job is queued |
| Prefix on `Universe.ExecuteNextVehicleSolvers` (after the apply, before the dispatch) | Runs after the frame's `NearestOrbitPointJob` was queued, so teleporting the controlled vessel there can race that job |
| The `PrepareFrame` handoff (`Universe.GetJobSimStep` call) | Join the nearest-orbit worker first, then teleport; used by the transpiler hook, see [the handoff](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-3.md) |

## Approaches

| Article | Mod | Technique | Trade-off |
|---|---|---|---|
| [1: Teleport to CCI state vectors in the solver-dispatch window](teleport-a-vessel-by-building-a-new-orbit-1.md) | gatOS | harmony-prefix | Short and general; can race the nearest-orbit job for the controlled vessel, and gives the vessel no attitude change |
| [2: Orbit solved backwards from a requested pass](teleport-a-vessel-by-building-a-new-orbit-2.md) | KSArmory | harmony-prefix | Spawns a drone on a computed pass; ecliptic to Cci conversion, ballistic correction, attach to the parent |
| [3: Teleport at the PrepareFrame handoff and fire at a clicked point](teleport-a-vessel-by-building-a-new-orbit-3.md) | unscience (Dent Wizard) | harmony-transpiler | Correct epoch and orbit-job join; requires the shared handoff hook; target-relative velocity and cursor picking |

## See also

- [Place a vessel on a surface at a latitude and longitude](../place-a-vessel-on-a-surface-at-latitude-and-longitude/overview.md)
- [Weld one vessel to another](../weld-one-vessel-to-another/overview.md), which re-teleports every frame
- [Mutate vehicles at a physics-safe point](../../solver/mutate-vehicles-at-a-physics-safe-point/overview.md)
- [Ray cast against part meshes and terrain](../../mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md), which picks the click target
- The topics create-and-read-maneuver-nodes, set-simulation-speed-from-a-mod, spawn-a-vehicle-at-runtime, cursor-to-world-ray, drive-the-flight-computer-and-attitude-modes-from-a-mod and spawn-an-eva-kitten-from-code (other batches) cover neighbouring needs
- [Create, chain and read stock maneuver nodes from code](../../../flight-and-orbits/autopilot/create-and-read-maneuver-nodes/overview.md) - related topic
- [Set, limit and request simulation speed from a mod](../../../flight-and-orbits/flight-state/set-simulation-speed-from-a-mod/overview.md) - related topic
- [Spawning a vehicle at runtime](../../../vehicles-and-parts/vehicles/spawn-a-vehicle-at-runtime/overview.md) - related topic
- [Turn the mouse cursor into a world ray and pick points on a construction plane](../../../ui/input/cursor-to-world-ray/overview.md) - related topic
- [Drive the flight computer and attitude modes from a mod](../../../flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md) - related topic
- [Spawn an EVA kitten from code](../../../kittens/characters/spawn-an-eva-kitten-from-code/overview.md) - related topic

