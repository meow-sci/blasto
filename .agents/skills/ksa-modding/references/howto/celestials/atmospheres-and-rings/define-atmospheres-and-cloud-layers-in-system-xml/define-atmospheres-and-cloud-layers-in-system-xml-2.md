---
type: How-To
title: Templated decks and laminae with absolute asset paths
description: Add a Clouds block with one templated deck per giant planet and thin laminae for haze, pointing textures at absolute paths in a mod folder and deriving light distance and flow speed from layer geometry and measured winds
category: celestials
group: atmospheres-and-rings
topic: define-atmospheres-and-cloud-layers-in-system-xml
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/realatmospheres/features/cloud-layers-in-system-xml]
techniques: [xml-patch]
hooks: [starmap StarMapBeforeMain]
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types:
  - KSA.CloudsReference
  - KSA.CloudLayerReference
  - KSA.VolumetricCloudReference
  - KSA.RaymarchingReference
  - KSA.TextureReference
  - KSA.Mod
tags: [clouds, volumetric, xml-patch, flowmap, absolute-path]
---
Outcome: volumetric cloud decks and thin haze layers on bodies that had none (Saturn, Uranus, Neptune, Mars cirrus, Titan laminae, Triton wisps), with per-planet masks and flowmaps. Demonstrated by Real Atmospheres ([Cloud layers in system XML](../../../../mods/realatmospheres/features/cloud-layers-in-system-xml.md)). Sub-step of [approach 1](define-atmospheres-and-cloud-layers-in-system-xml-1.md): the `Atmosphere` block already exists.

## Background: how the game does it

`<Clouds>` is a single child of the body template, a **sibling** of `<Atmosphere>`; a second `<Clouds>` inside `<Atmosphere>` is ignored, so extra layers go into the existing block ([current/decomp/KSA/AstronomicalTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AstronomicalTemplate.cs#L55-L60)). Its fields are the camera-altitude band where volumetrics hand over to the 2D shell (`OrbitTransitionStartAltitude`, `OrbitTransitionEndAltitude`), `MaxShadowsAltitude`, `VolumetricsFlickerReductionDistance` and `Layer`s ([current/decomp/KSA/CloudsReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CloudsReference.cs#L8-L21)). Each layer's `VolumetricCloud` names a coverage `Texture`, a `Detail` tile with a selector, a `Color` or a `VolumetricsColorMap`, a `VolumetricsFlowMap` (displacement and loop duration), `Raymarching` (`Step`, `LightDistance`, `LightSamples`) and one or more `CloudType`s (`StartAltitude`, `Height`, `Density`, `NoiseScale`, `EdgeSharpness`, `MultipleScatteringBrightness`, `CloudShape` curves) ([current/decomp/KSA/VolumetricCloudReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricCloudReference.cs#L9-L31), [current/decomp/KSA/CloudTypeReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CloudTypeReference.cs#L8-L30)). A stock layer with the same structure is Venus's in [current/Content/Core/Astronomicals.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Astronomicals.xml#L185-L260).

## Lifecycle and timing

Edits run at `startup:starmap-before-main` on a copy of the XML; the layers load with their bodies at `startup:asset-load`, and the textures load as ordinary texture assets. Two of the mod's edits depend on the shader patches applied just before in the same method, so XML and GLSL are tuned as one unit. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Write one deck template with placeholders** (`{BODY}`, `{ASSETS}`, `{BASE}`, `{HEIGHT}`, density, flow) and insert it right after the body's `</Atmosphere>`. Per-planet mask and flowmap, colour sampled from the planet's own diffuse cubemap (`VolumetricsColorMap`), two cloud types for vertical variety:

```csharp
// Payloads.cs L1100-L1137
    public const string GiantCloudsTemplate =
"""

        <!-- Real Atmospheres: Jupiter-style volumetric clouds (banding mask + diffuse colour) -->
        <Clouds>
            <!-- Camera-altitude band over which volumetrics fade into the 2D billboard.
                 Stock Jupiter fades between 7.2% and 12.9% of its radius; fading closer in
                 makes the swap obvious, so each giant uses those fractions of its own. -->
            <OrbitTransitionStartAltitude Km="{TRANSSTART}" />
            <OrbitTransitionEndAltitude Km="{TRANSEND}" />
            <MaxShadowsAltitude Km="{TRANSEND}" />
            <VolumetricsFlickerReductionDistance Km="{FLICKER}" />
            <Layer Id="{BODY}Clouds">
                <RotationSpeed X="0" Y="0" Z="0" />
                <VolumetricCloud>
                    <Texture Id="{BODY}CloudsMask" Path="{ASSETS}\{BODY}CloudsMask.dds" Category="Terrain">
                        <IsVirtual>false</IsVirtual>
                        <Manifest><MaxSize>0</MaxSize><MipMaps>false</MipMaps></Manifest>
                    </Texture>
                    <Detail>
                        <DetailSelector Id="{BODY}CloudsMask"/>
                        <Texture Path="{ASSETS}\GiantCloudDetail.dds" Category="Terrain">
                            <IsVirtual>false</IsVirtual>
                            <Manifest><MaxSize>0</MaxSize><MipMaps>false</MipMaps></Manifest>
                        </Texture>
                        <Size Km="40000"/>
                    </Detail>
                    <Color R="1" G="1" B="1" />
                    <VolumetricsFlowMap>
                        {VOLFLOWTEX}
                        <Displacement Km="{DISP}"/> <!-- per-planet, calibrated to real peak zonal winds -->
                        <LoopDuration Hours="{LOOP}"/>
                    </VolumetricsFlowMap>
                    <VolumetricsColorMap Id="{BODY}_Diffuse"/>
                    <Raymarching>
                        <Step Scale="0.00275">
                            <Size M="2000" />
                            <MaxSize M="30000" />
```

2. **Use absolute (rooted) texture paths.** `Mod.GetPath` is `Path.Combine(DirectoryPath, localPath)`, so a rooted path passes through and Core XML can point at a texture in a mod folder ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L389-L392)). The mod substitutes `{ASSETS}` with its own `assets` folder. Gate each insertion on the files existing and log instead of inserting a layer that would fail to load.

3. **Derive flow speed from the wind.** Velocity is `peak flow x displacement / loop duration`; pin the displacement near a ratio to the noise scale (700 km here) and let the loop carry each planet's real peak jet (Saturn and Neptune about 450 m/s, Uranus about 200). Format every number with `CultureInfo.InvariantCulture`: a decimal-comma culture wrote `0,389` into the XML, the whole file failed to load and the game could not start.

```csharp
// ShadowBuilder.cs L199-L226
bool hasFlow = File.Exists(Path.Combine(assetsDir, $"{body}Flowmap.dds"));
// Velocity = flow peak x Displacement / LoopDuration, so the pair can move
// together at constant wind. What matters visually is displacement against the
// cloud noise scale: Jupiter advects ~770 km with 73 km noise, about 10x, while
// ours had run to 146x on Uranus, which decorrelates the two advection phases
// and combs the deck. Displacement is pinned near that ratio and the loop
// carries each planet's real peak jet instead (Saturn and Neptune ~450 m/s,
// Uranus ~200). The flowmaps are normalised to +-0.45, doubled by the shader.
const double flowPeak = 0.9;
const double dispKm = 700.0;
double peakJetMs = body switch
{
    "Saturn"  => 450.0,
    "Uranus"  => 200.0,
    "Neptune" => 450.0,
    _         => 300.0
};
double loopHours = flowPeak * dispKm * 1000.0 / peakJetMs / 3600.0;
// The 2D map is not noise: it may travel much further before its loop restarts, and has to for its motion
// to show (Payloads.Giant2DFlowDisplacementKm). Same wind, a longer loop.
double loop2dHours = flowPeak * Payloads.Giant2DFlowDisplacementKm * 1000.0 / peakJetMs / 3600.0;
// Every number here goes into the XML, which the game reads in the invariant format.
// Under a decimal-comma culture (German, French, Spanish and most of Europe) the loop
// came out as 0,389, and the file holding it failed to load whole: Astronomicals.xml
// took the Sol template with it, and the game could not start.
var xml = CultureInfo.InvariantCulture;
string disp = dispKm.ToString("0", xml);
string loop = loopHours.ToString("0.###", xml);
```

4. **Derive light distance from the geometry.** The longest path a sunbeam can travel inside a layer is `sqrt((R + top)^2 - (R + bottom)^2)`, with `bottom` and `top` taken from its cloud types and `R` from the body's `<MeanRadius>`; more light distance changes nothing beyond that, so cap it and spend the samples where they matter:

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

5. **Spin a layer with the body.** `RotationSpeed Z` is in m/s at the layer's base; the engine's Z is opposite to an ordinary spin about +Z, so retrograde bodies flip the sign:

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

6. **Keep per-body bands in one table** (transition start and end, shadow and flicker distances, samples per layer) and apply it last over whatever the file holds:

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

## Complete example

| File | Role |
|---|---|
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L177-L259) | `InsertGiantClouds`: template fill, flow-speed calibration |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L863-L969) | `PatchCloudLighting`: band, light march, densities, step caps |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1091-L1237) | Giant deck, 2D flowmap and volumetric flowmap templates |

## Variations and gotchas

- **Values above the editor's range load fine.** The comment on Jupiter's dense deck notes a `MultipleScatteringBrightness` of 1.7 where the cloud editor offers 0 to 1; the render data copies the loaded value without clamping ([current/decomp/KSA.Atmosphere.Rendering/CloudLayerRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Atmosphere.Rendering/CloudLayerRenderData.cs#L609)).
- **Jupiter's rescale is all-or-nothing:** heights and densities must move together to keep optical depth, so one drifted anchor skips the rescale.
- **Bodies and layers are found by `Id` strings.** A different system, or another mod renaming layers, skips the edit with a log line.
- **Absolute paths are machine-specific.** The copy is regenerated each launch, so moving the install is harmless, but the edited copy must never be shipped.

## See also

- [Overview](overview.md); [approach 1](define-atmospheres-and-cloud-layers-in-system-xml-1.md); [approach 3](define-atmospheres-and-cloud-layers-in-system-xml-3.md)
- [Cloud layers in system XML](../../../../mods/realatmospheres/features/cloud-layers-in-system-xml.md)
- [Redirect game XML loading](../../../assets/xml-patching/redirect-game-xml-loading/overview.md)
