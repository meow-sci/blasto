---
type: How-To
title: Dotted path of Celestial ids from the top-level body down
description: Key mod data about bodies by a dotted path of Celestial ids such as Jupiter.Io, build it by walking Parent, and resolve the key back to the live object by matching the last segment and its parent depth-first under a root
category: celestials
group: systems-and-stars
topic: cache-and-key-body-data-per-loaded-system
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/compendium/features/star-systems-and-body-tree]
techniques: [game-object-graph]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Celestial, KSA.IParentBody, KSA.Astronomical, KSA.Universe]
tags: [key, path, parent, lookup, ids]
---
Outcome: stable keys for bodies that stay unique when ids repeat between systems or moons share names, and a lookup that resolves a key back to the live `Celestial`. Sub-step of the cache topic: how to key body data so it resolves back to the live object. Demonstrated by Compendium ([Star systems and body tree](../../../../mods/compendium/features/star-systems-and-body-tree.md)).

## Background: how the game does it

A `Celestial` knows its `Id` and, through `Orbit.Parent`, its parent ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L69)); children hang in `IParentBody.Children` ([current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L27)). The same `Id` can occur under different parents (several planets with a moon of the same name) and in different star systems, so a bare id is not a key.

## Lifecycle and timing

Key building and lookup run in `[StarMapAfterGui]` (`frame:draw-ui`) and walk the tree each call; the window caches the category lists and display names it derives and resets them when the category tree is rebuilt. The tree changes only inside `PrepareFrame` and whole-system loads. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Build the key** by walking `Parent` while it is a `Celestial` and joining ids from the top down. A star or barycenter keeps its bare id:

```csharp
// src/CdmHelpers.cs L47-L63
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
```

2. **Resolve the key back:** split on `.`, match the last segment as the id and the one before it as the parent id, depth-first under a root (matching case-insensitively):

```csharp
// src/CdmHelpers.cs L323-L364
private static Celestial? FindCelestialById(Astronomical? body, string id, string? parentId = null)
{
    if (body == null) return null;
    if (body is Celestial cel && cel.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
    {
        if (string.IsNullOrEmpty(parentId))
        {
            return cel;
        }

        if (cel.Parent is Celestial parentCelestial && parentCelestial.Id.Equals(parentId, StringComparison.OrdinalIgnoreCase))
        {
            return cel;
        }
    }

    if (body is IParentBody parentBody && parentBody.Children != null)
    {
        foreach (var child in parentBody.Children)
        {
            if (child is Astronomical childAstro)
            {
                var found = FindCelestialById(childAstro, id, parentId);
                if (found != null) return found;
            }
        }
    }
    return null;
}

private static Celestial? FindCelestialByKey(Astronomical? body, string bodyKey)
{
    if (string.IsNullOrWhiteSpace(bodyKey))
    {
        return null;
    }

    string[] parts = bodyKey.Split('.', StringSplitOptions.RemoveEmptyEntries);
    string id = parts.Length > 0 ? parts[^1] : bodyKey;
    string? parentId = parts.Length > 1 ? parts[^2] : null;
    return FindCelestialById(body, id, parentId);
}
```

3. **Prefix the data lookup with the root or system id** so entries for other star systems do not collide (see [approach 5](cache-and-key-body-data-per-loaded-system-5.md)).

4. **Tolerate absence:** a key that matches no loaded celestial is skipped, not an error.

## Complete example

| File | Role |
|---|---|
| [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L47-L63) | Dotted parent path |
| [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L303-L364) | Body collection and lookup by key |

## Variations and gotchas

- **Two levels are enough** for stock content (planet and moon); a deeper tree would need the full path compared, not just the last two segments.
- **Cost:** each lookup is a depth-first tree walk; cache resolved bodies if you call it per frame.
- **Case-insensitive ids** are matched, but keys should be written in the game's case.

## See also

- [Overview](overview.md); [approach 3](cache-and-key-body-data-per-loaded-system-3.md); [approach 5](cache-and-key-body-data-per-loaded-system-5.md)
- [Star systems and body tree](../../../../mods/compendium/features/star-systems-and-body-tree.md)
- [Enumerate star systems and bodies](../../../celestials/systems-and-stars/enumerate-star-systems-and-bodies/overview.md)
