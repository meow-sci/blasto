---
type: How-To
title: Situation assets referenced by a library-loaded starter vessel
description: Define a reusable orbital Situation and bind a stock default vehicle to it in a custom system for Testing mode
category: celestials
group: systems-and-stars
topic: place-starting-vessels-in-custom-systems
difficulty: intermediate
prerequisites: [KSA native content mod, custom system XML, stock default vehicle save]
demonstrated_by: [mods/lonelyplanets/features/planet-starting-situations]
techniques: [xml-asset, toml-config]
hooks: [native KSA.Mod.LoadAssetBundles, native KSA.Mod.LoadSystems, native KSA.Universe.LoadSystem]
lifecycle: [startup:asset-load, startup:game-loaded]
game_types:
  - KSA.Mod
  - KSA.AssetBundle
  - KSA.SystemTemplate
  - KSA.VehicleTemplate
  - KSA.SituationTemplate
  - KSA.OrbitTemplate
  - KSA.Vehicle
tags: [starting-vessel, situation-xml, orbital-elements, testing-mode]
---

Place a stock `Gemini7` in a chosen orbit around Mars when your custom system starts in Testing mode. Lonely Planets demonstrates the pattern with [planet starting situations](../../../../mods/lonelyplanets/features/planet-starting-situations.md). You need a native mod folder, a system XML with Sol and Mars, and Core's default `Gemini7` save; [compose a reduced system](../compose-reduced-star-systems/compose-reduced-star-systems.md) covers the body tree.[^lp-mars][^ksa-vehicletemplate]

## Background: how the game does it

An `<Assets>` bundle registers named `<Situation>` objects. A system's `<LoadVehicleFromLibrary>` creates a `VehicleTemplate`, whose `<SituationRef InstanceOf="…">` resolves that name through `SituationTemplate.GetTemplate`. `OrbitTemplate` holds the six orbit values; `Vehicle.GetSituationState` creates an orbit relative to the vehicle's `Parent` body.[^ksa-assetbundle][^ksa-systemtemplate][^ksa-vehicletemplate][^ksa-situation][^ksa-orbittemplate][^ksa-vehicle]

## Lifecycle and timing

- `startup:asset-load`: `Mod.LoadAssetBundles` reads the local situation XML and registers its assets before `Mod.LoadSystems` reads the selected system. During `SystemTemplate.OnDataLoad`, KSA strips starting vehicle templates outside Testing.[^ksa-mod][^ksa-assetbundle][^ksa-systemtemplate]
- `startup:game-loaded`: `Universe.LoadSystem` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L223-L259)) instantiates the selected system and its remaining starter vessel; `Vehicle.GetSituationState` evaluates the named orbit against the parent body. This is system construction, not a per-frame callback.[^ksa-universe][^ksa-vehicle]

See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Add a situation asset file to `mod.toml`.** In the same mod directory as your system XML, create `QuietMarsSituations.xml` and include its path in `assets`. This reduced manifest fragment is adapted from Lonely Planets' [mod.toml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/mod.toml#L58-L64). If the manifest already has an `assets` array, append the file to that array rather than declaring the key twice.[^lp-manifest][^ksa-mod]

   ```toml
   # adapted from mod.toml L58-L64
   assets = ["QuietMarsSituations.xml"]
   ```

2. **Define a uniquely named orbital situation.** Save this complete `QuietMarsSituations.xml` file. It wraps the Mars orbit values from Lonely Planets' [LonelySituations.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/LonelySituations.xml#L25-L34) in `<Assets>` and gives the situation a new ID so it can coexist with Lonely Planets. `SemiMajorAxis Km="5000"` is the orbit's radius from the body's center for this zero-eccentricity case, **not** 5,000 km above the surface. `TimeAtPeriapsis Minutes="90"` is an orbital epoch value, not an initialization delay.[^lp-situations][^ksa-orbittemplate]

   ```xml
   <!-- adapted from LonelySituations.xml L2, L25-L34, L46 -->
   <Assets>
       <Situation Id="QuietMarsStartOrbit">
           <Orbit>
               <SemiMajorAxis Km="5000" />
               <Inclination Degrees="-60" />
               <Eccentricity Value="0" />
               <LongitudeOfAscendingNode Degrees="0" />
               <ArgumentOfPeriapsis Degrees="0" />
               <TimeAtPeriapsis Minutes="90" />
           </Orbit>
       </Situation>
   </Assets>
   ```

3. **Bind the vehicle in your system XML.** Insert this block adapted from [MarsOnly.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/MarsOnly.xml#L32-L37) before `</System>`, after the Mars body reference. `Parent="Mars"` must match a body selected in that same system, and `InstanceOf` must match the situation ID from step 2. `Gemini7` is resolved from KSA's default vehicle saves; no vessel save is bundled here.[^lp-mars][^ksa-vehicletemplate]

   ```xml
   <!-- adapted from MarsOnly.xml L32-L37 -->
       <LoadVehicleFromLibrary Id="Gemini7" Parent="Mars">
       <SituationRef InstanceOf="QuietMarsStartOrbit"/>
           <Body2Cce X="0" Y="0" Z="0" />
           <BodyRates X="0" Y="0" Z="0" />
           <Color R="0.33" G="0.95" B="0" />
       </LoadVehicleFromLibrary>
   ```

4. **Start `QuietMars` in Testing mode and inspect the vessel.** In a non-Testing game, `SystemTemplate.OnDataLoad` removes this vehicle template. In Testing, choose `Gemini7` and the `QuietMarsStartOrbit` starting location in the startup picker ([current/decomp/KSA/ConfigOnStartPopup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConfigOnStartPopup.cs#L177-L213)), then confirm the vessel starts around Mars. The log should include `loaded Situation Template 'QuietMarsStartOrbit'` and `loaded system 'QuietMars'`. If the orbit differs, check the configured start vehicle, parent and situation: when `StartVehicle` matches this vessel and both `StartParent` and `StartSituation` are nonempty, KSA replaces the XML values.[^ksa-systemtemplate][^ksa-situation][^ksa-gamesettings][^ksa-startpopup]

## Complete example

| File | Role |
|---|---|
| [mod.toml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/mod.toml#L58-L64) | Registers `LonelySituations.xml` as a local asset bundle. |
| [LonelySituations.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/LonelySituations.xml#L1-L46) | Four reusable orbit situations; the Mars one is shown above. |
| [MarsOnly.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/MarsOnly.xml#L28-L38) | Sol/Mars body tree and the `Gemini7` starter vessel. |

## Variations and gotchas

- Jupiter and Saturn use the same pattern but `SemiMajorAxis Km="80000"`; their `TimeAtPeriapsis` values are `15` and `-90` minutes respectively. Recompute orbit parameters for your own body instead of treating these as surface altitudes.[^lp-jupiter][^lp-saturn]
- Lonely Planets' README reports a vehicle creator failure in its reduced systems and instructs Testing mode. The explicit non-Testing removal of the starter template is established by code; the creator failure was not reproduced here.[^lp-readme][^ksa-systemtemplate]
- `VehicleTemplate.OnDataLoad` logs `Could not find default vehicle save '…'` when the named stock save is unavailable. Check the exact vehicle ID and installed Core data.[^ksa-vehicletemplate]

## See also

- [Overview](overview.md)
- [Compose a reduced star system](../compose-reduced-star-systems/overview.md)
- [Read orbit elements and timing](../../../flight-and-orbits/flight-state/read-orbit-elements-and-timing/overview.md)
- [Planet starting situations](../../../../mods/lonelyplanets/features/planet-starting-situations.md)

[^lp-manifest]: LonelyPlanets mod.toml
[^lp-situations]: LonelyPlanets LonelySituations.xml
[^lp-mars]: LonelyPlanets MarsOnly.xml
[^lp-jupiter]: LonelyPlanets JupiterOnly.xml
[^lp-saturn]: LonelyPlanets SaturnOnly.xml
[^lp-readme]: LonelyPlanets README.md
[^ksa-mod]: KSA.Mod, build 2026.10.10.5554
[^ksa-assetbundle]: KSA.AssetBundle, build 2026.10.10.5554
[^ksa-systemtemplate]: KSA.SystemTemplate, build 2026.10.10.5554
[^ksa-vehicletemplate]: KSA.VehicleTemplate, build 2026.10.10.5554
[^ksa-situation]: KSA.SituationTemplate, build 2026.10.10.5554
[^ksa-orbittemplate]: KSA.OrbitTemplate, build 2026.10.10.5554
[^ksa-vehicle]: KSA.Vehicle, build 2026.10.10.5554
[^ksa-universe]: KSA.Universe, build 2026.10.10.5554
[^ksa-gamesettings]: KSA.GameSettings, build 2026.10.10.5554
[^ksa-startpopup]: KSA.ConfigOnStartPopup, build 2026.10.10.5554
