# Game Feature

* [Draw your own meshes through the engine's mesh renderers](overview.md) - Hand the game your meshes as StaticMeshRenderables, extra part-model instances or private buffers inside the static-object pipeline so lighting, shadows and culling come for free

# How-To

* [Add extra part-model instances at any matrix, per viewport](draw-mod-meshes-with-the-engine-renderers-3.md) - Draw shells, missiles or props as additional instances of the engine's own PartModel meshes from a prefix on PartModelRenderer.UpdateRenderData, and keep a launcher drawn past the pixel cull
* [Clone interleaved part meshes into private SimpleVkMeshes for another renderer](draw-mod-meshes-with-the-engine-renderers-4.md) - Reuse a part or glTF mesh in a renderer that needs standalone per-attribute vertex streams by sharing its retained HostPrimitives and uploading a private SimpleVkMesh through MeshReference.Bind
* [Draw private vertex, index and material buffers inside the static-object pipeline](draw-mod-meshes-with-the-engine-renderers-2.md) - Record DrawIndexed calls for your own GPU buffers from postfixes on StaticObjectRenderer so the mesh uses the game's PBR, shadow-receiving, light-aware static-object shader state
* [Submit StaticMeshRenderables from a ClearBuckets postfix for every viewport](draw-mod-meshes-with-the-engine-renderers-1.md) - Draw a lit, shadowed glTF mesh with the engine's own StaticMeshRenderable by submitting it from a postfix on SuperMeshRenderSystem.ClearBuckets, once per rendered viewport
