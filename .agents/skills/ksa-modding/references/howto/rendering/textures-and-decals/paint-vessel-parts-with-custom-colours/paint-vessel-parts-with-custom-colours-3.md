---
type: How-To
title: ADD_EMISSIVE_COLOR state bit and instance colour in a postfix on WriteState
description: Give a subpart a coloured emissive glow by setting the ADD_EMISSIVE_COLOR bit and the instance emissive colour from a postfix on PartTreeRenderData.WriteState
category: rendering
group: textures-and-decals
topic: paint-vessel-parts-with-custom-colours
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/coloured-emissive-lenses]
techniques: [harmony-postfix, reflection]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-postfix KSA.PartTreeRenderData.WriteState
  - reflection KSA.PartTreeRenderData.Batch.StateBitFlags
  - reflection KSA.PartTreeRenderData.Batch.EmissiveColors
lifecycle: [startup:all-mods-loaded, frame:render]
game_types: [KSA.PartTreeRenderData, KSA.ShaderReference, RenderCore.ShaderModuleUtils, KSA.PartModelRenderer]
tags: [emissive, lens, writestate, state-bit, glb-split, colour]
---

Outcome: lenses that glow in a colour of your choosing (red tail lights, amber markers) while neighbouring headlamp lenses stay white, and all of them go dark with the lamp switch. KSA's part shader can only add the emissive texture as white, or add one colour that comes with the drawn **instance**; KSA sets that colour only for a battery's status light. The mod sets it per subpart with a postfix on the private
`PartTreeRenderData.WriteState`, and cuts each coloured lens out of the body mesh into a subpart of its own so it is a separate instance. This is the emissive-glow sub-step of part colouring. Demonstrated by KSACars ([Coloured emissive lenses per subpart](../../../../mods/ksacars/features/coloured-emissive-lenses.md)). Prerequisites: StarMap, Harmony.

## Background: how the game does it

`MeshIndirect.frag` samples the material's emissive texture and reads only its red channel; if that is non-zero and state bit 6 (`NO_EMISSIVE`, 64) is clear it adds light: the sampled value as grey, or, when state bit 7 (`ADD_EMISSIVE_COLOR`, 128) is set, the instance's packed `EmissiveColor` at full strength:

```glsl
// current/Content/Core/Shaders/Mesh/MeshIndirect.frag L271-L288
#ifdef ENABLE_EMISSIVE
if (emissive && drawData.emissiveTextureIndex >= 0)
{
    float sampledEmissive = texture(sampler2D(globalTextures[drawData.emissiveTextureIndex], textureSampler), inUv).x;
    if (sampledEmissive.x != 0.0)
    {
        if (addEmissiveColor) {
            vec3 unpacked = unpackRGB(inEmissiveColor);
            unpacked = gammaToLinear(unpacked * EMISSIVE_MULTIPLIER);
            lightColor += unpacked;
        }
        else
        {
            vec3 emissive = gammaToLinear(vec3(sampledEmissive) * EMISSIVE_MULTIPLIER);
            lightColor += emissive;
        }
    }
}
```

So the emissive texture is a mask and a colour belongs to a whole instance, never to a texel. `PartTreeRenderData` keeps per batch of identical models arrays `StateBitFlags[]` and `EmissiveColors[]` indexed by slot [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L33-L35); `WriteState(Batch, int slot, Part part)` fills a slot (`NO_EMISSIVE` when `Part.IsLightSwitchedOff()`; `ADD_EMISSIVE_COLOR` plus a colour for a status-light battery).

## Lifecycle and timing

`WriteState` runs inside `EnsureBuilt` [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L444), which `PartTree.UpdateRenderData` calls once per tree per renderer frame, from `Program.Render` -> `UpdateRenderingResources` on the main thread before command recording: `frame:render`, not `frame:pre-render`. `EnsureBuilt` calls `WriteState` for every slot when membership is rebuilt or all states are dirty, and for just the dirty parts otherwise. The arrays persist between frames, so a colour written once stays until the slot is rewritten, and every rewrite
passes through the postfix again. The postfix runs while the vehicle step is in flight; it must not throw. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Install at `[StarMapAllModsLoaded]`:** find the private method with `AccessTools.Method(typeof(PartTreeRenderData), "WriteState")`, take the first parameter's private nested `Batch` type, find `StateBitFlags` (`int[]`) and `EmissiveColors` (`uint[]`) on it, and if anything is missing log "KSA's part render state has moved" and do not patch (the lenses then glow white and nothing else breaks).

   ```csharp
   // src/KSACars/Ksa/LensColourHook.cs L36-L51
   try
   {
       MethodInfo? target = AccessTools.Method(typeof(PartTreeRenderData), "WriteState");
       Type? batch = target?.GetParameters() is [{ } first, ..] ? first.ParameterType : null;
       _flags = batch is null ? null : AccessTools.Field(batch, "StateBitFlags");
       _colours = batch is null ? null : AccessTools.Field(batch, "EmissiveColors");
       if (target is null || _flags?.FieldType != typeof(int[]) || _colours?.FieldType != typeof(uint[]))
       {
           Log.Warn("KSA's part render state has moved; the cars' coloured lenses glow white");
           return;
       }

       MethodInfo postfix = typeof(LensColourHook).GetMethod(nameof(AfterWriteState),
                                                             BindingFlags.NonPublic | BindingFlags.Static)!;
       _harmony = new Harmony(HarmonyId);
       _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
   ```

2. **Postfix** `AfterWriteState(object inBatch, int inSlot, Part inPart)` (the batch bound as `object` because its type is not accessible): ask whether the part belongs to a car and its `Id` ends in a registered lens suffix, then OR `1 << 7` into the slot's flags and write the colour, bounds-checked against both arrays. Never clear `NO_EMISSIVE`, so a switched-off lens stays dark.

   ```csharp
   // src/KSACars/Ksa/LensColourHook.cs L75-L86
   // Inside the engine's render-data pass: nothing here may throw.
   private static void AfterWriteState(object inBatch, int inSlot, Part inPart)
   {
       try
       {
           if (Buggies.LensColour(inPart) is not { } rgb) return;
           if (_flags!.GetValue(inBatch) is not int[] flags || _colours!.GetValue(inBatch) is not uint[] colours) return;
           if ((uint)inSlot >= (uint)flags.Length || (uint)inSlot >= (uint)colours.Length) return;

           flags[inSlot] |= ColouredEmissive;
           colours[inSlot] = rgb;
       }
   ```

3. **Make the lens its own subpart (art side):** read the lens UV polygons out of the source model, paint them white on black into the emissive mask PNG, move the body triangles whose UV centroid falls inside a `tail` or `amber` polygon into new meshes in the same `.glb` (sharing the atlas, idempotent), and declare them as `SubPart`s in the asset XML with the body's `PbrMaterial`. Nothing moves when the triangles are split out, so nothing ends up coplanar with the body.

   ```
   // tools/model/split-lenses.py L80-L94
   moved = 0
   for source, target in ((body, name), (body + "_VM", name + "_VM")):
       attributes, triangles = meshes[source]
       centroids = attributes["TEXCOORD_0"][triangles].mean(axis=1)
       # glTF's V runs down the image and Blender's up.
       centroids[:, 1] = 1.0 - centroids[:, 1]
       taken = np.zeros(len(triangles), dtype=bool)
       for polygon in polygons:
           taken |= inside(centroids, polygon)
       if not taken.any():
           continue
       used, remapped = np.unique(triangles[taken], return_inverse=True)
       meshes[target] = ({k: v[used] for k, v in attributes.items()}, remapped.reshape(-1, 3))
       meshes[source] = (attributes, triangles[~taken])
       if target not in order:
   ```

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/LensColourHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/LensColourHook.cs) | Install and postfix |
| [tools/model/split-lenses.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/model/split-lenses.py) | GLB lens split |
| [tools/model/lens-emissive.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/model/lens-emissive.py) | Emissive mask |

## Variations and gotchas

- **A private target cannot be pinned** by a never-called signature reference; the hook validates the method and both field types at install and switches itself off with a warning.
- **Only the static batches:** dynamic batches (`PartModelDynamic`) and glass batches have their own state writers that the postfix does not touch.
- **Re-split after every export:** a Blender export writes the lens triangles back into the body mesh.
- **The colour replaces, it does not tint:** with bit 7 set any texel whose red channel is non-zero gets the full instance colour, so the mask's soft edges become hard edges.
- **A light is not a substitute:** a point or spot light at the lens lights the bodywork around it as a blob.

## See also

- [Overview](overview.md)
- [Approach 1: 7:7:7 colour and the patched fragment shader](paint-vessel-parts-with-custom-colours-1.md)
- [Approach 4: prefix on PartModelDynamic.AddInstance](paint-vessel-parts-with-custom-colours-4.md)
- [Coloured emissive lenses per subpart](../../../../mods/ksacars/features/coloured-emissive-lenses.md)
