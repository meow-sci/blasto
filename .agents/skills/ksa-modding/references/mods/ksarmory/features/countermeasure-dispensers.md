---
type: Mod Feature
title: Flare and chaff dispenser parts with simulated decoys
description: Radial ALE-47 dispenser parts throw mod-simulated flares and chaff in timed salvos, by button or missile warning, and each decoy flies as a dragged point with a fading signature that feeds the radar's chaff notch and seekers that judge each decoy once
mod: ksarmory
category: vehicles-and-parts
techniques:
  - starmap-lifecycle
  - harmony-prefix
  - xml-asset
  - part-id-lookup
  - semi-implicit-drag
  - reference-keyed-dictionary
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame, startup:asset-load]
game_types: [KSA.Vehicle, KSA.Part, KSA.PartTree, KSA.Celestial, KSA.IParentBody, KSA.Universe, KSA.Program]
tags: [countermeasures, flares, chaff, decoy, dispenser, warning-receiver, part-xml, seduction]
---

KSArmory adds two radially mounted parts, the AN/ALE-47 flare dispenser (15 MJU-7 flares) and the
AN/ALE-47 chaff dispenser (30 RR-170 cartridges). They have no part module: the mod recognises
them on any craft by part id, keeps a magazine per part, and on a button press or an automatic
missile warning throws decoys out of the part's mounting face. Each decoy is a mod-simulated point
that falls through the body's moving air with stiff drag and a signature that rises and fades.
Missile seekers judge each decoy once and may be seduced; tracking radars lose a beaming target
to chaff in the Doppler notch.[^ksarmory-countermeasures][^ksarmory-arsenal]

What is reusable: "a part is a capability" recognition by part id against a catalogue, keyed on
the `Part` reference so the magazine survives a craft splitting; a salvo queue that runs on
simulated time; and one world-wide list of decoys stepped once per step at the same epoch as every
other contact.

## How it works

**Parts.** Plain XML: a `<Part>` prefab in `KSArmoryAssets.xml` (one subpart, one surface
connector) and a `<PartGameData>` in `KSArmoryGameData.xml` (editor tags `Weapons`/`Radial`,
`ToSurface` connector, a full-load `SolidSphereMass` of 8.6 kg (flare) or 5.9 kg (chaff), about
3 kg of module plus cartridges, and a box collider). The mesh's `+X` is out of the mounting face, which is the eject direction.[^ksarmory-assets][^ksarmory-gamedata]

**Profiles** (`Arsenal`): `DispenserProfile` (part id, decoy name, `Count`, `SalvoSize = 2`,
`SalvoIntervalSeconds = 0.25`, `EjectDirection = +X`, `EjectSpeed = 30 m/s`) and `DecoyProfile`:

| Decoy | Kind | Peak signature | Rise | Life | Mass | Drag per metre |
|---|---|---|---|---|---|---|
| MJU-7 flare | Flare (fools infrared) | 25 kW/sr | 0 s | 4.5 s | 0.37 kg | 0.004 |
| RR-170 chaff | Chaff (fools radar) | 400 m² on `RadarSignature`'s scale | 0.5 s | 5 s | 0.095 kg | 10 |

**Discovery** (`Countermeasures.Sync`): for every live craft in the census, walk
`vehicle.Parts.Parts` and look each `Part.Id` up with `Catalogue.DispenserForPart`. Entries are keyed
on the `Part` reference, so a dispenser carried onto another craft by a split keeps its count. A
craft whose part tree threw while being read is marked unread and its dispensers are kept, so a
mid-rebuild frame does not refill them.[^ksarmory-catalogue]

**Throwing.**

1. A press (`Dispense(craft, kind)`) queues `SalvoSize` on **every** dispenser of that kind aboard,
   so a symmetric pair throws either side (`DispenseOrder.Request`).
2. `Dispenser.Update(step)` releases queued cartridges every `SalvoIntervalSeconds` of simulated
   time (a pause holds a salvo).
3. `Eject` places the decoy 0.2 m out along the part's eject axis, converted with
   `Part.PositionVehicleAsmb`, `Part.Asmb2VehicleAsmb` and the craft's `Asmb2Ego` and centre of mass,
   with the craft's velocity plus 30 m/s outward, and adds it to the static `Countermeasures.Live`.
4. `StoreMass.Want` lowers the part's mass by the cartridges thrown.[^ksarmory-dispenser]

**Auto-dispense** (`AutoDispense.Decide`, per craft, opt-in checkbox): every guided `Interceptor`
in the airborne sample whose `TargetRef` is this craft, within 6 km and closing, votes flares
(infrared seeker), chaff (radar seeker) or both (unknown band); at most one salvo per second.

**Decoy flight** (`Decoy.Step`): sub-steps of at most 20 ms; drag `k·|v_rel|` with `k =
DragPerMetre × density ratio`, applied semi-implicitly to the velocity relative to the ground-moving
air (`KsaWorld.GroundVelocityAt` = body velocity plus `GetAngularVelocityCce × r`) because chaff's drag
is stiff; gravity sampled half a step back; near the ground `TrySnapToGround` lands it, where it
rides the surface until spent.[^ksarmory-decoy][^ksarmory-ksaworld]

**Effect on sensors.**

| Consumer | Rule |
|---|---|
| Missile seeker (`SeekerLock`, W3 side) | Each decoy of the seeker's band is judged **once**, the first frame its `LockStrength` beats the target's; radar seekers only consider chaff within 150 m of the target and inside the Doppler gate; seduced with probability `0.8 × (1 − CountermeasureResistance)`, then held until spent[^ksarmory-seekerlock] |
| Tracking radar (`ChaffNotch`) | Target inside `ChaffNotchMps` of zero radial speed and chaff in its 150 m cell that outshines its cross-section and is also in the notch: track broken, dwell reset, `ChaffReacquireSeconds` to find again; with `OpticalBackup` the camera holds it while it stays in the notch[^ksarmory-chaffnotch] |

`Signature.LockStrength` is signature over range² (heat) or range⁴ (radar) in km, weighted by
`1 − (offBoresight/field)²`; only ratios are used.[^ksarmory-signature]

**Drawing** is separate: `Visuals.DrawDecoys` (debug overlay, UI pass) and `DecoyEffects` (pooled
particle emitters for flare cores and smoke, at most twelve lit, stepped in the simulation tail).[^ksarmory-decoyeffects]

## Lifecycle and timing

Inside `KSArmoryMod.StepSimulation`:[^ksarmory-mod]

| Line | Call | Gate |
|---|---|---|
| L697 | `_countermeasures?.Sync(KsaWorld.Vehicles)` | Every frame, before the clock gate |
| L763 | `CollectAirborne(step)` | Simulated step only |
| L768 | `_countermeasures?.Update(step, _airborne)`: step decoys, auto-dispense, release salvos | Simulated step only |
| L776 | Fire loop: radars run `ChaffNotch`, rounds run `SeekerLock` against `Countermeasures.Live` | Simulated step only |
| L779 | `WantStoreMass` (dispenser mass) | Simulated step only |
| L964 | `_decoyEffects.Update(Countermeasures.Live)` | Every frame (tail) |

The ordering is the point: decoys are stepped after the airborne sample (which the warning receiver
reads) and before any round is flown, so a seeker reads each decoy at the end of the step it is
about to integrate across, exactly as it reads a craft, and both use the same clamped `step`. A
decoy ejected in this update is placed at the step's end and first stepped next frame.

`StepSimulation` runs from `StepOnce` under `[StarMapAfterGui]` (postfix on
`Program.OnDrawUiViewports`, Program.cs L2268), or with the UI hidden from the Harmony prefix on
`Program.OnFrameCelestials` (L2320), or from `[StarMapAfterOnFrame]` as a fallback. All run after
`PrepareFrame` applied the vehicle step (`Universe.ApplyVehicleSolvers`, Universe.cs L1922/L1948) and
dispatched the next (`ExecuteNextVehicleSolvers`, Program.cs L2220), so craft position, velocity and
part transforms used for ejection are the state at `Universe.GetElapsedTime()` (Universe.cs L2399).
Decoys never touch the engine's physics: nothing is spawned, so there is no solver-window hazard.[^ksa-program][^ksa-universe]

The Flares/Chaff/Both buttons and the auto checkbox live in the panel (`UiSystem`), drawn after
`StepOnce` in the same hook; a press only queues, so the first cartridge leaves on the next
simulated step.[^ksarmory-uisystem]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/Countermeasures.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Countermeasures.cs#L66-L120) | `Sync`: find dispensers by part id, keep by `Part` reference |
| [src/KSArmory/Ksa/Countermeasures.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Countermeasures.cs#L168-L285) | `Update`, `StepDecoys`, landing, warning receiver input, `Eject` |
| [src/KSArmory/Sim/Dispenser.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Dispenser.cs) | Profile, salvo queue, craft-wide request, `AutoDispense` |
| [src/KSArmory/Sim/Decoy.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Decoy.cs) | Decoy profile, signature curve, semi-implicit drag step |
| [src/KSArmory/Sim/ChaffNotch.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ChaffNotch.cs) | Radar track break, reacquire, optical hold |
| [src/KSArmory/Sim/Signature.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Signature.cs) | Heat of craft and round, lock strength, closing speed |
| [src/KSArmory/Sim/RadarSignature.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/RadarSignature.cs) | Cross-section scale chaff is quoted on |
| [src/KSArmory/Sim/SeekerLock.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SeekerLock.cs#L50-L100) | Judged-once seduction (seeker side) |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L3680-L3717) | `Seek`: where a round consults `Countermeasures.Live` |
| [src/KSArmory/Ksa/Radar.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Radar.cs#L243-L252) | Where a radar consults the notch |
| [src/KSArmory/Sim/Arsenal.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Arsenal.cs#L1332-L1400) | MJU-7, RR-170, ALE-47 profiles |
| [src/KSArmory/KSArmoryAssets.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryAssets.xml#L1671-L1697) | Dispenser part prefabs (also mesh atlas L97-L110, subparts L403-L422) |
| [src/KSArmory/KSArmoryGameData.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryGameData.xml#L742-L800) | Editor tags, connector, mass, collider |
| [src/KSArmory/Ksa/Ui/UiSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiSystem.cs#L160-L216) | Load readout, Flares/Chaff/Both, Auto-dispense |

## Snippets

```csharp
// src/KSArmory/Ksa/Countermeasures.cs L168-L190
    public void Update(double step, IReadOnlyList<IContact> airborne)
    {
        StepDecoys(step);

        foreach ((Vehicle craft, CraftState state) in _crafts)
        {
            if (!state.AutoOn || !KsaWorld.IsAlive(craft)) continue;

            CollectIncoming(craft, airborne);
            (bool flares, bool chaff) = state.Auto.Decide(step, KsaWorld.PositionEcl(craft),
                                                          KsaWorld.VelocityEcl(craft), _incoming);
            if (flares) Dispense(craft, DecoyKind.Flare);
            if (chaff) Dispense(craft, DecoyKind.Chaff);
        }

        foreach (Entry e in _entries.Values)
        {
            if (!KsaWorld.IsAlive(e.Craft)) continue;

            int thrown = e.Dispenser.Update(step);
            for (int i = 0; i < thrown; i++) Eject(e);
        }
    }
```

```csharp
// src/KSArmory/Sim/Decoy.cs L118-L134
        double k = Profile.DragPerMetre * (double.IsFinite(densityRatio) ? Math.Max(0.0, densityRatio) : 0.0);
        int steps = Math.Max(1, (int)Math.Ceiling(dt / SubStep));
        double h = dt / steps;

        double3 relative = VelocityEcl - airVelocityEcl;
        double3 position = PositionEcl;

        for (int i = 0; i < steps; i++)
        {
            double3 before = relative;
            relative = (relative + (gravity * h)) / (1.0 + (k * Vec.Len(relative) * h));
            position += (airVelocityEcl + ((before + relative) * 0.5)) * h;
        }

        PositionEcl = position;
        VelocityEcl = airVelocityEcl + relative;
        Age += dt;
```

```csharp
// src/KSArmory/Sim/SeekerLock.cs L73-L96
        for (int i = 0; i < decoys.Count; i++)
        {
            Decoy d = decoys[i];
            if (d.Profile.Fools != munition.Band || _judged.Contains(d.Id)) continue;

            double3 at = d.PositionEcl - (d.VelocityEcl * frameSeconds);
            if (munition.Band == SeekerBand.Radar && Vec.Len(at - targetAt) > ChaffNotch.CellMetres) continue;

            double strength = StrengthOf(munition, seekerPosEcl, seekerLocalVel, at, d.Signature);
            if (strength <= best) continue;

            if (munition.DopplerGateMps > 0f)
            {
                double closing = Signature.ClosingSpeed(seekerPosEcl, seekerVelEcl, at, d.VelocityEcl);
                if (Math.Abs(closing - targetClosing) > munition.DopplerGateMps) continue;
            }

            _judged.Add(d.Id);
            if (_random.NextDouble() < SeductionChance * (1.0 - munition.CountermeasureResistance))
            {
                taken = d;
                best = strength;
            }
        }
```

```xml
<!-- src/KSArmory/KSArmoryAssets.xml L1671-L1685 -->
  <!--
    AN/ALE-47 countermeasures dispensers, radially mounted. One kind per part, as a real magazine
    section is loaded: the flare module's fifteen 1x2-inch cells, the chaff module's thirty 1x1.
  -->
  <Part Id="KSArmory_Prefab_Ale47Flare">
    <SubPart Id="KSArmory_Ale47Flare_Body" InstanceOf="KSArmory_Ale47Flare_SubPart" />

    <!-- Attaches by the flange, which the export puts on the mesh origin. -->
    <Connector Id="_cmConnectorBase">
      <Transform>
        <Position X="0.00000" />
        <Rotation Z="3.14159" />
      </Transform>
    </Connector>
  </Part>
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Vehicle.Parts`, `PartTree.Parts` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L136) | Walk the part list (Vehicle.cs L605)[^ksa-parttree] |
| `Part.Id`, `Part.PositionVehicleAsmb`, `Part.Asmb2VehicleAsmb` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L702-L724) | Recognise the dispenser; locate its face and eject axis[^ksa-part] |
| `Vehicle.Asmb2Ego`, `Vehicle.CenterOfMassAsmb` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L502-L565) | Vehicle-assembly point and direction to Ecl[^ksa-vehicle] |
| `Vehicle.GetPositionEcl`, `Vehicle.GetVelocityEcl` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L954-L963) | Ejection velocity, warning-receiver geometry |
| `Celestial.GetPositionEcl`, `Celestial.GetVelocityEcl`, `Celestial.MaxTerrainHeightApprox` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L386-L403) | Air frame and near-ground test (L145)[^ksa-celestial] |
| `IParentBody.GetAngularVelocityCce` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L81) | Ground-moving air the decoy drags against[^ksa-iparentbody] |
| `Universe.ApplyVehicleSolvers`, `Universe.GetElapsedTime` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1948) | Epoch of the craft samples (L2399) |

## Pitfalls and version notes

- **No part module.** Nothing in the part XML says "dispenser"; the C# catalogue maps part ids to
  profiles. A pack that reuses the part id without the profile gets an inert part.
- **Key by `Part`, not by craft and index.** A split renumbers parts; the reference survives and
  keeps the remaining count.
- **Do not drop entries when the part tree fails to read** (it throws mid-rebuild); that would
  refill the magazine next frame.
- **Judge once, not per frame.** A per-frame roll makes a flare's effectiveness depend on frame rate;
  `SeekerLock._judged` records decoy ids already tried.
- **Static live list.** `Countermeasures.Live` is static and shared with radars and seekers;
  `ForgetTheWorld` calls `Discard` on save load so decoys from the replaced world do not linger.
- **Decoys are invisible to radars as contacts.** They are not `IContact`s; they only break tracks
  through the notch and seduce seekers.
- **The flare `DragPerMetre` and seduction constants are the author's estimates** (comments say
  "order of", "chosen rather than measured"); treat them as tuning, not data.

## See also

- [Seekers, uplink and laser homing](./seekers-uplink-and-laser-homing.md)
- [Radar search cone, masking and lock dwell](./radar-search-and-lock.md)
- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Point gravity and atmosphere model](./point-gravity-and-atmosphere-model.md)
- [Per-part store mass](./per-part-store-mass.md)
- [Pooled particle emitters](./pooled-particle-emitters.md)
- [Weapon part definitions in XML](./weapon-part-definitions-xml.md)
- [Weapon registry and profiles](./weapon-registry-and-profiles.md)
- [Operator panel in ImGui](./operator-panel-imgui.md)
- [Decide the dispenser kind by which part is fitted, and simulate flares and chaff as decoys](../../../howto/vehicles-and-parts/craft-tracking/give-inert-parts-behaviour-by-part-id/give-inert-parts-behaviour-by-part-id-2.md) - how-to article demonstrating this feature

[^ksarmory-countermeasures]: src/KSArmory/Ksa/Countermeasures.cs
[^ksarmory-arsenal]: src/KSArmory/Sim/Arsenal.cs
[^ksarmory-assets]: src/KSArmory/KSArmoryAssets.xml
[^ksarmory-gamedata]: src/KSArmory/KSArmoryGameData.xml
[^ksarmory-catalogue]: src/KSArmory/Sim/Catalogue.cs
[^ksarmory-dispenser]: src/KSArmory/Sim/Dispenser.cs
[^ksarmory-decoy]: src/KSArmory/Sim/Decoy.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-seekerlock]: src/KSArmory/Sim/SeekerLock.cs
[^ksarmory-chaffnotch]: src/KSArmory/Sim/ChaffNotch.cs
[^ksarmory-signature]: src/KSArmory/Sim/Signature.cs
[^ksarmory-decoyeffects]: src/KSArmory/Ksa/DecoyEffects.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-uisystem]: src/KSArmory/Ksa/Ui/UiSystem.cs
[^ksa-program]: KSA.Program (decompiled)
[^ksa-universe]: KSA.Universe (decompiled)
[^ksa-parttree]: KSA.PartTree (decompiled)
[^ksa-part]: KSA.Part (decompiled)
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-celestial]: KSA.Celestial (decompiled)
[^ksa-iparentbody]: KSA.IParentBody (decompiled)
