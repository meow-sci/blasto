---
type: How-To
title: "Write a post-processing fragment shader: a ShaderReference subclass that builds its own descriptor set"
description: How ShaderEx counts descriptors per type, creates a pool for one set, lays out bindings from XML children and resolves them at ModLibrary.Bind time on the thread pool
category: shaders
group: post-processing
topic: write-a-post-processing-fragment-shader
approach: 4
difficulty: advanced
prerequisites: [starmap, shaderextensions, kittenextensions]
demonstrated_by: [mods/shaderextensions/features/shaderex-extra-bindings]
techniques: [vulkan-pipeline, xml-asset]
hooks: [starmap StarMapBeforeMain (ShaderExtensions), harmony-prefix KSA.Program.Main (ShaderExtensions)]
lifecycle: [startup:asset-load, frame:render]
game_types:
  - KSA.ShaderReference
  - KSA.FileReference
  - KSA.AssetBundle
  - KSA.ModLibrary
  - KSA.IBinder
  - KSA.TextureReference
  - Brutal.VulkanApi.Abstractions.DescriptorPoolEx
  - Brutal.VulkanApi.Abstractions.DescriptorSetLayoutEx
tags: [vulkan, descriptor-pool, descriptor-set, ibinder, shader-asset, kittenextensions]
---
Every extra texture or buffer a ShaderExtensions pass reads exists because `ShaderEx` builds a
descriptor set from XML. This article explains that build, for modders who want a new asset type that
carries its own descriptor layout or who need to understand why binding numbers behave as they do. The
class is [ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs); it needs KittenExtensions (which registers the XML element) and
Harmony only indirectly.

## Background: how the game does it

The game compiles a shader file into a `VkShaderModule` in `ShaderReference.DoLoad`, and every game
renderer hard-codes the descriptor layout that goes with it. Three engine facts shape the design:

- **`DoLoad` is internal** to the game assembly, so a subclass cannot override how the file loads;
  `FileReference.OnDataLoad` registers the reference as a loader and `ShaderReference.DoLoad` compiles
  the GLSL file, taking the stage from the extension
  ([current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L39-L55), [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L85-L180)).
- **Only top-level bundle entries get `OnDataLoad`.** `AssetBundle.OnDataLoad` loops over its `Assets`
  list and calls `OnDataLoad` on each one ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L84-L108)), so a nested
  child has to be loaded by its parent.
- **`IBinder.Bind` runs in parallel.** `ModLibrary.Bind(renderer)` runs `Parallel.ForEachAsync` over all
  registered binders, each with its own `StagingPool`, from the `Program` constructor
  ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1849-L1861), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L966-L983)).

## Lifecycle and timing

| Moment | What happens | Value |
|---|---|---|
| `Program.Main` prefix | `AssetEx.Init` injects XML elements (buffers, push constants, reference elements) into `ShaderEx.XmlBindings`; the element names must exist before assets load | `startup:starmap-before-main` |
| `ModLibrary.LoadAll` | `ShaderEx.OnDataLoad` loads each child and calls `ModLibrary.RegisterBinder(this)` | `startup:asset-load` |
| `ModLibrary.Bind` | `ShaderEx.Bind` resolves children on a thread-pool thread, concurrently with buffer binders; it only resolves references and never touches buffer memory | `startup:asset-load` |
| Pass construction | Pool, layout and set are created when a pass or ImGui renderer is constructed (renderer rebuild or inline in `RenderGame`) | `frame:render` |

## Steps

1. **Subclass the game's shader asset and implement `IBinder`.** The class carries `[KxAsset("ShaderEx")]`
   so KittenExtensions allows `<ShaderEx>` inside `<Assets>`, and an `XmlElement` list for the children:

   ```csharp
   // ShaderExtensions/ShaderEx.cs L12-L34
       [KxAsset("ShaderEx")]
       //[KxAssetInject(typeof(GaugeComponent), nameof(GaugeComponent.FragmentShader), "FragmentEx")]
       public class ShaderEx : ShaderReference, IBinder
       {
           private const int MIN_PUSH_CONSTANT_SIZE_BYTES = 128;

           [XmlElement("TextureBinding", typeof(TextureBindingReference))]
           public List<SerializedId> XmlBindings = [];

           [XmlIgnore]
           public List<IShaderBinding> Bindings;

           [XmlIgnore]
           public List<IShaderPushConstant> PushConstantBindings;

           public override void OnDataLoad(Mod mod)
           {
               base.OnDataLoad(mod);
               foreach (var binding in XmlBindings)
                   binding.OnDataLoad(mod);

               ModLibrary.RegisterBinder(this);
           }
   ```

   Source: [ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L12-L34). The forwarding loop is the manual replacement for the
   game's top-level-only `OnDataLoad`.

2. **Resolve children in `Bind`.** A child with only an `Id` is a reference to a binding declared
   elsewhere; `Get()` returns the real one. Push constants are collected separately and at most one is
   allowed:

   ```csharp
   // ShaderExtensions/ShaderEx.cs L36-L57
           public void Bind(Renderer renderer, StagingPool stagingPool)
           {
               // FileReference already implements ILoader and uses an internal virtual method we can't override
               // Bind happens after Load, so all the bindings should be ready here as well
               Bindings = new(XmlBindings.Count);
               PushConstantBindings = new();
               foreach (var binding in XmlBindings)
               {
                   if (binding is IShaderBinding shaderBinding)
                       Bindings.Add(shaderBinding.Get());

                   if (binding is IShaderPushConstant pushConstant)
                       PushConstantBindings.Add(pushConstant.Get());
               }

               if (PushConstantBindings.Count > 1)
               {
                   var shaderId = GetShaderId();
                   throw new InvalidOperationException(
                     $"ShaderEx '{shaderId}' has {PushConstantBindings.Count} push constants registered, but only one push constant is supported per shader instance.");
               }
           }
   ```

   Source: [ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L36-L57). An exception here surfaces from the `Task.Wait` in
   `ModLibrary.Bind` during loading.

3. **Count descriptors per type for the pool.** One base image (supplied by the renderer), one uniform
   buffer for the time block, plus each child's `DescriptorCount`. Zero-count types are dropped and the
   pool allows exactly one set:

   ```csharp
   // ShaderExtensions/ShaderEx.cs L116-L144
           public DescriptorPoolEx CreateDescriptorPool(
             Device device,
             VkDescriptorType baseType,
             VkAllocator allocator = null)
           {
               Span<VkDescriptorPoolSize> poolSizes = stackalloc VkDescriptorPoolSize[TYPE_COUNT];
               for (var i = 0; i < TYPE_COUNT; i++)
                   poolSizes[i] = new VkDescriptorPoolSize { Type = DESCRIPTOR_TYPES[i], DescriptorCount = 0 };

               // always have one base image input (gauge font atlas or renderpass 1 output)
               poolSizes[TypeIndex(baseType)].DescriptorCount = 1;
               poolSizes[TypeIndex(VkDescriptorType.UniformBuffer)].DescriptorCount = 1;

               foreach (var binding in Bindings)
                   poolSizes[TypeIndex(binding.DescriptorType)].DescriptorCount += binding.DescriptorCount;

               var nonZero = 0;
               for (var i = 0; i < TYPE_COUNT; i++)
                   if (poolSizes[i].DescriptorCount > 0)
                       poolSizes[nonZero++] = poolSizes[i];

               poolSizes = poolSizes[..nonZero];

               return device.CreateDescriptorPool(new DescriptorPoolEx.CreateInfo
               {
                   MaxSets = 1,
                   PoolSizes = poolSizes,
               }, allocator);
           }
   ```

   Source: [ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L116-L144).

4. **Lay out the set.** The renderer passes binding 0; the class adds binding 1 (time) and one binding per
   child numbered from 2 in list order, all `FragmentBit`
   ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L156-L186)). This is where "binding numbers follow XML order" comes from.

5. **Write the descriptors without allocating.** `UpdateDescriptorSets` counts how many image, buffer and
   texel-buffer infos are needed, takes them from `stackalloc`, and lets each child fill its slice through
   `IShaderBinding.WriteDescriptors`:

   ```csharp
   // ShaderExtensions/ShaderBinding.cs L45-L53
           public void WriteDescriptors(BindingDescriptorWrites write)
           {
               write.ImageInfo[0] = new VkDescriptorImageInfo
               {
                   ImageView = ImageView,
                   ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
                   Sampler = Program.LinearClampedSampler,
               };
           }
   ```

   Source: [ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L45-L53), the `TextureBindingReference` implementation. A
   uniform-buffer child writes its whole buffer instead (descriptor type `UniformBufferDynamic`), see
   [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../feed-uniform-buffers-to-a-shaderextensions-shader/overview.md).

6. **Make the element names exist before assets load.** `Patches.Program_Main_Prefix` calls
   `AssetEx.Init()` "before main, but after all mod assemblies are loaded in"
   ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L20-L25)); the element injection mechanism is covered by
   [Custom XML asset types and injected child elements](../../../../mods/shaderextensions/features/xml-asset-types-and-element-injection.md).

## Complete example

[ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs) (the asset class), [ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs) (`IShaderBinding`,
`IShaderPushConstant`, `TextureBindingReference`, `UniformBindingReference<T>`) and
[ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs) (element registration).

## Variations and gotchas

- **Supported descriptor types**: `CombinedImageSampler`, `UniformBuffer`, `UniformBufferDynamic`,
  `InputAttachment`. Others throw `NotSupportedException`; `InputAttachment` is a leftover from the
  removed subpass support.
- **The layout object leaks.** `PostProcessingPassRenderer` keeps the `DescriptorSetLayoutEx` in a field
  and `RenderTechnique.Dispose` destroys only the pool, pipeline layout and pipeline
  ([current/decomp/RenderCore/RenderTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/RenderTechnique.cs#L120-L128)), so each chain rebuild leaks one set
  layout per pass.
- **`<FragmentEx>` does not exist at the pinned ref.** The README shows it inside a gauge component,
  but the `KxAssetInject` that would register it is commented out (the first lines of the class above).
- **Static drop-in overloads** (`CreateDescriptorPool(ShaderReference, Device, ...)` and siblings) fall
  back to the stock call for non-`ShaderEx` shaders, so a transpiler can swap them in at a game call
  site; nothing uses them at the pinned ref.
- **Threading.** `Bind` may run concurrently with the `Bind` of the buffers it references. That is safe
  only because it resolves object references and never touches buffer memory.

## See also

- Siblings: [identity-for-neutral pass](write-a-post-processing-fragment-shader-1.md), [extra textures and port](write-a-post-processing-fragment-shader-2.md), [RenderTarget per shader](write-a-post-processing-fragment-shader-3.md), [overview](overview.md)
- [ShaderEx asset with extra descriptor bindings](../../../../mods/shaderextensions/features/shaderex-extra-bindings.md), [Custom XML asset types and injected child elements](../../../../mods/shaderextensions/features/xml-asset-types-and-element-injection.md)
- [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../declare-post-processing-shader-assets-in-xml/overview.md), [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../feed-uniform-buffers-to-a-shaderextensions-shader/overview.md), [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../send-push-constants-to-a-shaderextensions-shader/overview.md)
