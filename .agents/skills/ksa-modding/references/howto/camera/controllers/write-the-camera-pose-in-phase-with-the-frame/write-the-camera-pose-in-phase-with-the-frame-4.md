---
type: How-To
title: Pre-empt Orbit or Free by prefixing both controllers' OnFrame with one shared patch while a sequence plays
description: Camera Controller Override patches OrbitController.OnFrame and FlyController.OnFrame with the same prefix, edits the live camera while a sequence plays and returns false to skip the stock camera math for that frame
category: camera
group: controllers
topic: write-the-camera-pose-in-phase-with-the-frame
approach: 4
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/camera-controller-override-camera-animations]
techniques: [harmony-prefix]
hooks: [harmony-prefix KSA.OrbitController.OnFrame, harmony-prefix KSA.FlyController.OnFrame]
lifecycle: [frame:viewports, frame:draw-ui]
game_types: [KSA.OrbitController, KSA.FlyController, KSA.Controller, KSA.Camera, KSA.GameViewport]
tags: [camera, phase, harmony, orbit, free-camera, animation]
---
Sometimes the right thing is to leave the player's own camera in charge and take over only for a while.
Camera Controller Override plays scripted moves on top of the *Orbit* or *Free* camera: one Harmony prefix is
attached to both `OrbitController.OnFrame` and `FlyController.OnFrame`; while a sequence plays it writes
the controller's `Camera` and returns false so the stock math for that frame is skipped. The rest of
this article is the recipe for **pre-empting Orbit or Free instead of swapping controllers**. Prerequisites: StarMap and Harmony.

## Background: how the game does it

Both controllers write the pose every frame from their own state: `OrbitController.OnFrame` rebuilds
`LocalRotation` and `PositionCce` from the followed object's `OrbitView` ([current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L636-L655)) and
returns at once when nothing is followed; `FlyController.OnFrame` rewrites `LocalRotation` from its cached offset
([current/decomp/KSA/FlyController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlyController.cs#L734)). The public base field `Controller.Camera` is the camera being written ([current/decomp/KSA/Controller.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Controller.cs#L12)).
A mod write to those properties from anywhere else is overwritten, which is why this recipe skips the original
method rather than writing after it.

## Lifecycle and timing

`frame:viewports`: the controller's `OnFrame` runs inside `GameViewport.OnFrame`, before `Camera.OnFrame` bakes
the matrices ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L124)), so a write in the prefix is rendered on the same frame.
`Program.OnFrameController` does **not** run controllers; it only syncs the cursor mode
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2520-L2552)). The panel buttons (play, pause, stop) are ImGui in `frame:draw-ui`, so a change takes
effect on the next frame. Time comes from the controller's `inDeltaTime`, i.e. player time, so sequences keep
playing while the simulation is paused or warped.

## Pre-empt Orbit or Free instead of swapping controllers

1. **Resolve both targets by name** and attach the *same* prefix, so one patch class covers both controllers:

```csharp
// camera-controller-override.lib/CameraControllerOverridePatches.cs L20-L34
public static void Apply(Harmony harmony)
{
    var prefix = new HarmonyMethod(typeof(CameraControllerOverridePatches)
        .GetMethod(nameof(OnFramePrefix), BindingFlags.Static | BindingFlags.NonPublic));

    var orbitOnFrame = AccessTools.Method(typeof(OrbitController), "OnFrame");
    var flyOnFrame = AccessTools.Method(typeof(FlyController), "OnFrame");

    if (orbitOnFrame != null)
        harmony.Patch(orbitOnFrame, prefix: prefix);
    if (flyOnFrame != null)
        harmony.Patch(flyOnFrame, prefix: prefix);

    Console.WriteLine("camera-controller-override.lib: patches applied");
}
```

2. **Bind only `Controller __instance` and `double inDeltaTime`.** The camera is `__instance.Camera`, mutated by
   reference; anything that throws falls through to the stock controller. Return `!shouldSkip`:

```csharp
// camera-controller-override.lib/CameraControllerOverridePatches.cs L42-L64
private static bool OnFramePrefix(Controller __instance, double inDeltaTime)
{
    try
    {
        if (_sequencePlayer != null && _sequencePlayer.State == PlaybackState.Playing)
        {
            // The camera transform IS the controller's public `Camera` field
            // (KSA.Camera : Transform3D). KSA.Controller/OrbitController/FlyController have
            // no private `Transform` field, so the previous `Transform3D ___Transform`
            // Harmony field injector bound to nothing and threw at patch time — which also
            // aborted the rest of the supermod's patch chain. Reading __instance.Camera
            // mutates the real camera by reference, so animation moves the live view.
            bool shouldSkip = _sequencePlayer.Update(__instance, __instance.Camera, inDeltaTime);
            return !shouldSkip;
        }
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"camera-controller-override: Error in prefix: {ex.Message}");
        return true;
    }
}
```

3. **Do nothing unless a sequence is playing.** The prefix costs one reference test per Orbit or Free controller per frame otherwise.

4. **Stop cleanly.** When playback ends without a return blend the stock controller rewrites the pose from its own
   state on the next frame, so the camera jumps; the library blends back to the captured start pose by default
   ([Animate the flight camera with eased moves, keyframe sequences and saved recipes](../../special-views/chain-camera-moves-into-keyframe-sequences/overview.md), [Play scripted cinematic camera tracks with blends and easing](../../special-views/play-cinematic-camera-tracks/overview.md)).

## Complete example

- [camera-controller-override.lib/CameraControllerOverridePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/CameraControllerOverridePatches.cs)
- [camera-controller-override.lib/Animation/KeyframeSequencePlayer.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/KeyframeSequencePlayer.cs): the player the prefix calls

## Variations and gotchas

- **Every Orbit or Free controller is patched**, in the main viewport, the four secondary viewports and the two portraits. While a
  sequence plays, the first controller to call `Update` drives it and every other Orbit/Free controller is skipped, so secondary
  cameras in those modes freeze for the duration ([Keyframe camera sequences with parallel groups, return-to-start and saved recipes](../../../../mods/unscience/features/camera-controller-override-keyframe-sequences.md)).
- **IVA, Map and Fixed are never animated**; they use other controllers.
- **Names, not signatures.** The lookups use `AccessTools.Method(typeof(OrbitController), "OnFrame")`; a rename or overload makes the feature
  silently do nothing.
- **`UnpatchAll(harmony.Id)`** removes every patch on a shared Harmony instance, not just this one.
- **`Camera.OnFrame` still clamps** paths that dip below 0.5 m above terrain ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L633-L639)).
- **Two `OrbitController` classes** exist (`KSA` and `RenderCore.Input.Controllers`); `using KSA;` picks the flight camera.

## See also

- [Topic overview](overview.md); [approach 1](write-the-camera-pose-in-phase-with-the-frame-1.md), [2](write-the-camera-pose-in-phase-with-the-frame-2.md), [3](write-the-camera-pose-in-phase-with-the-frame-3.md)
- [Cinematic camera moves by pre-empting the Orbit and Fly controllers](../../../../mods/unscience/features/camera-controller-override-camera-animations.md)
- [Animate the flight camera with eased moves, keyframe sequences and saved recipes](../../special-views/chain-camera-moves-into-keyframe-sequences/overview.md), [Play scripted cinematic camera tracks with blends and easing](../../special-views/play-cinematic-camera-tracks/overview.md), [Understand KSA camera modes, controllers, the viewport frame order and which camera is which](../understand-camera-modes-and-controllers/overview.md)
