---
type: Mod Feature
title: See into the stock capsule by hiding its two opaque window models
description: Kitchen Sink's See Inside Capsule experiment hides exactly the medium capsule's two window models through the shared part render filter and takes an IvaForceRender requirement so the cabin shows behind the existing IVA window glass
mod: unscience
submod: kitchen-sink
category: rendering
techniques: [harmony-prefix, harmony-postfix, xml-asset, save-sidecar]
hooks:
  - harmony-prefix KSA.PartTreeRenderData.Compose (shared, via PartRenderFilter.RegisterStaticModel)
  - harmony-postfix KSA.PartTreeRenderData.Compose (shared, via PartRenderFilter.RegisterStaticModel)
  - harmony-postfix KSA.PartModel..ctor (shared, via IvaForceRender.SetRequired)
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent, toggle)
  - unscience ISaveParticipantSource (scene save record kitchen-sink-capsule-glass)
lifecycle: [frame:draw-ui, frame:render, event:save, event:load]
game_types: [KSA.PartModel, KSA.PartModelModule, KSA.PartModelGlass, KSA.PartTreeRenderData, KSA.Program]
tags: [rendering, iva, glass, part-models, xml, visibility, experimental]
---

"See Inside Capsule (Experimental)" lets the player look into the stock medium (Gemini-style)
capsule from outside. With it on, the two dark exterior window panes are not drawn and the cockpit
interior shows through the capsule's own IVA window glass, on every capsule of that type, without
entering IVA.[^ks-readme]

Mechanism in one line: no new patch at all. The experiment registers a whole-model predicate with the
shared [part render filter](./lib-part-render-filter.md) that matches two template ids, and calls
`IvaForceRender.SetRequired(owner, true)` from the shared [IVA force render](./lib-iva-force-render.md)
helper so the `Internal` cabin models are composed outside IVA.[^capsule-glass]

## How it works

Why three asset facts make this work (build 5554 content):

| Asset | Definition | Consequence |
|---|---|---|
| `CoreCommandA_Subpart_MediumCapsuleWindowA_Model`, `..._WindowB_Model` | Ordinary static `PartModel`s with the opaque hull material `CoreCommandA_Material`, no `<Internal>` and no `<RayTracing>` (default `Disabled`)[^ksa-commanda-assets] | Composed by `PartTreeRenderData.Compose` on the raster path in every mode, so the filter can always remove them |
| `CoreIVASpaceA_Subpart_MediumCapsuleAWindowGlass_Model` | A `PartModelGlass` with no `<Internal>` flag (`RayTracing` true), part of the `CoreIVASpaceA_Prefab_MediumCapsuleA` interior[^ksa-ivaspacea-assets] | Glass is drawn outside IVA already; it is simply hidden behind the opaque window |
| `CoreIVASpaceA_Subpart_MediumCapsuleA_Model`, `..._TempHatchesA_Model` | `<Internal>true</Internal>`, `RayTracing` `Enabled`; `..._RayBlocker_Model` is `Internal` with `ShadowProxy` | The cabin is skipped outside IVA unless the `Internal` flag is lifted; the shadow proxy must never be drawn |

The capsule part (`CoreCommandA_Prefab_MediumCapsuleVariantA`) attaches that interior with
`<AttachedInternal InstanceOf="CoreIVASpaceA_Prefab_MediumCapsuleA" />`.[^ksa-commanda-gamedata]
`PartModelGlass.AddInstance` gates only on `RenderPartModels` and the IVA ray-tracing case, not on
`Internal`.[^ksa-partmodelglass]

The experiment (`CapsuleGlassExperiment`):[^capsule-glass]

1. `Apply(harmony)` throws unless `IvaForceRender.IsInstalled`, then calls
   `PartRenderFilter.RegisterStaticModel(harmony, "kitchen-sink.capsule-glass", ShouldHide)`. The first
   owner of the filter installs its shared `Compose`/`ComposeDynamic`/`ComposeGlass` prefix and postfix
   pairs; later owners only add predicates.[^part-render-filter]
2. `ShouldHide(PartModel model) => Enabled && model.Template.Id is "...WindowA_Model" or "...WindowB_Model"`
   (exact ordinal match on the shared template's `TemplateDataBase.Id`).[^ksa-moduleBase]
3. `SetEnabled(bool)` refuses to enable when `IsAvailable` is false (filter not operational or IVA
   patches missing), sets the IVA requirement and the flag. `IvaForceRender` OR-combines owner
   requirements with the user's own "Always Render IVA Interiors" preference, so turning either off
   leaves interiors visible while the other still needs them.[^iva-force-render]
4. In the filter's static `Compose` postfix, `ShouldHideStaticModel(batch.Model)` is evaluated once per
   batch; when true, every slot the game just appended for that model is marked hidden and compacted
   out of `InstanceList` and `DentInstanceList`.[^part-render-filter-batches]

Scene save: a boolean record `kitchen-sink-capsule-glass` (version 1, restore order 20) separate from
the older `kitchen-sink` record for the user's IVA preference; reset releases the requirement before
the old world is destroyed; restore fails visibly and keeps the record if the render patches are
unavailable.[^ks-saves]

## Lifecycle and timing

| Code | When | Notes |
|---|---|---|
| Checkbox -> `SetEnabled` -> `IvaForceRender.SetRequired` | `frame:draw-ui` | Flips `Template.Internal` on the shared interior templates immediately on the main thread |
| `ShouldHide` (inside the shared `Compose` postfix) | `frame:render`: `Program.Render` -> `UpdateRenderingResources` (L2468) -> `Vehicle.UpdateRenderData` -> `PartTree.UpdateRenderData` -> `PartTreeRenderData.Compose`, once per tree per visible viewport | Same frame as the toggle, before `RenderGame` records draws[^ksa-program][^ksa-parttreerenderdata] |
| Save reset and restore | `event:save` / `event:load`, order 20 | Restore re-runs `SetEnabled` after native reconstruction |

Nothing here runs on a worker thread. The template flag and the instance lists are read only by the
main-thread render-data path.

## Key code

| Path | Role |
|---|---|
| [kitchen-sink.lib/CapsuleGlassExperiment.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/CapsuleGlassExperiment.cs#L9-L44) | Registration, availability, model predicate |
| [ksa-abstractions.lib/PartRenderFilter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PartRenderFilter.cs#L55-L77) | `RegisterStaticModel`, `ShouldHideStaticModel` |
| [ksa-abstractions.lib/PartRenderFilter.Batches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PartRenderFilter.Batches.cs#L79-L100) | Static-batch postfix applying the model predicate |
| [ksa-abstractions.lib/IvaForceRender.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/IvaForceRender.cs#L44-L57) | Owner-keyed interior requirement |
| [kitchen-sink.lib/KitchenSinkLib.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/KitchenSinkLib.cs#L80-L94) | Checkbox and status text |
| [kitchen-sink.lib/KitchenSinkSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/KitchenSinkSubmod.Saves.cs#L17-L23) | Boolean scene record |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L87-L88) | Install order: IVA patches before the experiment |

## Snippets

The whole feature:

```csharp
// kitchen-sink.lib/CapsuleGlassExperiment.cs L17-L43
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
// ...
    private static bool ShouldHide(PartModel model) => Enabled && model.Template.Id is
        "CoreCommandA_Subpart_MediumCapsuleWindowA_Model" or
        "CoreCommandA_Subpart_MediumCapsuleWindowB_Model";
```

The two exterior window models in the game content:

```xml
<!-- current/Content/Core/CoreCommandAAssets.xml L55-L72 -->
  <SubPart Id="CoreCommandA_Subpart_MediumCapsuleWindowA">
    <PartModel Id="CoreCommandA_Subpart_MediumCapsuleWindowA_Model">
      <Mesh Id="CoreCommandA_Subpart_MediumCapsuleWindowA" />
      <Material Id="CoreCommandA_Material" />
    </PartModel>
  </SubPart>
<!-- ... -->
  <SubPart Id="CoreCommandA_Subpart_MediumCapsuleWindowB">
    <PartModel Id="CoreCommandA_Subpart_MediumCapsuleWindowB_Model">
      <Mesh Id="CoreCommandA_Subpart_MediumCapsuleWindowB" />
      <Material Id="CoreCommandA_Material" />
    </PartModel>
  </SubPart>
```

The interior: non-internal glass, internal cabin, internal shadow proxy:

```xml
<!-- current/Content/Core/CoreIVASpaceAAssets.xml L20-L44 -->
    <SubPart Id="CoreIVASpaceA_Subpart_MediumCapsuleAWindowGlass">
        <PartModelGlass Id="CoreIVASpaceA_Subpart_MediumCapsuleAWindowGlass_Model">
            <Mesh Id="CoreIVASpaceA_Subpart_MediumCapsuleAWindowGlass" />
            <Material Id="CoreIVASpaceA_Material" />
            <RayTracing>true</RayTracing>
        </PartModelGlass>
    </SubPart>

    <SubPart Id="CoreIVASpaceA_Subpart_MediumCapsuleARayBlocker">
        <PartModel Id="CoreIVASpaceA_Subpart_MediumCapsuleARayBlocker_Model">
            <Internal>true</Internal>
            <Mesh Id="CoreIVASpaceA_Subpart_MediumCapsuleARayBlocker" />
            <Material Id="CoreIVASpaceA_Material" />
            <RayTracing>ShadowProxy</RayTracing>
        </PartModel>
    </SubPart>

    <SubPart Id="CoreIVASpaceA_Subpart_MediumCapsuleA">
        <PartModel Id="CoreIVASpaceA_Subpart_MediumCapsuleA_Model">
            <Internal>true</Internal>
            <Mesh Id="CoreIVASpaceA_Subpart_MediumCapsuleA" />
            <Material Id="CoreIVASpaceA_Material" />
            <RayTracing>Enabled</RayTracing>
        </PartModel>
    </SubPart>
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `PartTreeRenderData.Compose` (raster branch gate on `RayTracing` and `Internal`) | [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1258-L1303) | Where the window instances are appended and removed |
| `PartModelGlass.AddInstance` | [current/decomp/KSA/PartModelGlass.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelGlass.cs#L533-L548) | Glass has no `Internal` gate |
| `PartModel.Template.Id` (`ModuleBase.TemplateDataBase.Id`) | [current/decomp/KSA/ModuleBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleBase.cs#L8-L11) | Model identity for the predicate |
| `Program.UpdateRenderingResources` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4308-L4375) | Per-viewport composition before `RenderGame` |
| Part glass draw in the main view only (`PartModelGlass.Shared.WriteCommandsColor`) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4894) | Not called by `RenderViewport` |

## Pitfalls and version notes

- Secondary viewports (stock extra cameras, [Hot Pursuit](./hot-pursuit-part-mounted-cameras.md)) never
  draw part glass, so through them the windows look like open holes into the cabin.[^ks-readme]
- Global by design: every instance of the two models is hidden, and interiors of every vessel are
  composed outside IVA while the experiment is on (the requirement lifts the shared templates' flag).
- The filter cannot remove ray-traced submissions, but these window templates have ray tracing disabled,
  so the raster path also covers IVA with ray tracing on.[^scope-ui]
- Stock IVA glass is strongly tinted and was authored to be seen from inside; window alignment, depth,
  culling and exterior lighting are listed by the authors as unverified in game.[^scope-ui]
- Template ids are matched exactly. If a game update renames the window models or the medium capsule
  gains different window subparts, the experiment silently does nothing. Recheck
  `CoreCommandAAssets.xml` on updates.
- `Apply` depends on install order: `IvaForceRender.Patch` must run first (both patchers do this) or it
  throws and the panel shows the feature as unavailable.[^unscience-patcher]
- Added for 5541; the referenced XML and render members are identical in 5554.

## See also

- [Part render filter](./lib-part-render-filter.md)
- [IVA force render](./lib-iva-force-render.md)
- [Kitchen Sink IVA camera unlock](./kitchen-sink-iva-camera-unlock.md)
- [Hot Pursuit part-mounted cameras](./hot-pursuit-part-mounted-cameras.md) (secondary renderer limits)
- [Sub-mod: Kitchen Sink](../submods/kitchen-sink.md)
- [Mod overview](../overview.md)
- [Hide part models by compacting the instance lists around PartTreeRenderData.Compose](../../../howto/rendering/meshes/hide-parts-or-subparts-from-rendering/hide-parts-or-subparts-from-rendering-1.md) - how-to article demonstrating this feature
- [Show IVA interiors from outside with an owner-counted Template.Internal requirement](../../../howto/rendering/meshes/hide-parts-or-subparts-from-rendering/hide-parts-or-subparts-from-rendering-4.md) - how-to article demonstrating this feature

[^ks-readme]: kitchen-sink/README.md
[^capsule-glass]: kitchen-sink.lib/CapsuleGlassExperiment.cs
[^ksa-commanda-assets]: Content/Core/CoreCommandAAssets.xml (build 2026.10.10.5554)
[^ksa-ivaspacea-assets]: Content/Core/CoreIVASpaceAAssets.xml (build 2026.10.10.5554)
[^ksa-commanda-gamedata]: Content/Core/CoreCommandAGameData.xml (build 2026.10.10.5554)
[^ksa-partmodelglass]: KSA.PartModelGlass (decompiled, build 2026.10.10.5554)
[^part-render-filter]: ksa-abstractions.lib/PartRenderFilter.cs
[^ksa-moduleBase]: KSA.ModuleBase (decompiled, build 2026.10.10.5554)
[^iva-force-render]: ksa-abstractions.lib/IvaForceRender.cs
[^part-render-filter-batches]: ksa-abstractions.lib/PartRenderFilter.Batches.cs
[^ks-saves]: kitchen-sink.lib/KitchenSinkSubmod.Saves.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-parttreerenderdata]: KSA.PartTreeRenderData (decompiled, build 2026.10.10.5554)
[^scope-ui]: scope/ui-customization.md (authors' capsule glass asset map)
[^unscience-patcher]: unscience/Patcher.cs
