---
type: Mod Feature
title: Mod-defined uniform buffer bindings
description: Another mod declares a blittable struct as a new XML element; ShaderExtensions allocates a persistently mapped host-coherent uniform buffer for it, binds it to the ShaderEx fragment shader and fills the struct's static lookup delegates so the mod can write the buffer from C# every frame
mod: shaderextensions
category: shaders
techniques: [vulkan-buffer, xml-asset, inter-mod-api, reflection, glsl-shader]
hooks:
  - harmony-prefix KSA.Program.Main
  - binder KSA.ModLibrary.Bind
  - starmap StarMapAfterGui (consumer, MPFX)
lifecycle: [startup:starmap-before-main, startup:asset-load, frame:draw-ui, frame:render]
game_types:
  - KSA.ModLibrary
  - KSA.IBinder
  - KSA.SerializedId
  - KSA.LookupCollection
  - KSA.KeyHash
  - KSA.AssetBundle
  - Brutal.VulkanApi.Abstractions.BufferEx
  - Brutal.VulkanApi.Abstractions.MappedMemory
tags: [shaders, uniform-buffer, vulkan, inter-mod, xml, mpfx]
---

A dependent mod can feed its own parameters to a ShaderExtensions shader through a uniform buffer
whose layout it defines in C#. It declares a struct, tags it with an attribute naming a new XML
element, puts that element under a `<ShaderEx>` (or any post-processing or ImGui fragment
shader), and writes the values every frame through a delegate that ShaderExtensions fills in. No
compile-time reference to ShaderExtensions is needed.[^sx-readme] MPFX drives its whole effect
stack this way.[^mpfx-assets]

```xml
<!-- README.md L262-L273: Size owns a buffer, Id without Size references it -->
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

## How it works

1. **Declaration (consumer).** The consumer declares `ShaderExtensions.SxUniformBufferAttribute`
   and `SxUniformBufferLookupAttribute` in its own assembly, then a struct with
   `[SxUniformBuffer("MyBuffer")]` and one or more static delegate fields marked
   `[SxUniformBufferLookup]`. The attribute matching is by full type name, see
   [Attribute duck typing as an inter-mod API](duck-typed-attribute-api.md).
2. **Registration (ShaderExtensions, `Program.Main` prefix).** For each such struct,
   `UniformBufferEx.AddUniformBuffer` closes `AddUniformBufferGeneric<T>` over it, which injects
   `UniformBindingReference<T>` as the element name into both `ShaderEx.XmlBindings` and
   `AssetBundle.Assets` (so the element is also legal at the top level of `<Assets>`)
   ([ShaderExtensions/UniformBufferEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/UniformBufferEx.cs#L12-L35)).[^sx-uniformbufferex]
3. **Lookup delegates.** Each `[SxUniformBufferLookup]` field gets a delegate chosen by the return
   type of the field's delegate type: `BufferEx` to `GetBuffer`, `MappedMemory` to
   `GetMappedMemory`, `Span<T>` to `GetSpan` (length `Size`), `T*` to `GetPtr`. Any delegate type
   with a matching signature works, which is why MPFX can use its own `MPFXSpanLookup<T>`
   ([ShaderExtensions/UniformBufferEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/UniformBufferEx.cs#L37-L74),
   [MPFX/Buffers.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers.cs#L7-L25)).[^mpfx-buffers]
4. **Owner or reference.** `Size` is the element count. `Size <= 0` makes the element a reference
   (`IsReference`) that `Get()` resolves by `Id` through `IShaderBinding.Bindings`; an owner
   registers itself in that collection, in the per-type `Buffers` collection and as an `IBinder`
   ([ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L63-L98)).[^sx-shaderbinding]
5. **GPU buffer.** In `Bind`, an owner creates a `BufferEx` of `sizeof(T) * Size` bytes with
   `UniformBufferBit` and `HostVisibleBit | HostCoherentBit` and maps it once
   ([ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L100-L110)).
6. **Descriptor.** The binding is declared as `UniformBufferDynamic`, one descriptor, range = the
   whole buffer; the pass renderers always pass a zero dynamic offset for it
   ([ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L80-L81),
   [ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs#L123-L135)).[^sx-passrenderer]
7. **Writes.** The consumer calls the delegate with `KeyHash.Make(id)` and writes through the span
   or pointer. Memory is host-coherent and persistently mapped, so there is no flush and no copy.

Consumer side, from MPFX: a struct registered as `<MPFXDefaultBufferAsset>` and its per-frame
writes ([MPFX/Buffers/MPFXDefaultBuffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXDefaultBuffer.cs#L8-L22),
[MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L120-L152)).[^mpfx-defaultbuffer] [^mpfx-main]

## Lifecycle and timing

| Step | When | Lifecycle |
|---|---|---|
| Struct discovery, element injection, delegate assignment | `Program.Main` prefix, after StarMap loaded all mod assemblies | `startup:starmap-before-main` |
| XML parsed, owners registered | `ModLibrary.LoadAll` in the `Program` constructor | `startup:asset-load` |
| GPU buffer created and mapped | `ModLibrary.Bind` in the `Program` constructor, after `StarMapAllModsLoaded` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L976-L983)) | between `startup:all-mods-loaded` and `startup:game-loaded` |
| Consumer writes | Any main-thread point before `Program.RenderGame`; MPFX uses `[StarMapAfterGui]` | `frame:draw-ui` |
| GPU reads | When the command buffer recorded in `RenderGame` executes | `frame:render` and later |

`[StarMapAfterGui]` runs in `Program.OnFrame` before `ImGui.Render` and the `Render` call
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2268-L2324)), so values written there
reach the passes recorded later in the same frame.[^ksa-program] Like every StarMap frame hook it
also runs after `PrepareFrame` has dispatched the next physics step (`frame:dispatch-solvers`,
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2216-L2221)),
so shader values derived from vehicle state come from the last applied step while the workers
integrate the next one. Reading is safe there; writing simulated state is not.

## Key code

| Path | Role |
|---|---|
| [ShaderExtensions/UniformBufferEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/UniformBufferEx.cs#L12-L74) | Generic registration and delegate binding |
| [ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L63-L124) | `UniformBindingReference<T>`: lookup methods, owner and reference, buffer creation, descriptor write |
| [ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs#L86-L95) | Attribute match that triggers registration |
| [MPFX/Buffers/MPFXDefaultBuffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXDefaultBuffer.cs#L8-L22) | Consumer struct (MPFX) |
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L120-L152) | Consumer per-frame writes from `StarMapAfterGui` (MPFX) |

## Snippets

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

```csharp
// ShaderExtensions/UniformBufferEx.cs L42-L72
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
```

```csharp
// MPFX/Buffers/MPFXDefaultBuffer.cs L8-L22 (consumer)
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
// MPFX/MPFX.cs L140-L148 (consumer)
            if (UpdateValues)
            {
                UpdateValues = false;

                if (MPFXDefaultBuffer.LookupSpan != null)
                {
                    Span<MPFXDefaultBuffer> BrightnessData = MPFXDefaultBuffer.LookupSpan(KeyHash.Make("MPFXBrightnessBuffer"));
                    BrightnessData[0].a = CurrentProfile.BrightnessPreImgui ? CurrentProfile.BrightnessFloatPreImgui : 1f;
                    BrightnessData[0].b = CurrentProfile.BrightnessPostImgui ? CurrentProfile.BrightnessFloatPostImgui : 1f;
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ModLibrary.RegisterBinder`, `ModLibrary.Bind` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1849-L1861) | Buffer creation after asset load, parallel per binder |
| `LookupCollection<T>.Register`, `Get(KeyHash)` | [current/decomp/KSA/LookupCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LookupCollection.cs#L113-L174) | Id registry; `Get` returns `null` for unknown ids; empty ids are not registered |
| `KeyHash.Make` | [current/decomp/KSA/KeyHash.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KeyHash.cs#L15) | Lookup key from the XML `Id` |
| `BufferEx`, `MappedMemory.AsSpan<T>`, `MapPtr` | [current/decomp/Brutal.VulkanApi.Abstractions/BufferExExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/BufferExExtensions.cs#L5) | Buffer and persistent mapping |
| `Renderer.MaxFramesInFlight` | [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L28) | Two frames in flight share the one buffer |

## Pitfalls and version notes

- **Too early throws.** The delegates are assigned at `Program.Main`, but the buffer only exists
  after `ModLibrary.Bind`. From `[StarMapAllModsLoaded]` (a postfix on `ModLibrary.LoadAll`) the
  owner is found but its `MappedMemory` is still the default value: a span lookup throws
  `ArgumentException` from `MappedMemory.AsSpan` (zero-size region) and a pointer lookup returns a
  null pointer ([current/decomp/Brutal.VulkanApi.Abstractions/MappedMemory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/MappedMemory.cs#L101-L108)).
  An unknown `Id` makes `LookupCollection.Get` return `null`, so the lookup throws
  `NullReferenceException`.[^ksa-lookupcollection] [^brutal-mappedmemory]
- **Check the delegate.** If ShaderExtensions is not installed the fields stay `null`; MPFX guards
  with `LookupSpan != null`.
- **Single buffer, two frames in flight.** Writes land in memory the previous frame may still be
  reading. For values that must not change mid-flight, use a push constant (captured at record
  time), see [Mod-defined push constants](push-constants.md).[^core-renderer]
- **std140 layout is yours.** The struct is copied as-is. MPFX uses `Pack = 1`; `vec3`, `mat3` and
  arrays need manual padding to match std140.
- **References resolve by `Id` only.** `IShaderBinding.Bindings` is one collection for every binding
  type, so a reference element of one struct type can point at a buffer owned by another type.
  MPFX owns `MPFXColorBalanceBuffer` as `<MPFXMat3BufferAsset>` and references it from the post-ImGui
  shader as `<MPFXDefaultBufferAsset>`; the GLSL block must follow the owner's layout
  ([MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml#L3-L8),
  [MPFX/Buffers/MPFXMat4Buffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXMat4Buffer.cs#L12)).[^mpfx-mat4buffer]
- **`Id` is optional but needed.** Without an `Id` the buffer is still created and bound, but
  `LookupCollection.Register` skips empty ids, so nothing can write it.
- **Dynamic descriptor, static offset.** The `UniformBufferDynamic` type is not used for
  sub-allocation; the offset is always zero. In post-processing passes the dynamic-offset array is
  one entry longer than the bound sets declare, see
  [RenderPassId-ordered fullscreen pass chain](post-processing-pass-chain.md).
- **Updates stop with the HUD.** `[StarMapAfterGui]` does not fire while the HUD is hidden, so a
  consumer writing there freezes its values; the passes keep running with the last data.
- **Buffers are never destroyed.**

## See also

- [Attribute duck typing as an inter-mod API](duck-typed-attribute-api.md)
- [ShaderEx asset with extra descriptor bindings](shaderex-extra-bindings.md)
- [Mod-defined push constants](push-constants.md)
- [Custom XML asset types and injected child elements](xml-asset-types-and-element-injection.md)
- [Feed a shader with uniform buffers: share one buffer across passes (Size owns it, Id references it)](../../../howto/shaders/post-processing/feed-uniform-buffers-to-a-shaderextensions-shader/feed-uniform-buffers-to-a-shaderextensions-shader-2.md) - how-to article demonstrating this feature

[^sx-readme]: ShaderExtensions/README.md
[^mpfx-assets]: MPFX/MPFXAssests.xml
[^sx-uniformbufferex]: ShaderExtensions/UniformBufferEx.cs
[^mpfx-buffers]: MPFX/Buffers.cs
[^sx-shaderbinding]: ShaderExtensions/ShaderBinding.cs
[^sx-passrenderer]: ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs
[^mpfx-defaultbuffer]: MPFX/Buffers/MPFXDefaultBuffer.cs
[^mpfx-main]: MPFX/MPFX.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-lookupcollection]: KSA.LookupCollection (decompiled, build 2026.10.10.5554)
[^brutal-mappedmemory]: Brutal.VulkanApi.Abstractions.MappedMemory (decompiled, build 2026.10.10.5554)
[^core-renderer]: Core.Renderer (decompiled, build 2026.10.10.5554)
[^mpfx-mat4buffer]: MPFX/Buffers/MPFXMat4Buffer.cs
