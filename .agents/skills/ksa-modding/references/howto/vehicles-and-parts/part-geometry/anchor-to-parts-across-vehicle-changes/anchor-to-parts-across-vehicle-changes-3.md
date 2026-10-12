---
type: How-To
title: Save a part reference as tree paths plus a topology hash and resolve it after reload
description: Capture a vehicle id, tree and subpart index paths, template id and a SHA-256 topology signature, and resolve to the same part or null after load
category: vehicles-and-parts
group: part-geometry
topic: anchor-to-parts-across-vehicle-changes
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/lib-save-part-identity]
techniques: [save-sidecar, structural-hash]
hooks: [harmony-postfix KSA.GameSave.Populate, harmony-postfix KSA.Universe.DeserializeSave]
lifecycle: [event:save, event:load, frame:prepare]
game_types: [KSA.Part, KSA.PartTree, KSA.Vehicle, KSA.Universe]
tags: [parts, identity, save, load, sha256, topology]
---
Outcome: write a reference to a part into your own save data and get the same part back after a reload, or `null`. KSA persists nothing a mod can use as a part key, so Unscience's `SavedPartReference` records the
vehicle id, an index path through the part tree and a SHA-256 hash of the vehicle's whole ordered tree, and refuses to guess when anything differs.

Prerequisite: StarMap and a place to write a sidecar (see the persistence topics). Demonstrated by [unscience/lib-save-part-identity](../../../../mods/unscience/features/lib-save-part-identity.md).

## Background: how the game does it

`Part.InstanceId` comes from `Universe.GetNextRunningId()` and `Universe.DeserializeSave` resets the counter, so it changes between sessions; `Part.Id` is the template id when no name was given, so siblings share it
([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1486-L1494)[^ksa-part], [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L177-L185)[^ksa-universe]). What is stable is the **structure**: `Part.TreeChildren` order, `Part.SubParts` order and `part.Template.Id`, because native reconstruction rebuilds the same
tree from the same `PartInstance` data in the save.

## Lifecycle and timing

| Operation | When | Phase |
|---|---|---|
| Capture | postfix of `GameSave.Populate` | wherever the save was issued (`frame:input` for the console, `frame:draw-ui` for windows and auto saves) (`event:save`) |
| Resolve | postfix of `Universe.DeserializeSave` during the deferred replay | `frame:prepare`, after native reconstruction and before `Universe.GetJobSimStep` (`event:load`) |

Capture reads only main-thread tree structure (`TreeChildren`, `SubParts`, `Template`), which vehicle workers do not restructure. A per-operation snapshot cache means a vehicle with thousands of references is hashed once per
scope; the cache is static, main-thread only and must not be held across frames. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Compute an address for every part and a topology hash in one pass.** Depth-first over `TreeChildren` and, for each full part, its nested `SubParts`; append a length-prefixed token per part to a signature string and hash it.
   Throw if depth, part count or a duplicate is hit:

```csharp
// ksa-abstractions.lib/Persistence/PartIdentitySnapshot.cs L16-L49
public PartIdentitySnapshot(Vehicle vehicle)
{
    var signature = new StringBuilder();
    VisitTree(vehicle.Parts.Root, new(), signature);
    Topology = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));
}

private void VisitTree(Part part, List<int> tree, StringBuilder signature)
{
    if (tree.Count > 256) throw new InvalidOperationException("Part tree exceeds save depth limit.");
    signature.Append('T').Append(part.TreeChildren.Count).Append(':');
    VisitSub(part, tree, new(), signature);
    for (int i = 0; i < part.TreeChildren.Count; i++)
    {
        tree.Add(i);
        VisitTree(part.TreeChildren[i], tree, signature);
        tree.RemoveAt(tree.Count - 1);
    }
}

private void VisitSub(Part part, List<int> tree, List<int> sub, StringBuilder signature)
{
    if (sub.Count > 64 || Addresses.Count >= 200000) throw new InvalidOperationException("Part identity exceeds save limits.");
    if (!Addresses.TryAdd(part, new(tree.ToArray(), sub.ToArray())))
        throw new InvalidOperationException("A part appears more than once in the vehicle tree.");
    string template = part.Template.Id;
    signature.Append('P').Append(template.Length).Append(':').Append(template).Append(':').Append(part.SubParts.Length).Append(':');
    for (int i = 0; i < part.SubParts.Length; i++)
    {
        sub.Add(i);
        VisitSub(part.SubParts[i], tree, sub, signature);
        sub.RemoveAt(sub.Count - 1);
    }
}
```

2. **Capture:** look the part up in the snapshot and fail loudly if it is not in that vehicle. **Resolve:** reject malformed or over-long paths, require a unique live vehicle, require the topology hash to match,
   walk the two index paths, and require the template id to match; anything else returns `null`:

```csharp
// ksa-abstractions.lib/Persistence/SavedPartReference.cs L37-L63
public static SavedPartReference Capture(Vehicle vehicle, Part part)
{
    var snapshot = Snapshot(vehicle);
    if (!snapshot.Addresses.TryGetValue(part, out var path))
        throw new InvalidOperationException($"Part does not belong to {vehicle.Id}.");
    return new() { VehicleId = vehicle.Id, TreePath = path.Tree, SubPartPath = path.Sub,
        TemplateId = part.Template.Id, VehicleTopology = snapshot.Topology };
}

public Part? Resolve()
{
    if (TreePath == null || SubPartPath == null || TreePath.Length > 256 || SubPartPath.Length > 64) return null;
    var vehicle = VehicleProvider.FindVehicle(VehicleId);
    if (vehicle == null || vehicle.IsDisposed || Snapshot(vehicle).Topology != VehicleTopology) return null;
    Part part = vehicle.Parts.Root;
    foreach (int index in TreePath)
    {
        if (index < 0 || index >= part.TreeChildren.Count) return null;
        part = part.TreeChildren[index];
    }
    foreach (int index in SubPartPath)
    {
        if (index < 0 || index >= part.SubParts.Length) return null;
        part = part.SubParts[index];
    }
    return part.Template.Id == TemplateId ? part : null;
}
```

3. **Use the unique-or-null vehicle lookup** from [article 2](anchor-to-parts-across-vehicle-changes-2.md) so that ambiguous ids never retarget saved data:

```csharp
// ksa-abstractions.lib/VehicleProvider.cs L30-L40
public static Vehicle? FindVehicle(string vehicleId)
{
    Vehicle? match = null;
    foreach (var vehicle in GetAllVehicles(includeDebris: true))
    {
        if (vehicle.Id != vehicleId) continue;
        if (match != null) return null; // Ambiguous identities must never retarget saved edits.
        match = vehicle;
    }
    return match;
}
```

4. **Handle `null` in the caller.** Unscience's adapters report it as a warning and keep the original record for later saves instead of overwriting it; features that bind whole vehicles refuse to write a record whose vehicle id would not rebind uniquely.

## Complete example

- [ksa-abstractions.lib/Persistence/SavedPartReference.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/SavedPartReference.cs#L1-L64)[^unscience-savedpartreference] - DTO, transaction scope, capture and resolve.
- [ksa-abstractions.lib/Persistence/PartIdentitySnapshot.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/PartIdentitySnapshot.cs#L1-L50)[^unscience-partidentitysnapshot] - address map and topology hash.
- [ksa-abstractions.lib/VehicleProvider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/VehicleProvider.cs#L22-L40)[^unscience-vehicleprovider] - vehicle lookup.

## Variations and gotchas

- **Any topology change invalidates every reference into that vehicle**: a part added, removed or re-templated anywhere. That is intentional. The address is save-local, not a cross-edit matcher, and it is not a migration mechanism for hand-edited saves.
- **Identical siblings** (same template, same tree position) are indistinguishable by design; the index is right only because reconstruction preserves child order.
- **Vehicle ids must be unique** in the system; two craft launched with the same name make all their references unresolvable.
- **No fallback**, ever, to the controlled vehicle or a similar part.
- **Not thread-safe**; main thread only. Nested `BeginOperation` scopes restore the outer cache on dispose.
- **`Part.SubParts` type.** It is a `ReadOnlySpan<Part>` property in this build, indexed directly; a change is a compile break, not a silent one.

## See also

- [Overview](overview.md); [Article 2](anchor-to-parts-across-vehicle-changes-2.md)
- [unscience/lib-save-part-identity](../../../../mods/unscience/features/lib-save-part-identity.md)
- [Evolve a shipped part without breaking saves](../../parts/evolve-a-shipped-part-without-breaking-saves/overview.md)

[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-universe]: current/decomp/KSA/Universe.cs (decompiled, build 2026.10.10.5554)
[^unscience-savedpartreference]: unscience: ksa-abstractions.lib/Persistence/SavedPartReference.cs
[^unscience-partidentitysnapshot]: unscience: ksa-abstractions.lib/Persistence/PartIdentitySnapshot.cs
[^unscience-vehicleprovider]: unscience: ksa-abstractions.lib/VehicleProvider.cs
