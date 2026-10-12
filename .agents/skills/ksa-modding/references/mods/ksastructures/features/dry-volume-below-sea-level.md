---
type: Mod Feature
title: Dry volume below sea level
description: A radial column under the office is kept free of KSA's planet-wide ocean, by zeroing the ocean values physics sees inside it and skipping the ocean surface, underwater fog and ocean depth occlusion while the camera is inside
mod: ksastructures
category: celestials
techniques: [harmony-postfix, harmony-prefix, custom-physics]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-postfix KSA.PhysicsEnvironment.RecomputePositionalValues
  - harmony-prefix KSA.Rendering.Water.Rendering.OceanRenderer.Render
  - harmony-prefix KSA.Rendering.Water.Rendering.OceanRenderer.RenderUnderwater
  - harmony-postfix KSA.Rendering.Water.Rendering.OceanRenderer.GetMaxVisibleDepth
lifecycle: [startup:all-mods-loaded, physics:vehicle-solver, frame:draw-ui, render:planet, render:ocean]
game_types:
  - KSA.PhysicsEnvironment
  - KSA.VehicleProperties
  - KSA.Situation
  - KSA.IParentBody
  - KSA.Celestial
  - KSA.PhysicsStates
  - KSA.PhysicsBubble
  - KSA.Rendering.Water.Rendering.OceanRenderer
  - KSA.PlanetRenderer
  - KSA.PlanetFrustumCulling
tags: [ocean, buoyancy, physics, rendering, worker-threads, harmony, terrain]
---

The office's deep ramp ends in a hall 54 m under the ground, below sea level, and the optional vault under the lift can be deeper still. KSA's ocean has no idea anything is
roofed: a craft driven down there would float, and the camera would see the sea surface and
underwater fog through the walls. Inside one cylinder around each dug volume the mod tells physics the
body has no ocean. While the main camera is inside, the ocean is not drawn and terrain below sea level
is not blacked out.

## How it works

**KSA's ocean model.** A craft's ocean radius is the body's mean radius plus the ocean's `Level`, or
near the surface the displaced wave height from `OceanRenderer.GetOceanHeightAtPositionCcf`. It is
one sphere with waves and does not depend on where the land is
([current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L120-L139)). `RecomputeImmersion` then counts the
craft as immersed whenever that radius is above the terrain radius under it and above the bottom of
its bounding sphere, and sets `OceanVolume` and `OceanSurfaceArea` in proportion
([current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L141-L169)). The vehicle step applies buoyancy and
water drag when either is non-zero ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1002-L1006)). A pit
dug below sea level has terrain below the sphere, so it is under water.[^ksa-physicsenvironment]

**The volumes.** On the first simulated step with a controlled craft on Earth, `Office.Update`
defines a column for the deep pit. It is centred on the pit (`PitEast`, `PitNorth`), 2 m wider than
`PitRadius` (62 m), and runs from 84 m below the tower's ground radius to 0.2 m below it. A second
column covers the vault when one is configured ([src/KSAStructures/Ksa/OfficeLift.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeLift.cs#L112-L127),
[src/KSAStructures/Sim/OfficeDeep.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/OfficeDeep.cs#L10-L23)). Each is stored in planet-fixed (CCF) terms: an
axis direction, a radius, a minimum and maximum distance from the body centre. `Inside` tests one
point against every column of the same body
([src/KSAStructures/Ksa/OfficeDry.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeDry.cs#L90-L105)).

**Physics: postfix on `PhysicsEnvironment.RecomputePositionalValues`.** After the engine has computed
the craft's environment, the postfix zeroes `OceanRadius`, `OceanVolume` and `OceanSurfaceArea`
when the craft's CCF position is inside a column. It leaves them alone if the body had no ocean there
or the craft is already `Situation.Floating`. `PhysicsEnvironment` is a struct, so the postfix takes
`ref PhysicsEnvironment __instance` ([src/KSAStructures/Ksa/OfficeDry.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeDry.cs#L107-L123)).

**Rendering.**

| Patch | Effect while the camera is inside |
|---|---|
| Prefix on `OceanRenderer.Render(Celestial, CommandBuffer, IViewport, int)` | Returns false: no ocean surface for that viewport |
| Prefix on `OceanRenderer.RenderUnderwater(Celestial?, CommandBuffer, IViewport, int)` | Returns false: no underwater fog post-process |
| Postfix on `OceanRenderer.GetMaxVisibleDepth(Celestial)` | Returns -1 while the *main* camera is inside, which the planet shaders read as "no ocean occlusion" |

Both prefixes test the camera of the viewport they are handed:
`-camera.GetPositionEgo(body)` rotated by `GetCce2Ccf()` gives the camera's CCF position
([src/KSAStructures/Ksa/OfficeDry.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeDry.cs#L125-L145)). `GetMaxVisibleDepth` has no viewport, so it
reads `_mainCameraDry`, which `OfficeDry.Watch()` refreshes once per step on the main thread. The
negative depth matters because `Planet.frag` and the culling shader treat terrain deeper than
`oceanLevel - oceanMaxVisibleDepth` as hidden under the sea for a camera above sea level, and skip
that test when the value is negative
([current/Content/Core/Shaders/Planet/Planet.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Planet/Planet.frag#L82-L92),
[current/Content/Core/Shaders/Culling/Culling.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Culling/Culling.glsl#L64-L75)).[^ksa-planet-frag]

## Lifecycle and timing

- **Install:** `Office.Place` at `[StarMapAllModsLoaded]` calls `OfficeDry.Install(harmony)`
  ([src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L75-L86)).
- **Definition and `Watch()`:** inside `Office.Update(dtSim)`, from the mod's per-frame step on the
  main thread. That step runs from whichever comes first of `[StarMapAfterGui]`, a prefix on
  `Program.OnFrameCelestials`, or `[StarMapAfterOnFrame]`; normally that is before the render.
  Volumes are swapped as a whole new array into a `volatile` field, never edited in place
  ([src/KSAStructures/Ksa/OfficeDry.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeDry.cs#L26-L39)).
- **Physics postfix (`physics:vehicle-solver`):** `RecomputePositionalValues` runs inside the vehicle
  solver job that `Universe.ExecuteNextVehicleSolvers` queues on `JobSystems.VehicleSolver`
  ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2139)). It is called through
  `PhysicsStates.RecomputePositionalEnvironmentValues` after each integration step and before
  `ComputeDerivatives` ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L526-L539),
  [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1174-L1175),
  [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1242)), from the constraint simulation
  ([current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L411)), and from the flotation code. So it runs on
  worker threads, several times per substep. The postfix allocates nothing, takes no lock and
  swallows every exception.
- **Why `Floating` is skipped:** a floating craft is positioned by `UpdateFlotationRadius`, which
  calls `RecomputePositionalValues` and then returns `OceanRadius` plus offsets as the craft's
  radius. A zeroed ocean would put it at the planet's centre
  ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L2629-L2637),
  [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L2571-L2575),
  [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1450-L1455)).[^ksa-physicsbubble]
- **Ocean prefixes (`render:ocean`):** `RenderGame` calls `OceanRenderer.Render` after the opaque
  scene and `RenderUnderwater` after the translucency pass, for the main viewport only and only when
  the render camera's nearby body has a planet mesh (`IsBillboarded()`) ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4858-L4867),
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4905-L4909)). Main thread, during command recording.
- **Depth postfix (`render:planet`):** `GetMaxVisibleDepth` is read when `PlanetRenderer.Render` fills
  its uniform and when `PlanetFrustumCulling` fills its push constant
  ([current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L1978-L1988),
  [current/decomp/KSA/PlanetFrustumCulling.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetFrustumCulling.cs#L204-L210)).

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeDry.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeDry.cs#L107-L123) | Worker-thread postfix that removes the ocean |
| [src/KSAStructures/Ksa/OfficeDry.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeDry.cs#L125-L145) | Render prefixes and the depth postfix |
| [src/KSAStructures/Ksa/OfficeDry.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeDry.cs#L90-L105) | Column test in CCF |
| [src/KSAStructures/Ksa/OfficeDry.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeDry.cs#L47-L70) | `Watch()` and patch installation |
| [src/KSAStructures/Ksa/OfficeLift.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeLift.cs#L112-L129) | Where the columns are defined and watched |

## Snippets

```csharp
// src/KSAStructures/Ksa/OfficeDry.cs L90-L123
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

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `PhysicsEnvironment.RecomputePositionalValues`, `OceanRadius`, `OceanVolume`, `OceanSurfaceArea`, `ClosestParent` | [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L10-L118) | Patched; the values zeroed inside the column |
| `VehicleProperties.Situation`, `Situation.Floating` | [current/decomp/KSA/Situation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Situation.cs#L3-L13) | Leaves a floating craft its ocean |
| `PhysicsStates.RecomputePositionalEnvironmentValues` | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L526-L539) | The per-substep caller |
| `OceanRenderer.Render`, `RenderUnderwater` | [current/decomp/KSA.Rendering.Water.Rendering/OceanRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Water.Rendering/OceanRenderer.cs#L534-L630) | Patched; skipped from inside |
| `OceanRenderer.GetMaxVisibleDepth` | [current/decomp/KSA.Rendering.Water.Rendering/OceanRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Water.Rendering/OceanRenderer.cs#L473-L482) | Patched; -1 disables ocean occlusion of deep terrain |
| `Camera.GetPositionEgo`, `Celestial.GetCce2Ccf` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236), [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L538) | Camera position in the body-fixed frame |

## Pitfalls and version notes

- **Ocean is a sphere everywhere.** Any dug space below sea level, by any mod, is flooded to physics
  and drawn through the sea surface unless something like this is done.
- **A craft already floating keeps its ocean** inside the column, because zeroing it would teleport
  the craft to the body's centre.
- **The depth flag is the main camera's.** `GetMaxVisibleDepth` is answered for every caller, camera
  windows included, from the main camera's state as of the last step. When the step only reaches
  the frame postfix (UI hidden and the `OnFrameCelestials` prefix not installed) it is one frame late.
- **One point per craft.** The column test uses the single position handed to
  `RecomputePositionalValues`, so a large craft straddling the column's edge switches all at once.
- **Rendering follows the camera, not the craft.** The ocean is hidden only while the viewing camera
  is inside a column; physics follows each craft's own position.
- **Version:** the mod is built against KSA 2026.10.7.5541; this page was checked against
  2026.10.10.5554, and the decompiled diff between the two does not touch `PhysicsEnvironment`,
  `PhysicsStates`, `PhysicsBubble` or `OceanRenderer`.

## See also

- [Terrain decal pits](./terrain-decal-pits.md): how the volume below sea level is dug
- [Terrain height roof override](./terrain-height-roof-override.md): the other patch that answers for the dug ground (main thread, for cameras and wheel code)
- [Generated deep ramp](./generated-deep-ramp.md): the hall this keeps dry
- [Lift as a moving landmark](./lift-moving-landmark.md): the vault stop and `Office.Update`
- [Frame step plumbing](./frame-step-plumbing.md): where `Office.Update` runs each frame
- [Postfix PhysicsEnvironment.RecomputePositionalValues on the worker](../../../howto/physics/aero-and-ground/override-atmosphere-and-ocean-in-the-physics-environment/override-atmosphere-and-ocean-in-the-physics-environment-2.md) - how-to article demonstrating this feature
- [Read-only tables filled on the main thread before physics starts](../../../howto/physics/solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-2.md) - how-to article demonstrating this feature

[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled)
[^ksa-planet-frag]: Core shader Planet/Planet.frag
