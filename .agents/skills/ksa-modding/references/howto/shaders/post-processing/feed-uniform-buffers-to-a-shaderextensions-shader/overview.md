---
type: Game Feature
title: Feed a ShaderExtensions shader from C# with uniform buffers (std140)
description: How a mod defines a C# struct for a GLSL std140 uniform block, registers it as an XML element without referencing ShaderExtensions, and writes it each frame
category: shaders
group: post-processing
topic: feed-uniform-buffers-to-a-shaderextensions-shader
demonstrated_by:
  - mods/mpfx/features/duck-typed-buffer-structs
  - mods/mpfx/features/separable-gaussian-blur
  - mods/mpfx/features/color-grading-math
  - mods/shaderextensions/features/custom-uniform-buffers
  - mods/mpfx/features/effect-chain-assets
  - mods/ksa-aircrafthud/features/sx-push-constants-and-buffers
lifecycle:
  - startup:starmap-before-main
  - startup:asset-load
  - frame:draw-ui
  - render:post-process
  - frame:render
  - frame:prepare
game_types:
  - KSA.KeyHash
  - KSA.LookupCollection
  - Brutal.VulkanApi.Abstractions.BufferEx
  - Brutal.VulkanApi.Abstractions.MappedMemory
  - Brutal.Numerics.float4x4
  - KSA.SerializedId
  - Core.Renderer
  - KSA.Program
tags: [uniform-buffer, std140, shaderextensions, duck-typing, attributes, host-visible, persistently-mapped]
---
A shader parameter that changes at runtime has to travel from C# into a GLSL `uniform` block. ShaderExtensions
lets a mod describe that block as a plain C# struct: you tag the struct with an attribute that names a new XML
element, put the element under a shader asset, and write the values each frame through a delegate that
ShaderExtensions fills in. The mod needs no assembly reference to ShaderExtensions.

## Engine background

- **The pieces come from the host.** The buffer is a game `BufferEx` created with `UniformBufferBit` and
  `HostVisible | HostCoherent` memory, mapped once through `MappedMemory`, so a write through the returned
  `Span<T>` reaches GPU-visible memory directly. The class is `UniformBindingReference<T>` in
  [ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L63-L124); its descriptor type is `UniformBufferDynamic` bound at offset 0,
  so GLSL declares a plain `uniform` block.
- **Keys.** Lookups take a `KeyHash` made from the XML `Id` (`KeyHash.Make`,
  [current/decomp/KSA/KeyHash.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KeyHash.cs)); the registry is a game `LookupCollection<T>`, whose `Get` returns
  `null` for an unknown key
  ([current/decomp/KSA/LookupCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LookupCollection.cs)).
- **Binders.** Buffers are created in `ModLibrary.Bind`, which runs `Parallel.ForEachAsync` over all registered
  binders from the `Program` constructor, after `LoadAll` and before the first frame
  ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1849-L1861)).
- **Frames in flight.** `Renderer.MaxFramesInFlight` is 2, but the mod buffer is one copy; see the gotchas.

## std140 for a C# struct

| GLSL | C# | Rule |
|---|---|---|
| `float`, `int`, `uint` | `float`, `int`, `uint` | 4-byte aligned |
| `vec2` | `float2` | 8-byte aligned |
| `vec3` | `float3` plus 4 bytes of padding, or a `float4` | 16-byte aligned, 12 bytes of data; pad it so a following member cannot land in the wrong place |
| `vec4`, `mat4` columns | `float4`, `float4x4` | 16-byte aligned; C# `float4x4` rows arrive as GLSL columns, so `preData[1]` is C# row 1 |
| `float x[N]` | pad to `float4`/`vec4 x[(N+3)/4]` | Every array element is rounded up to 16 bytes, so pack floats into `vec4` arrays |
| struct | `[StructLayout(LayoutKind.Sequential, Pack = 1)]` | `Pack = 1` plus explicit padding fields keeps C# offsets equal to std140 offsets |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Pack=1 structs with manual padding, one block per effect](feed-uniform-buffers-to-a-shaderextensions-shader-1.md) | MPFX | `vulkan-buffer` | `startup:starmap-before-main`, `startup:asset-load`, `frame:draw-ui` | Whole pipeline in the mod: attribute shims, struct, XML, GLSL, writes only when a setting changes |
| [Share one buffer across passes](feed-uniform-buffers-to-a-shaderextensions-shader-2.md) | ShaderExtensions, MPFX | `vulkan-buffer` | `startup:asset-load`, `frame:prepare`, `frame:render` | `Size` owns the buffer, a bare `Id` references it; shows how the host allocates and maps and why there is one copy for two frames in flight |
| [Span, pointer and BufferEx lookups beside push constants](feed-uniform-buffers-to-a-shaderextensions-shader-3.md) | KSA_AircraftHUD | `vulkan-buffer` | `startup:starmap-before-main`, `startup:asset-load`, `frame:viewports` | Which lookup form to pick; a mod that uses no ShaderExtensions type at all |

## See also

- [Mod-defined uniform buffer bindings](../../../../mods/shaderextensions/features/custom-uniform-buffers.md), [Attribute duck typing as an inter-mod API](../../../../mods/shaderextensions/features/duck-typed-attribute-api.md), [Uniform buffer and push constant structs bound by attribute name](../../../../mods/mpfx/features/duck-typed-buffer-structs.md)
- [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../send-push-constants-to-a-shaderextensions-shader/overview.md), [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../declare-post-processing-shader-assets-in-xml/overview.md), [Write a fullscreen post-processing fragment shader for KSA](../write-a-post-processing-fragment-shader/overview.md), [Free mod-owned Vulkan resources without device loss](../../../rendering/vulkan-passes/free-vulkan-resources-safely/overview.md)
