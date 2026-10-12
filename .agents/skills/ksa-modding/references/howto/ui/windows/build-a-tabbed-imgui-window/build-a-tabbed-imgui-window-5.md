---
type: How-To
title: "Tabbed window: collapsible sections that pop out into their own windows"
description: Draw each panel section from one delegate that runs either docked inside the main window or inside its own window, with open and popped state keyed by a stable section id
category: ui
group: windows
topic: build-a-tabbed-imgui-window
approach: 5
difficulty: advanced
prerequisites: [harmony]
demonstrated_by: [mods/kitten-engineer-redux/features/draw-list-widget-kit]
techniques: [imgui-window, imgui-drawlist, harmony-postfix]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi, harmony-postfix KSA.VehicleEditor.OnDrawUi]
lifecycle: [frame:draw-ui]
game_types: [KSA.Vehicle, KSA.GameViewport, Brutal.ImGuiApi.ImGui]
tags: [sections, pop-out, dock, stable-id, draw-list, harmony-postfix, kitten-engineer-redux]
---
Kitten Engineer Redux (KER) does not use tabs. Its flight HUD is one window of collapsible sections (LIVE, FUEL, ORBIT, SUICIDE BURN, STAGES and so on) and each section bar carries a "Pop out" button that moves the section into a window of its own, with a "Dock" button to put it back. The trick that makes this cheap is that a section is a delegate, not a layout: the same function draws the content whether it is docked or floating, and two flags per section, both saved by id, say which. This
is the pattern to copy when a panel has more panes than fit on screen, or when players want some of them next to the navball and others elsewhere. Demonstrated by Kitten Engineer Redux. Prerequisite: a Harmony patch (or any other hook that runs during the UI pass).

## Background: how the game does it

KER hosts its windows from Harmony postfixes on the game's own UI calls, not from a StarMap GUI hook. `Vehicle.OnDrawUi(IGameViewport)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3848-L3848)) is a per-vehicle world-UI call that `CelestialSystem.OnDrawUi` makes for each astronomical in view, reached from `Universe.OnDrawUi(viewport)`.[current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L406-L406)[^ksa-celestialsystem] While the editor is open (`Program.EditorFlag`) the game skips `Universe.OnDrawUi` and calls `Program.Editor?.OnDrawUi(MainViewport)` instead.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3229-L3239)[^ksa-program] Both calls are in `Program.OnDrawUiThreadSafe`, which `OnFrame` runs only when `DrawUI` is true, between `PrepareImGui` and `ImGui.Render`, so ImGui calls are legal and the panel disappears with the rest of the UI when the HUD is hidden.

There is nothing in the game for pop-out sections. This is ordinary Dear ImGui: a window is a `Begin`/`End` pair whose identity is its title after `###`, and two windows submitted one after the other are independent.

## Lifecycle and timing

The patch runs in `frame:draw-ui`, once per call of `Vehicle.OnDrawUi` for a vehicle in view. `CelestialSystem.OnDrawUi` culls objects outside the camera's field of view but never the controlled vehicle (it is always kept, given distance -1 and so sorted last)[current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L376-L388)[^ksa-celestialsystem] so KER's filter (main viewport and controlled vehicle) fires once per frame while a vehicle is controlled and the editor is closed. The pop-out windows are submitted by the same postfix after the main window ends, so they appear and disappear with it and read the same per-frame data snapshot. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Hook the draw call** with a Harmony postfix and filter to the one vehicle you present for:

```csharp
// Patch_FlightHud.cs L6-L18
[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.OnDrawUi))]
internal static class Patch_FlightHud
{
    private static void Postfix(Vehicle __instance, GameViewport inViewport)
    {
        if (inViewport != Program.MainViewport)
            return;
        if (Program.ControlledVehicle != __instance)
            return;

        FlightHud.Draw(__instance, inViewport);
    }
}
```

2. **Model a section** as an id, a title, a draw delegate with the draw-list shape `(dl, origin, width, y) -> y`, and the two flags. The constructor builds the window title once with a `###` stable id, and registers itself, which immediately applies any saved state:

```csharp
// SectionHost.cs L8-L40
internal delegate float SectionDraw(ImDrawListPtr dl, float2 origin, float width, float y);

internal sealed class HudSection
{
    public HudSection(string id, string title, SectionDraw draw)
    {
        Id = id;
        Title = title;
        PoppedTitle = $"{title} (separate window)";
        Draw = draw;
        WindowTitle = Encoding.UTF8.GetBytes($"{title}###KerSection_{id}");
        SectionRegistry.Register(this);
    }

    public string Id { get; }
    public string Title { get; }
    public string PoppedTitle { get; }
    public SectionDraw Draw { get; }
    public byte[] WindowTitle { get; }
    public bool Open { get; set; }
    public bool Popped { get; set; }
}

internal static class SectionRegistry
{
    public static readonly List<HudSection> All = new();

    public static void Register(HudSection section)
    {
        All.Add(section);
        SettingsStore.ApplySection(section);
    }
}
```

3. **Declare the sections once**, as a static array, so the same objects serve the docked pass and the floating pass:

```csharp
// FlightHud.cs L36-L47
private static readonly HudSection[] Sections =
{
    new("live", "LIVE", DrawLiveSection),
    new("fuel", "FUEL", DrawFuelSection),
    new("orbit", "ORBIT", DrawOrbitSection),
    new("suicide", "SUICIDE BURN", DrawSuicideSection),
    new("stages", "STAGES", DrawStagesSection),
    new("elements", "ORBIT ELEMENTS", DrawElementsSection),
    new("planner", "ORBIT PLANNER", DrawPlannerSection),
    new("surface", "SURFACE", DrawSurfaceSection),
    new("history", "HISTORY", DrawHistorySection),
};
```

4. **Draw docked sections inside the main window.** `DrawDocked` draws the bar, applies a click on the bar or on the pop-out button before deciding whether to draw the content, so the change shows in the same frame:

```csharp
// SectionHost.cs L47-L61
public static float DrawDocked(HudSection section, ImDrawListPtr dl, float2 origin, float width, float y)
{
    y = DrawBar(dl, origin, width, y, section, false, out bool toggleOpen, out bool togglePopped);
    if (toggleOpen)
        section.Open = !section.Open;
    if (togglePopped)
        section.Popped = !section.Popped;

    if (section.Open && !section.Popped)
    {
        y = section.Draw(dl, origin, width, y);
        y += 4f;
    }
    return y;
}
```

5. **Draw popped sections after the main window ends.** `DrawFloating` returns at once for docked sections; otherwise it opens a separate window (cascaded 28 px per section index on first use), draws the bar in its floating form, and calls the same delegate:

```csharp
// SectionHost.cs L63-L86
public static void DrawFloating(HudSection section, GameViewport viewport, int index)
{
    if (!section.Popped)
        return;

    float2 defaultPos = viewport.Position + new float2(380f + index * 28f, 80f + index * 28f);
    bool open = PanelKit.BeginWindow(section.WindowTitle, defaultPos, new float2(FloatingWidth, FloatingHeight));
    if (open)
    {
        float width = ImGui.GetContentRegionAvail().X;
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float2 origin = ImGui.GetCursorScreenPos();
        PanelKit.DrawAccentStrip(dl, origin, ImGui.GetWindowSize().Y);

        float y = origin.Y;
        y = DrawBar(dl, origin, width, y, section, true, out _, out bool dock);
        if (dock)
            section.Popped = false;

        y = section.Draw(dl, origin, width, y);
        PanelKit.EndContent(origin, width, y);
    }
    PanelKit.EndWindow();
}
```

6. **Wire both passes into the host window** - one loop inside the main window, one after `EndWindow`:

```csharp
// FlightHud.cs L191-L199
    for (int i = 0; i < Sections.Length; i++)
        y = SectionHost.DrawDocked(Sections[i], dl, origin, contentWidth, y);

    PanelKit.EndContent(origin, contentWidth, y);
}
PanelKit.EndWindow();

for (int i = 0; i < Sections.Length; i++)
    SectionHost.DrawFloating(Sections[i], viewport, i);
```

7. **Persist by id.** Open/Popped are saved per section id and re-applied when a section is registered or when settings load, so a section stays popped across restarts:

```csharp
// SettingsStore.cs L121-L128
public static void ApplySection(HudSection section)
{
    if (SectionStates.TryGetValue(section.Id, out SectionStateData? state))
    {
        section.Open = state.Open;
        section.Popped = state.Popped;
    }
}
```
```csharp
// SettingsStore.cs L185-L188
foreach (KeyValuePair<string, SectionStateData> pair in SectionStates)
    data.Sections[pair.Key] = pair.Value;
foreach (HudSection section in SectionRegistry.All)
    data.Sections[section.Id] = new SectionStateData { Open = section.Open, Popped = section.Popped };
```

## Complete example

| File | Role |
|---|---|
| [Patch_FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Patch_FlightHud.cs#L6-L18) | Harmony postfix on `Vehicle.OnDrawUi` |
| [SectionHost.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SectionHost.cs#L8-L130) | `HudSection`, registry, docked and floating hosts, section bar |
| [PanelKit.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/PanelKit.cs#L33-L65) | `BeginWindow`/`EndWindow`/`EndContent` used by both hosts |
| [FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L36-L199) | Section table and the two loops |
| [SettingsStore.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SettingsStore.cs#L121-L240) | Saved section state |

## Variations and gotchas

- **Pair `BeginWindow` with `EndWindow` even when `Begin` returns false.** The style and font pushes in `PanelKit.BeginWindow` happen before `ImGui.Begin`, so the mod always calls `EndWindow`.
- **Unique widget ids per section.** The bar uses `"##KerBar" + section.Id` and `"##KerPop" + section.Id` for its `InvisibleButton`s; without the id suffix, the docked and floating bars would collide.
- **Floating position is the first-use default only.** After the first use ImGui remembers the position for the `###KerSection_<id>` window name; the popped state itself is KER's own saved state.
- **Section content must not depend on the host.** The delegate gets a draw list, an origin and a width, so it renders identically in either window; reading cached frame data (not the live vehicle) keeps the two passes consistent.
- **The editor uses the same pattern** with a single section (`EditorPanel.cs` L80 and L89), patched on `VehicleEditor.OnDrawUi`.
- **The postfix declares `GameViewport` but the target takes `IGameViewport`.** Harmony binds injected parameters by name here, and it only works because `GameViewport` is the sole implementation; secondary viewports reach the postfix too and are rejected by the reference comparison. Prefer the interface type in your own patches (per the mod feature page; not re-tested).
- **Popped windows share the HUD's fate.** They are drawn from inside the vehicle's `OnDrawUi`, so they disappear with the HUD (editor open, no controlled vehicle, UI hidden) and come back for the next controlled vehicle.
- **Version.** Checked against 2026.10.10.5554; the patch targets the public `Vehicle.OnDrawUi` and `VehicleEditor.OnDrawUi` signatures.

## See also

- [Overview of build-a-tabbed-imgui-window](overview.md)
- [Approach 2: per-craft window with pop-out panes](build-a-tabbed-imgui-window-2.md)
- [Build custom-drawn ImGui widgets, icons, trees and alert tiles on the draw list](../../draw-lists/build-custom-drawn-imgui-widgets/overview.md)
- [Inject UI into the game's own windows and menus, including the orbit right-click menu](../../menus/inject-ui-into-a-stock-window/overview.md)
- [Build a settings window](../build-a-settings-window/overview.md)
- Mod feature: [Draw-list widget kit](../../../../mods/kitten-engineer-redux/features/draw-list-widget-kit.md)

[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
