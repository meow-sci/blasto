---
type: How-To
title: Postfix PhysicsEnvironment.RecomputePositionalValues on the worker
description: Remove the ocean from a cylinder of a body by zeroing OceanRadius, OceanVolume and OceanSurfaceArea after the game fills the environment, so a basement below sea level is dry to physics, with a volatile region swap and matching render patches
category: physics
group: aero-and-ground
topic: override-atmosphere-and-ocean-in-the-physics-environment
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksastructures/features/dry-volume-below-sea-level]
techniques: [harmony-postfix, harmony-prefix, custom-physics]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-postfix KSA.PhysicsEnvironment.RecomputePositionalValues
  - harmony-prefix KSA.Rendering.Water.Rendering.OceanRenderer.Render
  - harmony-prefix KSA.Rendering.Water.Rendering.OceanRenderer.RenderUnderwater
  - harmony-postfix KSA.Rendering.Water.Rendering.OceanRenderer.GetMaxVisibleDepth
lifecycle: [startup:all-mods-loaded, physics:vehicle-solver, frame:draw-ui, render:ocean]
game_types:
  - KSA.PhysicsEnvironment
  - KSA.VehicleProperties
  - KSA.Situation
  - KSA.Rendering.Water.Rendering.OceanRenderer
  - KSA.IParentBody
tags: [physics, ocean, buoyancy, harmony, worker-thread, volatile, underground]
---

Outcome: inside a cylinder you define under part of a planet, vehicles are told the body has no ocean, so a hall dug below
sea level is dry to buoyancy and water drag, while everywhere else the sea behaves as before. Prerequisites: StarMap and
Harmony. Demonstrated by KSAStructures, whose office has a ramp that ends in a hall 54 m under the ground, below sea level.
The same mod also hides the drawn ocean while the camera is inside, which this page covers as the rendering half.

## Background: how the game does it

The physical ocean is one sphere with waves. `ComputeOceanRadius` is the body's mean radius plus the ocean `Level`, or near
the surface the displaced wave height, and `RecomputeImmersion` counts a vehicle as immersed whenever that radius is above
the terrain radius under it and above the bottom of its bounding sphere:[^ksa-physicsenvironment]

```csharp
// current/decomp/KSA/PhysicsEnvironment.cs L141-L169
	private void RecomputeImmersion(in VehicleProperties props, double3 positionCcf)
	{
		float num = props.ComputeBoundingSphereRadiusAsmb();
		float num2 = 2f * num;
		double oceanVolume = 0.0;
		double oceanSurfaceArea = 0.0;
		if (OceanRadius > TerrainRadius)
		{
			double num3 = (positionCcf + double3.Unpack(props.MassToGeometryAsmb)).Length() - (double)num;
			double val = OceanRadius - num3;
			val = Math.Min(val, num2);
			if (val > 0.0)
			{
				if (val >= (double)num2)
				{
					oceanVolume = props.TotalVolume;
					oceanSurfaceArea = props.TotalSurfaceArea;
				}
				else
				{
					double num4 = val / (double)num2;
					oceanVolume = num4 * (double)props.TotalVolume;
					oceanSurfaceArea = num4 * (double)props.TotalSurfaceArea;
				}
			}
		}
		OceanVolume = oceanVolume;
		OceanSurfaceArea = oceanSurfaceArea;
	}
```

`ComputeDerivatives` applies buoyancy and water drag when `OceanVolume` or `OceanSurfaceArea` is non-zero, so a pit with
terrain below the sphere is "under water".[^ksa-physicsstates] The environment struct is filled by
`RecomputePositionalValues`, which `PhysicsStates.RecomputePositionalEnvironmentValues` calls as the vehicle moves, and which
`PhysicsBubble.UpdateFlotationRadius` calls for floating vehicles:[^ksa-physicsbubble]

```csharp
// current/decomp/KSA/PhysicsBubble.cs L2629-L2637
	private static double UpdateFlotationRadius(PhysicsStates body)
	{
		double3 positionClosestParentBub = body.GetPositionClosestParentBub(body.GetPositionBub());
		double3 double5 = body.ClosestParentBubToCcf(positionClosestParentBub);
		body.Environment.RecomputePositionalValues(positionClosestParentBub, double5, in body.Props);
		double num = double3.Dot(double5.NormalizeOrZero(), double3.Unpack(body.Props.MassToGeometryAsmb).Transform(body.Kinematic.Body2Phys));
		float num2 = body.Props.ComputeBoundingSphereRadiusAsmb();
		return body.Environment.OceanRadius + num + (double)num2 - (double)body.Props.Draft;
	}
```

A floating vehicle is therefore placed at `OceanRadius`; zeroing that for a floating vehicle would put it at the centre of the
planet. This is the one case the postfix must skip.

## Lifecycle and timing

- **Install: `startup:all-mods-loaded`.** KSAStructures' `[StarMapAllModsLoaded]` method calls `Office.Place()`, which installs
  the patches; the patched methods are runtime methods, so nothing needs to precede content loading.[^ksas-mod][^ksas-office]
- **Define and watch: the mod's per-frame step on the main thread.** `Office.Update` runs the first time a controlled
  vehicle exists (the first simulated step), defines the columns once, and calls `OfficeDry.Watch()` every frame. The step
  runs from `StarMapAfterGui`, a prefix on `Program.OnFrameCelestials`, or `StarMapAfterOnFrame`, whichever comes first.
- **The postfix: `physics:vehicle-solver`.** `Universe.ExecuteNextVehicleSolvers` (after `Vehicle.PrepareWorker` per vehicle on
  the main thread) queues the vehicle solver; `RecomputePositionalValues` then runs on worker threads, several times per
  substep, for several vehicles at once.[^ksa-universe] The postfix allocates nothing, takes no lock and swallows every
  exception, and reads its regions from a `volatile` field that the main thread replaces whole.
- **The render prefixes: `render:ocean`, main thread.** `Program.RenderGame` calls `OceanRenderer.Render` after the opaque
  scene and `RenderUnderwater` after the translucency pass, for the main viewport and only when the render camera's nearby
  body is billboarded.[^ksa-program] `GetMaxVisibleDepth` is read by the planet renderer and by frustum culling.[^ksa-oceanrenderer]
  See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-physics-step).

## Steps

1. **Describe a region in planet-fixed terms.** Store an axis direction in CCF (body-fixed), a radius and a minimum and
   maximum distance from the body centre. Keep the list in a `volatile` array and replace it whole, never edit in place,
   because the postfix reads it on worker threads:[^ksas-officedry]

```csharp
// src/KSAStructures/Ksa/OfficeDry.cs L17-L39
    // A column under a place on the body's surface, between two radii from its centre. The list is
    // swapped whole: the physics postfix reads it on worker threads.
    private sealed class Volume
    {
        public required IParentBody Body;
        public required double3 AxisCcf;
        public required double RadiusMetres, MinRadius, MaxRadius;
    }

    private static volatile Volume[] _volumes = [];
    private static volatile bool _mainCameraDry;

    public static bool Defined => _volumes.Length > 0;

    /// <summary>Adds a column about a direction from the body's centre, between two radii.</summary>
    public static void Define(Celestial body, double3 axisCcf, double radiusMetres, double minRadius, double maxRadius)
    {
        _volumes = [.. _volumes, new Volume
        {
            Body = body, AxisCcf = axisCcf.Normalized(), RadiusMetres = radiusMetres, MinRadius = minRadius, MaxRadius = maxRadius,
        }];
        Log.Info($"office: dry volume defined, {maxRadius - minRadius:F0} m deep and {radiusMetres * 2.0:F0} m across");
    }
```

2. **Define the columns from terrain data on the main thread.** On the first step with a controlled craft on the right body,
   compute the ground radius at the site (`MeanRadius + GetTerrainHeightFromDirCcf`) and define one column per dug volume:[^ksas-officelift]
   2 m wider than the pit, from far enough under it to just below the surface:

```csharp
// src/KSAStructures/Ksa/OfficeLift.cs L112-L129
            if (!OfficeDry.Defined && KsaWorld.ControlledVehicle is { } any && KsaWorld.ParentBody(any) is { } planet
                && ReferenceEquals(planet.BodyTemplate, _earth))
            {
                // Only what is dug below the sea: the deep pit, and the vault where there is one.
                double level = planet.MeanRadius + planet.GetTerrainHeightFromDirCcf(_tower.ForwardCcf);
                _tower.GetAxesCcf(out _, out double3 dryEast, out double3 dryNorth);
                double3 At(double east, double north) =>
                    (_tower.ForwardCcf * level) + (dryEast * (east + 3.0)) + (dryNorth * (north - LandmarkNorth));

                OfficeDry.Define(planet, At(OfficeDeep.PitEast, OfficeDeep.PitNorth), OfficeDeep.PitRadius + 2.0,
                                 level - OfficeDeep.Depth - 30.0, level - 0.2);
                if (VaultDepth > 0.0)
                {
                    OfficeDry.Define(planet, At(VaultEast, VaultNorth), VaultRadius + 2.0, level - VaultDepth - 30.0, level - 0.2);
                }
            }

            OfficeDry.Watch();
```

3. **Test a point against the columns, allocation-free.** The point is in a column when its distance from the body centre is
   between the two radii, it is on the axis side of the planet (`along > 0`) and its perpendicular distance from the axis is
   within the radius:

```csharp
// src/KSAStructures/Ksa/OfficeDry.cs L90-L105
    private static bool Inside(IParentBody body, double3 positionCcf)
    {
        Volume[] volumes = _volumes;
        if (volumes.Length == 0) return false;

        double radius = positionCcf.Length();
        foreach (Volume volume in volumes)
        {
            if (!ReferenceEquals(body, volume.Body) || radius < volume.MinRadius || radius > volume.MaxRadius) continue;

            double along = double3.Dot(positionCcf, volume.AxisCcf);
            if ((radius * radius) - (along * along) <= volume.RadiusMetres * volume.RadiusMetres && along > 0.0) return true;
        }

        return false;
    }
```

4. **Postfix `PhysicsEnvironment.RecomputePositionalValues`.** `PhysicsEnvironment` is a struct, so take `ref
   PhysicsEnvironment __instance`; the other parameters bind by name (`positionClosestParentCcf`, `props`). Zero the three
   ocean values inside a column, and leave them alone when the body had no ocean there or the vehicle is already
   `Situation.Floating`:

```csharp
// src/KSAStructures/Ksa/OfficeDry.cs L107-L123
    // Worker threads, several times a sub-step: nothing here may allocate or throw. A craft already
    // floating is placed at the ocean's radius by the engine, so it is left its ocean.
    private static void AfterPositional(ref PhysicsEnvironment __instance, double3 positionClosestParentCcf, in VehicleProperties props)
    {
        try
        {
            if (__instance.OceanRadius <= 0.0 || props.Situation == Situation.Floating) return;
            if (!Inside(__instance.ClosestParent, positionClosestParentCcf)) return;

            __instance.OceanRadius = 0.0;
            __instance.OceanVolume = 0.0;
            __instance.OceanSurfaceArea = 0.0;
        }
        catch
        {
        }
    }
```

   With `OceanVolume` and `OceanSurfaceArea` zero, `ComputeDerivatives` skips the buoyancy and ocean-drag branch, and with
   `OceanRadius` zero the situation tests that compare against it no longer see water.

5. **Install each patch separately and log what a failure costs.** Resolve the target with `AccessTools.Method`, bind the
   prefix or postfix by name, and on failure log the player-visible consequence. The same install patches the render side:

```csharp
// src/KSAStructures/Ksa/OfficeDry.cs L61-L88
    public static void Install(Harmony harmony)
    {
        Patch(harmony, typeof(PhysicsEnvironment), nameof(PhysicsEnvironment.RecomputePositionalValues), null, nameof(AfterPositional),
              "craft in the vault float");
        Patch(harmony, typeof(OceanRenderer), nameof(OceanRenderer.Render), nameof(BeforeOcean), null, "the sea is drawn through the vault");
        Patch(harmony, typeof(OceanRenderer), nameof(OceanRenderer.RenderUnderwater), nameof(BeforeUnderwater), null,
              "the vault is fogged as if under water");
        Patch(harmony, typeof(OceanRenderer), nameof(OceanRenderer.GetMaxVisibleDepth), null, nameof(AfterMaxVisibleDepth),
              "the vault's floor is black from above sea level");
    }

    private static void Patch(Harmony harmony, Type type, string method, string? prefix, string? postfix, string cost)
    {
        try
        {
            MethodInfo? target = AccessTools.Method(type, method);
            if (target is null) { Log.Warn($"office: no {type.Name}.{method}; {cost}"); return; }

            const BindingFlags Mine = BindingFlags.NonPublic | BindingFlags.Static;
            harmony.Patch(target,
                          prefix: prefix is null ? null : new HarmonyMethod(typeof(OfficeDry).GetMethod(prefix, Mine)!),
                          postfix: postfix is null ? null : new HarmonyMethod(typeof(OfficeDry).GetMethod(postfix, Mine)!));
        }
        catch (Exception e)
        {
            Log.Warn($"office: could not patch {type.Name}.{method} ({e.GetBaseException().Message}); {cost}");
        }
    }
```

## Removing the ocean from the picture

Physics follows each vehicle's own position; rendering follows the camera. The mod hides the drawn ocean only while the
viewing camera is inside a column:

| Patch | Effect while the camera is inside |
|---|---|
| Prefix on `OceanRenderer.Render(Celestial, CommandBuffer, IViewport, int)` | Returns false: no ocean surface for that viewport |
| Prefix on `OceanRenderer.RenderUnderwater(Celestial?, CommandBuffer, IViewport, int)` | Returns false: no underwater fog |
| Postfix on `OceanRenderer.GetMaxVisibleDepth(Celestial)` | Sets the result to -1 while the main camera is inside, which the planet shader and culling treat as "no ocean occlusion" of deep terrain |

Both prefixes test the camera of the viewport they are handed (`-camera.GetPositionEgo(body)` rotated by `GetCce2Ccf()`
is the camera's CCF position). `GetMaxVisibleDepth` has no viewport, so it reads a `volatile bool` that
`OfficeDry.Watch()` refreshes once per step on the main thread:

```csharp
// src/KSAStructures/Ksa/OfficeDry.cs L125-L145
    private static bool BeforeOcean(Celestial atmosphericBody, IViewport inViewport)
    {
        try { return !CameraInside(atmosphericBody, inViewport); }
        catch { return true; }
    }

    private static bool BeforeUnderwater(Celestial? celestial, IViewport inViewport)
    {
        try { return celestial is null || !CameraInside(celestial, inViewport); }
        catch { return true; }
    }

    // Terrain further under the sea than the water is clear is drawn black to a camera above it.
    private static void AfterMaxVisibleDepth(Celestial celestial, ref double __result)
    {
        try { if (_mainCameraDry) __result = -1.0; }
        catch { }
    }

    private static bool CameraInside(Celestial body, IViewport viewport) =>
        Inside(body, (-viewport.GetCamera().GetPositionEgo(body)).Transform(body.GetCce2Ccf()));
```

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeDry.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeDry.cs) | Volumes, column test, the physics postfix, the render patches, `Watch()` and installation |
| [src/KSAStructures/Ksa/OfficeLift.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeLift.cs#L112-L129) | Where the columns are defined and watched, from the per-frame step |
| [src/KSAStructures/Sim/OfficeDeep.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/OfficeDeep.cs#L10-L23) | The pit's depth and radius that size the column |
| [src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L51-L92) | `Place()` installs `OfficeDry` inside the other patches |
| [src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L42-L47) | `[StarMapAllModsLoaded]` entry that calls `Office.Place()` |

## Variations and gotchas

- **The ocean is a sphere everywhere.** Any dug space below sea level, by any mod, is flooded to physics and drawn through the
  sea surface unless something like this is done.
- **A floating craft keeps its ocean.** `UpdateFlotationRadius` returns `OceanRadius` plus offsets as the craft's radius; zero
  would teleport it to the planet's centre.
- **One point per craft.** The test uses the single position handed to `RecomputePositionalValues`, so a large craft straddling
  a column's edge switches all at once.
- **Do not edit the region list in place.** Workers read it concurrently; build a new array and assign the `volatile` field.
- **The depth flag is the main camera's.** `GetMaxVisibleDepth` is answered for every caller from the main camera's state as of
  the last step. When the step only reaches the frame postfix (UI hidden and no `OnFrameCelestials` prefix) the flag is one
  frame late.
- **Rendering follows the camera, not the craft.** The ocean is hidden only while the viewing camera is inside a column.
- **Combine with approach 1 freely.** The postfix sees whatever the prefixed atmosphere functions produced; it only touches the
  three ocean values.
- **Version drift.** The pinned mod targets KSA 2026.10.7.5541; `AfterPositional` binds `positionClosestParentCcf` and
  `props` by name, so renaming either parameter in a game update turns the postfix into a Harmony error, which the install's
  `try`/`catch` reports as "craft in the vault float".

## See also

- [Overview and comparison](overview.md)
- [Prefix the density and pressure functions and move the private boundary height](override-atmosphere-and-ocean-in-the-physics-environment-1.md)
- Mod feature: [Dry volume below sea level](../../../../mods/ksastructures/features/dry-volume-below-sea-level.md)
- [Write worker-thread-safe solver patches](../../solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-2.md): approach 2 there covers this volatile region swap
- The topic query-terrain-height-and-line-of-sight (other batch) covers the terrain radius this environment compares the ocean against

[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksas-mod]: KSAStructures: src/KSAStructures/Ksa/KSAStructuresMod.cs
[^ksas-office]: KSAStructures: src/KSAStructures/Ksa/Office.cs
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-oceanrenderer]: KSA.Rendering.Water.Rendering.OceanRenderer (decompiled, build 2026.10.10.5554)
[^ksas-officedry]: KSAStructures: src/KSAStructures/Ksa/OfficeDry.cs
[^ksas-officelift]: KSAStructures: src/KSAStructures/Ksa/OfficeLift.cs
