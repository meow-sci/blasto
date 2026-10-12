---
type: How-To
title: Reproduce the original body for marked objects and return false
description: Replace Vehicle.GetWorldMatrix and Vehicle.UpdateRenderData for chosen vessels with a prefix that rebuilds the stock body minus the sub-pixel cull and skips the original, while every other vessel falls through
category: patching
group: harmony
topic: skip-the-original-method-with-a-prefix
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/gatos/features/scale-and-force-render-cheats]
techniques: [harmony-prefix, dynamic-harmony-install]
hooks: [harmony-prefix KSA.Vehicle.GetWorldMatrix, harmony-prefix KSA.Vehicle.UpdateRenderData]
lifecycle: [frame:render]
game_types: [KSA.Vehicle, KSA.Camera, KSA.PartTree, KSA.KittenEva]
tags: [harmony, prefix, skip-original, rendering, culling, vehicle]
---

KSA stops drawing a vehicle once its projected diameter drops under one pixel. gatOS keeps chosen
vessels drawn at any distance by prefixing the two methods that make that decision. For a marked
vessel the prefix rebuilds what the stock method would have returned, minus the size test, writes it
to `__result` where the method returns a value, and returns `false` so the original never runs. For
every other vessel the prefix returns `true` and stock code runs untouched. This is the full "replace
a method for some callers" shape of a skipping prefix.

Prerequisites: a StarMap mod that references `0Harmony` (the getting-started topic
create-a-minimal-starmap-mod covers the skeleton). Demonstrated by gatOS in
[Per-vessel scale and force-render cheats](../../../../mods/gatos/features/scale-and-force-render-cheats.md).

## Background: how the game does it

`Vehicle` (build 2026.10.10.5554) holds the cull in two places that both compute the same number, the
on-screen diameter of the vehicle's mean-radius sphere:

```csharp
// current/decomp/KSA/Vehicle.cs L3694-L3726
public float4x4? GetWorldMatrix(Camera camera)
{
	double3 vector = camera.GetPositionEgo(this);
	double objectDiameterPixels = camera.GetObjectDiameterPixels(2.0 * MeanRadius, vector.Length());
	if (objectDiameterPixels < 1.0)
	{
		return null;
	}
	float4x4 float4x5 = float4x4.CreateTranslation(float3.Pack(in vector));
	float4x4 float4x6 = float4x4.CreateFromQuaternion(floatQuat.Pack(Body2Cce));
	return float4x6 * float4x5;
}

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

Source: [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3694-L3726)[^ksa-vehicle]

- `GetWorldMatrix(Camera)` returns `null` when the diameter is under one pixel; otherwise it returns
  `rotation * translation` built from `Body2Cce` and the camera-relative position.
- `IsLargeEnoughToRender(Camera)` is the same test as a `bool`, and `UpdateRenderData(IViewport, int)`
  skips `Parts.UpdateRenderData` entirely when it is false.
- Callers of `GetWorldMatrix` in this build are `KittenEva.UpdateRenderData` and the PBR spheres debug view;
  the only caller of `IsLargeEnoughToRender` is `Vehicle.UpdateRenderData` itself
  ([current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L1062-L1065)[^ksa-kitteneva], [current/decomp/KSA.Rendering.PBR/PbrSpheres.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.PBR/PbrSpheres.cs#L76-L82)[^ksa-pbrspheres]).

gatOS ported its two prefixes from the unscience `i-feel-seen` mod. Its source comments record that
`IsLargeEnoughToRender` appeared as a helper in build 5482 and that the prefixes still replace the
whole bodies, so the helper is simply bypassed ([gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs#L114-L123)[^gatos-vesselforcerender]).

## Lifecycle and timing

Both prefixes run in `frame:render`, on the main thread, as `Program.Render` fills this frame's GPU
resources and records commands. Non-EVA vehicles reach `Vehicle.UpdateRenderData` from
`Program.UpdateRenderingResources`, which `Render` calls just before `RenderGame`
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2467-L2473)[^ksa-program], [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4337-L4358)). EVA kittens are skipped there and updated
inside `RenderViewport` and `RenderGame` instead ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4483-L4495),
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4690-L4702)); `KittenEva.UpdateRenderData` calls `base.UpdateRenderData` first, so it
runs through the patched `Vehicle` body too. gatOS's remarks state that the prefixes read one
`volatile` immutable set and are therefore safe on any thread; in this build both methods are reached
from the render path described above ([gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs#L14-L32)).
See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the surrounding frame.

The patch itself is installed and removed from the frame command drain, also on the main thread, so
no render code is mid-call while Harmony swaps the method body (the lazy install is the subject of
[Lazy install and removal of one patch while it is in use](../../safety/remove-your-patches-safely-at-runtime-and-at-exit/remove-your-patches-safely-at-runtime-and-at-exit-2.md)).

## Steps

1. **Pick the methods and read their stock bodies.** Open `Vehicle.cs` in the decompiled game
   (above). Decide what the replacement must reproduce: here the world matrix for `GetWorldMatrix`,
   the `Parts.UpdateRenderData(...)` call for `UpdateRenderData`. The stock `UpdateRenderData` also
   submits two debug terrain overlays when `Program.ShowPhysicsDebug` is on; gatOS deliberately does not
   reproduce them.

2. **Keep the decision data volatile and immutable.** The mod publishes a fresh `HashSet<string>` of
   marked vessel ids on every change and the prefixes read the reference once:

```csharp
// gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs L35-L41, L112
private static readonly HashSet<string> Marked = new(StringComparer.Ordinal);

/// <summary>The immutable snapshot the prefixes read; null while nothing is marked.</summary>
private static volatile HashSet<string>? _published;

private static Harmony? _harmony;
private static bool _prefixFaultLogged;
// ...
private static void Publish() => _published = Marked.Count == 0 ? null : new HashSet<string>(Marked, StringComparer.Ordinal);
```

Source: [gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs#L35-L112)

3. **Write the `GetWorldMatrix` prefix.** The signature mirrors the original: `__instance` for the
   vessel, the original's parameter name `camera` (Harmony binds injected arguments by name), and
   `ref float4x4? __result` for the return value. A marked vessel gets the same matrix expression
   as stock without the `< 1.0` test; the prefix writes it and returns `false`. Anything else returns
   `true`. An exception returns `true` too, so a fault falls back to stock culling:

```csharp
// gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs L189-L207
private static bool GetWorldMatrixPrefix(Vehicle __instance, KsaCamera camera, ref float4x4? __result)
{
    try
    {
        var marked = _published;
        if (marked is null || !marked.Contains(__instance.Id))
            return true;

        var positionEgo = camera.GetPositionEgo(__instance);
        __result = float4x4.CreateFromQuaternion(floatQuat.Pack(__instance.Body2Cce))
                   * float4x4.CreateTranslation(float3.Pack(in positionEgo));
        return false;
    }
    catch (Exception ex)
    {
        LogPrefixFaultOnce(ex);
        return true;
    }
}
```

Source: [gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs#L189-L207)

4. **Write the `UpdateRenderData` prefix.** The original has no return value, so skipping means doing
   the useful work yourself. The prefix calls the same `PartTree.UpdateRenderData` the stock method
   calls after its size test:

```csharp
// gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs L224-L242
private static bool UpdateRenderDataPrefix(Vehicle __instance, IViewport viewport, int inFrameIndex)
{
    try
    {
        var marked = _published;
        if (marked is null || !marked.Contains(__instance.Id))
            return true;

        var matrixAsmb2Ego = __instance.GetMatrixAsmb2Ego(viewport.GetCamera());
        __instance.Parts.UpdateRenderData(in matrixAsmb2Ego, __instance.IsEditedVehicle,
            viewport, inFrameIndex);
        return false;
    }
    catch (Exception ex)
    {
        LogPrefixFaultOnce(ex);
        return true;
    }
}
```

Source: [gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs#L224-L242)

5. **Attach the prefixes by exact signature.** `AccessTools.Method` with an explicit parameter array
   picks `GetWorldMatrix(Camera)` and `UpdateRenderData(IViewport, int)`; a missing method throws, and a
   failure between the two `Patch` calls rolls the pair back so no half-installed state leaks:

```csharp
// gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs L128-L159
private static void InstallPatches()
{
    if (_harmony is not null)
        return;

    var getWorldMatrix = AccessTools.Method(typeof(Vehicle), nameof(Vehicle.GetWorldMatrix),
        [typeof(KsaCamera)]);
    var updateRenderData = AccessTools.Method(typeof(Vehicle), nameof(Vehicle.UpdateRenderData),
        [typeof(IViewport), typeof(int)]);
    if (getWorldMatrix is null || updateRenderData is null)
        throw new MissingMethodException(
            "Vehicle.GetWorldMatrix/UpdateRenderData not found in this build");

    var harmony = new Harmony("gatos.always_render");
    try
    {
        harmony.Patch(getWorldMatrix, prefix: new HarmonyMethod(
            typeof(VesselForceRender).GetMethod(nameof(GetWorldMatrixPrefix),
                BindingFlags.NonPublic | BindingFlags.Static)!));
        harmony.Patch(updateRenderData, prefix: new HarmonyMethod(
            typeof(VesselForceRender).GetMethod(nameof(UpdateRenderDataPrefix),
                BindingFlags.NonPublic | BindingFlags.Static)!));
    }
    catch
    {
        harmony.UnpatchAll("gatos.always_render"); // don't leak a half-installed pair
        throw;
    }

    _harmony = harmony;
    ModLog.Log.Info("gatOS vessel force-render patches installed.");
}
```

Source: [gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs#L128-L159)

6. **What to expect.** After the first vessel is marked the log shows `gatOS vessel force-render
   patches installed.` The marked vessel now draws at any distance; unmarked vessels still disappear
   below one pixel.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs) | Mark registry, lazy install, both prefixes, fault logging |
| [gatOS.GameMod/Game/Ksa/Actuators/ScaleActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/ScaleActuator.cs)[^gatos-scaleactuator] | The sibling `scale` cheat (no patch, writes `Part.Scale`) |

## Variations and gotchas

- **A faithful replacement is a maintenance debt.** The prefix reproduces a method body. When a build
  changes the original (gatOS records that `UpdateRenderData` gained `IsLargeEnoughToRender`, a
  debug-only clutter overlay and an `IViewport` parameter across 5402 to 5482) the copy silently keeps
  the old behaviour. Keep the reproduced code short, and re-read the stock method after every game
  update.
- **Fall through on any fault.** Both prefixes catch, log once and return `true`; a throwing prefix on
  a per-frame render path would otherwise break rendering for every vessel.
- **`UpdateRenderData` is virtual.** The patch binds the `Vehicle` body. An override reaches it only if
  it calls `base`; `KittenEva` does (`base.UpdateRenderData` is its first statement).
- **A postfix can be enough for this particular problem.** Because the stock `GetWorldMatrix` returns
  `null` exactly when the cull fires, a postfix could compute the matrix only when `__result` is `null`
  and the vessel is marked, and stock code would run for everything else with nothing to reproduce:

```csharp
// adapted from gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs (untested sketch)
private static void GetWorldMatrixPostfix(Vehicle __instance, KsaCamera camera, ref float4x4? __result)
{
    if (__result.HasValue || _published?.Contains(__instance.Id) != true)
        return;
    var positionEgo = camera.GetPositionEgo(__instance);
    __result = float4x4.CreateFromQuaternion(floatQuat.Pack(__instance.Body2Cce))
               * float4x4.CreateTranslation(float3.Pack(in positionEgo));
}
```

  The trade-off, spelled out in [Skipping Harmony prefixes](overview.md): a postfix always
  pays for the original, but it cannot suppress an exception, a side effect or a cost that the
  original has before the value it overwrites.
- **Other mods.** Once this prefix returns `false`, Harmony skips the original and every later prefix
  that returns `bool`; `void` prefixes still run (library behaviour observed in a local test, see the overview); postfixes and finalizers still run. See [Coexisting with other mods that patch the same method](../coexist-with-other-mods-patching-the-same-method/overview.md).

## See also

- [Rewrite by-ref arguments and skip when a limit would throw](skip-the-original-method-with-a-prefix-2.md), [Re-inject a private field before a rebuild so nothing can change it back](skip-the-original-method-with-a-prefix-3.md), [Mouse prefix that pairs presses with releases and repairs private latches](skip-the-original-method-with-a-prefix-4.md), [Keyboard and mouse-binding gates with a held-key model](skip-the-original-method-with-a-prefix-5.md): the other skipping-prefix shapes
- [Skipping Harmony prefixes](overview.md): overview and comparison
- [Per-vessel scale and force-render cheats](../../../../mods/gatos/features/scale-and-force-render-cheats.md): the mod feature page
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for frame timing; [Patching private and overloaded game methods by name](../../internals/patch-a-private-game-method-by-name/overview.md) for attaching to private
  targets; the topic keep-vessels-rendered-at-any-distance applies this technique to a task

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-kitteneva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
[^ksa-pbrspheres]: KSA.Rendering.PBR.PbrSpheres (decompiled, build 2026.10.10.5554)
[^gatos-vesselforcerender]: gatOS/gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^gatos-scaleactuator]: gatOS/gatOS.GameMod/Game/Ksa/Actuators/ScaleActuator.cs
