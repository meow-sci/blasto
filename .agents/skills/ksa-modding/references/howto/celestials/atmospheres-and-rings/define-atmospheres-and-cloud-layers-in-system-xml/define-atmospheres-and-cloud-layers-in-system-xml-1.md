---
type: How-To
title: Promote a body to AtmosphericBody and splice generated Atmosphere values into the XML
description: Rename a PlanetaryBody or MinorBody element to AtmosphericBody, insert a complete Atmosphere block, and tune Rayleigh, Mie and ozone terms per body with generated values and data-as-key conventions
category: celestials
group: atmospheres-and-rings
topic: define-atmospheres-and-cloud-layers-in-system-xml
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/realatmospheres/features/atmosphere-optics-in-system-xml]
techniques: [xml-patch]
hooks: [starmap StarMapBeforeMain]
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types:
  - KSA.AtmosphericBodyTemplate
  - KSA.PlanetaryBodyTemplate
  - KSA.MinorBodyTemplate
  - KSA.VisualAtmosphereReference
  - KSA.PhysicalAtmosphereReference
  - KSA.SystemTemplate
tags: [atmosphere, atmospheric-body, rayleigh, mie, ozone, xml-patch]
---
Outcome: an airless body (Pluto and Triton here) gets a drawn sky, and existing atmospheres are retuned with per-channel Rayleigh and Mie coefficients, asymmetry, absorption and an ozone tent. Demonstrated by Real Atmospheres ([Atmosphere optics in system XML](../../../../mods/realatmospheres/features/atmosphere-optics-in-system-xml.md)), which applies its edits to a copy of the Core XML delivered through an entry redirect ([redirect game XML loading](../../../assets/xml-patching/redirect-game-xml-loading/overview.md)). For a plain additive edit, [XPath patches](../../../assets/xml-patching/xpath-xml-patch-recipes/overview.md) are the lighter tool.

## Background: how the game does it

The visible atmosphere is three terms in `<Atmosphere><Visual>`: Rayleigh scattering (per-channel coefficients plus a scale height), Mie scattering (coefficients, scale height, a phase-function asymmetry `Vector3`, an absorption multiplier) and an "ozone" tent (per-channel coefficients, centre `Altitude`, half-`Extent`) ([current/decomp/KSA/VisualAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VisualAtmosphereReference.cs#L8-L27), [current/decomp/KSA/RayleighScatteringReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RayleighScatteringReference.cs#L7-L13), [current/decomp/KSA/MieScatteringReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MieScatteringReference.cs#L7-L18), [current/decomp/KSA/OzoneReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OzoneReference.cs#L8-L15)). The renderer reads them every frame in `FillPlanetAtmosphereData` ([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1136-L1164)). Which bodies draw a sky is decided by element type: `AssignPlanetSlots` only slots an `AtmosphericBody` with an atmosphere reference ([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L393-L405)). A mod therefore either edits an existing `<Atmosphere>` or turns a `PlanetaryBody` or `MinorBody` element into an `AtmosphericBody`.

## Lifecycle and timing

Real Atmospheres edits a copy of the XML in `[StarMapBeforeMain]` (`startup:starmap-before-main`, inside its `ShadowBuilder.Build`), and the game deserialises the edited files at `startup:asset-load` through the Core XML redirect. From then on the values are ordinary game data: the renderer reads them each frame and KSA's in-game atmosphere editor edits them as if they were stock. Nothing here runs per frame. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Pick the element to edit.** `LoadFromLibrary` bodies (Venus, Earth, Mars, the giants) live in `Astronomicals.xml`; Titan, Pluto, Triton and Neptune are inline in each Sol template, so their edits must be applied to every template file.

2. **Promote the body.** Rename the open and close tags to `AtmosphericBody`; `AtmosphericBodyTemplate` extends `PlanetaryBodyTemplate`, so Pluto's existing children stay valid. Triton was a `MinorBodyTemplate`, a different branch, hence an extra kit (`<MeshCollection Id="Default"/>` and `<Color>`). Append the `<Atmosphere>` block before the closing tag:

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

3. **Write a complete block.** Include `RayleighScattering`, `MieScattering`, `Ozone` (zero coefficients when there is none) and `StartHeight`, plus a `Physical` block for pressure, density and scale height. The decompiled `VisualAtmosphereReference.IsValid` chains over all four visual parts, and the mod always writes all four rather than rely on defaults (in build 5554 nothing in the decompiled game calls `AtmosphereReference.IsValid` to gate loading, so this is a safety convention, not a verified load requirement):

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

4. **Tune the optics.** Use per-channel `Coefficients R/G/B` for Rayleigh and Mie, a scale height per term, `PhaseFunctionAsymmetry` (default `0.8`) and `AbsorptionMultiplier` (default about `1.111`). The ozone tent `saturate(1 - |h - Altitude| / Extent)` times per-channel coefficients is the only per-channel pure absorber in the model (Mie absorption is a grey multiplier), so it can carry any absorber: Venus's UV absorber, Jupiter's chromophore, the ice giants' methane. The boundary height where the densest term reaches `1e-10` is derived, so very large scale heights make a very tall sky.

5. **Generate the numbers, do not hand-type them.** The mod's XML payloads are C# interpolated raw strings that pull attribute text such as `R="0.000223272" G="..." B="..."` from a generated `AtmosphereTables` class, so regenerating updates the XML and the shader tables together (see [generate measured atmosphere tables](../../../celestials/atmospheres-and-rings/generate-measured-atmosphere-tables-offline/overview.md)).

6. **Make the data a key when shaders need to know the body.** The mod's patched shaders recognise a body by its radius and the exact scale height written here (for example Earth's 8430 m) and only then use a measured density table; asymmetry sentinels (`X < -0.5` for Mars dust, `Z < -1.5` for Pluto's two-part haze phase) select special phase functions. Another mod that changes those scale heights silently turns the tables off for those bodies, by design. See [branch per body inside shared game shaders](../../../shaders/stock-shaders/branch-per-body-inside-shared-game-shaders/overview.md).

## Complete example

| File | Role |
|---|---|
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L90-L159) | Which edit runs on which file |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L581-L602) | `UpgradeBody` |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1690-L1779) | Pluto and Triton atmospheres and Triton's body kit |
| [AtmosphereTables.Generated.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.Generated.cs#L270-L336) | Generated attribute strings |

## Variations and gotchas

- **Inline vs library bodies** need different files.
- **Values are tuned to KSA's renderer**, not only to physics: KSA has no exposure compensation for distance from the Sun, so Saturn's Rayleigh strength and Venus's crown brightness are eye-tuned.
- **Flight atmosphere is separate.** The `Physical` block (not the `Visual` one) feeds drag and pressure; see [override atmosphere and ocean in the physics environment](../../../physics/aero-and-ground/override-atmosphere-and-ocean-in-the-physics-environment/overview.md) and [generate measured atmosphere tables](../../../celestials/atmospheres-and-rings/generate-measured-atmosphere-tables-offline/overview.md).
- **Dead payloads.** A Titan forward-scattering attempt is defined but never applied; the build comments say it washed the haze white.

## See also

- [Overview](overview.md); [approach 2: cloud layers](define-atmospheres-and-cloud-layers-in-system-xml-2.md); [approach 3: texture scripts](define-atmospheres-and-cloud-layers-in-system-xml-3.md)
- [Atmosphere optics in system XML](../../../../mods/realatmospheres/features/atmosphere-optics-in-system-xml.md)
- [Read a body's properties and orbit elements](../../../celestials/systems-and-stars/read-body-properties-and-orbit-elements/overview.md)
