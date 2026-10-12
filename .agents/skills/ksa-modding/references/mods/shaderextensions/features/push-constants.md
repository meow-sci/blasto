---
type: Mod Feature
title: Mod-defined push constants
description: Another mod declares a small blittable struct as an XML element whose value lives in managed memory, becomes a VkPushConstantRange in the pass pipeline layout and is pushed with vkCmdPushConstants every time the pass is recorded
mod: shaderextensions
category: shaders
techniques: [vulkan-pipeline, xml-asset, inter-mod-api, reflection, glsl-shader]
hooks: [harmony-prefix KSA.Program.Main, "starmap StarMapAfterGui (consumer, MPFX)"]
lifecycle: [startup:starmap-before-main, startup:asset-load, frame:draw-ui, frame:render]
game_types:
  - KSA.SerializedId
  - KSA.LookupCollection
  - KSA.KeyHash
  - Brutal.VulkanApi.VkDeviceExtensions
  - Brutal.VulkanApi.VkPushConstantRange
tags: [shaders, push-constants, vulkan, inter-mod, xml]
---

For a few bytes that change every frame (a time value, an intensity), a dependent mod can declare a
push constant instead of a uniform buffer. The value lives in a managed object, so writing it is a
plain struct write, and it is copied into the command buffer when the pass is recorded, so each
frame gets exactly the value that was current at record time.[^sx-readme]

```xml
<!-- README.md L322-L326 -->
<Assets>
  <ShaderEx Id="MyFragmentShader" Path="MyShader.frag">
    <MyPushConstant Id="MyPush" Stage="FragmentBit" />
  </ShaderEx>
</Assets>
```

```glsl
// README.md L340-L344
layout(push_constant) uniform MyPushConstant
{
  float Intensity;
  float Time;
} Push;
```

## How it works

1. **Declaration (consumer).** A struct with `[SxPushConstant("MyPushConstant")]` and a static
   delegate field with `[SxPushConstantLookup]` returning `Span<T>`; the attribute classes are
   declared by the consumer in the `ShaderExtensions` namespace (see
   [Attribute duck typing as an inter-mod API](duck-typed-attribute-api.md)). MPFX declares
   `MPFXPushConstantsFloatBuffer` this way
   ([MPFX/Buffers/MPFXPushConstantsFloatBuffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXPushConstantsFloatBuffer.cs#L9-L17)).[^mpfx-pushbuffer]
2. **Registration.** `PushConstantEx.AddPushConstantGeneric<T>` injects
   `PushConstantBindingReference<T>` under the element name into `ShaderEx.XmlBindings` and
   `AssetBundle.Assets`, and binds every `Span<T>`-returning lookup field to
   `PushConstantBindingReference<T>.GetSpan`
   ([ShaderExtensions/PushConstantEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PushConstantEx.cs#L19-L56)).[^sx-pushconstantex]
3. **Storage.** The element object holds a private `T value`. `GetSpan` returns
   `MemoryMarshal.CreateSpan(ref value, 1)`, a one-element span over that field, and `GetData`
   returns the same bytes for the push. `Stage` is an XML attribute of type
   `VkShaderStageFlags`, default `FragmentBit`
   ([ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L126-L162)).[^sx-shaderbinding]
4. **Range.** `ShaderEx.Bind` collects push constants apart from descriptor bindings and refuses
   more than one. `CreatePushConstantRanges` rejects sizes that are zero or not a multiple of 4,
   aligns the offset to 4 and rejects totals above 128 bytes, the minimum `maxPushConstantsSize`
   every Vulkan device guarantees
   ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L59-L93)).[^sx-shaderex]
5. **Pipeline layout.** Post-processing passes create their layout from
   `[GlobalShaderBindings.DescriptorSetLayout, bindingLayout]` plus these ranges; ImGui window
   shaders prepend a 32-byte vertex range for the window rectangles
   ([ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs#L71-L73),
   [ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs#L89-L99)).[^sx-passrenderer] [^sx-imguipostrenderer]
6. **Push.** Each pass's `Render` calls `shader.PushConstants(commandBuffer, PipelineLayout)` after
   binding the pipeline and before the draw, which issues one `PushConstants` per range with the
   current bytes
   ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L95-L103),
   [current/decomp/Brutal.VulkanApi/VkDeviceExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi/VkDeviceExtensions.cs#L1189)).[^brutal-vkdeviceext]

## Lifecycle and timing

- **Element and lookup:** registered at the `Program.Main` prefix (`startup:starmap-before-main`);
  the value object exists and is registered from `ModLibrary.LoadAll` (`startup:asset-load`). There
  is no GPU resource, so unlike uniform buffers the lookup works as soon as assets are loaded.
- **Capture point: `frame:render`.** `vkCmdPushConstants` copies the bytes into the command buffer
  while `Program.RenderGame` records. A write in `[StarMapAfterGui]` (`frame:draw-ui`, as MPFX does
  for its film-grain time) applies to the same frame; a write after `RenderGame` (for example in
  `[StarMapAfterOnFrame]`) applies to the next one
  ([MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L120-L138)).[^mpfx-main]
- **Thread:** main thread only; the field is not synchronized.

## Key code

| Path | Role |
|---|---|
| [ShaderExtensions/PushConstantEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PushConstantEx.cs#L9-L57) | Generic registration and lookup binding |
| [ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L126-L162) | `PushConstantBindingReference<T>`: managed storage, stage, bytes |
| [ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L59-L103) | Range validation and the push call |
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L128-L138) | Consumer writes per frame (MPFX) |

## Snippets

```csharp
// ShaderExtensions/ShaderEx.cs L59-L93
        public VkPushConstantRange[] CreatePushConstantRanges()
        {
            if (PushConstantBindings.Count == 0)
                return [];

            var offset = 0;
            var ranges = new VkPushConstantRange[PushConstantBindings.Count];
            for (var i = 0; i < PushConstantBindings.Count; i++)
            {
                var pushConstant = PushConstantBindings[i];

                if (pushConstant.SizeBytes <= 0)
                    throw new InvalidOperationException($"Push constant {pushConstant} has invalid size {pushConstant.SizeBytes}");

                if ((pushConstant.SizeBytes & 3) != 0)
                    throw new InvalidOperationException($"Push constant {pushConstant} size must be 4-byte aligned");

                offset = (offset + 3) & ~3;

                pushConstant.OffsetBytes = offset;
                ranges[i] = new VkPushConstantRange
                {
                    StageFlags = pushConstant.StageFlags,
                    Offset = ByteSize.Of(offset),
                    Size = ByteSize.Of(pushConstant.SizeBytes),
                };

                offset += pushConstant.SizeBytes;
            }

            if (offset > MIN_PUSH_CONSTANT_SIZE_BYTES)
                throw new InvalidOperationException($"Push constant bytes ({offset}) exceed guaranteed Vulkan minimum {MIN_PUSH_CONSTANT_SIZE_BYTES} bytes");

            return ranges;
        }
```

```csharp
// ShaderExtensions/ShaderBinding.cs L126-L162
    public class PushConstantBindingReference<T> : SerializedId, IShaderPushConstant
      where T : unmanaged
    {
        public static readonly LookupCollection<PushConstantBindingReference<T>> PushConstants =
          new($"push constants of {typeof(T)}");

        public static Span<T> GetSpan(KeyHash key) => MemoryMarshal.CreateSpan(ref PushConstants.Get(key).value, 1);

        [XmlAttribute("Stage")]
        public VkShaderStageFlags Stage = VkShaderStageFlags.FragmentBit;

        private T value;

        public int LookupIndex { get; set; }

        [XmlIgnore]
        public int OffsetBytes { get; set; }

        public VkShaderStageFlags StageFlags => Stage;
        public int SizeBytes => (int)ByteSize.Of<T>().Bytes;

        public IShaderPushConstant Get() => this;
        public override bool IsReference() => false;

        public override void OnDataLoad(Mod mod)
        {
            base.OnDataLoad(mod);
            IShaderPushConstant.PushConstants.Register(this);
            PushConstants.Register(this);
        }

        public ReadOnlySpan<byte> GetData() =>
          MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1));

        public override SerializedId Populate() => throw new NotImplementedException();
        public override TableString.Row ToRow() => new();
    }
```

```csharp
// MPFX/MPFX.cs L128-L138 (consumer)
            if (MPFXPushConstantsFloatBuffer.LookupSpan != null)
            {
                CurrentProfile.FilmGrainPreTime += (float)dt * CurrentProfile.FilmGrainPreTimeMultiplier * (CurrentProfile.FilmGrainPreTimeWarpMultiplier ? (float)Universe.SimulationSpeed : 1f);
                CurrentProfile.FilmGrainPostTime += (float)dt * CurrentProfile.FilmGrainPostTimeMultiplier * (CurrentProfile.FilmGrainPostTimeWarpMultiplier ? (float)Universe.SimulationSpeed : 1f);

                Span<MPFXPushConstantsFloatBuffer> FilmgrainPreTime = MPFXPushConstantsFloatBuffer.LookupSpan(KeyHash.Make("MPFXFilmgrainPreTimeBuffer"));
                FilmgrainPreTime[0].a = CurrentProfile.FilmGrainPreTime;

                Span<MPFXPushConstantsFloatBuffer> FilmgrainPostTime = MPFXPushConstantsFloatBuffer.LookupSpan(KeyHash.Make("MPFXFilmgrainPostTimeBuffer"));
                FilmgrainPostTime[0].a = CurrentProfile.FilmGrainPostTime;
            }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `VkDeviceExtensions.PushConstants<T>(layout, stageFlags, offset, bytes)` | [current/decomp/Brutal.VulkanApi/VkDeviceExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi/VkDeviceExtensions.cs#L1189) | Record the bytes |
| `DeviceExtensions.CreatePipelineLayout(setLayouts, pushConstantRanges, allocator)` | [current/decomp/Brutal.VulkanApi.Abstractions/DeviceExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/DeviceExtensions.cs#L166) | Ranges enter the pass layout |
| `LookupCollection<T>.Register` | [current/decomp/KSA/LookupCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LookupCollection.cs#L113-L146) | Id registry; logs an error on duplicates and keeps the first |

## Pitfalls and version notes

- **Not shareable.** `IsReference()` always returns `false`, so the same `Id` in two shaders makes two
  objects. The second registration logs `error registering duplicate` and the lookup keeps
  returning the first, so the second shader's value cannot be written.[^ksa-lookupcollection]
- **One per shader, 128 bytes total.** Both limits throw during load.
- **Scan gate.** ShaderExtensions only scans assemblies that define
  `ShaderExtensions.SxUniformBufferAttribute`; a consumer that declares only the push constant
  attributes is never registered. Declare all four attribute classes, as MPFX does.
- **ImGui shaders overlap ranges.** `ImGuiPostRenderer` puts a vertex range at offset 0 (32 bytes)
  and `CreatePushConstantRanges` also starts the fragment range at offset 0. Vulkan's valid usage
  for `vkCmdPushConstants` requires that a push covering bytes shared by several ranges include
  all of those ranges' stages, which neither push here does, so validation layers will flag it and
  the fragment bytes can clobber the window rectangle. Prefer a uniform buffer for ImGui shader
  parameters.
- **Layout match is manual.** Use `Pack = 4` (or explicit padding) so the C# layout equals the GLSL
  `push_constant` block.

## See also

- [Mod-defined uniform buffer bindings](custom-uniform-buffers.md)
- [Attribute duck typing as an inter-mod API](duck-typed-attribute-api.md)
- [ShaderEx asset with extra descriptor bindings](shaderex-extra-bindings.md)
- [Per-window ImGui post shader](imgui-window-post-shader.md)
- [Push constants for a shader: one element per pass with an enabled flag and time, written from OnFrameEditor](../../../howto/shaders/post-processing/send-push-constants-to-a-shaderextensions-shader/send-push-constants-to-a-shaderextensions-shader-1.md) - how-to article demonstrating this feature

[^sx-readme]: ShaderExtensions/README.md
[^mpfx-pushbuffer]: MPFX/Buffers/MPFXPushConstantsFloatBuffer.cs
[^sx-pushconstantex]: ShaderExtensions/PushConstantEx.cs
[^sx-shaderbinding]: ShaderExtensions/ShaderBinding.cs
[^sx-shaderex]: ShaderExtensions/ShaderEx.cs
[^sx-passrenderer]: ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs
[^sx-imguipostrenderer]: ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs
[^brutal-vkdeviceext]: Brutal.VulkanApi.VkDeviceExtensions (decompiled, build 2026.10.10.5554)
[^mpfx-main]: MPFX/MPFX.cs
[^ksa-lookupcollection]: KSA.LookupCollection (decompiled, build 2026.10.10.5554)
