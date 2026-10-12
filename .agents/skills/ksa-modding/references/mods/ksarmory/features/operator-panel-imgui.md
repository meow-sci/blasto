---
type: Mod Feature
title: Operator panel in ImGui with a menu-bar entry and ModMenu registration
description: A multi-window Dear ImGui operator panel (craft switcher with drawn icon buttons, tabbed per-craft window, weapons switcher, pop-out panes, reopen button) drawn from the after-GUI hook, plus a menu from the before-GUI hook and a copied attribute that lets ModMenu list the mod
mod: ksarmory
category: ui
techniques: [starmap-lifecycle, imgui-window, imgui-menu, imgui-drawlist, inter-mod-api, reflection]
hooks: [starmap StarMapBeforeGui, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Program, Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImDrawListExtensions, KSA.Universe]
tags: [imgui, panel, windows, tabs, tables, menu-bar, modmenu, draw-list, icons, tooltips]
---

KSArmory's whole operator interface is Dear ImGui through KSA's `Brutal.ImGuiApi` binding, drawn
every frame from the `[StarMapAfterGui]` hook right after the simulation step. It is a set of
independent top-level windows rather than one big window: a craft switcher, a per-craft window
with tabs, a weapons switcher with the FIRE trigger, pop-out session and debug panes, plus the
scope and map windows. A top-level `KSArmory` menu is opened from `[StarMapBeforeGui]`, and a
copied `[ModMenuEntry]` attribute lets the third-party ModMenu list it under "Mods" without a
dependency.[^ksarmory-ui][^ksarmory-modmenuentry]

Modders can lift: the window inventory pattern (one `Begin/End` per window, each with its own open
flag, `###id` titles), icon buttons drawn with the window draw list, tooltips via
`BeginItemTooltip`, a recovery button when the main window is closed, and the zero-dependency
ModMenu integration.

## How it works

**Window inventory** (all top-level; none nested in another `Begin/End`):

| Window (`###id`) | Opened by | Content |
|---|---|---|
| `KSArmory {version}###KSArmory` | `Visible`, the menu, the reopen button | Craft switcher table, report footer |
| `KSArmory##reopen` | panel closed and `FloatingPanelButton` | One button, `AlwaysAutoResize \| NoTitleBar` |
| `{craft}###KSArmorySystem` | a row's "..." | Header (hold reason), `BeginTabBar`: Components, Ballistic, Radar/Emitters, Tuning |
| `Weapons - {craft}###KSArmoryWeapons` | the row's missile icon | Weapon groups, ammo, FIRE, hold line |
| `KSArmory settings`, `Teams`, `Debug tools`, `Test targets`, `Log` | buttons on the panel | `Pane` records: title, body delegate, open flag |
| Report, Map, Scope | their buttons | See the scope and map feature |

`Ui.Draw` order: set `Current`, `KsaWorld.InvalidateCensus()` (the UI walks the world in its own
pass), refresh the surveyed systems every 60 frames, sync rosters, follow a weapon a decoupler
carried to another craft, compute `Focused` (weapons window craft, else managed craft, else the
roster default), then the main window or the reopen button, then every other window, each gated
only on its own flag.[^ksarmory-ui]

**Craft switcher.** `BeginTable("##switcher", 6, SizingStretchProp)`, one row per craft that
carries an installation (`WeaponSurvey.Survey` over surveyed parts), grouped by team. Each row:
name (click to fly, right-click to watch), guard (shield), chase (camera), weapons (missile), team
(flag), "...". Guard uses `GuardState` to show Off/Partly/Guarding over every system aboard.

**Drawn icons.** KSA's fonts are said (by the mod) to carry only basic Latin, so icons are drawn:
`IconButton` makes a square `ImGui.Button` the frame height, then draws filled rects, triangles,
circles, a quad and a line on `ImGui.GetWindowDrawList()` inside `GetItemRectMin/Max`.

**Per-craft window.** `Focus(craft)` points the shared `_system`/`_policy` fields at that craft's
weapon system before any tab body runs (tab bodies only run for the selected tab). Panes in
`UiSystem.cs` read status (components, sensor, turret, track list); panes in `UiTuning.cs` write
sliders straight into the shared `SensorProfile`/`MunitionProfile`/`LauncherProfile` instances, so a
tweak reaches every system using that profile, while IFF and switches edit the per-system
`SystemConfig`.[^ksarmory-uisystem][^ksarmory-uituning]

**Session and debug panes.** `UiSession.cs` holds world-level switches (warp hold, stage disposal,
part damage, tank leaks, nuclear blackout, shared target count, drawing and sound); `UiDebug.cs`
holds development tools (burst tool, craft mover, send-to-body, test targets, logging, diagnostics,
log view).[^ksarmory-uisession][^ksarmory-uidebug]

**Patterns used throughout.**

- `Tip(text)` = `BeginItemTooltip` + wrapped text + `EndTooltip`, after nearly every control.
- `PushID(i)`/`PopID()` around loops of identical widgets; `##` suffixes for hidden labels.
- `BeginDisabled(empty)` around buttons that cannot act (no flares aboard).
- `TextField` marshals a string through a 64-byte `stackalloc` buffer for `InputText`.
- A craft switch never edits the wrong system: `Focus` is re-run inside the window that is drawing.

**Menu entry.** `DrawMenuBarEntry` (from `[StarMapBeforeGui]`) returns at once if an assembly named
`ModMenu` is loaded; otherwise `BeginMainMenuBar`, `BeginMenu("KSArmory")`, one `MenuItem("Panel")`
toggling `Visible`, all inside a try/catch that warns once.

**ModMenu.** `ModMenuEntryAttribute` is a private copy of ModMenu's attribute (method target,
`MenuName`, optional active-property name). The static `Ui.DrawModMenu` is tagged
`[ModMenuEntry("KSArmory")]` and calls `Current?.DrawMenuContents()`. Per the mod's comments, ModMenu
scans loaded assemblies and matches the attribute by type name only, so the copy is enough and is
inert without ModMenu; ModMenu itself is not in this corpus, so that side is unverified.

## Lifecycle and timing

| Hook | Firing point (build 5554) | What runs |
|---|---|---|
| `[StarMapBeforeGui]` | Harmony prefix on `Program.OnDrawUiFrame`, Program.cs L2267, inside `if (DrawUI)` | `Ui.DrawMenuBarEntry()`, errors swallowed |
| `[StarMapAfterGui]` | Harmony postfix on `Program.OnDrawUiViewports`, Program.cs L2268, inside `if (DrawUI)` | `StepOnce` first, then `_ui.Draw()`, then the world overlays |

Both sit between `PrepareImGui` (ImGui new frame, Program.cs L2261) and `ImGui.Render()` (L2286), so
every ImGui call is legal. Both run after `PrepareFrame` applied the last vehicle step
(`Universe.ApplyVehicleSolvers`, Universe.cs L1922/L1948) and dispatched the next
(`ExecuteNextVehicleSolvers`, Program.cs L2220), so the panel shows state at
`Universe.GetElapsedTime()` (Universe.cs L2399) that this frame's `StepOnce` has just advanced the
mod's own simulation to.[^ksa-program][^ksa-universe]

Consequences:

- **The panel vanishes with the HUD.** F2 or a screenshot sets `Program.DrawUI = false` and neither
  hook runs; the simulation keeps stepping from the fallback hooks, but nothing in the panel is
  drawn or clickable.
- **Clicks act after the step.** A button pressed here (FIRE, Flares, a slider) changes state that
  the next frame's step consumes; a missile fired from FIRE is first integrated next frame.
- **Submission order is layering.** `OnAfterGui` submits the panel before the full-screen marker
  overlays, so the overlays sit over the world and under the panel (KSArmoryMod.cs L582-L584).[^ksarmory-mod]
- **KSA's own menu bar is drawn later.** `OnDrawUiThreadSafe` (Program.cs L3178), after both hooks,
  calls `DrawMenuBar` (L3188, L3455-L4016), which opens a regular window named `"Menu Bar"` with
  `ImGuiWindowFlags.MenuBar` and `BeginMenuBar`, not ImGui's main menu bar. `OnDrawUiFrame`
  (L3131-L3160) draws alerts, popups and debug windows, not the bar.

Settings changed in the panel are persisted by comparison every frame (`_roster.Remember()` at the
end of `StepSimulation`); see the per-craft settings sidecar feature.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/Ui.cs#L10-L127) | Constructor wiring, `Focus`, `Pane` records, menu-bar rationale |
| [src/KSArmory/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/Ui.cs#L129-L196) | `DrawMenuBarEntry`, `DrawMenuContents`, `[ModMenuEntry]` `DrawModMenu` |
| [src/KSArmory/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/Ui.cs#L202-L295) | `Draw`: census, focus, main window, reopen button, other windows |
| [src/KSArmory/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/Ui.cs#L480-L652) | Switcher row buttons: guard, chase, weapons, team |
| [src/KSArmory/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/Ui.cs#L708-L756) | `IconButton`, `DrawIcon` |
| [src/KSArmory/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/Ui.cs#L760-L828) | Per-craft window with tab bar |
| [src/KSArmory/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/Ui.cs#L917-L929) | `DrawPanes` |
| [src/KSArmory/Ksa/Ui/ModMenuEntry.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/ModMenuEntry.cs) | Copied attribute, `ModMenuPresence` |
| [src/KSArmory/Ksa/Ui/UiSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiSystem.cs) | Status panes: components, dispensers, sensor, turret, track list |
| [src/KSArmory/Ksa/Ui/UiTuning.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiTuning.cs) | Live sliders on shared profiles |
| [src/KSArmory/Ksa/Ui/UiWeapons.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiWeapons.cs) | Weapons window, FIRE by station group, hold line |
| [src/KSArmory/Ksa/Ui/UiSession.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiSession.cs) | Session settings, display and sound panes |
| [src/KSArmory/Ksa/Ui/UiDebug.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiDebug.cs) | Development tools and log view |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L487-L524) | `OnBeforeGui`, `OnAfterGui` entry |

## Snippets

```csharp
// src/KSArmory/Ksa/Ui/Ui.cs L129-L156
    public void DrawMenuBarEntry()
    {
        // ModMenu draws this mod's entry when it is installed -- see DrawModMenu. Drawing a
        // second one here would list KSArmory twice in the same bar.
        if (ModMenuPresence.Installed) return;

        try
        {
            if (!ImGui.BeginMainMenuBar()) return;

            if (ImGui.BeginMenu("KSArmory"))
            {
                DrawMenuContents();
                ImGui.EndMenu();
            }

            ImGui.EndMainMenuBar();
        }
        catch (Exception e)
        {
            // Never take the panel down with it. A menu that does not appear is a cosmetic loss;
            // an exception here happens inside KSA's own GUI pass.
            if (_warnedMenuBar) return;

            _warnedMenuBar = true;
            Log.Warn($"menu bar entry failed, use the floating button: {e.Message}");
        }
    }
```

```csharp
// src/KSArmory/Ksa/Ui/ModMenuEntry.cs L19-L37
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class ModMenuEntryAttribute(string menuName, string? isModMenuActivePropertyName = null)
    : Attribute
{
    public string MenuName { get; } = menuName;

    public string? IsModMenuActivePropertyName { get; } = isModMenuActivePropertyName;
}

/// <summary>Whether ModMenu is loaded, so this mod does not draw its own menu as well.</summary>
internal static class ModMenuPresence
{
    private static bool? _present;

    /// <summary>
    /// True when ModMenu is in the process. Resolved once: assemblies do not come and go, and this
    /// is asked every frame by the menu-bar draw.
    /// </summary>
    public static bool Installed => _present ??= Detect();
```

```csharp
// src/KSArmory/Ksa/Ui/Ui.cs L712-L735
    private static bool IconButton(string id, Icon icon, ImColor8 ink, bool lit)
    {
        float side = ImGui.GetFrameHeight();

        if (lit) ImGui.PushStyleColor(ImGuiCol.Button, LitButton);
        bool clicked = ImGui.Button(id, new float2?(new float2(side, side)));
        if (lit) ImGui.PopStyleColor();

        DrawIcon(ImGui.GetWindowDrawList(), icon, ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ink);
        return clicked;
    }

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
```

```csharp
// current/decomp/KSA/Program.cs L3481-L4020
ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.MenuBar;
ImGui.SetNextWindowViewport(viewport.ImGuiId);
ImGui.SetNextWindowSize(new float2(windowWidth, 0f), ImGuiCond.Always);
ImGui.SetNextWindowPos(viewport.Position, ImGuiCond.Always, (float2?)null);
ConsoleStyle.PushWidgetStyle();
ImGui.Begin("Menu Bar"u8, flags);
if (ImGui.BeginMenuBar())
{
    float cursorPosY = ImGui.GetCursorPosY();
    if (ImGui.BeginMenu("File"u8))
// ...
    DrawProgramMenusHook();
// ...
    ImGui.EndMenuBar();
}
ImGui.End();
ConsoleStyle.PopWidgetStyle();
}

public void DrawProgramMenusHook()
{
}
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Program.OnDrawUiFrame`, `Program.OnDrawUiViewports` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3176) | Targets of the StarMap prefix and postfix the panel hangs off |
| `Program.DrawMenuBar`, `Program.DrawProgramMenusHook` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3455-L4020) | KSA's menu bar window; the empty hook method inside it (not used by this mod) |
| `Program.DrawUI` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L526) | Gate on both hooks (L2265-L2269) |
| `ImGui.Begin(name, ref open, flags)`, `BeginTabBar`, `BeginTabItem`, `BeginTable`, `BeginItemTooltip` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5584) | Windows (L5584), tabs (L7736, L7760), tables (L7588), tooltips (L7485)[^brutal-imgui] |
| `ImGui.BeginMainMenuBar`, `ImGui.BeginMenu`, `ImGui.BeginMenuBar` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7409-L7429) | The mod's own top-level menu |
| `ImGui.GetWindowDrawList`, `ImDrawListPtr.AddRectFilled/AddTriangleFilled/AddCircleFilled/AddQuadFilled` | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L73-L165) | Drawn icons (`GetWindowDrawList` at ImGui.cs L5648)[^brutal-drawlistext] |

## Pitfalls and version notes

- **Docs vs build 5554, menu bar.** docs/BLOCKED-ON-KSA.md L249-L278 and the Ui.cs comment (L110-L127)
  say KSA's bar has "no extension point of any kind", that `BeginMainMenuBar` appends to KSA's bar,
  and that from `[StarMapAfterGui]` "the bar has already been ended for the frame". In build 5554:
  KSA's bar is its own `"Menu Bar"` window with `BeginMenuBar` (Program.cs L3486-L3487), drawn in
  `OnDrawUiThreadSafe` **after** both StarMap GUI hooks, and never calls `BeginMainMenuBar`; so the
  mod's `BeginMainMenuBar` builds ImGui's separate main menu bar rather than appending to KSA's, and
  the "already ended" explanation does not follow from KSA's frame order (unverified at runtime).
  Build 5554 also has `Program.DrawProgramMenusHook()`, an empty public method called inside KSA's
  bar after the HUD menu (L4005, L4018), which a Harmony postfix could use as the extension point
  the doc asks for (unverified here; the mod does not use it).[^ksarmory-blocked][^ksa-program]
- **ModMenu matching is by type name.** The copy must keep the class name `ModMenuEntryAttribute`;
  ModMenu's code is not in this corpus, so its scanning behaviour is the mod's description.
- **Keep the reopen button unconditional.** The setting that would restore a closed panel lives
  inside that panel; KSArmory draws its floating button whenever the panel is closed and the
  option is on, even with ModMenu present.
- **Tab bodies only run for the selected tab.** Anything other windows read (the map's head list)
  is filled in `Draw`, not inside a tab.
- **Shared tuning.** A slider in Tuning edits a profile instance shared by every system of that
  type; per-installation settings live on `SystemConfig`.
- **Fonts.** The mod draws icons because, per its comment, KSA's fonts have basic Latin only; not
  verified against the font atlas.

## See also

- [Radar scope and relief map](./radar-scope-and-relief-map.md)
- [World-anchored screen markers](./world-anchored-screen-markers.md)
- [IFF teams and allegiance](./iff-teams-and-allegiance.md)
- [Fire control and target allocation](./fire-control-and-target-allocation.md)
- [Per-craft settings JSON sidecar](./per-craft-settings-json-sidecar.md)
- [StarMap entry and fault containment](./starmap-entry-and-fault-containment.md)
- [Dual-hook simulation step](./dual-hook-simulation-step.md)
- [Spawn and teleport test craft](./spawn-and-teleport-test-craft.md)
- [In-game bug reports](./in-game-bug-reports.md)
- [Director camera window](./director-camera-window.md)
- [Draw icon buttons from primitives, with no textures or icon font](../../../howto/ui/draw-lists/build-custom-drawn-imgui-widgets/build-custom-drawn-imgui-widgets-2.md) - how-to article demonstrating this feature
- [Add a menu with BeginMainMenuBar from StarMapBeforeGui, and why it is not KSA's bar](../../../howto/ui/menus/add-items-to-the-game-menu-bar/add-items-to-the-game-menu-bar-2.md) - how-to article demonstrating this feature
- [Copy the ModMenuEntry attribute into your mod so there is no dependency and no extra DLL](../../../howto/ui/menus/list-a-mod-in-modmenu/list-a-mod-in-modmenu-3.md) - how-to article demonstrating this feature
- [Tabbed window: a per-craft window with tabs, pop-out panes and a reopen button](../../../howto/ui/windows/build-a-tabbed-imgui-window/build-a-tabbed-imgui-window-2.md) - how-to article demonstrating this feature

[^ksarmory-ui]: src/KSArmory/Ksa/Ui/Ui.cs
[^ksarmory-modmenuentry]: src/KSArmory/Ksa/Ui/ModMenuEntry.cs
[^ksarmory-uisystem]: src/KSArmory/Ksa/Ui/UiSystem.cs
[^ksarmory-uituning]: src/KSArmory/Ksa/Ui/UiTuning.cs
[^ksarmory-uisession]: src/KSArmory/Ksa/Ui/UiSession.cs
[^ksarmory-uidebug]: src/KSArmory/Ksa/Ui/UiDebug.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-blocked]: docs/BLOCKED-ON-KSA.md
[^ksa-program]: KSA.Program (decompiled)
[^ksa-universe]: KSA.Universe (decompiled)
[^brutal-imgui]: Brutal.ImGuiApi.ImGui (decompiled)
[^brutal-drawlistext]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled)
