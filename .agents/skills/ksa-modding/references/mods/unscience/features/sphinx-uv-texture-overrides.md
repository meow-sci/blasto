---
type: Mod Feature
title: Live per-static UV scale and offset with shared-PNG texture overrides
description: Sphinx keeps each static's imported vertices in managed memory and re-uploads only private vertex buffers with UV times scale plus offset for repeatable edits, while a PNG override rebuilds the whole private resource first so a failed texture never replaces the working one
mod: unscience
submod: sphinx
category: rendering
techniques: [vulkan-buffer, vulkan-texture, glb-model, imgui-window]
hooks:
  - "starmap StarMapAfterGui (via unscience ISubmod.RenderContent: texture combo and UV widgets)"
  - "starmap StarMapBeforeGui (via unscience ISubmod.Update: queued texture edits)"
  - harmony-postfix KSA.StaticObjectRenderer.WriteCommandsColor (draws the remapped buffers, see render hooks)
lifecycle: [frame:draw-ui, frame:render, event:save, event:load]
game_types: [KSA.StaticObjectModel, KSA.TextureReference, KSA.Program, KSA.Rendering.Utils, Core.Renderer]
tags: [uv, textures, png, vertex-buffer, vulkan, statics, glb, live-edit]
---

Each Sphinx static can wear its embedded GLB materials or a PNG from the shared `pngs` catalog (the same
catalog Graffiti and Free Fallin use), and its texture coordinates can be tiled and shifted live per
placement: "Texture scale UV" repeats the image (2 = twice as often, 0.5 = twice as large) and "Texture
offset UV" slides it, with a reset to the imported UVs. Edits apply on the next frame, duplicates
copy the mapping independently, and a texture that fails to load leaves the previous one visible.[^sphinx-readme]

Mechanism in one line: the private render resource keeps every primitive's original
`StaticVertex[]` in managed memory, a UV edit computes `original UV × scale + offset` into fresh arrays
and uploads them into new device-local vertex buffers with a private command buffer, then swaps the
buffer handles after the GPU is idle; textures, indices, material descriptors and matrices stay as they
are. A PNG change instead builds a complete new resource and disposes the old one only after the new
one exists.[^resources-textures]

## How it works

**Mapping value** ([sphinx.lib/TextureMapping.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/TextureMapping.cs)).
`TextureMapping` is a `readonly record struct` of two `Vector2`s, so equality tests drive "pending"
detection in the UI. `Identity` is scale (1, 1), offset (0, 0). `Validate` requires finite values and
0.01 to 1000 per scale axis; `Apply` copies the source span, writes the new UVs, and rejects any
non-finite result. `StaticVertex` mirrors the game's 32-byte interleaved static-object vertex (position,
normal, UV at byte 24), which the resource constructor asserts with `Marshal.OffsetOf` before uploading
anything.[^texture-mapping][^resources]

**Initial build** ([sphinx.lib/StaticModelResources.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticModelResources.cs#L44-L126)).
`StaticGeometry.Read` converts each Pebbles host primitive into `StaticVertex[]` plus a
`StaticObjectModel.PerDrawData` record of bindless texture indices, duplicating vertices (normals
negated) and reversed triangles for double-sided materials. The resource stores those arrays as
`Draw.OriginalVertices` and uploads `mapping.Apply(source.Vertices)`, so the very first upload already
uses the entry's mapping.[^static-geometry][^resources]

**UV edit** ([sphinx.lib/StaticModelResources.Textures.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticModelResources.Textures.cs#L15-L53)):

1. Refuse if disposed or if `Program.GetRenderer()` is no longer the renderer that owns the buffers.
2. Validate, then for each draw build the remapped array and a new `BufferEx` (vertex usage, device
   local), stage it through Pebbles' `AssetUploadSubmission`, and record a transfer-write to
   vertex-attribute-read barrier.
3. `SubmitAndWait()` on the private command buffer, then `Owner.Device.WaitIdle()` so no in-flight frame
   still reads the old buffers.
4. Swap every draw's `Vertices` with its replacement; the `finally` block then disposes whatever is left
   in the replacement list, which after a successful swap is the old buffers and after a failure is the
   new ones. Either the whole mesh changes or none of it does.[^resources-textures][^upload-submission]

**PNG override** ([sphinx.lib/StaticGeometry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticGeometry.cs#L59-L70)).
`ImportedPngTexture` decodes the catalog PNG with Pebbles' bounded decoder (64 MiB file cap), uploads it
as a private bindless texture, and adds a separate opacity map with a 0.5 cutoff only if some pixel is
not fully opaque. In `StaticGeometry.Read` the PNG becomes the diffuse map and, when present, the alpha
map of every primitive; normal and PBR maps still come from the GLB material recipe. A primitive with an
alpha map is drawn in the blended bucket and skipped in the depth-normal prepass.[^imported-png][^static-geometry]

**One UV set for everything.** The stock static-object fragment shader samples diffuse, normal, PBR
and alpha with the same `inUv` and the sampler bound at set 2, binding 1. Sphinx binds its own sampler
there with `Repeat` addressing on U, V and W, so coordinates outside 0..1 tile instead of clamping.[^static-object-frag][^resources]

**Queued and isolated from transforms** ([sphinx.lib/SphinxSubmod.Editing.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxSubmod.Editing.cs#L115-L135)).
The texture combo and UV widgets edit `_editPng` / `_editMapping`; a change queues a texture edit that is
separate from the transform/collider edit queue. In the queued closure, a different PNG builds a full
replacement `StaticModelResources` (with the new mapping) before disposing the old one; same PNG with a
different mapping calls `UpdateTextureMapping`. While the edit values differ from the entry's applied
values the panel shows "Texture edit pending or failed", a Retry button and "Use current texture
settings".[^sphinx-editing]

**Persistence.** `StaticSave` carries `UvScale`, `UvOffset` and the PNG catalog name, validated through
`TextureMapping.Validate` before replay rebuilds the resource with them.[^sphinx-saves]

## Lifecycle and timing

| Step | Where | Phase |
|---|---|---|
| Widget edits, Retry, reset | `RenderContent` (StarMap `AfterGui`) | `frame:draw-ui` |
| Upload and swap (`UpdateTextureMapping`) or resource rebuild | Queued closure in `ISubmod.Update` (StarMap `BeforeGui`, or `HiddenUiFrameHook` with F2) | `frame:draw-ui` of the next frame |
| Draw with the new buffers | Render-hook postfixes on `StaticObjectRenderer` | `frame:render` |

Uploads happen on the main thread with their own command buffer and fence, before this frame's command
recording, and block until the copy and a full device idle complete. That makes the swap trivially safe
for frames in flight at the cost of a hitch on large models; the README warns about it. Because the
`Update` queue drain also waits for the vehicle and cloth solvers whenever anything is pending, a texture
edit additionally waits for the in-flight physics step even though it never touches physics.[^sphinx-readme]

## Key code

| Path | Role |
|---|---|
| [sphinx.lib/TextureMapping.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/TextureMapping.cs) | `StaticVertex` layout and the mapping value |
| [sphinx.lib/StaticModelResources.Textures.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticModelResources.Textures.cs) | Vertex-buffer-only re-upload and swap |
| [sphinx.lib/StaticModelResources.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticModelResources.cs#L19-L126) | `OriginalVertices`, Repeat sampler, initial upload with mapping |
| [sphinx.lib/StaticGeometry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticGeometry.cs) | Host mesh to native vertices; PNG as diffuse and alpha |
| [sphinx.lib/SphinxSubmod.Editing.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxSubmod.Editing.cs#L49-L57) | Pending, Retry and "use current" texture controls |
| [sphinx.lib/SphinxSubmod.Ui.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxSubmod.Ui.cs#L129-L143) | UV scale/offset widgets and reset |
| [pebbles.lib/Assets/ImportedPngTexture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/ImportedPngTexture.cs) | Owned PNG color and cutout textures |
| [sphinx.tests/TextureMappingChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.tests/TextureMappingChecks.cs) | Ordering, repeat, 200 edit/reset cycles, source isolation, vertex ABI |

## Snippets

The mapping, always applied to the original vertices:

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

Replace only the vertex buffers, all or nothing:

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
                    // ...
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

PNG change rebuilds first, UV-only change re-uploads:

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

PNG as diffuse and cutout, GLB normal and PBR kept:

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

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `StaticObjectModel.PerDrawData` | [current/decomp/KSA/StaticObjectModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectModel.cs#L28-L41) | Six bindless indices per draw (diffuse, normal, PBR, emissive, TFI, alpha) |
| `TextureReference.BindlessHandle`, `EmptyWhite`, `EmptyNormal` | [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L22-L70) | Texture slots and fallbacks |
| `KSA.Rendering.Utils.CreateBarrier(VkBuffer, ...)` | [current/decomp/KSA.Rendering/Utils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/Utils.cs#L515) | Transfer to vertex-input buffer barrier |
| `StaticObject.frag` set 2 sampler and `inUv` sampling | [current/Content/Core/Shaders/Mesh/StaticObject.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/StaticObject.frag#L60-L64) | All maps share UV0 and the per-draw sampler (L287-L291, L366) |
| `Program.GetRenderer()`, `Renderer.Device`, `Renderer.Allocator` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L557) | Owner check, buffers, `WaitIdle` |

## Pitfalls and version notes

- Mapping scales the existing layout; it cannot unwrap a mesh or fix individual UV islands, so an
  unrelated PNG may need a matching layout prepared outside the game.[^sphinx-readme]
- One mapping applies to every material and map of the model, including normal and PBR maps; there is no
  per-material mapping.
- PNG transparency is a 0.5 cutout drawn in the blended bucket with no prepass, not true alpha blending,
  and it never cuts collision holes.[^scope-statics]
- `Device.WaitIdle()` on every UV edit is deliberate and conservative. A drag issues one edit per
  frame, so large models hitch continuously while dragging.
- Original vertices stay in managed memory for the life of the resource (released on dispose), doubling
  the CPU-side footprint of each model.
- A renderer rebuild (graphics settings change) invalidates the owner check; the entry must be removed
  and placed again (or restored from a save).[^resources]
- The vertex ABI check (32-byte stride, UV at offset 24, 24-byte `PerDrawData`) throws before any upload
  if a game update changes the static-object layout.[^resources][^texture-checks]

## See also

- [Render hooks that draw private statics through the native pipelines](./sphinx-static-object-render-hooks.md)
- [Body-fixed GLB statics placement](./sphinx-glb-statics-placement.md)
- [GLB material conversion](./pebbles-glb-material-conversion.md) and [GLB import](./pebbles-glb-import.md)
- [Shared file library](./lib-shared-file-library.md)
- [Sphinx sub-mod](../submods/sphinx.md) and [mod overview](../overview.md)
- [PNG override rebuilds the mod mesh's private resource; UV scale and offset re-uploads the vertex buffer](../../../howto/rendering/textures-and-decals/replace-a-game-texture-at-runtime/replace-a-game-texture-at-runtime-2.md) - how-to article demonstrating this feature
- [Publish new GPU resources after a fence, retire old ones after WaitIdle](../../../howto/rendering/vulkan-passes/free-vulkan-resources-safely/free-vulkan-resources-safely-3.md) - how-to article demonstrating this feature

[^sphinx-readme]: sphinx/README.md
[^resources-textures]: sphinx.lib/StaticModelResources.Textures.cs
[^texture-mapping]: sphinx.lib/TextureMapping.cs
[^resources]: sphinx.lib/StaticModelResources.cs
[^static-geometry]: sphinx.lib/StaticGeometry.cs
[^upload-submission]: pebbles.lib/Assets/AssetUploadSubmission.cs
[^imported-png]: pebbles.lib/Assets/ImportedPngTexture.cs
[^static-object-frag]: StaticObject.frag (game shader, build 2026.10.10.5554)
[^sphinx-editing]: sphinx.lib/SphinxSubmod.Editing.cs
[^sphinx-saves]: sphinx.lib/SphinxSubmod.Saves.cs
[^scope-statics]: scope/statics.md
[^texture-checks]: sphinx.tests/TextureMappingChecks.cs
