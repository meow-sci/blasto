# Game Feature

* [Attach a prop to a kitten's hand or another bone at runtime, building the prop mesh from mod assets](overview.md) - How a mod hangs its own mesh on a kitten bone with a CharacterAvatar CosmeticAttachment, built from mod glTF and material assets, and how to place it exactly on the drawn hand per view

# How-To

* [Add a CosmeticAttachment with a static offset and build the prop mesh from the mod's glTF and material](attach-a-prop-to-a-kitten-bone-1.md) - Build a StaticMeshRenderable from mod-declared GltfFile and PbrMaterial assets, fill its material slots first, and add or update a CosmeticAttachment on a named bone by read, change and write back
* [Solve the attachment transform in UpdateSkeleton against the view's wrist](attach-a-prop-to-a-kitten-bone-2.md) - Write placed times the inverse of SocketTurn times the wrist's world transform into the CosmeticAttachment from an IAnimProcessor's UpdateSkeleton so the prop has no frame of lag in any view
