---
type: How-To
title: Show IVA interiors from outside with an owner-counted Template.Internal requirement
description: Flip the shared PartModelModule.Template.Internal flag on every loaded and newly constructed model so cabins render outside IVA, with several owners sharing one requirement count and an editor-only reveal bracketed by a finalizer
category: rendering
group: meshes
topic: hide-parts-or-subparts-from-rendering
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/lib-iva-force-render, mods/unscience/features/kitchen-sink-capsule-glass]
techniques: [harmony-postfix, harmony-prefix, harmony-finalizer]
hooks:
  - harmony-postfix KSA.PartModel..ctor(PartModelModule.Template)
  - harmony-prefix KSA.PartTreeRenderData.Compose
  - harmony-finalizer KSA.PartTreeRenderData.Compose
lifecycle: [frame:render, frame:draw-ui]
game_types: [KSA.PartModel, KSA.PartModelModule, KSA.PartTreeRenderData, KSA.CameraMode, KSA.Program]
tags: [iva, interior, internal-flag, template, cabin, capsule]
---
Outcome: cockpit interiors, authored as separate part models marked `<Internal>true</Internal>`, become visible from the exterior camera and in the vehicle editor. Several features can require interiors at once without stepping on the player's own toggle.

Demonstrated by unscience's `IvaForceRender` helper and its Kitchen Sink consumers ([Force IVA interior models to render outside IVA](../../../../mods/unscience/features/lib-iva-force-render.md), [See into the stock capsule by hiding its two opaque window models](../../../../mods/unscience/features/kitchen-sink-capsule-glass.md)). Prerequisites: StarMap, Harmony. To also see through the exterior hull, combine it with the [model-hiding filter of approach 1](hide-parts-or-subparts-from-rendering-1.md).

## Background: how the game does it

`<Internal>` is an XML-backed field on `PartModelModule.Template` ([current/decomp/KSA/PartModelModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelModule.cs#L37-L39)), and the template object is shared by every instance of that model. The renderer reads it at render time in two places: the raster branch of `PartTreeRenderData.Compose`, and `PartModel.AddInstance` for direct submissions. Both apply the same test, instance appended only when the model is not a shadow proxy and either not internal or the viewport camera is in IVA:

```csharp
// current/decomp/KSA/PartTreeRenderData.cs L1300-L1303
else if (model.Template.RayTracing != PartModelModule.RaytracingMode.ShadowProxy && (!model.Template.Internal || flag))
{
	PartModel.ViewportData viewportData = PartModel.ViewportData.Get(model, inViewport);
	int count = viewportData.InstanceList.Count;
```

```csharp
// current/decomp/KSA/PartModel.cs L486-L490
if (Template.RayTracing != PartModelModule.RaytracingMode.ShadowProxy && (!Template.Internal || viewport.Mode == CameraMode.IVA))
{
	viewportData.InstanceList.Add(instanceData);
	viewportData.DentInstanceList.Add(dentInstance);
}
```

Changing one boolean on the shared template therefore affects every copy, including ones created later. Shadow-proxy templates (`RayTracing == ShadowProxy`) exist only to cast ray-traced shadows and must never be touched.

## Lifecycle and timing

| Code path | When it runs | Notes |
|---|---|---|
| `Enabled` / `SetRequired` then `ForceInternalVisible` / `RestoreInternalHidden` | ImGui handlers (`frame:draw-ui`) or save replay | Main thread; takes effect the next time `Compose` runs in `frame:render` of the same frame |
| `CtorPostfix` | Whenever the game constructs a `PartModel` (vessel spawn, editor part creation, loads) | Main thread in practice |
| `ComposePrefix` / `ComposeFinalizer` | Inside `Program.UpdateRenderingResources`, once per tree per visible viewport (`frame:render`; in the editor scene via `Editor.EditingSpace.Parts?.UpdateRenderData`, which `UpdateRenderingResources` also calls from `Render`, so it is still `frame:render`, not `editor:render`) | Bracket a single call |

The temporary editor reveal is safe because in this build the only readers of `Template.Internal` are the main-thread render paths above; no worker thread observes the flag while it is flipped. See row 22 of [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence).

## Steps

1. **Keep user preference and requirements separate.** `EffectiveEnabled = Enabled || Requirements.Count > 0`; owners call `SetRequired(owner, bool)` and the flip happens only when the effective state changes:

   ```csharp
// ksa-abstractions.lib/IvaForceRender.cs L28-L57
public static bool IsInstalled { get; private set; }
public static bool EffectiveEnabled => _enabled || Requirements.Count > 0;

public static bool Enabled
{
    get => _enabled;
    set
    {
        if (_enabled == value) return;
        bool wasEffective = EffectiveEnabled;
        _enabled = value;
        RefreshVisibility(wasEffective);
    }
}

/// <summary>Require interiors without changing the user's independent Enabled preference.</summary>
public static void SetRequired(string owner, bool required)
{
    bool wasEffective = EffectiveEnabled;
    if (required) Requirements.Add(owner);
    else Requirements.Remove(owner);
    RefreshVisibility(wasEffective);
}

private static void RefreshVisibility(bool wasEffective)
{
    if (wasEffective == EffectiveEnabled) return;
    if (EffectiveEnabled) ForceInternalVisible();
    else RestoreInternalHidden();
}
```

2. **Install a constructor postfix and a `Compose` prefix/finalizer.** `PartModel`'s constructor is protected; find it with `AccessTools.Constructor`. Since the render-data cache raster-composes static models and applies the gate itself without calling `AddInstance`, the editor reveal moved onto `Compose`:

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

3. **Flip and restore exactly the templates you changed.** Record each template before clearing `Internal`, skip shadow proxies, and restore only the recorded ones so you never mark a template internal that was not:

   ```csharp
// ksa-abstractions.lib/IvaForceRender.cs L172-L193
private static void ForceInternalVisible()
{
    _mutatedTemplates.Clear();
    foreach (var pm in PartModel.Instances)
    {
        if (pm.Template.Internal && pm.Template.RayTracing != PartModelModule.RaytracingMode.ShadowProxy)
        {
            _mutatedTemplates.Add(pm.Template);
            pm.Template.Internal = false;
        }
    }
    Console.WriteLine($"ksa-abstractions: IvaForceRender forced {_mutatedTemplates.Count} internal templates visible");
}

private static void RestoreInternalHidden()
{
    foreach (var t in _mutatedTemplates)
        t.Internal = true;
    _internalTemplatesDirty = true;
    Console.WriteLine($"ksa-abstractions: IvaForceRender restored {_mutatedTemplates.Count} internal templates");
    _mutatedTemplates.Clear();
}
```

4. **Catch models constructed later** (new vessels, editor parts) in the constructor postfix:

   ```csharp
// ksa-abstractions.lib/IvaForceRender.cs L121-L130
private static void CtorPostfix(PartModel __instance)
{
    _internalTemplatesDirty = true;
    if (!EffectiveEnabled) return;
    if (!__instance.Template.Internal) return;
    if (__instance.Template.RayTracing == PartModelModule.RaytracingMode.ShadowProxy) return;

    __instance.Template.Internal = false;
    TrackMutated(__instance.Template);
}
```

5. **Keep interiors visible in the editor regardless.** The editor camera is never in IVA mode, so around one `Compose` call the prefix reveals every known internal template and the finalizer sets them back even if `Compose` throws:

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

6. **Drive it from UI and from other features.** Kitchen Sink's checkbox sets `Enabled`; the capsule-glass experiment calls `SetRequired("kitchen-sink.capsule-glass", true)` so the cabin behind the hidden window models is visible without changing the user's toggle:

   ```csharp
// kitchen-sink.lib/KitchenSinkLib.cs L60-L69
private void RenderIvaForceRender()
{
    ImGui.SeparatorText("Force IVA Rendering");
    ImGui.TextWrapped("Force interior (IVA) parts to render even when not in IVA camera mode.");
    ImGui.Spacing();

    var enabled = IvaForceRender.Enabled;
    if (ImGui.Checkbox("Always Render IVA Interiors", ref enabled))
        IvaForceRender.Enabled = enabled;
}
```

   ```csharp
// kitchen-sink.lib/CapsuleGlassExperiment.cs L26-L32
public static void SetEnabled(bool enabled)
{
    if (enabled && !IsAvailable)
        throw new InvalidOperationException("Capsule glass experiment render patches are unavailable.");
    IvaForceRender.SetRequired(Owner, enabled);
    Enabled = enabled;
}
```

## Complete example

| File | Role |
|---|---|
| [ksa-abstractions.lib/IvaForceRender.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/IvaForceRender.cs) | Preference, requirements, template flipping, patches |
| [kitchen-sink.lib/KitchenSinkLib.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/KitchenSinkLib.cs#L60-L69) | "Always Render IVA Interiors" checkbox |
| [kitchen-sink.lib/CapsuleGlassExperiment.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/CapsuleGlassExperiment.cs#L17-L44) | Requires interiors while the experiment is on |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L87-L88) | Install order: IVA patches before the experiment |

## Variations and gotchas

- **Global by design.** Templates are shared XML data; the flip affects every vessel using the model until restored, and a hot reload of part data would discard it.
- **Install order matters.** `Apply` of the capsule experiment throws unless `IvaForceRender.IsInstalled`, so the IVA patches must be installed first.
- **Ray-traced IVA interiors are a different path** (`PartModel.InstancesRayTrace`); this feature concerns raster rendering outside IVA.
- **Glass is already outside-visible.** `PartModelGlass.AddInstance` has no `Internal` gate, so interior window glass renders from outside; it is hidden behind the opaque exterior window models until those are hidden.
- **Secondary viewports never draw part glass**, so camera windows show hidden windows as holes into the cabin.
- **Before the render-data cache** the editor reveal was a postfix on the private `PartModel.AddInstance` sink; after it, the hook moved to `Compose`.
- **Related lazily installed variant:** [approach 5](hide-parts-or-subparts-from-rendering-5.md) does the same flip with a dedicated Harmony instance created only while the cheat is on.

## See also

- [Overview](overview.md)
- [Approach 1](hide-parts-or-subparts-from-rendering-1.md), [approach 5](hide-parts-or-subparts-from-rendering-5.md)
- [Force IVA interior models to render outside IVA](../../../../mods/unscience/features/lib-iva-force-render.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
