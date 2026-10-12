---
type: Mod Feature
title: Coloured emissive lenses per subpart
description: Make a tail lens glow red and a marker amber by setting KSA's add-emissive-colour state bit and colour per drawn subpart from a Harmony postfix on the private PartTreeRenderData.WriteState, with the lenses cut out of the body mesh as their own subparts
mod: ksacars
category: rendering
techniques: [harmony-postfix, reflection, glb-model, xml-asset, glsl-shader]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-postfix KSA.PartTreeRenderData.WriteState
  - reflection KSA.PartTreeRenderData.Batch.StateBitFlags
  - reflection KSA.PartTreeRenderData.Batch.EmissiveColors
lifecycle: [startup:all-mods-loaded, frame:render]
game_types: [KSA.PartTreeRenderData, KSA.PartTree, KSA.Part, KSA.PartModel, KSA.Battery]
tags: [rendering, emissive, part-shader, harmony, postfix, private-method, glb, subpart, python-tooling]
---

The Eldorado's tail lenses glow red and its side markers amber, while the headlamp lenses beside
them stay white, and all of them go dark with the lamp switch. KSA's part shader can only add the
emissive texture as white, or add one colour that comes with the drawn *instance*; KSA sets that
colour only for a battery's status light. KSACars sets it per subpart with a Harmony postfix on the
private `PartTreeRenderData.WriteState`, and cuts each coloured lens out of the body mesh into a
subpart of its own so it is a separate instance.[^ksacars-lenscolourhook][^ksacars-claude]

## How it works

**The shader contract.** `MeshIndirect.frag` samples the material's emissive texture and reads only
its red channel. If that is non-zero and state bit 6 (`NO_EMISSIVE`, 64) is clear, it adds light:
the sampled value as grey, or, when state bit 7 (`ADD_EMISSIVE_COLOR`, 128) is set, the instance's
packed `EmissiveColor` at full strength.[^ksa-meshindirect-frag] So the emissive texture is a mask
and a colour belongs to a whole instance, never to a texel.

**Who sets the bits.** `PartTreeRenderData` keeps, per batch of identical `PartModel`s, arrays
`StateBitFlags[]` and `EmissiveColors[]` indexed by slot. `WriteState(Batch, int slot, Part part)`
fills a slot from the full part's dynamic state (`NO_EMISSIVE` when `Part.IsLightSwitchedOff()`;
`ADD_EMISSIVE_COLOR` plus a colour when the part has a status-light battery) and from the part's
selection and highlight flags.[^ksa-parttreerenderdata] `ToPerInstanceData` later copies both
values into each drawn instance.

**The patch.** `LensColourHook.Install` (from `[StarMapAllModsLoaded]`):[^ksacars-lenscolourhook]

1. `AccessTools.Method(typeof(PartTreeRenderData), "WriteState")` finds the private method.
2. The first parameter's type is the private nested `Batch`; `AccessTools.Field` finds
   `StateBitFlags` and `EmissiveColors` on it.
3. If the method is missing or the fields are not `int[]` and `uint[]`, it logs
   "KSA's part render state has moved; the cars' coloured lenses glow white" and does not patch.
4. Otherwise it adds a postfix `AfterWriteState(object inBatch, int inSlot, Part inPart)`, taking
   the batch as `object` because its type is not accessible.

The postfix asks `Buggies.LensColour(inPart)`: the part must belong to a car (`part.FullPart` is the
car's part) and its `Id` must end in `SubpartPrefix + suffix` for one of
`BuggyProfile.ColouredLenses`, which for the Eldorado is `("TailLens", 0xFF1408)` and
`("MarkerLens", 0xFF8C14)`.[^ksacars-buggies][^ksacars-profile] It then ORs `1 << 7` into the slot's
flags and writes the colour, bounds-checked against both arrays. It never clears `NO_EMISSIVE`, so a
switched-off lens stays dark.

**Making the lens its own subpart (art side).**

| Step | Tool | What it does |
|---|---|---|
| Lens faces | `tools/model/lenses/<Car>.json` | UV polygons of every lens face, tagged `head`, `tail` or `amber`, read out of the bake-source `.blend` (Eldorado: 116 head, 24 tail, 12 amber; Buggy: 52 head) |
| Emissive mask | `tools/model/lens-emissive.py` | Paints every lens polygon white on black into a 2048 px emissive PNG, drawn at 2x and downsampled for antialiased edges |
| Split | `tools/model/split-lenses.py` | Moves body triangles whose UV centroid falls inside a `tail` or `amber` polygon into new meshes (and their `_VM` twins) in the same `.glb`, in place, sharing the body's atlas; idempotent |
| Declare | `KSACarsAssets.xml` | `KSACars_EldoTailLens_SubPart` and `KSACars_EldoMarkerLens_SubPart` with the body's `PbrMaterial`, instanced on the part as `KSACars_Eldo_TailLens` and `KSACars_Eldo_MarkerLens` |

Nothing moves when the triangles are split out, so nothing ends up coplanar with the body.
[^ksacars-split-lenses][^ksacars-lens-emissive][^ksacars-lenses-eldorado][^ksacars-lenses-buggy][^ksacars-assets]

## Lifecycle and timing

`WriteState` runs inside `PartTreeRenderData.EnsureBuilt`, which `PartTree.UpdateRenderData` calls
at its start, once per tree per renderer frame (it returns early when `_builtFrame` already equals
the frame count).[^ksa-parttree][^ksa-parttreerenderdata] `EnsureBuilt` calls `WriteState` for every
slot when membership is rebuilt (`RebuildAll`) or all states are dirty (`RewriteStates`), and for
just the dirty parts otherwise (`RewriteDirtyPartStates`). The arrays persist between frames, so a
colour written once stays until the slot is rewritten, and every rewrite passes through the
postfix again.

`PartTree.UpdateRenderData` is called from `Program.Render` -> `UpdateRenderingResources`, per
visible viewport and vehicle in frame, on the main thread, before command recording (the same pass
described in [headlamps as spotlights](./headlights-as-spotlights.md)). That is `frame:render`, not
`frame:pre-render`: `Program.OnPreRender` (exhaust, particles, swapchain acquire) has already returned
when `Render` calls `UpdateRenderingResources`, and command recording (`RenderGame`) follows it.[^ksa-program]
The postfix is therefore main-thread render-data work that runs while the vehicle step is in flight:
it must not throw, and it only touches arrays KSA is about to upload.

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/LensColourHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/LensColourHook.cs) | Finds the private target and fields, self-disables if they moved, sets bit 128 and the colour |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L297-L311) | `LensColour`: which subpart of which car glows which colour |
| [src/KSACars/Sim/BuggyProfile.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/BuggyProfile.cs#L82-L86) | `ColouredLenses` (suffix, 0xRRGGBB); Eldorado values at L229 |
| [src/KSACars/KSACarsAssets.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsAssets.xml#L386-L407) | The two lens subparts; instanced at L463-L464; emissive texture at L346 |
| [tools/model/split-lenses.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/model/split-lenses.py) | Cuts coloured lenses out of the body mesh by UV |
| [tools/model/lens-emissive.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/model/lens-emissive.py) | Paints the emissive mask from the same polygons |
| [tools/model/lenses/Eldorado.json](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/model/lenses/Eldorado.json) | Lens UV polygons and kinds for the Eldorado |
| [tools/model/README.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/model/README.md) | Tool table: run `split-lenses.py` again after every Blender export |

## Snippets

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

```glsl
// current/Content/Core/Shaders/Mesh/MeshIndirect.frag L271-L288 (KSA's part shader)
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

```python
# tools/model/split-lenses.py L80-L94
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

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `PartTreeRenderData.WriteState(Batch, int, Part)` (private) | [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1210-L1255) | Patch target: writes a slot's flags and emissive colour |
| `PartTreeRenderData.Batch.StateBitFlags` / `EmissiveColors` (private nested type) | [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L33-L35) | Arrays the postfix writes by reflection |
| `NO_EMISSIVE = 64`, `ADD_EMISSIVE_COLOR = 128` | [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L206-L212) | Shader state bits |
| `ComputeOwnerDynamicState` | [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L623-L648) | KSA's own use: light switch off and battery status colour |
| `EnsureBuilt`, `RebuildAll`, `RewriteStates`, `RewriteDirtyPartStates` | [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L444-L500) | When `WriteState` is called |
| `ToPerInstanceData` | [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1324-L1333) | Copies flags and colour into `PartModel.PerInstanceData` |
| `PartTree.UpdateRenderData` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1158-L1163) | Calls `RenderData.EnsureBuilt` once per frame |
| `Part.FullPart`, `Part.Id` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1171) | Matching a subpart to its car |
| `MeshIndirect.frag` emissive block | [current/Content/Core/Shaders/Mesh/MeshIndirect.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/MeshIndirect.frag#L271-L321) | Red channel as mask; bit 6 disables, bit 7 adds the instance colour |

## Pitfalls and version notes

- **A private target cannot be pinned.** The other public patches in the mod carry a
  never-called `PinTheSignature` so a KSA signature change breaks the build; this one cannot, so it
  validates the method and both field types at install and switches itself off with a warning.
  The lenses then glow white and nothing else breaks.[^ksacars-lenscolourhook]
- **Only the static batches.** `WriteState` writes the ordinary `Batch` slots. Dynamic batches
  (`PartModelDynamic`) and glass batches have their own state writers that the postfix does not
  touch.[^ksa-parttreerenderdata]
- **Re-split after every export.** A Blender export writes the lens triangles back into the body
  mesh; `split-lenses.py` has to be run again, and `lenses/<Car>.json` re-read if the car is
  unwrapped again.[^ksacars-model-readme][^ksacars-lens-emissive]
- **The colour replaces, it does not tint.** With bit 7 set, any texel whose red channel is
  non-zero gets the full instance colour, so the mask's soft edges become hard ones on coloured
  lenses.[^ksa-meshindirect-frag]
- **A light is not a substitute.** A point or spot light at the lens lights the bodywork around it
  as a blob, which is why the glow is done in the shader.[^ksacars-claude]

## See also

- [Headlamps and tail lamps as KSA spotlights](./headlights-as-spotlights.md)
- [Car part assets: GLB, PBR atlas and subparts](./car-part-assets-glb-pbr.md)
- [Art and audio pipeline tooling](./art-and-audio-pipeline-tooling.md)
- [KSACars overview](../overview.md)
- [ADD_EMISSIVE_COLOR state bit and instance colour in a postfix on WriteState](../../../howto/rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/paint-vessel-parts-with-custom-colours-3.md) - how-to article demonstrating this feature

[^ksacars-lenscolourhook]: src/KSACars/Ksa/LensColourHook.cs
[^ksacars-buggies]: src/KSACars/Ksa/Buggies.cs
[^ksacars-profile]: src/KSACars/Sim/BuggyProfile.cs
[^ksacars-assets]: src/KSACars/KSACarsAssets.xml
[^ksacars-split-lenses]: tools/model/split-lenses.py
[^ksacars-lens-emissive]: tools/model/lens-emissive.py
[^ksacars-lenses-eldorado]: tools/model/lenses/Eldorado.json
[^ksacars-lenses-buggy]: tools/model/lenses/Buggy.json
[^ksacars-model-readme]: tools/model/README.md
[^ksacars-claude]: CLAUDE.md, "The cars"
[^ksa-parttreerenderdata]: KSA.PartTreeRenderData (decompiled)
[^ksa-parttree]: KSA.PartTree (decompiled)
[^ksa-meshindirect-frag]: Core/Shaders/Mesh/MeshIndirect.frag
[^ksa-program]: KSA.Program (decompiled)
