---
type: How-To
title: Read GetStatesCcf in the PrepareWorker prefix
description: Read a vehicle's physics state in the planet-fixed frame from a Vehicle.PrepareWorker prefix and sample the height field under each point, so ground height and ground speed come out in metres and metres per second over the ground
category: physics
group: mod-simulation
topic: simulate-bodies-outside-the-vehicle-system
approach: 5
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksacars/features/terrain-contact-under-wheels]
techniques: [harmony-prefix, custom-physics]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.PhysicsStates, KSA.Celestial, KSA.SimStep, KSA.Universe]
tags: [physics, ccf, body-fixed, terrain, height-field, prepareworker, wheels, ground-contact]
---

Outcome: for any craft you push or probe, you know how far each chosen point is above the ground and how fast it is
moving over it, without orbital-motion error. KSACars does this for four wheel hubs per car, every frame, to feed a
spring model; the same two calls serve a hover controller, a landing probe or a ground-effect model. Prerequisites:
StarMap and Harmony. This is the vehicle-side counterpart of
[approach 2](simulate-bodies-outside-the-vehicle-system-2.md), where the body being simulated is the mod's own.

## Background: how the game does it

Two representations of a craft's position coexist. The analytic orbit (`Vehicle.Orbit.StateVectors`) is exact for a
craft on rails and an approximation for a landed one: its position and the physics state share an epoch but not a
frame, and on a landed craft they differ by metres. The physics state (`PhysicsStates` with a `BubbleOrigin` and
`KinematicStates`) is what the Bepu step integrates. KSACars measures springs in centimetres, so it reads the
physics state only.

`PhysicsStates.GetStatesCcf` turns the physics state into position, velocity and attitude in Ccf, the body-fixed
frame:

```csharp
// current/decomp/KSA/PhysicsStates.cs L428-L440
	public static void GetStatesCcf(in BubbleOrigin origin, in KinematicStates kinematic, out double3 positionCcf, out double3 velocityCcf, out doubleQuat body2Ccf)
	{
		BubbleFrame bubFrame = origin.BubFrame;
		BubbleFrame bubbleFrame = bubFrame;
		switch (bubbleFrame)
		{
		case BubbleFrame.Cci:
			CciStatesToCcf(in origin, in kinematic, origin.Time, out positionCcf, out velocityCcf, out body2Ccf);
			break;
		case BubbleFrame.Ccf:
			positionCcf = origin.PositionBub + kinematic.PositionPhys;
			velocityCcf = origin.VelocityBub + kinematic.VelocityPhys;
			body2Ccf = kinematic.Body2Phys;
```

In Ccf the planet's orbital motion and its spin cancel, so a velocity there is a speed over the ground. The terrain
height field is queried along a direction from the planet's centre. `Celestial.GetTerrainHeightFromDirCce` rotates the direction to Ccf and calls
`GetTerrainHeightFromDirCcf`:[^ksa-celestial]

```csharp
// current/decomp/KSA/Celestial.cs L777-L781
	public double GetTerrainHeightFromDirCce(double3 positionDirCce, bool accurate = true)
	{
		double3 positionDirCcf = positionDirCce.Transform(GetCcf2Cce().Inverse());
		return GetTerrainHeightFromDirCcf(positionDirCcf, accurate);
	}
```

The `accurate` flag selects bicubic base sampling plus the modifier stack instead of bilinear. Every overload
defaults to `accurate = true`, and the engine's own physics terrain (`TerrainPatch`, `BubbleTerrainGrid`) uses that
default; only the impact predictor (`TerrainImpactFinder`) and the camera visibility and label helpers
(`Camera.IsVisibleTo`, `LocationReference.DrawUi`) pass `false`, so ask for `accurate: true` to match the colliders.

## Lifecycle and timing

`Vehicle.PrepareWorker(SimStep)` is a virtual method called on the main thread for every vehicle by
`Universe.PrepareVehicleWorkers`, itself the first statement of `Universe.ExecuteNextVehicleSolvers`, which
`Program.PrepareFrame` calls after `ApplyVehicleSolvers`:

```csharp
// current/decomp/KSA/Universe.cs L2126-L2155
	public static void ExecuteNextVehicleSolvers(double dtPlayer, SimStep simStep)
	{
		if (CurrentSystem != null)
		{
			PrepareVehicleWorkers(simStep);
			SyncGroundClutter();
			_vehicleUpdateTask.PrepareFrame(CollectionsMarshal.AsSpan(_orphanVehicles), simStep);
			JobSystems.VehicleSolver.Queue(_vehicleUpdateTask, 1);
			_nextPlayerDeltaTime = dtPlayer;
			_nextSimStep = simStep;
			JobSystems.VehicleSolverProfiler.Prepare();
			JobSystems.VehicleSolver.ExecuteJobs();
		}
	}

	public static void PrepareVehicleWorkers(SimStep simStep)
	{
		_orphanVehicles.Clear();
		LookupCollection<Astronomical>.TypeFilter<Vehicle> enumerator = CurrentSystem.All.OfType<Vehicle>().GetEnumerator();
		while (enumerator.MoveNext())
		{
			Vehicle current = enumerator.Current;
			current.PrepareWorker(simStep);
			VehicleUpdateState vehicleUpdateState = current.TakeUpdateStateIfOrphaned();
			if (vehicleUpdateState != null)
			{
				_orphanVehicles.Add(vehicleUpdateState);
			}
		}
	}
```

At that point the previous step's results are applied and per-frame data refreshed, so the physics state, the body's attitude
(`GetCcf2Cce`) and the height field all describe the same instant: the end of the step just applied. The value you
compute is then integrated by the step about to be queued. Reading terrain from a StarMap hook would also work for
the heights, but any force or velocity written there would be lost; see
[the PrepareWorker window](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-1.md)
and the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Patch `Vehicle.PrepareWorker(SimStep)` with a prefix. Resolve the method by signature and refuse to run if it
   moved; wrap the body so an exception is logged once and never escapes into the engine's main-thread loop:

```csharp
// src/KSACars/Ksa/PhysicsHook.cs L34-L60
    public static void Install()
    {
        if (Installed) return;

        try
        {
            MethodInfo? target = typeof(Vehicle).GetMethod(nameof(Vehicle.PrepareWorker), [typeof(SimStep)]);
            if (target is null)
            {
                Log.Warn("cars cannot drive: KSA has no Vehicle.PrepareWorker(SimStep) to hook");
                return;
            }

            MethodInfo prefix = typeof(PhysicsHook).GetMethod(
                nameof(BeforePrepareWorker), BindingFlags.NonPublic | BindingFlags.Static)!;

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));

            Installed = true;
            Log.Info("car physics hooked into Vehicle.PrepareWorker");
        }
        catch (Exception e)
        {
            Log.Error("could not hook car physics; no car can drive", e);
        }
    }
```

```csharp
// src/KSACars/Ksa/PhysicsHook.cs L77-L89
    private static void BeforePrepareWorker(Vehicle __instance, SimStep simStep)
    {
        try
        {
            Buggies.Physics(__instance, simStep.DeltaTime);
        }
        catch (Exception e)
        {
            if (_complained) return;
            _complained = true;
            Log.Error("car physics failed inside the engine's step", e);
        }
    }
```

2. Take mutable access to the physics state and read it in Ccf. `GetPhysicsStatesMutable` returns views of the
   vehicle's `_bubbleOrigin`, `_kinematicStates`, `_props` and `_environment`; the output of `GetStatesCcf` is a copy:

```csharp
// src/KSACars/Ksa/Buggies.cs L586
        PhysicsStates states = craft.GetPhysicsStatesMutable();
```

```csharp
// src/KSACars/Ksa/Buggies.cs L600-L615
        PhysicsStates.GetStatesCcf(in states.Origin, in states.Kinematic,
                                   out double3 positionCcf, out double3 velocityCcf, out doubleQuat body2Ccf);
        doubleQuat ccf2Body = body2Ccf.Inverse();
        doubleQuat body2Phys = states.Kinematic.Body2Phys;
        double3 spinBody = states.Kinematic.AngularVelocityPhys.Transform(body2Phys.Inverse());
        double3 velocityBody = velocityCcf.Transform(ccf2Body);

        double3 com = craft.CenterOfMassAsmb;
        doubleQuat part2Asmb = e.Part.Asmb2VehicleAsmb;
        double3 partOrigin = e.Part.PositionVehicleAsmb;
        double3 up = part2Asmb * new double3(1, 0, 0);
        double3 forward = part2Asmb * new double3(0, 1, 0);

        doubleQuat ccf2Cce = body.GetCcf2Cce();
        TerrainHeights ground = new(body, accurate: true);
        LaunchPads pads = LaunchPads.On(body);
```

3. Wrap the height field behind a fallible query. A failed read must mean "no claim", never flat ground at the mean
   sphere. KSACars' `TerrainHeights` also rejects non-finite results:

```csharp
// src/KSACars/Ksa/TerrainHeights.cs L13-L32
internal sealed class TerrainHeights(Celestial body, bool accurate = false) : ITerrainHeights
{
    private readonly Celestial _body = body;
    private readonly bool _accurate = accurate;

    public bool TryHeight(double3 dirFromCentre, out double metres)
    {
        metres = 0.0;

        try
        {
            metres = _body.GetTerrainHeightFromDirCce(dirFromCentre, accurate: _accurate);

            return double.IsFinite(metres);
        }
        catch
        {
            return false;
        }
    }
```

4. For each point of interest (a wheel hub here), position it in Ccf, take its direction from the centre, convert the
   direction to Cce, read the height and subtract it from the radius. The ground's up is the radial direction, so
   slope emerges from several points reading different heights; velocity at the point adds the rigid-body spin term:

```csharp
// src/KSACars/Ksa/Buggies.cs L647-L666
        for (int i = 0; i < corners.Length; i++)
        {
            double3 hub = hubs[i];

            double3 atCcf = positionCcf + hub.Transform(body2Ccf);
            double radius = Vec.Len(atCcf);
            if (!(radius > 0.0)) continue;
            double3 dirCcf = atCcf / radius;
            if (!ground.TryHeight(dirCcf.Transform(ccf2Cce), out double height)) continue;

            double hubHeight = radius - (body.MeanRadius + height);
            if (pads.TryHeightOver(atCcf, out double overPad)) hubHeight = Math.Min(hubHeight, overPad);

            e.HubHeights[i] = hubHeight;
            contacts[i] = new WheelContact(
                Valid: true,
                HubHeight: hubHeight,
                GroundUp: dirCcf.Transform(ccf2Body),
                HubVelocity: velocityBody + Vec.Cross(spinBody, hub));
        }
```

5. Convert the result back to the frame you write in (body frame, then `Body2Phys`) when you apply a force; ground
   queries are in Ccf, writes are in the physics frame. See
   [injecting forces](../../solver/inject-forces-and-impulses-at-the-physics-safe-point/overview.md).

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/PhysicsHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/PhysicsHook.cs#L34-L89) | The prefix on `Vehicle.PrepareWorker` and its signature pin |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L575-L670) | Physics state in Ccf, per-hub direction, height and `WheelContact` |
| [src/KSACars/Ksa/TerrainHeights.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/TerrainHeights.cs) | One body's height field behind `ITerrainHeights` |
| [src/KSACars/Sim/ITerrainHeights.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/ITerrainHeights.cs) | The KSA-free seam used by the headless tests |

## Variations and gotchas

- Never use the analytic position for a landed craft; the two differ by metres.
- Ecl velocities are not ground speeds: near Earth an ecliptic velocity carries about 29.8 km/s of orbital motion.
- The height field returns the seabed. There is no waterline clamp, so a craft over the sea is sprung against the
  sea floor unless you add the ocean level yourself (KSAGolf does, see [approach 2](simulate-bodies-outside-the-vehicle-system-2.md)).
- A launch pad is not in the height field: it sits on a levelled decal disc. KSACars takes the lesser of the field
  and the pad deck height.
- `TerrainHeights` documents `accurate: false` as the engine default; in build 2026.10.10.5554 the default is `true`.
  Every call site in the mod passes `true` explicitly, so behaviour matches the colliders.
- A failed read leaves the point invalid, which the wheel model treats as in the air.
- The prefix runs on the main thread, but anything it writes is read by workers; keep the write bounded and finish before returning.

## See also

- [Overview and comparison](overview.md)
- [Approach 2: ball in Ccf](simulate-bodies-outside-the-vehicle-system-2.md), [approach 4: Ecl epochs](simulate-bodies-outside-the-vehicle-system-4.md)
- Mod feature: [KSACars terrain contact under wheels](../../../../mods/ksacars/features/terrain-contact-under-wheels.md)
- [Ray cast against part meshes and terrain](../../mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md), which walks a ray against the same height field
- The topic query-terrain-height-and-line-of-sight (other batch) covers height queries in full

[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
