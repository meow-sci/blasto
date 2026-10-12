---
type: Mod Feature
title: Force IVA interior models to render outside IVA
description: Flips the shared PartModelModule.Template.Internal flag on loaded and newly constructed part models, with an owner-counted requirement API and a Compose prefix and finalizer that reveal interiors in the vehicle editor for one call at a time
mod: unscience
category: rendering
techniques: [harmony-postfix, harmony-prefix, harmony-finalizer]
hooks:
  - harmony-postfix KSA.PartModel..ctor(PartModelModule.Template)
  - harmony-prefix KSA.PartTreeRenderData.Compose
  - harmony-finalizer KSA.PartTreeRenderData.Compose
lifecycle: [frame:render, frame:draw-ui]
game_types: [KSA.PartModel, KSA.PartModelModule, KSA.PartTreeRenderData, KSA.CameraMode, KSA.Program]
tags: [iva, interiors, part-models, editor, harmony, rendering]
---

KSA authors cockpit interiors as separate part models marked `<Internal>true</Internal>` in the
part XML; the renderer only draws them when the viewport camera is in IVA mode. `IvaForceRender`
makes those interiors visible from the outside (Kitchen Sink's "Always Render IVA Interiors"
toggle) and keeps them visible in the vehicle editor, where the editor camera is never in IVA mode.
Other features can require interiors without touching the user's own toggle.[^iva-force-render]

Mechanism in one line: the game reads `PartModelModule.Template.Internal` at render time, and the
template object is shared by every instance of that model, so the library flips that flag on all
current `PartModel.Instances` (and on new ones through a constructor postfix) and restores exactly
the templates it changed.

## How it works

The gate is a plain field check in two places in build 5554:[^ksa-partmodel][^ksa-parttreerenderdata]

- `PartTreeRenderData.Compose` (raster path, L1300): instances are appended only when
  `RayTracing != ShadowProxy && (!Template.Internal || viewport is IVA)`.
- `PartModel.AddInstance` (L486) applies the same test for direct submissions.

`IvaForceRender` state and API:

| Member | Behavior |
|---|---|
| `Enabled` (user preference) | Setting it recomputes `EffectiveEnabled` and forces or restores |
| `SetRequired(owner, bool)` | Owner-keyed requirement set; `EffectiveEnabled = Enabled \|\| Requirements.Count > 0` |
| `ForceInternalVisible()` | For every `PartModel.Instances` whose template is internal and not a shadow proxy: remember the template, set `Internal = false` |
| `RestoreInternalHidden()` | Set `Internal = true` on exactly the remembered templates |
| `CtorPostfix(PartModel)` | When effective, flips templates of models constructed later (new vessels, editor parts) |
| `ComposePrefix` / `ComposeFinalizer` | Editor-only temporary reveal around one `Compose` call |
| `Patch` / `Unpatch` | Install/remove; `Unpatch` clears preferences and requirements and restores first |

Shadow-proxy templates (`RaytracingMode.ShadowProxy`) are never touched, because they exist only to
cast ray-traced shadows and are not meant to be drawn.[^ksa-partmodelmodule]

The editor reveal is separate because the persistent flip only runs when the feature is effectively
enabled, but the authors also want IVA sub-parts to stay visible in the editor regardless (KSA 5482
moved editor composition to `PartTreeRenderData`). The prefix runs only when `Program.Editor` is open,
the viewport is not in IVA and renders part models; it sets `Internal = false` on every known
internal template and records them; the finalizer sets them back even if `Compose` throws. The list
of internal templates is rebuilt lazily when the constructor postfix reports new models.

Consumers: Kitchen Sink's checkbox drives `Enabled` and scene-saves it as a boolean record;
Kitchen Sink's capsule-glass experiment calls `SetRequired("kitchen-sink.capsule-glass", true)` so
the cabin behind the hidden window models is visible without changing the user's
toggle.[^kitchen-sink-lib][^capsule-glass][^kitchen-sink-saves]

## Lifecycle and timing

| Code path | When it runs | Notes |
|---|---|---|
| `Enabled` / `SetRequired` -> `ForceInternalVisible` / `RestoreInternalHidden` | ImGui handlers (`frame:draw-ui`) or save replay | Main thread; takes effect the next time `Compose` runs in `frame:render` of the same frame |
| `CtorPostfix` | Whenever the game constructs a `PartModel` (vessel spawn, editor part creation, loads) | Main thread in practice |
| `ComposePrefix` / `ComposeFinalizer` | Inside `Program.UpdateRenderingResources` (called from `Program.Render` before command recording), once per tree per visible viewport (`frame:render`; in the editor scene this is `Editor.EditingSpace.Parts?.UpdateRenderData`) | Bracket a single call |

The temporary editor reveal is safe because, in build 5554, the only readers of
`Template.Internal` are the main-thread render paths listed above (`PartTreeRenderData.Compose` and
`PartModel.AddInstance`); no worker thread observes the flag while it is flipped.[^ksa-program]
Mutating shared templates rather than individual instances is what makes a one-flag change affect
every copy of a model, including those created after the toggle.

## Key code

| Path | Role |
|---|---|
| [ksa-abstractions.lib/IvaForceRender.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/IvaForceRender.cs) | Preference, requirements, template flipping, patches |
| [kitchen-sink.lib/KitchenSinkLib.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/KitchenSinkLib.cs#L60-L69) | "Always Render IVA Interiors" checkbox |
| [kitchen-sink.lib/CapsuleGlassExperiment.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/CapsuleGlassExperiment.cs#L17-L31) | Requires interiors while the experiment is on |
| [kitchen-sink.lib/KitchenSinkSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/KitchenSinkSubmod.Saves.cs#L15-L16) | Scene-saves the user preference |

## Snippets

```csharp
// ksa-abstractions.lib/IvaForceRender.cs L62-L79
    public static void Patch(Harmony harmony)
    {
        if (IsInstalled) return;
        _ctorOriginal = AccessTools.Constructor(typeof(PartModel), new[] { typeof(PartModelModule.Template) });
        _ctorPostfix = typeof(IvaForceRender).GetMethod(nameof(CtorPostfix), BindingFlags.NonPublic | BindingFlags.Static)!;
        harmony.Patch(_ctorOriginal, postfix: new HarmonyMethod(_ctorPostfix));

        // KSA 5482 raster-composes static part models from cached batches and applies the
        // internal/IVA gate itself, without calling PartModel.AddInstance.
        _composeOriginal = AccessTools.Method(typeof(PartTreeRenderData), nameof(PartTreeRenderData.Compose), new[] { typeof(Brutal.Numerics.double4x4).MakeByRefType(), typeof(bool), typeof(IViewport), typeof(int) })
            ?? throw new MissingMethodException(nameof(PartTreeRenderData), nameof(PartTreeRenderData.Compose));
        _composePrefix = typeof(IvaForceRender).GetMethod(nameof(ComposePrefix), BindingFlags.NonPublic | BindingFlags.Static)!;
        _composeFinalizer = typeof(IvaForceRender).GetMethod(nameof(ComposeFinalizer), BindingFlags.NonPublic | BindingFlags.Static)!;
        harmony.Patch(_composeOriginal, prefix: new HarmonyMethod(_composePrefix), finalizer: new HarmonyMethod(_composeFinalizer));
        IsInstalled = true;

        Console.WriteLine("ksa-abstractions: IvaForceRender patches applied");
    }
```

The one-call editor reveal, restored by a finalizer so an exception cannot leave interiors exposed:

```csharp
// ksa-abstractions.lib/IvaForceRender.cs L138-L157
    private static void ComposePrefix(IViewport inViewport)
    {
        if (KSA.Program.Editor == null || inViewport.Mode == CameraMode.IVA
            || !inViewport.HasAny(ViewportOptionFlags.RenderPartModels)) return;
        if (_internalTemplatesDirty) RebuildInternalTemplates();
        foreach (var template in _internalTemplates)
        {
            if (!template.Internal) continue;
            template.Internal = false;
            _revealed.Add(template);
        }
    }

    private static Exception? ComposeFinalizer(Exception? __exception)
    {
        foreach (var template in _revealed)
            template.Internal = true;
        _revealed.Clear();
        return __exception;
    }
```

The game's gate in the raster branch:

```csharp
// current/decomp/KSA/PartTreeRenderData.cs L1300-L1303
			else if (model.Template.RayTracing != PartModelModule.RaytracingMode.ShadowProxy && (!model.Template.Internal || flag))
			{
				PartModel.ViewportData viewportData = PartModel.ViewportData.Get(model, inViewport);
				int count = viewportData.InstanceList.Count;
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `PartModel(PartModelModule.Template)` (protected ctor) | [current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L435) | Postfix catches models created after the toggle |
| `PartModel.Instances` (static list) | [current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L409) | Enumerate every loaded model |
| `PartModel.AddInstance` internal gate | [current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L486) | Second reader of the flag |
| `PartModelModule.Template.Internal` / `.RayTracing`, `RaytracingMode.ShadowProxy` | [current/decomp/KSA/PartModelModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelModule.cs#L10-L39) | The XML-backed flags being flipped |
| `PartTreeRenderData.Compose` | [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1258-L1319) | Prefix/finalizer target; applies the gate |
| `Program.Editor` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs) | Editor-scene detection |

## Pitfalls and version notes

- Templates are shared XML data: the flip affects every vessel using the model until restored, and a
  hot reload of part data would discard the change. The library restores only templates it recorded.
- Shares `PartTreeRenderData.Compose` with the [part render filter](./lib-part-render-filter.md); both
  are independent patches on the same method and the filter's range check is unaffected because the
  reveal happens before the game appends.
- Before KSA 5482 the editor reveal was a postfix on the private `PartModel.AddInstance` sink; 5482
  composes static models from cached batches without calling it, so the hook moved to `Compose`.
- Ray-traced IVA interiors are a different path (`PartModel.InstancesRayTrace`); this feature only
  concerns raster rendering outside IVA.

## See also

- [Part render filter](./lib-part-render-filter.md)
- [Kitchen Sink capsule glass](./kitchen-sink-capsule-glass.md)
- [Kitchen Sink IVA camera unlock](./kitchen-sink-iva-camera-unlock.md)
- [Sub-mod: Kitchen Sink](../submods/kitchen-sink.md)
- [Mod overview](../overview.md)
- [Restore what a prefix changed around one game call](../../../howto/patching/harmony/run-cleanup-or-recover-from-exceptions-with-a-finalizer/run-cleanup-or-recover-from-exceptions-with-a-finalizer-1.md) - how-to article demonstrating this feature
- [Show IVA interiors from outside with an owner-counted Template.Internal requirement](../../../howto/rendering/meshes/hide-parts-or-subparts-from-rendering/hide-parts-or-subparts-from-rendering-4.md) - how-to article demonstrating this feature

[^iva-force-render]: ksa-abstractions.lib/IvaForceRender.cs
[^ksa-partmodel]: KSA.PartModel (decompiled, build 2026.10.10.5554)
[^ksa-parttreerenderdata]: KSA.PartTreeRenderData (decompiled, build 2026.10.10.5554)
[^ksa-partmodelmodule]: KSA.PartModelModule (decompiled, build 2026.10.10.5554)
[^kitchen-sink-lib]: kitchen-sink.lib/KitchenSinkLib.cs
[^capsule-glass]: kitchen-sink.lib/CapsuleGlassExperiment.cs
[^kitchen-sink-saves]: kitchen-sink.lib/KitchenSinkSubmod.Saves.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
