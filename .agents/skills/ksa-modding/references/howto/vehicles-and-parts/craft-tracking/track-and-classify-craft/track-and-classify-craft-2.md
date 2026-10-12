---
type: How-To
title: Declare teams and alliances in a reference-keyed roster rebuilt each step
description: Rebuild a Vehicle-keyed team table every simulated step from live installations, classify contacts by declared team and never by craft name
category: vehicles-and-parts
group: craft-tracking
topic: track-and-classify-craft
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/iff-teams-and-allegiance]
techniques: [reference-keyed-dictionary, per-frame-census]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame]
game_types:
  - KSA.Vehicle
  - KSA.Universe
  - KSA.CelestialSystem
  - KSA.StructuralLoad
  - KSA.PhysicsBubble
  - KSA.Part
  - KSA.Program
tags: [teams, iff, allegiance, roster, reference-keyed]
---
Outcome: KSA has no notion of sides, so a mod can bring its own. KSArmory's team table is rebuilt every simulated step from the live installations, keyed by the `Vehicle` **reference**, so a craft can never inherit a team from its name,
no dictionary outlives a destroyed craft, and a round in flight carries its shooter's team.

Prerequisite: StarMap. Builds on the roster of [article 1](track-and-classify-craft-1.md). Demonstrated by [ksarmory/iff-teams-and-allegiance](../../../../mods/ksarmory/features/iff-teams-and-allegiance.md).

## Background: how the game does it

Nothing in the game identifies a craft's side; the only identity is the `Vehicle` object (and its `Id`, which craft built from one blueprint can share). The registry is therefore pure mod state: `Universe.CurrentSystem.All` supplies the craft, the mod supplies the table,
and anything keyed on the object disappears when the object does. See [overview](overview.md).

## Lifecycle and timing

`CollectTeams` runs inside the mod's `StepSimulation` only on frames where a new engine step was consumed, after the world sample and before the airborne census and fire loop, so every round snapshot and radar scan in that step reads one roster. `StepSimulation` is reached through `StepOnce`
from `[StarMapAfterGui]` (`frame:draw-ui`), with the UI hidden from a prefix on `Program.OnFrameCelestials`, or from `[StarMapAfterOnFrame]` as a last fallback (`frame:after-on-frame`); all run after `PrepareFrame` applied the vehicle step. The team UI runs in the same `[StarMapAfterGui]` call after `StepOnce`: a flag
change is applied to `OwnTeam` immediately but reaches the roster on the next step. The roster is cleared on a save load because `DeserializeSave` destroys every craft it is keyed on ([game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).

## Steps

1. **Key the table on references.** Declare the lowest rank first so the result does not depend on dictionary order (a craft whose installations disagree is a split carrying a part across, or settings saved before):

```csharp
// src/KSArmory/Sim/TeamRoster.cs L20-L53
public sealed class TeamRoster
{
    private readonly Dictionary<object, Membership> _declared = new(ReferenceEqualityComparer.Instance);

    private readonly record struct Membership(string Team, int Rank);

    /// <summary>The rank a director declares at, so any weapon aboard outranks it.</summary>
    public const int DirectorRank = int.MaxValue;

    /// <summary>Starts a fresh census. Every member has to be re-declared after this.</summary>
    public void Clear() => _declared.Clear();

    /// <summary>
    /// Records that <paramref name="craft"/> fights for <paramref name="team"/>. A null or blank
    /// team declares nothing, which leaves the craft on no team.
    ///
    /// <para><paramref name="rank"/> settles a craft whose installations disagree — the panel's
    /// flag sets every one of them together, so that is a split carrying a part across, or settings
    /// saved before. Lowest rank wins, which is the first launcher, because the roster
    /// enumerates in a dictionary's order and an allegiance decided by that is an unreproducible
    /// bug report.</para>
    /// </summary>
    public void Declare(object? craft, string? team, int rank = 0)
    {
        if (craft is null || string.IsNullOrWhiteSpace(team)) return;
        if (_declared.TryGetValue(craft, out Membership held) && held.Rank <= rank) return;

        _declared[craft] = new Membership(team, rank);
    }

    /// <summary>The side this craft was put on, or null when nothing has said.</summary>
    public string? For(object? craft)
        => craft is not null && _declared.TryGetValue(craft, out Membership m) ? m.Team : null;
}
```

2. **Rebuild it every step from the live installations.** Clear, declare each crewed system's team (directors at the lowest priority), and push the team's rules onto each policy:

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

3. **Classify with a pure function of two names and a policy.** A blank team is unknown; neutral outranks allied; same name as the own team is friendly; anything else is hostile. Engagement is a second function so rules (fire on unknown, fire on neutrals, protect friendlies) stay data:

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

4. **Let consumers read only the declared team.** A contact's team comes from `KsaWorld.TeamRoster.For(vehicle)`; a round carries its shooter's team captured when the airborne census is built, so allegiance survives the launcher's death. A radar never looks at a name.
5. **Give the craft a team through the UI, not by naming it.** Only craft carrying something of the mod's have a flag; a drone or airliner with nothing fitted is on no team.

## Complete example

- [src/KSArmory/Sim/Iff.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Iff.cs#L1-L140)[^ksarmory-iff] (`Allegiance`, `Teams`, `IffPolicy`) and [src/KSArmory/Sim/TeamRoster.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TeamRoster.cs#L1-L53)[^ksarmory-teamroster].
- [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L1263-L1283)[^ksarmory-ksarmorymod] - `CollectTeams`.

## Variations and gotchas

- **Relations are one-way.** Each team decides for itself what it makes of each other team; a coalition needs every member to list the others.
- **Never parse allegiance from craft names**: craft from one blueprint share a name and the roster warns when a name is taken.
- **Paused game.** The roster is not rebuilt while the step gate is closed.
- **Reference keys only.** A `ReferenceEqualityComparer` dictionary prevents a rename or a duplicate display name from merging two craft.
- **Rank tie-break.** Without it, enumeration order of a dictionary would make allegiance an unreproducible bug.

## See also

- [Overview](overview.md); [Article 1](track-and-classify-craft-1.md)
- [ksarmory/iff-teams-and-allegiance](../../../../mods/ksarmory/features/iff-teams-and-allegiance.md)
- [Pick and gate targets for automated weapons](../target-and-gate-automated-weapons/overview.md), where the classification feeds engagement gates

[^ksarmory-iff]: KSArmory: src/KSArmory/Sim/Iff.cs
[^ksarmory-teamroster]: KSArmory: src/KSArmory/Sim/TeamRoster.cs
[^ksarmory-ksarmorymod]: KSArmory: src/KSArmory/Ksa/KSArmoryMod.cs
