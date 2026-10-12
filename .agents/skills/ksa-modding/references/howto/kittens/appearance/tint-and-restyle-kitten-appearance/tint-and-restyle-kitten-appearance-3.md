---
type: How-To
title: Bind mod-owned material clones to the EVA renderables and restore them conditionally
description: Create mod-named material clones, write their handles into the protected MaterialIndices arrays reached through KittenEva._renderable and KittenRenderable._characterAvatar, and restore only slots that still hold your handle
category: kittens
group: appearance
topic: tint-and-restyle-kitten-appearance
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/vehicle-and-eva-paint]
techniques: [reflection, vulkan-buffer]
hooks:
  - starmap StarMapBeforeGui
  - starmap StarMapAfterOnFrame
  - reflection KSA.KittenEva._renderable
  - reflection KSA.KittenRenderable._characterAvatar
lifecycle: [frame:draw-ui, frame:after-on-frame, frame:render]
game_types:
  - KSA.KittenEva
  - KSA.KittenRenderable
  - KSA.CharacterAvatar
  - KSA.GpuMaterialSystem
  - KSA.GpuObjectSystem
  - KSA.AnimatedRenderable
  - KSA.CatFurRenderable
  - KSA.StaticMeshRenderable
  - KSA.CharacterRenderResources
tags: [kitten, material, eva, clone, gatos]
---
gatOS recolours the EVA suit, fur, helmet, visor and MMU of every kitten by **binding its own material clones** to the kitten's renderables, never editing a shared material. It is opt-in, reversible, and conservative: it records the original handle of every slot it changes, restores a slot only if it still holds the clone it wrote, and frees every clone it owns. This is the minimal, typed-access version of [approach 2](tint-and-restyle-kitten-appearance-2.md): no pool-budget framework, a clone pool keyed by (source handle, quantised colour) and capped by a setting. Prerequisites: StarMap, the reflection step into the avatar ([reach a kitten's avatar and skeleton](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md)), and a place to tick once per frame. Demonstrated by gatOS: [Vehicle and EVA paint](../../../../mods/gatos/features/vehicle-and-eva-paint.md) (the vehicle-part half of that feature is covered by [paint vessel parts with custom colours](../../../rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/overview.md)).

## Background: how the game does it

A kitten's meshes draw with integer handles from protected `MaterialIndices` arrays on `AnimatedRenderable` (body, MMU), `CatFurRenderable` (fur) and `StaticMeshRenderable` (helmet, visor). The material records live in a fixed 512-slot pool that frees by handle:

```csharp
// current/decomp/KSA/StaticMeshRenderable.cs L27
	protected readonly int[] MaterialIndices;
```

```csharp
// current/decomp/KSA/GpuObjectSystem.cs L77-L83
	public void Free(int handle)
	{
		lock (BigBufferAllocator)
		{
			BigBufferAllocator.Remove(handle);
		}
	}
```

The avatar is reached through two private fields, `KittenEva._renderable` and `KittenRenderable._characterAvatar`, and the structs on it are public (`Core`, `Fur`, `Attachments`):

```csharp
// current/decomp/KSA/KittenEva.cs L15
	private KittenRenderable _renderable;
```

```csharp
// current/decomp/KSA/CharacterAvatar.cs L73-L83
	public struct CharacterAttachments
	{
		public Helmet Helmet = default(Helmet);

		public Mmu Mmu = default(Mmu);

		public List<CosmeticAttachment> CosmeticAttachments = new List<CosmeticAttachment>();

		public CharacterAttachments()
		{
		}
```

## Lifecycle and timing

gatOS ticks the EVA binder once per frame from its per-frame driver, which runs from `[StarMapBeforeGui]` (so before the scene renders) and, when the HUD is hidden and that hook does not fire, from `[StarMapAfterOnFrame]` where the swap first shows in the next frame. `PaintManager.Tick` calls `EvaPaintBridge.Tick` only when the kitten master is on. The material swaps are array writes, so they take effect when the kitten records its draws in the same frame's `RenderGame`; creating a clone is a blocking staging upload on the main thread. See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Cache the two private fields.** Resolve `KittenEva._renderable` and `KittenRenderable._characterAvatar` once; both are looked up with `BindingFlags.NonPublic`:

```csharp
// gatOS.GameMod/Game/Ksa/Paint/EvaPaintBridge.cs L15-L18
    private static readonly FieldInfo EvaRenderable = typeof(KittenEva).GetField("_renderable",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo AvatarField = typeof(KittenRenderable).GetField("_characterAvatar",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
```

2. **Enumerate material slots per renderable.** Walk the five renderables of the avatar and record, for every entry of each protected `MaterialIndices` array, the array, the index and the original handle. Name slots `category`, `category.1`, ... by ordinal. The body skips the sclera mesh indices so eyes stay untouched:

```csharp
// gatOS.GameMod/Game/Ksa/Paint/EvaPaintBridge.cs L141-L150
    private static List<Slot> BuildSlots(CharacterAvatar avatar)
    {
        var slots = new List<Slot>();
        AddRenderable(slots, avatar.Core.CharacterModel, "body", avatar.Core.ScleraMeshIndices);
        AddRenderable(slots, avatar.Fur.CatFurRenderable, "fur", null);
        AddRenderable(slots, avatar.Attachments.Helmet.HelmetMesh, "helmet", null);
        AddRenderable(slots, avatar.Attachments.Helmet.VisorMesh, "visor", null);
        AddRenderable(slots, avatar.Attachments.Mmu.MmuMesh, "mmu", null);
        return slots;
    }
```

```csharp
// gatOS.GameMod/Game/Ksa/Paint/EvaPaintBridge.cs L152-L166
    private static void AddRenderable(List<Slot> slots, object? renderable, string category,
        ICollection<int>? excluded)
    {
        if (renderable is null) return;
        var field = FindField(renderable.GetType(), "MaterialIndices")
            ?? throw new MissingFieldException(renderable.GetType().FullName, "MaterialIndices");
        var array = (int[])field.GetValue(renderable)!;
        var ordinal = 0;
        for (var i = 0; i < array.Length; i++)
        {
            if (excluded?.Contains(i) == true) continue;
            var name = ordinal++ == 0 ? category : $"{category}.{ordinal - 1}";
            slots.Add(new Slot(array, i, name, array[i]));
        }
    }
```

3. **Bind per kitten, rebinding when the avatar changes.** Look the kitten up by id; if its avatar object changed (a reload), restore the old binding first. For each slot, resolve a colour rule (gatOS's `store.ResolveKitten` is mod-specific policy; replace it with your own lookup), key a clone by (source handle, quantised colour), create it if missing within the cap, and write the clone handle into the array:

```csharp
// gatOS.GameMod/Game/Ksa/Paint/EvaPaintBridge.cs L80-L116
    private static void Apply(KittenEva eva, PaintStore store, ISet<string> names)
    {
        var renderable = EvaRenderable.GetValue(eva) ?? throw new InvalidOperationException("KittenEva._renderable is null");
        var avatar = (CharacterAvatar)(AvatarField.GetValue(renderable)
            ?? throw new InvalidOperationException("KittenRenderable._characterAvatar is null"));
        if (!Bindings.TryGetValue(eva.Id, out var binding) || !ReferenceEquals(binding.Avatar, avatar))
        {
            if (binding is not null) Restore(binding);
            binding = new Binding(avatar, BuildSlots(avatar));
            Bindings[eva.Id] = binding;
        }

        foreach (var slot in binding.Slots)
        {
            names.Add(slot.Name);
            var rule = store.ResolveKitten(eva.Id, slot.Name);
            if (rule is null)
            {
                RestoreSlot(slot);
                continue;
            }
            var key = new CloneKey(slot.OriginalHandle, PaintBits.Encode(rule.Color));
            if (!Clones.TryGetValue(key, out var clone))
            {
                if (Clones.Count >= store.Current.MaterialCloneCap)
                    throw new InvalidOperationException($"EVA material clone cap {store.Current.MaterialCloneCap} reached");
                clone = CreateClone(key, rule.Color);
                Clones.Add(key, clone);
            }
            clone.Uses++;
            if (slot.CloneHandle >= 0 && slot.CloneHandle != clone.Asset.Handle
                && slot.Array[slot.Index] == slot.CloneHandle)
                slot.Array[slot.Index] = slot.OriginalHandle;
            slot.Array[slot.Index] = clone.Asset.Handle;
            slot.CloneHandle = clone.Asset.Handle;
        }
    }
```

4. **Clone a material.** Allocate the clone with `GpuMaterialSystem.CreateObject` under a mod-owned name, copying the source's textures. A fur material is rebuilt from `CharacterRenderResources.FurTexture` and `FurSampler` with the mask texture named in the material id, the same recipe the game uses; other materials come from the `PbrMaterialReference`:

```csharp
// gatOS.GameMod/Game/Ksa/Paint/EvaPaintBridge.cs L172-L183
    private static Clone CreateClone(CloneKey key, PaintColor color)
    {
        var system = Program.Instance.MaterialSystem;
        var source = FindAsset(key.SourceHandle)
            ?? throw new InvalidOperationException($"source material handle {key.SourceHandle} is not registered");
        var data = BuildMaterialData(source);
        data.AlbedoColor = new float4((float)color.R, (float)color.G, (float)color.B, 1f);
        var name = $"gatOS.Paint/{key.SourceHandle}/{unchecked((uint)key.ColorBits):X8}";
        if (!system.CreateObject(name, data))
            throw new InvalidOperationException($"could not allocate EVA material clone '{name}'");
        return new Clone(name, system.GetOrLoad(name));
    }
```

```csharp
// gatOS.GameMod/Game/Ksa/Paint/EvaPaintBridge.cs L185-L219
    private static MaterialData BuildMaterialData(GpuObjectAssetRef source)
    {
        var id = source.Id.ToString();
        var texture = Program.Instance.TextureSystem;
        if (id.EndsWith("_FurMaterial", StringComparison.Ordinal))
        {
            var parts = id[..^"_FurMaterial".Length].Split(',');
            if (parts.Length != 3) throw new InvalidOperationException($"unrecognized fur material '{id}'");
            var resources = Program.Instance.CharacterRenderResources;
            return new MaterialData
            {
                ExtraData = new float4(resources.FurTexture.BindlessHandle, resources.FurSampler.BindlessIndex,
                    texture.GetOrLoad(parts[2]).BindlessHandle, 0),
                NormalTexture = texture.GetOrLoad(parts[1]).BindlessHandle,
                RoughnessMetalScale = float4.One,
                AlbedoTexture = texture.GetOrLoad(parts[0]).BindlessHandle,
                RoughMetallicAOTexture = Program.Instance.SuperMeshRenderSystem.GltfSystem.BlankMaterialTexture.BindlessHandle,
                AlbedoColor = float4.One,
                Sampler = texture.SamplerRepeatHandle,
                EmissiveTexture = texture.DefaultBlackTexture.BindlessHandle,
            };
        }

        var material = ModLibrary.Get<PbrMaterialReference>(id).Get();
        return new MaterialData
        {
            AlbedoTexture = material.DiffuseReference?.Get().BindlessHandle ?? texture.DefaultWhiteTexture.BindlessHandle,
            Sampler = texture.SamplerRepeatHandle,
            AlbedoColor = float4.One,
            NormalTexture = material.NormalReference?.Get().BindlessHandle ?? texture.DefaultWhiteTexture.BindlessHandle,
            RoughMetallicAOTexture = material.PBRMap?.Get().BindlessHandle ?? texture.DefaultWhiteTexture.BindlessHandle,
            RoughnessMetalScale = float4.One,
            EmissiveTexture = material.EmissiveMap?.Get().BindlessHandle ?? texture.DefaultBlackTexture.BindlessHandle,
        };
    }
```

5. **Restore conditionally and free by name.** A slot is restored to its original handle only when the array still holds gatOS's handle (another mod may have swapped it). A clone is freed by removing its name from `AssetMap` and disposing the removed asset ref, which calls `Free(handle)`:

```csharp
// gatOS.GameMod/Game/Ksa/Paint/EvaPaintBridge.cs L234-L243
    private static void RemoveClone(CloneKey key)
    {
        if (!Clones.Remove(key, out var clone)) return;
        var system = Program.Instance.MaterialSystem;
        _assetMapField ??= FindField(system.GetType(), "AssetMap")!;
        var map = _assetMapField.GetValue(system)!;
        var method = map.GetType().GetMethods().First(m => m.Name == "TryRemove" && m.GetParameters().Length == 2);
        var args = new object?[] { clone.Asset.Id, null };
        if ((bool)method.Invoke(map, args)! && args[1] is GpuObjectAssetRef removed) removed.Dispose();
    }
```

```csharp
// gatOS.GameMod/Game/Ksa/Paint/EvaPaintBridge.cs L250-L255
    private static void RestoreSlot(Slot slot)
    {
        if (slot.CloneHandle >= 0 && slot.Array[slot.Index] == slot.CloneHandle)
            slot.Array[slot.Index] = slot.OriginalHandle;
        slot.CloneHandle = -1;
    }
```

6. **Prune each frame.** The `Tick` loop counts clone uses, rebinds every live `KittenEva` found in `Universe.CurrentSystem`, restores bindings whose kitten is gone, and removes clones with zero uses; any exception disables the feature and publishes a degraded status instead of throwing into the frame:

```csharp
// gatOS.GameMod/Game/Ksa/Paint/EvaPaintBridge.cs L24-L64
    internal static void Tick(PaintStore store)
    {
        try
        {
            foreach (var clone in Clones.Values) clone.Uses = 0;
            var live = new HashSet<string>(StringComparer.Ordinal);
            var materialNames = new HashSet<string>(StringComparer.Ordinal);
            if (Universe.CurrentSystem is { } system)
                foreach (var astronomical in system.All.UnsafeAsList())
                    if (astronomical is KittenEva eva)
                    {
                        live.Add(eva.Id);
                        Apply(eva, store, materialNames);
                    }

            foreach (var id in Bindings.Keys.Where(id => !live.Contains(id)).ToArray())
            {
                Restore(Bindings[id]);
                Bindings.Remove(id);
            }
            foreach (var key in Clones.Where(x => x.Value.Uses == 0).Select(x => x.Key).ToArray())
                RemoveClone(key);

            _peak = Math.Max(_peak, Clones.Count);
            store.PublishRuntime(s => s with
            {
                KittensStatus = KittenPaintStatus.Active,
                MaterialNames = materialNames.Order(StringComparer.Ordinal).ToArray(),
                ActiveKittenBindings = Bindings.Values.Sum(b => b.Slots.Count(s => s.CloneHandle >= 0)),
                LiveMaterialClones = Clones.Count,
                PeakMaterialClones = _peak,
                KittenError = "",
            });
        }
        catch (Exception ex)
        {
            Disable(store);
            store.SetKittensMaster(false);
            store.PublishRuntime(s => s with { KittensStatus = KittenPaintStatus.Degraded, KittenError = ex.Message });
        }
    }
```

7. **Check the result.** Enable kitten paint with a colour rule for `body`; the suit recolours, clone count in the status equals the distinct (source, colour) pairs, and disabling the master restores stock rendering and frees the clones.

## Complete example

- [gatOS.GameMod/Game/Ksa/Paint/EvaPaintBridge.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/EvaPaintBridge.cs) - the whole bridge (`Tick`, `Apply`, `BuildSlots`, `CreateClone`, `RemoveClone`, `Restore`).
- Feature page: [Vehicle and EVA paint](../../../../mods/gatos/features/vehicle-and-eva-paint.md).

## Variations and gotchas

- **Pool pressure.** Each (source, colour) pair costs a slot of the fixed 512-slot pool; gatOS caps clones by a setting (`MaterialCloneCap`) and throws, disabling the feature, when the cap is reached. Read [approach 2](tint-and-restyle-kitten-appearance-2.md) for a fuller budget.
- **Material order is a live contract.** Slots are named by array ordinal. The mod's notes record that a game update swapped the MMU's `<Materials>` order, so a saved rule for `mmu` repainted the body instead of the label decals; check array lengths and ordering after updates.
- **Head hiding is not a break.** `KittenRenderable.HideHead` (set per viewport by an IVA seat) skips masked head meshes and the fur draw, so the occupant's fur paint is invisible in that view by design.
- **Typed access.** Because `CreateObject`, `GetOrLoad` and the avatar structs are public, only `_renderable`, `_characterAvatar`, `AssetMap` and the `MaterialIndices` field itself need reflection.
- **Verification date.** The mod's own anchor notes were last verified against earlier builds; re-check `MaterialIndices` field names on every update.

## See also

- [Topic overview](overview.md); siblings: [approach 1](tint-and-restyle-kitten-appearance-1.md), [approach 2](tint-and-restyle-kitten-appearance-2.md), [approach 4](tint-and-restyle-kitten-appearance-4.md), [approach 5](tint-and-restyle-kitten-appearance-5.md)
- Mod feature: [Vehicle and EVA paint](../../../../mods/gatos/features/vehicle-and-eva-paint.md)
- [Paint vessel parts with custom colours](../../../rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/overview.md), [Reach a kitten's avatar and skeleton](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
