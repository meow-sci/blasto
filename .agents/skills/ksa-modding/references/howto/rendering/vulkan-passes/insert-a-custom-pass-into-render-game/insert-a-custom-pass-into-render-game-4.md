---
type: How-To
title: Prefix SunbloomRenderer.Render to dispatch compute on the HDR scene
description: Hook the public SunbloomRenderer.Render method with a Harmony prefix to dispatch compute shaders on the HDR scene colour and depth just before bloom and the tonemap composite
category: rendering
group: vulkan-passes
topic: insert-a-custom-pass-into-render-game
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksarmory/features/compute-pass-before-bloom
  - mods/ksastructures/features/underground-dark-compute-pass
techniques: [harmony-prefix, vulkan-pipeline]
hooks: [harmony-prefix KSA.SunbloomRenderer.Render]
lifecycle: [frame:render, render:bloom]
game_types: [KSA.SunbloomRenderer, KSA.Program, KSA.IViewport, KSA.Rendering.BarrierBatch]
tags: [harmony, compute, hdr, bloom, prefix, public-method]
---

Outcome: a pass that writes straight into KSA's HDR scene image inside the game's own command buffer, at a
moment when everything the scene contains has been drawn and bloom and the tonemap composite are still to come,
so what the mod writes blooms and is graded like the rest of the picture. No transpiler is needed: the hook is a
Harmony prefix on the **public** method `SunbloomRenderer.Render(CommandBuffer, IViewport, int)`. Demonstrated by
KSArmory ([A mod compute pass recorded into KSA's frame just before bloom](../../../../mods/ksarmory/features/compute-pass-before-bloom.md)) and KSAStructures
([Underground dark compute pass](../../../../mods/ksastructures/features/underground-dark-compute-pass.md)). The shader side (declaring `.comp` files as assets,
bindings, dispatch sizing) is in [Paint into or rescale the HDR scene with a compute pass shipped as a Shader asset](../modify-the-hdr-scene-with-a-compute-pass/overview.md).

## Background: how the game does it

`SunbloomRenderer.Render(CommandBuffer commandBuffer, IViewport viewport, int frameIndex)`
[current/decomp/KSA/SunbloomRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SunbloomRenderer.cs#L326) is called from `RenderGame` right after the game barriers the scene colour into
`StorageReadFc` and the depth into `DepthSampledReadVfc`; overall bloom follows:

```csharp
// current/decomp/KSA/Program.cs L4910-L4916
inImages = stackalloc VkImageMemoryBarrier2[2];
BarrierBatch barrierBatch3 = new BarrierBatch(inImages);
barrierBatch3.Add(_offscreenTarget.ColorImage, ImageBarrierInfo.Presets.StorageReadFc);
barrierBatch3.Add(_offscreenTarget.DepthImage, ImageBarrierInfo.Presets.DepthSampledReadVfc);
barrierBatch3.SubmitAndFlush(commandBuffer2);
_sunbloomRenderer.Render(commandBuffer2, MainViewport, resourceFrameIndex);
_overallBloomRenderer.Render(commandBuffer2, MainViewport, resourceFrameIndex);
```

It has three call sites: `RenderViewport` for visible secondary viewports with atmosphere, `RenderGame` for the main
view, and `RenderEditor`.

## Lifecycle and timing

| Fact | Detail |
|---|---|
| Phase | `render:bloom` (step 16 of the `RenderGame` table in [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence)), inside `frame:render` |
| State at the prefix | Command buffer recording; no dynamic-rendering scope open (the scene scope closed earlier); scene colour is the resolved single-sample HDR image in `General` layout; depth is in a sampled layout |
| Thread | Main thread; nothing is submitted until `PostRender` |
| Per call | Once per call site, so secondary viewports run it first with their own `ShaderSlot` |

Raytracing only exists for the cockpit view and runs before this hook, so in IVA with ray tracing on the pass
reads (and paints over) the ray-traced result; its own output is screen-space and never enters the acceleration
structure.

## Steps

1. Resolve the method with the exact signature and put it in your assembly's metadata so a signature change is a build
   error instead of a silent no-op (KSAStructures' `PinTheSignature` trick). Patch with a prefix; add
   `Priority.First` when other mods may also hook it and you want your result in place for them.

   ```csharp
   // src/KSArmory/Ksa/CloudPassHook.cs L55-L87
   public static void Install(Func<float> tint)
   {
       _tint = tint;

       if (Installed) return;

       try
       {
           MethodInfo? target = typeof(SunbloomRenderer).GetMethod(
               nameof(SunbloomRenderer.Render), [typeof(CommandBuffer), typeof(IViewport), typeof(int)]);

           if (target is null)
           {
               Log.Warn("KSA has no SunbloomRenderer.Render(CommandBuffer, IViewport, int) to hook; "
                        + "this mod's own shader pass will not draw");
               return;
           }

           MethodInfo prefix = typeof(CloudPassHook).GetMethod(
               nameof(BeforeSunbloom), BindingFlags.NonPublic | BindingFlags.Static)!;

           _harmony = new Harmony(HarmonyId);
           _harmony.Patch(target, prefix: new HarmonyMethod(prefix));

           Installed = true;
           Log.Info("the mod's own shader pass runs before bloom, via SunbloomRenderer.Render");
       }
       catch (Exception e)
       {
           Log.Warn($"could not hook the shader pass: {e.Message}; it will not draw");
       }
   }

   ```

   KSAStructures' variant, with the pinning method and `Priority.First`:

   ```csharp
   // src/KSAStructures/Ksa/DarkPass.cs L77-L114
   /// Puts <c>SunbloomRenderer.Render</c> in this assembly's metadata, so a KSA signature change is
   /// a build error rather than a garage that quietly keeps its daylight. Never called.
   /// </summary>
   public static void PinTheSignature()
   {
       SunbloomRenderer? never = null;
       never?.Render(default, null!, 0);
   }

   public static void Install()
   {
       if (Installed) return;

       try
       {
           MethodInfo? target = typeof(SunbloomRenderer).GetMethod(
               nameof(SunbloomRenderer.Render), [typeof(CommandBuffer), typeof(IViewport), typeof(int)]);

           if (target is null)
           {
               Log.Warn("dark pass: KSA has no SunbloomRenderer.Render(CommandBuffer, IViewport, int) to hook; "
                        + "what is under the ground keeps its daylight");
               return;
           }

           MethodInfo prefix = typeof(DarkPass).GetMethod(nameof(BeforeSunbloom), BindingFlags.NonPublic | BindingFlags.Static)!;

           _harmony = new Harmony(HarmonyId);
           _harmony.Patch(target, prefix: new HarmonyMethod(prefix) { priority = Priority.First });

           Installed = true;
           Log.Info("dark pass: runs before bloom, via SunbloomRenderer.Render");
       }
       catch (Exception e)
       {
           Log.Warn($"dark pass: could not hook the renderer ({e.Message}); what is under the ground keeps its daylight");
       }
   }
   ```

2. In the prefix, forward `commandBuffer`, `viewport` and `frameIndex` to your recorder. Key per-viewport resources by
   `viewport.ShaderSlot`, never by the frame index. (KSAStructures' prefix takes only the arguments it needs, returns `true`
   so the original still runs, counts faults and stands down after a limit, and records for `Program.MainViewport` only.)

   ```csharp
   // src/KSArmory/Ksa/CloudPassHook.cs L100-L110
   private static void BeforeSunbloom(CommandBuffer commandBuffer, IViewport viewport, int frameIndex)
   {
       // Always: at zero the pass skips only the cloud, and the rings, holes and tracers it paints
       // have no other way onto the screen.
       float tint = _tint?.Invoke() ?? 0f;
       CloudPass.Record(commandBuffer, viewport, frameIndex, Math.Max(0f, tint));

       // Last and whatever the cloud pass is set to: a sensor sees the burst too, and its look is
       // the director's setting rather than a display one.
       SensorPass.Record(commandBuffer, viewport);
   }
   ```

3. Record compute work: bind your pipeline, push constants, `Dispatch`. Between two dispatches that both read and write the
   scene, record a compute-to-compute memory barrier. Declare state for images you create through
   `BarrierBatch.Add(RenderImage, ImageBarrierInfo)`.

4. Wrap the record in a `try` that logs once, drops the views and marks the feature failed, so a broken shader degrades to
   "no effect" instead of crashing the render loop. Expect the first log line
   `the mod's own shader pass runs before bloom, via SunbloomRenderer.Render`.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/CloudPassHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPassHook.cs) | Hook install, removal, the prefix |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs) | Views, pipelines, dispatches |
| [src/KSAStructures/Ksa/DarkPass.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/DarkPass.cs) | A smaller pass: one kernel over colour, depth and light images |
| [src/KSAStructures/Shaders/KSAStructuresDark.comp](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Shaders/KSAStructuresDark.comp) | The kernel |

## Variations and gotchas

- **Write access is not declared to KSA.** The engine's preset before the call is `StorageReadFc` (shader read only);
  the mod writes the image anyway and records its own compute-to-compute barriers, so the tracked state still says
  "read". It works in play; synchronization validation was not checked.
- **Runs in camera windows and the editor.** Filter by viewport if the effect belongs to the main view only.
- **Pipelines are dropped, not destroyed** in KSArmory (`ComputePipelineWrapper.Destroy` exists but is never called),
  so every rebuild leaves a pipeline behind; destroy yours after a wait.
- **Shader errors appear at run time.** KSA compiles the mod's `.comp` assets at load, so the C# build cannot catch GLSL faults.
- **Push constants at the 128-byte floor** if you follow the KSArmory cloud layout.
- Compared with a `RenderGame` transpiler (approach 5), a public-method prefix has no IL to match, so a recompile
  cannot break it silently.

## See also

- [Overview](overview.md)
- [Approach 1: ResolveAttachments postfix](insert-a-custom-pass-into-render-game-1.md)
- [Approach 5: RenderGame transpiler](insert-a-custom-pass-into-render-game-5.md)
- [A mod compute pass recorded into KSA's frame just before bloom](../../../../mods/ksarmory/features/compute-pass-before-bloom.md), [Underground dark compute pass](../../../../mods/ksastructures/features/underground-dark-compute-pass.md)
- [Paint into or rescale the HDR scene with a compute pass shipped as a Shader asset](../modify-the-hdr-scene-with-a-compute-pass/overview.md)
