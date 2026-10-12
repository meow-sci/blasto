---
type: Mod Feature
title: PNG decals as mod-owned slots in KSA's bindless texture table
description: Graffiti decodes library PNGs with the game's TextureLoader, uploads them as mip-mapped SimpleVkTextures, claims slots in Program.BindlessTextures so the shader needs only a uint index, hot-swaps changed files on Rescan and destroys freed images after every frame in flight has retired
mod: unscience
submod: graffiti
category: rendering
techniques: [vulkan-texture, starmap-lifecycle]
hooks:
  - starmap StarMapBeforeGui (via unscience ISubmod.Update; retire-queue tick)
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent / RenderFloatingWindows; upload on place or Rescan)
lifecycle: [frame:draw-ui, event:load]
game_types:
  - RenderCore.Systems.BindlessTextureLibrary
  - RenderCore.SimpleVkTexture
  - RenderCore.TextureAsset
  - Brutal.TextureApi.TextureLoader
  - KSA.Program
  - Core.Renderer
  - KSA.Rendering.RetiredResourceQueue
tags: [textures, bindless, vulkan, png, hot-reload, resource-lifetime]
---

Graffiti decals reference their image by a single `uint` in the push constants. That works because
each PNG is uploaded once into a graffiti-owned GPU image and registered in the game's own bindless
texture library, the descriptor array every KSA shader samples through `Common/TextureSet.glsl`.
Editing a PNG on disk and pressing **Rescan PNGs** swaps the image under every decal that uses
it.[^decal-textures]

Mechanism in one line: `TextureLoader.LoadFromMemory` (PNG forced to RGBA8) -> `TextureAsset` ->
`SimpleVkTexture` (longest edge capped at 2048 px, full mip chain) ->
`Program.Instance.BindlessTextures.AddTexture(imageView)`; release calls `FreeTexture(handle)` and
parks the image in a tick-counted retire queue.

## How it works

`DecalTextureCache` keys bound images by PNG file name (case-insensitive) plus the file's last-write
time:[^decal-textures]

1. **Resolve** (`Resolve(name, out state)`): missing file -> `Missing`; same write time as the bound
   entry -> reuse the slot; different write time -> release the old slot (hot swap) and upload again.
   A decode failure is latched per (name, write time), so a broken file is retried only after it
   changes. If the renderer or `BindlessTextures` does not exist yet, it reports `Missing` without
   latching a failure.
2. **Upload**: decode with `TextureLoader.LoadFromMemory(bytes, FormatType.Png,
   TextureAsset.LoadOptions(VkFormat.R8G8B8A8UNorm, KtxTranscodeFmt.Rgba32))` (stb forced to four
   channels, the same defaults `TextureReference.DoLoad` uses for game textures), wrap in
   `new TextureAsset(decoded, "graffiti:decals/<name>")` (the path names the Vulkan image and must be
   non-empty), and build `new SimpleVkTexture(allocator, stagingPool, asset,
   new CreateOptions(2048, ReductionMethod.Downsample, fillMipChain: true))` on a one-shot staging
   pool that is submitted and waited on. The decoded CPU image is destroyed explicitly, because
   `ITexture` is not `IDisposable`.[^ksa-textureloader][^ksa-textureasset][^ksa-simplevktexture][^ksa-texturereference]
3. **Bind**: `AddTexture(image.ImageView)` returns the slot. Any exception after the slot is claimed
   frees it before retiring the image, so the table never points at destroyed memory.
4. **Release**: `FreeTexture(handle)`, which the library implements by rewriting the slot to its
   built-in white "empty" texture and returning the index to its free list, then queue the image with
   `MaxFramesInFlight + 1` ticks.[^ksa-bindless]
5. **Drain**: the first thing `GraffitiSubmod.Update` does each frame is age the retire queue and
   dispose images that reached zero. `Reconcile` (after removals, clears and rescans) releases every
   image no placed decal references. Teardown (`Dispose`, save reset) waits on
   `GraphicsAndCompute.WaitIdle()` and destroys everything immediately.[^graffiti-submod][^graffiti-saves]

The PNG catalog itself (folder `.unscience/pngs` under the KSA documents folder, scan, import with
copy-and-uniquify, the shared ImGui file browser) is the shared `PngLibrary`, also used by
Free Fallin; see [shared file library](./lib-shared-file-library.md).[^png-library]

The shader samples with `SAMPLE_TEXTURE(texId, 0, uv)`: sampler slot 0 of the library is the
linear-clamped sampler with `MaxLod = 1000` it registers in its constructor, so mips are used.[^ksa-bindless]

## Lifecycle and timing

| Operation | Called from | Lifecycle |
|---|---|---|
| `Resolve` on placement | `PlaceAtCursor` in `RenderFloatingWindows` (`[StarMapAfterGui]`) | `frame:draw-ui` |
| `Resolve` for all entries + `Reconcile` | `RefreshLibrary` (Rescan button in `RenderContent`, and `Initialize` at startup with no decals) | `frame:draw-ui` |
| `Resolve` on restore, `DisposeAll` on reset | Save participant restore/reset | `event:load` |
| `Drain` | top of `GraffitiSubmod.Update` (`[StarMapBeforeGui]` or the hidden-HUD replay) | `frame:draw-ui`, once per frame |

All of it runs on the main thread, outside command recording, so a slot claimed this frame is valid
when `RenderGame` records the decal pass later in the same frame. Counting retire ticks in `Update`
works because unscience calls `Update` exactly once per frame (the hidden-HUD replay replaces, never
duplicates, the StarMap call). The upload blocks the main thread on its own staging submit; that is a
one-off cost per image, not per frame.[^graffiti-submod]

## Key code

| Path | Role |
|---|---|
| [graffiti.lib/DecalTextures.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalTextures.cs#L52-L102) | `Resolve`: hot swap and failure latch |
| [graffiti.lib/DecalTextures.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalTextures.cs#L104-L145) | `Reconcile`, `Drain`, `DisposeAll` |
| [graffiti.lib/DecalTextures.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalTextures.cs#L147-L253) | `Bind`, `Upload`, `Release`, `Retire`, decode cleanup |
| [graffiti.lib/GraffitiSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/GraffitiSubmod.cs#L227-L246) | `RefreshLibrary` (Rescan re-resolves every placed decal) |
| [ksa-abstractions.lib/PngLibrary.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PngLibrary.cs) | Shared PNG catalog |

## Snippets

Hot swap keyed on the file's write time:

```csharp
// graffiti.lib/DecalTextures.cs L52-L76
    internal int? Resolve(string name, out DecalTextureState state)
    {
        var path = PngLibrary.FullPath(name);
        if (!File.Exists(path))
        {
            state = DecalTextureState.Missing;
            return null;
        }

        var lastWrite = File.GetLastWriteTimeUtc(path);
        if (_bound.TryGetValue(name, out var bound))
        {
            if (bound.LastWriteUtc == lastWrite)
            {
                state = DecalTextureState.Ready;
                return bound.Handle;
            }
            Release(name); // the file changed on disk — hot-swap to the new bytes
        }

        if (_failed.TryGetValue(name, out var failedWrite) && failedWrite == lastWrite)
        {
            state = DecalTextureState.Failed;
            return null;
        }
```

Claiming a slot, and giving it back if anything after the claim fails:

```csharp
// graffiti.lib/DecalTextures.cs L148-L169
    private Bound Bind(Core.Renderer renderer, string name, string path, DateTime lastWrite)
    {
        if (Program.Instance?.BindlessTextures is not { } bindless)
            throw new InvalidOperationException("the bindless texture table is not available yet");

        var image = Upload(renderer, name, File.ReadAllBytes(path));
        var handle = -1;
        try
        {
            handle = bindless.AddTexture(image.ImageView);
            return new Bound(image, handle, lastWrite);
        }
        catch
        {
            // Anything after AddTexture must give the slot back: retiring the image while a
            // claimed slot still points at it would leave the table sampling destroyed memory.
            if (handle >= 0)
                bindless.FreeTexture(handle);
            Retire(image);
            throw;
        }
    }
```

Decode and upload with the game's loaders:

```csharp
// graffiti.lib/DecalTextures.cs L175-L194
    private SimpleVkTexture Upload(Core.Renderer renderer, string name, byte[] bytes)
    {
        var decoded = TextureLoader.LoadFromMemory(bytes, TextureLoader.FormatType.Png,
            TextureAsset.LoadOptions(VkFormat.R8G8B8A8UNorm, Brutal.KtxApi.KtxTranscodeFmt.Rgba32));
        try
        {
            // FilePath must be non-empty (the ctor throws otherwise) and names the Vulkan image.
            var asset = new TextureAsset(decoded, $"graffiti:decals/{name}");
            using var pool = renderer.Allocator.CreateStagingPool(renderer.Graphics, 1);
            var image = new SimpleVkTexture(renderer.Allocator, pool, asset,
                new SimpleVkTexture.CreateOptions(MaxDimension,
                    SimpleVkTexture.CreateOptions.ReductionMethod.Downsample, fillMipChain: true));
            pool.Submit().Wait();
            return image;
        }
        finally
        {
            DestroyDecoded(decoded);
        }
    }
```

Slot first, image later:

```csharp
// graffiti.lib/DecalTextures.cs L200-L219
    private void Release(string name)
    {
        if (!_bound.Remove(name, out var bound))
            return;
        try
        {
            Program.Instance?.BindlessTextures?.FreeTexture(bound.Handle);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"graffiti: bindless slot {bound.Handle} free failed: {ex.Message}");
        }
        Retire(bound.Image);
    }

    private void Retire(SimpleVkTexture image)
    {
        var frames = Program.GetRenderer()?.MaxFramesInFlight ?? 3;
        _retiring.Add((image, frames + 1));
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `BindlessTextureLibrary.AddTexture(VkImageView, VkImageLayout)` / `FreeTexture(int)` | [current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L186-L240) | Claim a slot; free rewrites it to the empty texture |
| `BindlessTextureLibrary` constructor, `ClampToDeviceLimits` | [current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L56-L162) | Update-after-bind / partially-bound layout, empty texture in slot 0, sampler 0 |
| `Program.BindlessTextures` (created with 1024 slots) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L830) | The shared table, shared with every game texture (field at L111) |
| `TextureLoader.LoadFromMemory(ReadOnlySpan<byte>, FormatType, params TextureLoadSettings?[])` | [current/decomp/Brutal.TextureApi/TextureLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.TextureApi/TextureLoader.cs#L135-L150) | PNG decode through stb |
| `TextureAsset(ITexture, string)`, `TextureAsset.LoadOptions(VkFormat, KtxTranscodeFmt)` | [current/decomp/RenderCore/TextureAsset.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/TextureAsset.cs#L61-L115) | CPU asset wrapper; forced channel count |
| `SimpleVkTexture(IImageAllocator, StagingPool, TextureAsset, CreateOptions)` | [current/decomp/RenderCore/SimpleVkTexture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/SimpleVkTexture.cs#L28-L60) | Max dimension, downsample, mip chain (ctor at L245) |
| `TextureReference.DoLoad` | [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L84-L104) | The game's own default load options, mirrored |

## Pitfalls and version notes

- **Capacity.** `Program` creates the library with 1024 slots, clamped down on devices with fewer
  update-after-bind sampled images, and every game texture lives in the same table. Graffiti holds one
  slot per distinct PNG in use, not per decal. The 2048 figure in the code is the image edge cap, not
  a slot count.[^ksa-program][^ksa-bindless]
- **Free the slot before the image.** The order matters: the slot is safe as soon as `FreeTexture`
  rewrote it, but frames already recorded may still sample the old image.
- **Retire counting.** The queue counts `Update` calls, assuming one per frame. KSA 5554 ships its own
  frame-stamped `RetiredResourceQueue`
  ([current/decomp/KSA.Rendering/RetiredResourceQueue.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RetiredResourceQueue.cs#L9-L88),
  drained by `RetiredResourceQueue.CheckQueued` in `Program.OnFrame`);
  graffiti predates it and its comment that "KSA has no deferred-destroy helper" is out of date for
  this build.[^ksa-retiredresourcequeue]
- **Rescan is manual.** There is no file watcher; hot swap happens only when `Resolve` runs (Rescan,
  placement, restore).
- Decode buffers leak unless `StbTexture/KtxTexture/GliTexture.Destroy()` is called; `ITexture` has
  no finalizer.[^decal-textures]

## See also

- [Projected decal pass](./graffiti-projected-decal-pass.md) (samples the slots)
- [Shared file library](./lib-shared-file-library.md)
- [Free Fallin canopy texture compositing](./free-fallin-canopy-texture-compositing.md) (same PNG library, different upload path)
- [Graffiti sub-mod](../submods/graffiti.md) and the [mod overview](../overview.md)
- [Unit-cube decal volumes with reverse-Z depth reconstruction](../../../howto/rendering/textures-and-decals/project-decals-onto-scene-geometry/project-decals-onto-scene-geometry-1.md) - how-to article demonstrating this feature
- [Mip-mapped library textures claimed into Program.BindlessTextures, hot-swapped on rescan](../../../howto/rendering/textures-and-decals/upload-an-image-as-a-vulkan-texture/upload-an-image-as-a-vulkan-texture-2.md) - how-to article demonstrating this feature

[^decal-textures]: graffiti.lib/DecalTextures.cs
[^graffiti-submod]: graffiti.lib/GraffitiSubmod.cs
[^graffiti-saves]: graffiti.lib/GraffitiSubmod.Saves.cs
[^png-library]: ksa-abstractions.lib/PngLibrary.cs
[^ksa-bindless]: RenderCore.Systems.BindlessTextureLibrary (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-simplevktexture]: RenderCore.SimpleVkTexture (decompiled, build 2026.10.10.5554)
[^ksa-textureasset]: RenderCore.TextureAsset (decompiled, build 2026.10.10.5554)
[^ksa-textureloader]: Brutal.TextureApi.TextureLoader (decompiled, build 2026.10.10.5554)
[^ksa-texturereference]: KSA.TextureReference (decompiled, build 2026.10.10.5554)
[^ksa-retiredresourcequeue]: KSA.Rendering.RetiredResourceQueue (decompiled, build 2026.10.10.5554)
