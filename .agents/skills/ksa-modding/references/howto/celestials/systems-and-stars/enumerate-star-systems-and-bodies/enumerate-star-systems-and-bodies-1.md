---
type: How-To
title: Roots then IParentBody.Children into an immutable forest, with role rules in a game-type-free class
description: Build an immutable forest of every star system by walking CelestialSystem.All for independent roots and recursing through IParentBody.Children, keeping only astronomical parents and classifying stars, barycenters and minor-body exoplanets by pure rules
category: celestials
group: systems-and-stars
topic: enumerate-star-systems-and-bodies
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-deltavmap/features/live-celestial-system-graph]
techniques: [system-graph]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.CelestialSystem
  - KSA.IIndependentRoot
  - KSA.IParentBody
  - KSA.IOrbiter
  - KSA.Astronomical
  - KSA.Celestial
  - KSA.StellarBody
  - KSA.Barycenter
  - KSA.OrbitingStar
  - KSA.MinorBody
tags: [forest, roots, children, barycenter, minor-body, role-rules]
---
Outcome: a data structure holding every star system, star, planet, moon, asteroid and comet of the loaded universe, with a role for each node and the other systems ordered by distance from home. Demonstrated by KSA-DeltaVMap's live system graph ([Live celestial system graph](../../../../mods/ksa-deltavmap/features/live-celestial-system-graph.md)). Caching it per loaded system is covered in [cache and key per-body data](../../../celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/overview.md).

## Background: how the game does it

`Universe.LoadSystem` itself builds `Universe.Roots` by enumerating `CurrentSystem.All.OfType<IIndependentRoot>()` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L248-L255)), so the same enumeration works on any `CelestialSystem` you are given. The independent roots in this build are `FixedStar` and `Barycenter`; an `OrbitingStar` is a `StellarBody` that is an `IOrbiter` under a barycenter. Each body's `Children` (`List<IOrbiter>` on `IParentBody`) mixes bodies and vehicles; vehicles are not an `IParentBody` and not an `Astronomical`.

## Lifecycle and timing

The mod builds the graph lazily from the draw hook (`[StarMapAfterGui]`, `frame:draw-ui`, main thread) on the first frame its window is shown after a system load, not at `Universe.OnLoaded`; while the window is closed nothing is read. Reading is safe there: bodies are created on the main thread by `Universe.LoadSystem`, celestial orbits are analytic Keplerian, and the mod only reads static elements plus the fixed root positions. The graph never holds vehicles. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Enumerate roots and recurse.** Take each `IIndependentRoot` that is an `Astronomical`, build a node, sort the roots with the home system first and the rest nearest first:

```csharp
// DeltaVMap/Model/SystemGraph.cs L133-L153
public static SystemGraph? Build(CelestialSystem system)
{
    var byId = new Dictionary<string, PhysicalNode>();
    var roots = new List<PhysicalNode>();
    foreach (IIndependentRoot root in system.All.OfType<IIndependentRoot>())
    {
        if (root is not Astronomical astro)
            continue;
        roots.Add(BuildNode(root, astro, parent: null, isMinor: false, byId));
    }
    if (roots.Count == 0)
        return null;

    PhysicalNode home = HomeRootOf(system, byId) ?? roots[0];
    double3 homePos = home.Astro.GetPositionEcl();
    roots.Sort((a, b) => ForestRules.CompareRoots(
        ReferenceEquals(a, home), (a.Astro.GetPositionEcl() - homePos).LengthSquared(), a.Id,
        ReferenceEquals(b, home), (b.Astro.GetPositionEcl() - homePos).LengthSquared(), b.Id));

    return new SystemGraph(system, roots, byId);
}
```

2. **Recurse through `Children`, keeping only astronomical parents.** Vehicles also live in `Children`; drop them by type. Track whether any child is a major (non-minor) `Celestial`:

```csharp
// DeltaVMap/Model/SystemGraph.cs L190-L203
// Every orbiting celestial extends the tree: planets, moons, comets (Celestial) and the
// stars of a multiple system (OrbitingStar). Vehicles also live in Children, but they are
// not an IParentBody, not destinations, and are skipped.
var childBodies = new List<IOrbiter>();
bool hasMajorPlanet = false;
foreach (IOrbiter child in body.Children)
{
    if (child is not IParentBody || child is not Astronomical)
        continue;
    childBodies.Add(child);
    if (child is Celestial and not MinorBody)
        hasMajorPlanet = true;
}
bool minorChildrenArePlanets = ForestRules.MinorChildrenArePlanets(role, hasMajorPlanet);
```

3. **Sort children once** so every consumer sees the same order: bound orbits by semi-major axis, open orbits (`Eccentricity >= 1`, interstellar comets) after all bound ones and ordered by periapsis, ties broken by `string.CompareOrdinal` on the Id.

4. **Classify with rules that know no game types**, so they unit-test offline. A `StellarBody` is a star; an independent root that is no star is a barycenter (`Barycenter.IsStar()` is false); minor-body children of a star or barycenter with no major planet are treated as that system's planets (stock authors exoplanets as `MinorBody`):

```csharp
// DeltaVMap/Model/ForestRules.cs L17-L38
// A StellarBody is a star. An independent root that is no star is a barycenter, which the
// game's own Astronomical.IsStar() does not count as a star (Barycenter.IsStar() is false).
public static BodyRole RoleOf(bool isStellarBody, bool isIndependentRoot)
{
    if (isStellarBody)
        return BodyRole.Star;
    return isIndependentRoot ? BodyRole.Barycenter : BodyRole.Body;
}

// The minor bodies directly under a star or barycenter that has no major planet are that
// system's planets, because stock authors every exoplanet as a MinorBody. Under a body, or
// next to a major planet (Ceres beside the planets of Sol), they stay minor.
public static bool MinorChildrenArePlanets(BodyRole parentRole, bool hasMajorPlanet)
{
    return parentRole != BodyRole.Body && !hasMajorPlanet;
}

public static bool IsMinor(bool isMinorBody, bool minorChildrenArePlanets)
{
    return isMinorBody && !minorChildrenArePlanets;
}

```

5. **Order the roots** home first, then by squared distance of `GetPositionEcl()` from home, with the Id as a stable tie-breaker:

```csharp
// DeltaVMap/Model/ForestRules.cs L39-L47
// The order of the system roots: the home system first, then the others nearest to it, with
// the Id as a stable tie-breaker.
public static int CompareRoots(bool aIsHome, double aDistanceSquared, string aId, bool bIsHome, double bDistanceSquared, string bId)
{
    if (aIsHome != bIsHome)
        return aIsHome ? -1 : 1;
    int byDistance = aDistanceSquared.CompareTo(bDistanceSquared);
    return byDistance != 0 ? byDistance : string.CompareOrdinal(aId, bId);
}
```

6. **Find the home root** with `IIndependentRoot.RootOf(system.HomeBody)`. Root positions of a `FixedStar` and a `Barycenter` are stored in `readonly` fields set from the template, so the distance between two star systems never changes for the loaded system and per-source destination lists can be cached.

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Model/SystemGraph.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/SystemGraph.cs) | `PhysicalNode`, `SystemGraph.Build`, root ordering, destination cache |
| [DeltaVMap/Model/ForestRules.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/ForestRules.cs) | Game-type-free role, minor-body and root-order rules |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1740-L1804) | `EnsureBuilt` |

## Variations and gotchas

- **`IsMoon()` misfires under barycenters:** test the parent type instead.
- **Hubs have no surface.** Stars and barycenters are hub-only; do not give them a ladder of surface rungs.
- **Caps.** The map draws at most eight other systems individually (`SystemGraph.MaxShownSystems`).
- **The comment on stock exoplanets** (authored as `MinorBody`) is the mod's claim; it was not checked against the stock content files.

## See also

- [Overview](overview.md); [approach 2](enumerate-star-systems-and-bodies-2.md); [approach 3](enumerate-star-systems-and-bodies-3.md)
- [Live celestial system graph](../../../../mods/ksa-deltavmap/features/live-celestial-system-graph.md)
- [Cache and key per-body data per loaded system](../../../celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/overview.md)
