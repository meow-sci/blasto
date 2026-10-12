---
type: Mod Feature
title: ModMenu entry and game-styled windows
description: Register an Oh My Stars submenu through the ModMenuEntry attribute and draw resizable windows with the game ConsoleStyle and ConsoleWidgets look while keeping ImGui saved geometry
mod: ohmystars
category: ui
techniques: [imgui-window, imgui-menu, starmap-lifecycle]
hooks: [starmap StarMapBeforeGui, modmenu ModMenuEntry]
lifecycle: [frame:draw-ui]
game_types: [KSA.ConsoleStyle, KSA.ConsoleWidgets, KSA.ImGuiDisabledScope, KSA.Program, Brutal.ImGuiApi.ImGui]
tags: [imgui, modmenu, menu, console-style, widgets, windows]
---

Oh My Stars has two ordinary ImGui windows ("Oh My Stars" with Settings and Information tabs, and the
Star Editor) that look like KSA's own console-styled UI, plus an "Oh My Stars" submenu in ModMenu with
one item per window. The interesting parts are how little it takes to register with ModMenu, how it
borrows the game's `ConsoleStyle`/`ConsoleWidgets` look without the game's window helper, and the
custom-painted tab buttons.[^oms-ohmystarsentrypoint-cs] [^oms-ohmystarswindow-cs]

## How it works

1. **ModMenu registration.** A public static method on the `[StarMapMod]` class carries
   `[ModMenuEntry("Oh My Stars")]` and draws two `ImGui.MenuItem`s that toggle the windows
   ([src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L34-L42)). The attribute comes from
   `ModMenu.Attributes.dll` 1.0.2 (a .NET Standard 2.0 assembly by MrJeranimo, per its metadata),
   committed to `src/` and referenced by `HintPath` because, as the csproj comment says, the package
   is not on nuget.org ([src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L50-L55),
   [src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L97-L99)).[^oms-ohmystars-csproj] The DLL's metadata describes the
   contract: decorate menu methods with `[ModMenuEntry]` (constructor argument `menuName`, optional
   `isModMenuActivePropertyName` for a bool property ModMenu sets while the menu is active). How and
   when ModMenu discovers and calls the method is in ModMenu itself, which is not part of this corpus
   (unverified here). The original StellariumCatalog drew its checkboxes and sky-culture list directly
   inside this menu.
2. **Windows from `[StarMapBeforeGui]`.** `OnBeforeGui` draws both windows and then calls
   `PersistWindowOpenStates()` ([src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L44-L49)).
3. **Plain `ImGui.Begin` plus game styling.** Each window uses
   `SetNextWindowSize(..., ImGuiCond.FirstUseEver)`, `SetNextWindowBgAlpha(1f)` and
   `ImGui.Begin(title, ref open, None)`, then `ConsoleStyle.PushWidgetStyle()` ...
   `PopWidgetStyle()` around the content ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L256-L272)). The game's
   `ConsoleStyle.BeginWindow` would apply the full console chrome but adds `NoSavedSettings`, losing
   position persistence ([current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L667-L703)).[^ksa-consolestyle-cs]
4. **Game widgets.** Rows use `ConsoleWidgets.BeginRow/EndRow`, `Checkbox(id, ref value, pending:
   false)`, `SliderFloat`/`SliderInt` with preformatted value text, `Button`/`PositiveButton` (the
   "on" state), `Readout`, `RegionHeader` and `Tooltip` gated on `ConsoleWidgets.RowHovered`
   ([current/decomp/KSA/ConsoleWidgets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleWidgets.cs#L140-L997)).[^ksa-consolewidgets-cs]
   `ImGuiDisabledScope` disables controls with a `using` block
   ([current/decomp/KSA/ImGuiDisabledScope.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiDisabledScope.cs)).
5. **Custom tab buttons.** `DrawColoredTabButton` lays out an `InvisibleButton` with the same padding
   as `ConsoleWidgets` buttons and paints it with `AddRectFilled` in the live
   `ImGuiCol.TitleBgActive` colour, a hover lighten, a border and centred label text, so the active
   tab matches the window title bar in any theme ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L651-L711)).
   `DrawThickSeparator` paints a 3 px rule on the window draw list in place of `Separator`
   ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L515-L531)).
6. **Collapsible sections and indents.** A text button toggles each section, and `ImGui.Indent`
   is used instead of a one-off cursor nudge because `ConsoleWidgets.BeginRow` captures the cursor as
   the row origin ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L548-L585)).
7. **Modals.** The Star Editor's confirmations subclass the game's `Popup` (see
   [Star binary switcher](star-binary-switcher.md)).

## Lifecycle and timing

- **`frame:draw-ui`, before the stock UI.** `[StarMapBeforeGui]` is a Harmony prefix on
  `Program.OnDrawUiFrame` ([StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L14-L24)),[^starmap-patches-programpatcher-cs]
  so the mod's windows are submitted before the game's alerts, `Popup.DrawAll` and static windows
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3140)).[^ksa-program-cs] Both are inside
  `if (DrawUI)`, so hiding the HUD hides the windows.
- **Data handed to later hooks.** The window records its position and size each frame for the star
  pointer and the navball coverage test, both drawn later from `[StarMapAfterGui]`.
- **ModMenu timing** is decided by ModMenu, which is not in this corpus (unverified). If it draws
  inside the game's menu bar, as gatOS's fallback menu does through a `Program.DrawProgramMenusHook`
  postfix ([gatOS ModMenu entry](../../gatos/features/modmenu-entry-with-fallback-menu.md)), that bar is
  a separate window (`ImGui.Begin("Menu Bar")` with `BeginMenuBar`) drawn by `OnDrawUiThreadSafe`
  after both StarMap GUI hooks ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3178-L3189)). A
  toggle made there shows or hides the window on the next frame, and `PersistWindowOpenStates` saves
  it then.

## Key code

| Path | Role |
|---|---|
| [src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L34-L49) | ModMenu entry and window hook |
| [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L256-L513) | Settings tab layout |
| [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L515-L711) | Separator, section header, indent, tab button helpers |
| [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L183-L444) | Star Editor layout |

## Snippets

```csharp
// src/OhMyStarsEntryPoint.cs L34-L49
    [ModMenuEntry("Oh My Stars")]
    public static void DrawMenu() {
        if(ImGui.MenuItem("Oh My Stars Window")) {
            OhMyStarsWindow.ToggleWindow();
        }
        if(ImGui.MenuItem("Star Editor")) {
            StarsEditWindow.ToggleWindow();
        }
    }

    [StarMapBeforeGui]
    public static void OnBeforeGui(double dt) {
        OhMyStarsWindow.Draw();
        StarsEditWindow.Draw();
        OhMyStarsWindow.PersistWindowOpenStates();
    }
```

```csharp
// src/OhMyStarsWindow.cs L680-L711
    internal static bool DrawColoredTabButton(string label) {
        ConsoleStyle.PushLabelFont();
        float2 textSize = ImGui.CalcTextSize(label);
        float fontSize = ImGui.GetFontSize();
        ConsoleStyle.PopFont();

        float2 size = new float2(
            textSize.X + ConsoleWidgets.ButtonHorizontalPaddingPx * 2f,
            fontSize + ConsoleWidgets.ButtonVerticalPaddingPx * 2f);
        float2 pMin = ImGui.GetCursorScreenPos();
        float2 pMax = pMin + size;

        bool clicked = ImGui.InvisibleButton(new ImString($"##Tab_{label}"), in size, ImGuiButtonFlags.None);
        bool hovered = ImGui.IsItemHovered();

        float4 titleBarColor = ImGui.GetStyleColorVec4(ImGuiCol.TitleBgActive);
        uint fill = ImGui.ColorConvertFloat4ToU32(new float4(titleBarColor.X, titleBarColor.Y, titleBarColor.Z, 1f));
        uint border = ConsoleStyle.FrameU32;
        uint textColor = ImGui.ColorConvertFloat4ToU32(new float4(1f, 1f, 1f, 1f));

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(in pMin, in pMax, ConsoleStyle.ApplyAlpha(fill), ConsoleStyle.FrameRoundingPx);
        if(hovered) {
            drawList.AddRectFilled(in pMin, in pMax, ConsoleStyle.ApplyAlpha(ConsoleStyle.ButtonHoverLightenU32), ConsoleStyle.FrameRoundingPx);
        }
        drawList.AddRect(in pMin, in pMax, ConsoleStyle.ApplyAlpha(border), ConsoleStyle.FrameRoundingPx, ImDrawFlags.None, ConsoleStyle.WindowBorderThicknessPx);

        ConsoleStyle.PushLabelFont();
        drawList.AddText(pMin + (size - textSize) * 0.5f, ConsoleStyle.ApplyAlpha(textColor), label);
        ConsoleStyle.PopFont();
        return clicked;
    }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ConsoleStyle.PushWidgetStyle`, `PopWidgetStyle`, `PushLabelFont`, `PushDisabled`, `ApplyAlpha`, `HairlineU32` | [current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L181-L506) | Game look for widgets |
| `ConsoleStyle.BeginWindow` (avoided) | [current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L667-L703) | Forces `NoSavedSettings` |
| `ConsoleWidgets.BeginRow`, `Checkbox`, `SliderFloat`, `SliderInt`, `Button`, `PositiveButton`, `Readout`, `RegionHeader`, `Tooltip`, `RowHovered` | [current/decomp/KSA/ConsoleWidgets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleWidgets.cs#L140-L997) | Game widgets |
| `ImGuiDisabledScope` | [current/decomp/KSA/ImGuiDisabledScope.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiDisabledScope.cs) | Disabled blocks |
| `Program.OnDrawUiFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3160) | What `[StarMapBeforeGui]` precedes |

## Pitfalls and version notes

- **ModMenu is optional but undeclared.** `mod.toml` declares no dependency. Without ModMenu the
  windows still open by default (`ShowWindow=True`), but once closed there is no in-game way to
  reopen them except editing `settings.ini`. gatOS shows a fallback menu pattern.
- **Private copy of the attribute DLL.** `ModMenu.Attributes.dll` ships inside the mod folder and is
  loaded into the mod's own `AssemblyLoadContext` unless an earlier context already has it, so
  ModMenu must match the attribute by name rather than by type identity (StarMap's resolver order:
  default context, game context, declared dependencies, then the mod's `.deps.json`;
  [StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L73)).[^starmap-modrepository-modassemblyloadcontext-cs]
- **Game widget APIs are internal-feeling.** `ConsoleWidgets` and `ConsoleStyle` signatures (for
  example the `pending` parameter) can change between builds.
- **Fixed window titles are IDs.** The windows are keyed by their titles; renaming them resets the
  saved geometry in `imgui.ini`.

## See also

- [settings.ini persistence](ini-settings-persistence.md)
- [Star binary switcher](star-binary-switcher.md)
- [Mod root and packaging](mod-root-and-packaging.md)
- [Reference ModMenu.Attributes, tag a static method and ship the DLL beside your mod](../../../howto/ui/menus/list-a-mod-in-modmenu/list-a-mod-in-modmenu-1.md) - how-to article demonstrating this feature
- [Tabbed window: title-bar-coloured custom tab buttons](../../../howto/ui/windows/build-a-tabbed-imgui-window/build-a-tabbed-imgui-window-3.md) - how-to article demonstrating this feature
- [Match the stock UI: a plain ImGui.Begin window plus PushWidgetStyle, with custom tab buttons](../../../howto/ui/windows/match-the-stock-console-ui-style/match-the-stock-console-ui-style-2.md) - how-to article demonstrating this feature

[^oms-ohmystarsentrypoint-cs]: OhMyStars src/OhMyStarsEntryPoint.cs
[^oms-ohmystarswindow-cs]: OhMyStars src/OhMyStarsWindow.cs
[^oms-ohmystars-csproj]: OhMyStars src/OhMyStars.csproj
[^ksa-consolestyle-cs]: KSA.ConsoleStyle (decompiled, build 2026.10.10.5554)
[^ksa-consolewidgets-cs]: KSA.ConsoleWidgets (decompiled, build 2026.10.10.5554)
[^starmap-patches-programpatcher-cs]: StarMap StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-program-cs]: KSA.Program (decompiled, build 2026.10.10.5554)
[^starmap-modrepository-modassemblyloadcontext-cs]: StarMap StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
