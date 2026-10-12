---
type: How-To
title: Reference a specific part with tree paths and a topology hash, resolving to null instead of guessing
description: Capture a vehicle id, the index path through Part.TreeChildren and SubParts, the template id and a SHA-256 signature of the whole ordered tree, and resolve it only when the vehicle is unique, the hash matches and the template matches
category: persistence
group: saves
topic: identify-vessels-and-parts-across-saves
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/lib-save-part-identity]
techniques: [save-sidecar]
hooks: [unscience SavedPartReference.BeginOperation]
lifecycle: [event:save, event:load, frame:prepare]
game_types: [KSA.Part, KSA.PartTree, KSA.PartTemplate, KSA.Vehicle, KSA.Universe, KSA.CelestialSystem]
tags: [saves, identity, part-address, topology-hash, sha256, structural-reference]
---

Outcome: a save-local address for one part that survives a reload and refuses to guess when the vehicle changed. unscience uses it for decals on fairings, cameras on struts, light-grid cells and the seat an IVA camera was unlocked in. Prerequisite: a place to store it (see [Persist feature state as ISaveParticipant records through a coordinator that retains what it cannot restore](../save-mod-state-with-the-scene/save-mod-state-with-the-scene-1.md)). Demonstrated by [Stable vehicle and part identity across save and reload](../../../../mods/unscience/features/lib-save-part-identity.md).

## Background: how the game does it

Neither `Part.InstanceId` (a running counter reset by `DeserializeSave`) nor `Part.Id` (falls back to the template id) is a usable key (see the [overview](./overview.md#engine-background)). The game itself resolves a part inside a saved vehicle structurally: `CelestialSystem.DeserializeSave` walks the live `Part` tree with a `PartTreeIterator` and the saved `PartInstance` tree with a `PartInstanceTreeIterator` in lock step and matches `LocalInstanceId` ([current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L731-L749)). That works because native reconstruction builds the same tree from the same `PartInstance` data; a mod can rely on the same property: after `DeserializeSave` the tree order and template ids match what was saved. Part trees are `Part.TreeChildren` (a `List<Part>`) and nested `Part.SubParts`.

## Lifecycle and timing

| Operation | When | Lifecycle |
|---|---|---|
| Capture scope | Inside the host's capture callback (postfix of `GameSave.Populate`) | wherever the save was issued (`frame:input`, `frame:draw-ui`) |
| Resolve scope | Inside the host's restore callback (postfix of `Universe.DeserializeSave` during the deferred replay) | `frame:prepare`, after native reconstruction and before the next step is computed |

Capture reads only the main-thread part tree structure (`TreeChildren`, `SubParts`, `Template`). Resolution runs after `DeserializeSave` has rebuilt every vehicle from `universe.xml`, which is why indices line up. The per-operation cache is static and must not be held across frames: topology edits made while a scope is open would not be seen. Frame order: [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define the address.** A plain DTO so it serialises with the sidecar; the limits keep malformed data from recursing:

   ```csharp
   // ksa-abstractions.lib/Persistence/SavedPartReference.cs L8-L14
   public sealed class SavedPartReference
   {
       public string VehicleId { get; set; } = "";
       public int[] TreePath { get; set; } = Array.Empty<int>();
       public int[] SubPartPath { get; set; } = Array.Empty<int>();
       public string TemplateId { get; set; } = "";
       public string VehicleTopology { get; set; } = "";
   ```

2. **Take one snapshot per vehicle per transaction.** The snapshot walks the vehicle once, assigns every `Part` its `(tree path, sub path)` address and builds a signature string of length-prefixed tokens (`T<childCount>:` and `P<len>:<templateId>:<subPartCount>:`) that is SHA-256 hashed into `Topology`. It throws if a depth or part limit is exceeded or a part appears twice:

   ```csharp
   // ksa-abstractions.lib/Persistence/PartIdentitySnapshot.cs L10-L49
   internal sealed class PartIdentitySnapshot
   {
       internal sealed record Address(int[] Tree, int[] Sub);
       public Dictionary<Part, Address> Addresses { get; } = new();
       public string Topology { get; }

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

3. **Scope the cache to a transaction.** `BeginOperation()` installs a per-operation `Dictionary<Vehicle, PartIdentitySnapshot>` and restores the previous scope when disposed, so a vehicle with thousands of pixel-grid cells is hashed once rather than once per cell. The host opens one scope around the whole capture and one around the whole restore ([unscience/UnscienceSaves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/UnscienceSaves.cs#L35-L46)):

   ```csharp
   // ksa-abstractions.lib/Persistence/SavedPartReference.cs L15-L35
   private static Dictionary<Vehicle, PartIdentitySnapshot>? _operation;

   /// <summary>Use on the game thread around an immutable capture/rebind phase. Never hold across frames.</summary>
   public static IDisposable BeginOperation()
   {
       var previous = _operation;
       _operation = new();
       return new OperationScope(previous);
   }

   private sealed class OperationScope(Dictionary<Vehicle, PartIdentitySnapshot>? previous) : IDisposable
   {
       public void Dispose() => _operation = previous;
   }

   private static PartIdentitySnapshot Snapshot(Vehicle vehicle)
   {
       if (_operation == null) return new(vehicle);
       if (!_operation.TryGetValue(vehicle, out var snapshot)) _operation[vehicle] = snapshot = new(vehicle);
       return snapshot;
   }
   ```

4. **Capture** by looking the part up in the snapshot's address map; a part that does not belong to the vehicle throws, so a bad binding fails the feature's capture and the coordinator keeps the previous record:

   ```csharp
   // ksa-abstractions.lib/Persistence/SavedPartReference.cs L37-L44
   public static SavedPartReference Capture(Vehicle vehicle, Part part)
   {
       var snapshot = Snapshot(vehicle);
       if (!snapshot.Addresses.TryGetValue(part, out var path))
           throw new InvalidOperationException($"Part does not belong to {vehicle.Id}.");
       return new() { VehicleId = vehicle.Id, TreePath = path.Tree, SubPartPath = path.Sub,
           TemplateId = part.Template.Id, VehicleTopology = snapshot.Topology };
   }
   ```

5. **Resolve conservatively.** `Resolve` returns `null` when the paths are malformed or too long, when no vehicle or several vehicles have that id, when the vehicle is disposed, when its topology hash differs, when any index is out of range, or when the final template id differs. There is no fallback to the controlled vehicle or to a "similar" part:

   ```csharp
   // ksa-abstractions.lib/Persistence/SavedPartReference.cs L46-L63
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

6. **Make vehicle lookup unambiguous.** `VehicleProvider.FindVehicle` scans the system including debris and returns `null` when the id matches more than one vehicle, because ambiguous identities must never retarget saved edits:

   ```csharp
   // ksa-abstractions.lib/VehicleProvider.cs L28-L40
   /// <summary>Finds a vehicle by id, or null if none or more than one matches. Debris is searched too, so an id
   /// held from before a part failure still resolves.</summary>
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

7. **Handle `null` where you apply.** Report it through the restore context (`context.Warn`), which keeps the original record for later saves, and skip that item (see [Persist feature state as ISaveParticipant records through a coordinator that retains what it cannot restore](../save-mod-state-with-the-scene/save-mod-state-with-the-scene-1.md)).

## Complete example

| File | Role |
|---|---|
| [ksa-abstractions.lib/Persistence/SavedPartReference.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/SavedPartReference.cs) | DTO, transaction scope, capture and resolve |
| [ksa-abstractions.lib/Persistence/PartIdentitySnapshot.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/PartIdentitySnapshot.cs) | One-pass address map and topology hash |
| [ksa-abstractions.lib/VehicleProvider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/VehicleProvider.cs#L28-L40) | Unique-or-null vehicle lookup |
| [unscience/UnscienceSaves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/UnscienceSaves.cs#L35-L46) | Opens the scope around capture and restore |

## Variations and gotchas

- **Any structural edit invalidates the reference.** Adding, removing or reordering parts changes the hash and every reference to that vehicle resolves to `null` (the stated policy: structural addresses are not a migration mechanism for edited `universe.xml` files or changed part trees).
- **Vehicle ids must be unique to resolve.** A duplicated id returns `null`; ids that change (renames) are not followed (compare [Key mod data on Vehicle.Id and follow renames and disposals with postfixes](identify-vessels-and-parts-across-saves-1.md)).
- **Cost.** Hashing is linear in the part count and limited to 200000 parts, 256 tree levels and 64 sub-part levels; the transaction scope exists to avoid repeating it.
- **Do not hold references in a field across frames.** The cache is static and scoped; capture and resolve inside the scope only.
- **Staging changes the answer.** After a decouple the same `Part` is in a new vehicle with a different id and tree; structural references describe the saved vehicle only. For live follow across splits and docks see [Keeping an anchor on a part across staging, docking, editor changes and saves](../../../vehicles-and-parts/part-geometry/anchor-to-parts-across-vehicle-changes/overview.md).

## See also

- [Key mod data on Vehicle.Id and follow renames and disposals with postfixes](identify-vessels-and-parts-across-saves-1.md), [Anchor a saved window layout on Vehicle.Id and accept best-effort part ids](identify-vessels-and-parts-across-saves-3.md), [overview](./overview.md)
- [Stable vehicle and part identity across save and reload](../../../../mods/unscience/features/lib-save-part-identity.md)
- [Save a whole mod's scene state as versioned sidecar records, with reset and replay on load](../save-mod-state-with-the-scene/overview.md), [Keeping an anchor on a part across staging, docking, editor changes and saves](../../../vehicles-and-parts/part-geometry/anchor-to-parts-across-vehicle-changes/overview.md)
