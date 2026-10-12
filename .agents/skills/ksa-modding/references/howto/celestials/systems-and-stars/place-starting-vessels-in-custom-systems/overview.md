---
type: Game Feature
title: Place starting vessels in a custom system with reusable situations
description: Register orbital Situation assets and reference them from library-loaded starter vessels in a native system XML
category: celestials
group: systems-and-stars
topic: place-starting-vessels-in-custom-systems
demonstrated_by: [mods/lonelyplanets/features/planet-starting-situations]
lifecycle: [startup:asset-load, startup:game-loaded]
game_types:
  - KSA.AssetBundle
  - KSA.SystemTemplate
  - KSA.VehicleTemplate
  - KSA.SituationTemplate
  - KSA.OrbitTemplate
  - KSA.Vehicle
tags: [starting-vessel, situation-xml, orbit, testing-mode]
---

A native system can specify a starting vehicle and a reusable orbit situation. Lonely Planets defines one situation per featured planet, then places Core's `Gemini7` around the selected planet with `<LoadVehicleFromLibrary>`.[^lp-situations][^lp-mars]

## Engine background

`AssetBundle.OnDataLoad` registers `<Situation>` assets. `SystemTemplate.Bodies` recognizes `<LoadVehicleFromLibrary>` and `VehicleTemplate.Situation` reads its `<SituationRef>`. `SituationTemplate.GetTemplate` resolves `InstanceOf`; `Vehicle.GetSituationState` creates the runtime orbit around the vehicle's parent from `OrbitTemplate` values.[^ksa-assetbundle][^ksa-systemtemplate][^ksa-vehicletemplate][^ksa-situation][^ksa-orbittemplate][^ksa-vehicle]

`SystemTemplate.OnDataLoad` removes all vehicle templates outside Testing mode. For the configured start vehicle, nonempty `StartSituation` and `StartParent` settings can replace the XML's situation and parent.[^ksa-systemtemplate]

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Situation assets referenced by a library-loaded vessel](place-starting-vessels-in-custom-systems.md) | Lonely Planets | `xml-asset` | `startup:asset-load`, `startup:game-loaded` | Reuses a stock default vessel and orbit data; starter is Testing-only and start settings can override it |

## See also

- [Compose a reduced star system](../compose-reduced-star-systems/overview.md)
- [Read orbit elements and timing](../../../flight-and-orbits/flight-state/read-orbit-elements-and-timing/overview.md)
- [Spawn a vehicle at runtime](../../../vehicles-and-parts/vehicles/spawn-a-vehicle-at-runtime/overview.md)
- [Game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)

[^lp-situations]: LonelyPlanets LonelySituations.xml
[^lp-mars]: LonelyPlanets MarsOnly.xml
[^ksa-assetbundle]: KSA.AssetBundle, build 2026.10.10.5554
[^ksa-systemtemplate]: KSA.SystemTemplate, build 2026.10.10.5554
[^ksa-vehicletemplate]: KSA.VehicleTemplate, build 2026.10.10.5554
[^ksa-situation]: KSA.SituationTemplate, build 2026.10.10.5554
[^ksa-orbittemplate]: KSA.OrbitTemplate, build 2026.10.10.5554
[^ksa-vehicle]: KSA.Vehicle, build 2026.10.10.5554
