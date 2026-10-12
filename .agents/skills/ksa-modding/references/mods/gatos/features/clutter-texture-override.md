---
type: Mod Feature
title: Ground-clutter texture override
description: Replace a stock ground-clutter texture at runtime by decoding a user image with the game's own loaders and re-pointing its existing bindless texture slot, with the stock view captured for restore and old images retired only after every frame in flight has finished
mod: gatos
category: rendering
techniques: [vulkan-texture, bindless-slot-swap, deferred-destruction]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapBeforeGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:viewports, frame:draw-ui, frame:after-on-frame]
game_types:
  - RenderCore.Systems.BindlessTextureLibrary
  - RenderCore.SimpleVkTexture
  - Brutal.TextureApi.TextureLoader
  - KSA.Program
  - KSA.PlanetRenderer
  - KSA.GroundClutterRenderer
  - Core.Renderer
tags: [vulkan, textures, bindless, ground-clutter, terrain, runtime-assets]
---

`/sim/paint/textures` lets a player draw the rocks, trees, grass and shrubs scattered over the
terrain with their own image: upload a PNG (or JPEG, BMP, HDR, DDS, KTX, KTX2), bind it to a stock
texture id, and it is live that frame. Nothing is written to game asset folders, there is no
Harmony patch, no shader change and no renderer rebuild. The technique is the cheapest way to swap
any texture KSA samples through its bindless table.

## How it works

**Discover** ([gatOS.GameMod/Game/Ksa/Paint/ClutterTextureBridge.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/ClutterTextureBridge.cs#L295-L330)): about once a
second while relevant, walk `PlanetRenderer.GroundClutterRenderer.CelestialsWithGroundClutter`,
each body's ground clutter reference and its materials, and publish every texture asset with its
content-relative path (the stable id), slot, size, mips, how many materials use it and which
ecotypes. The `PlanetRenderer` handle reuses the FX editors' reflection accessor.

**Upload** ([gatOS.GameMod/Game/Ksa/Paint/UserTextureGpu.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/UserTextureGpu.cs#L56-L95)): decode with
`TextureLoader.LoadFromMemory(bytes, format, TextureAsset.LoadOptions(R8G8B8A8UNorm, Rgba32))`, the
same settings `TextureReference` uses for game assets, wrap in a `TextureAsset`, then create a
`SimpleVkTexture` from `renderer.Allocator` and a staging pool with a size cap and a generated mip
chain, and wait for the staging submit.

**Bind** ([gatOS.GameMod/Game/Ksa/Paint/ClutterTextureBridge.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/ClutterTextureBridge.cs#L219-L244)): capture the stock
`ImageView` and the slot's bindless handle (keeping the first capture across re-binds), then call
`Program.Instance.BindlessTextures.SetTexture(handle, ourImageView)`. Every material that samples
that slot now samples the user image. No KSA code calls `BindlessTextureLibrary.SetTexture` in 5554,
so gatOS is the only writer of those slots. Unbind writes the captured stock view back.

**Reconcile, not command-execute.** Commands only change a game-free `TextureStore` (bindings and a
revision counter). `Tick()` compares the store revision with the last applied one and returns
immediately when equal, so the idle cost is one integer compare per frame
([gatOS.GameMod/Game/Ksa/Paint/ClutterTextureBridge.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/ClutterTextureBridge.cs#L123-L142)).

**Deferred destruction.** A replaced or unbound image may still be referenced by a frame in flight,
so it goes on a retire queue and is destroyed `MaxFramesInFlight + 1` ticks later; teardown restores
every slot, waits for `GraphicsAndCompute` to go idle, then drains
([gatOS.GameMod/Game/Ksa/Paint/UserTextureGpu.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/UserTextureGpu.cs#L160-L200)).

**Colour fidelity.** Clutter diffuse maps are modulation maps, not photographs: the shader treats
alpha 0 as sRGB and alpha 1 as linear, maps RGB with `x*2-1` then adds 1 (a doubling), and mixes the
per-instance terrain tint in by alpha ([current/Content/Core/Shaders/Planet/GroundClutter/Solid.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Planet/GroundClutter/Solid.frag#L326-L337)).
The default `faithful` mode rescales RGB by `2^(-1/2.2)` and clears alpha so an ordinary sRGB PNG
renders as authored in every biome; a `raw` token uploads bytes untouched.

## Lifecycle and timing

- `bind`/`unbind`/`clear` commands only update the game-free store; they execute in the Frame-phase
  drain (the main-viewport `GameViewport.OnFrame` prefix, `frame:viewports`). The bridge is ticked
  later from `DrivePaint`, which runs in `StarMapBeforeGui` before the scene renders, or in
  `StarMapAfterOnFrame` on hidden-HUD frames, after the frame has been recorded and submitted; then
  the slot change first shows in the next frame. `DrivePaint` is never called from the viewport prefix.
- Uploads arrive on transport threads into the game-free store; all Vulkan work (decode, upload,
  slot writes, destruction) happens in the tick on the main thread. The staging submit waits for its
  own transfer only; it does not stall the frame's command buffers.
- The bindless set is created with `UpdateAfterBind` and `PartiallyBound` binding flags
  ([current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L22-L22)), so a
  slot write between frames is picked up by the next submission; the replaced image stays alive on
  the retire queue for frames still in flight.

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Paint/ClutterTextureBridge.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/ClutterTextureBridge.cs) | catalog walk, reconcile, bind and restore |
| [gatOS.GameMod/Game/Ksa/Paint/UserTextureGpu.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/UserTextureGpu.cs) | decode, upload, faithful correction, retire queue |
| [gatOS.SimFs/Paint/TextureStore.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Paint/TextureStore.cs) | game-free uploads, caps, bindings, revision |
| [gatOS.SimFs/Paint/TextureCommands.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Paint/TextureCommands.cs) | `bind`/`unbind`/`clear` grammar |

## Snippets

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `BindlessTextureLibrary.SetTexture(int, VkImageView, VkImageLayout)` | [current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L186-L220) | re-point a slot (also `AddTexture`, `FreeTexture`) |
| `Program.BindlessTextures` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L111-L111) | the game's bindless table (1024 slots) |
| `TextureLoader.LoadFromMemory` | [current/decomp/Brutal.TextureApi/TextureLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.TextureApi/TextureLoader.cs) | image decode |
| `SimpleVkTexture` | [current/decomp/RenderCore/SimpleVkTexture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/SimpleVkTexture.cs) | GPU image with mips |
| `PlanetRenderer.GroundClutterRenderer`, `GroundClutterRenderer.CelestialsWithGroundClutter` | [current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L290-L290) | catalog |
| `Renderer.MaxFramesInFlight` | [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L28-L28) | retire delay |

## Pitfalls and version notes

- A slot holds a texture asset, so an override changes every material that uses it (the catalog's
  `used_by` shows how many).
- Never destroy a `VkImage` a recorded frame may still sample; defer by frames in flight.
- The texture-id scheme was corrected at 5348 per the repo validation notes; ids are content-relative
  asset paths such as `Textures/Planets/Earth/GroundClutter/Grass_Diffuse.ktx2`.
- In-game validation of this feature is still listed as pending in the repo.

## See also

- [Projected sticker decals](sticker-projected-decals.md)
- [Vehicle and EVA paint](vehicle-and-eva-paint.md)
- [FX editors via reflection](fx-editor-reflection.md)
- [HTTP /v1 API server](http-v1-api-server.md)
- [gatOS overview](../overview.md)
- [BindlessTextureLibrary.SetTexture on the stock slot with deferred destruction](../../../howto/rendering/textures-and-decals/replace-a-game-texture-at-runtime/replace-a-game-texture-at-runtime-1.md) - how-to article demonstrating this feature
- [Decode with the game's TextureLoader and re-point an existing bindless slot](../../../howto/rendering/textures-and-decals/upload-an-image-as-a-vulkan-texture/upload-an-image-as-a-vulkan-texture-1.md) - how-to article demonstrating this feature
- [Retire by MaxFramesInFlight + 1 frames](../../../howto/rendering/vulkan-passes/free-vulkan-resources-safely/free-vulkan-resources-safely-2.md) - how-to article demonstrating this feature

