---
type: How-To
title: Open a secondary viewport, make it visible and re-resolve its index by reference every frame
description: KSArmory and KSAStructures lease a spare camera window with ViewportRegistry.TryOpenSecondaryViewport, find it again by reference in GameViews each frame, disable the control when AvailableSecondaryCount is zero and hand it back with ReleaseSecondaryViewport
category: camera
group: secondary-viewports
topic: borrow-a-secondary-viewport
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksarmory/features/director-camera-window
  - mods/ksastructures/features/computer-flies-nearest-craft
techniques: [viewport-lease]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:viewports, frame:render]
game_types:
  - KSA.ViewportRegistry
  - KSA.IGameViewport
  - KSA.GameViewport
  - KSA.ViewportBase
  - KSA.ViewportPresets
  - KSA.ViewportType
  - KSA.DockingPort
tags: [camera, secondary-viewport, lease, viewport-registry, camera-window]
---
KSA builds all its viewports once at boot and seals the registry, so a mod cannot create a camera. What it can do is borrow one of the four spare `Secondary` viewports (the same ones behind the View menu's Add Camera). This article is the plain lease: open one, make it visible, track it, give it back.
The pose is [Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](../aim-a-secondary-viewport-camera/overview.md); showing the picture in your own window is in [approaches 2](borrow-a-secondary-viewport-2.md) and [3](borrow-a-secondary-viewport-3.md). Prerequisites: StarMap.

## Background: how the game does it

`Program` creates one main viewport, a part-thumbnail viewport, **four** `ViewportType.Secondary` viewports (500 x 500, with UI and input but no shadows, ray tracing or audio) and two crew-portrait viewports, then calls `ViewportRegistry.SealAllocation()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L948-L962)). Allocation after that throws.

```csharp
// current/decomp/KSA/ViewportRegistry.cs L180-L183
public static bool TryOpenSecondaryViewport([NotNullWhen(true)] out IGameViewport? outViewport)
{
	return TryLeaseSecondaryViewport(out outViewport);
}
```

```csharp
// current/decomp/KSA/ViewportRegistry.cs L200-L215
private static bool TryLeaseSecondaryViewport([NotNullWhen(true)] out IGameViewport? outViewport)
{
	IGameViewport[] gameViews = _gameViews;
	foreach (IGameViewport gameViewport in gameViews)
	{
		if (gameViewport.Is(ViewportType.Secondary) && !LeasedSecondaryViewports.Contains(gameViewport.Id))
		{
			((IGameViewportLifecycle)gameViewport).ResetToDefaults();
			LeasedSecondaryViewports.Add(gameViewport.Id);
			outViewport = gameViewport;
			return true;
		}
	}
	outViewport = null;
	return false;
}
```

- A lease **resets the viewport**: `ResetToDefaults` returns it to `Orbit` mode, zeroes the Fixed offsets, restores the default option flags and name ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L66-L85)).
- Releasing hides the viewport and resets it again ([current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L226-L248)).
- `AvailableSecondaryCount` counts unleased secondaries; docking-port cameras claim from the same pool ([current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L52-L66), [current/decomp/KSA/DockingPort.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DockingPort.cs#L188)).
- By default each leased viewport gets a stock ImGui window: `Program.OnDrawUiViewports` calls `DrawImGui` for non-main viewports with `HasUi`, and its close button releases the lease ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3162-L3176), [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L193-L260)). Visible secondary viewports are rendered before the main view in `RenderGame` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4672-L4684)).

## Lifecycle and timing

The lease is taken and released from the mod's ImGui step (`frame:draw-ui`); the viewport is rendered in `frame:render` with the camera matrices from `frame:viewports` or from the mod's own rebuild ([Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](../aim-a-secondary-viewport-camera/overview.md)). The stock window is drawn only while the HUD is shown, from `OnDrawUiViewports`.

## Steps

1. **Offer the control only when a slot is free:**

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L3858-L3865
public static int SpareCameraWindows
{
    get
    {
        try { return ViewportRegistry.AvailableSecondaryCount; }
        catch { return 0; }
    }
}
```

2. **Open it and make it visible.** `TryOpenSecondaryViewport` leases the first free secondary and resets it. Read the position back by reference afterwards, because "the index is a position in the registry's span ... opening one is exactly the event that can move the others":

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L3879-L3908
public static bool TryOpenCameraWindow(out int index)
{
    index = -1;

    try
    {
        if (!ViewportRegistry.TryOpenSecondaryViewport(out IGameViewport? opened)) return false;

        opened.SetVisible(true);

        // The index is a position in the registry's span, so it is read back rather than
        // remembered -- opening one is exactly the event that can move the others.
        ReadOnlySpan<IGameViewport> viewports = GameViewports;
        for (int i = 0; i < viewports.Length; i++)
        {
            if (ReferenceEquals(viewports[i], opened))
            {
                index = i;
                return true;
            }
        }

        return false;
    }
    catch (Exception e)
    {
        Log.Warn($"camera: could not open a window ({e.GetType().Name}: {e.Message})");
        return false;
    }
}
```

3. **Never keep an index as identity.** Re-resolve it every frame and check the window is still usable (visible and `Secondary`); a closed stock window hands the lease back, after which the index names a viewport nobody shows:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L3814-L3825
public static bool IsUsableCameraWindow(int index)
{
    try
    {
        return TryViewport(index, out IGameViewport viewport)
            && viewport is { Visible: true, Type: ViewportType.Secondary };
    }
    catch
    {
        return false;
    }
}
```

4. **Give it back with `ReleaseSecondaryViewport`**, exactly what the stock close button does; nothing the mod changed needs restoring by hand:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L3723-L3730
public static void CloseCameraWindow(int index)
{
    try
    {
        if (TryViewport(index, out IGameViewport viewport)) ViewportRegistry.ReleaseSecondaryViewport(viewport);
    }
    catch { /* already gone */ }
}
```

5. **Put the field of view back.** The player's zoom keys clamp at 15 degrees, so a camera zoomed to one degree would otherwise stay there: KSArmory resets released windows to 50 ([FOV topic](../../main-view/override-the-camera-field-of-view/overview.md), [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5806-L5816)).

6. **One user per window.** KSArmory lets one head hold a window and switches a contested second one off; KSAStructures' office computer opens one window for the monitor and closes it on log-off ([src/KSAStructures/Ksa/OfficeComputer.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeComputer.cs#L82-L135)).

## Complete example

- [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3623-L3730), [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3856-L3908), [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L1060-L1104)
- [src/KSAStructures/Ksa/OfficeComputer.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeComputer.cs)

## Variations and gotchas

- **The registry is sealed.** Four secondaries is the whole budget, shared with the Add Camera menu, docking-port cameras and other mods.
- **The stock window needs the HUD.** With the HUD hidden `OnDrawUiViewports` does not run, so the stock window is not drawn; the viewport still renders.
- **Reset on lease.** Everything you configured before a claim is gone; re-apply after every claim ([approach 2](borrow-a-secondary-viewport-2.md)).
- **The main-view alternative.** Secondary viewports are created without the main view's shadow, ray-tracing and audio option flags (`UseShadows`, `UseRaytracing`, `HasAudio`), so a feature that needs those uses the main view instead ([Borrow the main camera, hand it back and arbitrate between camera users](../../main-view/borrow-the-main-camera-and-hand-it-back/overview.md)).

## See also

- [Topic overview](overview.md); [approach 2](borrow-a-secondary-viewport-2.md), [3](borrow-a-secondary-viewport-3.md)
- [Director camera windows on leased secondary viewports](../../../../mods/ksarmory/features/director-camera-window.md), [Fly the nearest craft from the office computer](../../../../mods/ksastructures/features/computer-flies-nearest-craft.md)
- [Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](../aim-a-secondary-viewport-camera/overview.md), [Mount a camera on a vessel part and look through it](../mount-a-camera-on-a-part/overview.md), [Draw full-screen click-through overlays and choose the right ImGui draw list](../../../ui/overlays/draw-a-fullscreen-click-through-overlay/overview.md), [Render ImGui UI into a Vulkan texture for use in the 3D world](../../../rendering/textures-and-decals/render-imgui-into-a-texture/overview.md), [Taking control of another vehicle](../../../vehicles-and-parts/vehicles/take-control-of-another-vehicle/overview.md), [Record and submit your own Vulkan work: offscreen renders and one-shot uploads](../../../rendering/vulkan-passes/submit-your-own-vulkan-commands/overview.md), [Colour-grade the frame and add an aspect-correct vignette](../../../shaders/screen-effects/colour-grade-the-frame-and-add-a-vignette/overview.md)
