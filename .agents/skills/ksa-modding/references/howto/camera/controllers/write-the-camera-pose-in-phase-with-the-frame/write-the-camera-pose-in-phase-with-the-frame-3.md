---
type: How-To
title: Skip the stock FixedController.OnFrame for the viewports you own with a Harmony prefix
description: Hot Pursuit leaves the controller alone and prefixes FixedController.OnFrame, returning false only for viewports it has leased, writing a pose recomposed from the part's current transform and repeating ClampCamera
category: camera
group: controllers
topic: write-the-camera-pose-in-phase-with-the-frame
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/unscience/features/hot-pursuit-part-mounted-cameras
  - mods/unscience/features/hot-pursuit-mesh-ray-placement
techniques: [harmony-prefix]
hooks: [harmony-prefix KSA.FixedController.OnFrame]
lifecycle: [frame:viewports]
game_types: [KSA.FixedController, KSA.GameViewport, KSA.Camera, KSA.IViewport, KSA.ViewportRegistry]
tags: [camera, phase, harmony, secondary-viewport, fixed-camera]
---
When a mod owns only some viewports (the secondary camera windows it has leased), replacing the controller would
affect every other `Fixed` camera, such as the docking cameras. Hot Pursuit instead prefixes
`FixedController.OnFrame(IViewport, double)`: it finds its own entry for the viewport, writes the pose, and
returns false to skip the stock math for that viewport only. Every other `Fixed` viewport runs stock.
Prerequisites: StarMap and Harmony; the leasing is in [Lease a secondary viewport for a mod camera and show it in a window](../../secondary-viewports/borrow-a-secondary-viewport/overview.md).

## Background: how the game does it

`GameViewport.OnFrame` calls the active controller's `OnFrame(this, dt)` and then `GetCamera().OnFrame(dt)`
([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L124)). For a leased secondary viewport in `Fixed` mode the controller is that
viewport's own `FixedController` (each `GameViewport` has one), so a Harmony prefix on the method sees the call with
`inViewport` equal to the viewport. Stock `OnFrame` places a following camera at `following.GetPositionEcl() +
CameraOffset` ([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L22-L47)), which is why the prefix must replace it rather than run after it.

## Lifecycle and timing

`frame:viewports`, immediately before `Camera.OnFrame` (L119) builds the view matrix and frustum: the feed
has no one-frame lag behind the vessel. Physics results for this frame are already applied; the part matrices
are stable on the main thread. The submod's lease bookkeeping (`Update`) runs later, in `frame:draw-ui`, so the
prefix re-checks ownership itself.

## Steps

1. **Patch the one overload with an explicit parameter list**, and fail loudly if it disappears:

```csharp
// hot-pursuit.lib/HotPursuitPatches.cs L11-L30
private static MethodBase? Target() =>
    AccessTools.Method(typeof(FixedController), nameof(FixedController.OnFrame),
        new[] { typeof(IViewport), typeof(double) });

public static void Apply(Harmony harmony)
{
    var original = Target();
    var prefix = AccessTools.Method(typeof(HotPursuitPatches), nameof(BeforeFixedFrame));
    if (original == null || prefix == null)
        throw new MissingMethodException(typeof(FixedController).FullName, nameof(FixedController.OnFrame));
    harmony.Patch(original, prefix: new HarmonyMethod(prefix));
}

public static void Remove(Harmony harmony)
{
    var original = Target();
    var prefix = AccessTools.Method(typeof(HotPursuitPatches), nameof(BeforeFixedFrame));
    if (original != null && prefix != null)
        harmony.Unpatch(original, prefix);
}
```

2. **Return false only for your own viewports, and never let an exception reach stock:**

```csharp
// hot-pursuit.lib/HotPursuitPatches.cs L32-L47
private static bool BeforeFixedFrame(IViewport inViewport)
{
    try
    {
        // False skips only the stock controller update for a Hot Pursuit-owned viewport.
        // GameViewport still calls Camera.OnFrame immediately afterward to bake the view.
        return !(HotPursuitSubmod.Instance?.ApplyFixedPose(inViewport) ?? false);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"hot-pursuit: fixed-camera pose error: {ex.Message}");
        // ApplyFixedPose can throw only after matching one of our entries. Keep the camera's
        // last valid pose instead of running stock FixedController with its zero direction.
        return false;
    }
}
```

3. **Select the entry by reference and re-validate the lease.** The stock camera window can release the lease between
   frames; a released slot must run stock:

```csharp
// hot-pursuit.lib/HotPursuitSubmod.cs L119-L145
internal bool ApplyFixedPose(IViewport viewport)
{
    foreach (var entry in _cameras)
    {
        if (!ReferenceEquals(entry.Viewport, viewport))
            continue;

        // The stock viewport window can release this lease after our previous Update. Do not
        // suppress FixedController for a slot that another owner has already reclaimed.
        if (!ViewportRegistry.TryGetOwned(entry.Owner, out var ownedViewport) ||
            !ReferenceEquals(ownedViewport, viewport))
        {
            entry.Viewport = null;
            entry.LeaseLost = true;
            return false;
        }

        // FixedController runs before the StarMap update hook. Re-resolve here as well so a
        // vehicle destruction/part-failure handoff cannot leave the pose patch touching last
        // frame's disposed Part reference.
        ResolveTarget(entry);
        if (entry.IsResolved && entry.Visible)
            HotPursuitPose.TryApply(entry, viewport);
        return true;
    }
    return false;
}
```

4. **Write the pose, then repeat `ClampCamera`.** `Camera.OnFrame` clamps again, but the next step (the celestial
   sync) must see the final position, and `LookAtRotation` supplies the rotation:

```csharp
// hot-pursuit.lib/HotPursuitPose.cs L66-L76

var camera = ownedViewport.BaseCamera;
camera.PositionEcl = positionEcl;
// GameViewport calls Camera.OnFrame after this prefix, which terrain-clamps the camera.
// Clamp once now as well so the celestial metrics below describe the final render pose;
// Camera.OnFrame's second clamp is then idempotent.
camera.ClampCamera();
HotPursuitCelestialState.Synchronize(camera);
camera.WorldRotation = Camera.LookAtRotation(forwardEcl, upEcl);
camera.SetFieldOfView(entry.FieldOfView);
return true;
```

   The pose composition itself is in [Mount a camera on a vessel part and look through it](../../secondary-viewports/mount-a-camera-on-a-part/overview.md).

## Complete example

- [hot-pursuit.lib/HotPursuitPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitPatches.cs), [hot-pursuit.lib/HotPursuitSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.cs#L119-L145), [hot-pursuit.lib/HotPursuitPose.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitPose.cs)

## Variations and gotchas

- **A throw after matching must still return false**, or stock runs with a zero direction and crashes ([current/decomp/Brutal.Numerics/double3.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/double3.cs#L746-L754)).
- **Portraits and docking cameras are untouched**: the prefix matches by `ReferenceEquals(entry.Viewport, viewport)`.
- **`FixedController.OnFrame` must exist with that signature**; `Apply` throws `MissingMethodException` otherwise ([hot-pursuit.lib/HotPursuitPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitPatches.cs#L15-L22)).
- **Cost.** The prefix runs once per `Fixed` viewport per frame, including the crew portraits; the first line is a loop over the mod's own entries.

## See also

- [Topic overview](overview.md); [approach 1](write-the-camera-pose-in-phase-with-the-frame-1.md), [2](write-the-camera-pose-in-phase-with-the-frame-2.md), [4](write-the-camera-pose-in-phase-with-the-frame-4.md)
- [Live part-mounted cameras in leased stock secondary viewports](../../../../mods/unscience/features/hot-pursuit-part-mounted-cameras.md), [Click-to-mount picking on vessel meshes with stable part anchoring](../../../../mods/unscience/features/hot-pursuit-mesh-ray-placement.md)
- [Lease a secondary viewport for a mod camera and show it in a window](../../secondary-viewports/borrow-a-secondary-viewport/overview.md), [Mount a camera on a vessel part and look through it](../../secondary-viewports/mount-a-camera-on-a-part/overview.md), [Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](../../secondary-viewports/aim-a-secondary-viewport-camera/overview.md)
