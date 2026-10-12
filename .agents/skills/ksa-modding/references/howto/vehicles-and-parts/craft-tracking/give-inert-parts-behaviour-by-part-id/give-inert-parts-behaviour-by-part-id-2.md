---
type: How-To
title: Decide the dispenser kind by which part is fitted, and simulate flares and chaff as decoys
description: Recognise dispenser parts by Id on any craft, keep a magazine per Part, queue salvos on simulated time and step mod-side decoys that seduce seekers and break radar tracks
category: vehicles-and-parts
group: craft-tracking
topic: give-inert-parts-behaviour-by-part-id
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/countermeasure-dispensers]
techniques: [part-id-lookup, reference-keyed-dictionary]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Part, KSA.PartTemplate, KSA.PartTree, KSA.Vehicle, KSA.ModLibrary, KSA.ModEntry]
tags: [parts, part-id, decoys, flares, chaff, countermeasures]
---
Outcome: a part recognised only by Id becomes a flare or chaff dispenser: it keeps a magazine per part, throws decoys from its mounting face on a button press or an automatic missile warning, and the decoys are mod-simulated points that
fall through the air and seduce seekers. Which kind of dispenser a craft has is decided in the editor by which parts are fitted, not by a setting.

Prerequisite: StarMap. Demonstrated by [ksarmory/countermeasure-dispensers](../../../../mods/ksarmory/features/countermeasure-dispensers.md). Article 1 covers the registry idea; this is the same pattern with a different consumer.

## Background: how the game does it

The parts are plain XML: a `<Part>` prefab with one subpart and one surface connector in `KSArmoryAssets.xml`, plus a `<PartGameData>` with editor tags, a `ToSurface` connector, a mass and a box collider. There is no dispenser module, so the mod looks the part Id up against
`Arsenal.Dispensers`. The mesh's `+X` is out of the mounting face and is the eject direction. Decoys are not game objects: nothing is spawned, so there is no solver-window hazard ([Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md)).

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

## Lifecycle and timing

The mod's `StepSimulation` runs from `StepOnce` under `[StarMapAfterGui]` (`frame:draw-ui`), or with the UI hidden from a Harmony prefix on `Program.OnFrameCelestials` (`frame:celestials`), or from `[StarMapAfterOnFrame]` as a fallback (`frame:after-on-frame`). All run
after `PrepareFrame` applied the vehicle step and dispatched the next one, so craft position, velocity and part transforms used for ejection are the state at that time ([game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).

| Call | Gate |
|---|---|
| `_countermeasures?.Sync(KsaWorld.Vehicles)` | every frame, before the clock gate |
| `_countermeasures?.Update(step, _airborne)`: step decoys, auto-dispense, release salvos | simulated step only |
| radars run `ChaffNotch`, rounds run `SeekerLock` against `Countermeasures.Live` | simulated step only |
| `_decoyEffects.Update(Countermeasures.Live)` (particles) | every frame, in the tail |

The ordering is the point: decoys are stepped after the airborne sample and before any round is flown, so a seeker reads each decoy at the end of the step it is about to integrate across.

## Steps

1. **Discover dispensers on every craft by Id, and key them by `Part`.** A split carries a dispenser onto another craft with its count. A craft whose tree threw while being read is marked unread and its dispensers are kept, so a mid-rebuild frame does not refill them:

```csharp
// src/KSArmory/Ksa/Countermeasures.cs L71-L78,L82-L101,L105-L114
for (int i = 0; i < craft.Count; i++)
{
    Vehicle v = craft[i];
    if (!KsaWorld.IsAlive(v) || !KsaWorld.HasPlatform(v)) continue;

    if (!FindAll(v, _scratch))
    {
        _unread.Add(v);
// ...
    for (int ordinal = 0; ordinal < _scratch.Count; ordinal++)
    {
        Part part = _scratch[ordinal].Part;
        _seen.Add(part);

        if (_entries.TryGetValue(part, out Entry? known))
        {
            if (!ReferenceEquals(known.Craft, v))
            {
                Log.Info($"a countermeasures dispenser went with its part onto {KsaWorld.DisplayName(v)}, "
                         + $"{known.Dispenser.Remaining} left");
            }

            known.Craft = v;
            known.Ordinal = ordinal;
            continue;
        }

        _entries[part] = new Entry(part, v, ordinal, new Dispenser(_scratch[ordinal].Profile));
        Log.Info($"fitted a countermeasures dispenser on {KsaWorld.DisplayName(v)}");
// ...
// Gone only from a craft whose parts were read: one mid-rebuild is not one without them, and
// dropping its dispensers would refill them on the next frame.
_stale.Clear();
foreach (Entry e in _entries.Values)
{
    if (_seen.Contains(e.Part) || (_unread.Contains(e.Craft) && KsaWorld.IsAlive(e.Craft))) continue;
    _stale.Add(e.Part);
}

foreach (Part part in _stale) _entries.Remove(part);
```

2. **Map Id to profile.**

```csharp
// src/KSArmory/Sim/Catalogue.cs L200-L211
/// <summary>The dispenser a part Id names, or null if it names none.</summary>
public static DispenserProfile? DispenserForPart(string? partId)
{
    if (string.IsNullOrEmpty(partId)) return null;

    foreach (DispenserProfile d in Arsenal.Dispensers)
    {
        if (d.PartId == partId) return d;
    }

    return null;
}
```

3. **Queue a salvo, release on simulated time.** A press queues `SalvoSize` on every dispenser of that kind aboard; `Dispenser.Update` releases one cartridge per `SalvoIntervalSeconds` of simulated time, so spacing is frame-rate independent and a pause holds the salvo:

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

4. **Eject from the mounting face.** Place the decoy 0.2 m out along the part's eject axis using `Part.PositionVehicleAsmb` and `Part.Asmb2VehicleAsmb`, convert to ECL through the vehicle's `Asmb2Ego` and centre of mass, and add the craft velocity plus the eject speed:

```csharp
// src/KSArmory/Ksa/Countermeasures.cs L262-L285
private void Eject(Entry e)
{
    if (Catalogue.DispenserForPart(e.Part.Id) is not { } profile) return;

    try
    {
        Part part = e.Part;

        double3 faceAsmb = part.PositionVehicleAsmb + (part.Asmb2VehicleAsmb * (profile.EjectDirection * 0.2));
        double3 at = KsaWorld.VehicleAsmbToEcl(e.Craft, faceAsmb);
        double3 outward = Vec.Unit(KsaWorld.VehicleAsmbDirectionToEcl(e.Craft,
                                                                        part.Asmb2VehicleAsmb * profile.EjectDirection));
        double3 velocity = KsaWorld.VelocityEcl(e.Craft) + (outward * profile.EjectSpeed);

        DecoyProfile decoy = Catalogue.DecoyNamed(profile.Decoy);
        _live.Add(new Decoy(decoy, at, velocity, e.Craft) { Body = KsaWorld.ParentBody(e.Craft) });
        Log.Debug(() => $"  {decoy.DisplayName} out of {profile.DisplayName} at {profile.EjectSpeed:F0} m/s, "
                        + $"{_live.Count} decoy(s) in the air");
    }
    catch (Exception ex)
    {
        Log.Warn($"a {e.Dispenser.Kind} could not be dispensed: {ex.Message}");
    }
}
```

5. **Simulate the decoy.** Sub-steps of at most 20 ms; drag proportional to the velocity relative to the ground-moving air, applied semi-implicitly because chaff's drag is stiff:

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

6. **Let consumers judge each decoy once.** A missile seeker considers each decoy of its band once, the first frame its lock strength beats the target's, and is seduced with a probability scaled by the round's countermeasure resistance. A tracking radar loses a beaming target to chaff inside its Doppler notch:

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

## Complete example

- [src/KSArmory/Ksa/Countermeasures.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Countermeasures.cs#L66-L190)[^ksarmory-countermeasures] - sync, update, eject.
- [src/KSArmory/Sim/Dispenser.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Dispenser.cs#L1-L80)[^ksarmory-dispenser] and [src/KSArmory/Sim/Decoy.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Decoy.cs#L100-L136)[^ksarmory-decoy] - profile, salvo queue and flight.
- [src/KSArmory/Sim/SeekerLock.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SeekerLock.cs#L60-L100)[^ksarmory-seekerlock] - seduction.

## Variations and gotchas

- **No part module.** A pack that reuses the part Id without a profile gets an inert part.
- **Key by `Part`, not by craft and index.** A split renumbers parts; the reference survives and keeps the remaining count.
- **Judge once, not per frame.** A per-frame roll makes a flare's effect depend on frame rate.
- **Static live list.** `Countermeasures.Live` is shared with radars and seekers; clear it on save load so decoys from a replaced world do not linger.
- **Decoys are not contacts.** They break tracks through the notch and seduce seekers but are never targets themselves.
- **Constants are estimates.** The author's comments call the drag and seduction numbers "order of" and "chosen rather than measured".

## See also

- [Overview](overview.md); [Article 1](give-inert-parts-behaviour-by-part-id-1.md)
- [ksarmory/countermeasure-dispensers](../../../../mods/ksarmory/features/countermeasure-dispensers.md)
- [Track which craft carry your part](../track-and-classify-craft/overview.md)

[^ksarmory-countermeasures]: KSArmory: src/KSArmory/Ksa/Countermeasures.cs
[^ksarmory-dispenser]: KSArmory: src/KSArmory/Sim/Dispenser.cs
[^ksarmory-decoy]: KSArmory: src/KSArmory/Sim/Decoy.cs
[^ksarmory-seekerlock]: KSArmory: src/KSArmory/Sim/SeekerLock.cs
