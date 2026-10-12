---
type: Game Feature
title: Override the terrain height cameras and wheels see
description: How a mod makes wheel and camera code treat a roof, ramp or floor as the ground by postfixing the camera-frame terrain height query while leaving the fixed-frame query that physics collision reads
category: physics
group: aero-and-ground
topic: override-terrain-height-for-roofs-and-platforms
demonstrated_by: [mods/ksastructures/features/terrain-height-roof-override]
lifecycle: [frame:dispatch-solvers, frame:viewports, frame:celestials, startup:all-mods-loaded]
game_types: [KSA.Celestial, KSA.Vehicle]
tags: [physics, terrain, height, wheels, camera, harmony, postfix, collision]
---

KSA's terrain is a height field on each celestial, and several different systems ask it how high the ground is: the
camera keeps itself above it, altitude readouts subtract it, `Vehicle.TeleportToLocation` stands a craft on it, and mod wheel
code springs against it. Collision is a different matter: the Bepu terrain patches, the radius cache and the launch-pad
placement all read it through the fixed-frame overload. A mod that builds a roof, ramp or platform out of a collider (and
digs the terrain under it) wants wheels and cameras to treat the collider as the ground, while physics keeps seeing the real
pit. KSA offers no switch for that; the trick is that the two overloads are separate methods.

## Engine background

`Celestial` exposes the same query in several frames. The camera-frame overload transforms the direction into the body-fixed
frame and calls the fixed-frame one:[^ksa-celestial]

```csharp
// current/decomp/KSA/Celestial.cs L777-L781
	public double GetTerrainHeightFromDirCce(double3 positionDirCce, bool accurate = true)
	{
		double3 positionDirCcf = positionDirCce.Transform(GetCcf2Cce().Inverse());
		return GetTerrainHeightFromDirCcf(positionDirCcf, accurate);
	}
```

Which one each caller uses decides what a patch on it changes:

| Caller | Overload | Effect of patching `GetTerrainHeightFromDirCce` |
|---|---|---|
| `Camera.TryGetSurfaceClampPositionEcl`, from `ClampCamera` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L633-L657) | `Cce` | The camera clamps to the answer (a 0.5 m clamp above it) |
| `Program.GetCurrentAltitudeKm` and the altitude helper next to it [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2734-L2759) | `Cce` | Altitude readouts follow the answer |
| `Vehicle` debug overlay [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4440) | `Cce` | Cosmetic |
| `TerrainPatch` terrain radius for physics [current/decomp/KSA/TerrainPatch.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainPatch.cs#L517-L524)[^ksa-terrainpatch] | `Ccf` | None: physics collision keeps the real terrain |
| `ConstraintSim` placement of static objects [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L569-L572)[^ksa-constraintsim] | `Ccf` | None |
| `Vehicle.GetInitialKinematicStateForLocation` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4150-L4157) (teleport start height) | `Ccf` | None directly; a mod lends a launch-pad `SurfaceHeight` instead |

So a postfix on the `Cce` overload changes what cameras and readouts see, and, because a mod's own wheel code usually asks
the `Cce` overload too, what its wheels spring against, without moving a single collider.

## Where it runs

- `Universe.ExecuteNextVehicleSolvers` calls `Vehicle.PrepareWorker` per vehicle on the main thread, immediately before the
  vehicle's state is copied for the worker. A wheel mod's own prefix on `PrepareWorker` therefore runs in the
  `frame:dispatch-solvers` window, and any `Cce` query it makes is answered on the main thread.[^ksa-universe][^ksa-vehicle]
- The camera clamp runs in `Camera.OnFrame` from the viewport pass (`frame:viewports`), and
  `Program.OnFrameCelestials` reads the camera's terrain height (`frame:celestials`).[^ksa-camera][^ksa-program]
- The postfix is therefore a main-thread patch; nothing in it needs worker-thread safety, but it should still never throw,
  because it runs inside the camera clamp and inside other mods' wheel code. See the
  [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Trade-off |
|---|---|---|---|
| [Postfix GetTerrainHeightFromDirCce keyed on who is asking](override-terrain-height-for-roofs-and-platforms.md) | KSAStructures | harmony-postfix | Wheels and cameras see the roof, ramp or floor depending on who is asking; collision keeps the pit; the answer is not a pure function and assumes main-thread callers |

## See also

- [Sample terrain and pad surfaces under each wheel](../sample-the-ground-under-each-wheel/overview.md): wheel code that reads this height, plus launch-pad surfaces
- [Simulate wheels, suspension and tyres without a wheel module](../simulate-wheels-and-suspension-without-a-wheel-module/overview.md)
- [Identify the vehicle a callback runs for with a ThreadStatic](../../solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-5.md): the same asker bracket as a general pattern
- [Place a vessel on a surface at latitude and longitude](../../kinematics/place-a-vessel-on-a-surface-at-latitude-and-longitude/overview.md)
- The topic query-terrain-height-and-line-of-sight (other batch) covers asking the terrain height and ray tests directly
- [Query the terrain height field and test line of sight across terrain](../../../celestials/terrain/query-terrain-height-and-line-of-sight/overview.md) - related topic
- [Write worker-thread-safe patches inside the vehicle solver](../../solver/write-worker-thread-safe-solver-patches/overview.md) - related topic

[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-terrainpatch]: KSA.TerrainPatch (decompiled, build 2026.10.10.5554)
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
