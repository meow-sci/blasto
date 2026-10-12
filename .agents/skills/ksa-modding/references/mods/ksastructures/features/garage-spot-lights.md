---
type: Mod Feature
title: Garage spot lights submitted per viewport
description: Ceiling lamps made of KSA's own spot lights, resubmitted every frame for every viewport from the static-object render-data postfix, with the nearest lamps flagged to fill the player's light-shadow slots
mod: ksastructures
category: rendering
techniques: [harmony-postfix, starmap-lifecycle, dynamic-lights]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-postfix KSA.LocationReference.UpdateStaticObjectRenderData
  - starmap StarMapAfterGui
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:render]
game_types:
  - KSA.Rendering.Lighting.Light
  - KSA.Rendering.Lighting.ELightFlags
  - KSA.Rendering.Lighting.ILightSystem
  - KSA.Rendering.Lighting.ClusteredLightSystem
  - KSA.Rendering.Lighting.LightSystemShadowMap
  - KSA.LocationReference
  - KSA.Program
  - KSA.IViewport
  - KSA.GameSettings
tags: [lighting, spot-lights, shadows, clustered-lighting, viewports, harmony]
---

The office's underground garage and its deep spiral ramp are lit by 90 warm ceiling lamps and 14
weak fill lights. The lamps nearest the camera cast soft shadows past pillars and cars, and the
player can turn them off in the mod's settings.

Mechanism in one line: every frame, for every visible viewport, a Harmony postfix on
`LocationReference.UpdateStaticObjectRenderData` builds `Light.CreateSpotLight` structs in that
viewport's camera-relative frame and hands each one to `Program.LightSystem.CreateLightInstance`.
The engine's light system is cleared every frame, so nothing is registered once and kept.

## How it works

1. **Hook.** `Office.Place` runs at `[StarMapAllModsLoaded]` and patches
   `LocationReference.UpdateStaticObjectRenderData` with the postfix `AfterRenderData`
   ([src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L75-L86)). For the tower landmark the postfix rebuilds the
   origin and the up/east/north axes the engine itself computes (camera `EclToEgo` of the landmark's
   surface point, axes from `GetAxesCcf`) and calls `Underground.Light(viewport, origin, up, east, north)`
   before the engine's visibility test, so the lamps are submitted even when the engine does not draw
   the tower ([src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L122-L140)).
2. **Lamp table.** 19 garage lamps (east, north, range, intensity) 0.5 m below the building's ground level, just
   under the garage ceiling, plus the 71
   generated deep-ramp lamps from `OfficeDeep.Lamps`, concatenated into one array because they share
   the shadow slots; 14 fill lights sit along the tunnel walls with no fixture and no shadow
   ([src/KSAStructures/Ksa/OfficeUnderground.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeUnderground.cs#L46-L97)). The 19 garage entries are restated in
   the dark pass's shader ([src/KSAStructures/Shaders/KSAStructuresDark.comp](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Shaders/KSAStructuresDark.comp#L70-L92)); the deep
   ones are generated into [src/KSAStructures/Sim/OfficeDeepLayout.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/OfficeDeepLayout.cs#L6-L80).
3. **Gate.** Nothing is submitted unless `LampsOn` is true and the building origin is within 600 m of
   that viewport's camera (`DarkWithinMetres`).
4. **Which lamps cast.** Only for the main viewport (`viewport.GetCamera()` is
   `Program.GetMainCamera()`), with the bridge's `Shadows` flag on and the camera within 260 m: the
   squared distance of every lamp to the camera is sorted with `Array.Sort(Nearness, Nearest)` and the
   first `Program.LightSystem.MaxShadowCasters - 4` lamps are flagged. Four slots are left for part
   lights and exhaust ([src/KSAStructures/Ksa/OfficeUnderground.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeUnderground.cs#L42-L44)).
5. **Submit.** Each lamp is `Light.CreateSpotLight(position, -up, range, 1.3 rad, 1.05 rad, warm,
   intensity * LampScale, flags)` with `ELightFlags.CastsShadows | ELightFlags.SoftShadows` for the
   chosen ones and `ShadowTileSize = 512`; fills are unflagged spot lights. Everything goes to
   `Program.LightSystem.CreateLightInstance(light, viewport)` inside a `try { } catch { }`.
6. **Setting.** `Underground.LampsOn` is copied from `Settings.OfficeLamps` in the GUI pass
   ([src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L152-L172),
   [src/KSAStructures/Sim/Settings.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/Settings.cs#L12-L13)); the dev bridge's `office_light` command tunes
   `Shadows`, `LampScale` and `Fill` live ([src/KSAStructures/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Bridge.cs#L293-L300)).

What the engine does with a submitted light:

| Step | Engine behaviour (build 2026.10.10.5554) |
|---|---|
| `CreateLightInstance` | Ignored when `Range` is zero or the viewport's `LightMode` is `None`; appended to that viewport's forward list (cap 256); appended to the clustered list (cap `MaxLights` = 1024) only when the viewport is the main one and not in `Forward` mode[^ksa-clusteredlights] |
| `WriteForwardLights` | Per visible viewport, keeps the top 8 lights by `Intensity * Range^2 / distance^2` (a `Forward`-mode viewport keeps 1-16 by its pixel count)[^ksa-clusteredlights] |
| `UpdateRenderResources` | Sorts the clustered list by the same influence, times 1.5 for `CastsShadows`, then asks the shadow atlas for a tile in that order[^ksa-clusteredlights] |
| `LightSystemShadowMap.RegisterSpotLight` | Returns no slot when the light is not flagged, when `MaxShadowCasters` tiles are taken, or when the tile does not fit (it is halved until it fits, down to 16 px)[^ksa-shadowmap] |
| `Light.CreateLightData` | Shadowed only with `CastsShadows` and a slot, so a flagged lamp without a slot is drawn unshadowed; `SoftShadows` is dropped when the player's hard-shadows setting is on[^ksa-light] |
| `ShadowTileSize` | Non-zero overrides `ComputeShadowTileSize`, which otherwise scales the tile with range over distance between a quarter of and the full default tile (atlas size divided by the rounded-up square root of the slot count, rounded down to 16)[^ksa-clusteredlights][^ksa-shadowmap] |
| `MaxShadowCasters` | `min(GameSettings.GetLightSystemShadowSlots(), 64)`, or 0 with light shadows off; the slot setting is 4, 9, 16 or 25 (default 25) and the atlas defaults to 4096 px[^ksa-clusteredlights][^ksa-gamesettings] |

So with default settings 21 lamps cast shadows at 512 px each. The mod picks them itself because
the engine's own pick is by influence across every light in view and changes as other lights come and
go (the comment at [src/KSAStructures/Ksa/OfficeUnderground.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeUnderground.cs#L155-L157)).

## Lifecycle and timing

- **Install:** `[StarMapAllModsLoaded]` (`Office.Place`, from
  [src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L42-L47)).
- **Lights cleared:** `Program.OnFrame` calls `LightSystem.ClearLights()` and `LightSystem.OnFrame()`
  after the GUI pass and the controller update, just before `OnFrameCelestials` and the render
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2316-L2324)). `ClearLights` empties the clustered list and every
  viewport's forward list ([current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs#L923-L934)),
  so a light created in `StarMapAfterGui` would be gone before it is used. Lights have to be created
  again every frame, after that call.
- **Lights submitted (this hook):** `Program.Render` calls `UpdateRenderingResources` before recording
  commands ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2467-L2478)). It loops over every visible viewport and
  calls `Universe.UpdateRenderData(viewport)` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4338-L4397)), which
  reaches `CelestialSystem.UpdateRenderData` -> `Celestial.UpdateRenderData` -> each location's
  `UpdateStaticObjectRenderData` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1877-L1880),
  [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L552-L563), [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L1594-L1604)).
  The engine's explosion lights are submitted from the same loop
  (`ExplosionFlashSystem.SubmitLights(viewport2)`, L4395). This is the `frame:render` lifecycle
  window: inside `Program.Render`, after `OnPreRender` and `LightSystem.OnFrame`, on the main thread,
  before `RenderGame` records any command.
- **Lights consumed:** straight after the loop, `WriteForwardLights` per visible viewport and
  `LightSystem.UpdateRenderResources(RenderedViewport)` sort the lights and assign shadow tiles
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4400-L4409)); `RenderGame` then draws the light shadow atlas
  (`render:shadows`, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4721-L4728)) and the scene samples the result.
- **Why this hook:** it runs once per visible viewport per frame, with the viewport handed in, between
  the clear and the consumption, right where the engine works out the landmark's position and axes
  for that viewport. Light positions
  are camera-relative (the engine measures influence by `Position.LengthSquared()`), and the origin
  the postfix computes is in that viewport's Ego frame, so each viewport gets lamps placed for its own
  camera.
- **Settings copy:** `[StarMapAfterGui]`, skipped while the UI is hidden (F2); the last value stays in
  force.

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeUnderground.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeUnderground.cs#L137-L202) | `Underground.Light`: gate, nearest-lamp choice, spot and fill submission |
| [src/KSAStructures/Ksa/OfficeUnderground.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeUnderground.cs#L46-L97) | Lamp and fill tables, merged with the generated deep lamps |
| [src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L117-L160) | `AfterRenderData` postfix that calls `Underground.Light` per viewport |
| [src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L75-L86) | Patch installation at `StarMapAllModsLoaded` |
| [src/KSAStructures/Sim/OfficeDeepLayout.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/OfficeDeepLayout.cs#L6-L80) | Generated deep-ramp lamps (71) |
| [src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L152-L172) | GUI pass that copies `Settings.OfficeLamps` into `Underground.LampsOn` |

## Snippets

```csharp
// src/KSAStructures/Ksa/OfficeUnderground.cs L151-L189
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
```

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

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `Light.CreateSpotLight` | [current/decomp/KSA.Rendering.Lighting/Light.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/Light.cs#L54-L79) | Builds a spot light; swaps inner/outer if reversed, clamps the outer angle below 90 degrees, near plane = 1% of range |
| `Light.ShadowTileSize`, `Light.CreateLightData` | [current/decomp/KSA.Rendering.Lighting/Light.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/Light.cs#L81-L106) | Fixed shadow tile; what the shader is told (shadow only with a slot) |
| `ELightFlags.CastsShadows`, `ELightFlags.SoftShadows` | [current/decomp/KSA.Rendering.Lighting/ELightFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/ELightFlags.cs) | Request a shadow tile and soft filtering |
| `ILightSystem.CreateLightInstance`, `ILightSystem.MaxShadowCasters` | [current/decomp/KSA.Rendering.Lighting/ILightSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/ILightSystem.cs#L14-L28) | Submission and the slot count read every frame |
| `ClusteredLightSystem.CreateLightInstance` / `ClearLights` / `WriteForwardLights` | [current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs#L897-L981) | Per-viewport forward lists and the main view's clustered list |
| `ClusteredLightSystem.PackLightsAndShadows` | [current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs#L1125-L1162) | Influence-ordered shadow slot assignment |
| `LightSystemShadowMap.RegisterSpotLight` | [current/decomp/KSA.Rendering.Lighting/LightSystemShadowMap.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/LightSystemShadowMap.cs#L292-L303) | Slot and atlas-fit refusal |
| `LocationReference.UpdateStaticObjectRenderData` | [current/decomp/KSA/LocationReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LocationReference.cs#L156-L180) | Patched; per-viewport, per-location render-data call |
| `Program.LightSystem` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L147) | The light system singleton (a `ClusteredLightSystem`) |
| `Program.GetMainCamera` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L631-L634) | Main-view test in `Underground.Light` |
| `GameSettings.GetLightSystemShadowSlots` | [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L3113-L3116) | Player's light-shadow slot setting behind `MaxShadowCasters` |

## Pitfalls and version notes

- **Secondary viewports get no lamp shadows and few lamps.** Only the main viewport feeds the
  clustered list and the shadow atlas; a camera window gets forward lights only, the 8 most
  influential (`WriteForwardLights`), so most of the 104 lights never reach it.[^ksa-clusteredlights]
- **Slots are shared.** Part lights and exhaust lights compete for the same `MaxShadowCasters`; the
  engine assigns tiles by influence (flagged lights x1.5), so a flagged lamp that loses the race is
  drawn without a shadow rather than dropped. With the slot setting at 4 (`VeryLow`) the mod flags none.
- **A fixed 512 px tile can still shrink.** `ShrinkTileToFit` halves the requested tile while the
  atlas has no room for it.[^ksa-shadowmap]
- **The postfix runs whether or not the original drew anything.** `UpdateStaticObjectRenderData`
  returns early when `ShowGroundMarker` is false (`LandmarkReference.ShowGroundMarker` is
  `IsLaunchPad`) or the camera is under the landmark's tangent plane; the mod relies on the postfix
  still running.[^ksa-locationreference][^ksa-landmarkreference]
- **The lamp layout lives in three places**: `OfficeUnderground.cs`, the shader's `Lamps` array, and
  the generated `OfficeDeepLayout.cs`; moving a garage lamp means editing the first two by hand.
- **Version:** the mod is built against KSA 2026.10.7.5541; this page was checked against
  2026.10.10.5554. The decompiled diff between the two builds does not touch any member used here.

## See also

- [Invisible shadow casters](./invisible-shadow-casters.md): what the lamps' shadows are cast by
- [Underground dark compute pass](./underground-dark-compute-pass.md): reads the light result these lamps produce
- [Office landmarks as static objects](./office-landmarks-static-objects.md): the landmarks whose render-data call is patched
- [Generated deep ramp](./generated-deep-ramp.md): generates `OfficeDeep.Lamps`
- [Settings menu](./settings-menu.md): the "Office garage lamps" toggle
- [Dev file bridge and MCP](./dev-file-bridge-and-mcp.md): the `office_light` command
- [Submit per-viewport lights from an UpdateStaticObjectRenderData postfix with a shadow-caster budget](../../../howto/rendering/lights-and-particles/add-dynamic-lights-to-the-world/add-dynamic-lights-to-the-world-2.md) - how-to article demonstrating this feature

[^ksa-clusteredlights]: KSA.Rendering.Lighting.ClusteredLightSystem (decompiled)
[^ksa-shadowmap]: KSA.Rendering.Lighting.LightSystemShadowMap (decompiled)
[^ksa-light]: KSA.Rendering.Lighting.Light (decompiled)
[^ksa-gamesettings]: KSA.GameSettings (decompiled)
[^ksa-locationreference]: KSA.LocationReference (decompiled)
[^ksa-landmarkreference]: KSA.LandmarkReference (decompiled)
