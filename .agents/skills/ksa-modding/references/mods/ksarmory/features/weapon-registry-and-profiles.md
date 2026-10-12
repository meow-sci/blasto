---
type: Mod Feature
title: A weapon system is data plus art, discovered by part Id
description: Launchers, rounds, sensors, optics and craft components are plain profile objects in a registry that is open until all mods load and then frozen, and the mod crews one weapon system per matching launcher part it finds on any craft, following that part across splits and docks
mod: ksarmory
category: vehicles-and-parts
techniques: [data-driven-registry, part-id-discovery, sim-ksa-split, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.PartTree, KSA.Part, KSA.PartTemplate, KSA.Part.Connector]
tags: [registry, profiles, data-driven, part-id, discovery, survey, roster, decoupling]
---

Nothing in KSArmory's fire control, guidance, drives or game binding names a particular weapon.
A launcher, its round, its sensor, an optical head and the craft components the panel recognises
are all profile objects; the registry holds them; and the mod finds weapons in the world by
matching each part's `Id` against registered launcher part Ids. Adding a weapon system is a profile
entry (in C# or in a pack's XML) plus a part and its art.[^ksarmory-arsenal][^ksarmory-readme]

Because KSA cannot register a new part module without patching the engine, the part itself is
inert: KSA sees structure with mass and a collider, and the *part Id* is the only thing that says
what it is.[^ksarmory-weaponsurvey]

## How it works

**Profiles (all in `Sim/`, no KSA types):**

| Type | Key | What it carries |
|---|---|---|
| `LauncherProfile` | `PartId` | Names `Munition`, `Sensor`, optional `GunMunition`; subpart markers; generated geometry (`Tubes`, pivots, `GunMuzzles`); drive rates and travel limits; magazine and belt sizes; store masses; sounds[^ksarmory-launcherprofile] |
| `MunitionProfile` | `Name` | Body/fin markers or `BodyModel`; boost stages; `GuidanceMode` and seeker band; drag; fuse, charge, terrain; chute; step limits[^ksarmory-munitionprofile] |
| `SensorProfile` | `Name` | Range, cone, `BoresightMode`, lock time, emission, notch and chaff behaviour, terrain masking[^ksarmory-sensorprofile] |
| `OpticProfile` | `PartId` | Optical heads (separate parts, or riding a turret) |
| `ComponentProfile` | `PartId` | `WeaponRole` (Launcher, Gun, Sensor, Camera, FireControl, Guidance, Countermeasures) plus `Provides` rows for roles built into the part as subparts |
| `DecoyProfile`, `DispenserProfile` | `Name`, `PartId` | Flares and chaff |

**Two registries, deliberately apart.** `Arsenal` is the hand-written list of built-ins (eight
launchers, seven rounds, ten sensors, three optics, components). `Catalogue` is what every lookup
reads: it starts as a copy of `Arsenal`'s lists and accepts pack registrations while open
(`Register`, rounds and sensors before launchers, refusing key collisions and any launcher that
names a refused key). `Freeze()` shuts it; a later `Register` returns a fault instead.[^ksarmory-catalogue]
The Pantsir itself is not in `Arsenal.cs`: its round, sensor and launcher are in
`src/KSArmory/KSArmory/Weapons.xml`, read through the same pack path as a third party's.[^ksarmory-weapons-xml]

**Lookups.** `Catalogue.LauncherForPart(partId)` is an exact `PartId` match;
`LoadoutFor(launcher)` resolves round and sensor together; `MunitionNamed`/`SensorNamed` fall back
to the first entry rather than throwing (keeps a typo playable), while `TryMunitionNamed` returns
null for code that must judge a name.

**Craft survey (pure C#).** `KsaWorld.SurveyParts` flattens a craft into
`SurveyedPart(Id, PositionVehicleAsmb, Asmb2VehicleAsmb, Rides)`, where `Rides` is true when any of
the part template's connectors carries `ToSurface` or `FromSurface`.[^ksarmory-ksaworld]
`WeaponSurvey.Survey` exact-matches each Id against `Catalogue.Components`, adds one row per
`Provides`, and decides `IsWeaponSystem` (a launcher, gun or fire control, on a craft with at least
one non-riding part) versus a mere `IsInstallation` (anything recognised, e.g. a lone director).[^ksarmory-weaponsurvey]

**Roster (`WeaponSystems`).** One `WeaponSystem` per launcher *part*, keyed by
`(Vehicle, ordinal)` in part-tree order:[^ksarmory-weaponsystems]

1. `Sync` first runs `FollowDecoupledLaunchers`: an entry whose system lost its launcher looks for
   the exact `Part` object it held on every other craft (a split carries parts whole), then by
   `PartId` through `PlatformHandover.Choose`; the entry is moved, not recreated, so magazine, rounds
   in flight, settings and teams come along. After repeated fruitless searches it retires.
2. For each surveyed craft that `IsWeaponSystem`, `LauncherPart.FindAll` lists launcher parts; any
   ordinal not yet crewed (and whose part no entry already holds) gets a new system, its saved
   `SystemConfig` restored first, pinned to that craft, and `KsaWorld.EnsureControlPart` elects the
   launcher as control part if the craft has none.
3. `RetireDestroyed`: a dead craft's system first tries to follow its held part onto a surviving
   platform; otherwise it goes *loose*, flying its rounds in the air until the last lands.

Per frame, each system resolves its launcher by the held `Part` reference (falling back to the
ordinal until it holds one) and re-resolves subparts and loadout from the profile it found.[^ksarmory-weaponsystem]

**Role interfaces.** `WeaponSystemRoles.cs` splits the 4500-line `WeaponSystem` into narrow
interfaces (`IWeaponPlatform`, `IWeaponLoadout`, `IRoundsInFlight`, `IEffectSource`, `IManualFire`,
...), so an effect or camera depends on five members instead of fifty.[^ksarmory-weaponsystemroles]

## Lifecycle and timing

| Step | Hook | Notes |
|---|---|---|
| Built-ins | Static initialisation of `Arsenal` and `Catalogue` | Whenever first touched; no KSA state |
| Pack registration | Before and inside `[StarMapAllModsLoaded]` | Code packs from their own entry points; folder packs via `InstalledPacks.RegisterAll()` at the top of `OnFullyLoaded` (KSArmoryMod.cs L149-L157)[^ksarmory-mod] |
| Freeze and audit | `Catalogue.Freeze()` then `Catalogue.Audit(new DeclaredParts())`, still in `OnFullyLoaded` (L161-L166) | `startup:all-mods-loaded`, a postfix on `ModLibrary.LoadAll`, so part templates exist to audit against |
| Survey and crew | `Ui.Draw` -> `RefreshSystems` (every 60th call) -> `_roster.Sync` (Ui.cs L212-L213, L323-L337) | Called from `OnAfterGui` after `StepOnce` (KSArmoryMod.cs L524), i.e. the `[StarMapAfterGui]` postfix on `OnDrawUiViewports` (Program.cs L2268); skipped while the HUD is hidden (`DrawUI` guard, L2265)[^ksarmory-ui][^ksa-program] |
| Per-frame resolution | `WeaponSystem.SampleWorld` from `StepSimulation` | Same hook as the simulation step; reads part trees applied this frame |

Freezing before any system is crewed is the point: a registry that grows while systems are crewed
would resize a magazine under a launcher mid-salvo.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Sim/Arsenal.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Arsenal.cs#L1400-L1623) | Built-in lists, `Components`, `LauncherForPart`, `LoadoutFor`, `Named<T>` |
| [src/KSArmory/Sim/Catalogue.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Catalogue.cs) | Live registry: `Register`, `Freeze`, `Audit`, lookups |
| [src/KSArmory/Sim/LauncherProfile.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/LauncherProfile.cs) | The launcher data model |
| [src/KSArmory/Sim/MunitionProfile.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/MunitionProfile.cs), [src/KSArmory/Sim/SensorProfile.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SensorProfile.cs) | Round and sensor data models |
| [src/KSArmory/Sim/WeaponSurvey.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/WeaponSurvey.cs) | `WeaponRole`, `ComponentProfile`, inventory and survey |
| [src/KSArmory/Ksa/WeaponSystems.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystems.cs#L430-L790) | Roster sync, follow across splits, retire and loose |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L2955-L2975) | `ResolveLauncher` by held part, then ordinal |
| [src/KSArmory/Ksa/LauncherPart.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LauncherPart.cs#L32-L89) | `Find`, `FindAll`, `FindNth` over `Vehicle.Parts.Parts` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4905-L4998) | `HasPlatform`, `PartRides`, `SurveyParts` |
| [src/KSArmory/Ksa/WeaponSystemRoles.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystemRoles.cs) | Narrow role interfaces |
| [tests/KSArmory.Tests/ArsenalTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/ArsenalTests.cs) | Registry consistency tests |

## Snippets

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

```csharp
// src/KSArmory/Sim/Catalogue.cs L70-L111
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

            if (Merge([launcher], _launchers, l => l.PartId, "Launcher", pack.Source, faults, refused) == 0)
            {
                continue;
            }

            registered++;
            _packOf[launcher.PartId] = pack.Source;
            foreach (ComponentProfile component in pack.Components)
            {
                if (component.PartId == launcher.PartId) _components.Add(component);
            }
        }

        return Record(new PackResult(pack.Source, registered, faults));
    }
```

```csharp
// src/KSArmory/Sim/WeaponSurvey.cs L151-L154
    public bool IsWeaponSystem => HasPlatform
                                  && (CountOf(WeaponRole.Launcher) > 0
                                      || CountOf(WeaponRole.Gun) > 0
                                      || CountOf(WeaponRole.FireControl) > 0);
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `Vehicle.Parts` (`PartTree`), `PartTree.Parts` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L136) | Walk a craft's full parts in tree order |
| `Part.Id` (instance Id, else template Id) | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1484-L1494) | The discovery key |
| `Part.Template.Connectors`, `Part.Connector.TemplateBase.Flags`, `Part.Connector.Flag.ToSurface` / `FromSurface` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L194-L210) | Decide whether a part only rides (a store) or can be a platform |
| `Part.PositionVehicleAsmb`, `Part.Asmb2VehicleAsmb` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L708-L738) | Where each surveyed component sits |
| `Vehicle.ControlPart`, `Vehicle.SetControlPart` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2205-L2231) | Elect the launcher as control part when the craft has none (accepted only if its subtree has a `Control` module) |

## Pitfalls and version notes

- **Two registries for C# entries.** A launcher added to `Arsenal.Launchers` without a matching
  `ComponentProfile` in `Arsenal.Components` loads, matches its part and is then invisible to the
  panel, with nothing in any log; `ArsenalTests.EveryRegisteredLauncherIsAlsoARecognisedComponent`
  guards it. Pack XML avoids the trap: `PackReader` mints the component row for each launcher.[^ksarmory-claude-md][^ksarmory-arsenal-tests]
- **Doc vs code.** README "How it works" says `Arsenal.cs` and `Weapons.xml` register each launcher,
  and README step 3 of "Adding a weapon system" says to register in `Arsenal.cs`; the shipped Pantsir
  is defined only in `KSArmory/Weapons.xml` (CLAUDE.md L837-L838 says so). Both routes feed the same
  `Catalogue`.
- **Exact match for parts, substring for subparts.** Craft-level discovery is exact (`PartId ==`),
  because a substring across a whole craft would match a player's own parts; subpart markers are
  substrings within one part (see [articulated mounts](./articulated-mount-subparts.md)).
- **Crewing needs the HUD.** Survey and `Sync` run from the GUI pass, so a craft built or split while
  the HUD is hidden is crewed when the HUD returns; the survey also only refreshes every 60 draws.
- **Keys are references and ordinals.** Entries key on the `Vehicle` object; KSA rebuilds part trees
  on staging and docking, which is why a system holds its `Part` by reference once found and treats
  the ordinal as a fallback.
- **Settings collide by name.** Saved `SystemConfig` is keyed on the craft's display name (plus
  `#n` for later launchers), so craft from one blueprint share settings; the roster warns.

## See also

- [Weapon packs read from mod folders](./weapon-packs-from-mod-folders.md): how XML packs feed this registry
- [Armoury pack registration API](./armoury-pack-registration-api.md): the code entry point
- [Weapon part definitions in XML](./weapon-part-definitions-xml.md): the parts these Ids name
- [Articulated turrets by rewriting subpart transforms](./articulated-mount-subparts.md): what the markers drive
- [Per-craft settings JSON sidecar](./per-craft-settings-json-sidecar.md): where `SystemConfig` is restored from
- [Operator panel](./operator-panel-imgui.md): the UI pass that runs the survey
- [Weapon registry keyed by part Id, with profiles in a game-free layer](../../../howto/vehicles-and-parts/craft-tracking/give-inert-parts-behaviour-by-part-id/give-inert-parts-behaviour-by-part-id-1.md) - how-to article demonstrating this feature

[^ksarmory-arsenal]: src/KSArmory/Sim/Arsenal.cs
[^ksarmory-catalogue]: src/KSArmory/Sim/Catalogue.cs
[^ksarmory-launcherprofile]: src/KSArmory/Sim/LauncherProfile.cs
[^ksarmory-munitionprofile]: src/KSArmory/Sim/MunitionProfile.cs
[^ksarmory-sensorprofile]: src/KSArmory/Sim/SensorProfile.cs
[^ksarmory-weaponsurvey]: src/KSArmory/Sim/WeaponSurvey.cs
[^ksarmory-weaponsystems]: src/KSArmory/Ksa/WeaponSystems.cs
[^ksarmory-weaponsystemroles]: src/KSArmory/Ksa/WeaponSystemRoles.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-ui]: src/KSArmory/Ksa/Ui/Ui.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-weapons-xml]: src/KSArmory/KSArmory/Weapons.xml
[^ksarmory-readme]: README.md
[^ksarmory-claude-md]: CLAUDE.md (author notes)
[^ksarmory-arsenal-tests]: tests/KSArmory.Tests/ArsenalTests.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
