---
type: How-To
title: Clear HasUi and RenderOrbitLines through the reflected OptionFlags setter and draw the viewport texture in your own window
description: KSArmory takes KSA's window off a leased viewport by clearing its HasUi and RenderOrbitLines flags with the protected OptionFlags setter, then draws ViewportBase.ImGuiTexture in an ImGui window of its own with controls over the picture
category: camera
group: secondary-viewports
topic: borrow-a-secondary-viewport
approach: 3
difficulty: advanced
prerequisites: [starmap, reflection]
demonstrated_by: [mods/ksarmory/features/director-camera-window]
techniques: [reflection, viewport-lease, imgui-window]
hooks: [starmap StarMapAfterGui, reflection KSA.ViewportBase.set_OptionFlags]
lifecycle: [frame:draw-ui, frame:render, render:imgui]
game_types:
  - KSA.ViewportRegistry
  - KSA.IGameViewport
  - KSA.ViewportBase
  - KSA.GameViewport
  - KSA.ViewportOptionFlags
  - Brutal.ImGuiApi.ImGui
tags: [camera, secondary-viewport, imgui, texture, reflection, camera-window]
---
KSA wraps every leased secondary viewport in its own window, with a title bar and a menu. A mod that wants the picture inside its own interface (a sight with its own buttons, a monitor) has two jobs: take KSA's window away without stopping the rendering, and draw the viewport's texture itself.
This article shows the second way of showing the camera in a window: one the mod draws itself. The lease is [approach 1](borrow-a-secondary-viewport-1.md). Prerequisites: StarMap and reflection.

## Background: how the game does it

`Program.OnDrawUiViewports` only calls `DrawImGui` for non-main viewports that have `HasUi` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3162-L3176)). `GameViewport.DrawImGui` hosts the whole window: the title and close button (which releases the lease), the image, the input blocker that decides `Hovered`, world UI and the menu bar:

```csharp
// current/decomp/KSA/GameViewport.cs L193-L215
public void DrawImGui()
{
	if (!base.Visible || !this.HasAll(ViewportOptionFlags.HasUi))
	{
		SetHovered(inValue: false);
		return;
	}
	SetHovered(inValue: false);
	float num = 50f;
	float2 float5 = new float2(ImGui.GetMainViewport().Size.X - (float)Size.X - num, num);
	ImGui.SetNextWindowPos(ImGui.GetMainViewport().Pos + float5, ImGuiCond.Once, (float2?)null);
	ImGuiWindowFlags imGuiWindowFlags = ImGuiWindowFlags.NoSavedSettings;
	if (!base.AllowResize)
	{
		imGuiWindowFlags |= ImGuiWindowFlags.NoResize;
	}
	bool pOpen = base.Visible;
	bool flag = ImGui.Begin(base.Name, ref pOpen, imGuiWindowFlags);
	SetVisible(pOpen);
	if (!pOpen)
	{
		ViewportRegistry.ReleaseSecondaryViewport(this);
		ImGui.End();
```

So clearing `HasUi` removes all of it, hover handling included. `ViewportBase.OptionFlags` has a **protected setter** ([current/decomp/KSA/ViewportBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportBase.cs#L28)). The viewport keeps rendering because rendering depends on `Visible`, not on `HasUi` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4672-L4684)); the picture is `ViewportBase.ImGuiTexture`.
The default flag set for a secondary viewport is `ViewportPresets.SECONDARY_GAME` ([current/decomp/KSA/ViewportPresets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportPresets.cs#L7)). Releasing the viewport runs `ResetToDefaults`, which restores the default flags, so nothing needs undoing by hand.

## Lifecycle and timing

The flags are cleared and the window drawn in the mod's `[StarMapAfterGui]` step (`frame:draw-ui`); the viewport is rendered in `frame:render` before the main view and its texture is sampled by the ImGui pass later in the same command buffer (`render:imgui`), so the picture is this frame's render. With the HUD hidden the GUI hook does not run,
so the mod's window is not drawn although the camera is still aimed from the hidden-HUD step.

## Steps

1. **Fetch the protected setter once:**

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L3648-L3652
// ViewportBase.OptionFlags has a protected setter. Clearing HasUi on a window the mod draws
// itself is what stops KSA wrapping the same picture in its own window; releasing the window
// puts the defaults back (GameViewport.ResetToDefaults), so nothing here has to restore it.
private static readonly MethodInfo? OptionFlagsSetter =
    typeof(ViewportBase).GetProperty(nameof(ViewportBase.OptionFlags))?.GetSetMethod(nonPublic: true);
```

2. **Clear the flags and verify.** `HasUi` and `RenderOrbitLines` (a sight looking past the Moon otherwise shows its orbit across the picture); return false if it cannot be done so the caller leaves KSA's window alone:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L3659-L3680
public static bool TryHideViewportWindow(int index)
{
    try
    {
        if (OptionFlagsSetter is null) return false;
        if (!TryViewport(index, out IGameViewport viewport)) return false;
        if (viewport.Type != ViewportType.Secondary || !viewport.Visible) return false;

        // Orbit lines too: a sight looking past the Moon otherwise shows its orbit across the picture.
        const ViewportOptionFlags taken = ViewportOptionFlags.HasUi | ViewportOptionFlags.RenderOrbitLines;

        ViewportOptionFlags flags = viewport.OptionFlags;
        if ((flags & taken) == 0) return true;

        OptionFlagsSetter.Invoke(viewport, [flags & ~taken]);
        return (viewport.OptionFlags & taken) == 0;
    }
    catch
    {
        return false;
    }
}
```

3. **Read the texture and its rendered size:**

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L3683-L3698
public static bool TryViewportTexture(int index, out ImTextureRef texture, out float2 size)
{
    texture = default;
    size = default;
    try
    {
        if (!TryViewport(index, out IGameViewport viewport)) return false;
        texture = viewport.ImGuiTexture;
        size = new float2(viewport.Width, viewport.Height);
        return size.X > 0f && size.Y > 0f;
    }
    catch
    {
        return false;
    }
}
```

4. **Draw an ImGui window with a stable id and a changing title.** The window can be dragged out of the game onto another monitor, because KSA turns on ImGui platform viewports:

```csharp
// src/KSArmory/Ksa/Ui/DirectorWindow.cs L31-L72
public static bool Draw(OpticalHead head, OpticConfig policy, string title, Action<SightSurface> paint)
{
    int index = policy.Viewport;
    if (!KsaWorld.TryHideViewportWindow(index)) return false;
    if (!KsaWorld.TryViewportTexture(index, out ImTextureRef texture, out float2 rendered)) return true;

    ImGui.SetNextWindowSize(new float2(540f, 560f), ImGuiCond.FirstUseEver);

    bool open = true;
    // The index in the id rather than the title, so renaming the craft does not move the window.
    if (ImGui.Begin($"{title}###KSArmoryDirector{index}", ref open,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                    | ImGuiWindowFlags.NoCollapse))
    {
        float2 at = ImGui.GetCursorScreenPos();
        float2 room = ImGui.GetContentRegionAvail();

        ImGui.SetNextItemAllowOverlap();
        ImGui.Image(texture, in rendered, null, null);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.None))
        {
            float wheel = ImGui.GetIO().MouseWheel;
            if (wheel != 0f) policy.Magnification = SightZoom.Stepped(policy.Magnification, wheel > 0f ? 1 : -1, head.Profile.MaxMagnification);
        }

        // Written before the sight paints: its projections read the picture's place back.
        KsaWorld.PlaceViewportPicture(index, at, ImGui.GetWindowViewport().ID, int2.Zero);
        paint(SightSurface.InWindow(index, at, rendered));

        float rowHeight = ImGui.GetFrameHeight();
        ImGui.SetCursorScreenPos(new float2(at.X + Inset, at.Y + rendered.Y - rowHeight - Inset));
        Controls(head, policy, head.Profile.MaxMagnification);

        // Resized once the drag ends: every size is a new set of render targets.
        int2 wanted = ImGui.IsMouseDown(ImGuiMouseButton.Left)
                    ? int2.Zero
                    : new int2((int)room.X, (int)room.Y);
        KsaWorld.PlaceViewportPicture(index, at, ImGui.GetWindowViewport().ID, wanted);
    }
    ImGui.End();
```

5. **Write back what KSA's window would have recorded** (`SetPosition` and `ImGuiId`, the window's platform viewport id) because projections and overlays read the picture's place from the viewport; request a resize only once the left mouse button is up, since every size is a new set of render targets:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L3704-L3720
public static void PlaceViewportPicture(int index, float2 pos, uint imGuiId, int2 wantedSize)
{
    try
    {
        if (!TryViewport(index, out IGameViewport viewport)) return;

        viewport.SetPosition(pos);
        viewport.ImGuiId = imGuiId;

        if (wantedSize.X >= 64 && wantedSize.Y >= 64
            && (wantedSize.X != viewport.Width || wantedSize.Y != viewport.Height))
        {
            viewport.RequestResize(wantedSize);
        }
    }
    catch { /* a window that cannot be placed draws where it last was */ }
}
```

6. **Fall back cleanly.** When the flags cannot be cleared `Draw` returns false and the caller paints over KSA's window through that platform viewport's foreground list instead ([src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L626-L650)).

## Complete example

- [src/KSArmory/Ksa/Ui/DirectorWindow.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/DirectorWindow.cs), [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3647-L3730), [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L1109-L1170)
- [src/KSAStructures/Ksa/OfficeComputer.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeComputer.cs): puts the same texture on a monitor mesh ([Live viewport image on an in-world monitor](../../../../mods/ksastructures/features/viewport-image-on-monitor.md))

## Variations and gotchas

- **Reflection target.** If `OptionFlags` loses its non-public setter the method returns false and KSA's window stays.
- **Window names are identities.** Rewriting `SetName` each frame resets where the player put the window; write it only when it differs, and keep the ImGui `###` id fixed while the visible title follows the craft's name.
- **Not drawn with the HUD hidden**, as above.
- **Secondary viewports skip main-view passes** (ocean, clouds, shadows cascades fitted to the main camera), so the window shows less than the main view.

## See also

- [Topic overview](overview.md); [approach 1](borrow-a-secondary-viewport-1.md), [2](borrow-a-secondary-viewport-2.md)
- [Director camera windows on leased secondary viewports](../../../../mods/ksarmory/features/director-camera-window.md)
- [Render ImGui UI into a Vulkan texture for use in the 3D world](../../../rendering/textures-and-decals/render-imgui-into-a-texture/overview.md), [Draw full-screen click-through overlays and choose the right ImGui draw list](../../../ui/overlays/draw-a-fullscreen-click-through-overlay/overview.md), [Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](../aim-a-secondary-viewport-camera/overview.md)
