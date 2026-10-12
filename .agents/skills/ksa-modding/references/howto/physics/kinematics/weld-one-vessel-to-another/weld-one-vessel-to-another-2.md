---
type: How-To
title: VehicleSolver.Wait then teleport stamped with the next sim step
description: Weld a vessel from a StarMap frame hook without a transpiler by waiting for the vehicle solver, re-resolving the target part by InstanceId, and teleporting onto an orbit stamped with the in-flight step's end time
category: physics
group: kinematics
topic: weld-one-vessel-to-another
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/welds-vessel-anchoring]
techniques: [kinematic-teleport, solver-wait]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame]
game_types: [KSA.Vehicle, KSA.Orbit, KSA.Universe, KSA.JobSystems, KSA.SimStep]
tags: [physics, kinematics, weld, teleport, solver-wait, topological-sort, gatos]
---

Outcome: `/sim/debug/welds` welds one vessel onto another: the source keeps an exact offset and, optionally, orientation relative to the
target's centre of mass or a chosen part, including chains. It is a port of the unscience weld engine to a plain frame hook:
no Harmony patch, at the price of a main-thread stall while any weld exists. Prerequisites: StarMap.

## Background: how the game does it

`Vehicle.Teleport` detaches the source through `RemoveFromCurrentBubble()` -> `PhysicsBubble.RemoveVehicle`, which drops its
`VehicleUpdateState` from the bubble's `_vehicleStates`:

```csharp
// current/decomp/KSA/Vehicle.cs L2241-L2293
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
		if (bodyRates.HasValue)
		{
			BodyRates = bodyRates.Value;
			flag = true;
		}
		if (flag)
		{
			_lastTeleportTime = Program.GetPlayerTime();
			Span<Tank> modules = Parts.Tanks.Modules;
			for (int i = 0; i < modules.Length; i++)
			{
				Tank tank = modules[i];
				tank.ResetParticles();
			}
			GetPhysicsStatesMutable().OverwriteFromLeaderAnalytic(Orbit, in Orbit.StateVectors, Body2Cce, BodyRates);
			Program.Instance.SchedulePlanetUpdate(nearbyCelestial);
		}
	}
```

The worker iterates that list while it runs, which is why the wait is required; the engine exposes the bubble list only while the
`VehicleUpdateTask` is idle (`SyncWindowBubbles` throws otherwise). The detach is also why the weld survives the next frame's
`ApplyVehicleSolvers`: the finished step's results are written back per bubble by `PhysicsBubble.ApplyResultsToVehicles`, which walks
`_vehicleStates`, so the source's stale result is never applied over the teleport. A plain kinematic write made after the same wait
would be overwritten. The detached source is re-bubbled as an orphan at the next dispatch (`Universe.PrepareVehicleWorkers`).[^ksa-physicsbubble]

## Lifecycle and timing

- Driven from `Mod.DriveWelds`, called from `StarMapAfterGui` (or `StarMapAfterOnFrame` when the HUD is hidden) after the solver
  phase.
- At `frame:draw-ui` nothing has joined the vehicle step dispatched in this frame's `PrepareFrame`, so it may still be running (the
  mod's comment that the workers "have finished" by this hook is wrong). `JobSystems.VehicleSolver.Wait()` blocks the main thread until
  it completes; that stall happens only while at least one weld exists.
- The anchor is read from the target's main-thread state, which is the step applied at the start of the frame. The orbit is stamped
  with `Universe.GetJobSimStep(Program.GetPlayerDeltaTime()).NextTime`, which here equals the end time of the step that was in flight
  (same `dtPlayer`, same `_achievedSpeedFraction`), rather than `Universe.GetElapsedTime()`; the mod's notes say the worker otherwise
  logs a "SnapToLeader body/origin time" mismatch.
- No Harmony patch is needed; the driver self-gates to one branch when the registry is empty.

See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Return at once when there are no welds (zero cost when unused), then join the solver:

```csharp
// gatOS.GameMod/Game/Ksa/Welds/WeldManager.cs L106-L144
    public void Update(double dt)
    {
        if (_welds.Count == 0)
            return;

        // Cheap once the workers (queued in PrepareFrame) have finished, which they have by this point.
        JobSystems.VehicleSolver.Wait();

        List<WeldEntry>? toRemove = null;
        foreach (var entry in _welds)
        {
            try
            {
                if (!IsLive(entry.Source) || !IsLive(entry.Target))
                {
                    (toRemove ??= []).Add(entry);
                    continue;
                }

                // Re-resolve the anchor each tick: robust to the target being edited/staged. A removed
                // anchor part falls back to body-frame anchoring rather than dropping the weld.
                if (entry.TargetPartInstanceId != 0)
                    entry.TargetPart = FindPart(entry.Target, entry.TargetPartInstanceId);

                if (!WeldEngine.UpdateWeld(entry))
                    (toRemove ??= []).Add(entry);
            }
            catch (Exception ex)
            {
                ModLog.Log.Debug($"gatOS weld '{entry.SourceId}'→'{entry.TargetId}' update error: {ex.Message}");
                (toRemove ??= []).Add(entry);
            }
        }

        if (toRemove is null)
            return;
        foreach (var entry in toRemove)
            _welds.Remove(entry);
    }
```

2. Drop welds whose source or target vanished; re-resolve the target part by `InstanceId` every tick (robust to staging; a removed
   part falls back to the vehicle body frame). Process welds in topological order so a target is updated before anything welded
   to it (input order on a cycle).
3. Compute the anchor pose exactly as [approach 1](weld-one-vessel-to-another-1.md) does, and the source state; then stamp the
   orbit with the in-flight step's end time and teleport:

```csharp
// gatOS.GameMod/Game/Ksa/Welds/WeldEngine.cs L78-L108
        double3 newSrcPosCci = anchorPosCci + double3.Transform(entry.Position, anchorBody2Cci);
        double3 newSrcVelCci = tgtVelCci;

        doubleQuat cci2Cce = source.Parent.GetCci2Cce();
        doubleQuat newSrcBody2Cce;
        double3 newBodyRates;
        if (entry.LockRotation)
        {
            doubleQuat newSrcBody2Cci = doubleQuat.Concatenate(entry.Orientation, anchorBody2Cci);
            newSrcBody2Cce = doubleQuat.NormalizeOrZero(doubleQuat.Concatenate(newSrcBody2Cci, cci2Cce));
            newBodyRates = target.BodyRates;
        }
        else
        {
            doubleQuat srcBody2Cci = doubleQuat.NormalizeOrZero(source.GetBody2Cci());
            newSrcBody2Cce = doubleQuat.NormalizeOrZero(doubleQuat.Concatenate(srcBody2Cci, cci2Cce));
            newBodyRates = source.BodyRates;
            if (!IsFinite(newBodyRates))
                newBodyRates = new double3(0, 0, 0);
        }

        // Stamp the orbit with the time the just-completed worker tick advanced to, NOT
        // GetElapsedTime() (the PREVIOUS tick's end). Teleport sets body.Time = orbit StateTime;
        // using the next-tick time keeps the source aligned with the queued solver origin (see the
        // unscience notes) — otherwise the worker logs a "SnapToLeader body/origin time" mismatch.
        UniverseTime tickEndTime = Universe.GetJobSimStep(Program.GetPlayerDeltaTime()).NextTime;
        Orbit newOrbit = Orbit.CreateFromStateCci(
            source.Parent, tickEndTime, newSrcPosCci, newSrcVelCci, source.Orbit.OrbitLineColor);
        source.Teleport(newOrbit, newSrcBody2Cce, newBodyRates);
        source.UpdatePerFrameData();
        return true;
```

4. `weld_here` captures the current relative pose with the inverse maths so a vessel can be welded where it already sits.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Welds/WeldManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Welds/WeldManager.cs#L106-L144) | Registry, wait, re-resolve, sort |
| [gatOS.GameMod/Game/Ksa/Welds/WeldEngine.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Welds/WeldEngine.cs#L39-L108) | `UpdateWeld`, `CapturePose` |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L487-L500) | `DrivePostSolver` hook |

## Variations and gotchas

- Each tick's teleport detaches the source from its physics bubble; since 5348 bubble trimming, merging and splitting happen on the
  solver worker, so welds drive a bubble split/merge every frame. gatOS lists live confirmation as pending.
- Welds work only within one SOI (same parent body).
- Teleporting into a NaN target state is guarded: the weld skips the tick.
- `Vehicle.Teleport` replaces the source's flight plan. Neither this driver nor gatOS' solver-phase teleport waits on
  `JobSystems.NearestOrbitAndPerformanceWorker`, whose `NearestOrbitPointJob` reads the controlled vehicle's flight plan; welding
  the controlled vessel can race that job (not observed; inferred from the decomp). Approach 1 joins it with `JoinOrbitReaders`.
- The repo's weld validation checklist is still marked not run in game.

## See also

- [Overview and comparison](overview.md)
- [Handoff callback](weld-one-vessel-to-another-1.md)
- Mod feature: [gatOS welds vessel anchoring](../../../../mods/gatos/features/welds-vessel-anchoring.md)
- [Join JobSystems.VehicleSolver before mutating](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-4.md)

[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
