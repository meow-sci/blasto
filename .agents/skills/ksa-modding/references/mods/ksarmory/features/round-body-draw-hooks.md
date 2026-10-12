---
type: Mod Feature
title: Round bodies drawn past the engine's pixel cull and as part-model instances
description: A postfix on Vehicle.UpdateRenderData redraws a sub-pixel launcher while its rounds fly, and a prefix on PartModelRenderer.UpdateRenderData adds gun shells and the rounds of a destroyed launcher as extra instances of the engine's own part meshes, per viewport
mod: ksarmory
category: rendering
techniques: [harmony-postfix, harmony-prefix, part-model-instancing]
hooks: [harmony-postfix KSA.Vehicle.UpdateRenderData, harmony-prefix KSA.PartModelRenderer.UpdateRenderData]
lifecycle: [startup:all-mods-loaded, frame:render]
game_types:
  - KSA.Vehicle
  - KSA.PartModelRenderer
  - KSA.PartModel
  - KSA.PartTree
  - KSA.Camera
  - KSA.Program
  - KSA.KittenEva
tags: [part-model, instancing, culling, render-data, harmony, viewport, subparts]
---

KSArmory's missiles are real meshes: each round's body is a hidden subpart of its launcher that
the mod moves every frame, so KSA draws it with the part shader, lighting and shadows. Two engine
behaviours break that picture, and two small render-data patches fix them:

- **The pixel cull.** KSA skips every part of a craft whose bounding sphere is under one pixel
  across. A round kilometres downrange is part of the launcher, so it vanished with it (the mod's
  example: a chase camera 3.7 m behind a 5"/54 shell lost the shell part-way down a 60 km
  shot).[^ksarmory-roundbody] The postfix asks the engine's own question again and draws the
  launcher's parts exactly when the engine did not.
- **The missing subpart.** When a launcher is destroyed its parts go with it, and a gun can have
  more shells in the air than its part declares subparts. `PartModel` (the mesh a part template
  draws with) is shared and outlives any craft, and `PartModel.AddInstance` draws it at any matrix.
  The prefix queues those rounds as extra instances, with no subpart at all.[^ksarmory-loosebody]

## How it works

### The cull redraw (`RoundBodyDrawHook`)

| Step | Detail |
|---|---|
| Engine test | `Vehicle.UpdateRenderData(viewport, inFrameIndex)`: if `IsLargeEnoughToRender(camera)`, build `GetMatrixAsmb2Ego(camera)` and call `Parts.UpdateRenderData(...)`[^ksa-vehicle] |
| `IsLargeEnoughToRender` | `!(camera.GetObjectDiameterPixels(2 * MeanRadius, GetPositionEgo(this).Length()) < 1)`; `MeanRadius` is the body-frame bounding sphere radius; `GetObjectDiameterPixels` is the camera's projection of a size at a range[^ksa-camera] |
| Postfix | Same camera, same formula; continue only if under a pixel **and** `hasRoundsInFlight(craft)` |
| Redraw | `__instance.Parts.UpdateRenderData(in asmb2Ego, __instance.IsEditedVehicle, viewport, inFrameIndex)`: the whole sub-pixel launcher is drawn too, which costs nothing visible |
| Predicate | `craft => _roster?.HasRoundsInFlight(craft) == true`, null after unload so a stale patch does nothing |

### Instances with no subpart (`LooseBodyDrawHook` + `WeaponSystem`)

| Piece | Detail |
|---|---|
| Prefix body | Calls the installed `Action<IViewport, int>`: every system's `DrawShellBodies`, then every loose (launcher-destroyed) system's `DrawLooseBodies`, each timed in the mod's frame budget[^ksarmory-mod] |
| `DrawShellBodies` | Every flying gun shell, even with the launcher alive, as an instance of the munition's shell model; position `camera.GetPositionEgo(platform) + (TryRoundEffectEcl(round) - PositionEcl(platform))`, both read now so ecliptic motion cancels; attitude from `TurnShellBodies` in the step[^ksarmory-weaponsystem] |
| `CaptureLooseBodies` | Before the launcher's parts are released: records each round's body, fin blades and fin set as `PartModel`s via `LauncherPart.ModelOf` (the template's `PartModelModule.Template` passed to `PartModel.Get`), then hides the subparts so debris does not carry a second copy[^ksarmory-launcherpart] |
| `DrawLooseBodies` | `camera.GetPositionEgo(body) + round.OffsetFromPlatform` for each flying round; body, each blade rolled about the axis and moved to the hinge station, fin set scaled by deployment |
| `AddInstance` | `Scale * FromQuaternion(rotation) * Translation(ego)`, packed to `float4x4`, into `PartModel.AddInstance(new PerInstanceData { ModelMatrix }, viewport, frameIndex)` |

What the engine does with an instance (build 5554):[^ksa-partmodel]

- `PartModel.AddInstance` returns at once if the viewport lacks `RenderPartModels`; in IVA with
  ray tracing it routes to the RT renderer instead; otherwise it appends to this model's
  per-viewport `InstanceList` (an `Internal` template only in IVA).
- `PartModelRenderer.UpdateRenderData(viewport, frameIndex)` then runs
  `PartModel.WriteInstancesToGpu` for every model: one indirect draw per model with
  `InstanceCount` = list length, per-draw material handles, per-instance data, and shadow-cull
  sources when the template is a shadow caster.[^ksa-partmodelrenderer]
- The lists are cleared in `PostRender` through `ClearFrameData` (L4440 calls
  `PartModelRenderer.ClearFrameData`), so instances must be re-added every frame.[^ksa-program]

## Lifecycle and timing

Both patches are installed by hand in `OnFullyLoaded` (`[StarMapAllModsLoaded]`, KSArmoryMod
L187-L207) with their own Harmony ids; a failed install logs and leaves the engine's behaviour.

Both targets are called from `Program.UpdateRenderingResources(frameIndex)`, which
`Program.Render` calls at L2468, after `OnPreRender` and the swapchain acquire and before
`RenderGame` records commands (L2473).[^ksa-program] That is `frame:render`, not `frame:pre-render`:
the [game loop reference](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence) (row 22) and the conventions put
`UpdateRenderingResources` inside `Render`, after `OnPreRender` has returned. Inside it:

| Line | Call | Notes |
|---|---|---|
| L4339-L4345 | `for each viewport in ViewportRegistry.Views`, skip if not `Visible` | Main, the four camera windows, portraits: once per visible viewport |
| L4347-L4357 | `vehicle.UpdateRenderData(viewport2, frameIndex)` for every `VehiclesInFrame` that is not a `KittenEva` (only when `Editor == null`) | **Postfix** runs here, per craft per viewport |
| L4371 | `Universe.UpdateRenderData` | |
| L4374 | `PartModelRenderer.UpdateRenderData(viewport2, frameIndex)` | **Prefix** runs here, after every craft queued its parts and before upload |
| L4829 / L4560 | `PartModelRenderer.ColorData.WriteCommands` in `RenderGame` / `RenderViewport` | The instances are drawn in `render:scene-opaque`; L4762 adds them to cascaded shadows |

State: GPU buffers for this frame index are being filled on the main thread; the simulation step
for the frame (from the GUI hook or the `OnFrameCelestials` prefix) has already run, so round
offsets and drawn attitudes are current. The camera for each call is `viewport.GetCamera()`, so
every window places the bodies with its own camera.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/RoundBodyDrawHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/RoundBodyDrawHook.cs) | Postfix that redraws a culled launcher |
| [src/KSArmory/Ksa/LooseBodyDrawHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LooseBodyDrawHook.cs) | Prefix that runs the instance callback |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L187-L207) | Installs both with their callbacks |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L2132-L2186) | `TurnShellBodies`, `DrawShellBodies` |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L3083-L3184) | `CaptureLooseBodies`, `TurnLooseBodies`, `DrawLooseBodies`, `AddInstance` |
| [src/KSArmory/Ksa/LauncherPart.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LauncherPart.cs#L628-L669) | `ModelOf`: a part's or template's `PartModel` |

## Snippets

```csharp
// src/KSArmory/Ksa/RoundBodyDrawHook.cs L92-L122
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
        // ...
    }
    catch (Exception e)
    {
        // Stand down rather than throw again next frame from inside the render preparation.
        _hasRoundsInFlight = null;
        // ...
    }
}
```

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
        // ...
    }
}
```

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

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Vehicle.UpdateRenderData(IViewport, int)` (public virtual) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3713-L3726) | Postfix target |
| `Vehicle.IsLargeEnoughToRender`, `MeanRadius`, `GetMatrixAsmb2Ego(Camera)`, `Parts`, `IsEditedVehicle` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3707-L3711) | The cull test and the redraw |
| `Camera.GetObjectDiameterPixels`, `GetPositionEgo` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L807-L812) | Pixel size; camera-relative placement |
| `PartModelRenderer.UpdateRenderData(IViewport, int)` (public static) | [current/decomp/KSA/PartModelRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelRenderer.cs#L807-L827) | Prefix target: just before upload |
| `PartModel.AddInstance(PerInstanceData, IViewport, int)`, `PartModel.Get(PartModelModule.Template)` | [current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L417-L491) | A body with no subpart |
| `Program.UpdateRenderingResources` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4308-L4400) | The caller of both targets |

## Pitfalls and version notes

- **Kittens hit the postfix inside command recording.** `KittenEva.UpdateRenderData` calls
  `base.UpdateRenderData`,[^ksa-kitteneva] and kittens are updated from `RenderGame` (L4698) and
  `RenderViewport` (L4491), not from the loop above. The postfix runs there too and returns at the
  predicate, since a kitten has no rounds.
- **Editor frames call neither path for flight craft**: with `Editor != null` the loop draws the
  edited part trees instead, so no rounds are drawn (none fly in the editor anyway).
- **A helper now exists.** Build 5554 has public `Vehicle.IsLargeEnoughToRender(Camera)`; the
  postfix duplicates the formula. If KSA changes the threshold, the copy drifts and parts are either
  drawn twice or not at all.
- **Instances are per viewport and per frame.** Adding once is not enough; the prefix runs for
  every visible viewport and the lists are cleared after submit.
- **IVA with ray tracing reroutes instances** into the RT renderer for RT-enabled templates.
- **Shell rendering depends on the hook.** `ShellDrawnAsBody` is true only when
  `LooseBodyDrawHook.Installed` and a shell model exists; otherwise the gizmo streak draws shells.
- **Never throw.** Both patches catch, null their callback and log once: an exception here is inside
  the engine's render preparation.

## See also

- [Rounds as hidden launcher subparts](./rounds-as-hidden-launcher-subparts.md)
- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [World-anchored overlays through KSA's gizmo renderer](./gizmo-world-overlays.md)
- [Chase camera on a round](./chase-camera-on-a-round.md)
- [Frames, epochs and the draw anchor](./ecliptic-frames-and-epochs.md)
- [API surface tracking](./api-surface-tracking.md)
- [Add extra part-model instances at any matrix, per viewport](../../../howto/rendering/meshes/draw-mod-meshes-with-the-engine-renderers/draw-mod-meshes-with-the-engine-renderers-3.md) - how-to article demonstrating this feature
- [Postfix Vehicle.UpdateRenderData and ask the pixel test again for a launcher with rounds in flight](../../../howto/rendering/meshes/keep-vessels-rendered-at-any-distance/keep-vessels-rendered-at-any-distance-3.md) - how-to article demonstrating this feature

[^ksarmory-roundbody]: src/KSArmory/Ksa/RoundBodyDrawHook.cs
[^ksarmory-loosebody]: src/KSArmory/Ksa/LooseBodyDrawHook.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-launcherpart]: src/KSArmory/Ksa/LauncherPart.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-partmodelrenderer]: KSA.PartModelRenderer (decompiled, build 2026.10.10.5554)
[^ksa-partmodel]: KSA.PartModel (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-kitteneva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
