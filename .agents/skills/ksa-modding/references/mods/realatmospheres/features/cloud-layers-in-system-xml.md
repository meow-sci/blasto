---
type: Mod Feature
title: Cloud layers in the system XML
description: Inserts templated volumetric cloud decks with masks and flowmaps for the giants, rebuilds Venus's and Jupiter's decks, adds Mars cirrus and Titan hazes, and derives each layer's light-march distance from its geometry
mod: realatmospheres
category: celestials
techniques: [xml-patch, xml-asset]
hooks: [starmap StarMapBeforeMain]
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types:
  - KSA.CloudsReference
  - KSA.CloudLayerReference
  - KSA.VolumetricCloudReference
  - KSA.RaymarchingReference
  - KSA.AstronomicalTemplate
  - KSA.Mod
tags: [celestials, clouds, volumetric-clouds, flowmap, xml, textures, giants, venus, titan, mars]
---

KSA's clouds are data: a body's `<Clouds>` element (a sibling of `<Atmosphere>`) holds the
camera-altitude band where volumetric clouds hand over to a 2D shell, and one or more `<Layer>`s, each
with a `<VolumetricCloud>` (coverage texture, detail tile, flowmap, colour map, raymarch steps and light
march, cloud types with altitude, height, density and shape curve) and a `<TwoDimensionalCloud>`
([current/decomp/KSA/AstronomicalTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AstronomicalTemplate.cs#L56-L60),
[current/decomp/KSA/CloudsReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CloudsReference.cs#L8-L21),
[current/decomp/KSA/VolumetricCloudReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricCloudReference.cs#L9-L31)).[^ksa-cloudsreference]
Real Atmospheres edits and adds layers in the copied XML, pointing new textures at absolute paths in
its own `assets/` folder.[^ra-shadowbuilder]

## How it works

| Change | Where | Code |
|---|---|---|
| Saturn, Uranus, Neptune get a Jupiter-style volumetric deck: a two-type `GiantBands`/`GiantBandEdge` layer from a template with `{BODY}`, `{ASSETS}`, `{BASE}`, `{HEIGHT}`, density and flow placeholders, inserted after the body's `</Atmosphere>`; colour from the planet's own diffuse cubemap via `VolumetricsColorMap` | `Astronomicals.xml` (Saturn, Uranus), system templates (Neptune) | [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L177-L259), [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1091-L1237) |
| Per-planet flowmaps for both the 2D shell and the volumetric deck; loop duration computed so peak flow equals each planet's measured peak jet (Saturn and Neptune 450 m/s, Uranus 200 m/s) | same | [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L199-L226) |
| Higher-resolution diffuse maps swapped in by path | same | [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L261-L271) |
| Jupiter's two decks rescaled from 220-770 km showcase heights to -1..111 km, densities scaled inversely, noise scale and raymarch steps shrunk with them, flowmap speeds calibrated to ~150 m/s; all-or-nothing inside the two layers' span; mask with grey and white swapped | `Astronomicals.xml` | [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L510-L574), [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1294-L1460) |
| Venus's five stock cloud types replaced by one solid 48-72 km deck with short crowns, the detail tile swapped for a coverage-lifted copy | `Astronomicals.xml` | [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L397-L432), [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L178-L240) |
| Venus's `RotationSpeed` set so the layer laps in 98 h with Venus's retrograde spin, computed from the file's own spin period, radius and deck base | `Astronomicals.xml` | [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L434-L477), [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L201-L224) |
| Mars water-ice cirrus layer inserted after `MarsDustStorms` | `Astronomicals.xml` | [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L479-L508), [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1854-L1920) |
| Titan: methane-cloud coverage swapped for polar-cap masks and white colour; detached haze laminae inserted into the existing `<Clouds>` block after the placeholder layer; transition band raised | system templates | [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L273-L344), [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1462-L1688) |
| Triton: a new `<Clouds>` block of N2-ice wisps, only if both masks exist | system templates | [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L109-L117), [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1787-L1852) |
| Every cloudy body: volumetric-to-2D band, shadow and flicker distances, light distance and light samples per layer, a few densities and maximum step sizes, applied last over whatever the file holds | all | [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L863-L969), [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1017-L1089) |

Details that make this reusable:

- **Absolute asset paths.** `{ASSETS}` is replaced with the mod's `assets` folder; texture `Path`s
  resolve through `Mod.GetPath`, a `Path.Combine` that passes rooted paths through, so a Core layer can
  read a texture from another mod's folder ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1854-L1858),
  [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L389-L392)).[^ksa-mod]
- **Asset-gated edits.** Each insertion checks that its textures exist and logs instead of inserting
  a layer that would fail to load, so deleting a texture from `assets/` reverts just that layer.
- **Light distance from geometry.** For each layer the longest path a sunbeam can travel inside it is
  `sqrt((R + top)^2 - (R + bottom)^2)`, with `bottom` and `top` taken from its cloud types and `R`
  from the body's `<MeanRadius>`; the layer's `LightDistance` is set to that, capped at 1000 km, and
  `LightSamples` to a per-layer count ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L905-L937)). Beyond that
  path more light distance changes nothing, so the samples are spent where they matter.
- **`Clouds` must be a sibling.** A second `<Clouds>` inside `<Atmosphere>` is ignored, so Titan's
  laminae go into the existing block ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1525-L1538)); the schema has
  exactly one `Clouds` element per body template.
- **Numbers in invariant culture.** Every formatted value uses `CultureInfo.InvariantCulture`
  ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L220-L226)).

## Lifecycle and timing

- **`startup:starmap-before-main`.** All XML edits run in `ShadowBuilder.Build`. Two of them depend on
  the shader patches applied just before in the same method: Jupiter's and the other giants' dense
  deck types, and Venus's crown brightness, are only used when the face-lighting shader patch applied
  (`_faceLight`) ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L808-L835),
  [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L565-L571)). XML and GLSL are tuned as one unit.
- **`startup:asset-load`.** The layers load with the bodies; the textures load as ordinary texture
  assets of the Core mod.

## Key code

| Path | Role |
|---|---|
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L177-L259) | `InsertGiantClouds`: template fill, flow-speed calibration |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L863-L969) | `PatchCloudLighting`: band, light march, densities, step caps |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L434-L477) | `PatchVenusCloudTurn` |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1017-L1089) | `CloudLighting`, `CloudMaxSteps`, `CloudTypeDensities` tables |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1091-L1237) | Giant deck, 2D flowmap and volumetric flowmap templates |

## Snippets

```csharp
// ShadowBuilder.cs L916-L931
                double bottom = double.MaxValue, top = double.MinValue;
                foreach (Match type in Regex.Matches(text, @"<CloudType\b.*?</CloudType>", RegexOptions.Singleline))
                {
                    if (DistanceKm(type.Value, "StartAltitude") is double start && DistanceKm(type.Value, "Height") is double height)
                    {
                        bottom = Math.Min(bottom, start);
                        top = Math.Max(top, start + height);
                    }
                }
                if (bottom > top)
                {
                    Log($"WARN: {label}: {body}'s layer {layer} has no cloud types to measure, its light march stays");
                    continue;
                }
                double path = Math.Sqrt((radiusKm + top) * (radiusKm + top) - (radiusKm + bottom) * (radiusKm + bottom));
                double lightKm = Math.Min(Math.Round(path), Payloads.MaxLightDistanceKm);
```

```csharp
// Payloads.cs L216-L224
    /// <summary>The RotationSpeed Z, in m/s at the layer's base, that takes a cloud layer round once in
    /// <paramref name="cloudHours"/> against the stars, the same way as its body spins.</summary>
    public static double CloudTurnSpeed(double cloudHours, double spinDays, bool retrograde, double baseRadiusM)
    {
        double clouds = 2.0 * Math.PI / (cloudHours * 3600.0);
        double ground = spinDays > 0.0 ? 2.0 * Math.PI / (spinDays * 86400.0) : 0.0;
        // about the body's +Z a body's ordinary spin is positive, and the engine's Z is the other way round
        return (retrograde ? 1.0 : -1.0) * (clouds - ground) * baseRadiusM;
    }
```

```csharp
// Payloads.cs L1038-L1050
    public static readonly (string Body, int StartKm, int EndKm, int? ShadowsKm, int? FlickerKm,
        (string Layer, int Samples)[] Layers)[] CloudLighting =
    {
        ("Earth",    1000,  3000,  null,  null, new[] { ("EarthClouds", 10), ("EarthClouds2", 8) }),
        ("Venus",    1080,  1800,  null,  null, new[] { ("VenusPlaceholderClouds", 4) }),
        ("Mars",      670,  2000,  2000,  null, new[] { ("MarsDustStorms", 6), ("MarsCirrus", 4) }),
        ("Jupiter",  7780, 14000, 14000, 15400, new[] { ("JupiterLowerClouds", 10), ("JupiterClouds", 10) }),
        ("Saturn",   6720, 12000, 12000, 13200, new[] { ("SaturnClouds", 10) }),
        ("Uranus",   5450, 10000, 10000, 11000, new[] { ("UranusClouds", 10) }),
        ("Neptune",  5630, 10000, 10000, 11000, new[] { ("NeptuneClouds", 10) }),
        ("Titan",    1470,  2200,  null,  null, new[] { ("TitanPlaceholderClouds", 2), ("TitanLowerHaze", 4), ("TitanDetachedHaze", 4) }),
        ("Triton",    440,   800,  null,  null, new[] { ("TritonClouds", 4) }),
    };
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `AstronomicalTemplate.CloudsReference` (`<Clouds>`) | [current/decomp/KSA/AstronomicalTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AstronomicalTemplate.cs#L56-L60) | One clouds element per body, sibling of `<Atmosphere>` |
| `CloudsReference` band fields, `Layer` | [current/decomp/KSA/CloudsReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CloudsReference.cs#L8-L21) | `OrbitTransitionStart/EndAltitude`, `MaxShadowsAltitude`, `VolumetricsFlickerReductionDistance` |
| `CloudLayerReference` | [current/decomp/KSA/CloudLayerReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CloudLayerReference.cs#L9-L19) | `RotationSpeed`, `VolumetricCloud`, `TwoDimensionalCloud` |
| `VolumetricCloudReference` | [current/decomp/KSA/VolumetricCloudReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricCloudReference.cs#L9-L31) | Texture, detail, colour, raymarching, flow and colour maps, cloud types |
| `RaymarchingReference` | [current/decomp/KSA/RaymarchingReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RaymarchingReference.cs#L7-L14) | `Step`, `LightDistance`, `LightSamples` |
| `Mod.GetPath` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L389-L392) | Rooted texture paths pass through |

## Pitfalls and version notes

- **Values above the editor's range load fine.** The comment on Jupiter's dense deck notes a
  `MultipleScatteringBrightness` of 1.7 where the game's cloud editor offers 0 to 1 ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1416-L1426));
  the render data copies the loaded value to the GPU without clamping it
  ([current/decomp/KSA.Atmosphere.Rendering/CloudLayerRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Atmosphere.Rendering/CloudLayerRenderData.cs#L609)).
- **Jupiter's rescale is all-or-nothing.** Heights and densities have to move together to keep optical
  depth, so one drifted anchor skips the whole rescale ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L532-L561)).
- **Bodies are found by `Id` strings and `<Layer Id>`s.** A different system, or another mod renaming
  layers, skips the edit with a log line.
- **Absolute paths are machine-specific.** The copy is regenerated each launch, so moving the install
  is harmless, but the shadow XML must never be shipped.

## See also

- [Per-body atmosphere optics in the system XML](atmosphere-optics-in-system-xml.md)
- [Cloud face lighting](cloud-face-lighting.md)
- [Volumetric cloud map flow and faint-cloud lift](volumetric-cloud-map-flow-and-lift.md)
- [Texture and phase asset scripts](texture-and-phase-asset-scripts.md)
- [Reach another mod's or Core's files with absolute paths and Id-only references](../../../howto/assets/asset-xml/resolve-asset-ids-and-mod-relative-paths/resolve-asset-ids-and-mod-relative-paths-2.md) - how-to article demonstrating this feature
- [Templated decks and laminae with absolute asset paths](../../../howto/celestials/atmospheres-and-rings/define-atmospheres-and-cloud-layers-in-system-xml/define-atmospheres-and-cloud-layers-in-system-xml-2.md) - how-to article demonstrating this feature
- [Python asset scripts with encodings matched to the shader that reads them](../../../howto/celestials/atmospheres-and-rings/define-atmospheres-and-cloud-layers-in-system-xml/define-atmospheres-and-cloud-layers-in-system-xml-3.md) - how-to article demonstrating this feature

[^ksa-cloudsreference]: KSA.CloudsReference (decompiled, build 2026.10.10.5554)
[^ra-shadowbuilder]: ShadowBuilder.cs
[^ksa-mod]: KSA.Mod (decompiled, build 2026.10.10.5554)
