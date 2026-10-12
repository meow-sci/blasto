---
type: Mod Feature
title: Stable vehicle and part identity across save and reload
description: SavedPartReference addresses a part by vehicle id, tree and sub-part index paths, template id and a SHA-256 topology signature of the whole vehicle, and resolves to null rather than substituting another vehicle or part
mod: unscience
category: persistence
techniques: [save-sidecar]
hooks:
  - harmony-postfix KSA.GameSave.Populate (capture scope via NativeSaveHooks.Capturing)
  - harmony-postfix KSA.Universe.DeserializeSave (rebind scope via NativeSaveHooks.Restoring)
lifecycle: [event:save, event:load, frame:prepare]
game_types: [KSA.Vehicle, KSA.Part, KSA.PartTree, KSA.PartTemplate, KSA.Universe, KSA.CelestialSystem]
tags: [saves, identity, parts, vehicles, rebind, sha256]
---

Many unscience records point at a specific part: a decal on a fairing, a camera on a strut, a light
grid cell, the seat an IVA camera was unlocked in. KSA does not persist anything a mod can use as a
part key. `Part.InstanceId` is drawn from a running counter that `Universe.DeserializeSave` resets,
and `Part.Id` falls back to the template id, so siblings share it.[^ksa-part][^ksa-universe]
`SavedPartReference` replaces both with a save-local structural address that survives a reload and
refuses to guess when the vehicle changed.[^saved-part-reference]

Mechanism in one line: record the vehicle id, the index path through `Part.TreeChildren`, the index
path through `Part.SubParts`, the template id and a SHA-256 signature of the vehicle's whole ordered
tree; on load, walk the same path in the rebuilt vehicle and accept the part only if the vehicle id is
unique, the topology hash matches and the template id matches.

## How it works

`SavedPartReference` (a plain DTO serialized by `SaveJson`):

| Field | Meaning |
|---|---|
| `VehicleId` | `Vehicle.Id` of the owning vessel |
| `TreePath` | Indices from `vehicle.Parts.Root` down through `Part.TreeChildren` (at most 256) |
| `SubPartPath` | Indices through `Part.SubParts` from that full part (at most 64) |
| `TemplateId` | `part.Template.Id` of the target |
| `VehicleTopology` | Hex SHA-256 of the vehicle's tree signature |

`PartIdentitySnapshot` walks a vehicle once: depth-first over `TreeChildren`, and for each full part
over its nested `SubParts`, assigning every `Part` its `(tree path, sub path)` address and appending a
length-prefixed token (`T<childCount>:`, `P<len>:<templateId>:<subPartCount>:`) to a signature string
that is then hashed. It throws if the depth or part limits are exceeded (256 tree levels, 64 sub-part
levels, 200000 parts) or a part appears twice.[^part-identity-snapshot]

- **Capture** (`SavedPartReference.Capture(vehicle, part)`) looks the part up in the snapshot's
  address map and throws if it does not belong to that vehicle, so a bad binding fails the feature's
  capture (the coordinator then keeps the previous record and warns).
- **Resolve** returns `null` when the paths are malformed or too long, when
  `VehicleProvider.FindVehicle` finds zero or several vehicles with that id, when the vehicle is
  disposed, when its current topology hash differs, when any index is out of range, or when the final
  template id differs. There is no fallback to the controlled vehicle or to a "similar" part.
- **Vehicle lookup** (`VehicleProvider.FindVehicle`) scans `Universe.CurrentSystem.All` including debris
  and returns `null` on an ambiguous id rather than the first match.[^vehicle-provider]
- **One traversal per transaction.** `BeginOperation()` installs a per-operation cache
  (`Dictionary<Vehicle, PartIdentitySnapshot>`) and returns an `IDisposable` that restores the previous
  scope. The host opens one scope around the whole capture and one around the whole restore, so a
  vehicle with thousands of pixel-grid cells is hashed once, not once per cell.[^unscience-saves]

What adapters do with a `null`: report it through `SaveRestoreContext.Warn` (which keeps the original
record for later saves) and skip that item. Features that bind whole vehicles by id apply the same
rule at capture time: The Tick refuses to write a record whose vehicle would not rebind uniquely.[^the-tick-saves][^thug-life-persistence]

The authors spell out the policy: references never fall back to the controlled vehicle or a similarly
named part, and structural addresses are not a migration mechanism for edited `universe.xml` files or
changed part trees.[^saves-acceptance][^scope-saves]

## Lifecycle and timing

| Operation | When | Frame phase |
|---|---|---|
| Capture scope | Inside `UnscienceSaves.Capture`, i.e. the postfix of `GameSave.Populate` | Wherever the save was issued (`frame:input` for the console, `frame:draw-ui` for windows and auto saves) |
| Resolve scope | Inside `UnscienceSaves.Restore`, i.e. the postfix of `Universe.DeserializeSave` during the deferred replay | `frame:prepare`, after native reconstruction and before `Universe.GetJobSimStep` |

Capture reads only the main-thread part tree structure (`TreeChildren`, `SubParts`, `Template`), which
vehicle workers do not restructure. Resolution runs after `DeserializeSave` has rebuilt every vehicle
from `universe.xml`, which is why tree order and template ids line up with what was saved: native
reconstruction builds the same tree from the same `PartInstance` data. The cache is static and the
code comment says never to hold it across frames; topology edits made while a scope is open would not
be seen.

## Key code

| Path | Role |
|---|---|
| [ksa-abstractions.lib/Persistence/SavedPartReference.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/SavedPartReference.cs) | DTO, transaction scope, capture and resolve |
| [ksa-abstractions.lib/Persistence/PartIdentitySnapshot.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/PartIdentitySnapshot.cs) | One-pass address map and topology hash |
| [ksa-abstractions.lib/VehicleProvider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/VehicleProvider.cs#L28-L40) | Unique-or-null vehicle lookup |
| [unscience/UnscienceSaves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/UnscienceSaves.cs#L35-L46) | Opens the scope around capture and restore |
| [the-tick.lib/TheTickSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.lib/TheTickSubmod.Saves.cs#L33-L43) | Refuses to capture vehicle ids that would not rebind |
| [saves.tests/PartReferenceTests.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/saves.tests/PartReferenceTests.cs) | Runtime id churn, duplicate templates, topology change, ambiguity checks |

Users across the repo include Blinky, Its So Shiny, Garry's Torch, Godzilla, Graffiti, Hot Pursuit,
Humble Arteest, Iron Man, Kitchen Sink, Pyro, Thug Life and Zippo adapters.

## Snippets

Capture and resolve:

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

The topology signature and address map, built in one traversal:

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

Unique-or-null vehicle lookup:

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Part.TreeChildren`, `Part.SubParts`, `Part.Template` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L668) | Tree walk (`List<Part>` at L668, `ReadOnlySpan<Part>` at L1127, template field at L576) |
| `Part.InstanceId`, `Part.Id` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1485-L1494) | Why they are not usable: running counter, template-id fallback |
| `Universe.GetNextRunningId` / `ResetRunningId` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2463) | Counter reset inside `DeserializeSave` |
| `PartTree.Root` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L138) | Walk start |
| `Vehicle.Parts`, `Vehicle.IsDisposed`, `Vehicle.IsDebris`, `Vehicle.Id` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L605) | Owning vessel (`Id` is inherited from `Astronomical`) |
| `Universe.CurrentSystem.All.UnsafeAsList()` | [current/decomp/KSA/LookupCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LookupCollection.cs#L210) | Enumerate all vehicles for the unique-id check |

## Pitfalls and version notes

- Two identical sibling parts with identical templates in an identical tree are indistinguishable by
  design; the address picks the same index position, which is correct only because native
  reconstruction preserves child order.
- Any topology change (a part added, removed or re-templated anywhere in the vehicle) invalidates every
  reference into that vehicle. That is intentional: the address is save-local, not a cross-edit
  matcher.
- Vehicle ids must be unique within the system for anything to rebind; duplicate names (two craft
  launched with the same name) make all their references unresolvable, and the diagnostics say so.
- `Part.SubParts` is a `ReadOnlySpan<Part>` property in 5554, so the walk indexes it directly; a game
  change to that type is a compile break, not a silent one.
- The snapshot cache is a static field; nested `BeginOperation` scopes restore the outer cache on
  dispose, but the type is not thread-safe and is meant for the main thread only.

## See also

- [Scene save coordinator](./lib-scene-save-coordinator.md) (warnings and retained records)
- [Native save and load hooks](./lib-native-save-hooks.md) (when capture and restore run)
- [Unscience scene save sidecar](./unscience-scene-save-sidecar.md)
- [Graffiti decal anchoring](./graffiti-decal-anchoring.md) and [Hot Pursuit part-mounted cameras](./hot-pursuit-part-mounted-cameras.md) (consumers)
- [Mod overview](../overview.md)
- [Reference a specific part with tree paths and a topology hash, resolving to null instead of guessing](../../../howto/persistence/saves/identify-vessels-and-parts-across-saves/identify-vessels-and-parts-across-saves-4.md) - how-to article demonstrating this feature
- [Save a part reference as tree paths plus a topology hash and resolve it after reload](../../../howto/vehicles-and-parts/part-geometry/anchor-to-parts-across-vehicle-changes/anchor-to-parts-across-vehicle-changes-3.md) - how-to article demonstrating this feature

[^saved-part-reference]: ksa-abstractions.lib/Persistence/SavedPartReference.cs
[^part-identity-snapshot]: ksa-abstractions.lib/Persistence/PartIdentitySnapshot.cs
[^vehicle-provider]: ksa-abstractions.lib/VehicleProvider.cs
[^unscience-saves]: unscience/UnscienceSaves.cs
[^the-tick-saves]: the-tick.lib/TheTickSubmod.Saves.cs
[^thug-life-persistence]: thug-life.lib/ThugLifeSubmod.Persistence.cs
[^saves-acceptance]: plans/saves-acceptance.md (authors' coverage and acceptance report)
[^scope-saves]: scope/saves.md (authors' identity and preflight notes)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
