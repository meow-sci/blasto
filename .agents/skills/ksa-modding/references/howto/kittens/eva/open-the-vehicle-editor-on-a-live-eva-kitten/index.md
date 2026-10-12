# Game Feature

* [Open the stock vehicle editor on a live EVA kitten, show it upright and keep its attachment nodes](overview.md) - How a mod opens the stock vehicle editor on a live EVA kitten by setting Program.EditorFlag at the physics handoff, shows the kitten upright, and gives it attachment nodes that persist through saves

# How-To

* [Draw the avatar from a ClearBuckets postfix and re-base the editor camera upright](open-the-vehicle-editor-on-a-live-eva-kitten-2.md) - Submit the kitten right after RenderEditor clears the buckets, pass the assembly-to-ego matrix unmodified, and rotate only the editor camera basis and scroll pan so the kitten stands upright
* [Give one kitten private attachment nodes that survive saves, blueprints and copies](open-the-vehicle-editor-on-a-live-eva-kitten-3.md) - Append owned Part.Connector nodes after the stock connectors, encode them as a versioned marker in the serialized PartInstance.Id and rebuild them in a Part constructor postfix before connection indices resolve
* [Open the editor on a live kitten with Program.EditorFlag at the handoff and root guards on every editor entry point](open-the-vehicle-editor-on-a-live-eva-kitten-1.md) - Set Program.EditorFlag from the PrepareFrame handoff so the stock existing-vehicle editor edits the controlled KittenEva in place, then guard the body root, new-vehicle, duplicate and blueprint paths so only the configured kitten is affected
