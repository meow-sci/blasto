---
type: How-To
title: Hide part models by compacting the instance lists around PartTreeRenderData.Compose
description: Drop selected parts or whole models from the frame's draw lists with a shared prefix/postfix pair on the three PartTreeRenderData compose methods, leaving the parts alive in the vessel
category: rendering
group: meshes
topic: hide-parts-or-subparts-from-rendering
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony, reflection]
demonstrated_by: [mods/unscience/features/lib-part-render-filter, mods/unscience/features/kitchen-sink-capsule-glass]
techniques: [harmony-prefix, harmony-postfix, reflection]
hooks:
  - harmony-prefix KSA.PartTreeRenderData.Compose
  - harmony-postfix KSA.PartTreeRenderData.Compose
  - harmony-prefix KSA.PartTreeRenderData.ComposeDynamic
  - harmony-postfix KSA.PartTreeRenderData.ComposeDynamic
  - harmony-prefix KSA.PartTreeRenderData.ComposeGlass
  - harmony-postfix KSA.PartTreeRenderData.ComposeGlass
  - reflection KSA.PartTreeRenderData._batches
lifecycle: [frame:render, frame:draw-ui]
game_types:
  - KSA.PartTreeRenderData
  - KSA.PartTree
  - KSA.PartModel
  - KSA.PartModelDynamic
  - KSA.PartModelGlass
  - KSA.PartModelRenderer
  - KSA.Vehicle
  - KSA.Part
tags: [parts, hide, render-filter, instance-list, compose, capsule-window]
---
Outcome: parts that stay in the vessel, keep mass, thrust, resources and colliders, but whose meshes are not drawn: hundreds of "pixel" parts that only exist as logic, a single window model, a part that should appear only while lit. Several mods can register "hide this" predicates on one shared patch set, and a predicate can be flipped at any frame.

Demonstrated by unscience's part render filter and its Kitchen Sink capsule-glass experiment ([Hide individual part meshes by compacting the per-frame instance lists](../../../../mods/unscience/features/lib-part-render-filter.md), [See into the stock capsule by hiding its two opaque window models](../../../../mods/unscience/features/kitchen-sink-capsule-glass.md)). Prerequisites: StarMap, Harmony, reflection into private batch fields. Needs KSA 2026.8 or later render data (the `PartTreeRenderData` cache).

## Background: how the game does it

Since the per-tree cache, each `PartTree` owns a `PartTreeRenderData` with private batch lists, one batch per model: `_batches` (static models), `_dynamicBatches`, `_glassBatches` ([current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L237-L243)). Every frame `PartTree.UpdateRenderData` calls `Compose`, `ComposeDynamic` and `ComposeGlass`, which append one contiguous, slot-ordered range per batch to that model's per-viewport `InstanceList` (and the parallel `DentInstanceList` for static and dynamic models):

```csharp
// current/decomp/KSA/PartTree.cs L1158-L1171
public void UpdateRenderData(ref readonly double4x4 matrixAsmb2Ego, bool isEditedVehicle, IViewport viewport, int frameIndex)
{
	EnsureDerived(DerivedData.NozzlePlumeFx | DerivedData.AttachedInternals);
	Profiler.MainThread.Begin(Profiler.Tag.PartsModelBuild);
	RenderData.EnsureBuilt(this, Program.GetRenderer().FrameCount);
	Profiler.MainThread.End();
	Profiler.MainThread.Begin(Profiler.Tag.PartsModel);
	RenderData.Compose(in matrixAsmb2Ego, isEditedVehicle, viewport, frameIndex);
	Profiler.MainThread.End();
	Profiler.MainThread.Begin(Profiler.Tag.PartsModelDynamic);
	RenderData.ComposeDynamic(in matrixAsmb2Ego, isEditedVehicle, viewport, frameIndex);
	Profiler.MainThread.End();
	Profiler.MainThread.Begin(Profiler.Tag.PartsModelGlass);
	RenderData.ComposeGlass(in matrixAsmb2Ego, isEditedVehicle, viewport, frameIndex);
```

```csharp
// current/decomp/KSA/PartTreeRenderData.cs L1300-L1312
else if (model.Template.RayTracing != PartModelModule.RaytracingMode.ShadowProxy && (!model.Template.Internal || flag))
{
	PartModel.ViewportData viewportData = PartModel.ViewportData.Get(model, inViewport);
	int count = viewportData.InstanceList.Count;
	CollectionsMarshal.SetCount(viewportData.InstanceList, count + batch.Count);
	CollectionsMarshal.SetCount(viewportData.DentInstanceList, count + batch.Count);
	Span<PartModel.PerInstanceData> span2 = CollectionsMarshal.AsSpan(viewportData.InstanceList).Slice(count);
	Span<PerInstanceDent> span3 = CollectionsMarshal.AsSpan(viewportData.DentInstanceList).Slice(count);
	for (int j = 0; j < batch.Count; j++)
	{
		span2[j] = ToPerInstanceData(batch, j, in inMatrixAsmb2Ego, num);
	}
	span3.Fill(PerInstanceDent.Undented);
```

`PartModelRenderer.UpdateRenderData` then uploads the lists. Cached render data is never modified by the filter, so predicate changes take effect on the next frame with no invalidation, and shadow bounds and draw counts stay consistent with the compacted lists.

## Lifecycle and timing

The compose calls run on the main thread inside `Program.Render`, in the render-resource update that precedes command recording, for every visible viewport:

| Step | Location (build 2026.10.10.5554) |
|---|---|
| `Program.Render` then `UpdateRenderingResources(ResourceFrameIndex)` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4308) (call at L2468) |
| per visible viewport, flight: `vehicle.UpdateRenderData(viewport, frameIndex)` for every non-`KittenEva` vehicle | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4347-L4357) |
| `Vehicle.UpdateRenderData`, pixel cull, `Parts.UpdateRenderData` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3713-L3726) |
| `PartTree.UpdateRenderData` then `Compose`, `ComposeDynamic`, `ComposeGlass` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1158-L1171) |
| editor scene: `Editor.EditingSpace.Parts?.UpdateRenderData(...)` instead | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4359-L4366) |
| `PartModelRenderer.UpdateRenderData` uploads the lists | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4374) |

That is `frame:render` (row 22 of [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)). Predicates are called on the main thread once per model instance per viewport per frame, so they must be cheap. Toggling a predicate from an ImGui handler (`frame:draw-ui`) takes effect the same frame, because the draw lists are built later in `Render`.

## Steps

1. **Resolve the private batch members** once, as `AccessTools.FieldRefAccess` delegates so the hot path has no reflection: the three batch lists on `PartTreeRenderData`, and `Model`, `Parts` and `Count` on the private nested `Batch`, `DynamicBatch` and `GlassBatch` classes:

   ```csharp
// ksa-abstractions.lib/PartRenderFilter.Batches.cs L35-L53
private static void ResolveBatchAccessors()
{
    Type batch = Inner("Batch"), dynamic = Inner("DynamicBatch"), glass = Inner("GlassBatch");
    _staticBatches = AccessTools.FieldRefAccess<PartTreeRenderData, IList>("_batches");
    _dynamicBatches = AccessTools.FieldRefAccess<PartTreeRenderData, IList>("_dynamicBatches");
    _glassBatches = AccessTools.FieldRefAccess<PartTreeRenderData, IList>("_glassBatches");
    _staticModel = AccessTools.FieldRefAccess<PartModel>(batch, "Model");
    _dynamicModel = AccessTools.FieldRefAccess<PartModelDynamic>(dynamic, "Model");
    _glassModel = AccessTools.FieldRefAccess<PartModelGlass>(glass, "Model");
    _staticParts = AccessTools.FieldRefAccess<Part[]>(batch, "Parts");
    _dynamicParts = AccessTools.FieldRefAccess<Part[]>(dynamic, "Parts");
    _glassParts = AccessTools.FieldRefAccess<Part[]>(glass, "Parts");
    _staticCount = AccessTools.FieldRefAccess<int>(batch, "Count");
    _dynamicCount = AccessTools.FieldRefAccess<int>(dynamic, "Count");
    _glassCount = AccessTools.FieldRefAccess<int>(glass, "Count");
}

private static Type Inner(string name) => AccessTools.Inner(typeof(PartTreeRenderData), name)
    ?? throw new MissingMemberException(nameof(PartTreeRenderData), name);
```

2. **Patch all three compose methods with prefix and postfix**, with the exact signature `(ref readonly double4x4, bool, IViewport, int)`. The install is all-or-nothing: if any method is missing, roll back so the filter never hides only some model kinds:

   ```csharp
// ksa-abstractions.lib/PartRenderFilter.cs L88-L103
private static void Install(Harmony harmony)
{
    ResolveBatchAccessors();
    try
    {
        Patch(harmony, nameof(PartTreeRenderData.Compose), nameof(ComposePrefix), nameof(ComposePostfix));
        Patch(harmony, nameof(PartTreeRenderData.ComposeDynamic), nameof(ComposeDynamicPrefix), nameof(ComposeDynamicPostfix));
        Patch(harmony, nameof(PartTreeRenderData.ComposeGlass), nameof(ComposeGlassPrefix), nameof(ComposeGlassPostfix));
    }
    catch
    {
        Uninstall(harmony); // Never leave a partial filter that hides some model kinds only.
        throw;
    }
    Console.WriteLine("ksa-abstractions: PartRenderFilter patches applied");
}
```

   ```csharp
// ksa-abstractions.lib/PartRenderFilter.cs L116-L129
/// <summary>All three compose methods share the exact signature
/// <c>(ref readonly double4x4, bool, IViewport inViewport, int)</c>.</summary>
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

3. **Prefix: remember where each batch's range will start.** For every batch, record the current length of the model's `InstanceList` for this viewport, or `-1` for empty batches:

   ```csharp
// ksa-abstractions.lib/PartRenderFilter.Batches.cs L61-L77
private static void ComposePrefix(PartTreeRenderData __instance, IViewport inViewport, out bool __state)
{
    __state = Active(inViewport);
    if (!__state) return;
    try
    {
        StaticStarts.Clear();
        IList batches = _staticBatches!(__instance);
        for (int i = 0; i < batches.Count; i++)
        {
            object batch = batches[i]!; // batch lists never hold null
            StaticStarts.Add(_staticCount!(batch) == 0 ? -1
                : PartModel.ViewportData.Get(_staticModel!(batch), inViewport).InstanceList.Count);
        }
    }
    catch (Exception ex) { __state = false; Fault(ex); }
}
```

4. **Postfix: compact the appended range in place.** Only when exactly `batch.Count` entries were appended (the raster path) does the filter evaluate the predicates against `Part.FullPart` for each slot and remove hidden ones with a stable compaction and one `RemoveRange`:

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
// ksa-abstractions.lib/PartRenderFilter.Batches.cs L186-L212
/// <summary>Fills <see cref="_hidden"/> for a complete raster range; false when nothing to remove.</summary>
private static bool MarkHidden(Part[] parts, int count, int appended, bool hideModel = false)
{
    if (count == 0 || appended != count) return false;
    if (_hidden.Length < count) _hidden = new bool[Math.Max(count, _hidden.Length * 2)];
    bool any = false;
    for (int j = 0; j < count; j++)
    {
        bool hide = hideModel || ShouldHide(parts[j]);
        _hidden[j] = hide;
        any |= hide;
    }
    return any;
}

/// <summary>Removes the slots marked in <see cref="_hidden"/> from the list's tail range, in order.</summary>
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

5. **Fail open.** Any exception sets `_faulted`, logs once and makes the filter inert, so everything renders rather than breaking the render loop:

   ```csharp
// ksa-abstractions.lib/PartRenderFilter.Batches.cs L214-L219
/// <summary>A filter bug must never break the render loop: log once and render everything.</summary>
private static void Fault(Exception ex)
{
    _faulted = true;
    Console.WriteLine($"ksa-abstractions: PartRenderFilter disabled after an error: {ex}");
}
```

6. **Register an owner predicate.** The first registration installs the six patches; later owners just add predicates. Blinky hides its pixel engines, It's So Shiny shows a light pixel only while lit:

   ```csharp
// ksa-abstractions.lib/PartRenderFilter.cs L43-L60
public static void Register(Harmony harmony, string owner, Func<Part, bool> shouldHide)
{
    if (!IsInstalled) Install(harmony);
    Owners[owner] = shouldHide;
    _filters = Owners.Values.ToArray();
}

/// <summary>
/// Adds a static-model filter, evaluated once per raster batch. This hides only the selected
/// model, even when it belongs to a full part containing other models. Dynamic/glass models
/// and other owners' full-part predicates keep their existing behavior.
/// </summary>
public static void RegisterStaticModel(Harmony harmony, string owner, Func<PartModel, bool> shouldHide)
{
    if (!IsInstalled) Install(harmony);
    StaticModelOwners[owner] = shouldHide;
    _staticModelFilters = StaticModelOwners.Values.ToArray();
}
```

   ```csharp
// its-so-shiny.lib/ShinyPatches.cs L18-L22
public static void Apply(Harmony harmony)
{
    PartRenderFilter.Register(harmony, FilterOwner, fullPart => !ShouldRenderShinyPart(fullPart));
    Console.WriteLine("its-so-shiny.lib: render-skip patches applied");
}
```

7. **Hide one model of a part, not the whole part.** `RegisterStaticModel` evaluates a `Func<PartModel,bool>` once per static batch. Kitchen Sink hides only the stock medium capsule's two opaque window models by template id, which lets the interior glass show through:

   ```csharp
// kitchen-sink.lib/CapsuleGlassExperiment.cs L17-L44
    public static void Apply(Harmony harmony)
    {
        if (_registered) return;
        if (!IvaForceRender.IsInstalled)
            throw new InvalidOperationException("Capsule glass requires the IVA rendering patches.");
        PartRenderFilter.RegisterStaticModel(harmony, Owner, ShouldHide);
        _registered = true;
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled && !IsAvailable)
            throw new InvalidOperationException("Capsule glass experiment render patches are unavailable.");
        IvaForceRender.SetRequired(Owner, enabled);
        Enabled = enabled;
    }

    public static void Remove(Harmony harmony)
    {
        SetEnabled(false);
        PartRenderFilter.Unregister(harmony, Owner);
        _registered = false;
    }

    private static bool ShouldHide(PartModel model) => Enabled && model.Template.Id is
        "CoreCommandA_Subpart_MediumCapsuleWindowA_Model" or
        "CoreCommandA_Subpart_MediumCapsuleWindowB_Model";
}
```

   The game's definition of those window models and of the interior it reveals is in `current/Content/Core/CoreCommandAAssets.xml` and `CoreIVASpaceAAssets.xml` ([current/Content/Core/CoreCommandAAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/CoreCommandAAssets.xml#L55-L72)). For the interior cabin to be composed outside IVA the experiment also needs the [Template.Internal flip of approach 4](hide-parts-or-subparts-from-rendering-4.md).

## Complete example

| File | Role |
|---|---|
| [ksa-abstractions.lib/PartRenderFilter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PartRenderFilter.cs) | Owner registry, install and uninstall, exact-signature lookup |
| [ksa-abstractions.lib/PartRenderFilter.Batches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PartRenderFilter.Batches.cs) | Field-ref accessors, prefixes and postfixes, compaction, fault handling |
| [blinky.lib/BlinkyPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/BlinkyPatches.cs#L18-L28) | Full-part predicate owner |
| [its-so-shiny.lib/ShinyPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny.lib/ShinyPatches.cs#L18-L28) | Full-part predicate owner |
| [kitchen-sink.lib/CapsuleGlassExperiment.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/CapsuleGlassExperiment.cs) | Static-model predicate owner |

## Variations and gotchas

- **One shared patch set.** Two independent compactions would each shift the ranges the other measured, so all owners share one install.
- **Raytraced IVA is not filtered.** With ray tracing active in an IVA viewport `Compose` calls `model.AddInstance` per slot instead of appending a raster range; the appended-count check fails and the range is left alone.
- **Internal and shadow-proxy models** append nothing outside IVA and are skipped; see approach 4 for revealing interiors.
- **Kitten vehicles are composed late.** `UpdateRenderingResources` skips `KittenEva`; their part trees are composed inside `RenderViewport` and `RenderGame` after `PartModelRenderer.UpdateRenderData` has already uploaded, so the filter cannot hide the kitten avatar (a `KittenRenderable`, not part models) and only matters for kitten equipment submitted early (see approach 6).
- **Private member names.** The accessors are resolved by string; a rename makes `Register` throw on first use.
- **Secondary viewports and glass.** Part glass is only drawn in the main view (`RenderGame` calls `PartModelGlass.Shared.WriteCommandsColor`; `RenderViewport` does not), so through camera windows hidden opaque windows look like open holes.
- **Older builds.** Before the render-data cache the equivalent hooks were prefixes on `PartModelModule.UpdateRenderData` and siblings; those methods no longer exist.

## See also

- [Overview](overview.md)
- [Approach 2](hide-parts-or-subparts-from-rendering-2.md), [3](hide-parts-or-subparts-from-rendering-3.md), [4](hide-parts-or-subparts-from-rendering-4.md), [5](hide-parts-or-subparts-from-rendering-5.md), [6](hide-parts-or-subparts-from-rendering-6.md)
- [Hide individual part meshes by compacting the per-frame instance lists](../../../../mods/unscience/features/lib-part-render-filter.md)
- [See into the stock capsule by hiding its two opaque window models](../../../../mods/unscience/features/kitchen-sink-capsule-glass.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
