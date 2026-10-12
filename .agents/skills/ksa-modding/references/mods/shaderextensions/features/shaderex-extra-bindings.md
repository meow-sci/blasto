---
type: Mod Feature
title: ShaderEx asset with extra descriptor bindings
description: A ShaderReference subclass registered as the <ShaderEx> XML element that gives a fragment shader extra texture and uniform-buffer bindings by building its own descriptor pool, set layout and writes from the element's children
mod: shaderextensions
category: shaders
techniques: [xml-asset, glsl-shader, vulkan-pipeline, vulkan-texture, vulkan-buffer]
hooks: [kittenextensions KxAsset ShaderEx, binder KSA.ModLibrary.Bind]
lifecycle: [startup:asset-load, frame:render]
game_types:
  - KSA.ShaderReference
  - KSA.FileReference
  - KSA.ModLibrary
  - KSA.IBinder
  - KSA.TextureReference
  - KSA.Program
  - Brutal.VulkanApi.Abstractions.DescriptorPoolEx
  - Brutal.VulkanApi.Abstractions.DescriptorSetLayoutEx
tags: [shaders, vulkan, descriptor-sets, xml, shaderex, glsl]
---

`<ShaderEx>` is the building block of every other ShaderExtensions asset. It is a normal game shader
reference (GLSL file compiled by the game) that also owns a list of child elements describing extra
descriptor bindings: `<TextureBinding Path="...">` textures, mod-defined uniform buffers and one
mod-defined push constant. The post-processing assets (`<PostProcessingShader>`,
`<GlobalPostShader>`) derive from it, and `<ImGuiShader>` uses one as its fragment
stage.[^sx-readme] [^sx-shaderex]

The game's own shader references carry no binding information; every renderer hard-codes its
descriptor layout. `ShaderEx` moves the layout into the asset, so a shader author can add a texture
or a buffer by editing XML and GLSL only.

## How it works

1. **Asset class.** `ShaderEx : ShaderReference, IBinder` carries `[KxAsset("ShaderEx")]`, which
   KittenExtensions turns into an allowed child of `<Assets>` (`AssetBundle.Assets`). Its
   `XmlBindings` list maps `<TextureBinding>` to `TextureBindingReference`; other element names are
   injected at startup (see [Custom XML asset types](xml-asset-types-and-element-injection.md))
   ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L12-L25),
   [KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L69-L78)).[^kx-assetex]
2. **Compilation stays stock.** `FileReference.OnDataLoad` registers the reference as a loader when
   it has a `Path`, and `ShaderReference.DoLoad` compiles the GLSL file with shaderc into a
   `VkShaderModule`, taking the stage from the file extension
   ([current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L39-L55),
   [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L85-L180)).
   `DoLoad` is `internal`, so `ShaderEx` cannot override it.[^ksa-filereference] [^ksa-shaderreference]
3. **Children get their load call.** The game only calls `OnDataLoad` on the top-level entries of
   an asset bundle, so `ShaderEx.OnDataLoad` forwards the call to every child binding and then
   registers the shader with `ModLibrary.RegisterBinder`
   ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L27-L34),
   [current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L77-L109)).[^ksa-assetbundle]
4. **Bind resolves references.** `ModLibrary.Bind` later calls `IBinder.Bind` on every registered
   binder. `ShaderEx.Bind` resolves each child through `IShaderBinding.Get()` (a child with only an
   `Id` is a reference to a binding declared elsewhere), collects push constants separately, and
   throws if more than one push constant is declared
   ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L36-L57)).
5. **Descriptor layout.** Whoever renders with the shader supplies the binding-0 descriptor (the
   input image). `CreateDescriptorSetLayout` adds binding 1 for the shared time buffer and then
   one binding per resolved child, numbered from 2 in document order, all `FragmentBit`
   ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L156-L186)).
6. **Descriptor pool.** `CreateDescriptorPool` counts descriptors per type (one base image, one time
   buffer, plus each child's `DescriptorCount`), drops zero entries and creates a pool with
   `MaxSets = 1`, so every renderer instance owns exactly one set
   ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L116-L144)).
7. **Descriptor writes.** `UpdateDescriptorSets` builds all `VkWriteDescriptorSet` entries on the
   stack (base write, time buffer write, then one per child) and lets each child fill its slice of
   image or buffer infos through `IShaderBinding.WriteDescriptors`. Textures write their
   `ImageView` with `Program.LinearClampedSampler` in `ShaderReadOnlyOptimal`; uniform buffers write
   the whole buffer
   ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L196-L256),
   [ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L39-L61)).[^sx-shaderbinding] [^ksa-texturereference]
8. **Static drop-in overloads.** `CreateDescriptorPool(ShaderReference, Device, CreateInfo, ...)`
   and its siblings fall back to the stock call when the shader is not a `ShaderEx`. They match
   the game's call signatures so a transpiler can swap them in at a game call site; at this ref
   only the commented-out gauge patch used them
   ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L105-L115)).

Binding layout seen by the two consumers:

| Consumer | Set | Binding 0 | Binding 1 | Bindings 2+ |
|---|---|---|---|---|
| `<PostProcessingShader>`, `<GlobalPostShader>` | 1 (set 0 is the game's `GlobalShaderBindings`) | previous pass image, `sampler2D` | `ShaderTime` UBO | children in order |
| `<ImGuiShader>` fragment | 0 | the window re-rendered offscreen | `ShaderTime` UBO | children in order |

The renderers that supply binding 0 are `PostProcessingPassRenderer` and `ImGuiPostRenderer`
([ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs#L42-L73),
[ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs#L58-L99)).[^sx-passrenderer] [^sx-imguipostrenderer]

## Lifecycle and timing

- **XML and `OnDataLoad`: `startup:asset-load`.** `ModLibrary.LoadAll` runs in the `Program`
  constructor ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L942)).[^ksa-program]
- **`Bind`: still in the `Program` constructor, after `StarMapAllModsLoaded`.** `ModLibrary.Bind`
  runs after `LoadAll` and `BuildRenderTargets`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L966-L983)) and dispatches every
  binder with `Parallel.ForEachAsync`, each with its own staging pool
  ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1849-L1861)). `ShaderEx.Bind` therefore
  runs on a thread-pool thread, possibly at the same time as the `Bind` of the uniform buffers it
  references. That is safe because it only resolves object references and never touches buffer
  memory.[^ksa-modlibrary]
- **Descriptor sets: when a pass or ImGui renderer is constructed.** That happens in the renderer
  rebuild path or inline during `Program.RenderGame` (`frame:render`), see
  [Rebuilding mod GPU resources](renderer-rebuild-hook.md).

## Key code

| Path | Role |
|---|---|
| [ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L12-L34) | Asset class, child list, load forwarding, binder registration |
| [ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L36-L57) | `Bind`: reference resolution, one-push-constant rule |
| [ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L116-L186) | Pool and set layout built from the children |
| [ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L196-L283) | Descriptor writes and the supported descriptor types |
| [ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L11-L61) | `IShaderBinding`, `IShaderPushConstant`, `TextureBindingReference` |

## Snippets

```csharp
// ShaderExtensions/ShaderEx.cs L156-L186
        public DescriptorSetLayoutEx CreateDescriptorSetLayout(
          Device device,
          VkDescriptorSetLayoutBinding baseBinding,
          VkAllocator allocator = null)
        {
            var bindingCount = Bindings.Count;
            Span<VkDescriptorSetLayoutBinding> bindings = stackalloc VkDescriptorSetLayoutBinding[2 + bindingCount];
            bindings[0] = baseBinding;

            bindings[1] = new VkDescriptorSetLayoutBinding
            {
                Binding = 1,
                DescriptorType = VkDescriptorType.UniformBuffer,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.FragmentBit,
            };

            for (var i = 0; i < bindingCount; i++)
            {
                var binding = Bindings[i];
                bindings[i + 2] = new VkDescriptorSetLayoutBinding
                {
                    Binding = i + 2,
                    DescriptorType = binding.DescriptorType,
                    DescriptorCount = binding.DescriptorCount,
                    StageFlags = VkShaderStageFlags.FragmentBit,
                };
            }
            return device.CreateDescriptorSetLayout(
              new DescriptorSetLayoutEx.CreateInfo { Bindings = bindings }, allocator);
        }
```

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

```xml
<!-- README.md L180-L186 -->
<Assets>
  <ShaderEx Id="MyFragmentShader" Path="MyShader.frag">
    <TextureBinding Path="Texture1.png" />
    <TextureBinding Path="Texture2.png" />
    <MyBuffer Id="MyBuf" Size="1" />
  </ShaderEx>
</Assets>
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ShaderReference` (`Stage`, `Shader`, `Get`, `OnDataLoad`) | [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L21-L62) | Base class; GLSL compile and `VkShaderModule` |
| `FileReference.OnDataLoad`, `IsReference` | [current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L39-L60) | Loader registration; `Path`-less elements become references |
| `ModLibrary.RegisterBinder`, `ModLibrary.Bind` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L221-L227) | Post-load hook where bindings are resolved |
| `AssetBundle.Assets`, `AssetBundle.OnDataLoad` | [current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L77-L109) | Top-level asset list; only top-level entries get `OnDataLoad` |
| `TextureReference.ImageView` | [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L67) | Image view written for `<TextureBinding>` |
| `Program.LinearClampedSampler` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L472) | Sampler for every image binding |
| `Device.AllocateDescriptorSet(DescriptorPoolEx, DescriptorSetLayoutEx)` | [current/decomp/Brutal.VulkanApi.Abstractions/DescriptorPoolExExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/DescriptorPoolExExtensions.cs#L12) | One set per renderer instance |

## Pitfalls and version notes

- **Binding numbers follow XML order.** Bindings 2+ are assigned in the order the binding children
  appear; push constant elements do not take a number. Reordering children silently renumbers the
  GLSL bindings.
- **Fragment stage only.** Every extra binding is created with `VkShaderStageFlags.FragmentBit`;
  a custom vertex shader cannot read them.
- **Supported descriptor types** are `CombinedImageSampler`, `UniformBuffer`,
  `UniformBufferDynamic` and `InputAttachment`; anything else throws `NotSupportedException`. The
  `InputAttachment` entry is a leftover from the removed subpass support.
- **One push constant per shader.** A second one throws from `Bind`, which runs inside
  `ModLibrary.Bind`'s task, so the exception surfaces from that `Task.Wait` during loading.
- **`<FragmentEx>` does not exist at this ref.** The README shows `<FragmentEx>` inside a gauge
  `<Component>`, but the `KxAssetInject` that would register it is commented out
  ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L13)).
- **Layout object leak.** `PostProcessingPassRenderer` and `ImGuiPostRenderer` keep the
  `DescriptorSetLayoutEx` from `CreateDescriptorSetLayout` in a field and never destroy it;
  `RenderTechnique.Dispose` only destroys the pool, pipeline layout and pipeline, so every chain
  rebuild leaks one set layout per pass.

## See also

- [Shared shader time uniform buffer](shared-time-uniform-buffer.md)
- [Mod-defined uniform buffer bindings](custom-uniform-buffers.md)
- [Mod-defined push constants](push-constants.md)
- [RenderPassId-ordered fullscreen pass chain](post-processing-pass-chain.md)
- [Per-window ImGui post shader](imgui-window-post-shader.md)
- [Write a post-processing fragment shader: extra textures and the port from subpasses to dynamic rendering](../../../howto/shaders/post-processing/write-a-post-processing-fragment-shader/write-a-post-processing-fragment-shader-2.md) - how-to article demonstrating this feature
- [Write a post-processing fragment shader: a ShaderReference subclass that builds its own descriptor set](../../../howto/shaders/post-processing/write-a-post-processing-fragment-shader/write-a-post-processing-fragment-shader-4.md) - how-to article demonstrating this feature

[^sx-readme]: ShaderExtensions/README.md
[^sx-shaderex]: ShaderExtensions/ShaderEx.cs
[^kx-assetex]: KittenExtensions/AssetEx.cs
[^ksa-filereference]: KSA.FileReference (decompiled, build 2026.10.10.5554)
[^ksa-shaderreference]: KSA.ShaderReference (decompiled, build 2026.10.10.5554)
[^ksa-assetbundle]: KSA.AssetBundle (decompiled, build 2026.10.10.5554)
[^sx-shaderbinding]: ShaderExtensions/ShaderBinding.cs
[^ksa-texturereference]: KSA.TextureReference (decompiled, build 2026.10.10.5554)
[^sx-passrenderer]: ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs
[^sx-imguipostrenderer]: ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
