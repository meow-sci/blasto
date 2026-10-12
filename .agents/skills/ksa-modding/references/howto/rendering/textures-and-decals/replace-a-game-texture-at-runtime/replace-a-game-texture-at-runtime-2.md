---
type: How-To
title: PNG override rebuilds the mod mesh's private resource; UV scale and offset re-uploads the vertex buffer
description: Swap the diffuse texture of a mod-owned static mesh by building a complete replacement resource first, and remap UVs by re-uploading vertex buffers computed from the original vertices
category: rendering
group: textures-and-decals
topic: replace-a-game-texture-at-runtime
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/sphinx-uv-texture-overrides]
techniques: [vulkan-texture]
hooks:
  - starmap StarMapBeforeGui
  - starmap StarMapAfterGui
  - harmony-postfix KSA.StaticObjectRenderer.WriteCommandsColor
lifecycle: [frame:draw-ui, frame:render, event:save, event:load]
game_types:
  - RenderCore.Systems.BindlessTextureLibrary
  - KSA.TextureReference
  - RenderCore.SimpleVkTexture
  - KSA.ClutterRenderer
  - KSA.Program
tags: [texture, replace, uv, vertex-buffer, private-resource, png]
---

Outcome: a mod-built static mesh can wear its embedded GLB materials or a PNG from a shared catalog, and its texture coordinates can be tiled and shifted live per placement. The mesh is not a stock object, so nothing in the game is patched: the mod owns a private render resource and replaces either the whole resource (new PNG) or just its vertex buffers (new UV mapping).
Demonstrated by unscience Sphinx ([Live per-static UV scale and offset with shared-PNG texture overrides](../../../../mods/unscience/features/sphinx-uv-texture-overrides.md)). Prerequisites: StarMap. Uses the upload helper from [submit your own Vulkan commands](../../vulkan-passes/submit-your-own-vulkan-commands/overview.md) (approach 1) and the swap rules of [free mod-owned Vulkan resources safely](../../vulkan-passes/free-vulkan-resources-safely/overview.md) (approach 3).

## Background: how the game does it

The stock static-object fragment shader samples diffuse, normal, PBR and alpha with the same `inUv` and the sampler bound at set 2, binding 1; Sphinx binds its own sampler there with `Repeat` addressing so coordinates outside 0..1 tile. A static object's vertex is 32 bytes interleaved (position, normal, UV at byte 24), and per-draw material data holds bindless handles
(`PerDrawData.DiffuseTextureIndex`, `AlphaTextureIndex`, and so on). Textures are `TextureReference`s with a `BindlessHandle` [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L70); `TextureReference.EmptyWhite` / `EmptyNormal` [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L22-L26) are the neutral fallbacks.

## Lifecycle and timing

| Step | Where | Phase |
|---|---|---|
| Widget edits, Retry, reset | `RenderContent` (`[StarMapAfterGui]`) | `frame:draw-ui` |
| Upload and swap, or resource rebuild | queued closure in `ISubmod.Update` (`[StarMapBeforeGui]`, or the hidden-HUD replay) | `frame:draw-ui` of the next frame |
| Draw with the new buffers | render-hook postfixes on `StaticObjectRenderer` | `frame:render` |

Uploads happen on the main thread with their own command buffer and fence, before this frame's command recording, and block until the copy and a full device idle complete. The `Update` queue drain also waits for the vehicle and cloth solvers whenever anything is pending, so a texture edit waits for the in-flight physics step.
See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Keep the original vertices in managed memory** and always apply the mapping to them (`original UV * scale + offset`), so repeated edits do not accumulate error. Validate scale (0.01 to 1000) and finite values.

   ```csharp
   // sphinx.lib/TextureMapping.cs L7-L36
   [StructLayout(LayoutKind.Sequential)]
   internal struct StaticVertex { public Vector3 Position, Normal; public Vector2 Uv; }

   /// <summary>Per-placement mapping, always applied to the original imported UVs.</summary>
   internal readonly record struct TextureMapping(Vector2 Scale, Vector2 Offset)
   {
       public static TextureMapping Identity => new(Vector2.One, Vector2.Zero);

       public void Validate()
       {
           if (!Finite(Scale) || !Finite(Offset) || Scale.X < .01f || Scale.Y < .01f ||
               Scale.X > 1000 || Scale.Y > 1000)
               throw new ArgumentException("Texture scale must be 0.01–1000 on both axes; UV offsets must be finite.");
       }

       public StaticVertex[] Apply(ReadOnlySpan<StaticVertex> original)
       {
           Validate();
           var vertices = original.ToArray();
           for (int i = 0; i < vertices.Length; i++)
           {
               var uv = vertices[i].Uv * Scale + Offset;
               if (!Finite(uv)) throw new ArgumentException("Texture mapping produces nonfinite UV coordinates.");
               vertices[i].Uv = uv;
           }
           return vertices;
       }

       private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
   }
   ```

2. **UV edit:** build remapped arrays and new device-local vertex buffers, stage through the fenced upload, `SubmitAndWait()`, `Device.WaitIdle()`, swap every draw's buffer and dispose the old ones in `finally` (all or nothing).

   ```csharp
   // sphinx.lib/StaticModelResources.Textures.cs L15-L53
   public void UpdateTextureMapping(TextureMapping mapping)
   {
       if (_disposed || !ReferenceEquals(Owner, Program.GetRenderer()))
           throw new InvalidOperationException("The renderer changed; remove and re-place this static.");
       mapping.Validate();
       var replacements = new List<BufferEx>();
       try
       {
           using (var upload = new AssetUploadSubmission(Owner))
           {
               foreach (var draw in _draws)
               {
                   var vertices = mapping.Apply(draw.OriginalVertices);
                   var buffer = Buffer("Sphinx remapped vertices", ByteSize.Of<StaticVertex>(vertices.Length), VkBufferUsageFlags.VertexBufferBit);
                   replacements.Add(buffer);
                   VkUtils.StageAndUploadToBuffer(upload.Staging, buffer.VkBuffer, buffer.BindOffset, vertices.AsSpan(), upload.Command);
                   var barrier = KSA.Rendering.Utils.CreateBarrier(buffer.VkBuffer, VkAccessFlags.TransferWriteBit,
                       VkAccessFlags.VertexAttributeReadBit, VK.WHOLE_SIZE, ByteSize.Zero);
                   upload.Command.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.VertexInputBit,
                       VkDependencyFlags.None, default(ReadOnlySpan<VkMemoryBarrier>),
                       new ReadOnlySpan<VkBufferMemoryBarrier>(ref barrier), default(ReadOnlySpan<VkImageMemoryBarrier>));
               }
               upload.SubmitAndWait();
           }
           // Keep every old buffer alive until all viewports have finished using it.
           // Validation/upload failure above leaves the entire visible mesh unchanged.
           Owner.Device.WaitIdle();
           for (int i = 0; i < _draws.Count; i++)
           {
               var previous = _draws[i].Vertices;
               _draws[i].Vertices = replacements[i];
               replacements[i] = previous!.Value;
           }
       }
       finally
       {
           foreach (var buffer in replacements) buffer.Dispose();
       }
   }
   ```

3. **PNG override:** decode the catalog PNG with a bounded decoder, upload it as a private bindless texture (plus an opacity map only if some pixel is not fully opaque), and make it the diffuse (and alpha) map of every primitive while normal and PBR maps still come from the GLB material recipe:

   ```csharp
   // sphinx.lib/StaticGeometry.cs L59-L69
   TextureReference Resolve(string name, TextureReference fallback) => name.Length == 0 ? fallback : assets.ResolveTexture(name);
   var diffuse = png?.Color ?? Resolve(recipe.DiffuseId, TextureReference.EmptyWhite);
   var normal = Resolve(recipe.NormalId, TextureReference.EmptyNormal);
   var pbr = Resolve(recipe.PbrId, TextureReference.EmptyWhite);
   var alpha = png != null ? png.Opacity : recipe.OpacityId.Length > 0 ? assets.ResolveTexture(recipe.OpacityId) : null;
   var material = new StaticObjectModel.PerDrawData
   {
       DiffuseTextureIndex = diffuse.BindlessHandle, NormalTextureIndex = normal.BindlessHandle,
       PbrTextureIndex = pbr.BindlessHandle, AlphaTextureIndex = alpha?.BindlessHandle ?? -1,
       EmissiveTextureIndex = -1, TfiTextureIndex = -1
   };
   ```

4. **Queue the edit** separately from transform edits. A different PNG builds a complete replacement resource before the old one is disposed; the same PNG with a different mapping calls `UpdateTextureMapping`. A failure leaves the previous mesh and material visible.

   ```csharp
   // sphinx.lib/SphinxSubmod.Editing.cs L115-L135
   private void QueueTextureEdit(SphinxEntry entry)
   {
       string? png = _editPng;
       var mapping = _editMapping;
       _pending.Enqueue(() =>
       {
           if (!_entries.Contains(entry)) return;
           if (png != entry.Png)
           {
               // Build first: invalid images or UVs retain the complete previous material/mesh.
               var replacement = new StaticModelResources(_assets, entry.MeshId, png,
                   8_000_000 - _entries.Where(e => e != entry).Sum(e => e.Model.VertexCount), mapping);
               try { entry.Model.Dispose(); }
               catch { replacement.Dispose(); throw; }
               entry.Model = replacement;
           }
           else if (mapping != entry.Mapping) entry.Model.UpdateTextureMapping(mapping);
           entry.Png = png; entry.Mapping = mapping;
           _status = $"Updated texture for #{entry.Id}.";
       });
   }
   ```

5. **Persist** the PNG catalog name, scale and offset in the save, validated before replay rebuilds the resource.

## Complete example

| File | Role |
|---|---|
| [sphinx.lib/TextureMapping.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/TextureMapping.cs) | Mapping value and application |
| [sphinx.lib/StaticModelResources.Textures.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticModelResources.Textures.cs) | UV re-upload and swap |
| [sphinx.lib/StaticGeometry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticGeometry.cs) | PNG as diffuse and cutout |
| [sphinx.lib/SphinxSubmod.Editing.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxSubmod.Editing.cs) | Queued edits |

## Variations and gotchas

- **Mapping scales the existing layout;** it cannot unwrap a mesh or fix UV islands. One mapping applies to every map of the model.
- **PNG transparency is a 0.5 cutout** drawn in the blended bucket with no prepass, not alpha blending, and never cuts collision holes.
- **`WaitIdle` on every UV edit** means a drag hitches large models.
- **Original vertices stay in managed memory** for the life of the resource, doubling the CPU-side footprint.
- **A renderer rebuild** invalidates the owner check; the entry must be removed and placed again (see [Recreate mod render targets when KSA rebuilds its renderer](../../vulkan-passes/handle-renderer-rebuilds/overview.md)).
- The vertex ABI check (32-byte stride, UV at offset 24, 24-byte `PerDrawData`) throws before any upload if a game update changes the layout.

## See also

- [Overview](overview.md)
- [Approach 1: re-point the stock bindless slot](replace-a-game-texture-at-runtime-1.md)
- [Approach 3: private material buffer behind a scoped context](replace-a-game-texture-at-runtime-3.md)
- [Live per-static UV scale and offset with shared-PNG texture overrides](../../../../mods/unscience/features/sphinx-uv-texture-overrides.md)
- [Free mod-owned Vulkan resources without device loss](../../vulkan-passes/free-vulkan-resources-safely/overview.md)
