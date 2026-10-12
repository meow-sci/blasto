---
type: How-To
title: "Feed a shader with uniform buffers: Span, pointer and BufferEx lookups beside push constants in a HUD mod"
description: Pick the right lookup form for a ShaderExtensions uniform buffer and integrate by attribute name with no use of any ShaderExtensions type, as KSA_AircraftHUD does
category: shaders
group: post-processing
topic: feed-uniform-buffers-to-a-shaderextensions-shader
approach: 3
difficulty: intermediate
prerequisites: [starmap, shaderextensions, kittenextensions]
demonstrated_by: [mods/ksa-aircrafthud/features/sx-push-constants-and-buffers]
techniques: [vulkan-buffer, reflection]
hooks:
  - harmony-postfix KSA.Program.OnFrameEditor
  - xml-asset AircraftHUDBufferAsset
  - harmony-prefix KSA.Program.Main (ShaderExtensions AssetEx.Init)
lifecycle: [startup:starmap-before-main, startup:asset-load, frame:viewports]
game_types:
  - KSA.KeyHash
  - Brutal.VulkanApi.Abstractions.BufferEx
  - Brutal.VulkanApi.Abstractions.MappedMemory
  - KSA.Program
tags: [uniform-buffer, push-constant, lookup-delegate, duck-typing, aircrafthud, std140]
---
KSA_AircraftHUD talks to ShaderExtensions without using one type from `ShaderExtensions.dll`. It copies the attribute and delegate
declarations into its own assembly, marks two structs, and lets ShaderExtensions find them by name. This article uses that mod to answer
the practical question "which lookup form do I declare, and what does each give me?", and to show the registration side of the same
pattern for a mod whose buffer feeds an `ImGuiShader`.

## Background: how the game does it

ShaderExtensions assigns each `[SxUniformBufferLookup]` static field a method according to the return type of the delegate type
([ShaderExtensions/UniformBufferEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/UniformBufferEx.cs#L37-L74)):

| Delegate returns | Gives you | Pick it when |
|---|---|---|
| `Span<T>` (`SxSpanLookup<T>`) | The mapped memory as a span of length `Size` | Almost always: bounds-checked, works with `ref` locals |
| `T*` (`SxPtrLookup<T>`) | Pointer to the first element | Unsafe code, avoiding the span construction cost in a hot loop |
| `MappedMemory` | Raw mapped region of the game type | You need byte offsets or a size-agnostic copy |
| `BufferEx` | The Vulkan buffer object | You build your own descriptor writes or copy commands |

The generic forms need `where T : unmanaged`. Push constants accept only the `Span<T>` form
([ShaderExtensions/PushConstantEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PushConstantEx.cs#L34-L56)).

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Probe and delegate injection | `Program.Main` prefix of ShaderExtensions, after StarMap loaded every mod assembly (the HUD itself has no `StarMapBeforeMain`) | `startup:starmap-before-main` |
| Buffer exists | After `ModLibrary.Bind` | `startup:asset-load` |
| Writes | The HUD writes inside its `Program.OnFrameEditor` postfix | `frame:viewports` |

The `OnFrameEditor` postfix runs before the frame's windows are submitted, even with the HUD hidden, and is the earliest ImGui point of the
frame, so the values reach the passes recorded later in the same `RenderGame`. See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Copy the shims.** Put them in `namespace ShaderExtensions` of your assembly (the HUD's own delegate names start with `AH`):

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

   Source: [ShaderExtensionsHelpers.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ShaderExtensionsHelpers.cs#L12-L33). A mod that uses only push constants must still declare the uniform-buffer attribute: it is the probe.

2. **Declare the struct with every lookup form you may need:**

   ```csharp
   // ShaderExtensionsHelpers.cs L48-L62
       // <MyBuffer Id="MyBuf" Size="1" />, where Size is the number of sequential AircraftHUDBuffer elements in the buffer
       [SxUniformBuffer("AircraftHUDBufferAsset")]
       [StructLayout(LayoutKind.Sequential, Pack = 1)]
       public struct AircraftHUDBuffer
       {
           public float V1;

           // lookup delegate fields must be static fields on the buffer element type
           // the names and specific types of these are not relevant, as long as the delegate signature matches
           // these are not all required, but you will need at least one to be able to set the uniform data
           [SxUniformBufferLookup] public static AHBufferLookup LookupBuffer;
           [SxUniformBufferLookup] public static AHMemoryLookup LookupMemory;
           [SxUniformBufferLookup] public static AHSpanLookup<AircraftHUDBuffer> LookupSpan; // gives a Span<T> of length Size
           [SxUniformBufferLookup] public static AHPtrLookup<AircraftHUDBuffer> LookupPtr; // gives T* to first element
       }
   ```

   Source: [ShaderExtensionsHelpers.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ShaderExtensionsHelpers.cs#L48-L62). Fields must be static, and the struct must be `unmanaged`.

3. **Use the element under a fragment shader.** The HUD puts its buffer under the fragment of an `ImGuiShader`:

   ```xml
   <!-- HudAssests.xml L21-L26 -->
   	<ImGuiShader Id="HudShader">
   	    <Vertex Id="ImGuiVertexPost" />
   		<Fragment Path="Shaders/UI_PP_HUD.frag" >
   			<AircraftHUDBufferAsset Id="HudTestBuffer" Size="1" />
   		</Fragment>
   	</ImGuiShader>
   ```

   For an `ImGuiShader` the buffer is `set = 0, binding = 2` (set 0 binding 0 is the window texture, binding 1 the time block). The HUD's
   shader declares `layout(set = 1, binding = 0) uniform HudTestBuffer` ([Shaders/UI_PP_HUD.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/UI_PP_HUD.frag#L15-L17)), which does not match that
   layout, but nothing reads the block, so it has no effect. Copy the set and binding numbers from the ShaderExtensions README, not from
   this file. Source of the XML: [HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml#L21-L26).

4. **Write with the lookup you picked.** The HUD's writer is commented out (the buffer once carried the throttle), but the form is the
   standard one. The push constants next to it are written every frame:

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

   Source: [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L936-L954). Cache the `KeyHash` fields once (the HUD does, [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L60-L63)).

## Complete example

| File | Role |
|---|---|
| [ShaderExtensionsHelpers.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ShaderExtensionsHelpers.cs) | Shims, the uniform struct and the push-constant struct |
| [HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml) | Elements under each shader |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs) | `SetPostProcess`, `ProgramPatches` (`OnFrameEditor` postfix) |

## Variations and gotchas

- **A typo in the `Id` throws every frame.** `GetSpan` dereferences `LookupCollection.Get(key)`, which returns null for a missing key.
  `SetPostProcess` null-checks the delegate, not the lookup result.
- **Unknown XML attributes are ignored.** The push-constant element has no `Size` attribute (only `Stage`), so the `Size="1"` on the
  HUD's push-constant elements is dropped silently; the size comes from the struct.
- **The ShaderExtensions project reference is unused at runtime.** `BufferEx` and `MappedMemory` are game types and `KeyHash` is in `KSA`;
  nothing in these two files resolves to a ShaderExtensions type.
- **Layout.** `Pack = 1` is fine for `int` plus `float`; uniform buffers still follow std140 so `vec3` and `mat3` need padding.
- **Which form?** If you only ever write scalars, the span form is enough; add `LookupBuffer` only when you copy into the buffer with
  Vulkan commands.

## See also

- Siblings: [Pack=1 structs](feed-uniform-buffers-to-a-shaderextensions-shader-1.md), [share one buffer](feed-uniform-buffers-to-a-shaderextensions-shader-2.md), [overview](overview.md)
- [ShaderExtensions push constants and uniform buffers from C#](../../../../mods/ksa-aircrafthud/features/sx-push-constants-and-buffers.md), [Attribute duck typing as an inter-mod API](../../../../mods/shaderextensions/features/duck-typed-attribute-api.md)
- [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../send-push-constants-to-a-shaderextensions-shader/overview.md), [Bend the picture with CRT curvature and chromatic aberration](../../screen-effects/bend-the-picture-with-crt-curvature-and-chromatic-aberration/overview.md)
