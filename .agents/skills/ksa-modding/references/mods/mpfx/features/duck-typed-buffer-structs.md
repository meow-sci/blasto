---
type: Mod Feature
title: Uniform buffer and push constant structs bound by attribute name
description: MPFX tags blittable structs with private copies of ShaderExtensions' SxUniformBuffer and SxPushConstant attributes, and ShaderExtensions finds them by name, registers XML elements and fills their static lookup delegates, so MPFX never references ShaderExtensions.dll
mod: mpfx
category: shaders
techniques: [inter-mod-api, reflection, vulkan-buffer, xml-asset]
hooks:
  - harmony-prefix KSA.Program.Main (ShaderExtensions AssetEx.Init)
  - reflection static delegate fields filled by ShaderExtensions
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types: [KSA.KeyHash, KSA.SerializedId, Brutal.Numerics.float4x4]
tags: [shaders, uniform-buffer, push-constants, reflection, inter-mod-api, std140, shaderextensions]
---

To write to a ShaderExtensions uniform buffer, a mod needs a struct type and some way to get at the
GPU memory. MPFX does this without a compile-time dependency on ShaderExtensions. It declares internal
attribute classes and delegate types inside a `namespace ShaderExtensions` block in its own assembly
([MPFX/Buffers.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers.cs)), tags its structs with them, and leaves static delegate fields
on each struct for ShaderExtensions to fill in. The csproj has no ShaderExtensions reference, and CI
never downloads it ([.github/workflows/build.yml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/.github/workflows/build.yml#L159-L160)).[^mpfx-buffers] [^mpfx-ci]

## How it works

1. **Private attribute copies.** `SxUniformBufferAttribute(string xmlElement)`,
   `SxUniformBufferLookupAttribute`, `SxPushConstantAttribute(string xmlElement)` and
   `SxPushConstantLookupAttribute` are declared `internal` in namespace `ShaderExtensions`, matching
   ShaderExtensions' own declarations ([ShaderExtensions/Attributes.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Attributes.cs#L17-L30)).
   MPFX adds its own delegate types `MPFXBufferLookup`, `MPFXMemoryLookup`, `MPFXSpanLookup<T>` and
   `MPFXPtrLookup<T>`. Only the delegate signature matters.[^sx-attributes]
2. **Discovery.** In a Harmony prefix on `Program.Main`, ShaderExtensions calls `AssetEx.Init`. That
   walks `AssemblyLoadContext.All`. Any assembly where
   `asm.GetType("ShaderExtensions.SxUniformBufferAttribute")` resolves gets every type's
   `GetCustomAttributesData()` compared by `AttributeType.FullName`
   ([ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs#L17-L108)). Matching by name lets each mod's private copy
   work across load contexts.[^sx-assetex]
3. **Registration.** For `[SxUniformBuffer("X")]` on struct `T`, `UniformBufferEx.AddUniformBufferGeneric<T>`
   adds element `X` of type `UniformBindingReference<T>` to `ShaderEx.XmlBindings` and to
   `AssetBundle.Assets`. It then reflects over `T`'s static fields marked `[SxUniformBufferLookup]` and
   assigns each one a delegate created from `GetBuffer`, `GetMappedMemory`, `GetSpan` or `GetPtr`,
   chosen by the delegate's return type (`BufferEx`, `MappedMemory`, `Span<T>`, `T*`)
   ([ShaderExtensions/UniformBufferEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/UniformBufferEx.cs#L22-L75)). Push constants work the same way, but only the `Span<T>`
   lookup is accepted ([ShaderExtensions/PushConstantEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PushConstantEx.cs#L19-L57)).[^sx-uniformbufferex] [^sx-pushconstantex]
4. **What the memory is.** A `UniformBindingReference<T>` with `Size > 0` creates, at bind time, one
   `UniformBufferBit` buffer of `sizeof(T) * Size` bytes in `HostVisible | HostCoherent` memory and keeps
   it mapped. `GetSpan` returns `mappedMemory.AsSpan<T>()`, so writing through the span writes GPU-visible
   memory directly. The descriptor type is `UniformBufferDynamic`, always bound at dynamic offset 0. A push constant is a plain `T value` field, and its `GetSpan` returns a one-element
   span over that field ([ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L63-L162)).[^sx-shaderbinding]
5. **MPFX's structs** (all `[StructLayout(LayoutKind.Sequential, Pack = 1)]`):

   | Struct | XML element | Layout | Used by |
   |---|---|---|---|
   | `MPFXDefaultBuffer` | `MPFXDefaultBufferAsset` | `float a, b` | brightness, contrast, saturation, temperature, hue, experimental effects |
   | `MPFXVec4Buffer` | `MPFXVec4BufferAsset` | `float4 a, b` | overlay, vignette, film grain |
   | `MPFXMat4Buffer` | `MPFXMat3BufferAsset` (sic) | `float4x4 a, b` | color balance |
   | `MPFXGaussianBlurBuffer` | `MPFXGaussianBlurBufferAsset` | `int Radius` + 3 padding ints + `fixed float Weights[24]` | blur |
   | `MPFXPushConstantsFloatBuffer` | `MPFXPushConstantsFloatBufferAsset` (push constant) | `float a` | film grain time |
   | `MPFXIntFloatBuffer` | `MPFXIntFloatBufferAsset` | `int a; float b` | unused |

6. **Matrix layout.** `Brutal.Numerics.float4x4` stores four row vectors `X, Y, Z, W` in sequence, and its
   indexer is `[row, column]` ([current/decomp/Brutal.Numerics/float4x4.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/float4x4.cs#L70-L107)).
   GLSL `mat4` is column-major, so C# row `r` arrives as GLSL column `preData[r]`. MPFX uses this
   deliberately: row 0 holds lift, row 1 gamma, row 2 gain, and element `[3,3]` is the on/off flag.[^ksa-float4x4]

## Lifecycle and timing

- **`startup:starmap-before-main`.** `AssetEx.Init` runs in ShaderExtensions' `Program.Main` prefix,
  after StarMap has loaded every mod assembly ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L20-L25)). From this point
  the lookup delegates are non-null, and the new element names exist for the XML serializers.[^sx-patches]
- **`startup:asset-load`.** Each `<MPFX...BufferAsset Id=".." Size="1"/>` registers itself in
  `OnDataLoad`. Its GPU buffer is created only when the game runs `ModLibrary.Bind`, which calls every
  registered binder's `Bind` in parallel from the `Program` constructor after the asset bundles are
  loaded ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1849-L1860)).[^ksa-modlibrary] Calling
  `LookupSpan` earlier fails, because there is either no registered buffer or no mapped memory yet.
  MPFX only calls the delegates from its per-frame hook, which runs long after load.
- The delegate fields are static and process-wide. Any code in the mod can call
  `MPFXDefaultBuffer.LookupSpan(KeyHash.Make("MPFXBrightnessBuffer"))` once loading has finished.

## Key code

| Path | Role |
|---|---|
| [MPFX/Buffers.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers.cs) | Private attribute and delegate declarations in `namespace ShaderExtensions` |
| [MPFX/Buffers/MPFXDefaultBuffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXDefaultBuffer.cs) | Two-float buffer showing all four lookup kinds |
| [MPFX/Buffers/MPFXGaussianBlurBuffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXGaussianBlurBuffer.cs) | Hand-padded std140 layout with a fixed array |
| [MPFX/Buffers/MPFXPushConstantsFloatBuffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXPushConstantsFloatBuffer.cs) | Push-constant struct |
| [ShaderExtensions/UniformBufferEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/UniformBufferEx.cs) (ShaderExtensions) | Fills the delegates by return type |

## Snippets

```csharp
// MPFX/Buffers.cs L7-L25
namespace ShaderExtensions
{
    [AttributeUsage(AttributeTargets.Struct)]
    internal class SxUniformBufferAttribute(string xmlElement) : Attribute;

    [AttributeUsage(AttributeTargets.Field)]
    internal class SxUniformBufferLookupAttribute() : Attribute;

    [AttributeUsage(AttributeTargets.Struct)]
    internal class SxPushConstantAttribute(string xmlElement) : Attribute;

    [AttributeUsage(AttributeTargets.Field)]
    internal class SxPushConstantLookupAttribute() : Attribute;

    public delegate BufferEx MPFXBufferLookup(KeyHash hash);
    public delegate MappedMemory MPFXMemoryLookup(KeyHash hash);
    public delegate Span<T> MPFXSpanLookup<T>(KeyHash hash) where T : unmanaged;
    public unsafe delegate T* MPFXPtrLookup<T>(KeyHash hash) where T : unmanaged;
}
```

```csharp
// MPFX/Buffers/MPFXDefaultBuffer.cs L8-L22
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[SxUniformBuffer("MPFXDefaultBufferAsset")]
public struct MPFXDefaultBuffer
{
    public float a;
    public float b;

    // lookup delegate fields must be static fields on the buffer element type
    // the names and specific types of these are not relevant, as long as the delegate signature matches
    // these are not all required, but you will need at least one to be able to set the uniform data
    [SxUniformBufferLookup] public static MPFXBufferLookup LookupBuffer;
    [SxUniformBufferLookup] public static MPFXMemoryLookup LookupMemory;
    [SxUniformBufferLookup] public static MPFXSpanLookup<MPFXDefaultBuffer> LookupSpan; // gives a Span<T> of length Size
    [SxUniformBufferLookup] public static MPFXPtrLookup<MPFXDefaultBuffer> LookupPtr; // gives T* to first element
}
```

```csharp
// MPFX/Buffers/MPFXPushConstantsFloatBuffer.cs L9-L17
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[SxPushConstant("MPFXPushConstantsFloatBufferAsset")]
public struct MPFXPushConstantsFloatBuffer
{
    public float a;

    // lookup delegate fields must be static fields on the buffer element type
    [SxPushConstantLookup] public static MPFXSpanLookup<MPFXPushConstantsFloatBuffer> LookupSpan; // gives a Span<T> of length Size
}
```

```csharp
// ShaderExtensions/UniformBufferEx.cs L42-L74 (ShaderExtensions)
var staticFields = typeof(T).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
foreach (var field in staticFields)
{
    var ftype = field.FieldType;
    foreach (var attr in field.GetCustomAttributesData())
    {
        if (attr.AttributeType.FullName != typeof(SxUniformBufferLookupAttribute).FullName)
            continue;

        var rtype = ftype.GetMethod("Invoke")?.ReturnType;
        if (rtype == null)
        {
            DefaultCategory.Log.Warning($"{ftype} {typeof(T)}.{field.Name} is not a delegate type");
            continue;
        }

        MethodInfo lookup = null;

        if (rtype == typeof(BufferEx))
            lookup = bufMethod;
        else if (rtype == typeof(MappedMemory))
            lookup = memMethod;
        else if (rtype == typeof(Span<T>))
            lookup = spanMethod;
        else if (rtype == typeof(T*))
            lookup = ptrMethod;

        if (lookup != null)
            field.SetValue(null, lookup.CreateDelegate(ftype));
        else
            DefaultCategory.Log.Warning($"{typeof(T)}.{field.Name} return {rtype} is not a valid lookup type");
    }
}
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `KSA.KeyHash.Make(ReadOnlySpan<char>)` | [current/decomp/KSA/KeyHash.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KeyHash.cs#L15) | Turns a buffer `Id` string into the lookup key[^ksa-keyhash] |
| `Brutal.Numerics.float4x4`, `float4` | [current/decomp/Brutal.Numerics/float4x4.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/float4x4.cs#L70-L107) | Blittable vector and matrix fields that match GLSL `vec4`/`mat4` |
| `Brutal.VulkanApi.Abstractions.BufferEx`, `MappedMemory` | via ShaderExtensions | Return types of two of the lookup kinds |

## Pitfalls and version notes

- `AssetEx.Init` only scans assemblies in which `ShaderExtensions.SxUniformBufferAttribute` itself
  resolves. A mod that only uses push constants must still declare that attribute type, or its
  `[SxPushConstant]` structs are never registered.
- The XML element name comes from the attribute string, not the struct name.
  `MPFXMat4Buffer` registers `MPFXMat3BufferAsset`, which is easy to misread.
- `Pack = 1` plus std140 means every padding field is your job. A `vec3` or `mat3` in GLSL needs manual
  padding, as the ShaderExtensions README warns.
- Push constants must be 4-byte aligned, and all push constants of one shader together may not exceed
  128 bytes. ShaderExtensions throws otherwise, and it allows only one push-constant element per shader.
- The uniform buffer is a single host-coherent allocation. It is not per frame in flight, so a CPU write
  may become visible to a frame the GPU is still executing. MPFX writes only when a setting changes, so
  this is not visible in practice.

## See also

- [Per-frame effect parameter upload](per-frame-parameter-upload.md)
- [Separable Gaussian blur with CPU-computed weights](separable-gaussian-blur.md)
- [Custom XML asset types via KxAsset attributes](../../kittenextensions/features/custom-xml-asset-types.md), the same name-matched attribute contract that KittenExtensions uses
- [Duck-type an API: copy look-alike attributes and bind by full name across load contexts](../../../howto/getting-started/dependencies/share-types-between-mods/share-types-between-mods-3.md) - how-to article demonstrating this feature
- [Feed a shader with uniform buffers: Pack=1 structs with manual std140 padding, one block per effect](../../../howto/shaders/post-processing/feed-uniform-buffers-to-a-shaderextensions-shader/feed-uniform-buffers-to-a-shaderextensions-shader-1.md) - how-to article demonstrating this feature
- [Push constants for a shader: per-side clock and strengths in a settings-driven mod](../../../howto/shaders/post-processing/send-push-constants-to-a-shaderextensions-shader/send-push-constants-to-a-shaderextensions-shader-2.md) - how-to article demonstrating this feature

[^mpfx-buffers]: MPFX/Buffers.cs
[^mpfx-ci]: .github/workflows/build.yml
[^sx-attributes]: ShaderExtensions Attributes.cs
[^sx-assetex]: ShaderExtensions AssetEx.cs
[^sx-uniformbufferex]: ShaderExtensions UniformBufferEx.cs
[^sx-pushconstantex]: ShaderExtensions PushConstantEx.cs
[^sx-shaderbinding]: ShaderExtensions ShaderBinding.cs
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-float4x4]: Brutal.Numerics.float4x4 (decompiled, build 2026.10.10.5554)
[^sx-patches]: ShaderExtensions Patches.cs
[^ksa-keyhash]: KSA.KeyHash (decompiled, build 2026.10.10.5554)
