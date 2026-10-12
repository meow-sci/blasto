# Game Feature

* [Recolour vessel parts and kittens with a patched part shader](overview.md) - Tint, multiply or replace the colour of part instances, part types or whole vessels by packing colour into per-instance state bits and compiling a modified fragment shader, plus coloured emissive glow per subpart and engine heat glow overrides

# How-To

* [7:7:7 colour in the unused StateBitFlag high bits plus a patched MeshIndirect.frag](paint-vessel-parts-with-custom-colours-1.md) - Pack a part colour into bits 11 to 31 of the per-instance state flags and compile a copy of the part fragment shader with a decode snippet, with Multiply, Tint and Replace modes
* [ADD_EMISSIVE_COLOR state bit and instance colour in a postfix on WriteState](paint-vessel-parts-with-custom-colours-3.md) - Give a subpart a coloured emissive glow by setting the ADD_EMISSIVE_COLOR bit and the instance emissive colour from a postfix on PartTreeRenderData.WriteState
* [Prefix on PartModelDynamic.AddInstance rewrites Temperature and TfiThickness](paint-vessel-parts-with-custom-colours-4.md) - Override engine heat glow by rewriting the Temperature and TfiThickness fields of the instance data in a prefix on the private PartModelDynamic.AddInstance sink
* [Shader transform at compile time with packed colours and runtime masters](paint-vessel-parts-with-custom-colours-2.md) - Apply the same shader and state-bit technique with global, per-template, per-vessel or per-instance scope, restore stock rendering when disabled, and clone materials for EVA suit, fur and visor
