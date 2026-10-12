---
type: How-To
title: Recolour every kitten with one staged float4 write into the shared material buffer
description: Write AlbedoColor into GpuMaterialSystem.BigBuffer at handle times sizeof(MaterialData) plus 16 through a staging copy, track originals by asset identity, and restore only handles you still own
category: kittens
group: appearance
topic: tint-and-restyle-kitten-appearance
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/humble-arteest-kitten-color]
techniques: [vulkan-buffer, reflection]
hooks:
  - starmap StarMapAfterGui (ImGui panel)
  - reflection KSA.Program.MaterialSystem
  - reflection KSA.AssetManager.AssetMap
  - reflection KSA.GpuObjectSystem.DeviceCtx
lifecycle: [frame:draw-ui, event:load, frame:render]
game_types:
  - KSA.GpuMaterialSystem
  - KSA.GpuObjectSystem
  - KSA.MaterialData
  - KSA.AssetManager
  - KSA.GpuObjectAssetRef
  - KSA.Program
tags: [kitten, material, tint, gpu-buffer, humble-arteest]
---
Every kitten material is an 80-byte `MaterialData` record in one GPU buffer, indexed by the material's handle. Writing a `float4` at the right byte offset in that buffer recolours every mesh that uses the material, with no shader change and no per-kitten data. This article builds that write: find the material system, list the named materials, push one `AlbedoColor` through a staging copy, remember the original colour, and restore only what you still own. It tints all kittens that share a material, which is the cheapest of the five approaches and also the bluntest. Prerequisites: StarMap, and a way to run code from a UI callback. Demonstrated by Unscience's Humble Arteest Kitten Color panel: [Kitten Color GPU material buffer writes](../../../../mods/unscience/features/humble-arteest-kitten-color.md). If you need a different colour per kitten, read [approach 2](tint-and-restyle-kitten-appearance-2.md) or [approach 3](tint-and-restyle-kitten-appearance-3.md) instead.

## Background: how the game does it

Character and glTF materials are created in `Program.MaterialSystem`, a `GpuMaterialSystem` built with a fixed capacity of 512 records. The base class stores the records in one device-local storage buffer and keeps a name-to-asset map:

```csharp
// current/decomp/KSA/Program.cs L1002
		MaterialSystem = new GpuMaterialSystem(this, TextureSystem, 512, IsRaytracingActive ? (VkShaderStageFlags.RaygenBitKHR | VkShaderStageFlags.AnyHitBitKHR | VkShaderStageFlags.ClosestHitBitKHR) : VkShaderStageFlags.None);
```

```csharp
// current/decomp/KSA/GpuObjectSystem.cs L14-L39
	protected readonly FreeListIndexPool BigBufferAllocator;

	protected readonly IVulkanContext DeviceCtx;

	public BufferEx BigBuffer { get; protected set; }

	public GpuObjectSystem(IGlobalRenderSystem rs, int capacity)
		: base(1024)
	{
		DeviceCtx = rs.DeviceContext;
		BigBufferAllocator = new FreeListIndexPool(capacity, allowResize: false);
		BigBuffer = rs.Globals.Allocator.CreateBuffer(new BufferEx.CreateInfo
		{
			Name = "GpuObjectSystem Backing Buffer",
			BufferSize = capacity * ByteSize.Of<T>(),
			BufferUsage = (VkBufferUsageFlags.TransferDstBit | VkBufferUsageFlags.StorageBufferBit),
			AllocRequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit
		});
	}

	public virtual void Dispose()
	{
		BigBuffer.Dispose();
		AssetMap.Clear();
	}
```

A material is a `MaterialData` struct (`Pack = 1`, sequential). Four ints (`AlbedoTexture`, `NormalTexture`, `RoughMetallicAOTexture`, `Sampler`) come first, so `AlbedoColor` starts at byte 16, and each record is 80 bytes. `GpuObjectAssetRef.Handle` is the record index:

```csharp
// current/decomp/KSA/MaterialData.cs L6-L30
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MaterialData
{
	public int AlbedoTexture;

	public int NormalTexture;

	public int RoughMetallicAOTexture;

	public int Sampler;

	public float4 AlbedoColor;

	public float4 RoughnessMetalScale;

	public float4 ExtraData;

	public int EmissiveTexture;

	public int Padding0;

	public int Padding1;

	public int Padding2;
}
```

```csharp
// current/decomp/KSA/GpuObjectAssetRef.cs L5-L25
public class GpuObjectAssetRef : LoadedAssetRef
{
	public readonly int Handle;

	protected IHandleManager<int>? Source;

	public GpuObjectAssetRef(AssetName id, IHandleManager<int>? source, int handle)
	{
		Source = source;
		Handle = handle;
		base.Id = id;
	}

	protected override void Destroy()
	{
		if (Source != null)
		{
			Source.Free(Handle);
		}
	}
}
```

The fragment shaders multiply the sampled albedo by `AlbedoColor`, and the opaque PBR shader discards fragments whose resulting alpha falls below 0.1, so an alpha under 0.1 makes a surface vanish:

```glsl
// current/Content/Core/Shaders/Mesh/ModelPbr.frag L64-L67
    GetMaterialData(materialID, uv, albedo, normalMap, roughness, metallic, ao, emissive);
    
    if(albedo.a < 0.1)
        discard;
```

Glass and eyes use `ModelTranslucent.frag`, which reads the same record but ignores its alpha; the visor and eye branches hard-code their opacity and the glass colour is mixed 90 percent toward dark grey, so a tint barely shows there (use [approach 4](tint-and-restyle-kitten-appearance-4.md) or [5](tint-and-restyle-kitten-appearance-5.md) to hide the visor):

```glsl
// current/Content/Core/Shaders/Mesh/ModelTranslucent.frag L72-L81
    // TODO TEMP - HARD CODED
    #ifdef EYE
    float opacity = 0.3;//albedo.a;
    roughness = 0.33;
    metallic = 0.2;
    ao = 1;
    vec3 color = vec3(0.15,0.15,0.15);
    #else
    float opacity = 0.75;
    vec3 color = mix(albedo.xyz,vec3(0.1,0.1,0.1), 0.9);
```

Vehicle parts never read this buffer (their paint needs a separate transport, see [paint vessel parts with custom colours](../../../rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/overview.md)).

## Lifecycle and timing

| Work | Where | Lifecycle |
|---|---|---|
| Discovery and every colour write | ImGui callbacks (`RenderContent` from `[StarMapAfterGui]`) | `frame:draw-ui` |
| Replay of saved colours | save-participant load callback | `event:load` |

The write is a blocking transfer: `StagingPool.Dispose` calls `Submit()` then `Wait()`, so it completes on the main graphics queue before this frame's command buffer records and the new colour is visible in the same frame. It does not wait for frames already in flight, which may still render the old colour. There are no Harmony patches and no per-frame work: the colour persists in the buffer until something rewrites the record. A material created again through `GpuObjectSystem.CreateObject` (as `CharacterRenderResources.CreateFurMaterial` does for kitten fur) gets a fresh record, and other mods may write the same slot. See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Reach the material system.** `Program.MaterialSystem` is a public readonly field, but `AssetMap` (on `AssetManager<T>`) and `DeviceCtx` (on `GpuObjectSystem<T>`) are protected, and `BigBuffer` is a public property declared on the base. Humble Arteest reads everything by name and walks the type hierarchy, and reports a message instead of throwing if anything is missing:

```csharp
// humble-arteest.lib/KittenColor.cs L56-L74
            var programType = typeof(Part).Assembly.GetType("KSA.Program");
            if (programType == null) { _lastError = "KSA.Program type not found."; return false; }

            var instanceProp = programType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            if (instanceProp == null) { _lastError = "Program.Instance not found."; return false; }

            var programInstance = instanceProp.GetValue(null);
            if (programInstance == null) { _lastError = "Program.Instance is null (game not fully loaded?)."; return false; }

            _materialSystem = GetFieldOrProp(programType, programInstance, "MaterialSystem");
            if (_materialSystem == null) { _lastError = "MaterialSystem not found on Program."; return false; }

            _assetMap = FindFieldInHierarchy(_materialSystem, "AssetMap") as IDictionary;
            if (_assetMap == null) { _lastError = "AssetMap not found in MaterialSystem hierarchy."; return false; }

            _bigBufferProp = _materialSystem.GetType().GetProperty("BigBuffer",
                BindingFlags.Public | BindingFlags.Instance);

            _deviceCtxField = FindFieldInfoInHierarchy(_materialSystem.GetType(), "DeviceCtx");
```

2. **List materials by name.** Each `AssetMap` entry's key is the material name and its value's public `Handle` field is the slot. Cache the sorted list and let the UI refresh it. Never persist a handle; persist the name and re-resolve.

3. **Write one `float4`.** The offset is `handle * sizeof(MaterialData) + offsetof(AlbedoColor)`. Create a one-command-buffer staging pool on the main queue, record `VkUtils.StageAndUploadToBuffer` into `BigBuffer.VkBuffer`, end the command buffer, and let the `using` disposal submit and wait:

```csharp
// humble-arteest.lib/KittenColor.cs L196-L230
    private static bool WriteAlbedoColor(int handle, float4 color)
    {
        if (_materialSystem == null || _bigBufferProp == null || _deviceCtxField == null)
        {
            _lastError = "MaterialSystem not fully initialized.";
            return false;
        }

        try
        {
            var bigBuffer = (BufferEx)_bigBufferProp.GetValue(_materialSystem)!;
            var deviceCtx = (IVulkanContext)_deviceCtxField.GetValue(_materialSystem)!;

            int albedoColorOffset = (int)Marshal.OffsetOf<MaterialData>(nameof(MaterialData.AlbedoColor));
            ByteSize targetOffset = handle * ByteSize.Of<MaterialData>() + albedoColorOffset;

            using var stagingPool = deviceCtx.Device.CreateStagingPool(deviceCtx.MainQueue, 1);
            var commandBuffer = stagingPool.NextCommandBuffer();

            float4 colorCopy = color;
            var span = new Span<float4>(ref colorCopy);

            commandBuffer.Begin();
            VkUtils.StageAndUploadToBuffer(stagingPool, bigBuffer.VkBuffer, targetOffset, MemoryMarshal.AsBytes(span), commandBuffer);
            commandBuffer.End();

            return true;
        }
        catch (Exception ex)
        {
            _lastError = $"Error writing material handle {handle}: {ex.Message}";
            Console.WriteLine($"humble-arteest: KittenColor {_lastError}");
            return false;
        }
    }
```

4. **Remember the original colour.** The GPU buffer has no readback path in the mod, so the "original" is whatever was recorded in the shared `MaterialColorState` (default white). Before the first write to a handle, record the original together with the exact asset object that owned the handle:

```csharp
// humble-arteest.lib/KittenColor.cs L173-L182
    public static bool ApplyToMaterial(int handle, float4 color)
    {
        _lastError = null;
        if (handle < 0) { _lastError = "Invalid material handle."; return false; }
        var original = MeowSci.KsaAbstractions.Persistence.MaterialColorState.GetOrDefault(handle, float4.One);
        if (!WriteAlbedoColor(handle, color)) return false;
        TrackColor(handle, original, color);
        MeowSci.KsaAbstractions.Persistence.MaterialColorState.Record(handle, color);
        return true;
    }
```

```csharp
// humble-arteest.lib/KittenColor.Persistence.cs L16-L28
    private static void TrackColor(int handle, float4 original, float4 color)
    {
        if (_assetMap == null) return;
        foreach (DictionaryEntry material in _assetMap)
        {
            string? name = material.Key?.ToString();
            if (name == null || material.Key == null || material.Value == null || ReadHandle(material.Value) != handle) continue;
            if (!OriginalColors.TryGetValue(handle, out var previous) || !ReferenceEquals(previous.Asset, material.Value))
                OriginalColors[handle] = new OriginalMaterial(material.Key, name, material.Value, original);
            AppliedColors[name] = color;
            break;
        }
    }
```

5. **Restore only what you still own.** Renderer-owned materials can be disposed and their integer slot reused between edits. Restore a handle only when its `AssetMap` entry is still the same asset object:

```csharp
// humble-arteest.lib/KittenColor.Persistence.cs L45-L59
    internal static void ResetOwnedColors()
    {
        foreach (var pair in OriginalColors)
        {
            var original = pair.Value;
            // Renderer-owned assets can be disposed and their integer slot reused between edits.
            if (_assetMap == null || !ReferenceEquals(_assetMap[original.Key], original.Asset)
                || ReadHandle(original.Asset) != pair.Key) continue;
            if (!WriteAlbedoColor(pair.Key, original.Color))
                throw new InvalidOperationException(LastError ?? "Could not restore a material's original color.");
            MaterialColorState.Record(pair.Key, original.Color);
        }
        OriginalColors.Clear();
        AppliedColors.Clear();
    }
```

6. **Apply to everything or to one.** "Apply to All" loops over the cached list and calls `ApplyToMaterial` for each handle; the table UI calls it for one. Alpha below 0.1 hides surfaces drawn with `ModelPbr.frag`, so clamp or warn on alpha in your UI.

```csharp
// humble-arteest.lib/KittenColor.cs L133-L150
    public static bool ApplyToAll(float4 color)
    {
        _lastError = null;
        var materials = GetMaterials();
        if (materials.Length == 0)
        {
            _lastError = "No materials available.";
            return false;
        }

        int successCount = 0;
        foreach (var (name, handle) in materials)
        {
            if (handle < 0) continue;
            if (ApplyToMaterial(handle, color))
                successCount++;
        }
```

7. **Check the result.** Open the panel in flight with a kitten in view, apply a colour: fur, suit and helmet shell change in the same frame; the visor and eyes barely change. Reset writes white back. In the log you should see `humble-arteest: KittenColor Applied color to N/M materials.`

## Complete example

- [humble-arteest.lib/KittenColor.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/KittenColor.cs) - reflection discovery, listing, apply and staged write.
- [humble-arteest.lib/KittenColor.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/KittenColor.Persistence.cs) - original-colour tracking, identity-checked restore, save capture by material name.
- Feature page: [Kitten Color GPU material buffer writes](../../../../mods/unscience/features/humble-arteest-kitten-color.md).

## Variations and gotchas

- **It is not kitten-only.** "Apply to All" writes every entry in `AssetMap`, which includes glTF model materials and other mods' runtime materials. Filter by name if you only want kitten materials. The save capture skips `doh_*` and `free-fallin/` names because those features persist their own colours.
- **All kittens sharing a material change together.** Stock kittens of the same character share material handles; use [approach 2](tint-and-restyle-kitten-appearance-2.md) for per-kitten colour.
- **Handles are recycled.** Persist the material name and verify asset identity before restoring.
- **No GPU readback.** Colours written by other code without the shared tracker are not restored.
- **Reflected member names.** A rename of `AssetMap`, `DeviceCtx` or `BigBuffer` makes initialization fail with a message rather than crash. `MaterialData` and these names were unchanged in build 2026.10.10.5554.
- **BRDF lookup changed in 5472.** Tinted kittens shade slightly differently than on older builds (cosmetic).

## See also

- [Topic overview](overview.md); siblings: [approach 2](tint-and-restyle-kitten-appearance-2.md), [approach 3](tint-and-restyle-kitten-appearance-3.md), [approach 4](tint-and-restyle-kitten-appearance-4.md), [approach 5](tint-and-restyle-kitten-appearance-5.md)
- Mod features: [Kitten Color](../../../../mods/unscience/features/humble-arteest-kitten-color.md), [doh runtime GPU materials](../../../../mods/unscience/features/doh-runtime-gpu-materials.md)
- [Restore kitten customizations after a load](../../characters/restore-kitten-customizations-after-a-load/overview.md), [Paint vessel parts with custom colours](../../../rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/overview.md), [Replace a game texture at runtime](../../../rendering/textures-and-decals/replace-a-game-texture-at-runtime/overview.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
