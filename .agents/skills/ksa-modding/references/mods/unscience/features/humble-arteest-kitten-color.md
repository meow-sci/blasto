---
type: Mod Feature
title: Kitten tinting by staged writes into the GPU material buffer
description: Kitten Color finds Program.MaterialSystem and its protected AssetMap and DeviceCtx by reflection and overwrites MaterialData.AlbedoColor for chosen materials with a one-shot Vulkan staging upload into GpuMaterialSystem.BigBuffer, which ModelPbr.frag multiplies into the albedo and alpha-tests
mod: unscience
submod: humble-arteest
category: kittens
techniques: [reflection, vulkan-buffer]
hooks:
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent; Initialize, Apply, Reset)
  - reflection KSA.Program.MaterialSystem
  - reflection KSA.AssetManager.AssetMap (protected)
  - reflection KSA.GpuObjectSystem.DeviceCtx (protected)
  - reflection KSA.GpuObjectAssetRef.Handle
lifecycle: [frame:draw-ui, event:load]
game_types:
  - KSA.Program
  - KSA.GpuMaterialSystem
  - KSA.GpuObjectSystem
  - KSA.AssetManager
  - KSA.GpuObjectAssetRef
  - KSA.MaterialData
  - KSA.CharacterRenderResources
  - RenderCore.VkUtils
  - Brutal.VulkanApi.Abstractions.StagingPool
tags: [kittens, materials, gpu-buffer, vulkan, reflection, tint]
---

Kitten Color tints kitten fur, body, eyes and helmet by changing the colour multiplier of their
materials on the GPU. One colour can be applied to every registered material, or colours can be set
per material from a filterable table; Reset writes white back. An alpha below 0.1 makes surfaces drawn
with `ModelPbr.frag` disappear.[^kitten-color-submod]

Mechanism in one line: every character and glTF material lives as an 80-byte `MaterialData` record
in `GpuMaterialSystem.BigBuffer`, indexed by the material's handle, so writing a `float4` at
`handle * sizeof(MaterialData) + offsetof(AlbedoColor)` with a staging copy recolours every mesh using
that material, with no shader change.[^kitten-color][^ksa-materialdata]

## How it works

1. **Discovery** (`KittenColor.Initialize`, on the panel's Initialize button): get `KSA.Program` from
   the game assembly, read `Program.Instance`, then `MaterialSystem`; walk the type hierarchy for the
   protected `AssetMap` field (a `ConcurrentDictionary<AssetName, GpuObjectAssetRef>` declared on
   `AssetManager<T>`), the public `BigBuffer` property and the protected `DeviceCtx` field (declared on
   `GpuObjectSystem<T>`).[^kitten-color][^ksa-gpuobjectsystem][^ksa-assetmanager]
2. **Listing**: each `AssetMap` entry's key is the material name and its value's public `Handle` field
   is the slot in the big buffer. The list is cached and sorted.[^ksa-gpuobjectassetref]
3. **Write** (`WriteAlbedoColor`): offset =
   `handle * ByteSize.Of<MaterialData>() + Marshal.OffsetOf<MaterialData>("AlbedoColor")` (16); create a
   one-command-buffer staging pool on `DeviceCtx.MainQueue`, record
   `VkUtils.StageAndUploadToBuffer(pool, BigBuffer.VkBuffer, offset, bytes, cmd)`, end the command
   buffer, and let the `using` disposal submit and wait (`StagingPool.Dispose` calls `Submit()` then
   `Wait()`).[^kitten-color][^ksa-stagingpool][^ksa-vkutils]
4. **Ownership**: before the first write to a handle, the original colour (from the shared
   `MaterialColorState`, defaulting to white) and the exact asset object are recorded. Reset, disable
   and unload restore only handles whose `AssetMap` entry is still that same asset object, because
   renderer-owned materials can be disposed and their integer slot reused.[^kitten-color-persistence][^material-color-state]

**Why it shows up.** `ModelPbr.frag` (static, skinned and two-sided skinned PBR pipelines) calls
`GetMaterialData`, which computes `albedo = mat.albedoColor * texture(...)`, and discards when
`albedo.a < 0.1`. The kitten's glass and eyes use `ModelTranslucent.frag`, which reads the same
material but hard-codes opacity (0.75 for glass, 0.3 for eyes) and mixes the glass colour 90 % toward
dark grey, so a tint barely shows there and alpha cannot hide the visor; that is what the
[visor toggle](./humble-arteest-kitten-visor-toggle.md) is for.[^ksa-modelpbr-frag][^ksa-materialset-glsl][^ksa-modeltranslucent-frag]
Vehicle parts never read this buffer (`MeshIndirect.frag` samples textures named in its per-draw
data), which is why vehicle paint needs its own transport.[^humble-readme]

## Lifecycle and timing

Every write is triggered from the ImGui panel (`RenderContent`, run from `[StarMapAfterGui]`), i.e.
`frame:draw-ui`, or from the save participant on load (`event:load`). The upload is a blocking
transfer submitted on the main graphics queue before this frame's command buffer, so the new colour
is visible in the same frame. It does not wait for frames already in flight; those may still render
the old colour.[^kitten-color] There are no Harmony patches and no per-frame work: the colour persists
in the GPU buffer until something rewrites the record: a material created again through
`GpuObjectSystem.CreateObject` (as `CharacterRenderResources` does for kitten materials) gets a fresh
record, and other mods may write the same slot.[^ksa-characterrenderresources]

## Key code

| Path | Role |
|---|---|
| [humble-arteest.lib/KittenColor.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/KittenColor.cs#L48-L122) | Reflection discovery and material listing |
| [humble-arteest.lib/KittenColor.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/KittenColor.cs#L133-L230) | Apply-to-all, per-material apply/reset, staged write |
| [humble-arteest.lib/KittenColor.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/KittenColor.Persistence.cs) | Original-colour tracking, identity-checked restore, save capture/restore by material name |
| [humble-arteest.lib/KittenColorSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/KittenColorSubmod.cs#L57-L85) | Active toggle, reset on deactivate and dispose |
| [ksa-abstractions.lib/Persistence/MaterialColorState.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/MaterialColorState.cs) | Shared record of mod-written colours (also used by doh and Free Fallin) |

## Snippets

Reflection chain to the material system:

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

The staged write of one `float4`:

```csharp
// humble-arteest.lib/KittenColor.cs L204-L222
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
```

Restoring only colours this mod still owns:

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

Where the tint and the alpha test take effect:

```glsl
// current/Content/Core/Shaders/Mesh/ModelPbr.frag L64-L67
    GetMaterialData(materialID, uv, albedo, normalMap, roughness, metallic, ao, emissive);
    
    if(albedo.a < 0.1)
        discard;
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Program.Instance`, `Program.MaterialSystem` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L119) | The material system (created with 512 slots at L1002) |
| `GpuObjectSystem<T>.BigBuffer`, `DeviceCtx` (protected) | [current/decomp/KSA/GpuObjectSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuObjectSystem.cs#L12-L32) | Device-local storage buffer of `capacity * sizeof(T)` and its Vulkan context |
| `AssetManager<T>.AssetMap` (protected) | [current/decomp/KSA/AssetManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetManager.cs#L9-L19) | Name to asset-ref map |
| `GpuObjectAssetRef.Handle` | [current/decomp/KSA/GpuObjectAssetRef.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuObjectAssetRef.cs#L7) | Record index in the big buffer |
| `MaterialData` | [current/decomp/KSA/MaterialData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MaterialData.cs) | `[StructLayout(Sequential, Pack = 1)]`, `AlbedoColor` at byte 16, 80-byte stride |
| `VkUtils.StageAndUploadToBuffer(StagingPool, VkBuffer, ByteSize, Span<byte>, CommandBuffer)` | [current/decomp/RenderCore/VkUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/VkUtils.cs#L82-L100) | Staging copy into the device-local buffer |
| `StagingPool.Dispose` | [current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs#L214-L218) | Submits and waits, so `using` is enough |
| `GetMaterialData` / `Material.albedoColor` | [current/Content/Core/Shaders/Common/MaterialSet.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/MaterialSet.glsl#L12-L31) | `albedo = mat.albedoColor * texture(...)` |
| Glass and eye opacity | [current/Content/Core/Shaders/Mesh/ModelTranslucent.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/ModelTranslucent.frag#L68-L81) | Hard-coded opacity; ignores material alpha |
| `CharacterRenderResources` material creation | [current/decomp/KSA/CharacterRenderResources.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterRenderResources.cs#L58-L69) | Kitten materials registered in the same system |

## Pitfalls and version notes

- **It is not kitten-only.** "Apply to All" writes every entry in `AssetMap`, which includes glTF
  model materials and other mods' runtime materials. The save capture skips `doh_*` and
  `free-fallin/` names because those features persist their own colours.[^kitten-color-persistence]
- **Handles are recycled.** Never persist a handle; persist the material name and re-resolve, and
  verify asset identity before restoring (as `ResetOwnedColors` does).
- **No GPU readback.** The "original" colour is whatever `MaterialColorState` recorded or white;
  colours written by other code without that tracker are not restored.[^material-color-state]
- `MaterialData` and the reflected member names were unchanged in 5554; a rename of `AssetMap`,
  `DeviceCtx` or `BigBuffer` makes Initialize fail with a message rather than crash.
- KSA 5472 changed the BRDF lookup in `ModelPbr.frag`, so tinted kittens shade slightly differently
  than on older builds (cosmetic).

## See also

- [Kitten visor toggle](./humble-arteest-kitten-visor-toggle.md)
- [doh runtime GPU materials](./doh-runtime-gpu-materials.md) (creates private kitten materials in the same system)
- [Free Fallin canopy material substitution](./free-fallin-canopy-material-substitution.md) (creates a material instead of editing one)
- [Vehicle paint](./humble-arteest-vehicle-paint-shader-patch.md)
- [Humble Arteest sub-mod](../submods/humble-arteest.md) and the [mod overview](../overview.md)
- [Recolour every kitten with one staged float4 write into the shared material buffer](../../../howto/kittens/appearance/tint-and-restyle-kitten-appearance/tint-and-restyle-kitten-appearance-1.md) - how-to article demonstrating this feature
- [Restore kitten colours by material name, only where the asset object is still the one written](../../../howto/kittens/characters/restore-kitten-customizations-after-a-load/restore-kitten-customizations-after-a-load-2.md) - how-to article demonstrating this feature
- [7:7:7 colour in the unused StateBitFlag high bits plus a patched MeshIndirect.frag](../../../howto/rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/paint-vessel-parts-with-custom-colours-1.md) - how-to article demonstrating this feature

[^kitten-color]: humble-arteest.lib/KittenColor.cs
[^kitten-color-persistence]: humble-arteest.lib/KittenColor.Persistence.cs
[^kitten-color-submod]: humble-arteest.lib/KittenColorSubmod.cs
[^material-color-state]: ksa-abstractions.lib/Persistence/MaterialColorState.cs
[^humble-readme]: humble-arteest.lib/README.md (authors' design notes)
[^ksa-gpuobjectsystem]: KSA.GpuObjectSystem (decompiled, build 2026.10.10.5554)
[^ksa-assetmanager]: KSA.AssetManager (decompiled, build 2026.10.10.5554)
[^ksa-gpuobjectassetref]: KSA.GpuObjectAssetRef (decompiled, build 2026.10.10.5554)
[^ksa-materialdata]: KSA.MaterialData (decompiled, build 2026.10.10.5554)
[^ksa-characterrenderresources]: KSA.CharacterRenderResources (decompiled, build 2026.10.10.5554)
[^ksa-stagingpool]: Brutal.VulkanApi.Abstractions.StagingPool (decompiled, build 2026.10.10.5554)
[^ksa-vkutils]: RenderCore.VkUtils (decompiled, build 2026.10.10.5554)
[^ksa-modelpbr-frag]: Mesh/ModelPbr.frag (game shader, build 2026.10.10.5554)
[^ksa-materialset-glsl]: Common/MaterialSet.glsl (game shader header, build 2026.10.10.5554)
[^ksa-modeltranslucent-frag]: Mesh/ModelTranslucent.frag (game shader, build 2026.10.10.5554)
