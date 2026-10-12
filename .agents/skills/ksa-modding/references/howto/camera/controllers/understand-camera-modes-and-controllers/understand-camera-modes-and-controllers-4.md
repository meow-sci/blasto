---
type: How-To
title: Run per-frame camera work from a prefix on the main viewport's GameViewport.OnFrame
description: A Harmony prefix and postfix on GameViewport.OnFrame filtered by reference to Program.MainViewport puts sampling, command draining and camera preparation inside the viewport pass
category: camera
group: controllers
topic: understand-camera-modes-and-controllers
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/gatos/features/programmable-camera-director]
techniques: [harmony-prefix, harmony-postfix]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, harmony-postfix KSA.GameViewport.OnFrame]
lifecycle: [frame:viewports]
game_types: [KSA.GameViewport, KSA.ViewportBase, KSA.Program, KSA.ViewportRegistry, KSA.FixedController]
tags: [camera, harmony, viewport, main-viewport, gatos]
---
gatOS lets a guest program own the player's camera. Its per-frame work (sampling telemetry, ticking
schedules, draining commands, preparing the pose) runs from a Harmony prefix on `GameViewport.OnFrame`,
and a postfix publishes the final clamped transform. The prefix must act only for the **main**
viewport, which is this approach's answer to **Which camera is the main one?** Prerequisites:
StarMap and Harmony.

## Background: how the game does it

`GameViewport.OnFrame(double)` runs the active controller, then `Camera.OnFrame`, then audio for the
viewport that owns it ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L124)), and `Program.OnFrameViewports` calls it for every
viewport in `ViewportRegistry.Views` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2608-L2625)). `ViewportBase.OnFrame` is abstract and only
`GameViewport` overrides it for game viewports; the part thumbnail viewport has its own override, so a patch
on `GameViewport.OnFrame` never fires for it. gatOS' own anchor note counts eight registered viewports
(main, thumbnail, four secondary, two portraits), the same layout as [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L948-L962).

## Lifecycle and timing

This is `frame:viewports`: the prefix runs after `PrepareFrame` applied the solvers and before the
controller and `Camera.OnFrame` of the same viewport, so a write made by the controller afterwards is in
phase with the matrix build. The postfix runs after both and sees the final, ground-clamped transform
(`Camera.ClampCamera` runs at the top of `Camera.OnFrame`, [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L487-L497)). It runs every rendered
frame, including with the HUD hidden, because `OnFrameViewports` is outside the `DrawUI` block
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2260-L2272)). The state it touches is main-thread only; gatOS' transports enqueue commands
and read a volatile status.

## Steps

1. **Resolve and patch `GameViewport.OnFrame(double)` with an explicit parameter list**, since it is the only overload:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraViewportPatch.cs L36-L50
internal static bool Install(Harmony harmony)
{
    var original = AccessTools.Method(typeof(GameViewport), nameof(GameViewport.OnFrame), [typeof(double)]);
    if (original is null)
    {
        ModLog.Log.Warn("Camera hook: GameViewport.OnFrame(double) not found; programmable camera disabled.");
        return false;
    }

    _faulted = false;
    harmony.Patch(original,
        prefix: new HarmonyMethod(typeof(CameraViewportPatch), nameof(Prefix)),
        postfix: new HarmonyMethod(typeof(CameraViewportPatch), nameof(Postfix)));
    return true;
}
```

2. **Filter to the main viewport by reference** and swallow every exception, disabling the feature
   after the first fault (an exception inside the viewport pass would end the process):

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraViewportPatch.cs L52-L80
private static void Prefix(GameViewport __instance, double dt)
{
    if (_faulted || !ReferenceEquals(__instance, Program.MainViewport))
        return;
    try
    {
        Mod.PrepareMainViewportFrame(dt);
    }
    catch (Exception ex)
    {
        _faulted = true;
        ModLog.Log.Error($"gatOS camera viewport prefix disabled after an error: {ex.Message}");
    }
}

private static void Postfix(GameViewport __instance)
{
    if (_faulted || !ReferenceEquals(__instance, Program.MainViewport))
        return;
    try
    {
        Mod.PublishMainViewportCameraStatus();
    }
    catch (Exception ex)
    {
        _faulted = true;
        ModLog.Log.Error($"gatOS camera viewport postfix disabled after an error: {ex.Message}");
    }
}
```

3. **Do the real work in one place.** `PrepareMainViewportFrame` samples, ticks, drains commands and then
   lets the director prepare the pose; the write itself is made by a `FixedController` subclass the
   director installs ([Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../write-the-camera-pose-in-phase-with-the-frame/overview.md)):

```csharp
// gatOS.GameMod/Game/Mod.Game.cs L574-L583
internal static void PrepareMainViewportFrame(double dt)
{
    if (_instance is not { IsInitialized: true } mod)
        return;
    mod.SampleTelemetry(dt);
    mod.TickSchedules(dt);
    mod.DrainCommands();
    mod._mainViewportPreparedThisFrame = true;
    mod.DriveCamera(dt);
}
```

4. **Guard the idle case.** `DriveCamera` returns after one branch while nothing owns the camera, so the
   patch costs almost nothing for players who never use it:

```csharp
// gatOS.GameMod/Game/Mod.Game.cs L544-L566
partial void DriveCamera(double dt)
{
    if (_cameraDead || _cameraDirector is not { } camera || camera.IsIdle)
        return;
    try
    {
        camera.Update(dt);
    }
    catch (Exception ex)
    {
        _cameraDead = true;
        try
        {
            camera.Restore(); // never leave the player holding a camera gatOS stopped driving
        }
        catch (Exception restoreEx)
        {
            ModLog.Log.Debug($"gatOS camera restore error: {restoreEx.Message}");
        }

        ModLog.Log.Error($"gatOS camera disabled after a driver error: {ex.Message}");
    }
}
```

5. **Publish the result from the postfix** with `PublishMainViewportCameraStatus`, which reads KSA's
   final transform for read-back ([gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L586-L590)).

## Complete example

- [gatOS.GameMod/Game/Ksa/Camera/CameraViewportPatch.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraViewportPatch.cs): the patch
- [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L536-L610): `DriveCamera`, `PrepareMainViewportFrame`, `InstallCameraHook`
- [gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs): the director that owns the camera

## Variations and gotchas

- **Identity, not type.** Test `ReferenceEquals(__instance, Program.MainViewport)`; a type test would
  match every secondary and portrait viewport.
- **A faulted patch must hand the camera back** (`camera.Restore()` in `DriveCamera`'s catch) so the player is
  never left holding a camera nobody drives.
- **Same-frame guest effects.** Because the prefix runs before the controller, commands drained there change
  this frame's camera; a StarMap GUI hook would change the next frame's ([game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)).
- **Patching a hot path.** The method runs once per viewport per frame; keep the filter first.

## See also

- [Topic overview](overview.md), [approach 1](understand-camera-modes-and-controllers-1.md), [approach 2](understand-camera-modes-and-controllers-2.md), [approach 3](understand-camera-modes-and-controllers-3.md)
- [Programmable camera director](../../../../mods/gatos/features/programmable-camera-director.md), [Synthetic camera follow anchor](../../../../mods/gatos/features/synthetic-camera-follow-anchor.md)
- [Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../write-the-camera-pose-in-phase-with-the-frame/overview.md), [Drive the main camera through Fixed mode without crashing it, with a stable up vector](../drive-the-main-camera-in-fixed-mode/overview.md)
