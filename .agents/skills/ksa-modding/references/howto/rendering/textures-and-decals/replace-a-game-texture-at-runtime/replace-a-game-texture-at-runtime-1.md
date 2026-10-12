---
type: How-To
title: BindlessTextureLibrary.SetTexture on the stock slot with deferred destruction
description: Re-point the bindless slot a stock material already samples at a mod-owned image, restore it on unbind, and retire the replaced image after frames in flight
category: rendering
group: textures-and-decals
topic: replace-a-game-texture-at-runtime
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/clutter-texture-override]
techniques: [bindless-slot-swap]
hooks: [starmap StarMapBeforeGui, harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapAfterOnFrame]
lifecycle: [frame:viewports, frame:draw-ui, frame:render]
game_types:
  - RenderCore.Systems.BindlessTextureLibrary
  - KSA.TextureReference
  - RenderCore.SimpleVkTexture
  - KSA.ClutterRenderer
  - KSA.Program
tags: [texture, replace, bindless, settexture, clutter, restore]
---

Outcome: a stock texture (the rocks, trees, grass and shrubs scattered over terrain) is drawn with a player's own image, live and reversibly, with no game files written, no Harmony patch on the renderer and no shader change. Every material sampling that bindless slot samples your image. Demonstrated by gatOS' clutter
texture override ([Ground-clutter texture override](../../../../mods/gatos/features/clutter-texture-override.md)). Prerequisites: StarMap. The upload recipe is in [upload an image as a Vulkan texture](../upload-an-image-as-a-vulkan-texture/overview.md) (approach 1); the retirement rules in [free mod-owned Vulkan resources safely](../../vulkan-passes/free-vulkan-resources-safely/overview.md) (approach 2).

## Background: how the game does it

`TextureReference` [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L20) loads a texture asset and, in `DoLoad`, claims a bindless slot: `BindlessHandle = Program.Instance.BindlessTextures.AddTexture(ImageView)` [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L149); `BindlessHandle` is a public getter [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L70), and the reference's `ImageView` is the stock view.
Materials store the handle, and shaders sample through `Common/TextureSet.glsl`. `BindlessTextureLibrary.SetTexture(int handle, VkImageView, layout)` [current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L201) rewrites a slot; the set is created with `UpdateAfterBind` and `PartiallyBound` [current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L22), so the write is picked up by the next submission.
No KSA code calls `SetTexture` in this build besides texture loading, so a mod is the only writer of those slots.

## Lifecycle and timing

| Step | Where |
|---|---|
| Bind/unbind commands update a game-free store | the main-viewport `GameViewport.OnFrame` prefix (`frame:viewports`) |
| Tick: upload, `SetTexture`, retire the replaced image | `[StarMapBeforeGui]` (`frame:draw-ui`) before the scene renders |
| Sampling | `frame:render` |

With the HUD hidden the tick runs from `[StarMapAfterOnFrame]`, after the frame was recorded, so the change first shows next frame. All Vulkan work is on the main thread. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Discover the targets.** About once a second while relevant, walk the planet renderer's `GroundClutterRenderer.CelestialsWithGroundClutter`, each body's ground clutter reference and its ecotype materials, and publish every texture asset with its content-relative path (the stable id), slot, size and mips.

   ```csharp
   // gatOS.GameMod/Game/Ksa/Paint/ClutterTextureBridge.cs L295-L330
   private void RefreshCatalog()
   {
       if (FxReflect.Terrain(out var rendererError) is not { } planet)
       {
           _store.PublishCatalog([]);
           FxReflect.Degrade(_health, CatalogAccessor, rendererError);
           PublishRuntime(available: false, error: rendererError);
           return;
       }

       if (planet.GroundClutterRenderer is not { } clutter)
       {
           _store.PublishCatalog([]);
           PublishRuntime(available: false, error: "ground clutter is not loaded");
           return;
       }

       FxReflect.Healthy(_health, CatalogAccessor);
       var rows = new Dictionary<string, (string Slot, int W, int H, int Mips, int Used, SortedSet<string> Eco)>(
           StringComparer.Ordinal);
       var walk = new WalkStats();

       foreach (var celestial in clutter.CelestialsWithGroundClutter)
       {
           walk.Celestials++;
           if (celestial.BodyTemplate.GroundClutterReference is not { } reference)
               continue;
           foreach (var ecotype in reference.Ecotypes)
           {
               walk.Ecotypes++;
               foreach (var materialRef in ecotype.MaterialReferences)
               {
                   walk.Materials++;
                   var material = materialRef.Get();
                   Add(rows, walk, ecotype.Name, "diffuse", material.DiffuseReference);
                   Add(rows, walk, ecotype.Name, "normal", material.NormalReference);
   ```

2. **Capture the stock slot,** its bindless handle and the stock `ImageView`, before the first swap, keeping the first capture across re-binds so restore always returns to stock.

3. **Upload and write the slot,** then retire the previous override image (never destroy inline). Unbind writes the captured stock view back.

   ```csharp
   // gatOS.GameMod/Game/Ksa/Paint/ClutterTextureBridge.cs L219-L244
   private Override Apply(string targetId, TextureFile file, TextureBindMode mode)
   {
       if (Program.GetRenderer() is not { } renderer)
           throw new InvalidOperationException("the renderer is not running yet");
       if (Program.Instance?.BindlessTextures is not { } bindless)
           throw new InvalidOperationException("the bindless texture table is not available yet");
       if (ResolveStock(targetId) is not { } stock)
           throw new InvalidOperationException($"stock texture '{targetId}' is gone");

       var image = UserTextureGpu.Upload(renderer, file, _maxDimension,
           faithful: mode == TextureBindMode.Faithful, "gatos:paint/textures/");

       // Capture the pristine slot BEFORE the first swap. A re-bind over an existing override keeps
       // the original capture, so restore always returns to stock and never to a previous override.
       var stockView = _live.TryGetValue(targetId, out var previous) ? previous.StockView : stock.ImageView;
       var handle = _live.TryGetValue(targetId, out var prior) ? prior.Handle : stock.BindlessHandle;

       bindless.SetTexture(handle, image.ImageView);
       _retire.Retire(previous?.Image);

       var live = new Override(targetId, file.Name, file.Version, handle, stockView, image,
           image.Width, image.Height, image.MipMapCount, UserTextureGpu.VramBytes(image));
       _live[targetId] = live;
       return live;
   }

   ```

4. **Retire by frames in flight:** destroy a replaced or unbound image `MaxFramesInFlight + 1` ticks later; teardown restores every slot, waits for the graphics-and-compute queue to idle, then drains.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Paint/ClutterTextureBridge.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/ClutterTextureBridge.cs) | Catalog, bind, unbind, reconcile |
| [gatOS.GameMod/Game/Ksa/Paint/UserTextureGpu.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/UserTextureGpu.cs) | Upload and retire queue |

## Variations and gotchas

- **One slot, many materials:** an override changes every material that uses that texture asset; the catalog's `used_by` count shows how many.
- **Texture ids** are content-relative asset paths such as `Textures/Planets/Earth/GroundClutter/Grass_Diffuse.ktx2` (the scheme was corrected in a later build).
- **Never destroy a `VkImage` a recorded frame may still sample.**
- **Colour:** clutter diffuse maps are modulation maps; see approach 1 of the upload topic.
- `TextureReference.Dispose(Device)` [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L77-L82) frees the slot and disposes the stock texture, so a captured stock view is not valid after the game disposes that reference; re-capture after a reload.

## See also

- [Overview](overview.md)
- [Approach 2: rebuild the mod mesh resource with a PNG override](replace-a-game-texture-at-runtime-2.md)
- [Approach 3: private material buffer behind a scoped context](replace-a-game-texture-at-runtime-3.md)
- [Ground-clutter texture override](../../../../mods/gatos/features/clutter-texture-override.md)
- [Upload an image or procedural pixels as a GPU texture](../upload-an-image-as-a-vulkan-texture/overview.md), [Free mod-owned Vulkan resources without device loss](../../vulkan-passes/free-vulkan-resources-safely/overview.md), [Recreate mod render targets when KSA rebuilds its renderer](../../vulkan-passes/handle-renderer-rebuilds/overview.md)
