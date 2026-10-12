---
type: Mod Feature
title: ShaderExtensions push constants and uniform buffers from C#
description: Declares ShaderExtensions' marker attributes locally in the ShaderExtensions namespace so the framework discovers the mod's buffer structs by name, gets lookup delegates injected into static fields, and writes an enabled flag and time value into three per-pass push constants each frame
mod: ksa-aircrafthud
category: shaders
techniques: [inter-mod-api, reflection, post-processing, xml-asset]
hooks: [harmony-postfix KSA.Program.OnFrameEditor]
lifecycle: [startup:starmap-before-main, frame:viewports, render:post-process]
game_types: [KSA.KeyHash, Brutal.VulkanApi.Abstractions.BufferEx, Brutal.VulkanApi.Abstractions.MappedMemory]
tags:
  - shaders
  - shaderextensions
  - push-constants
  - uniform-buffer
  - duck-typing
  - attributes
  - reflection
  - keyhash
---

AircraftHUD talks to ShaderExtensions without using a single type from `ShaderExtensions.dll`.
It copies the framework's four marker attributes into its own assembly under the `ShaderExtensions`
namespace, marks two structs with them, and lets ShaderExtensions find the structs by attribute
name, register new XML elements for them, and fill static delegate fields with lookup functions.
Each frame the mod calls a lookup with a `KeyHash` of the XML `Id` and writes into the returned
`Span<T>`.[^ah-sxhelpers] [^sx-readme]

## How it works

1. **Attribute shims.** [ShaderExtensionsHelpers.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ShaderExtensionsHelpers.cs#L12-L33)
   declares `internal` `SxUniformBufferAttribute(string xmlElement)`,
   `SxUniformBufferLookupAttribute`, `SxPushConstantAttribute(string xmlElement)`,
   `SxPushConstantLookupAttribute` and four delegate types in `namespace ShaderExtensions`. They mirror
   ShaderExtensions' own internal declarations in `Attributes.cs`.
2. **Discovery.** In a `Program.Main` prefix, `AssetEx.Init` loops over every assembly in every
   `AssemblyLoadContext` and calls `RegisterAll` on those where
   `asm.GetType("ShaderExtensions.SxUniformBufferAttribute")` is non-null. `RegisterAttr` then
   compares attribute types by `FullName`, never by identity
   ([ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs#L17-L108)).[^sx-assetex] This is
   why the shims must live in exactly that namespace, and why a mod that only uses push constants
   still has to declare `SxUniformBufferAttribute`: it is the probe.
3. **XML element registration.** `[SxPushConstant("PPPushConstantsBufferAsset")]` on
   `PPPushConstantsBuffer` makes `<PPPushConstantsBufferAsset>` a valid child of any `ShaderEx`
   (including `PostProcessingShader`) and of an asset bundle; `[SxUniformBuffer("AircraftHUDBufferAsset")]`
   does the same for `<AircraftHUDBufferAsset>`
   ([ShaderExtensions/PushConstantEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PushConstantEx.cs#L19-L33),
   [ShaderExtensions/UniformBufferEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/UniformBufferEx.cs#L22-L35)).
4. **Lookup injection.** For each static field tagged with the lookup attribute, ShaderExtensions
   inspects the delegate's `Invoke` return type and binds a matching static method: `Span<T>` for
   push constants; `BufferEx`, `MappedMemory`, `Span<T>` or `T*` for uniform buffers
   ([ShaderExtensions/PushConstantEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PushConstantEx.cs#L34-L56),
   [ShaderExtensions/UniformBufferEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/UniformBufferEx.cs#L37-L74)).[^sx-pushconstantex]
   [^sx-uniformbufferex]
5. **Storage.** Each `<PPPushConstantsBufferAsset Id=...>` element becomes a
   `PushConstantBindingReference<T>` registered by its `Id` hash; `GetSpan(key)` returns a one-element
   span over its stored value ([ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L126-L145)).[^sx-binding]
   Push constants are per shader, so the mod declares three (`PPPushConstantsBuffer0..2`), one per pass
   ([HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml#L7-L19)).[^ah-assets]
6. **Per-frame write.** `SetPostProcess(enabled, frame)` checks that the delegate was injected, then
   writes the same two fields into all three spans
   ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L936-L954)). The shaders read them as
   `layout(push_constant, std430) uniform Params { int enabled; float frame; } pp;`
   ([Shaders/GaussianBlurH.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/GaussianBlurH.frag#L12-L15)).[^ah-blurh]

What the C# side sends, in full:

| Binding | Fields | Written | Value |
|---|---|---|---|
| `PPPushConstantsBuffer0/1/2` (push constants) | `int enabled`, `float frame` | Every frame the HUD draws | `enabled` = 1 in IVA or with "Show Effects", else 0; `frame` = accumulated `dtPlayer` seconds |
| `HudTestBuffer` (`AircraftHUDBuffer`, uniform) | `float V1` | Never (writer commented out; it once sent the throttle) | Unused |

No attitude, speed or altitude data goes to the GPU; all HUD symbology is CPU-drawn.

## Lifecycle and timing

- **`startup:starmap-before-main`.** Discovery and delegate injection run in ShaderExtensions'
  `Program.Main` prefix ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L20-L25)),
  after StarMap has loaded every mod assembly into its context and before any asset XML is
  parsed.[^sx-patches] The spans only exist after the asset bundles load (`startup:asset-load`), so
  the README warns to call lookups only after that.
- **`frame:viewports`.** The mod writes in the `Program.OnFrameEditor` postfix.
- **`render:post-process`.** `ShaderEx.PushConstants` copies the stored value into the command buffer
  when each pass is recorded ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L95-L103)),
  later in the same frame. No synchronization is needed because both happen on the main thread.[^sx-shaderex]

## Key code

| Path | Role |
|---|---|
| [ShaderExtensionsHelpers.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ShaderExtensionsHelpers.cs#L12-L33) | Attribute and delegate shims |
| [ShaderExtensionsHelpers.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ShaderExtensionsHelpers.cs#L48-L73) | `AircraftHUDBuffer` and `PPPushConstantsBuffer` structs |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L60-L63) | Cached `KeyHash`es for the three push constants |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L936-L954) | `SetPostProcess` |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1029-L1036) | Commented-out uniform buffer write |

## Snippets

```csharp
// ShaderExtensionsHelpers.cs L12-L33
// Needs to be in ShaderExtensions namespace for shader buffer generation to work
// SxImGui can be in the normal namespace
namespace ShaderExtensions
{
     //Needed for shader buffers
    [AttributeUsage(AttributeTargets.Struct)]
    internal class SxUniformBufferAttribute(string xmlElement) : Attribute;

    [AttributeUsage(AttributeTargets.Field)]
    internal class SxUniformBufferLookupAttribute() : Attribute;

    [AttributeUsage(AttributeTargets.Struct)]
    internal class SxPushConstantAttribute(string xmlElement) : Attribute;

    [AttributeUsage(AttributeTargets.Field)]
    internal class SxPushConstantLookupAttribute() : Attribute;

    public delegate BufferEx AHBufferLookup(KeyHash hash);
    public delegate MappedMemory AHMemoryLookup(KeyHash hash);
    public delegate Span<T> AHSpanLookup<T>(KeyHash hash) where T : unmanaged;
    public unsafe delegate T* AHPtrLookup<T>(KeyHash hash) where T : unmanaged;
}
```

```csharp
// ShaderExtensionsHelpers.cs L64-L73
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    [SxPushConstant("PPPushConstantsBufferAsset")]
    public struct PPPushConstantsBuffer
    {
        public int enabled;
        public float frame;

        // lookup delegate fields must be static fields on the buffer element type
        [SxPushConstantLookup] public static AHSpanLookup<PPPushConstantsBuffer> LookupSpan; // gives a Span<T> of length Size
    }
```

```csharp
// AircraftHUDMain.cs L936-L954
        public void SetPostProcess(int enabled, float frame)
        {
            if (PPPushConstantsBuffer.LookupSpan != null)
            {
                //CurrentProfile.FilmGrainPreTime += (float)dt * CurrentProfile.FilmGrainPreTimeMultiplier * (CurrentProfile.FilmGrainPreTimeWarpMultiplier ? (float)Universe.SimulationSpeed : 1f);

                Span<PPPushConstantsBuffer> PP0 = PPPushConstantsBuffer.LookupSpan(PPBuffer0Hash);
                PP0[0].enabled = enabled;
                PP0[0].frame = frame;

                Span<PPPushConstantsBuffer> PP1 = PPPushConstantsBuffer.LookupSpan(PPBuffer1Hash);
                PP1[0].enabled = enabled;
                PP1[0].frame = frame;

                Span<PPPushConstantsBuffer> PP2 = PPPushConstantsBuffer.LookupSpan(PPBuffer2Hash);
                PP2[0].enabled = enabled;
                PP2[0].frame = frame;
            }
        }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `KeyHash.Make(ReadOnlySpan<char>)`, `KeyHash.Code` | [current/decomp/KSA/KeyHash.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KeyHash.cs#L9-L20) | Ids of buffers and shaders[^ksa-keyhash] |
| `BufferEx` | [current/decomp/Brutal.VulkanApi.Abstractions/BufferEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/BufferEx.cs) | Return type of the raw buffer lookup delegate |
| `MappedMemory` | [current/decomp/Brutal.VulkanApi.Abstractions/MappedMemory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/MappedMemory.cs) | Return type of the mapped-memory lookup delegate |

## Pitfalls and version notes

- **Lookup throws on an unknown id.** `GetSpan` dereferences `LookupCollection.Get(key)`, which
  returns null for a missing key
  ([current/decomp/KSA/LookupCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LookupCollection.cs#L170-L173)),
  so a typo in the XML `Id` ends in a `NullReferenceException` every frame the HUD draws.
  `SetPostProcess` only null-checks the delegate.[^ksa-lookupcollection] A pass that ShaderExtensions
  later skips (see
  [Canopy-glass post-process chain](glass-post-process-chain.md#pitfalls-and-version-notes)) does not
  cause this: `PushConstantBindingReference.OnDataLoad` registers the push constant when the asset
  loads, and the skip only happens when the chain is built
  ([ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L150-L155)),
  so on the pinned version the writes succeed and simply reach no pass.[^sx-binding]
- **Unknown XML attributes are ignored.** `PushConstantBindingReference<T>` has no `Size` attribute
  (only `Stage`), so the `Size="1"` on each `<PPPushConstantsBufferAsset>` is dropped silently by the
  XML serializer; KSA registers no unknown-attribute handler. Size comes from the struct.
- **Layout.** `Pack = 1` is fine here (`int` + `float`, 8 bytes, 4-byte aligned). Push constants must
  be 4-byte aligned and at most 128 bytes; uniform buffers follow std140, so `vec3` and `mat3` need
  padding (ShaderExtensions README).
- **The `ShaderExtensions` project reference is unused at runtime.** `BufferEx` and `MappedMemory` are
  game types and `KeyHash` is in `KSA`; nothing in the two C# files resolves to a ShaderExtensions
  type. A built copy of this mod once committed as a test fixture in the ShaderExtensions repo
  (removed in its commit `2d76148`) shipped `AircraftHUD.deps.json` listing `ShaderExtensions`, but
  no `ShaderExtensions.dll` beside it.
- **Duplicate names across mods.** XML element names are global. Two mods registering the same
  element name for different structs collide; prefix them with the mod name (this mod uses
  `AircraftHUDBufferAsset`, but the generic `PPPushConstantsBufferAsset`).

## See also

- [Canopy-glass post-process chain](glass-post-process-chain.md)
- [Spatiotemporal blue-noise dithering](blue-noise-dithering.md)
- [Curved, chromatic HUD window via an ImGuiShader](imgui-window-hud-shader.md)
- [KittenExtensions custom XML asset types](../../kittenextensions/features/custom-xml-asset-types.md)
  (the same duck-typed attribute technique for asset classes)
- [Duck-type an API: copy look-alike attributes and bind by full name across load contexts](../../../howto/getting-started/dependencies/share-types-between-mods/share-types-between-mods-3.md) - how-to article demonstrating this feature
- [Feed a shader with uniform buffers: Span, pointer and BufferEx lookups beside push constants in a HUD mod](../../../howto/shaders/post-processing/feed-uniform-buffers-to-a-shaderextensions-shader/feed-uniform-buffers-to-a-shaderextensions-shader-3.md) - how-to article demonstrating this feature
- [Push constants for a shader: one element per pass with an enabled flag and time, written from OnFrameEditor](../../../howto/shaders/post-processing/send-push-constants-to-a-shaderextensions-shader/send-push-constants-to-a-shaderextensions-shader-1.md) - how-to article demonstrating this feature

[^ah-sxhelpers]: KSA_AircraftHUD/ShaderExtensionsHelpers.cs
[^ah-assets]: KSA_AircraftHUD/HudAssests.xml
[^ah-blurh]: KSA_AircraftHUD/Shaders/GaussianBlurH.frag
[^sx-readme]: ShaderExtensions/README.md
[^sx-assetex]: ShaderExtensions AssetEx.cs
[^sx-pushconstantex]: ShaderExtensions PushConstantEx.cs
[^sx-uniformbufferex]: ShaderExtensions UniformBufferEx.cs
[^sx-binding]: ShaderExtensions ShaderBinding.cs
[^sx-patches]: ShaderExtensions Patches.cs
[^sx-shaderex]: ShaderExtensions ShaderEx.cs
[^ksa-keyhash]: KSA.KeyHash (decompiled, build 2026.10.10.5554)
[^ksa-lookupcollection]: KSA.LookupCollection (decompiled, build 2026.10.10.5554)
