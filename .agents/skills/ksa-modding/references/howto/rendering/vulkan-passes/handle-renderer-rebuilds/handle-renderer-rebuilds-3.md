---
type: How-To
title: Re-create the private material pipeline on the renderer-rebuild event
description: Re-enter a thread-static construction scope around the game's RebuildFrameResources so pipelines the mod owns are recompiled with the mod's private material buffer and shader variant
category: rendering
group: vulkan-passes
topic: handle-renderer-rebuilds
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/pebbles-private-clutter-materials]
techniques: [vulkan-pipeline, harmony-prefix, harmony-finalizer]
hooks:
  - harmony-prefix KSA.ClutterEcotypeRenderData.RebuildFrameResources
  - harmony-finalizer KSA.ClutterEcotypeRenderData.RebuildFrameResources
  - harmony-postfix KSA.GroundClutterRenderer.RebuildFrameResources
  - harmony-prefix KSA.Universe.ExecuteNextClothSolvers
lifecycle: [frame:dispatch-solvers, frame:prepare]
game_types: [KSA.Program, KSA.ClutterRenderer, KSA.ShaderReference]
tags: [harmony, rebuild, clutter, pipeline, finalizer, thread-static]
---

Outcome: render objects the mod built with private resources (a private material buffer and a patched fragment shader) are rebuilt the same way after the game rebuilds its renderer. When the
game recreates its own pipelines it calls the same construction code the mod redirected, so the redirection has to be active during the game's rebuild too, otherwise the rebuilt pipelines silently go
back to the stock shared buffer. Demonstrated by unscience Pebbles' private clutter materials ([Private clutter material buffer and source-color shader variant scoped to owned construction](../../../../mods/unscience/features/pebbles-private-clutter-materials.md)); the redirection itself is explained in
[replace a game texture at runtime](../../textures-and-decals/replace-a-game-texture-at-runtime/overview.md) (approach 3). Requires StarMap and Harmony.

## Background: how the game does it

`Program.RebuildRenderer` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5105) walks the renderers and calls their `RebuildFrameResources`; for ground clutter that means
`PlanetRenderer.RebuildFrameResources` -> `GroundClutterRenderer.RebuildFrameResources` -> `ClutterEcotypeRenderData.RebuildFrameResources`, which recompiles the clutter shader for each pipeline. Descriptors
capture the material buffer at pipeline creation, so drawing never needs any hook; only (re)creation does.

## Lifecycle and timing

| Moment | Hook | State |
|---|---|---|
| First commit | `Universe.ExecuteNextClothSolvers` prefix | Context entered in `ClutterResources.Build`; main thread, solvers and GPU idle (`frame:dispatch-solvers`) |
| Graphics rebuild | `Program.RebuildRenderer` (any of the callers in [approach 1](handle-renderer-rebuilds-1.md)) -> `RebuildFrameResources` | Prefix and finalizer fire inside the renderer rebuild on the main thread |
| Every other time | transpiled calls see no context | Stock behaviour; other mods and planets unaffected |

The context is `[ThreadStatic]`, so a context opened on the main thread never leaks to other threads that reach the same game code.

## Steps

1. Patch `ClutterEcotypeRenderData.RebuildFrameResources` with a **prefix that re-enters the scope** and a **finalizer that always leaves it** (and rethrows the original exception). The prefix looks the instance up
   in the `Owned` dictionary filled when the mod committed its objects, so the stock render data of the same body is rebuilt with stock bindings.

   ```csharp
   // pebbles.lib/Runtime/ClutterHooks.cs L30-L50
   public static void Apply(Harmony harmony, ClutterController controller)
   {
       if (_controller != null && !ReferenceEquals(_controller, controller)) throw new InvalidOperationException("Pebbles already owns a runtime bridge.");
       _controller = controller;
       foreach (var type in new[] { typeof(GroundClutterPlacementData), typeof(ClutterEcotypeRenderData), typeof(ClutterEcotypePhysicalData), typeof(ClutterCubeCellGrid), typeof(ClutterViewResources), typeof(SimpleVkMeshAtlas) })
           foreach (var constructor in type.GetConstructors()) harmony.Patch(constructor, prefix: Method(nameof(BeforeConstruct)));
       // Patch callers rather than tiny getters, which the JIT may inline.
       foreach (var name in new[] { "SortMaterialIds", "CreateColorRenderer", "CreateDepthPrePassRenderer", "CreateShadowDepthRenderer" })
           harmony.Patch(Required(typeof(ClutterEcotypeRenderData), name), transpiler: Method(nameof(MaterialCalls)));
       harmony.Patch(Required(typeof(Universe), nameof(Universe.ExecuteNextClothSolvers)), prefix: Method(nameof(BeforeCloth)));
       harmony.Patch(Required(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.RebuildFrameResources)), postfix: Method(nameof(RebuildOriginals)));
       harmony.Patch(Required(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.Dispose)), prefix: Method(nameof(BeforeDispose)));
       var serializeSave = AccessTools.Method(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.SerializeSave), [typeof(List<ClutterEcotypeSaveData>)])
           ?? throw new MissingMethodException(typeof(GroundClutterRenderer).FullName, nameof(GroundClutterRenderer.SerializeSave));
       harmony.Patch(serializeSave, postfix: Method(nameof(AfterSerializeSave)));
       harmony.Patch(Required(typeof(ClutterEcotypeRenderData), nameof(ClutterEcotypeRenderData.RebuildFrameResources)), prefix: Method(nameof(BeforeRebuild)), finalizer: Method(nameof(AfterRebuild)));
       harmony.Patch(Required(typeof(ShaderReference), nameof(ShaderReference.CompileVariantWithCustomOptions)), prefix: Method(nameof(CompileColor)));
   }
   public static void Remove(Harmony harmony, ClutterController controller)
   {
       if (ReferenceEquals(_controller, controller)) { controller.Dispose(); _controller = null; }
   ```

2. Implement the pair so the scope is entered only for mod-owned instances:

   ```csharp
   // pebbles.lib/Runtime/ClutterHooks.cs L89-L91
   private static void BeforeRebuild(ClutterEcotypeRenderData __instance, out IDisposable? __state)
       => __state = Owned.TryGetValue(__instance, out var resources) ? Enter(resources) : null;
   private static Exception? AfterRebuild(Exception? __exception, IDisposable? __state) { __state?.Dispose(); return __exception; }
   ```

3. Also postfix `GroundClutterRenderer.RebuildFrameResources` to rebuild the originals the mod retained for restore (`RebuildOriginals`).

4. While the scope is active, the transpiled material lookups return the private buffer and index, and a prefix on `ShaderReference.CompileVariantWithCustomOptions` substitutes the mod's fragment variant,
   compiled with `ShaderModuleUtils.FromString` and the original NUL-terminated path as debug name so includes still resolve.

5. Expect the feature to be unavailable, not wrong, when the transpilers fail: they throw unless exactly one call was replaced, which aborts the whole patch set on an incompatible build.

## Complete example

| File | Role |
|---|---|
| [pebbles.lib/Runtime/ClutterHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterHooks.cs) | Scoped context, transpiler, rebuild prefix and finalizer, shader variant |
| [pebbles.lib/Runtime/ClutterResources.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterResources.cs#L43-L64) | Private material rows with the two flag bits |
| [pebbles.lib/Runtime/ClutterRetirement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterRetirement.cs#L25-L58) | Cleanup of partially built objects |

## Variations and gotchas

- **Exact-count transpiler.** A game update that adds a second material read makes the patch set fail to apply; the feature then does not fall back to mutating the shared buffer.
- **Shader marker.** The shader substitution keys on a literal line occurring exactly once; a reworded line throws.
- **Finalizer, not postfix.** Use a finalizer so the thread-static scope is left even when the rebuild throws.
- **Partial cleanup only.** The constructor capture cannot see allocations a native constructor holds in locals.

## See also

- [Overview](overview.md)
- [Approach 1: postfix RebuildRenderer](handle-renderer-rebuilds-1.md)
- [Approach 2: rebuild in place](handle-renderer-rebuilds-2.md)
- [Private clutter material buffer and source-color shader variant scoped to owned construction](../../../../mods/unscience/features/pebbles-private-clutter-materials.md)
- [Replace a game texture or model texture with a user PNG at runtime](../../textures-and-decals/replace-a-game-texture-at-runtime/overview.md), [Free mod-owned Vulkan resources without device loss](../free-vulkan-resources-safely/overview.md)
