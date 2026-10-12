---
type: How-To
title: "Feed a shader with uniform buffers: share one buffer across passes (Size owns it, Id references it)"
description: Let a pre-ImGui and a post-ImGui pass read one persistently mapped uniform buffer so the mod writes once, and see how the host allocates and maps that buffer
category: shaders
group: post-processing
topic: feed-uniform-buffers-to-a-shaderextensions-shader
approach: 2
difficulty: intermediate
prerequisites: [starmap, shaderextensions, kittenextensions]
demonstrated_by: [mods/shaderextensions/features/custom-uniform-buffers, mods/mpfx/features/effect-chain-assets]
techniques: [vulkan-buffer, xml-asset]
hooks:
  - xml-asset UniformBindingReference<T>
  - harmony-prefix KSA.Program.RenderGame (ShaderExtensions time buffer)
lifecycle: [startup:asset-load, frame:render, frame:prepare]
game_types:
  - KSA.SerializedId
  - KSA.LookupCollection
  - Brutal.VulkanApi.Abstractions.BufferEx
  - Brutal.VulkanApi.Abstractions.MappedMemory
  - Core.Renderer
  - KSA.ModLibrary
tags: [uniform-buffer, shared-buffer, host-visible, host-coherent, persistently-mapped, frames-in-flight]
---
Two passes often need the same parameters: the pre-ImGui and post-ImGui halves of an effect, or a horizontal and a vertical blur.
ShaderExtensions lets one XML element **own** a buffer (it has `Size`) and any number of others **reference** it (only `Id`),
so the mod writes the values once. This article shows how ownership and references resolve, how the host allocates and maps
the buffer, and what that implies for timing and frames in flight. Struct and attribute setup is in the
[first article](feed-uniform-buffers-to-a-shaderextensions-shader-1.md).

## Background: how the game does it

- **Registry.** Every binding is registered by `Id` hash in the type-agnostic `IShaderBinding.Bindings`
  (`LookupCollection<IShaderBinding>`) and, for owners, in a per-struct-type `Buffers` collection. `Get()` returns `this`
  for an owner and `IShaderBinding.Bindings.Get(Hash)` for a reference ([ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L63-L98)).
- **Allocation.** The owner's `Bind` runs inside `ModLibrary.Bind`'s parallel loop
  ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1849-L1861)) and creates one `BufferEx` of `sizeof(T) * Size` bytes with
  `UniformBufferBit` and `HostVisibleBit | HostCoherentBit`, then maps it once (`buffer.Map()` returns a `MappedMemory`).
  There is no flush and no staging copy.
- **Frames in flight.** `Renderer.MaxFramesInFlight` is 2 ([current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L28)); the mod buffer is a single copy.

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Owner and reference elements parsed; owners register | `ModLibrary.LoadAll` | `startup:asset-load` |
| Buffer created and mapped | `ModLibrary.Bind` in the `Program` constructor | `startup:asset-load` |
| `ShaderEx.Bind` resolves references | same `Bind` loop, possibly in parallel with the owner's `Bind` | `startup:asset-load` |
| Descriptor sets referencing the buffer | When a pass is constructed (renderer rebuild or inline in `RenderGame`) | `frame:prepare` / `frame:render` |
| Writes | Any main-thread point before `RenderGame` records | `frame:draw-ui` |

`ShaderEx.Bind` resolves references without touching buffer memory, which is why running concurrently with the owner's `Bind`
is safe. See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare the owner once, the references wherever needed.** From the README, and in MPFX's pre/post pair:

   ```xml
   <!-- ShaderExtensions/README.md L262-L273: Size owns a buffer, Id without Size references it -->
   <Assets>
     <ShaderEx Id="MyFragmentShader" Path="MyShader.frag">
       <MyBuffer Id="MyBuf" Size="1" />
     </ShaderEx>
     <ShaderEx Id="MyFragmentShader2" Path="MyShader2.frag">
       <MyBuffer Id="MyBuf" />
     </ShaderEx>
     <MyBuffer Id="MyBuf2" Size="1" />
     <ShaderEx Id="MyFragmentShader3" Path="MyShader.frag">
       <MyBuffer Id="MyBuf2" />
     </ShaderEx>
   </Assets>
   ```

   The third form declares the buffer at the top level of `<Assets>`, which is legal because the element is also injected
   into `AssetBundle.Assets`. MPFX's colour balance owns `MPFXColorBalanceBuffer` as `<MPFXMat3BufferAsset>` under the
   pre shader and references it from the post shader as `<MPFXDefaultBufferAsset Id="MPFXColorBalanceBuffer" />`
   ([MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml#L3-L8)); the lookup only compares ids.

2. **Understand ownership in code.** An owner registers itself in three collections and creates the GPU buffer in `Bind`:

   ```csharp
   // ShaderExtensions/ShaderBinding.cs L63-L72, L89-L110
       public class UniformBindingReference<T> : SerializedId, IShaderBinding, IBinder
         where T : unmanaged
       {
           public static readonly LookupCollection<UniformBindingReference<T>> Buffers =
             new($"uniform buffers of {typeof(T)}");

           public static BufferEx GetBuffer(KeyHash key) => Buffers.Get(key).buffer;
           public static MappedMemory GetMappedMemory(KeyHash key) => Buffers.Get(key).mappedMemory;
           public static Span<T> GetSpan(KeyHash key) => Buffers.Get(key).mappedMemory.AsSpan<T>();
           public static unsafe T* GetPtr(KeyHash key) => (T*)Buffers.Get(key).mappedMemory.MapPtr;
   // ...
           public override void OnDataLoad(Mod mod)
           {
               base.OnDataLoad(mod);
               if (IsReference())
                   return;

               IShaderBinding.Bindings.Register(this);
               Buffers.Register(this);
               ModLibrary.RegisterBinder(this);
           }

           public void Bind(Renderer renderer, StagingPool stagingPool)
           {
               buffer = renderer.Device.CreateBuffer(new BufferEx.CreateInfo
               {
                   Name = "UniformBindingReference.UniformBuffer",
                   BufferUsage = VkBufferUsageFlags.UniformBufferBit,
                   BufferSize = ByteSize.Of<T>() * Size,
                   AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
               });
               mappedMemory = buffer.Map();
           }
   ```

   Source: [ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L63-L110). `IsReference()` is `Size <= 0`.

3. **Write once from C#.** Only the owner's struct type has to match the lookup you call: `LookupSpan(KeyHash.Make("MyBuf"))` yields a
   `Span<T>` of length `Size` over the mapped memory. Both shaders see the change on the next recorded frame.

4. **Mirror the layout in every consumer's GLSL.** The block name is free but each shader that references the buffer must declare the
   same std140 members at its own binding number (the first buffer child of a pass is binding 2).

5. **The host-owned time buffer works the same way.** ShaderExtensions' own shared `ShaderTime` buffer is a singleton of this kind
   written from a `Program.RenderGame` prefix and bound at binding 1 of every set
   ([ShaderExtensions/SharedTimeBuffer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/SharedTimeBuffer.cs#L60-L85), [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L27-L43)); see the time article in
   [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../send-push-constants-to-a-shaderextensions-shader/overview.md).

## Complete example

| File | Role |
|---|---|
| [ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs) | `UniformBindingReference<T>` and the lookup methods |
| [ShaderExtensions/UniformBufferEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/UniformBufferEx.cs) | Registration and delegate binding |
| [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml) | Pre/post pairs sharing buffers, vignette and blur with one buffer per side |

## Variations and gotchas

- **Single buffer, two frames in flight.** The write lands in memory the previous frame may still be reading. Harmless for animation or
  settings that change rarely; use a push constant for per-frame exactness.
- **Too early throws.** From `[StarMapAllModsLoaded]` the owner is found but its mapped memory is still the default value: a span
  lookup throws `ArgumentException`, a pointer lookup returns null.
- **Duplicate ids.** `LookupCollection.Register` logs an error and keeps the first registration.
- **Empty `Id`.** The buffer is created and bound but not registered, so nothing can write it.
- **Buffers are never destroyed.** The mod has no shutdown path for them.

## See also

- Siblings: [Pack=1 structs and std140 padding](feed-uniform-buffers-to-a-shaderextensions-shader-1.md), [lookup forms in a HUD mod](feed-uniform-buffers-to-a-shaderextensions-shader-3.md), [overview](overview.md)
- [Mod-defined uniform buffer bindings](../../../../mods/shaderextensions/features/custom-uniform-buffers.md), [Shared shader time uniform buffer](../../../../mods/shaderextensions/features/shared-time-uniform-buffer.md), [Effect chain declared as RenderPassId-ordered post-process assets](../../../../mods/mpfx/features/effect-chain-assets.md)
- [Free mod-owned Vulkan resources without device loss](../../../rendering/vulkan-passes/free-vulkan-resources-safely/overview.md), [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../declare-post-processing-shader-assets-in-xml/overview.md)
