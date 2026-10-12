---
type: How-To
title: Submit per-viewport lights from an UpdateStaticObjectRenderData postfix with a shadow-caster budget
description: Light a building with ceiling lamps by building spot lights in each viewport's camera-relative frame from a postfix on LocationReference.UpdateStaticObjectRenderData and flagging only the nearest ones to cast shadows
category: rendering
group: lights-and-particles
topic: add-dynamic-lights-to-the-world
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksastructures/features/garage-spot-lights]
techniques: [dynamic-lights, harmony-postfix]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-postfix KSA.LocationReference.UpdateStaticObjectRenderData
  - starmap StarMapAfterGui
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:render, render:shadows]
game_types:
  - KSA.Rendering.Lighting.Light
  - KSA.Rendering.Lighting.ELightFlags
  - KSA.Rendering.Lighting.ILightSystem
  - KSA.Rendering.Lighting.ClusteredLightSystem
  - KSA.LocationReference
  - KSA.Program
  - KSA.IViewport
tags: [lights, spotlight, static-object, shadow-atlas, shadow-budget, per-viewport]
---
Outcome: ceiling lamps for a building (here 90 warm lamps and 14 fill lights in an underground garage) that follow the building's frame in every viewport, with the lamps nearest the camera casting soft shadows within the player's shadow-slot budget.

Demonstrated by KSAStructures ([Garage spot lights submitted per viewport](../../../../mods/ksastructures/features/garage-spot-lights.md)). Prerequisites: StarMap, Harmony. For a lamp attached to a vessel use [approach 1](add-dynamic-lights-to-the-world-1.md); for lamps tied to a place on a celestial use this one.

## Background: how the game does it

Lights are cleared every frame (`Program.OnFrame` L2317-L2318) and must be submitted again during render-data building, once per visible viewport. `Program.Render` calls `UpdateRenderingResources`, which loops over every visible viewport and calls `Universe.UpdateRenderData(viewport)`; that reaches each location's `LocationReference.UpdateStaticObjectRenderData(IViewport, Celestial, int)`:

```csharp
// current/decomp/KSA/LocationReference.cs L156-L180
public void UpdateStaticObjectRenderData(IViewport viewport, Celestial celestial, int frameIndex)
{
	if (!ShowGroundMarker)
	{
		return;
	}
	StaticObject staticObject = GetStaticObject();
	if (staticObject != null)
	{
		Camera camera = viewport.GetCamera();
		doubleQuat ccf2Cce = celestial.GetCcf2Cce();
		double3 double5 = ForwardCcf.Transform(ccf2Cce);
		if (camera.IsVisibleTo(double5, celestial))
		{
			double3 double6 = camera.EclToEgo(celestial.GetSurfacePositionEclFromCce(double5));
			GetAxesCcf(out var upCcf, out var eastCcf, out var northCcf);
			double3 double7 = upCcf.Transform(ccf2Cce);
			double3 double8 = eastCcf.Transform(ccf2Cce);
			double3 double9 = northCcf.Transform(ccf2Cce);
			double3 double10 = double6 + double7 * staticObject.GroundOffset;
			double4x4 matrixAsmb2Ego = new double4x4(double7.X, double7.Y, double7.Z, 0.0, double8.X, double8.Y, double8.Z, 0.0, double9.X, double9.Y, double9.Z, 0.0, double10.X, double10.Y, double10.Z, 1.0);
			staticObject.UpdateRenderData(viewport, frameIndex, in matrixAsmb2Ego);
		}
	}
}
```

The engine's explosion lights are submitted from the same loop (`ExplosionFlashSystem.SubmitLights(viewport2)`, Program.cs L4395). Straight after the loop, `WriteForwardLights` per viewport and `LightSystem.UpdateRenderResources(RenderedViewport)` sort the lights and assign shadow tiles ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4393-L4409)). What the engine does with a submitted light:

| Step | Engine behaviour (build 2026.10.10.5554) |
|---|---|
| `CreateLightInstance` | Ignored when `Range` is zero or the viewport's `LightMode` is `None`; appended to the viewport's forward list (cap 256); appended to the clustered list (cap `MaxLights` 1024) only for the main viewport not in `Forward` mode |
| `WriteForwardLights` | Per visible viewport keeps the top lights by `Intensity * Range^2 / distance^2`: 8 for a `Clustered` (main) viewport, `clamp(2000000 / (width * height), 1, 16)` for a `Forward`-mode camera window |
| `UpdateRenderResources` / `PackLightsAndShadows` | Sorts by influence (times 1.5 for `CastsShadows`) then asks the shadow atlas for a tile in that order |
| `LightSystemShadowMap.RegisterSpotLight` | Returns no slot when the light is not flagged, when `MaxShadowCasters` tiles are taken, or when the tile does not fit (halved until it fits, down to 16 px) |
| `Light.CreateLightData` | Shadowed only with `CastsShadows` and a slot, so a flagged lamp without a slot is drawn unshadowed; `SoftShadows` dropped when the player's hard-shadows setting is on |
| `ShadowTileSize` | Non-zero overrides `ComputeShadowTileSize` |
| `MaxShadowCasters` | `min(GameSettings.GetLightSystemShadowSlots(), 64)`, or 0 with light shadows off |

```csharp
// current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs L1125-L1147
public void PackLightsAndShadows(Camera inCamera)
{
	uint defaultTileSize = ShadowMap.DefaultTileSize;
	double3 forwardEcl = inCamera.GetForwardEcl();
	Span<LightSortInstance> span = _sortedLightInstances.AsSpan();
	for (int i = 0; i < span.Length; i++)
	{
		LightSortInstance lightSortInstance = span[i];
		if ((int)_acceptedLights.Count >= 1024)
		{
			break;
		}
		ref Light reference = ref Lights[lightSortInstance.Index];
		if (EnableShadows)
		{
			uint inTileSize = ((reference.ShadowTileSize != 0) ? reference.ShadowTileSize : ComputeShadowTileSize(reference.Position, reference.Range, defaultTileSize));
			reference.ShadowIndex = ShadowMap.Register(reference, inTileSize, out var outShadowData);
			if (reference.ShadowIndex != uint.MaxValue)
			{
				_shadowData[reference.ShadowIndex] = outShadowData;
				_shadowCastingLights[_shadowCastingLightCount++] = reference;
			}
		}
```

## Lifecycle and timing

- **Install:** `[StarMapAllModsLoaded]` (`Office.Place`).
- **Lights cleared:** `Program.OnFrame` (`frame:lights`), after the GUI pass; a light created in `StarMapAfterGui` would be gone before it is used.
- **Lights submitted (this hook):** inside `Program.Render`, in `UpdateRenderingResources`, per visible viewport, on the main thread, before `RenderGame` records any command: `frame:render`.
- **Lights consumed:** `WriteForwardLights` and `UpdateRenderResources` right after the loop; `RenderGame` then draws the light shadow atlas (`render:shadows`, row 4 of the [RenderGame table](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence)) and the scene samples the result.
- **Why this hook:** it runs once per visible viewport per frame with the viewport handed in, between clear and consumption, right where the engine works out the landmark's position and axes for that viewport. Light positions are camera-relative (the engine measures influence by `Position.LengthSquared()`), so each viewport needs lamps placed for its own camera.
- **Settings copy:** `[StarMapAfterGui]`, skipped while the UI is hidden; the last value stays in force.

## Steps

1. **Patch `UpdateStaticObjectRenderData`** at `[StarMapAllModsLoaded]`, when you place your landmarks:

   ```csharp
// src/KSAStructures/Ksa/Office.cs L75-L86
MethodInfo? target = AccessTools.Method(typeof(LocationReference), nameof(LocationReference.UpdateStaticObjectRenderData));
MethodInfo postfix = typeof(Office).GetMethod(nameof(AfterRenderData), BindingFlags.NonPublic | BindingFlags.Static)!;
if (target is null) Log.Warn("office: no render hook, the lift car is not drawn");
else
{
    _harmony = new Harmony(HarmonyId);
    _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
    Underground.Install(_harmony);
    OfficeDry.Install(_harmony);
    ClutterZones.Install(_harmony);
    KeepOutOfSaves(_harmony);
}
```

2. **Rebuild the origin and axes the engine uses** for that location and viewport (camera `EclToEgo` of the landmark's surface point, axes from `GetAxesCcf` rotated into the body frame), and call your lamp routine before the engine's visibility test, so lamps are submitted even when the engine does not draw the building:

   ```csharp
// src/KSAStructures/Ksa/Office.cs L122-L140
private static void AfterRenderData(LocationReference __instance, IViewport viewport, Celestial celestial, int frameIndex)
{
    try
    {
        bool tower = ReferenceEquals(__instance, _tower);
        if (!tower && !ReferenceEquals(__instance, _lift)) return;

        Camera camera = viewport.GetCamera();
        doubleQuat ccf2Cce = celestial.GetCcf2Cce();
        double3 forwardCce = __instance.ForwardCcf.Transform(ccf2Cce);
        double3 origin = camera.EclToEgo(celestial.GetSurfacePositionEclFromCce(forwardCce));
        __instance.GetAxesCcf(out double3 upCcf, out double3 eastCcf, out double3 northCcf);
        double3 up = upCcf.Transform(ccf2Cce), east = eastCcf.Transform(ccf2Cce), north = northCcf.Transform(ccf2Cce);

        if (tower)
        {
            Underground.Light(viewport, origin + (east * 3.0) - (north * LandmarkNorth), up, east, north);
            if (camera.IsVisibleTo(forwardCce, celestial)) return;
        }
```

3. **Gate, choose casters, submit.** Submit nothing unless lamps are on and the building is within 600 m of this viewport's camera. Only for the main viewport, with the camera close, sort lamps by squared distance and flag the nearest `MaxShadowCasters - 4` (four slots are left for part lights and exhaust). Each lamp is `Light.CreateSpotLight(position, -up, range, outer, inner, warm, intensity, flags)` with `ShadowTileSize = 512` on casters; fills are unflagged:

   ```csharp
// src/KSAStructures/Ksa/OfficeUnderground.cs L151-L192
if (LampsOn && origin.Length() < DarkWithinMetres)
{
    try
    {
        // The nearest lamps cast shadows, as many as the player's slots allow and only with the
        // camera at the site: chosen here, because a flagged lamp the engine finds no slot for
        // is drawn unshadowed, and its own choice moves with every other light in view.
        int casting = 0;
        if (main && Shadows && origin.Length() < ShadowsWithinMetres)
        {
            for (int i = 0; i < All.Length; i++)
            {
                Nearest[i] = i;
                Nearness[i] = (origin + (up * All[i].X) + (east * All[i].Y) + (north * All[i].Z)).LengthSquared();
            }

            Array.Sort(Nearness, Nearest);
            casting = Math.Clamp(Program.LightSystem.MaxShadowCasters - SlotsLeftForOthers, 0, All.Length);
        }

        double3 down = -up;
        for (int i = 0; i < All.Length; i++)
        {
            (double x, double y, double z, float range, float intensity) = All[i];
            bool casts = Array.IndexOf(Nearest, i, 0, casting) >= 0;
            Light lamp = KSA.Rendering.Lighting.Light.CreateSpotLight(
                origin + (up * x) + (east * y) + (north * z), down, range, OuterAngle, InnerAngle, Warm,
                intensity * LampScale, casts ? ELightFlags.CastsShadows | ELightFlags.SoftShadows : ELightFlags.None);
            if (casts) lamp.ShadowTileSize = ShadowTile;
            Program.LightSystem.CreateLightInstance(lamp, viewport);
        }

        foreach ((double y, double z, float range, float intensity) in Fills)
        {
            Program.LightSystem.CreateLightInstance(
                KSA.Rendering.Lighting.Light.CreateSpotLight(
                    origin + (up * LampHeight) + (east * y) + (north * z), down, range, OuterAngle, InnerAngle, Warm,
                    intensity * LampScale), viewport);
        }
    }
    catch { }
}
```

   The mod picks casters itself because the engine's own choice is by influence across every light in view and moves as other lights come and go; a flagged lamp that loses the race is drawn unshadowed.

4. **Make the lamp's shadows real.** Static objects cast no shadows in the engine; see [add invisible shadow casters](../../meshes/add-invisible-shadow-casters/overview.md), which this article's garage uses so pillars and cars block lamp light.

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeUnderground.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeUnderground.cs#L137-L202) | `Underground.Light`: gate, nearest-lamp choice, spot and fill submission |
| [src/KSAStructures/Ksa/OfficeUnderground.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeUnderground.cs#L46-L97) | Lamp and fill tables |
| [src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L117-L160) | `AfterRenderData` postfix that calls `Underground.Light` per viewport |
| [src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L152-L172) | GUI pass that copies the lamps setting |

## Variations and gotchas

- **Secondary viewports get no lamp shadows and few lamps.** Only the main viewport feeds the clustered list and the shadow atlas; a camera window gets forward lights only, the 1 to 16 most influential (8 at the default 500 x 500 size).
- **Slots are shared.** Part lights and exhaust compete for the same `MaxShadowCasters`; with the slot setting at 4 the mod flags none. The slot setting is 4, 9, 16 or 25 (default 25), so with defaults 21 lamps cast shadows at 512 px.
- **A fixed tile can still shrink.** `ShrinkTileToFit` halves the requested tile while the atlas has no room.
- **The postfix runs whether or not the original drew anything.** `UpdateStaticObjectRenderData` returns early when `ShowGroundMarker` is false or the camera is under the landmark's tangent plane; the mod relies on the postfix still running.
- **The layout lives in several places** (managed table, a compute shader that darkens the sky ambient underground, a generated layout): keep them in sync.

## See also

- [Overview](overview.md)
- [Approach 1](add-dynamic-lights-to-the-world-1.md), [approach 3](add-dynamic-lights-to-the-world-3.md)
- [Garage spot lights submitted per viewport](../../../../mods/ksastructures/features/garage-spot-lights.md)
- [Make static objects cast sun and lamp shadows with invisible casters](../../meshes/add-invisible-shadow-casters/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
