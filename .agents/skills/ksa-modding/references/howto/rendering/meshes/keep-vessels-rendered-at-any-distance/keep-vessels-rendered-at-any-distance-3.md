---
type: How-To
title: Postfix Vehicle.UpdateRenderData and ask the pixel test again for a launcher with rounds in flight
description: Draw a culled vessel's parts from a postfix that repeats the engine's one-pixel test and renders the part tree only when the engine did not, so a distant launcher keeps its projectiles visible
category: rendering
group: meshes
topic: keep-vessels-rendered-at-any-distance
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksarmory/features/round-body-draw-hooks]
techniques: [harmony-postfix]
hooks: [starmap StarMapAllModsLoaded, harmony-postfix KSA.Vehicle.UpdateRenderData]
lifecycle: [startup:all-mods-loaded, frame:render]
game_types: [KSA.Vehicle, KSA.PartTree, KSA.Camera, KSA.Program]
tags: [cull, render-distance, postfix, projectile, launcher]
---
Outcome: a launcher whose projectile bodies are subparts stays drawn once it falls under a pixel across, but only while it has rounds in flight. The stock path runs untouched; the postfix adds the parts exactly when the engine skipped them, so nothing is drawn twice and no other vessel is affected.

Demonstrated by KSArmory ([Round bodies drawn past the engine's pixel cull and as part-model instances](../../../../mods/ksarmory/features/round-body-draw-hooks.md)). Prerequisites: StarMap, Harmony. Compared to approach 1, nothing is skipped or re-implemented: the postfix uses the engine's own public members.

## Background: how the game does it

`Vehicle.UpdateRenderData` draws a vessel's parts only when `IsLargeEnoughToRender(camera)`, which is `!(camera.GetObjectDiameterPixels(2 * MeanRadius, range) < 1)`. A projectile that is a subpart of its launcher therefore vanishes with the launcher once the launcher shrinks under a pixel (the mod's example: a chase camera 3.7 m behind a 5"/54 shell loses the shell part-way down a 60 km shot).

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

The postfix runs from `Program.UpdateRenderingResources(frameIndex)`, once per vehicle per visible viewport, at the point where `vehicle.UpdateRenderData(viewport, frameIndex)` is called ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4347-L4357)). That is `frame:render`, before `RenderGame` records commands, and before `PartModelRenderer.UpdateRenderData` uploads the instance lists, so the parts it adds are uploaded this frame. KSArmory installs the patch from `[StarMapAllModsLoaded]` with its own Harmony id; a failed install logs and leaves the engine's behaviour. `KittenEva.UpdateRenderData` calls the base method from `RenderGame`/`RenderViewport` outside that loop, so the postfix also runs there; it returns at the predicate because a kitten has no rounds.

## Steps

1. **Install the postfix by exact signature**, fail soft, and keep the callback that says whether the vessel has rounds in flight:

   ```csharp
// src/KSArmory/Ksa/RoundBodyDrawHook.cs L41-L73
public static void Install(Func<Vehicle, bool> hasRoundsInFlight)
{
    _hasRoundsInFlight = hasRoundsInFlight;

    if (Installed) return;

    try
    {
        MethodInfo? target = typeof(Vehicle).GetMethod(
            nameof(Vehicle.UpdateRenderData), [typeof(IViewport), typeof(int)]);

        if (target is null)
        {
            Log.Warn("KSA has no Vehicle.UpdateRenderData(IViewport, int) to hook; a round's body "
                     + "vanishes with its launcher once the launcher is under a pixel across");
            return;
        }

        MethodInfo postfix = typeof(RoundBodyDrawHook).GetMethod(
            nameof(AfterUpdateRenderData), BindingFlags.NonPublic | BindingFlags.Static)!;

        _harmony = new Harmony(HarmonyId);
        _harmony.Patch(target, postfix: new HarmonyMethod(postfix));

        Installed = true;
        Log.Info("round bodies drawn past their launcher's cull, via Vehicle.UpdateRenderData");
    }
    catch (Exception e)
    {
        Log.Warn($"could not hook the round-body draw; a round's body vanishes with its launcher once "
                 + $"the launcher is under a pixel across ({e.Message})");
    }
}
```

2. **Repeat the engine's test in the postfix and draw only if it culled.** Same camera, same vessel: `pixels < 1` means the engine skipped the parts, so drawing them then can never draw them twice:

   ```csharp
// src/KSArmory/Ksa/RoundBodyDrawHook.cs L92-L112
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

        if (_announced) return;
        _announced = true;
        Log.Info($"{KsaWorld.DisplayName(__instance)} is {pixels:F2} px across {range / 1000.0:F1} km from "
                 + "the camera, which KSA does not draw; drawing it anyway for the rounds it has in the air");
    }
```

   In this build `Vehicle.IsLargeEnoughToRender(Camera)` is public; calling `!__instance.IsLargeEnoughToRender(camera)` is shorter and cannot drift from the engine's formula, at the cost of depending on a newer public member.

3. **Stand down on any exception** so a bug cannot throw again every frame from inside the engine's render preparation:

   ```csharp
// src/KSArmory/Ksa/RoundBodyDrawHook.cs L113-L122
    catch (Exception e)
    {
        // Stand down rather than throw again next frame from inside the render preparation.
        _hasRoundsInFlight = null;

        if (_complained) return;
        _complained = true;
        Log.Error("the round-body draw hook failed; rounds vanish with a culled launcher again", e);
    }
}
```

4. **Install from the mod entry point** with the predicate:

   ```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L187-L189
// A round's body is a part of its launcher, and KSA draws none of a craft under a pixel across.
// A refusal leaves that cull, so a long shot's body vanishes where it always did.
RoundBodyDrawHook.Install(craft => _roster?.HasRoundsInFlight(craft) == true);
```

5. **Pin the signature.** A never-called method that references `Vehicle.UpdateRenderData` puts it in the assembly's metadata, so a KSA signature change is a build error rather than a patch that silently stops applying:

   ```csharp
// src/KSArmory/Ksa/RoundBodyDrawHook.cs L124-L127
// Never called. It puts the patched method in this assembly's metadata, so docs/KSA-API-SURFACE.md
// tracks it and a KSA signature change is a build error here.
private static void PinTheSignature(Vehicle vehicle, IViewport viewport, int frameIndex)
    => vehicle.UpdateRenderData(viewport, frameIndex);
```

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/RoundBodyDrawHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/RoundBodyDrawHook.cs) | Install, postfix, fault handling, signature pin |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L187-L189) | Installs it with the roster predicate |

## Variations and gotchas

- **Drawing the whole sub-pixel launcher** costs nothing visible; the draw is limited to vessels with rounds in flight to avoid it elsewhere.
- **Editor frames** call neither path for flight craft: with an editor open the loop draws the edited part trees instead.
- **Formula drift.** If the postfix duplicates the formula and KSA changes the threshold, parts are drawn twice or not at all; prefer the public helper.
- **Not an enlarger.** A sub-pixel vessel is still sub-pixel. For projectiles that need to be seen, draw them as separate part-model instances instead ([draw-your-own-meshes, approach 3](../draw-mod-meshes-with-the-engine-renderers/draw-mod-meshes-with-the-engine-renderers-3.md)).

## See also

- [Overview](overview.md)
- [Approach 1](keep-vessels-rendered-at-any-distance-1.md), [approach 2](keep-vessels-rendered-at-any-distance-2.md), [approach 4](keep-vessels-rendered-at-any-distance-4.md)
- [Round bodies drawn past the engine's pixel cull and as part-model instances](../../../../mods/ksarmory/features/round-body-draw-hooks.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
