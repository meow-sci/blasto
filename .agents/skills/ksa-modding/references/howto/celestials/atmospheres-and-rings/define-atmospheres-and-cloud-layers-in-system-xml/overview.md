---
type: Game Feature
title: Define atmospheres, scattering and cloud layers for a body in system XML
description: Promote a body to AtmosphericBody with a complete Atmosphere block, tune Rayleigh, Mie and ozone terms, add a sibling Clouds block with coverage masks, detail tiles, flowmaps and cloud types, and build the textures offline
category: celestials
group: atmospheres-and-rings
topic: define-atmospheres-and-cloud-layers-in-system-xml
demonstrated_by:
  - mods/realatmospheres/features/atmosphere-optics-in-system-xml
  - mods/realatmospheres/features/cloud-layers-in-system-xml
  - mods/realatmospheres/features/texture-and-phase-asset-scripts
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types:
  - KSA.AtmosphericBodyTemplate
  - KSA.PlanetaryBodyTemplate
  - KSA.MinorBodyTemplate
  - KSA.VisualAtmosphereReference
  - KSA.PhysicalAtmosphereReference
  - KSA.SystemTemplate
  - KSA.CloudsReference
  - KSA.CloudLayerReference
  - KSA.VolumetricCloudReference
  - KSA.RaymarchingReference
  - KSA.TextureReference
tags: [atmosphere, clouds, scattering, rayleigh, mie, ozone, system-xml, textures]
---
A body's sky is data. KSA reads an `<Atmosphere>` element (a `Visual` block that drives scattering and a `Physical` block that drives drag and pressure) and a sibling `<Clouds>` element from the body's XML template, and the renderers copy those numbers to the GPU every frame. Writing them is therefore an XML job: promote an airless body to `AtmosphericBody`, give it a complete block, tune the optics, and describe cloud layers whose coverage and flow come from textures built offline.

## Engine background

- Schema: `AstronomicalTemplate` declares `[XmlElement("Atmosphere")] AtmosphereReference` and `[XmlElement("Clouds")] CloudsReference`, side by side ([current/decomp/KSA/AstronomicalTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AstronomicalTemplate.cs#L55-L60)); `OnDataLoad` loads both ([current/decomp/KSA/AstronomicalTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AstronomicalTemplate.cs#L152-L153)).
- `AtmosphereReference` holds `Visual` and `Physical` ([current/decomp/KSA/AtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereReference.cs#L5-L13)). `VisualAtmosphereReference` has `RayleighScattering`, `MieScattering`, `Ozone` and `StartHeight`, and derives a boundary `Height` from where the densest term falls to `1e-10` ([current/decomp/KSA/VisualAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VisualAtmosphereReference.cs#L8-L52)). `PhysicalAtmosphereReference` has `SeaLevelPressure`, `SeaLevelDensity` and `ScaleHeight` ([current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L12-L18)).
- Body kinds: `SystemTemplate.Bodies` maps element names to templates: `AtmosphericBody`, `PlanetaryBody`, `MinorBody`, `TerrestrialBody`, `StellarBody`, `LoadFromLibrary` and others ([current/decomp/KSA/SystemTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SystemTemplate.cs#L24-L36)). `AtmosphericBodyTemplate` extends `PlanetaryBodyTemplate`; `MinorBodyTemplate` is a different branch ([current/decomp/KSA/AtmosphericBodyTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphericBodyTemplate.cs#L5)).
- Rendering: `AtmosphereRenderer.AssignPlanetSlots` only gives a slot to planets that are an `AtmosphericBody` **and** have an atmosphere reference ([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L393-L405)); `FillPlanetAtmosphereData` copies Rayleigh, Mie, ozone, `StartHeight` and the derived `Height` into the GPU struct ([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1136-L1164)). So the element type, not just the child, decides whether a sky is drawn.
- Clouds: `CloudsReference` has the volumetric-to-2D transition band, shadow and flicker distances and a list of `Layer`s ([current/decomp/KSA/CloudsReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CloudsReference.cs#L8-L21)); a `CloudLayerReference` has `RotationSpeed`, `VolumetricCloud`, `TwoDimensionalCloud` and `MatchGroundColor` ([current/decomp/KSA/CloudLayerReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CloudLayerReference.cs#L8-L19)); `VolumetricCloudReference` has `Texture`, `Detail`, `Color`, `Raymarching`, `Noise`, `VolumetricsFlowMap`, `VolumetricsColorMap` and `CloudType`s ([current/decomp/KSA/VolumetricCloudReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricCloudReference.cs#L9-L31)).
- Stock reference: Venus's `<Atmosphere>` and `<Clouds>` in [current/Content/Core/Astronomicals.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Astronomicals.xml#L162-L204).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: promote to AtmosphericBody and splice generated Atmosphere values](define-atmospheres-and-cloud-layers-in-system-xml-1.md) | realatmospheres | `xml-patch` | `startup:starmap-before-main`, `startup:asset-load` | Full control of the sky; values are tuned to the renderer and shader tables |
| [2: templated decks and laminae with absolute asset paths](define-atmospheres-and-cloud-layers-in-system-xml-2.md) | realatmospheres | `xml-patch` | `startup:starmap-before-main`, `startup:asset-load` | Clouds per planet from one template; a rooted path lets Core XML reach a mod folder |
| [3: offline scripts for coverage, detail and flowmap textures](define-atmospheres-and-cloud-layers-in-system-xml-3.md) | realatmospheres | `xml-asset` | `startup:asset-load` (textures only; scripts run at build time) | Channel layouts must match the shader; large derived textures are not checked in |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Redirect game XML loading](../../../assets/xml-patching/redirect-game-xml-loading/overview.md), [Patch game XML data](../../../assets/xml-patching/patch-game-xml-data/overview.md)
- [Generate measured atmosphere tables from reference profiles](../../../celestials/atmospheres-and-rings/generate-measured-atmosphere-tables-offline/overview.md), [Override atmosphere and ocean in the physics environment](../../../physics/aero-and-ground/override-atmosphere-and-ocean-in-the-physics-environment/overview.md)
- [Branch per body inside shared game shaders](../../../shaders/stock-shaders/branch-per-body-inside-shared-game-shaders/overview.md), [Extend stock sky and cloud shaders](../../../shaders/stock-shaders/extend-stock-sky-and-cloud-shaders/overview.md), [Send extra data to stock shaders without new uniforms](../../../shaders/stock-shaders/send-extra-data-to-stock-shaders-without-new-uniforms/overview.md)
- [Read a body's properties and orbit elements](../../../celestials/systems-and-stars/read-body-properties-and-orbit-elements/overview.md), [Composite a PNG over a stock texture](../../../assets/models-and-textures/composite-png-over-stock-texture/overview.md), [Draw exhaust plumes without an engine](../../../rendering/lights-and-particles/draw-exhaust-plumes-without-an-engine/overview.md)
- [Read a body's atmosphere, ocean, ring, rotation, tilt, tidal-lock and orbit-element data](../../systems-and-stars/read-body-properties-and-orbit-elements/overview.md) - related topic
- [Generate measured atmosphere tables from reference profiles](../generate-measured-atmosphere-tables-offline/overview.md) - related topic

