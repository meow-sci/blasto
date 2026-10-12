---
type: How-To
title: GetTerrainHeightFromDirCce with accurate true in the PrepareWorker prefix
description: Read the planet-fixed physics state of a craft in a Vehicle.PrepareWorker prefix and sample the accurate terrain height along each wheel hub's direction from the body centre to get the hub height above the ground
category: physics
group: aero-and-ground
topic: sample-the-ground-under-each-wheel
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksacars/features/terrain-contact-under-wheels]
techniques: [harmony-prefix, custom-physics]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Celestial, KSA.PhysicsStates, KSA.Vehicle]
tags: [physics, terrain, height, wheels, accurate, ccf, prepareworker]
---

Outcome: for every wheel of a craft, once per frame, the height of the hub above the terrain the engine's own colliders are
built from, and the velocity of the hub over that ground. Prerequisites: StarMap and Harmony. Demonstrated by KSACars, whose
cars spring against this height; the launch-pad half of the problem is in the [other two approaches](overview.md).

## Background: how the game does it

`Celestial.GetTerrainHeightFromDirCcf` returns the height above the mean radius along a unit direction from the body centre,
in the body-fixed frame; `GetTerrainHeightFromDirCce` rotates a camera-frame direction into it first.[^ksa-celestial] Both
default to `accurate = true`. The flag changes exactly two things: the base height is sampled bicubically instead of
bilinearly, and each terrain modifier's `Evaluate` runs only when `accurate` is set. The normal-map, biome and per-modifier
LUT samples run either way, so `accurate: false` is not much cheaper:

```csharp
// current/decomp/KSA/Celestial.cs L797
		double num2 = MathEx.Lerp(t: (!accurate) ? SampleHeightMapBilinear(bodyTemplate.HeightReference, vector) : SampleHeightBicubic(bodyTemplate.HeightReference.TextureAsset, vector), a: bodyTemplate.HeightReference.Minimum, b: bodyTemplate.HeightReference.Maximum);
```

```csharp
// current/decomp/KSA/Celestial.cs L875-L878
				if (accurate)
				{
					modifierReference.Evaluate(base.TerrainModifiersRenderData, ref modData, ref heightKm, ref gradient);
				}
```

The engine's collision terrain asks with the default: `TerrainPatch.GetTerrainRadiusCcf` and `BubbleTerrainGrid.SampleRadius`
call `GetTerrainHeightFromDirCcf(dir)`, and only `TerrainImpactFinder` (impact prediction) and the
visibility and label helpers `Camera.IsVisibleTo` and `LocationReference.DrawUi` (through
`GetSurfacePositionEclFromCce`) pass `accurate: false`.[^ksa-terrainpatch][^ksa-bubbleterraingrid][^ksa-terrainimpactfinder]

```csharp
// current/decomp/KSA/BubbleTerrainGrid.cs L255-L259
	private double SampleRadius(double3 dirCcf)
	{
		Celestial celestial = _celestial;
		return (celestial != null) ? (celestial.MeanRadius + celestial.GetTerrainHeightFromDirCcf(dirCcf)) : _fallbackRadius;
	}
```

So a wheel that wants to agree with the colliders samples `accurate: true`. On a detailed body most metre-scale relief comes
from the modifiers; a non-accurate sample would drive on a smoothed surface the colliders do not match. The field answers the
seabed under water (no waterline clamp), and it knows nothing of launch pads.

## Lifecycle and timing

KSACars samples inside a Harmony prefix on `Vehicle.PrepareWorker(SimStep)`, which runs on the main thread in
`frame:dispatch-solvers`: `Program.PrepareFrame` has just applied the previous step and polled input, and
`Universe.ExecuteNextVehicleSolvers` calls `PrepareWorker` per vehicle immediately before the worker snapshot is taken.
[^ksa-universe][^ksa-vehicle] The physics state, the body's attitude (`GetCcf2Cce`) and the height field therefore describe
the same instant, the end of the step just applied, and the impulse computed from them is integrated by the step about to
be dispatched. Reading the heights from a StarMap per-frame hook would work for the numbers, but forces written there are lost
to the snapshot (see
[mutate vehicles at a physics-safe point](../../solver/mutate-vehicles-at-a-physics-safe-point/overview.md)). See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-physics-step).

## Steps

1. **Hook `Vehicle.PrepareWorker` with a prefix that cannot throw.** It runs inside the engine's own frame loop, where an
   exception is the game rather than a log line. KSACars reports the first failure and swallows the rest:

```csharp
// src/KSACars/Ksa/PhysicsHook.cs L77-L90
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

   The install resolves the method by its `SimStep` parameter and logs `car physics hooked into Vehicle.PrepareWorker`:[^ksacars-physicshook]

```csharp
// src/KSACars/Ksa/PhysicsHook.cs L40-L54
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
```

2. **Read the physics state in the planet-fixed frame.** Use the vehicle's physics state, not its analytic orbit position: a
   landed craft's analytic position can be metres away from where the physics has it, and a spring measured in centimetres
   would be thrown off the planet. `GetStatesCcf` gives the centre-of-mass position, velocity and attitude in Ccf[^ksa-physicsstates], where the
   orbital motion and the planet's spin cancel so a hub's velocity is simply its velocity over the ground:[^ksacars-buggies]

```csharp
// src/KSACars/Ksa/Buggies.cs L600-L605
        PhysicsStates.GetStatesCcf(in states.Origin, in states.Kinematic,
                                   out double3 positionCcf, out double3 velocityCcf, out doubleQuat body2Ccf);
        doubleQuat ccf2Body = body2Ccf.Inverse();
        doubleQuat body2Phys = states.Kinematic.Body2Phys;
        double3 spinBody = states.Kinematic.AngularVelocityPhys.Transform(body2Phys.Inverse());
        double3 velocityBody = velocityCcf.Transform(ccf2Body);
```

   (`states` is `craft.GetPhysicsStatesMutable()`, obtained a few lines earlier in the same method.)

3. **Wrap the height query so a failed read is "no claim", never flat ground.** `TerrainHeights` calls
   `GetTerrainHeightFromDirCce(dir, accurate: ...)` and rejects non-finite results; the caller leaves that wheel's contact
   invalid, which the model treats as a wheel in the air.[^ksacars-iterrainheights] Construct it with `accurate: true`:

```csharp
// src/KSACars/Ksa/TerrainHeights.cs L13-L33
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
}
```

4. **Sample once per hub.** For each wheel, rotate the hub's rest point (relative to the centre of mass) to Ccf, add it to the
   centre-of-mass position, take the unit direction from the planet centre, convert it to Cce with `Celestial.GetCcf2Cce()`,
   ask for the height, and subtract from the radius. The ground's "up" is the radial direction (the slope emerges from four
   wheels reading four heights):

```csharp
// src/KSACars/Ksa/Buggies.cs L613-L615
        doubleQuat ccf2Cce = body.GetCcf2Cce();
        TerrainHeights ground = new(body, accurate: true);
        LaunchPads pads = LaunchPads.On(body);
```

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

   The `pads.TryHeightOver` line is the launch-pad override from [approach 3](sample-the-ground-under-each-wheel-3.md); drop it
   for terrain alone. Each `WheelContact` then goes to the suspension and tyre model
   ([simulate wheels and suspension](../simulate-wheels-and-suspension-without-a-wheel-module/overview.md)).

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/PhysicsHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/PhysicsHook.cs) | The `PrepareWorker` prefix and its install |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L600-L670) | Ccf state, per-hub direction, height, `WheelContact` |
| [src/KSACars/Ksa/TerrainHeights.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/TerrainHeights.cs) | One body's height field via `GetTerrainHeightFromDirCce` |
| [src/KSACars/Sim/ITerrainHeights.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/ITerrainHeights.cs) | KSA-free seam; `false` means "no claim", never flat ground |
| [docs/KSA-TERRAIN.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/docs/KSA-TERRAIN.md) | The authors' measurements of the height field |

## Variations and gotchas

- **Never use the analytic position for a landed craft.** The orbit's state vectors and the physics state share an epoch but
  not a frame; on a landed craft they differ by metres.
- **Ecliptic velocities are not ground speeds.** Near Earth an ecliptic velocity carries about 29.8 km/s of orbital motion; in Ccf it
  cancels.
- **The height field answers the seabed.** There is no waterline clamp, so a car driven into the sea is sprung against the sea floor.
- **The default in `TerrainHeights` is misleading.** Its doc comment calls `accurate: false` the engine's choice and the default;
  in build 2026.10.10.5554 every `GetTerrainHeight*` overload defaults to `true` and the collision terrain uses that default.
  Every caller in KSACars passes `accurate: true` explicitly, so behaviour is unaffected.[^ksacars-terrainheights]
- **Another mod can change the answer.** A postfix on `GetTerrainHeightFromDirCce` (see
  [override the terrain height](../override-terrain-height-for-roofs-and-platforms/overview.md)) changes what this wheel code sees
  without moving colliders; KSAStructures uses exactly that to hold KSACars' wheels up on a garage roof.
- **A launch pad is not in the field.** The pad stands on a levelled decal disc, and the deck is higher than the field's answer
  (KSACars' notes measure the grate about 1.75 m above it); approaches 2 and 3 exist for that.
- **Sample cost.** Each call evaluates the modifier stack; four wheels once per frame is cheap, a ray march of tens of samples
  is not free.

## See also

- [Overview and comparison](overview.md)
- [Copy convex children under the shapes lock in an ExecuteNextVehicleSolvers prefix](sample-the-ground-under-each-wheel-2.md)
- [Rebuild box and cylinder colliders from template data and drop a ray per wheel](sample-the-ground-under-each-wheel-3.md)
- Mod feature: [Terrain contact under each wheel](../../../../mods/ksacars/features/terrain-contact-under-wheels.md)
- [Inject forces and impulses at the physics-safe point](../../solver/inject-forces-and-impulses-at-the-physics-safe-point/overview.md)
- The topic query-terrain-height-and-line-of-sight (other batch) covers the height query in general

[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-terrainpatch]: KSA.TerrainPatch (decompiled, build 2026.10.10.5554)
[^ksa-bubbleterraingrid]: KSA.BubbleTerrainGrid (decompiled, build 2026.10.10.5554)
[^ksa-terrainimpactfinder]: KSA.TerrainImpactFinder (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksacars-physicshook]: KSACars: src/KSACars/Ksa/PhysicsHook.cs
[^ksacars-buggies]: KSACars: src/KSACars/Ksa/Buggies.cs
[^ksacars-iterrainheights]: KSACars: src/KSACars/Sim/ITerrainHeights.cs
[^ksacars-terrainheights]: KSACars: src/KSACars/Ksa/TerrainHeights.cs
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
