---
type: Mod Feature
title: Star systems and the body tree
description: How Compendium finds every star system through Universe.Roots, walks IParentBody.Children to collect bodies and stars, names bodies by dotted parent paths, and keeps its own selected system instead of following Universe.WorldSun
mod: compendium
category: celestials
techniques: [game-object-graph]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Universe
  - KSA.CelestialSystem
  - KSA.IIndependentRoot
  - KSA.IParentBody
  - KSA.IOrbiter
  - KSA.Astronomical
  - KSA.Celestial
  - KSA.StellarBody
  - KSA.FixedStar
  - KSA.OrbitingStar
  - KSA.Barycenter
tags: [celestials, universe, star-systems, barycenter, body-tree, interstellar]
---

Since the game added interstellar travel, one loaded universe can contain several star systems, each
with its own central star or barycenter. Compendium shows one system at a time, chosen in a combo,
and lists that system's bodies; its Stars category lists every star and barycenter inside the
selected system. The code that gets there is a compact map of how the game models systems, which is
the reusable part.[^cmp-copilot-instructions-md]

## How it works

1. **The engine model.** `Universe.LoadSystem` creates one `CelestialSystem` as `Universe.CurrentSystem`
   for everything, then caches `Universe.Stars` from `All.OfType<StellarBody>()` and `Universe.Roots`
   from `All.OfType<IIndependentRoot>()`, and sets `Universe.WorldSun`
   ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L232-L256)).[^ksa-universe] `CelestialSystem.CreateTreeFromRoot` adds each
   template's bodies to the root's `Children` ([current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L197-L213)).[^ksa-celestialsystem]
   The class hierarchy that matters:[^ksa-fixedstar] [^ksa-barycenter] [^ksa-orbitingstar] [^ksa-celestial]

   | Type | Declared as | Role |
   |---|---|---|
   | `FixedStar` | `StellarBody, IIndependentRoot, IParentBody` | A system's central star; does not orbit ([current/decomp/KSA/FixedStar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedStar.cs#L7)) |
   | `Barycenter` | `Astronomical, IIndependentRoot, IParentBody` | Root of a multi-star system ([current/decomp/KSA/Barycenter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Barycenter.cs#L7)) |
   | `OrbitingStar` | `StellarBody, IOrbiter` | A star orbiting a barycenter ([current/decomp/KSA/OrbitingStar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitingStar.cs#L9)) |
   | `Celestial` | `Astronomical, IOrbiter, IParentBody` | Planets, moons, small bodies ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L19)) |

2. **Roots.** `GetSystemRoots` copies `Universe.Roots` (a `ReadOnlySpan<IIndependentRoot>`) into a list
   of `Astronomical`; `GetRootOf` uses the game's static `IIndependentRoot.RootOf(IParentBody)`, which
   walks `IOrbiter.Parent` upward until it reaches a non-orbiter ([src/CdmStarSystems.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmStarSystems.cs#L19-L53),
   [current/decomp/KSA/IIndependentRoot.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IIndependentRoot.cs#L9-L16)).[^cmp-cdmstarsystems] [^ksa-iindependentroot]
3. **Which system is selected.** `GetSelectedRoot` keeps the player's choice while that root is still
   in `Universe.Roots` (matched by id and reference) and otherwise falls back to the root of
   `Universe.WorldSun` ([src/CdmStarSystems.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmStarSystems.cs#L55-L64)). `WorldSun` is not a stable choice:
   `Program.Render` moves it to the star nearest the main camera every frame
   ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2460), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L134-L157)).[^ksa-program]
4. **Bodies.** `CollectAllCelestials` recurses through `IParentBody.Children` and collects only
   `Celestial` instances, passing through stars and barycenters
   ([src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L303-L321), [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L27)).[^cmp-cdmhelpers] [^ksa-iparentbody]
   The comment in the code records the API move: `Children` now lives on `IParentBody`, which both
   `Celestial` and `StellarBody` implement.
5. **Stars.** `CollectSystemStars` collects every `StellarBody` or `Barycenter` reachable through
   children that are themselves stars or barycenters, and `FindSystemStar` looks one up by id
   ([src/CdmStarSystems.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmStarSystems.cs#L66-L96)).
6. **Stable keys.** Ids can repeat between systems, so bodies are keyed by a dotted path of `Celestial`
   ids from the top-level body down (`Jupiter.Io`), built by walking `Celestial.Parent` and stopping at
   the star ([src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L47-L63)). `FindCelestialByKey` splits the key and matches the last
   segment, and the parent when given, depth-first under a root ([src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L323-L364)).
   The data lookup further prefixes the key with the root id or the system id (see
   [Cross-mod JSON catalog](cross-mod-json-catalog.md)).
7. **Derived star facts.** The star page reads `StellarBody.Mass` or `Barycenter.Mass`, `MeanRadius`, an
   `OrbitingStar`'s `Orbit`, and the distance from the root of `CurrentSystem.HomeBody` in light years
   ([src/CdmStarSystems.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmStarSystems.cs#L233-L265), [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L64-L70)).

## Lifecycle and timing

- **No hook at load.** `Roots`, `CurrentSystem` and `WorldSun` are empty or null until
  `Universe.LoadSystem` runs (`startup:game-loaded`). Compendium does not hook that point; it polls
  from the draw hook and treats a null `WorldSun` as "not loaded yet".
- **Read in `frame:draw-ui`.** All walks happen in the `[StarMapAfterGui]` window on the main thread.
  Body positions were refreshed by `ApplyOrbitSolvers` and `ApplyVehicleSolvers` earlier in the frame;
  the tree itself changes only inside `PrepareFrame`, not during the UI pass: a vehicle that changed
  sphere of influence moves between `Children` lists in its apply
  (`Vehicle.ApplyPendingParentChildrenFix`, [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L638-L647)), and vehicles are
  created and destroyed through the input buffers, besides whole-system loads.[^ksa-vehicle]
- **Cost.** Lookups by key walk the tree each time they are called. The window caches the category
  lists and display names it derives from them, and resets those caches whenever the category tree is
  rebuilt ([src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L35-L45)).

## Key code

| Path | Role |
|---|---|
| [src/CdmStarSystems.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmStarSystems.cs#L19-L96) | Roots, root of a body, selected system, star collection |
| [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L47-L63) | Dotted parent path |
| [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L303-L364) | Body collection and lookup by key |
| [src/CdmStarSystems.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmStarSystems.cs#L209-L283) | Star page facts |

## Snippets

```csharp
// src/CdmStarSystems.cs L19-L36, L55-L64
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
// ...
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

```csharp
// src/CdmHelpers.cs L47-L63, L303-L321
private static string GetCelestialPath(Astronomical astronomical)
{
    var pathSegments = new Stack<string>();
    if (astronomical is not Celestial)
    {
        return astronomical.Id;
    }
    Astronomical? current = astronomical;

    while (current is Celestial currentCelestial)
    {
        pathSegments.Push(currentCelestial.Id);
        current = currentCelestial.Parent as Astronomical;
    }

    return string.Join(".", pathSegments);
}
// ...
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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Universe.Roots`, `Stars`, `CurrentSystem`, `WorldSun` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L106-L114) | System roots and fallbacks |
| `Universe.LoadSystem`, `UpdateWorldSun`, `NearestStar` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L134-L157) | When roots exist; why `WorldSun` moves |
| `IIndependentRoot.RootOf` | [current/decomp/KSA/IIndependentRoot.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IIndependentRoot.cs#L9-L16) | Root of any body |
| `IParentBody.Children` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L27) | Tree walk |
| `CelestialSystem.HomeBody`, `All`, `Id` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L64-L70) | Home system, vehicle list, system id |
| `StellarBody.Mass`, `Barycenter.Mass`, `Celestial.Parent` | [current/decomp/KSA/StellarBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StellarBody.cs#L33) | Star facts and paths |

## Pitfalls and version notes

- **Do not use `WorldSun` as the current system.** It changes as the camera moves between systems.
- **Duplicate ids are legal.** The project's instructions state that the same id can appear in
  different systems and must not be deduplicated; keys must carry the system or root.[^cmp-copilot-instructions-md]
- **`FindCelestialById` returns the first depth-first match** when no parent is given, so a bare id is
  ambiguous inside one system if two branches reuse it.
- **Older API.** Code written against builds where `Celestial` held `Children` no longer compiles; the
  property is on `IParentBody` in build 2026.10.10.5554.

## See also

- [Celestial compendium window](celestial-info-window.md)
- [Cross-mod JSON catalog](cross-mod-json-catalog.md)
- [Navigation-only root targets](root-navigation-targets.md)
- [Dotted path of Celestial ids from the top-level body down](../../../howto/celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/cache-and-key-body-data-per-loaded-system-4.md) - how-to article demonstrating this feature
- [Universe.Roots, RootOf and a selected root, collecting only Celestials or only stars](../../../howto/celestials/systems-and-stars/enumerate-star-systems-and-bodies/enumerate-star-systems-and-bodies-2.md) - how-to article demonstrating this feature

[^cmp-copilot-instructions-md]: Compendium: .github/copilot-instructions.md
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-fixedstar]: KSA.FixedStar (decompiled, build 2026.10.10.5554)
[^ksa-barycenter]: KSA.Barycenter (decompiled, build 2026.10.10.5554)
[^ksa-orbitingstar]: KSA.OrbitingStar (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^cmp-cdmstarsystems]: Compendium: src/CdmStarSystems.cs
[^ksa-iindependentroot]: KSA.IIndependentRoot (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^cmp-cdmhelpers]: Compendium: src/CdmHelpers.cs
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
