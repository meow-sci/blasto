---
type: How-To
title: Universe.Roots, RootOf and a selected root, collecting only Celestials or only stars
description: Use Universe.Roots and IIndependentRoot.RootOf to offer a system choice, recurse through Children to collect planets and moons or stars and barycenters, and keep your own selected root because WorldSun moves
category: celestials
group: systems-and-stars
topic: enumerate-star-systems-and-bodies
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/compendium/features/star-systems-and-body-tree]
techniques: [game-object-graph]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Universe
  - KSA.IIndependentRoot
  - KSA.IParentBody
  - KSA.Astronomical
  - KSA.Celestial
  - KSA.StellarBody
  - KSA.Barycenter
  - KSA.OrbitingStar
tags: [roots, selected-system, worldsun, children, stars]
---
Outcome: a window that shows one system at a time, chosen in a combo, lists that system's planets and moons and its stars and barycenters, and survives the player flying to another system. Demonstrated by Compendium ([Star systems and body tree](../../../../mods/compendium/features/star-systems-and-body-tree.md)). Sub-step of the enumeration topic: roles of barycenter roots, orbiting stars and the moving `WorldSun`.

## Background: how the game does it

`Universe.Roots` is a `ReadOnlySpan<IIndependentRoot>` with one entry per star system ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L108-L110)). `IIndependentRoot.RootOf(IParentBody)` maps any body to its root by walking `Parent` ([current/decomp/KSA/IIndependentRoot.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IIndependentRoot.cs#L9-L16)). `IParentBody.Children` is where bodies hang; the API moved there from `Celestial`, which both `Celestial` and `StellarBody` implement ([current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L27)). `Universe.WorldSun` is set at load and then moved every frame by `Program.Render` to the star nearest the main camera ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2460), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L134-L140)), so it is not a stable choice of "the current system".

## Lifecycle and timing

Everything runs in the `[StarMapAfterGui]` window on the main thread (`frame:draw-ui`). `Roots`, `CurrentSystem` and `WorldSun` are empty or null until `Universe.LoadSystem` (`startup:game-loaded`); Compendium does not hook that point but polls from the draw hook and treats a null `WorldSun` as "not loaded yet". Body positions were refreshed earlier in the frame by `ApplyOrbitSolvers` and `ApplyVehicleSolvers`; the tree changes only inside `PrepareFrame` (a vehicle moving between `Children` lists on a sphere-of-influence change, or whole-system loads), not during the UI pass. Lookups walk the tree each call, so cache derived lists and reset them when the category tree is rebuilt. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Copy the roots** and map any body to its root:

```csharp
// src/CdmStarSystems.cs L19-L36
// Roots are the parentless StellarBody / Barycenter entries, one per star system.
private static List<Astronomical> GetSystemRoots()
{
    var roots = new List<Astronomical>();
    foreach (var root in Universe.Roots)
    {
        if (root is Astronomical astronomical)
        {
            roots.Add(astronomical);
        }
    }
    return roots;
}

private static Astronomical? GetRootOf(Astronomical? astronomical)
{
    return astronomical is IParentBody parentBody ? IIndependentRoot.RootOf(parentBody) as Astronomical : null;
}
```

2. **Keep your own selected root** while it is still in `Universe.Roots` (matched by id and reference); only fall back to the root of `WorldSun` otherwise:

```csharp
// src/CdmStarSystems.cs L55-L64
private static Astronomical? GetSelectedRoot()
{
    if (selectedSystemRoot != null && FindRootById(selectedSystemRoot.Id) == selectedSystemRoot)
    {
        return selectedSystemRoot;
    }

    selectedSystemRoot = GetRootOf(Universe.WorldSun) ?? Universe.WorldSun;
    return selectedSystemRoot;
}
```

3. **Collect only `Celestial`s** by recursing through `Children` and passing through stars and barycenters:

```csharp
// src/CdmHelpers.cs L303-L321
private static void CollectAllCelestials(Astronomical astro, List<Celestial> collection)
{
    if (astro is Celestial cel && !collection.Contains(cel))
    {
        collection.Add(cel);
    }

    // KSA API change: Children moved to IParentBody, which Celestial and StellarBody implement.
    if (astro is IParentBody parentBody && parentBody.Children != null)
    {
        foreach (var child in parentBody.Children)
        {
            if (child is Astronomical childAstro)
            {
                CollectAllCelestials(childAstro, collection);
            }
        }
    }
}
```

4. **Collect the stars** (`StellarBody` or `Barycenter`) reachable through children that are themselves stars or barycenters, and look one up by id:

```csharp
// src/CdmStarSystems.cs L66-L96
private static void CollectSystemStars(Astronomical astronomical, List<Astronomical> stars)
{
    if (astronomical is StellarBody || astronomical is Barycenter)
    {
        stars.Add(astronomical);
    }

    if (astronomical is IParentBody parentBody && parentBody.Children != null)
    {
        foreach (var child in parentBody.Children)
        {
            if (child is StellarBody || child is Barycenter)
            {
                CollectSystemStars((Astronomical)child, stars);
            }
        }
    }
}

private static Astronomical? FindSystemStar(string id)
{
    var root = GetSelectedRoot();
    if (root == null || string.IsNullOrWhiteSpace(id))
    {
        return null;
    }

    var stars = new List<Astronomical>();
    CollectSystemStars(root, stars);
    return stars.FirstOrDefault(star => star.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
```

5. **Read star facts** from `StellarBody.Mass` or `Barycenter.Mass`, `MeanRadius`, an `OrbitingStar`'s `Orbit`, and the distance from the root of `CurrentSystem.HomeBody` in light years ([current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L64-L66)).

## Complete example

| File | Role |
|---|---|
| [src/CdmStarSystems.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmStarSystems.cs#L19-L96) | Roots, root of a body, selected system, star collection |
| [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L303-L364) | Body collection and lookup by key |
| [src/CdmStarSystems.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmStarSystems.cs#L209-L283) | Star page facts |

## Variations and gotchas

- **`WorldSun` is not a selection.** Persist the player's choice yourself.
- **Ids repeat between systems,** so key bodies by a path rather than the bare id (see [cache and key per-body data](../../../celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/overview.md)).
- **The `Children` API note:** older code that read `Celestial.Children` must read `IParentBody.Children`.

## See also

- [Overview](overview.md); [approach 1](enumerate-star-systems-and-bodies-1.md); [approach 3](enumerate-star-systems-and-bodies-3.md)
- [Star systems and body tree](../../../../mods/compendium/features/star-systems-and-body-tree.md)
- [Cache and key per-body data per loaded system](../../../celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/overview.md)
