---
type: How-To
title: Weapon registry keyed by part Id, with profiles in a game-free layer
description: Describe parts as profile objects keyed by Id, freeze a registry after load, find them on every craft by exact Id and keep each system keyed by its Part reference
category: vehicles-and-parts
group: craft-tracking
topic: give-inert-parts-behaviour-by-part-id
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/weapon-registry-and-profiles]
techniques: [data-driven-registry]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Part, KSA.PartTemplate, KSA.PartTree, KSA.Vehicle, KSA.ModLibrary, KSA.ModEntry]
tags: [parts, registry, part-id, weapons, survey]
---
Outcome: nothing in your fire control, guidance or game binding names a particular part. A profile object describes a part (a launcher, its round, its sensor), a registry holds the profiles, and the mod finds a system on
any craft by matching each part's Id. Adding a new weapon is a profile entry and a part, never new code in the loop.

Prerequisite: StarMap. Demonstrated by [ksarmory/weapon-registry-and-profiles](../../../../mods/ksarmory/features/weapon-registry-and-profiles.md). Making the parts themselves is [Define a part with Assets and GameData XML and a GLB model](../../parts/define-a-part-with-game-data/overview.md).

## Background: how the game does it

KSA cannot register a new part module without patching the engine, so the part is just structure with mass and a collider ([overview](overview.md)). The part Id is therefore the only thing that says what it is.
The craft exposes its parts through `vehicle.Parts.Parts`, and a surveyor can read each part's `Id`, `PositionVehicleAsmb`, `Asmb2VehicleAsmb` and whether its template connectors are surface connectors.

## Lifecycle and timing

| Step | Hook | Phase |
|---|---|---|
| Built-ins, pack registration, `Freeze()` and audit | `[StarMapAllModsLoaded]` (`OnFullyLoaded`) | `startup:all-mods-loaded`, a postfix on `ModLibrary.LoadAll`; part templates exist to audit against |
| Survey and crew the roster | `Ui.Draw` -> `RefreshSystems` (every 60th call) -> `_roster.Sync` | `frame:draw-ui`, `[StarMapAfterGui]` after `StepOnce`; skipped while the HUD is hidden |
| Per-frame resolution | `WeaponSystem.SampleWorld` from `StepSimulation` | same hook as the simulation step; reads part trees applied this frame |

Freeze before anything is crewed: a registry that grows while systems exist would resize a magazine under a launcher mid-salvo. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define profiles with no game types.** `LauncherProfile` (key `PartId`) names its `Munition` and `Sensor` and carries geometry markers and drive rates; `ComponentProfile` (key `PartId`) says which `WeaponRole` the part has. Put them in a layer that does not reference KSA so
   they can be tested headlessly. A pack or a built-in entry looks like:

```xml
<!-- src/KSArmory/KSArmory/Weapons.xml L92-L95 -->
<Launcher PartId="KSArmory_Prefab_Launcher6" DisplayName="Pantsir-S1"
          Munition="57E6" Sensor="1RS1"

          TurretMarker="Turret" PodsMarker="Pods" RadarMarker="Radar" GunsMarker="Guns"
```

2. **Hold them in a registry that closes.** Built-ins are a hand-written list; the live `Catalogue` starts as a copy and accepts registrations while open, rounds and sensors before launchers, refusing key collisions and any launcher that names a refused key. `Freeze()` shuts it:

```csharp
// src/KSArmory/Sim/Catalogue.cs L70-L96
internal static PackResult Register(PackContents pack)
{
    List<PackFault> faults = [.. pack.Faults];

    if (!_open)
    {
        faults.Add(new PackFault(pack.Source, "WeaponPack", "",
                                 "registered after the roster was built; nothing can be added now"));
        return Record(new PackResult(pack.Source, 0, faults));
    }

    HashSet<string> refused = [];
    int registered = 0;
    registered += Merge(pack.Munitions, _munitions, m => m.Name, "Munition", pack.Source, faults, refused);
    registered += Merge(pack.Sensors, _sensors, s => s.Name, "Sensor", pack.Source, faults, refused);
    registered += Merge(pack.Optics, _optics, o => o.PartId, "Optic", pack.Source, faults, refused);
    foreach (OpticProfile head in pack.Optics) _packOf.TryAdd(head.PartId, pack.Source);

    foreach (LauncherProfile launcher in pack.Launchers)
    {
        if (Taken(launcher, refused) is { } lost)
        {
            faults.Add(new PackFault(pack.Source, "Launcher", launcher.PartId,
                                     $"names '{lost}', which something else already claims"));
            continue;
        }

```

3. **Find parts on a craft by exact Id.** Exact, not substring: a substring over a whole craft would match a player's own parts. Tolerate a half-built tree, which happens during staging and docking:

```csharp
// src/KSArmory/Ksa/LauncherPart.cs L60-L78
public static void FindAll(Vehicle vehicle, List<(Part Part, LauncherProfile Profile)> into)
{
    into.Clear();
    try
    {
        ReadOnlySpan<Part> parts = vehicle.Parts.Parts;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] is { } part && Catalogue.LauncherForPart(part.Id) is { } profile)
            {
                into.Add((part, profile));
            }
        }
    }
    catch
    {
        // Part tree can be mid-rebuild during staging or docking.
    }
}
```

4. **Survey a craft, then decide what it is.** Flatten the craft to plain records (`Id`, pose, whether the part only rides on a surface connector) so the decision is pure C#:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L4977-L4998
public static void SurveyParts(Vehicle? vehicle, List<SurveyedPart> into)
{
    into.Clear();
    if (!IsAlive(vehicle)) return;

    try
    {
        ReadOnlySpan<Part> parts = vehicle!.Parts.Parts;
        for (int i = 0; i < parts.Length; i++)
        {
            Part part = parts[i];
            into.Add(new SurveyedPart(part.Id, part.PositionVehicleAsmb,
                                      part.Asmb2VehicleAsmb, PartRides(part)));
        }
    }
    catch
    {
        // A tree being rebuilt underneath the read, during staging or docking. Next frame sees
        // the finished one; reporting a half-built craft would be worse than reporting none.
        into.Clear();
    }
}
```

```csharp
// src/KSArmory/Sim/WeaponSurvey.cs L151-L154
public bool IsWeaponSystem => HasPlatform
                              && (CountOf(WeaponRole.Launcher) > 0
                                  || CountOf(WeaponRole.Gun) > 0
                                  || CountOf(WeaponRole.FireControl) > 0);
```

5. **Keep a roster keyed by the part, not by the ordinal.** One `WeaponSystem` per launcher part, keyed `(Vehicle, ordinal)` in part-tree order for first crewing, then held by the exact `Part` reference. On each `Sync`, an entry whose system lost its launcher looks for the same `Part` object on every
   other craft (a split carries parts whole), then by `PartId`; the entry is moved, not recreated, so magazine, rounds in flight and settings come along. A dead craft's system goes loose and flies its rounds until the last lands.

## Complete example

- [src/KSArmory/Sim/Catalogue.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Catalogue.cs#L1-L120)[^ksarmory-catalogue] and [src/KSArmory/Sim/WeaponSurvey.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/WeaponSurvey.cs#L100-L160)[^ksarmory-weaponsurvey] - registry and survey.
- [src/KSArmory/Ksa/LauncherPart.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LauncherPart.cs#L55-L80)[^ksarmory-launcherpart] and [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4943-L4998)[^ksarmory-ksaworld] - part discovery and the survey read.
- The roster (`WeaponSystems.cs` under `src/KSArmory/Ksa/`) is cited by [the mod feature](../../../../mods/ksarmory/features/weapon-registry-and-profiles.md).

## Variations and gotchas

- **Two registries for C# entries.** A launcher added to the built-in list without a matching component profile loads, matches its part and is then invisible to the panel; a consistency test guards it, and pack XML avoids it because the reader mints the component row.
- **Craft-level exact match, subpart-level substring.** Subpart markers are substrings within one part, first hit wins, and the audit flags zero or several matches.
- **Crewing needs the HUD.** Survey and `Sync` run from the GUI pass, so a craft built or split while the HUD is hidden is crewed when it returns.
- **Part Id is a name, not a type.** If craft files may name part instances, `Part.Id` is that name; key on `Part.Template.Id` if you cannot control it.
- **Settings keyed by craft display name collide** between craft from one blueprint; the roster warns.

## See also

- [Overview](overview.md); [Article 2](give-inert-parts-behaviour-by-part-id-2.md); [Article 3](give-inert-parts-behaviour-by-part-id-3.md)
- [ksarmory/weapon-registry-and-profiles](../../../../mods/ksarmory/features/weapon-registry-and-profiles.md)
- [Track which craft carry your part](../track-and-classify-craft/overview.md), [Pick and gate targets for automated weapons](../target-and-gate-automated-weapons/overview.md)

[^ksarmory-catalogue]: KSArmory: src/KSArmory/Sim/Catalogue.cs
[^ksarmory-weaponsurvey]: KSArmory: src/KSArmory/Sim/WeaponSurvey.cs
[^ksarmory-launcherpart]: KSArmory: src/KSArmory/Ksa/LauncherPart.cs
[^ksarmory-ksaworld]: KSArmory: src/KSArmory/Ksa/KsaWorld.cs
