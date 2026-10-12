---
type: Mod Feature
title: Hide individual part meshes by compacting the per-frame instance lists
description: Shared prefix and postfix pairs on PartTreeRenderData.Compose, ComposeDynamic and ComposeGlass record where each model's instances start and remove hidden parts from the appended range before the lists are uploaded, so parts stay in the vessel but are not drawn
mod: unscience
category: rendering
techniques: [harmony-prefix, harmony-postfix, reflection]
hooks:
  - harmony-prefix KSA.PartTreeRenderData.Compose
  - harmony-postfix KSA.PartTreeRenderData.Compose
  - harmony-prefix KSA.PartTreeRenderData.ComposeDynamic
  - harmony-postfix KSA.PartTreeRenderData.ComposeDynamic
  - harmony-prefix KSA.PartTreeRenderData.ComposeGlass
  - harmony-postfix KSA.PartTreeRenderData.ComposeGlass
  - reflection KSA.PartTreeRenderData._batches / _dynamicBatches / _glassBatches
  - reflection KSA.PartTreeRenderData.Batch.Model / Parts / Count (and DynamicBatch, GlassBatch)
lifecycle: [frame:render]
game_types:
  - KSA.PartTreeRenderData
  - KSA.PartTree
  - KSA.PartModel
  - KSA.PartModelDynamic
  - KSA.PartModelGlass
  - KSA.PartModelRenderer
  - KSA.Vehicle
  - KSA.Program
  - KSA.Part
tags: [rendering, part-models, instancing, harmony, reflection, visibility]
---

Several unscience features build vessels out of hundreds of real parts (Blinky's engine pixels,
It's So Shiny's light pixels) or want to see through a specific model (Kitchen Sink's capsule
window). The parts must stay in the part tree so they keep working, but their meshes should not be
drawn. `PartRenderFilter` is a small shared library that lets any number of owners register a
"hide this part" predicate, and drops matching instances from the frame's draw lists without
touching the game's cached render data.[^part-render-filter]

Mechanism in one line: for each of the three compose methods on `PartTreeRenderData`, a prefix
records the current length of every model's per-viewport instance list, and a postfix removes the
hidden slots from the range the game just appended, in place, before `PartModelRenderer` uploads
the lists.

## How it works

KSA 5482 (game revision 5456) replaced the per-module `*Module.UpdateRenderData` submissions with a
per-tree cache, `PartTreeRenderData`. Each tree keeps private batch lists, one batch per model
(`_batches` for static models, `_dynamicBatches`, `_glassBatches`), and every frame `Compose`,
`ComposeDynamic` and `ComposeGlass` append one contiguous, slot-ordered range per batch to that
model's `ViewportData.InstanceList` (and the parallel `DentInstanceList` for static and dynamic
models).[^ksa-parttreerenderdata]

The filter uses that layout:

1. `Register(harmony, owner, Func<Part,bool>)` stores a predicate under an owner key; the first
   registration installs the six patches. `RegisterStaticModel(harmony, owner, Func<PartModel,bool>)`
   adds a whole-model predicate (used for the capsule window). `Unregister` removes an owner; the last
   one uninstalls the patches. Install is all-or-nothing: if any compose method is missing, the
   partial install is rolled back and the exception propagates.
2. `ResolveBatchAccessors` builds `AccessTools.FieldRefAccess` delegates for the three private batch
   lists and for `Model`, `Parts` and `Count` on the private nested `Batch`, `DynamicBatch` and
   `GlassBatch` classes, so the hot path has no reflection.[^part-render-filter-batches]
3. Prefix (`__state` = filter active for this viewport): for each batch, remember
   `ViewportData.Get(model, viewport).InstanceList.Count`, or `-1` for empty batches.
4. Postfix: for each batch, compute how many entries were appended. Only when exactly `batch.Count`
   entries were appended (the raster path) and the dent list is aligned does it evaluate the
   predicates against `Part.FullPart` for each slot and compact the instance and dent lists with a
   `CollectionsMarshal.AsSpan` stable compaction plus one `RemoveRange`.
5. Any exception sets `_faulted`, logs once and makes the filter inert (fail open: everything renders)
   rather than breaking the render loop.

Owners in the repo: Blinky (hide pixel engines unless "render engine meshes" is on), It's So Shiny
(show a light pixel only while lit) and Kitchen Sink's capsule-glass experiment, which hides the two
stock medium-capsule window models by template id.[^blinky-patches][^shiny-patches][^capsule-glass]

## Lifecycle and timing

The compose calls run on the main thread inside `Program.Render`, in the render-resource update that
precedes command recording, for every visible viewport:[^ksa-program][^ksa-vehicle][^ksa-parttree]

| Step | Location (build 5554) |
|---|---|
| `Program.Render` (after `OnPreRender`) -> `UpdateRenderingResources(ResourceFrameIndex)` | `Program.cs` L2468, defined L4308 |
| per visible viewport, flight scene: `vehicle.UpdateRenderData(viewport, frameIndex)` for every non-`KittenEva` vehicle in frame | `Program.cs` L4355 |
| `Vehicle.UpdateRenderData` -> cull by `IsLargeEnoughToRender` -> `Parts.UpdateRenderData` | `Vehicle.cs` L3713-L3719 |
| `PartTree.UpdateRenderData` -> `RenderData.EnsureBuilt`, then **`Compose`**, **`ComposeDynamic`**, **`ComposeGlass`** | `PartTree.cs` L1158-L1171 |
| editor scene: `Editor.EditingSpace.Parts?.UpdateRenderData(...)` and unattached trees instead | `Program.cs` L4359-L4366 |
| `Universe.UpdateRenderData`, then `PartModelRenderer.UpdateRenderData(viewport2, frameIndex)` uploads the lists | `Program.cs` L4371-L4374 |

So the filter runs in `frame:render`, in the resource-update step that precedes command recording: after the GUI phase and `ImGui.Render`, after controllers
and lights, before Vulkan command recording. Predicates are called on the main thread once per part
model instance per viewport per frame, so they must be cheap (Blinky's checks a part-id prefix or a
grid registry). Because the cached `PartTreeRenderData` is never modified, flipping a predicate
takes effect on the next frame without invalidating anything, and shadow bounds and draw counts
stay consistent with the compacted lists. The filter is independent of physics: parts keep mass,
thrust, resources and colliders.

## Key code

| Path | Role |
|---|---|
| [ksa-abstractions.lib/PartRenderFilter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PartRenderFilter.cs) | Owner registry, install/uninstall, exact-signature lookup |
| [ksa-abstractions.lib/PartRenderFilter.Batches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PartRenderFilter.Batches.cs) | Field-ref accessors, prefixes/postfixes, compaction, fault handling |
| [blinky.lib/BlinkyPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/BlinkyPatches.cs) | Full-part predicate owner |
| [its-so-shiny.lib/ShinyPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny.lib/ShinyPatches.cs) | Full-part predicate owner |
| [kitchen-sink.lib/CapsuleGlassExperiment.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/CapsuleGlassExperiment.cs#L15-L43) | Static-model predicate owner |

## Snippets

Registration and the shared install, with the exact compose signature:

```csharp
// ksa-abstractions.lib/PartRenderFilter.cs L43-L48
    public static void Register(Harmony harmony, string owner, Func<Part, bool> shouldHide)
    {
        if (!IsInstalled) Install(harmony);
        Owners[owner] = shouldHide;
        _filters = Owners.Values.ToArray();
    }
```

```csharp
// ksa-abstractions.lib/PartRenderFilter.cs L118-L129
    private static void Patch(Harmony harmony, string target, string prefix, string postfix)
    {
        MethodInfo original = AccessTools.Method(typeof(PartTreeRenderData), target, new[]
        {
            typeof(double4x4).MakeByRefType(), typeof(bool), typeof(IViewport), typeof(int)
        }) ?? throw new MissingMethodException(nameof(PartTreeRenderData), target);
        MethodInfo prefixMethod = AccessTools.Method(typeof(PartRenderFilter), prefix);
        MethodInfo postfixMethod = AccessTools.Method(typeof(PartRenderFilter), postfix);
        harmony.Patch(original, prefix: new HarmonyMethod(prefixMethod), postfix: new HarmonyMethod(postfixMethod));
        Installed.Add((original, prefixMethod));
        Installed.Add((original, postfixMethod));
    }
```

The static-model postfix and the in-place compaction:

```csharp
// ksa-abstractions.lib/PartRenderFilter.Batches.cs L79-L100
    private static void ComposePostfix(PartTreeRenderData __instance, IViewport inViewport, bool __state)
    {
        if (!__state) return;
        try
        {
            IList batches = _staticBatches!(__instance);
            for (int i = 0; i < batches.Count && i < StaticStarts.Count; i++)
            {
                object batch = batches[i]!; // batch lists never hold null
                int start = StaticStarts[i];
                if (start < 0) continue;
                var data = PartModel.ViewportData.Get(_staticModel!(batch), inViewport);
                int count = _staticCount!(batch);
                if (data.DentInstanceList.Count - start != count) continue;
                if (!MarkHidden(_staticParts!(batch), count, data.InstanceList.Count - start,
                        ShouldHideStaticModel(_staticModel!(batch)))) continue;
                Compact(data.InstanceList, start, count);
                Compact(data.DentInstanceList, start, count);
            }
        }
        catch (Exception ex) { Fault(ex); }
    }
```

```csharp
// ksa-abstractions.lib/PartRenderFilter.Batches.cs L202-L212
    private static void Compact<T>(List<T> list, int start, int count)
    {
        Span<T> range = CollectionsMarshal.AsSpan(list).Slice(start, count);
        int write = 0;
        for (int read = 0; read < count; read++)
        {
            if (_hidden[read]) continue;
            range[write++] = range[read];
        }
        list.RemoveRange(start + write, count - write);
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `PartTreeRenderData.Compose` / `ComposeDynamic` / `ComposeGlass` `(ref readonly double4x4, bool, IViewport, int)` | [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1258-L1319) | Patched; append per-batch instance ranges |
| `PartTreeRenderData._batches` / `_dynamicBatches` / `_glassBatches` (private) | [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L237-L243) | Field refs to the batch lists |
| `PartTreeRenderData.Batch` / `DynamicBatch` / `GlassBatch` `.Model`, `.Parts`, `.Count` (private nested) | [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L23-L172) | Which parts own which slots |
| `PartModel.ViewportData.InstanceList` / `DentInstanceList` | [current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L356-L380) | The lists compacted in place (same pattern on `PartModelDynamic` and `PartModelGlass`) |
| `PartTree.UpdateRenderData` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1158-L1171) | Calls the three compose methods |
| `Vehicle.UpdateRenderData` / `IsLargeEnoughToRender` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3707-L3719) | Per-viewport entry point and pixel-size cull |
| `Program.UpdateRenderingResources` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4308-L4375) | Frame position: vehicles, then `PartModelRenderer.UpdateRenderData` upload |
| `Part.FullPart` | `KSA/Part.cs` L1171 (`PartParent ?? this`) | Predicates receive the full part, not a sub-part |

## Pitfalls and version notes

- One shared patch set for all owners is required: two independent compactions would each shift the
  ranges the other measured.
- Raytraced IVA is not filtered. In `Compose`, when ray tracing is active in an IVA viewport the game
  calls `model.AddInstance` per slot instead of appending a raster range; the appended-count check
  fails and the filter leaves the range alone.[^ksa-parttreerenderdata]
- Internal (IVA-only) models outside IVA and shadow-proxy models append nothing, so they are skipped
  too; see [IVA force render](./lib-iva-force-render.md) for revealing interiors.
- `KittenEva` vehicles are skipped by `UpdateRenderingResources`; their part trees are composed later,
  inside `RenderViewport` and `RenderGame` (`KittenEva.UpdateRenderData` -> `base.UpdateRenderData`,
  `Program.cs` L4698), after `PartModelRenderer.UpdateRenderData` has already uploaded the instance
  lists. The filter's patches still run there, but those instances are never drawn in stock, so the
  filter cannot hide the kitten avatar (a `KittenRenderable`, not part models) and only matters for kitten
  equipment that [Iron Man](./iron-man-equipment-render-ordering.md) submits early.[^ksa-program]
- The accessors are resolved by string names of private members; a rename makes `Register` throw on
  first use. The authors' scope file tracks these names per build.[^scope-pixel-grids] All of them exist
  unchanged in 5554.
- Before 5482 the equivalent hooks were prefixes on `PartModelModule.UpdateRenderData` and siblings;
  those methods no longer exist.

## See also

- [IVA force render](./lib-iva-force-render.md) (also patches `PartTreeRenderData.Compose`)
- [Blinky pixel patterns](./blinky-scroll-animation-and-patterns.md)
- [It's So Shiny light-part grids](./its-so-shiny-light-part-grids.md)
- [Kitchen Sink capsule glass](./kitchen-sink-capsule-glass.md)
- [Humble Arteest vehicle paint](./humble-arteest-vehicle-paint-shader-patch.md) (writes into the same cached state)
- [Mod overview](../overview.md)
- [Idempotent shared installs with a registration API](../../../howto/patching/harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-3.md) - how-to article demonstrating this feature
- [Hide part models by compacting the instance lists around PartTreeRenderData.Compose](../../../howto/rendering/meshes/hide-parts-or-subparts-from-rendering/hide-parts-or-subparts-from-rendering-1.md) - how-to article demonstrating this feature

[^part-render-filter]: ksa-abstractions.lib/PartRenderFilter.cs
[^part-render-filter-batches]: ksa-abstractions.lib/PartRenderFilter.Batches.cs
[^ksa-parttreerenderdata]: KSA.PartTreeRenderData (decompiled, build 2026.10.10.5554)
[^blinky-patches]: blinky.lib/BlinkyPatches.cs
[^shiny-patches]: its-so-shiny.lib/ShinyPatches.cs
[^capsule-glass]: kitchen-sink.lib/CapsuleGlassExperiment.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^scope-pixel-grids]: scope/pixel-grids-and-render.md (authors' render-hook analysis)
