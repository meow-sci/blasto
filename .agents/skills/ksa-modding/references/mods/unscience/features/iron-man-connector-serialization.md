---
type: Mod Feature
title: Per-instance attachment nodes that survive saves, copies and unload
description: Iron Man adds private Part.Connector instances to one kitten's backpack root, encodes them as a versioned marker in the serialized PartInstance.Id, restores them in a Part constructor postfix before connection indices resolve, and converts live links to stock endpoints before its hooks are removed
mod: unscience
submod: iron-man
category: persistence
techniques: [harmony-prefix, harmony-postfix, save-payload-embedding]
hooks:
  - harmony-postfix KSA.Part.GetReferenceWithChildren(ref uint, PartInstance, bool) (internal)
  - harmony-prefix KSA.Part..ctor(string, PartTemplate, PartInstance, Part)
  - harmony-postfix KSA.Part..ctor(string, PartTemplate, PartInstance, Part)
  - unscience PhysicsFrameHook.Enqueue (node edits)
lifecycle: [frame:prepare, event:save, event:load, event:part-copy, event:unload]
game_types:
  - KSA.Part
  - KSA.Part.Connector
  - KSA.Part.Connector.TemplateBase
  - KSA.Part.Connection
  - KSA.PartInstance
  - KSA.PartConnectorInstance
  - KSA.PartTree
  - KSA.PartTemplate
  - KSA.SerializedId
  - KSA.ConnectorCapabilityFlags
  - KSA.JobSystems
tags: [connectors, attachment-nodes, part-instance, serialization, save-compatibility, harmony, unload]
---

The stock kitten backpack (`KittenBackPackPart`) has no attachment nodes, and its `PartTemplate` is
shared by every kitten. Iron Man gives one kitten's root part private nodes (an "Up" node above the
head and a "Down" node at the feet by default, editable, up to 16) without touching the template,
and makes them survive world saves, blueprint saves, editor copies, reloads and mod unload. A node
carries bulk fuel, service fluid and electricity, so attached tanks feed attached engines.[^iron-man-readme]

Mechanism in one line: connectors are ordinary `Part.Connector` objects built from a private
`TemplateBase` and appended after the stock connectors; on save their definitions ride in the
serialized `PartInstance.Id` string, and a constructor patch rebuilds them before the game resolves
saved connector indices.

## How it works

**Authoring** ([iron-man.lib/IronManConnectors.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManConnectors.cs#L30-L118)).
`Add` refuses anything but a non-sub-part `KittenBackPackPart` root, validates position (within
100 m), direction and radius, then creates `new Part.Connector.TemplateBase { Id = name, Capabilities
= ConnectorCapabilityFlags.BulkFluid }` and `new Part.Connector(template, root)`, appends it to
`root.Connectors`, and records ownership in a `ConditionalWeakTable<Part.Connector, TemplateBase>`
(no strong references to game objects). `ToCapability` turns `BulkFluid` plus the default flags into
bulk fluid, service fluid and electricity.[^ksa-connector-capability-ext] `WriteTransform` rotates
the connector's local +X (the outward snapping normal) onto the requested direction and writes both
the runtime fields (`PositionParentAsmb`, `Asmb2ParentAsmb`, `Scale` = radius) and the private
template transform divided by the part scale, because `Connector.SetScale` recomputes position and
scale from the template when stock scaling refreshes connectors.[^ksa-part] The kitten frame comes
from the game's own comment in `PartGameData.xml`: origin at the feet, -Z up.[^ksa-part-game-data]
`Update` and `Remove` refuse a connected node. UI edits run as queued handoff actions that also call
`RecomputeAllDerivedData` and mark the editor tree dirty
([iron-man.lib/IronManSubmod.Ui.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManSubmod.Ui.cs#L176-L190)).

**Why ordering matters.** The game saves a connection as the connector's index in `Part.Connectors`
(`PartConnectorInstance.Index`, written by `SetupPartReferenceConnectorInstances`) and on load
indexes `Connectors[partInstance.Connectors[i].Index]` in
`RegenerateConnectionsFromPartInstance`.[^ksa-part][^ksa-part-connector-instance] The constructor
adds the template's connectors first, so owned nodes always sit in a suffix after the stock prefix.
`IronManConnectorData.Capture` refuses to save if that layout is broken (stock count differs from
the template, or owned and stock connectors interleave).

**Codec** ([iron-man.lib/IronManConnectorData.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManConnectorData.cs)).
`PartInstance` inherits `[XmlAttribute] Id` from `SerializedId`; for part instances it only feeds
the runtime part name (`Part.ResolveRuntimeId` falls back to the template id when it is empty), and
template lookups use `InstanceOf`.[^ksa-serializedid][^ksa-partinstance][^ksa-part] Iron Man writes
`"iron-man:v1:" + base64(JSON{OriginalId, StockConnectorCount, Anchors[]})` there, where each anchor
has name, position, outward direction and radius. `Capture` round-trips its own output through
`Decode` before returning, so it never writes data the loader would reject. `Decode` returns null for
ordinary ids, and for anything starting with the `iron-man:v` family it enforces the exact version,
a 32,000-character cap, the backpack template, the stock connector count, 1-16 anchors, names of
1-80 characters and the same numeric bounds as authoring; a damaged marked save throws
`InvalidDataException` instead of continuing into index regeneration with missing connectors.

**Patches** ([iron-man.lib/IronManConnectorPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManConnectorPatches.cs)).

| Hook | Action |
|---|---|
| postfix internal `Part.GetReferenceWithChildren(ref uint, PartInstance, bool)` | If the part owns nodes, replace `__result.Id` with the marker. The method recurses through the same patched entry, so every node in the tree is covered |
| prefix `Part(string, PartTemplate, PartInstance, Part)` | Decode `PartInstance.Id`; on a marker, restore the original runtime name into the `inName` argument (`ref string __0`) and pass the data via `__state` |
| postfix same constructor | `__state?.Restore(__instance)` re-adds the anchors after the stock connectors |

Every load path funnels through this constructor: `Part(PartInstance, Part)` chains to it with
`inInstance.Id`, and `PartTree.Deserialize` builds all parts (`RegenerateFromPartInstance`) before
it regenerates any connection.[^ksa-part][^ksa-parttree] `PartTree.DeepCopy` is
`Deserialize(Serialize())`, and the editor's copy, paste and subtree operations call
`GetReferenceWithChildren` and `PartTree.Deserialize` directly, so copies keep the nodes too.

**Unload** ([iron-man.lib/IronManConnectorUnload.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManConnectorUnload.cs)).
If the mod's hooks left while a live tree still had connections on owned nodes, the next save would
write indices past the stock connector count with no marker to restore them. `PrepareForUnload`
finds every root that ever received a node (weak references, so editor copies and inactive loaded
roots are included), and for each connection on an owned node it disconnects and reconnects the
same two endpoints with the owned connector replaced by its parent `Part` (a stock part-surface
link). Two owned endpoints joined by a bulk-fluid link cannot keep fuel flow as a stock
part-to-part link, so that case aborts. Any failure rolls back: replacements are disconnected,
originals reconnected and trees recomputed, and `IronManPatches.Remove` keeps the passive
persistence hooks installed and logs the reason
([iron-man.lib/IronManPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManPatches.cs#L32-L48)).
On success the nodes are removed and the persistence hooks are unpatched last.

## Lifecycle and timing

| Moment | What runs | Lifecycle |
|---|---|---|
| Configure / node edit | Queued `PhysicsFrameHook` action at `Program.PrepareFrame` L2216; `RecomputeAllDerivedData` only marks derived data dirty and `PartTree.FlushDirtyDerived` (L2218) rebuilds it before `ExecuteNextVehicleSolvers` (L2220) | `frame:prepare` |
| World save | `Vehicle.SerializeSave` -> `Parts.Serialize()` -> root `GetReferenceWithChildren` (`PartTree.cs` L376-L379) | `event:save` |
| Blueprint save / editor copy | `VehicleSaveData.Create`, `PartTree.DeepCopy`, editor copy/paste paths | `event:part-copy` |
| World or blueprint load | `PartTree.Deserialize` -> `RegenerateWholePartInstanceTree` (`Part.cs` L2403-L2406): constructors for every node, then `RegenerateConnectionsFromPartInstance` | `event:load` |
| Mod unload | `[StarMapUnload]` -> `Patcher.Unload` -> `IronManPatches.Remove` -> `PrepareForUnload` | `event:unload` |

All of these are main-thread code paths. Construction and serialization do not touch worker state,
so the hooks need no synchronization. Unload is different: it runs outside the frame handoff and
disconnects live links, so `PrepareForUnload` first calls `JobSystems.VehicleSolver.Wait()` to make
sure no vehicle worker is stepping the trees it is about to rewire.

## Key code

| Path | Role |
|---|---|
| [iron-man.lib/IronManConnectors.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManConnectors.cs) | Instance-owned connectors, ownership table, transform math, validation, weak root registry |
| [iron-man.lib/IronManConnectorData.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManConnectorData.cs) | Versioned marker codec with layout checks |
| [iron-man.lib/IronManConnectorPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManConnectorPatches.cs) | Serializer postfix, constructor prefix/postfix |
| [iron-man.lib/IronManConnectorUnload.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManConnectorUnload.cs) | Transactional conversion to stock endpoints |
| [iron-man.lib/IronManPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManPatches.cs) | Install order and conditional removal |
| [iron-man.tests/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.tests/README.md) | Managed checks running the real Harmony constructor/serializer hooks on fixtures |

## Snippets

The three hooks; the prefix swaps the constructor's name argument back before the game resolves the
runtime id:

```csharp
// iron-man.lib/IronManConnectorPatches.cs L52-L65
    private static void SerializePostfix(Part __instance, PartInstance __result)
    {
        if (IronManConnectors.GetOwned(__instance).Count != 0)
            __result.Id = IronManConnectorData.Capture(__instance);
    }

    private static void ConstructPrefix(ref string __0, PartTemplate __1, PartInstance? __2,
        out IronManConnectorData? __state)
    {
        __state = IronManConnectorData.Decode(__2?.Id, __1);
        if (__state != null) __0 = __state.OriginalId;
    }

    private static void ConstructPostfix(Part __instance, IronManConnectorData? __state) => __state?.Restore(__instance);
```

Capture keeps the stock prefix and owned suffix exact, then validates its own output:

```csharp
// iron-man.lib/IronManConnectorData.cs L28-L53
    internal static string Capture(Part part)
    {
        var owned = IronManConnectors.GetOwned(part);
        int stockCount = part.Connectors.Count - owned.Count;
        // Connector references serialize by index. Preserve the stock prefix and owned suffix exactly.
        if (stockCount != part.Template.Connectors.Count ||
            part.Connectors.Take(stockCount).Any(IronManConnectors.IsOwned) ||
            part.Connectors.Skip(stockCount).Any(c => !IronManConnectors.IsOwned(c)))
            throw new InvalidOperationException("Iron Man connector ordering changed; saving this kitten would lose attachment references.");
        var data = new IronManConnectorData { OriginalId = part.Id, StockConnectorCount = stockCount };
        foreach (var connector in owned)
        {
            double3 direction = double3.UnitX.Transform(connector.Asmb2ParentAsmb);
            IronManConnectors.Validate(connector.PositionParentAsmb, direction, connector.Scale.Y);
            data.Anchors.Add(new AnchorData
            {
                Name = connector.Id,
                Position = ToArray(connector.PositionParentAsmb),
                Direction = ToArray(direction),
                Radius = connector.Scale.Y,
            });
        }
        string marker = Marker + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(data));
        _ = Decode(marker, part.Template); // Refuse to write anything the loader would reject.
        return marker;
    }
```

The game resolves saved links by index into `Part.Connectors`, which is why nodes must exist before
this runs:

```csharp
// current/decomp/KSA/Part.cs L2445-L2449
	private void RegenerateConnectionsFromPartInstance(PartInstance partInstance)
	{
		for (int i = 0; i < partInstance.Connectors?.Count; i++)
		{
			if (Connectors[partInstance.Connectors[i].Index].Connection != null)
```

Unload: persistence hooks go last, and stay if live links cannot be converted:

```csharp
// iron-man.lib/IronManPatches.cs L32-L48
    public static void Remove(Harmony harmony)
    {
        Ready = false;
        RemoveSafely(() => IronManTeleportPatches.Remove(harmony));
        RemoveSafely(() => IronManRcsOrientationPatches.Remove(harmony));
        RemoveSafely(() => IronManEditorOrientationPatches.Remove(harmony));
        RemoveSafely(() => IronManControlFramePatches.Remove(harmony));
        RemoveSafely(() => IronManFlightComputerPatches.Remove(harmony));
        if (!IronManConnectorUnload.PrepareForUnload())
        {
            Console.WriteLine("iron-man: retaining passive hooks because attached nodes could not be safely converted for unload");
            return;
        }
        RemoveSafely(() => IronManFlightPatches.Remove(harmony));
        RemoveSafely(() => IronManEditorPatches.Remove(harmony));
        RemoveSafely(() => IronManConnectorPatches.Remove(harmony));
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Part.Connector`, `Part.Connector.TemplateBase`, `Connector(TemplateBase, Part)` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L191-L290) | Private template per node; runtime transform fields; `SetScale` reads the template |
| `Part.Connectors` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L670) | Mutable list; template connectors added in the constructor (L1526-L1529) |
| `Part(string, PartTemplate, PartInstance, Part)`, `Part.ResolveRuntimeId` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1475-L1494) | Patched constructor; `Part(PartInstance, ...)` overloads chain into it |
| `Part.GetReferenceWithChildren` (public L2153, internal L2158) | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2153-L2215) | Builds `PartInstance` per node; patched internal overload |
| `Part.SetupPartReferenceConnectorInstances`, `RegenerateConnectionsFromPartInstance` (private) | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2357) | Save and resolve connections by connector index (L2357, L2445) |
| `Part.Connection.Connect`, `Disconnect`, `HasCapabilities` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L506-L554) | Unload conversion |
| `PartTree.Serialize`, `Deserialize`, `DeepCopy` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L299-L400) | Every save, load and copy path |
| `PartInstance.Connectors` (`PartConnectorRef`) | [current/decomp/KSA/PartInstance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartInstance.cs#L36-L37) | Saved index list |
| `SerializedId.Id` (`[XmlAttribute]`) | [current/decomp/KSA/SerializedId.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedId.cs#L12-L13) | Carrier for the marker |
| `PartConnectorInstance.Index` | [current/decomp/KSA/PartConnectorInstance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartConnectorInstance.cs) | Why the layout must be stable |
| `ConnectorCapabilityExtensions.ToCapability` | [current/decomp/KSA/ConnectorCapabilityExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConnectorCapabilityExtensions.cs#L15-L30) | `BulkFluid` flag plus default electricity/service fluid |
| `Vehicle.SerializeSave` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1099-L1105) | World-save entry into `Parts.Serialize()` |
| `KittenBackPackPart` frame comment | [current/Content/Core/PartGameData.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/PartGameData.xml#L22) | Feet at origin, -Z up |

## Pitfalls and version notes

- Saves with connected Iron Man nodes need the mod to load: stock KSA would index connectors that do
  not exist. Unload conversion protects saves made after unload; it cannot rewrite files already on
  disk.[^iron-man-readme]
- Since KSA 5482 the `Part` constructor no longer creates a tree (`Part.Tree` is nullable; trees come
  from `CreateOwnTree`, `PartTree.CreateFromNewPartTree` or `Merge`). A root registered by the
  constructor postfix can therefore be treeless if deserialization aborts; unload filters roots with
  `.OfType<PartTree>()`.[^scope-iron-man]
- Also since 5482, `PartTree.RecomputeAllDerivedData` only marks data dirty. Resource-graph rebuild
  errors after unload conversion surface later, at the next `FlushDirtyDerived`, not inside the
  transaction.[^scope-iron-man]
- The decoder requires `StockConnectorCount` to equal the current template's connector count. A game
  update that adds a connector to `KittenBackPackPart` makes old marked saves fail loudly by design.
- Using `PartInstance.Id` is safe only because part lookups use `InstanceOf`; the restored runtime
  id comes from the marker's `OriginalId`, so nothing else sees the marker text.
- The managed checks run the production hooks against fixture classes, not `KSA.dll`, so they
  cannot detect signature drift; the constructor and the internal serializer overload are matched by
  exact parameter types and fail installation if they change.[^iron-man-tests-readme][^scope-iron-man]

## See also

- [Iron Man kitten vehicle editing](./iron-man-kitten-vehicle-editing.md) for where nodes are edited
- [Iron Man equipment render ordering](./iron-man-equipment-render-ordering.md), which keys on owned nodes
- [Physics frame hook](./lib-physics-frame-hook.md)
- [Save part identity](./lib-save-part-identity.md) and [scene save coordinator](./lib-scene-save-coordinator.md) for the sidecar half of Iron Man saves
- [Iron Man sub-mod](../submods/iron-man.md) and [mod overview](../overview.md)
- [Give one kitten private attachment nodes that survive saves, blueprints and copies](../../../howto/kittens/eva/open-the-vehicle-editor-on-a-live-eva-kitten/open-the-vehicle-editor-on-a-live-eva-kitten-3.md) - how-to article demonstrating this feature

[^iron-man-readme]: iron-man/README.md (player instructions)
[^ksa-connector-capability-ext]: KSA.ConnectorCapabilityExtensions (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-part-game-data]: Content/Core/PartGameData.xml (KittenBackPackPart, build 2026.10.10.5554)
[^ksa-part-connector-instance]: KSA.PartConnectorInstance (decompiled, build 2026.10.10.5554)
[^ksa-serializedid]: KSA.SerializedId (decompiled, build 2026.10.10.5554)
[^ksa-partinstance]: KSA.PartInstance (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^scope-iron-man]: scope/iron-man.md (authors' hook inventory, build 5482)
[^iron-man-tests-readme]: iron-man.tests/README.md (managed connector checks)
