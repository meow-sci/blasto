# Game Feature

* [Tint kitten fur, body and eyes, and hide the visor, helmet or MMU](overview.md) - How mods recolour kittens by changing AlbedoColor in the shared GPU material buffer or in per-kitten material clones, and hide the visor, helmet or MMU with a transpiler or the attachment flags

# How-To

* [Bind mod-owned material clones to the EVA renderables and restore them conditionally](tint-and-restyle-kitten-appearance-3.md) - Create mod-named material clones, write their handles into the protected MaterialIndices arrays reached through KittenEva._renderable and KittenRenderable._characterAvatar, and restore only slots that still hold your handle
* [Clone the materials per kitten, swap the MaterialIndices handles and stay inside the 512-slot pool](tint-and-restyle-kitten-appearance-2.md) - Create new MaterialData records through GpuObjectSystem.CreateObject, overwrite the protected MaterialIndices arrays of one kitten, and budget, reserve and free slots because the pool is fixed
* [Hide the helmet, visor and MMU per avatar by clearing Helmet.Enabled and Mmu.Enabled](tint-and-restyle-kitten-appearance-5.md) - Clear the public Enabled flags on CharacterAvatar.Attachments.Helmet and Mmu, remember the old values, and restore them on let-go, with no Harmony patch
* [Recolour every kitten with one staged float4 write into the shared material buffer](tint-and-restyle-kitten-appearance-1.md) - Write AlbedoColor into GpuMaterialSystem.BigBuffer at handle times sizeof(MaterialData) plus 16 through a staging copy, track originals by asset identity, and restore only handles you still own
* [Transpile the single VisorMesh.Draw call into a gated forwarder](tint-and-restyle-kitten-appearance-4.md) - Match ldfld VisorMesh, ldloc view, call StaticMeshRenderable.Draw inside KittenRenderable.UpdateRenderData and redirect it to a forwarder that skips the draw while a flag is set
