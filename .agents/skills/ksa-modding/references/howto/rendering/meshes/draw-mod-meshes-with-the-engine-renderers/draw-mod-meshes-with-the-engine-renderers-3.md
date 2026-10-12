---
type: How-To
title: Add extra part-model instances at any matrix, per viewport
description: Draw shells, missiles or props as additional instances of the engine's own PartModel meshes from a prefix on PartModelRenderer.UpdateRenderData, and keep a launcher drawn past the pixel cull
category: rendering
group: meshes
topic: draw-mod-meshes-with-the-engine-renderers
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksarmory/features/round-body-draw-hooks]
techniques: [harmony-prefix, harmony-postfix, part-model-instancing]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-prefix KSA.PartModelRenderer.UpdateRenderData
  - harmony-postfix KSA.Vehicle.UpdateRenderData
lifecycle: [startup:all-mods-loaded, frame:render, render:scene-opaque, render:shadows]
game_types: [KSA.PartModel, KSA.PartModelRenderer, KSA.Vehicle, KSA.Camera, KSA.Program]
tags: [part-model, instancing, per-viewport, pixel-cull, projectile]
---
Outcome: any number of extra copies of a part's mesh, at any position and rotation, drawn with the part shader, lighting and shadows, with no part, no vessel and no subpart behind them. The cost is one `AddInstance` call per copy per viewport per frame.

Demonstrated by KSArmory ([Round bodies drawn past the engine's pixel cull and as part-model instances](../../../../mods/ksarmory/features/round-body-draw-hooks.md)): a gun can have more shells in the air than its part declares subparts, and a destroyed launcher takes its parts with it, but the shared mesh outlives both. Prerequisites: StarMap, Harmony.

## Background: how the game does it

`PartModel` [current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L417) is the shared GPU mesh for one `PartModelModule.Template`. `PartModel.Get(template)` returns it; it is kept for the session. Every part of that template appends an instance to it each frame, and `PartModelRenderer.UpdateRenderData` uploads the lists. The public `AddInstance` guards on the viewport:

```csharp
// current/decomp/KSA/PartModel.cs L465-L491
public void AddInstance(PerInstanceData instanceData, IViewport viewport, int frameIndex)
{
	AddInstance(instanceData, PerInstanceDent.Undented, viewport, frameIndex);
}

private void AddInstance(PerInstanceData instanceData, PerInstanceDent dentInstance, IViewport viewport, int frameIndex)
{
	if (!viewport.HasAny(ViewportOptionFlags.RenderPartModels))
	{
		return;
	}
	ViewportData viewportData = ViewportData.Get(this, viewport);
	if (Program.Instance.IsRaytracingActive && viewport.HasAll(ViewportOptionFlags.UseRaytracing) && viewport.Mode == CameraMode.IVA && Template.RayTracing != PartModelModule.RaytracingMode.Disabled)
	{
		viewportData.RayTraceTransforms.Add(instanceData);
		if (Template.RayTracing != PartModelModule.RaytracingMode.ShadowProxy)
		{
			Program.Instance.RaytracingRenderer?.PartRaytracingDataOpaque.AddInstance(this, instanceData, frameIndex);
			return;
		}
	}
	if (Template.RayTracing != PartModelModule.RaytracingMode.ShadowProxy && (!Template.Internal || viewport.Mode == CameraMode.IVA))
	{
		viewportData.InstanceList.Add(instanceData);
		viewportData.DentInstanceList.Add(dentInstance);
	}
}
```

Consequences: viewports without `RenderPartModels` ignore the call; in IVA with ray tracing the instance is routed to the ray tracing renderer; an `Internal` template (interior geometry) is only drawn in IVA. The upload is one indirect draw per model:

```csharp
// current/decomp/KSA/PartModelRenderer.cs L807-L827
public static void UpdateRenderData(IViewport viewport, int frameIndex)
{
	Profiler.MainThread.Begin(Profiler.Tag.UpdateRenderDataPartModelsStatic);
	foreach (PartModel instance in PartModel.Instances)
	{
		instance.WriteInstancesToGpu(viewport, frameIndex);
	}
	Profiler.MainThread.End();
	Profiler.MainThread.Begin(Profiler.Tag.UpdateRenderDataPartModelsDynamic);
	foreach (PartModelDynamic instance2 in PartModelDynamic.Instances)
	{
		instance2.WriteInstancesToGpu(viewport, frameIndex);
	}
	Profiler.MainThread.End();
	Profiler.MainThread.Begin(Profiler.Tag.UpdateRenderDataPartModelsGlass);
	foreach (PartModelGlass instance3 in PartModelGlass.Instances)
	{
		instance3.WriteInstancesToGpu(viewport, frameIndex);
	}
	Profiler.MainThread.End();
}
```

The instance lists are cleared in `PostRender` (`ClearFrameData(ResourceFrameIndex)` at [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2344), which reaches `PartModelRenderer.ClearFrameData` and `PartModel.ClearInstanceLists`), so instances must be re-added every frame, for every viewport.

The second engine behaviour this approach works around is the pixel cull. `Vehicle.UpdateRenderData` draws a vessel's parts only if its bounding sphere is at least one pixel across:

```csharp
// current/decomp/KSA/Vehicle.cs L3707-L3726
public bool IsLargeEnoughToRender(Camera inCamera)
{
	double3 positionEgo = inCamera.GetPositionEgo(this);
	return !(inCamera.GetObjectDiameterPixels(2.0 * MeanRadius, positionEgo.Length()) < 1.0);
}

public virtual void UpdateRenderData(IViewport viewport, int inFrameIndex)
{
	Camera camera = viewport.GetCamera();
	if (IsLargeEnoughToRender(camera))
	{
		double4x4 matrixAsmb2Ego = GetMatrixAsmb2Ego(camera);
		Parts.UpdateRenderData(in matrixAsmb2Ego, IsEditedVehicle, viewport, inFrameIndex);
		if (viewport.IsMain() && Program.ShowPhysicsDebug && IsInSameBubbleAs(Program.ControlledVehicle) && _showTerrain)
		{
			SubmitTerrainDebugOverlay(viewport);
			SubmitClutterTerrainDebugOverlay(viewport);
		}
	}
}
```

## Lifecycle and timing

Both targets are called from `Program.UpdateRenderingResources(frameIndex)`, which `Program.Render` runs before `RenderGame` records commands: that is `frame:render`, not `frame:pre-render` (see rows 21 and 22 of [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)). Inside it, for each visible viewport:

| Order | Call ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4337-L4377)) | Your hook |
|---|---|---|
| 1 | `vehicle.UpdateRenderData(viewport, frameIndex)` for every non-kitten vehicle in frame | postfix on `Vehicle.UpdateRenderData` (cull redraw) |
| 2 | `Universe.UpdateRenderData` | |
| 3 | `PartModelRenderer.UpdateRenderData(viewport, frameIndex)` | prefix: after every vessel queued its parts, before the upload |
| 4 | `StaticObjectRenderer.UpdateRenderData` | |

The instances are drawn in `render:scene-opaque` (`PartModelRenderer.ColorData.WriteCommands`) and added to the cascaded shadow pass for the main view (`PartModelRenderer.DepthData.AddCommandsCSM`); see rows 6 and 9 of the RenderGame table. KSArmory installs both patches from `[StarMapAllModsLoaded]`. Simulation for the frame has already run, so positions you read are current; the camera for each call is `viewport.GetCamera()`.

## Steps

1. **Get the model.** Find the `PartModelModule.Template` among a part template's components and call `PartModel.Get`. KSArmory does this for the round's own part template, so the instance looks exactly like the real part:

   ```csharp
// src/KSArmory/Ksa/LauncherPart.cs L659-L669
private static PartModel? ModelOf(List<ModuleBase.TemplateDataBase>? components)
{
    if (components is null) return null;

    foreach (ModuleBase.TemplateDataBase component in components)
    {
        if (component is PartModelModule.Template template) return PartModel.Get(template);
    }

    return null;
}
```

2. **Prefix `PartModelRenderer.UpdateRenderData(IViewport, int)`.** It is `public static`, so resolve it by signature, fail soft, and never throw (you are inside the engine's render preparation):

   ```csharp
// src/KSArmory/Ksa/LooseBodyDrawHook.cs L35-L66
public static void Install(Action<IViewport, int> draw)
{
    _draw = draw;

    if (Installed) return;

    try
    {
        MethodInfo? target = typeof(PartModelRenderer).GetMethod(
            nameof(PartModelRenderer.UpdateRenderData), [typeof(IViewport), typeof(int)]);

        if (target is null)
        {
            Log.Warn("KSA has no PartModelRenderer.UpdateRenderData(IViewport, int) to hook; a round "
                     + "whose launcher is destroyed flies on without its body");
            return;
        }

        MethodInfo prefix = typeof(LooseBodyDrawHook).GetMethod(
            nameof(BeforeUpdateRenderData), BindingFlags.NonPublic | BindingFlags.Static)!;

        _harmony = new Harmony(HarmonyId);
        _harmony.Patch(target, prefix: new HarmonyMethod(prefix));

        Installed = true;
        Log.Info("rounds whose launcher is destroyed keep their bodies, via PartModelRenderer.UpdateRenderData");
    }
    catch (Exception e)
    {
        Log.Warn($"could not hook the loose-round draw; such rounds fly on without their bodies ({e.Message})");
    }
}
```

   The prefix body only calls your delegate; on the first exception it nulls the delegate and logs once:

   ```csharp
// src/KSArmory/Ksa/LooseBodyDrawHook.cs L85-L101
// Parameter names are Harmony's contract with the original: viewport and frameIndex.
private static void BeforeUpdateRenderData(IViewport viewport, int frameIndex)
{
    try
    {
        _draw?.Invoke(viewport, frameIndex);
    }
    catch (Exception e)
    {
        // Stand down rather than throw again next frame from inside the render preparation.
        _draw = null;

        if (_complained) return;
        _complained = true;
        Log.Error("the loose-round draw hook failed; such rounds fly on without their bodies", e);
    }
}
```

3. **Add the instance.** Build a matrix in the camera-relative frame of the viewport's camera and pack it into `PerInstanceData`:

   ```csharp
// src/KSArmory/Ksa/WeaponSystem.cs L3175-L3184
private static void AddInstance(PartModel? model, double3 scale, doubleQuat rotation, double3 ego,
                                IViewport viewport, int frameIndex)
{
    if (model is null) return;

    double4x4 matrix = double4x4.CreateScale(scale) * double4x4.CreateFromQuaternion(rotation)
                       * double4x4.CreateTranslation(ego);
    model.AddInstance(new PartModel.PerInstanceData { ModelMatrix = float4x4.Pack(in matrix) },
                      viewport, frameIndex);
}
```

4. **Place it with the same camera.** Each shell is positioned from the camera's view of its launcher (`camera.GetPositionEgo(platform)`) plus the round's offset from that launcher, both read now, so ecliptic motion cancels:

   ```csharp
// src/KSArmory/Ksa/WeaponSystem.cs L2169-L2186
public void DrawShellBodies(IViewport viewport, int frameIndex)
{
    if (_shellModel is null || !_config.UseRoundBodies || Platform is not { } platform
        || !KsaWorld.IsAlive(platform) || viewport.GetCamera() is not { } camera) return;

    double3 platformEgo = camera.GetPositionEgo(platform);
    double3 platformEcl = KsaWorld.PositionEcl(platform);

    foreach (IProjectile round in _rounds)
    {
        if (!RoundLabel.IsGunRound(round.Tube) || round.State != RoundState.Flying
            || !_drawnAttitudes.TryGetValue(round, out DrawnAttitude drawn)
            || !TryRoundEffectEcl(round, out double3 ecl)) continue;

        double3 ego = platformEgo + (ecl - platformEcl);
        if (Vec.IsFinite(ego)) AddInstance(_shellModel, double3.One, drawn.Ecl, ego, viewport, frameIndex);
    }
}
```

5. **Install from the mod entry point**, with the delegate that fans out to every system:

   ```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L187-L207
// A round's body is a part of its launcher, and KSA draws none of a craft under a pixel across.
// A refusal leaves that cull, so a long shot's body vanishes where it always did.
RoundBodyDrawHook.Install(craft => _roster?.HasRoundsInFlight(craft) == true);

LooseBodyDrawHook.Install((viewport, frameIndex) =>
{
    if (_roster is not { } roster) return;

    using (_budget.Measure("shell bodies"))
    {
        foreach (WeaponSystems.Entry e in roster.All) e.Weapon.DrawShellBodies(viewport, frameIndex);
    }

    if (roster.Loose.Count == 0) return;

    using (_budget.Measure("loose bodies"))
    {
        IReadOnlyList<WeaponSystem> loose = roster.Loose;
        for (int i = 0; i < loose.Count; i++) loose[i].DrawLooseBodies(viewport, frameIndex);
    }
});
```

6. **Optional: keep the launcher drawn when it is under a pixel.** A round that flies far away is part of the launcher; the launcher is culled by `Vehicle.UpdateRenderData` once it is under a pixel across. A postfix repeats the engine's own test and, when the vessel was culled and has rounds in flight, calls `Parts.UpdateRenderData` itself:

   ```csharp
// src/KSArmory/Ksa/RoundBodyDrawHook.cs L92-L106
// Parameter names are Harmony's contract with the original: viewport and inFrameIndex.
private static void AfterUpdateRenderData(Vehicle __instance, IViewport viewport, int inFrameIndex)
{
    try
    {
        if (_hasRoundsInFlight is not { } hasRounds || viewport.GetCamera() is not { } camera) return;

        // The engine's own test, on the same camera and the same craft: it drew the parts unless this
        // is under a pixel, so drawing them only then never draws them twice.
        double range = camera.GetPositionEgo(__instance).Length();
        double pixels = camera.GetObjectDiameterPixels(2.0 * __instance.MeanRadius, range);
        if (!(pixels < 1.0) || !hasRounds(__instance)) return;

        double4x4 asmb2Ego = __instance.GetMatrixAsmb2Ego(camera);
        __instance.Parts.UpdateRenderData(in asmb2Ego, __instance.IsEditedVehicle, viewport, inFrameIndex);
```

   Wrapped in the same try/catch and install-by-signature pattern as step 2 (see [src/KSArmory/Ksa/RoundBodyDrawHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/RoundBodyDrawHook.cs#L41-L73)). This part overlaps with [keep a vessel rendered at any distance](../keep-vessels-rendered-at-any-distance/overview.md), approach 3.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/LooseBodyDrawHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LooseBodyDrawHook.cs) | Prefix on `PartModelRenderer.UpdateRenderData`, delegate call, fault handling |
| [src/KSArmory/Ksa/RoundBodyDrawHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/RoundBodyDrawHook.cs) | Postfix on `Vehicle.UpdateRenderData` for the cull redraw |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L187-L207) | Installs both |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L2169-L2186) | Shells as instances |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L3175-L3184) | `AddInstance` helper |
| [src/KSArmory/Ksa/LauncherPart.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LauncherPart.cs#L628-L669) | `ModelOf`: a template's `PartModel` |

## Variations and gotchas

- **Per viewport, per frame.** The prefix runs for every visible viewport and the lists are cleared after submit. Add once and the mesh blinks for one frame.
- **Kittens hit the postfix inside command recording.** `KittenEva.UpdateRenderData` calls the base method from `RenderGame`/`RenderViewport`, outside the loop above, so the cull postfix runs there too. Return early when the vessel is not yours.
- **Editor.** With an editor open the loop draws the edited part trees instead of vessels, so neither hook sees flight craft.
- **A duplicated formula can drift.** Build 2026.10.10.5554 has a public `Vehicle.IsLargeEnoughToRender(Camera)`; the postfix duplicates its formula. Calling the method is safer if you can.
- **IVA with ray tracing** reroutes instances for templates with ray tracing enabled; `Internal` templates only show in IVA.
- **Pin the signature.** KSArmory keeps a never-called method that references `PartModelRenderer.UpdateRenderData`, so a signature change is a compile error instead of a patch that silently stops applying.
- **Cost.** One draw per distinct model regardless of instance count; the per-instance upload is a `PerInstanceData` (one `float4x4`).

## See also

- [Overview](overview.md)
- [Approach 1: StaticMeshRenderable](draw-mod-meshes-with-the-engine-renderers-1.md), [approach 2](draw-mod-meshes-with-the-engine-renderers-2.md), [approach 4](draw-mod-meshes-with-the-engine-renderers-4.md)
- [Round bodies drawn past the engine's pixel cull and as part-model instances](../../../../mods/ksarmory/features/round-body-draw-hooks.md), [Rounds as hidden launcher subparts](../../../../mods/ksarmory/features/rounds-as-hidden-launcher-subparts.md)
- [Keep a vessel or kitten rendered at any distance](../keep-vessels-rendered-at-any-distance/overview.md), [Hide part meshes or subparts without removing them](../hide-parts-or-subparts-from-rendering/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
