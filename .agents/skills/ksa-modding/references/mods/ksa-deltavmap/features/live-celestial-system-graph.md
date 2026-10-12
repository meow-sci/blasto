---
type: Mod Feature
title: Live celestial forest of every star system, cached per loaded system
description: Builds one physical tree per independent root (fixed star or barycenter) straight from the loaded CelestialSystem, reads every body's mass, radius, SOI, orbit and atmosphere once, and rebuilds only when Universe.CurrentSystem changes instance
mod: ksa-deltavmap
category: celestials
techniques: [system-graph, per-system-cache]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Universe
  - KSA.CelestialSystem
  - KSA.IIndependentRoot
  - KSA.IParentBody
  - KSA.IOrbiter
  - KSA.Astronomical
  - KSA.StellarBody
  - KSA.FixedStar
  - KSA.Barycenter
  - KSA.OrbitingStar
  - KSA.Celestial
  - KSA.MinorBody
  - KSA.Comet
  - KSA.Orbit
  - KSA.AtmosphereReference
tags: [celestials, star-systems, barycenter, soi, orbits, caching, multi-star]
---

The map works with any universe, stock or modded, single-star or multi-star, with no per-system
setup: it reads the loaded `CelestialSystem` live, every star system, star, planet, moon,
asteroid and comet, and nothing is hardcoded. Mechanism: `SystemGraph.Build(CelestialSystem)`
walks `system.All.OfType<IIndependentRoot>()` and then each body's `IParentBody.Children`
recursively into an immutable forest of `PhysicalNode`s, computing every body's delta-v ladder
once; `MapWindow.EnsureBuilt` keeps that forest (and the transfer cost cache) until
`Universe.CurrentSystem` is a different object.[^dvmap-systemgraph][^dvmap-mapwindow]

## How it works

**Roots.** One `CelestialSystem` can hold several star systems. The game's own
`Universe.LoadSystem` builds `Universe.Roots` by enumerating
`CurrentSystem.All.OfType<IIndependentRoot>()`; the mod uses the same enumeration on the system it
is given, so the graph can be built for a `CelestialSystem` that is not the current
one.[^ksa-universe] In build 2026.10.10.5554 the independent roots are `FixedStar` and
`Barycenter` (both implement `IIndependentRoot`); `OrbitingStar` is a `StellarBody` that is an
`IOrbiter` under a barycenter.[^ksa-fixedstar][^ksa-barycenter]

**Recursion.** `BuildNode` creates a `PhysicalNode` per body and recurses into
`body.Children` (`List<IOrbiter>` on `IParentBody`), keeping only children that are both
`IParentBody` and `Astronomical`, which drops vehicles (they also live in `Children`).[^ksa-iparentbody]
Children are sorted once so every consumer sees the same order: bound orbits by semi-major axis,
open orbits (`Eccentricity >= 1`, interstellar comets) after all bound ones and ordered by
periapsis, ties broken by `string.CompareOrdinal` on the Id.

**Roles.** `ForestRules` keeps the structural decisions free of game types so they can be unit
tested offline: a `StellarBody` is a `Star`, an `IIndependentRoot` that is not a star is a
`Barycenter` (the mod notes the game's `Barycenter.IsStar()` is false), everything else is a
`Body`. Stars and barycenters are hub-only (no surface). `MinorBody` children of a star or
barycenter that has no major planet are treated as that system's planets, otherwise as minor
bodies; the mod's comment gives the reason that stock authors exoplanets as `MinorBody`
(not checked against the stock content files).[^dvmap-forestrules]

**Per-body physics, read once.** Each node carries a `BodyLadder` built by
`OrbitalStates.BuildLadder` (or `BuildHubLadder` for stars and barycenters), which reads:[^dvmap-orbitalstates]

| Game member | Used for |
|---|---|
| `IParentBody.Mu` (`Mass * 6.6743E-11`) | Every vis-viva and Oberth formula |
| `IRadius.MeanRadius` | Surface rung, gas-giant heuristic (15,000 km with atmosphere) |
| `IParentBody.SphereOfInfluence` | SOI-edge rung, "can hold an orbit" test, hub parking radius clamp |
| `IParentBody.GetAtmosphereReference()` -> `Physical.Height`, `Physical.SeaLevelDensity` | Low-orbit altitude, ascent loss factor, landing fraction, aerobrake marker |
| `IParentBody.GetNearSurfaceRadius()` | Low-orbit clearance over terrain |
| `IParentBody.GetAngularVelocity()` | Stationary (synchronous) orbit rung |
| `IParentBody.BodyTemplate.RingsReference` | Ring ellipse glyph |
| `IOrbiter.Orbit` (`SemiMajorAxis`, `Eccentricity`, `Periapsis`, `Apoapsis`, `Mu`, `Parent`) | Transfer radii and speeds |

`PhysicalAtmosphereReference` exposes `SeaLevelDensity`, `SeaLevelPressure`, `ScaleHeight` and
`Height` in this build.[^ksa-physicalatmosphere]

**Home first, then nearest.** The home root is `IIndependentRoot.RootOf(system.HomeBody)`
(walks `IOrbiter.Parent` until it reaches a root).[^ksa-iindependentroot] Roots are sorted home
first, then by squared distance of `GetPositionEcl()` from home. `DestinationsFrom(sourceRoot)`
returns the other roots nearest first and caches the list per source, because a `FixedStar` and a
`Barycenter` store their ecliptic position in a `readonly` field set from the template, so the
distance between two star systems never changes for the loaded system.
`SystemGraph.MaxShownSystems = 8` caps how many other systems the map draws individually.

**Caching per loaded system.** `MapWindow.EnsureBuilt` reads `Universe.CurrentSystem` every frame
and rebuilds the graph, a fresh `DvCache` and the `ColorPalette` only when
`!ReferenceEquals(_graph.System, system)`. The comment explains the reference latch: reloading the
same system Id creates new body objects, and nodes holding the old ones would read orbits that no
longer update. Everything per-root (visual tree, layout, routes, search state) is reset at the
same time. Within a loaded system, Keplerian orbits are treated as fixed, so `DvCache` computes
each body-pair transfer once and never invalidates it; it is keyed by the ordinal-ordered pair of
body Ids and serves every star system because Ids are unique across the
`CelestialSystem`.[^dvmap-dvcache][^dvmap-colorpalette]

## Lifecycle and timing

- Hook: `[StarMapAfterGui]` (postfix on `Program.OnDrawUiViewports`), via
  `MapWindow.DrawActive` -> `ImGuiWindow.OnDrawUi` -> `MapWindow.DrawContent` -> `EnsureBuilt`.
  Lifecycle `frame:draw-ui`, main thread.
- The build is lazy: it runs on the first frame the window is shown after a system load, not at
  `Universe.OnLoaded`. While the window is closed nothing is read.
- Reading celestial state here is safe: `Universe.LoadSystem` creates `CurrentSystem` and its
  bodies on the main thread, celestial orbits are Keplerian and analytic, and the mod only reads
  static elements (mass, radius, SOI, orbital elements, atmosphere data) plus the fixed root
  positions. Live positions (phase angles for transfer windows, the map-mode overlay) are read
  per frame from `Orbit` with the current `Universe.GetElapsedTime()`.
- The graph never holds vehicles; the controlled vehicle is read separately per frame
  (`Program.ControlledVehicle`) for the "you are here" marker and re-rooting.

## Key code

| Path | Role |
|---|---|
| [DeltaVMap/Model/SystemGraph.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/SystemGraph.cs) | `PhysicalNode`, `SystemGraph.Build`, root ordering, destination cache |
| [DeltaVMap/Model/ForestRules.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/ForestRules.cs) | Game-type-free role, minor-body and root-order rules |
| [DeltaVMap/Dv/OrbitalStates.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/OrbitalStates.cs) | Per-body ladder from mass, radius, SOI, atmosphere, rotation |
| [DeltaVMap/Dv/DvCache.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/DvCache.cs) | Per-system transfer cache keyed by unordered body pair |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1740-L1804) | `EnsureBuilt` reference latch on `Universe.CurrentSystem` |
| [DeltaVMap/Render/ColorPalette.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/ColorPalette.cs) | Per-system color assignment built with the graph |

## Snippets

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

```csharp
// DeltaVMap/Model/SystemGraph.cs L190-L230
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
// ...
childBodies.Sort(static (a, b) =>
{
    Orbit oa = a.Orbit;
    Orbit ob = b.Orbit;
    bool aOpen = oa.Eccentricity >= 1.0;
    bool bOpen = ob.Eccentricity >= 1.0;
    if (aOpen != bOpen)
        return aOpen ? 1 : -1;
    double ka = aOpen ? oa.Periapsis : oa.SemiMajorAxis;
    double kb = bOpen ? ob.Periapsis : ob.SemiMajorAxis;
    int byKey = ka.CompareTo(kb);
    return byKey != 0 ? byKey : string.CompareOrdinal(a.Id, b.Id);
});
```

```csharp
// DeltaVMap/Render/MapWindow.cs L1742-L1760
private bool EnsureBuilt()
{
    CelestialSystem? system = Universe.CurrentSystem;
    if (system == null)
        return false;

    // Latched on the system instance, not its Id: loading the same system again builds new
    // bodies, and nodes holding the old ones would read orbits that no longer update.
    if (_graph == null || !ReferenceEquals(_graph.System, system))
    {
        SystemGraph? graph = SystemGraph.Build(system);
        if (graph == null)
            return false;
        _graph = graph;
        _cache = new DvCache();
        _palette = ColorPalette.Build(graph);
        _currentRootId = null;
        // ...
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Universe.CurrentSystem`, `Universe.LoadSystem`, `Universe.Roots` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs) | The loaded system; the same root enumeration the game uses |
| `CelestialSystem.All`, `CelestialSystem.HomeBody`, `CelestialSystem.Id` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs) | Body lookup collection and home body |
| `IIndependentRoot.RootOf` | [current/decomp/KSA/IIndependentRoot.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IIndependentRoot.cs) | Root of the home body |
| `IParentBody.Children`, `Mu`, `SphereOfInfluence`, `GetAtmosphereReference`, `GetNearSurfaceRadius`, `GetAngularVelocity`, `BodyTemplate` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs) | Hierarchy and per-body physics |
| `FixedStar.GetPositionEcl`, `Barycenter.GetPositionEcl` | [current/decomp/KSA/FixedStar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedStar.cs), [current/decomp/KSA/Barycenter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Barycenter.cs) | Fixed root positions, so inter-system distances are cacheable |
| `PhysicalAtmosphereReference.Height`, `SeaLevelDensity` | [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs) | Atmosphere height and density for the ladder |

## Pitfalls and version notes

- Do not use `Astronomical.IsStar()` to find hubs: a barycenter is a root but not a star. Test
  `is StellarBody` and `is IIndependentRoot` instead, as `ForestRules.RoleOf` does.
- `Celestial.IsMoon()` is true for anything whose parent is not a `StellarBody`, so a planet
  circling a barycenter would count as a moon; the mod defines its own `PhysicalNode.IsMoon`.
- Open-orbit bodies have a negative or undefined semi-major axis; sorting by SMA alone would put
  interstellar comets first.
- Latch caches on the `CelestialSystem` instance, not its Id: a reload of the same Id builds new
  body objects.
- The gas-giant test (atmosphere and mean radius above 15,000 km means no surface) is a
  heuristic, because the game exposes no gas-giant flag.

## See also

- [Body state ladder and "you are here"](./body-state-ladder-and-you-are-here.md)
- [Patched-conic transfer model](./patched-conic-transfer-model.md)
- [Ego re-rooting and vessel follow](./ego-rerooting-and-vessel-follow.md)
- [Multi-system scene composition](./multi-system-scene-composition.md)
- [Overview](../overview.md)
- [Latch on the Universe.CurrentSystem reference, not its Id](../../../howto/celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/cache-and-key-body-data-per-loaded-system-1.md) - how-to article demonstrating this feature
- [Roots then IParentBody.Children into an immutable forest, with role rules in a game-type-free class](../../../howto/celestials/systems-and-stars/enumerate-star-systems-and-bodies/enumerate-star-systems-and-bodies-1.md) - how-to article demonstrating this feature

[^dvmap-systemgraph]: DeltaVMap/Model/SystemGraph.cs
[^dvmap-forestrules]: DeltaVMap/Model/ForestRules.cs
[^dvmap-orbitalstates]: DeltaVMap/Dv/OrbitalStates.cs
[^dvmap-dvcache]: DeltaVMap/Dv/DvCache.cs
[^dvmap-mapwindow]: DeltaVMap/Render/MapWindow.cs
[^dvmap-colorpalette]: DeltaVMap/Render/ColorPalette.cs
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-iindependentroot]: KSA.IIndependentRoot (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-fixedstar]: KSA.FixedStar (decompiled, build 2026.10.10.5554)
[^ksa-barycenter]: KSA.Barycenter (decompiled, build 2026.10.10.5554)
[^ksa-physicalatmosphere]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
