---
type: Mod Feature
title: IFF teams, alliances and the per-frame team roster
description: KSA has no sides, so the mod keeps its own team names, puts a craft on a team through the flag on its panel row, rebuilds a reference-keyed craft-to-team roster every simulated step, and classifies every contact as friendly, hostile, neutral or unknown against per-team rules of engagement
mod: ksarmory
category: vehicles-and-parts
techniques: [starmap-lifecycle, harmony-prefix, imgui-window, reference-keyed-dictionary, per-frame-census]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Vehicle, KSA.Universe, KSA.Program]
tags: [iff, teams, allegiance, rules-of-engagement, roster, imgui, sides]
---

KSA has no notion of sides, so KSArmory brings its own Identification Friend or Foe. The player
declares team names, puts a craft on one with the flag button on its panel row, and sets each
team's relations (allied, neutral, hostile toward every other team) and rules (fire on unknown,
fire on neutrals, never fire on friendlies) in a Teams window. Every radar classifies each
contact against its own system's policy, and a mod round in flight carries its shooter's team, so
a launcher's own salvo reads as friendly to its side.[^ksarmory-iff][^ksarmory-uiteams]

The reusable idea is the two-sided allegiance: a team assignment is both "the side this
installation fights for" and "the side everyone else sees this craft on". The second half is a
`TeamRoster` keyed by `Vehicle` **reference**, rebuilt every simulated step from the live
policies, so no dictionary ever outlives a destroyed craft.[^ksarmory-teamroster]

## How it works

**Data.** Three layers, all in `Sim/` (no KSA types; unit-tested in `IffTests.cs`):[^ksarmory-ifftests]

| Type | Scope | Holds |
|---|---|---|
| `Config.TeamNames` | Session | Declared team names; `Teams.Declare` trims and dedupes case-insensitively |
| `TeamRules` (`Config.RulesFor(team)`, `Config.NoTeamRules`) | Per team | `Allies`, `Neutrals`, `EngageUnknown` (true), `EngageNeutral` (false), `ProtectFriendly` (true) |
| `IffPolicy` (`SystemConfig.Iff`) | Per installation | `OwnTeam` plus a copy of its team's rules |

**How a craft gets a team.** Only craft carrying something of this mod's (a weapon system or an
optical director) have a flag. `Ui.SetTeam` writes `OwnTeam` on every weapon system and director on
that row's craft; left-click opens a menu with every team, "No team" and a field to create one;
right-click steps to the next team (`Teams.Next`, wrapping, case-insensitive). A drone or airliner
with nothing fitted is on no team whatever it is called.[^ksarmory-ui]

**Roster rebuild.** `KSArmoryMod.CollectTeams` clears `KsaWorld.TeamRoster` and, for every crewed
weapon system, `Declare(craft, OwnTeam, ordinal)` then `RulesFor(OwnTeam).ApplyTo(policy.Iff)`;
directors declare at `TeamRoster.DirectorRank` (`int.MaxValue`). When a craft's installations
disagree, the **lowest rank wins** (first launcher), because enumeration order is a dictionary's and
would make allegiance unreproducible.[^ksarmory-mod][^ksarmory-teamroster]

**Whose side a contact is on.**

- `VehicleContact.DeclaredTeam` = `KsaWorld.TeamRoster.For(vehicle)`.[^ksarmory-contact]
- `RoundContact.DeclaredTeam` = the shooter's `WeaponSystem.Team`, captured by `CollectAirborne`, so
  allegiance survives the launcher's death (a loose system keeps its policy).[^ksarmory-roundcontact]
- `Radar.Consider` reads only `DeclaredTeam`; a name never decides a side.[^ksarmory-radar]

**Classification** (`IffPolicy.Classify`), in this order:

1. Blank team: `Unknown`.
2. In `NeutralTeams`: `Neutral` (neutral outranks allied).
3. In `AlliedTeams`: `Friendly`.
4. Own team blank: `Unknown` (a battery with no side classifies nothing).
5. Same name as `OwnTeam` (case-insensitive): `Friendly`, else `Hostile`.

**Engagement** (`IffPolicy.MayEngage`): Friendly only if `!ProtectFriendly`; Hostile always; Neutral
if `EngageNeutral`; Unknown if `EngageUnknown`. The defaults engage everything not friendly, so a
world with no teams still fights; turning off "Fire on unknown craft" gives hostiles-only.

Where it is enforced:

| Place | Effect |
|---|---|
| `Radar.Consider` | `IsThreat` requires `MayEngage(allegiance)`; the contact is still tracked and listed |
| `FireLadder.Holding` | Locked or designated target refused with "target is not engageable (IFF)"[^ksarmory-fireladder] |
| `UiSystem` track list, scope, lock cue | Allegiance colour and F/H/N/? mark |

**Relations are one-way.** Each team decides for itself what it makes of each other team (the
Teams window button steps hostile, allied, neutral). A coalition needs every member to list the
others.

**Removal and persistence.** `Ui.ForgetTeam` removes the name, calls `IffPolicy.Forget` on every
policy in the world and `Config.ForgetRules`. Team rules are not saved on their own: each system's
saved policy carries its team's copy, and on restore `WeaponSystems` re-declares the names
(`stored.DeclareTeams`) and `Config.AdoptRules` takes the first restored system's rules as the
team's.[^ksarmory-weaponsystems][^ksarmory-config]

## Lifecycle and timing

`CollectTeams` runs in `KSArmoryMod.StepSimulation` at L759, inside the gate that only runs on a
frame where `KsaWorld.ConsumeSimStep()` returned a new engine step, after `SampleWorld` and before
`CollectAirborne` (L763) and the fire loop (L776), so every round snapshot and every radar scan in
that step reads one roster.[^ksarmory-mod]

`StepSimulation` is reached through `StepOnce` from `[StarMapAfterGui]` (postfix on
`Program.OnDrawUiViewports`, Program.cs L2268), or with the UI hidden from the Harmony prefix on
`Program.OnFrameCelestials` (L2320), or from `[StarMapAfterOnFrame]` as a last fallback. All three
run after `PrepareFrame` applied the vehicle step (`Universe.ApplyVehicleSolvers`, Universe.cs L1922,
L1948) and dispatched the next (`ExecuteNextVehicleSolvers`, Program.cs L2220), so the craft the
roster keys on are the ones alive at `Universe.GetElapsedTime()` (Universe.cs L2399).[^ksa-program][^ksa-universe]

The team UI runs in the same `[StarMapAfterGui]` call, after `StepOnce` returned, inside
`if (DrawUI)` (Program.cs L2265-L2269). A flag change made there is applied to `OwnTeam`
immediately but reaches the roster on the next simulated step. On a paused game the roster is not
rebuilt (the gate is closed), and `ForgetTheWorld` clears it on a save load because
`DeserializeSave` destroys every craft it is keyed on.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Sim/Iff.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Iff.cs) | `Allegiance`, `Teams` (step, declare), `IffPolicy` (classify, may engage, forget) |
| [src/KSArmory/Sim/TeamRules.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TeamRules.cs) | Per-team relations and rules, `ApplyTo` and `TakeFrom` |
| [src/KSArmory/Sim/TeamRoster.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TeamRoster.cs) | Reference-keyed craft-to-team map with rank tie-break |
| [src/KSArmory/Sim/Config.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Config.cs#L225-L267) | `TeamNames`, `TeamRules`, `RulesFor`, `AdoptRules`, `ForgetRules` |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L1263-L1283) | `CollectTeams`: rebuild the roster, push team rules onto every policy |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L629-L640) | `KsaWorld.TeamRoster` static |
| [src/KSArmory/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/Ui.cs#L569-L652) | Flag button, team menu, `SetTeam`, `TeamOf` |
| [src/KSArmory/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/Ui.cs#L971-L987) | `ForgetTeam`, allegiance colours |
| [src/KSArmory/Ksa/Ui/UiTeams.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiTeams.cs) | Teams window: relations buttons, rules checkboxes, add/remove |
| [src/KSArmory/Ksa/Contact.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Contact.cs#L101-L111) | `VehicleContact.DeclaredTeam` from the roster |
| [src/KSArmory/Ksa/RoundContact.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/RoundContact.cs#L43-L63) | Round inherits its shooter's team |
| [src/KSArmory/Ksa/WeaponSystems.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystems.cs#L470-L480) | Restore: re-declare names, adopt rules |
| [tests/KSArmory.Tests/IffTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/IffTests.cs) | Free-for-all, coalitions, neutral precedence, flag stepping |

## Snippets

```csharp
// src/KSArmory/Sim/Iff.cs L113-L134
    /// <summary>Where a contact stands relative to <see cref="OwnTeam"/>.</summary>
    public Allegiance Classify(string? contactTeam)
    {
        if (string.IsNullOrWhiteSpace(contactTeam)) return Allegiance.Unknown;
        if (NeutralTeams.Contains(contactTeam)) return Allegiance.Neutral;
        if (AlliedTeams.Contains(contactTeam)) return Allegiance.Friendly;

        if (string.IsNullOrWhiteSpace(OwnTeam)) return Allegiance.Unknown;

        return string.Equals(contactTeam, OwnTeam, StringComparison.OrdinalIgnoreCase)
            ? Allegiance.Friendly
            : Allegiance.Hostile;
    }

    /// <summary>Whether a contact of this allegiance may be fired on.</summary>
    public bool MayEngage(Allegiance allegiance) => allegiance switch
    {
        Allegiance.Friendly => !ProtectFriendly,
        Allegiance.Hostile => true,
        Allegiance.Neutral => EngageNeutral,
        _ => EngageUnknown,
    };
```

```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L1263-L1283
    private void CollectTeams()
    {
        KsaWorld.TeamRoster.Clear();

        if (_roster is not null)
        {
            foreach (WeaponSystems.Entry e in _roster.All)
            {
                KsaWorld.TeamRoster.Declare(e.Craft, e.Policy.Iff.OwnTeam, e.Ordinal);
                _config.RulesFor(e.Policy.Iff.OwnTeam).ApplyTo(e.Policy.Iff);
            }
        }

        if (_heads is null) return;

        foreach (OpticalHeads.Entry h in _heads.All)
        {
            KsaWorld.TeamRoster.Declare(h.Head.Platform, h.Policy.Iff.OwnTeam, TeamRoster.DirectorRank);
            _config.RulesFor(h.Policy.Iff.OwnTeam).ApplyTo(h.Policy.Iff);
        }
    }
```

```csharp
// src/KSArmory/Sim/TeamRoster.cs L42-L52
    public void Declare(object? craft, string? team, int rank = 0)
    {
        if (craft is null || string.IsNullOrWhiteSpace(team)) return;
        if (_declared.TryGetValue(craft, out Membership held) && held.Rank <= rank) return;

        _declared[craft] = new Membership(team, rank);
    }

    /// <summary>The side this craft was put on, or null when nothing has said.</summary>
    public string? For(object? craft)
        => craft is not null && _declared.TryGetValue(craft, out Membership m) ? m.Team : null;
```

## Game APIs touched

KSA has no team, faction or allegiance API; this feature only keys on `Vehicle` identity.

| API | Decompiled file | Use |
|---|---|---|
| `KSA.Vehicle` (reference identity) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L29) | Roster key; never the display name[^ksa-vehicle] |
| `Universe.ApplyVehicleSolvers`, `Universe.GetElapsedTime` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1948) | Which craft exist when the roster is rebuilt (L2399) |
| `Program.OnDrawUiViewports`, `Program.DrawUI` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2269) | Where the flag and Teams window are drawn |

## Pitfalls and version notes

- **Both halves or neither.** Writing `OwnTeam` without declaring it into the roster leaves two
  sites on Blue reading each other as `Unknown`, which is engaged by default; likewise restoring
  memberships without re-declaring the session's team names (WeaponSystems.cs L474-L478).
- **Key by reference, not name.** Craft built from one blueprint share a display name.
- **Stale in-code comments.** `IContact.TeamKey` (Contact.cs L29-L36) is documented as the
  fallback when no team is declared, and `KsaWorld.TeamRoster`'s summary (KsaWorld.cs L629-L640)
  calls `Teams` "the helper that resolves a craft name to a side". Neither holds at this commit:
  `Radar.Consider` reads only `DeclaredTeam` and `Teams` only steps and declares names, so
  `TeamKey` is unused by classification.[^ksarmory-ksaworld]
- **Rules are pushed every step.** `CollectTeams` overwrites each policy's allies, neutrals and
  switches from its team's `TeamRules`, so per-system edits to those fields do not stick.
- **Neutral beats allied**, and a team listed in both is neutral (test `NeutralOutranksAllied`).
- **IFF does not hide.** Friendly and unknown contacts stay on the scope and in the track list; only
  `IsThreat` and the fire ladder consult `MayEngage`.

## See also

- [Radar search cone, masking and lock dwell](./radar-search-and-lock.md)
- [CPA threat classification](./cpa-threat-classification.md)
- [Fire control and target allocation](./fire-control-and-target-allocation.md)
- [Operator panel in ImGui](./operator-panel-imgui.md)
- [Per-craft settings JSON sidecar](./per-craft-settings-json-sidecar.md)
- [Detect a save load via OnGameLoaded](./detect-save-load-ongameloaded.md)
- [Optical director heads](./optical-director-heads.md)
- [Declare teams and alliances in a reference-keyed roster rebuilt each step](../../../howto/vehicles-and-parts/craft-tracking/track-and-classify-craft/track-and-classify-craft-2.md) - how-to article demonstrating this feature

[^ksarmory-iff]: src/KSArmory/Sim/Iff.cs
[^ksarmory-uiteams]: src/KSArmory/Ksa/Ui/UiTeams.cs
[^ksarmory-teamroster]: src/KSArmory/Sim/TeamRoster.cs
[^ksarmory-ifftests]: tests/KSArmory.Tests/IffTests.cs
[^ksarmory-ui]: src/KSArmory/Ksa/Ui/Ui.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-contact]: src/KSArmory/Ksa/Contact.cs
[^ksarmory-roundcontact]: src/KSArmory/Ksa/RoundContact.cs
[^ksarmory-radar]: src/KSArmory/Ksa/Radar.cs
[^ksarmory-fireladder]: src/KSArmory/Sim/FireLadder.cs
[^ksarmory-weaponsystems]: src/KSArmory/Ksa/WeaponSystems.cs
[^ksarmory-config]: src/KSArmory/Sim/Config.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksa-program]: KSA.Program (decompiled)
[^ksa-universe]: KSA.Universe (decompiled)
[^ksa-vehicle]: KSA.Vehicle (decompiled)
