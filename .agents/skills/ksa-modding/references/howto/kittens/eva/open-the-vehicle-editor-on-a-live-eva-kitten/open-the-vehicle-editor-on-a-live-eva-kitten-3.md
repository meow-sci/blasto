---
type: How-To
title: Give one kitten private attachment nodes that survive saves, blueprints and copies
description: Append owned Part.Connector nodes after the stock connectors, encode them as a versioned marker in the serialized PartInstance.Id and rebuild them in a Part constructor postfix before connection indices resolve
category: kittens
group: eva
topic: open-the-vehicle-editor-on-a-live-eva-kitten
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony, open-the-vehicle-editor-on-a-live-eva-kitten-1]
demonstrated_by: [mods/unscience/features/iron-man-connector-serialization]
techniques: [harmony-prefix, harmony-postfix, save-payload-embedding]
hooks:
  - harmony-postfix KSA.Part.GetReferenceWithChildren(ref uint, PartInstance, bool) (internal)
  - harmony-prefix KSA.Part..ctor(string, PartTemplate, PartInstance, Part)
  - harmony-postfix KSA.Part..ctor(string, PartTemplate, PartInstance, Part)
lifecycle: [frame:prepare, event:save, event:load, event:part-copy, event:unload]
game_types:
  - KSA.Part
  - KSA.Part.Connector
  - KSA.PartInstance
  - KSA.PartConnectorInstance
  - KSA.PartTree
  - KSA.PartTemplate
  - KSA.SerializedId
  - KSA.ConnectorCapabilityFlags
tags: [kitten, connectors, attachment-nodes, save-compatibility, part-instance, iron-man]
---
The stock kitten backpack part (`KittenBackPackPart`) has no attachment nodes, and its `PartTemplate` is shared by every kitten, so equipment has nothing to snap to. This article gives **one kitten's** root part private `Part.Connector` nodes without touching the template, and makes them survive world saves, blueprint saves, editor copies and reloads. The trick: connectors are ordinary `Part.Connector` objects built from a private `TemplateBase` and appended after the stock connectors; on save their definitions ride inside the serialized `PartInstance.Id` string, and a constructor patch rebuilds them before the game resolves saved connector indices. Prerequisites: StarMap, Harmony, and the editor session from [approach 1](open-the-vehicle-editor-on-a-live-eva-kitten-1.md). Demonstrated by Unscience's Iron Man: [Per-instance attachment nodes that survive saves](../../../../mods/unscience/features/iron-man-connector-serialization.md). The same save-compatibility reasoning applies to any per-instance data on a part (compare [evolve a shipped part without breaking saves](../../../vehicles-and-parts/parts/evolve-a-shipped-part-without-breaking-saves/overview.md)).

## Background: how the game does it

The game saves a connection as the connector's **index** into `Part.Connectors`, and on load indexes straight back into that list. If the list is shorter or reordered on load, the index lands on the wrong connector or throws.

```csharp
// current/decomp/KSA/PartConnectorInstance.cs L7-L8
	[XmlAttribute("Index")]
	public int Index;
```

```csharp
// current/decomp/KSA/Part.cs L2445-L2449
	private void RegenerateConnectionsFromPartInstance(PartInstance partInstance)
	{
		for (int i = 0; i < partInstance.Connectors?.Count; i++)
		{
			if (Connectors[partInstance.Connectors[i].Index].Connection != null)
```

The `Part` constructor adds the template's connectors first, so owned nodes you append always sit in a suffix after the stock prefix. Every load path funnels through this constructor: `Part(PartInstance, Part)` chains into it with `inInstance.Id`, and `PartTree.Deserialize` builds all parts before it regenerates any connection.

```csharp
// current/decomp/KSA/Part.cs L1480-L1483
	public Part(PartInstance inInstance, Part? parent)
		: this(inInstance.Id, inInstance.GetTemplate(), inInstance, parent)
	{
	}
```

```csharp
// current/decomp/KSA/Part.cs L1526-L1529
		foreach (Connector.TemplateBase connector in Template.Connectors)
		{
			Connectors.Add(new Connector(connector, this));
		}
```

A part's runtime id comes from the instance id when it is non-empty, and template lookups use `InstanceOf`, so the saved `PartInstance.Id` string is a free carrier:

```csharp
// current/decomp/KSA/Part.cs L1485-L1488
	public static string ResolveRuntimeId(string? instanceName, PartTemplate template)
	{
		return string.IsNullOrEmpty(instanceName) ? template.Id : instanceName;
	}
```

Saving builds the `PartInstance` in `GetReferenceWithChildren` (recursing through the same internal overload), and `PartTree.DeepCopy` is `Deserialize(Serialize())`, so editor copies take the same paths:

```csharp
// current/decomp/KSA/Part.cs L2208-L2211
		for (int i = 0; i < TreeChildren.Count; i++)
		{
			partInstance.Children.Add(TreeChildren[i].GetReferenceWithChildren(ref localRunningId, partInstance));
		}
```

```csharp
// current/decomp/KSA/PartTree.cs L397-L400
	public PartTree DeepCopy()
	{
		return Deserialize(Serialize());
	}
```

## Lifecycle and timing

| Moment | What runs | Lifecycle |
|---|---|---|
| Add, move or remove a node | a queued callback at the `PrepareFrame` handoff, then `RecomputeAllDerivedData` | `frame:prepare` |
| World save | `Vehicle.SerializeSave` to `Parts.Serialize()` to the root's `GetReferenceWithChildren` | `event:save` |
| Blueprint save, editor copy and paste | `VehicleSaveData.Create`, `PartTree.DeepCopy` | `event:part-copy` |
| World or blueprint load | `PartTree.Deserialize` to `Part.RegenerateWholePartInstanceTree`: constructors for every node, then `RegenerateConnectionsFromPartInstance` | `event:load` |
| Mod unload | convert live links to stock endpoints, then remove the hooks | `event:unload` |

All of these are main-thread code paths. Construction and serialization do not touch worker state, so the hooks need no synchronization; unload does disconnect live links and therefore first waits on the vehicle solver. The game loop details are in the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Author a node.** Refuse anything but the kitten backpack root, validate the numbers, then build a private `TemplateBase` and a `Part.Connector` over it and append it to `root.Connectors`. Remember which connectors you own in a `ConditionalWeakTable` so no strong references to game objects are held. `BulkFluid` plus the defaults gives bulk fuel, service fluid and electricity, so attached tanks feed attached engines.

```csharp
// iron-man.lib/IronManConnectors.cs L30-L50
    public static Part.Connector Add(Part root, string name, double3 position, double3 direction, double radius)
    {
        if (root.Template.Id != BackpackTemplateId || root.IsSubPart)
            throw new InvalidOperationException("Iron Man anchors require a kitten backpack root.");
        if (GetOwned(root).Count >= MaximumAnchors)
            throw new InvalidOperationException($"A kitten can have at most {MaximumAnchors} Iron Man anchors.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
            throw new ArgumentException("Give the connector a name of 1–80 characters.", nameof(name));
        Validate(position, direction, radius);
        var template = new Part.Connector.TemplateBase
        {
            Id = name,
            // Bulk fuel plus the stock defaults (service fluid and electricity).
            Capabilities = ConnectorCapabilityFlags.BulkFluid,
        };
        var connector = new Part.Connector(template, root);
        Owned.Add(connector, template);
        WriteTransform(connector, template, position, direction, radius);
        root.Connectors.Add(connector);
        if (GetOwned(root).Count == 1) Owners.Add(new WeakReference<Part>(root));
        return connector;
```

2. **Write the transform both places.** Rotate the connector's local +X (the outward snapping normal) onto the requested direction, and write the runtime fields (`PositionParentAsmb`, `Asmb2ParentAsmb`, `Scale`) and the private template transform divided by the part scale. `Connector.SetScale` recomputes position and scale from the template when stock scaling refreshes connectors, so writing only the runtime fields would be undone. The kitten frame is the game's own: origin at the feet, -Z up (comment in `current/Content/Core/PartGameData.xml`).

```csharp
// iron-man.lib/IronManConnectors.cs L100-L118
    private static void WriteTransform(Part.Connector connector, Part.Connector.TemplateBase template,
        double3 position, double3 direction, double radius)
    {
        direction /= Math.Sqrt(direction.LengthSquared());
        double dot = Math.Clamp(direction.X, -1, 1);
        double3 axis = double3.Cross(double3.UnitX, direction);
        doubleQuat rotation = axis.LengthSquared() < 1e-12
            ? (dot >= 0 ? doubleQuat.Identity : doubleQuat.CreateFromAxisAngle(double3.UnitZ, Math.PI))
            : doubleQuat.CreateFromAxisAngle(axis / Math.Sqrt(axis.LengthSquared()), Math.Acos(dot));
        double parentScale = new ScaleFactors(connector.Parent.Scale).Scale;
        if (!double.IsFinite(parentScale) || parentScale <= 0)
            throw new InvalidOperationException("The kitten scale must be positive and finite.");
        template.Transform.PositionValue = position / parentScale;
        template.Transform.RotationValue = rotation;
        template.Transform.ScaleValue = new double3(radius / parentScale);
        connector.PositionParentAsmb = position;
        connector.Asmb2ParentAsmb = rotation;
        connector.Scale = new double3(radius);
    }
```

```csharp
// iron-man.lib/IronManConnectors.cs L22-L28
    public static void EnsureDefaults(Part root)
    {
        if (GetOwned(root).Count != 0) return;
        // The game authors the kitten with feet at the origin and up along -Z.
        Add(root, "Up", new double3(0, 0, -0.43), -double3.UnitZ, 0.12);
        Add(root, "Down", new double3(0, 0, -0.10), double3.UnitZ, 0.12);
    }
```

3. **Capture on save.** A postfix on the internal `GetReferenceWithChildren(ref uint, PartInstance, bool)` replaces `__result.Id` with a marker when the part owns nodes. The marker is `iron-man:v1:` plus base64 JSON of the original id, the stock connector count and each anchor. Refuse to save if the stock-prefix, owned-suffix layout is broken, and round-trip your own output through `Decode` so you never write something the loader would reject:

```csharp
// iron-man.lib/IronManConnectorPatches.cs L52-L56
    private static void SerializePostfix(Part __instance, PartInstance __result)
    {
        if (IronManConnectors.GetOwned(__instance).Count != 0)
            __result.Id = IronManConnectorData.Capture(__instance);
    }
```

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

4. **Decode on load.** A prefix on the `Part(string, PartTemplate, PartInstance, Part)` constructor decodes `PartInstance.Id`; on a marker it restores the original runtime name into the `inName` argument (`ref string __0`) and passes the data through `__state`. Ordinary ids return null. For anything starting with the marker family, enforce the exact version, a length cap, the backpack template, the stock connector count and numeric bounds, and throw rather than continue into index regeneration with missing connectors:

```csharp
// iron-man.lib/IronManConnectorData.cs L55-L83
    internal static IronManConnectorData? Decode(string? id, PartTemplate template)
    {
        if (id == null || !id.StartsWith(MarkerFamily, StringComparison.Ordinal)) return null;
        try
        {
            if (!id.StartsWith(Marker, StringComparison.Ordinal))
                throw new InvalidDataException("Unsupported Iron Man anchor save version.");
            if (id.Length > 32000 || template.Id != IronManConnectors.BackpackTemplateId)
                throw new InvalidDataException("Iron Man anchor data is invalid for this part.");
            var data = JsonSerializer.Deserialize<IronManConnectorData>(Convert.FromBase64String(id[Marker.Length..]))
                ?? throw new InvalidDataException("Empty Iron Man anchor data.");
            if (data.OriginalId == null || data.OriginalId.Length > 1024 || data.OriginalId.StartsWith(MarkerFamily, StringComparison.Ordinal) ||
                data.StockConnectorCount != template.Connectors.Count || data.Anchors == null ||
                data.Anchors.Count == 0 || data.Anchors.Count > IronManConnectors.MaximumAnchors)
                throw new InvalidDataException("Iron Man anchor data or the stock connector layout has changed.");
            foreach (var anchor in data.Anchors)
            {
                if (anchor == null || string.IsNullOrWhiteSpace(anchor.Name) || anchor.Name.Length > 80)
                    throw new InvalidDataException("Invalid Iron Man anchor name.");
                IronManConnectors.Validate(ToVector(anchor.Position), ToVector(anchor.Direction), anchor.Radius);
            }
            return data;
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException or InvalidDataException)
        {
            // Never continue to stock connector-index regeneration after a damaged marked save.
            throw new InvalidDataException("Cannot load this Iron Man kitten: its saved anchors are invalid. " + ex.Message, ex);
        }
    }
```

5. **Restore in a constructor postfix.** Re-add the anchors after the stock connectors, before the game resolves saved indices:

```csharp
// iron-man.lib/IronManConnectorPatches.cs L58-L65
    private static void ConstructPrefix(ref string __0, PartTemplate __1, PartInstance? __2,
        out IronManConnectorData? __state)
    {
        __state = IronManConnectorData.Decode(__2?.Id, __1);
        if (__state != null) __0 = __state.OriginalId;
    }

    private static void ConstructPostfix(Part __instance, IronManConnectorData? __state) => __state?.Restore(__instance);
```

```csharp
// iron-man.lib/IronManConnectorData.cs L85-L89
    internal void Restore(Part part)
    {
        foreach (var anchor in Anchors)
            IronManConnectors.Add(part, anchor.Name, ToVector(anchor.Position), ToVector(anchor.Direction), anchor.Radius);
    }
```

6. **Install the three hooks transactionally.** The internal serializer overload and the constructor are matched by exact parameter types, and a miss throws `MissingMethodException` so installation fails instead of silently skipping persistence:

```csharp
// iron-man.lib/IronManConnectorPatches.cs L15-L37
    public static void Apply(Harmony harmony)
    {
        if (_applied) return;
        _serialize = AccessTools.Method(typeof(Part), nameof(Part.GetReferenceWithChildren),
            new[] { typeof(uint).MakeByRefType(), typeof(PartInstance), typeof(bool) })
            ?? throw new MissingMethodException("Part.GetReferenceWithChildren(ref uint, PartInstance, bool)");
        _construct = AccessTools.Constructor(typeof(Part),
            new[] { typeof(string), typeof(PartTemplate), typeof(PartInstance), typeof(Part) })
            ?? throw new MissingMethodException("Part(string, PartTemplate, PartInstance, Part)");
        try
        {
            harmony.Patch(_serialize, postfix: new HarmonyMethod(typeof(IronManConnectorPatches), nameof(SerializePostfix)));
            harmony.Patch(_construct,
                prefix: new HarmonyMethod(typeof(IronManConnectorPatches), nameof(ConstructPrefix)),
                postfix: new HarmonyMethod(typeof(IronManConnectorPatches), nameof(ConstructPostfix)));
            _applied = true;
        }
        catch
        {
            Remove(harmony);
            throw;
        }
    }
```

7. **Plan the unload.** If your hooks leave while a live tree still has connections on owned nodes, the next save would write indices past the stock connector count with no marker to restore them. Iron Man first converts each connection on an owned node to a stock part-surface link (disconnect, reconnect with the parent `Part`), rolls everything back on any failure and, if it cannot convert, keeps the passive persistence hooks installed. Persistence hooks are removed last:

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

8. **Check the result.** Attach a tank to the Up node in the editor, return to flight, save, reload: the kitten has the same nodes and the same attached parts. Duplicate the equipment subtree in the editor: the copy keeps its nodes. In the log you should see no `InvalidDataException` from the decoder.

## Complete example

- [iron-man.lib/IronManConnectors.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManConnectors.cs) - node authoring, ownership table, transform maths.
- [iron-man.lib/IronManConnectorData.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManConnectorData.cs) - the marker codec and layout checks.
- [iron-man.lib/IronManConnectorPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManConnectorPatches.cs) - the three Harmony hooks.
- [iron-man.lib/IronManPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManPatches.cs) - install order and conditional removal.
- Feature page: [Per-instance attachment nodes that survive saves](../../../../mods/unscience/features/iron-man-connector-serialization.md).

## Variations and gotchas

- **Saves need the mod.** A save with connected nodes written while the mod is loaded cannot be read by stock KSA: it would index connectors that do not exist. Unload conversion protects saves made after unload; it cannot rewrite files already on disk.
- **The stock connector count is pinned.** The decoder requires `StockConnectorCount` to equal the current template's connector count, so a game update that adds a connector to `KittenBackPackPart` makes old marked saves fail loudly by design.
- **`Part.Tree` is nullable (since 5482).** A root registered by the constructor postfix can be treeless if deserialization aborts; the unload path filters roots with `.OfType<PartTree>()`.
- **`RecomputeAllDerivedData` only marks data dirty (since 5482).** Resource-graph rebuild errors surface at the next `FlushDirtyDerived`, not inside your transaction.
- **Using `PartInstance.Id` is safe only because lookups use `InstanceOf`.** The restored runtime id comes from the marker's `OriginalId`, so nothing else sees the marker text.
- **Tests run against fixtures, not `KSA.dll`.** The managed checks cannot detect signature drift; the exact-parameter-type lookups in `Apply` are the real guard.

## See also

- [Approach 1: open the editor and guard the body](open-the-vehicle-editor-on-a-live-eva-kitten-1.md), [approach 2: draw the kitten upright](open-the-vehicle-editor-on-a-live-eva-kitten-2.md), [topic overview](overview.md)
- Mod feature: [Iron Man connector serialization](../../../../mods/unscience/features/iron-man-connector-serialization.md), [Iron Man kitten vehicle editing](../../../../mods/unscience/features/iron-man-kitten-vehicle-editing.md)
- [Evolve a shipped part without breaking saves](../../../vehicles-and-parts/parts/evolve-a-shipped-part-without-breaking-saves/overview.md), [Define a part with game data](../../../vehicles-and-parts/parts/define-a-part-with-game-data/overview.md)
- [Mutate vehicles at a physics-safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md), [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
