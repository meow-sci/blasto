---
type: Mod Feature
title: Per-body atmosphere optics in the system XML
description: Rewrites Rayleigh, Mie and ozone values for ten bodies from generated physics, promotes Pluto and Triton to AtmosphericBody, and repurposes the ozone slot and scale heights as absorbers and shader keys
mod: realatmospheres
category: celestials
techniques: [xml-patch, glsl-shader]
hooks: [starmap StarMapBeforeMain]
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types:
  - KSA.VisualAtmosphereReference
  - KSA.AtmosphereReference
  - KSA.AtmosphericBodyTemplate
  - KSA.PlanetaryBodyTemplate
  - KSA.MinorBodyTemplate
  - KSA.SystemTemplate
  - KSA.AssetBundle
  - KSA.AtmosphereRenderer
tags:
  - celestials
  - atmosphere
  - rayleigh
  - mie
  - ozone
  - xml
  - system-templates
  - pluto
  - triton
  - titan
  - venus
  - mars
---

KSA describes a body's visible atmosphere with three terms in `<Atmosphere><Visual>`: Rayleigh
scattering (per-channel coefficients and a scale height), Mie scattering (coefficients, scale height,
a per-channel phase asymmetry and an absorption multiplier) and an "ozone" tent (per-channel
absorption, centre altitude and half-width) ([current/decomp/KSA/VisualAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VisualAtmosphereReference.cs#L8-L27)).
The renderer copies those numbers into a uniform buffer per body
([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1136-L1164)).[^ksa-atmosphererenderer]
Real Atmospheres replaces them with values derived from each body's composition and measurements,
and adds atmospheres to two bodies that have none.[^ra-payloads]

## How it works

### What changes, per body

| Body | File | Change | Payload |
|---|---|---|---|
| Earth | `Astronomicals.xml` | Rayleigh scale height 8 -> 8.43 km (the US Standard Atmosphere 1976 column, and the shader's key for its density table); ozone tent re-centred at 22.3 km with generated per-channel cross-sections | [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L67-L103) |
| Mars | `Astronomicals.xml` | CO2 Rayleigh from Pathfinder/Phoenix (generated), dust Mie with asymmetry `X=-1` as the sentinel for the dust phase function, terrain Lambertian 0.2 -> 0.65 | [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L18-L52) |
| Venus | `Astronomicals.xml` | Dense CO2 Rayleigh (scale height 15.9 km = shader key), lower-haze Mie (12.5 km key), the "unknown UV absorber" moved into the ozone slot as a blue-only tent, measured cloud-top colours | [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L105-L176) |
| Jupiter, Saturn, Uranus | `Astronomicals.xml` | H2/He Rayleigh at 1 bar (generated), stratospheric haze Mie (generated, Jupiter and Saturn), chromophore or methane absorption in the ozone slot, physical scale height matched to visual | [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L758-L896), [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1239-L1292) |
| Neptune | system templates | Same ice-giant recipe, inline body | [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L898-L949) |
| Titan | system templates | "Golden shroud": Rayleigh carries the haze's scattering colour, Mie (absorption x100) its absorption, both keyed to measured shader tables | [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1488-L1523) |
| Pluto | system templates | `PlanetaryBody` becomes `AtmosphericBody` with an N2 + tholin haze atmosphere; haze phase is a two-part function flagged by `Z="-2"` | [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1690-L1742) |
| Triton | system templates | `MinorBody` becomes `AtmosphericBody` with mesh and colour, N2 + haze atmosphere, optional cloud layer | [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1744-L1779) |

Every body in the flight tables also gets its `<Physical>` block set to measured 0 km values (see
[Measured flight atmospheres](measured-flight-atmospheres.md)).

### Techniques worth copying

- **Generated constants spliced into XML.** The XML payloads are C# interpolated raw strings that
  pull attribute text such as `R="0.000223272" G="..." B="..."` from the generated
  `AtmosphereTables` class, so a regeneration updates the XML and the shader tables together
  ([AtmosphereTables.Generated.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.Generated.cs#L270-L336)).
- **The ozone slot as a general absorber.** The tent `saturate(1 - |h - Altitude| / Extent)` times
  per-channel coefficients is the only per-channel pure absorber in the model (Mie absorption is a
  grey multiplier), so it carries Venus's UV absorber, Jupiter's chromophore and the ice giants'
  methane ([current/Content/Core/Shaders/Atmosphere/Atmosphere.comp](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/Atmosphere.comp#L125-L127)).
- **Scale heights as keys.** The shaders recognise a body by its radius and the exact scale height
  written here (for example Earth's 8430 m), and only then use a measured density table; a body whose
  atmosphere someone else replaces falls back to the plain exponential. See
  [Measured density tables in the atmosphere shaders](measured-density-tables-in-shaders.md).
- **Sentinels in the phase asymmetry.** `X < -0.5` selects the Mars dust phase and `Z < -1.5` the
  two-part Pluto haze phase in the patched `MiePhaseFunction`; see
  [Mie phase sentinel dispatch](mie-phase-sentinel-dispatch.md).
- **Promoting a body.** `UpgradeBody` renames the open and close tags to `AtmosphericBody`, optionally
  inserts a minimal kit (`<MeshCollection Id="Default"/>` and `<Color>` for Triton), and appends the
  `<Atmosphere>` block before the closing tag ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L576-L602)).
  `AtmosphericBody` deserializes to `AtmosphericBodyTemplate`, which extends `PlanetaryBodyTemplate`,
  so Pluto's existing children stay valid; Triton was a `MinorBodyTemplate` (a different branch), hence
  the extra kit ([current/decomp/KSA/SystemTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SystemTemplate.cs#L24-L29),
  [current/decomp/KSA/AtmosphericBodyTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphericBodyTemplate.cs#L5)).
  The atmosphere renderer keys its per-planet slots by `AtmosphericBody`
  ([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L395-L405)),
  so the element type, not just the `<Atmosphere>` child, decides whether a sky is drawn.
- **Validity.** The new blocks include an `<Ozone>` with zero coefficients and `<StartHeight>`
  because `VisualAtmosphereReference.IsValid` requires both to be valid
  ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1690-L1699)).

## Lifecycle and timing

- **`startup:starmap-before-main`.** The edits are applied to the shared shadow copy inside
  `ShadowBuilder.Build` ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L90-L159)).
- **`startup:asset-load`.** The game deserializes the edited files through the
  [Core XML entry redirect](core-xml-entry-redirect.md): `Astronomicals.xml` as an asset bundle, the
  selected Sol template in `Mod.LoadSystems`. From then on the values are ordinary game data: the
  renderer reads them every frame in `FillPlanetAtmosphereData`, and the in-game atmosphere editor
  edits them as if they were stock.

## Key code

| Path | Role |
|---|---|
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L90-L159) | Which edit runs on which file |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L348-L395) | Value pairs and the `<Physical>` rewrite |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L576-L602) | `UpgradeBody` |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1690-L1779) | Pluto and Triton atmospheres and Triton's body kit |
| [AtmosphereTables.Generated.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.Generated.cs#L270-L336) | Generated attribute strings |

## Snippets

```csharp
// Payloads.cs L1710-L1742
    public const string PlutoAtmosphere =
$"""
<Atmosphere>
            <Visual>
                <RayleighScattering>
                    <!-- Real Atmospheres: Pluto's nitrogen as New Horizons measured it (shader tables): 11.5 microbar and 40 K at the surface, ~110 K at 25 km -->
                    <Coefficients {AtmosphereTables.PlutoRayleighCoefficients} />
                    <ScaleHeight Km="{AtmosphereTables.PlutoRayleighScaleHeightKm}" />
                </RayleighScattering>
                <MieScattering>
                    <!-- Real Atmospheres: Pluto's tholin haze as New Horizons measured it (shader tables): extinction following the nitrogen (Alice), strength and phase function fitted to its limb brightness from 20 to 167 degrees of phase (LORRI), blue from MVIC -->
                    <Coefficients {AtmosphereTables.PlutoMieCoefficients} />
                    <ScaleHeight Km="{AtmosphereTables.PlutoMieScaleHeightKm}" />
                    <!-- Z below -1.5: a forward Henyey-Greenstein lobe of asymmetry X carrying the share Y, the rest isotropic -->
                    <PhaseFunctionAsymmetry {AtmosphereTables.PlutoMieAsymmetry} />
                    <AbsorptionMultiplier Value="1.5" />
                </MieScattering>
                <Ozone>
                    <!-- No ozone on Pluto. Haze bands are armed by body radius (RaBodyHasHaze in
                         AtmosphereData.glsl), not by this slot, so it stays clean. -->
                    <Coefficients R="0" G="0" B="0" />
                    <Altitude Km="0" />
                    <Extent Km="0" />
                </Ozone>
                <StartHeight Km="0" />
            </Visual>
            <Physical>
                <SeaLevelPressure Atm="0.00000987"/>
                <SeaLevelDensity KgPerM3="0.0000842"/>
                <ScaleHeight Km="19.2"/>
            </Physical>
        </Atmosphere>
""";
```

```csharp
// ShadowBuilder.cs L581-L602
    private static string UpgradeBody(string s, string name, string openTag, string closeTag,
        string? extraKit, string atmosphere)
    {
        if (s.Contains($"<AtmosphericBody Id=\"{name}\""))
            return s; // already upgraded

        int i = s.IndexOf(openTag, StringComparison.Ordinal);
        int j = i < 0 ? -1 : s.IndexOf(closeTag, i, StringComparison.Ordinal);
        if (j < 0)
        {
            Log($"WARN: {name} body anchor not found, skipping atmosphere upgrade");
            return s;
        }

        string newOpen = openTag
            .Replace("<PlanetaryBody ", "<AtmosphericBody ")
            .Replace("<MinorBody ", "<AtmosphericBody ");
        string insert = (extraKit == null ? "" : "\n        " + extraKit.Trim('\n'))
            + "\n        " + atmosphere.Trim('\n') + "\n    ";
        return s[..i] + newOpen + s[(i + openTag.Length)..j] + insert + "</AtmosphericBody>"
            + s[(j + closeTag.Length)..];
    }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `VisualAtmosphereReference` (`RayleighScattering`, `MieScattering`, `Ozone`, `StartHeight`, `IsValid`) | [current/decomp/KSA/VisualAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VisualAtmosphereReference.cs#L8-L27) | XML schema of the visual atmosphere |
| `AtmosphereRenderer.FillPlanetAtmosphereData` | [current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1136-L1164) | Where the values reach the GPU |
| `SystemTemplate` body element types | [current/decomp/KSA/SystemTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SystemTemplate.cs#L24-L29) | `AtmosphericBody`, `MinorBody`, `PlanetaryBody` |
| `AtmosphericBodyTemplate : PlanetaryBodyTemplate` | [current/decomp/KSA/AtmosphericBodyTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphericBodyTemplate.cs#L5) | Why a rename keeps Pluto's children valid |
| Stock Sol template | [current/Content/Core/SolSystem.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/SolSystem.xml#L36-L43) | Library bodies vs inline bodies |

## Pitfalls and version notes

- **Inline vs library bodies.** Venus, Earth, Mars and the giants come from `Astronomicals.xml` via
  `<LoadFromLibrary>`; Titan, Pluto, Triton and Neptune are inline in each Sol template, so their edits
  must be applied to every template file (the mod does all three).
- **Dead payload kept for reference.** `Payloads.TitanPairs` (a forward-scattering Titan attempt) is
  defined but never applied; the build comments say it washed the haze white
  ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L951-L1015), [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L96-L99)).
- **Values are tuned to KSA's renderer, not only to physics.** Several comments record eye-tuning
  where KSA has no exposure compensation for distance from the Sun (Saturn's Rayleigh strength, Venus's
  crown brightness). The generated values are the physics; the hand values are the look.
- **Other atmosphere mods.** Because the shaders key on the scale heights written here, another mod
  that changes these bodies' Rayleigh or Mie scale heights silently turns the measured density tables
  off for them, by design.

## See also

- [Cloud layers in the system XML](cloud-layers-in-system-xml.md)
- [Measured density tables in the atmosphere shaders](measured-density-tables-in-shaders.md)
- [Mie phase sentinel dispatch](mie-phase-sentinel-dispatch.md)
- [Shared shadow for two cooperating mods](cross-mod-shared-shadow.md)
- [Swap Core's XML entries for patched copies with prefixes on the Mod loaders](../../../howto/assets/xml-patching/redirect-game-xml-loading/redirect-game-xml-loading-1.md) - how-to article demonstrating this feature
- [Edit shadow copies with anchor-verified text patches and generated XML between markers](../../../howto/assets/xml-patching/redirect-game-xml-loading/redirect-game-xml-loading-3.md) - how-to article demonstrating this feature
- [Promote a body to AtmosphericBody and splice generated Atmosphere values into the XML](../../../howto/celestials/atmospheres-and-rings/define-atmospheres-and-cloud-layers-in-system-xml/define-atmospheres-and-cloud-layers-in-system-xml-1.md) - how-to article demonstrating this feature

[^ksa-atmosphererenderer]: KSA.AtmosphereRenderer (decompiled, build 2026.10.10.5554)
[^ra-payloads]: Payloads.cs
