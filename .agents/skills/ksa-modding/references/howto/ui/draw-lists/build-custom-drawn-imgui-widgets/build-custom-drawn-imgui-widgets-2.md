---
type: How-To
title: Draw icon buttons from primitives, with no textures or icon font
description: Keep a stock ImGui Button for hit testing and style, then draw a shield, camera, flag or missile on top of it with the window draw list inside GetItemRectMin and GetItemRectMax
category: ui
group: draw-lists
topic: build-custom-drawn-imgui-widgets
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/operator-panel-imgui]
techniques: [imgui-drawlist, imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImDrawListPtr, Brutal.ImGuiApi.ImDrawListExtensions]
tags: [icons, buttons, glyphs, font, draw-list]
---
KSArmory's operator panel has per-craft icon buttons (guard shield, chase camera, team flag, weapons missile). It draws them rather than typing a symbol because, per the mod, KSA's fonts carry basic Latin only and a symbol would render as a box. The pattern keeps ImGui's `Button` for hit testing, tooltips and the pressed colour, then paints a shape over the button's rectangle. Demonstrated by [Operator panel in ImGui with a menu-bar entry and ModMenu registration](../../../../mods/ksarmory/features/operator-panel-imgui.md). Prerequisite: a window you are drawing into (see [Organise a mod window: tabs, button tabs, tables and pop-out sections](../../../../howto/ui/windows/build-a-tabbed-imgui-window/overview.md)).

## Background: how the game does it

The font claim is the mod's, not verified against the game's font files here; what matters is that the icon cannot depend on a glyph being present. The Brutal binding offers the three calls the pattern needs: `ImGui.Button(label, size)`, `ImGui.GetItemRectMin()/GetItemRectMax()` ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8013-L8021)[^brutal-imguiapi-imgui]) which return the last item's screen rectangle, and `ImDrawListExtensions.AddRectFilled/AddTriangleFilled/AddCircleFilled/AddQuadFilled/AddLine` ([current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L73-L149)[^brutal-imguiapi-imdrawlistextensions]). Mod fonts that are added to the atlas can be lost on a font-settings change (see [Load a custom TTF font into KSA's ImGui atlas and keep it across font rebuilds](../../../../howto/ui/draw-lists/load-a-custom-font-into-imgui/overview.md)), which is a second reason to prefer shapes.

## Lifecycle and timing

Drawn every frame inside KSArmory's windows, from the `[StarMapAfterGui]` UI pass: `frame:draw-ui`. The draw list is the window's own (`ImGui.GetWindowDrawList()`), so the icon is clipped and layered with the button it sits on. When the HUD is hidden `StarMapAfterGui` does not fire. See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare the icon set** as an enum, with a few `ImColor8` inks:

```csharp
// src/KSArmory/Ksa/Ui/Ui.cs L702-L708
private static readonly ImColor8 GuardInk = new(100, 240, 120, 255);
private static readonly ImColor8 PartlyInk = new(255, 200, 70, 255);
private static readonly ImColor8 ChaseInk = new(130, 190, 255, 255);
private static readonly ImColor8 OffInk = new(140, 140, 150, 255);
private static readonly ImColor8 Lens = new(24, 26, 32, 255);

private enum Icon { Shield, Camera, Flag, Missile }
```

2. **Make a square button the height of a text button**, push a lit style colour if the state is on, and capture the click. The icon is drawn right after, so the item rectangle still refers to this button:

```csharp
// src/KSArmory/Ksa/Ui/Ui.cs L710-L722
// A square button the height of a text one, with a symbol drawn on it. Drawn rather than
// typed, because KSA's fonts carry basic Latin only and a symbol would render as a box.
private static bool IconButton(string id, Icon icon, ImColor8 ink, bool lit)
{
    float side = ImGui.GetFrameHeight();

    if (lit) ImGui.PushStyleColor(ImGuiCol.Button, LitButton);
    bool clicked = ImGui.Button(id, new float2?(new float2(side, side)));
    if (lit) ImGui.PopStyleColor();

    DrawIcon(ImGui.GetWindowDrawList(), icon, ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ink);
    return clicked;
}
```

3. **Draw each icon in unit coordinates.** The local function `P(u, v)` maps `[0,1]` to the button rectangle, so the same code scales with the frame height (and therefore with the interface scale):

```csharp
// src/KSArmory/Ksa/Ui/Ui.cs L724-L756
private static void DrawIcon(ImDrawListPtr draw, Icon icon, float2 min, float2 max, ImColor8 ink)
{
    float w = max.X - min.X;
    float h = max.Y - min.Y;
    float2 P(float u, float v) => new(min.X + (u * w), min.Y + (v * h));

    switch (icon)
    {
        case Icon.Shield:
            draw.AddRectFilled(P(0.25f, 0.18f), P(0.75f, 0.52f), ink);
            draw.AddTriangleFilled(P(0.25f, 0.52f), P(0.75f, 0.52f), P(0.5f, 0.86f), ink);
            break;

        case Icon.Camera:
            draw.AddRectFilled(P(0.18f, 0.34f), P(0.82f, 0.78f), ink);
            draw.AddRectFilled(P(0.36f, 0.24f), P(0.56f, 0.34f), ink);
            draw.AddCircleFilled(P(0.5f, 0.56f), 0.13f * h, Lens);
            break;

        case Icon.Flag:
            draw.AddLine(P(0.3f, 0.16f), P(0.3f, 0.86f), ink, 1.5f);
            draw.AddTriangleFilled(P(0.3f, 0.18f), P(0.78f, 0.34f), P(0.3f, 0.5f), ink);
            break;

        case Icon.Missile:
            // Climbing to the right: body, nose and a pair of tail fins.
            draw.AddQuadFilled(P(0.20f, 0.70f), P(0.28f, 0.78f), P(0.70f, 0.36f), P(0.62f, 0.28f), ink);
            draw.AddTriangleFilled(P(0.62f, 0.28f), P(0.70f, 0.36f), P(0.84f, 0.14f), ink);
            draw.AddTriangleFilled(P(0.24f, 0.74f), P(0.14f, 0.58f), P(0.32f, 0.66f), ink);
            draw.AddTriangleFilled(P(0.24f, 0.74f), P(0.40f, 0.84f), P(0.32f, 0.66f), ink);
            break;
    }
}
```

4. **Use it like any button.** Give it a `##` id (the label is hidden, the id stays unique per control) and read the click. A row of icons per craft is a loop with `PushID`/`PopID`:

```csharp
// src/KSArmory/Ksa/Ui/Ui.cs L495-L510
ImColor8 ink = state switch
{
    Guard.Guarding => GuardInk,
    Guard.Partly => PartlyInk,
    _ => OffInk,
};

if (IconButton("##guard", Icon.Shield, ink, lit: state != Guard.Off))
{
    bool on = GuardState.TurnsOn(state);
    foreach (WeaponSystems.Entry e in _rowSystems)
    {
        if (CanAutoEngage(e)) e.Policy.AutoEngage = on;
    }
}

```

5. **Explain every icon with a wrapped tooltip.** The shapes are not self-explanatory, so the mod calls `Tip(...)` after nearly every control:

```csharp
// src/KSArmory/Ksa/Ui/Ui.cs L1011-L1020
private static void Tip(string text)
{
    if (!ImGui.BeginItemTooltip()) return;

    ImGui.PushTextWrapPos(ImGui.GetFontSize() * TipWidthEms);
    ImGui.TextWrapped(text);
    ImGui.PopTextWrapPos();
    ImGui.EndTooltip();
}

```

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/Ui.cs#L480-L652) | Switcher row buttons using `IconButton` |
| [src/KSArmory/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/Ui.cs#L702-L756) | `IconButton`, `DrawIcon` |

## Variations and gotchas

- **Draw after the button.** `GetItemRectMin/Max` refer to the last submitted item; submit another item first and the icon lands on the wrong rectangle.
- **Frame height, not a constant.** `GetFrameHeight()` follows font size and the game's interface scale, so the icon stays proportional.
- **Colour contrast.** Draw icons with high-contrast inks (the mod has distinct on/off/partly colours) because the button face colour changes with hover and press.
- **Fallback to text for accessibility**: add a tooltip for every icon; the shapes are not self-explanatory.
- **Version.** Pure Brutal ImGui; no game types involved.

## See also

- [Overview of custom-drawn widgets](overview.md)
- [Approach 1: widget kit](build-custom-drawn-imgui-widgets-1.md)
- [Approach 3: one glyph function shared by map and legend](build-custom-drawn-imgui-widgets-3.md)
- Mod feature: [Operator panel in ImGui with a menu-bar entry and ModMenu registration](../../../../mods/ksarmory/features/operator-panel-imgui.md)

[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imdrawlistextensions]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
