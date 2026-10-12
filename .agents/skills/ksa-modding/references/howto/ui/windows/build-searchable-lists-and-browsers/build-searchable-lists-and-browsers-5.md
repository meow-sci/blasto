---
type: How-To
title: "Searchable lists: a draggable splitter between two panes, a universe guard and a go-to button"
description: Build a two-pane browser whose splitter is a thin button moved by MouseDelta, wait for a loaded universe before reading it, and move the camera to the selected body with Universe.MoveCameraTo
category: ui
group: windows
topic: build-searchable-lists-and-browsers
approach: 5
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/compendium/features/celestial-info-window]
techniques: [imgui-window, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Universe, KSA.Program, KSA.Camera, KSA.ConsoleStyle, Brutal.ImGuiApi.ImGui]
tags: [splitter, two-pane, browser, begin-child, mouse-delta, wait-for-universe, move-camera, compendium]
---
Compendium is a browser over every body in the loaded star system: a left pane with category buttons and a body list, a right pane with the selected body's page, and a draggable splitter between them. Three pieces generalise: the splitter (a thin `Button` plus `MouseDelta` between two `BeginChild` panes), the guard that waits for a universe to exist before reading it, and the "go to" buttons that focus the camera on a body or a vessel with `Universe.MoveCameraTo`. Demonstrated by Compendium. Prerequisite: StarMap.

## Background: how the game does it

- **`Universe.WorldSun` is null until a system is loaded.** It is assigned in the load path together with `CurrentSystem` and `Roots`, so every universe-reading UI needs a guard.[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L106-L106)[^ksa-universe]
- **`Universe.MoveCameraTo(Astronomical)`** takes the hovered viewport's camera and calls `Camera.SetFollow(obj, camera.TidalLocking, hoveredViewport.IsMain())`.[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2527-L2532)[^ksa-universe] For the main viewport the third argument, `changeControl`, is true, and `SetFollow` then assigns `Program.ControlledVehicle = target as Vehicle`, so following a planet **releases** the controlled vehicle (null) while following a vehicle takes control of it.[current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L602-L618)[^ksa-camera]
- **`Program.SetOrbitCamera(azimuth, elevation, distancePower)`** sets the orbit camera angles; Compendium uses it to frame the whole system.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3041-L3041)[^ksa-program]
- **Console look.** `ConsoleStyle.PushWidgetStyle()` / `PopWidgetStyle()` give the window the game's widget style; see [match the stock console UI style](../match-the-stock-console-ui-style/overview.md).

## Lifecycle and timing

`[StarMapAllModsLoaded]` runs before any universe exists, so the early category build finds no bodies and the real tree is built on the first `[StarMapAfterGui]` frame that has a universe. The window draws in `frame:draw-ui`, inside `if (DrawUI)`, after the cameras were updated; camera and control changes made here take effect from the next frame. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Guard on the universe.** Show a placeholder and return while `Universe.WorldSun` is null; build the lazy data on the first frame that has one:

```csharp
// src/CompendiumMain.cs L73-L99
// Wait for universe to be fully loaded
if (Universe.WorldSun == null)
{
    if (ImGui.Begin("Compendium", ref CompendiumWindow))
    {
        ImGui.Text("Waiting for universe to load...");
        ImGui.End();
    }
    return;
} else if (!processedBodyJsonDict)
{
    AttachInfoBodyJsonDict();
    processedBodyJsonDict = true;

    // now that the full jsonBodyDict is processed, for debugging purposes export it as a json named "ProcessedBodyJsonDict.json" in the Compendium folder
    //string processedJsonPath = Path.Combine(parentDir ?? "", "ProcessedBodyJsonDict.json");
    //string processedJsonContent = JsonSerializer.Serialize(Compendium.bodyJsonDict, new JsonSerializerOptions { WriteIndented = true });
    //File.WriteAllText(processedJsonPath, processedJsonContent);
}

// Build category tree on first render when Universe is loaded, or rebuild if it was built without celestials
if (buttonsCatsTree == null || buttonsCatsTree.Count == 0 || !Compendium.categoriesBuiltWithCelestials || categoriesBuiltForRoot != GetSelectedRoot())
{
    CategoryLoader(GetSelectedRoot());
    categoryKeys = GetCategoryKeys();
    selectedCategoryKey = GetDefaultCategoryKey(categoryKeys);
    selectedCategoryIndex = -1;
```

2. **Begin the window and the left pane**, pushing the console widget style and remembering that you did, so an exception can pop it:

```csharp
// src/CompendiumMain.cs L135-L144
ConsoleStyle.PushWidgetStyle();
consoleStylePushed = true;
CaptureWindowRectForPointer();
DrawAtmosphereDensityGraphPopup();

// Get available window size
float2 windowSize = ImGui.GetContentRegionAvail();

// Main content area
ImGui.BeginChild("MainContent", new float2(mainContentWidth, 0));
```

3. **Draw the splitter between the panes.** A fixed-width button the full height of the window; while it is the active item, add the horizontal `MouseDelta` to the left pane's width, clamp it, and show the resize cursor on hover. The right pane takes the remaining width:

```csharp
// src/CompendiumMain.cs L385-L405
ImGui.EndChild();

// Resizable splitter
ImGui.SameLine();
ImGui.Button("##splitter", new float2(8f, -1));
if (ImGui.IsItemActive())
{
    mainContentWidth += ImGui.GetIO().MouseDelta.X;
    // Clamp to reasonable values
    if (mainContentWidth < 200f) mainContentWidth = 200f;
    if (mainContentWidth > windowSize.X - 250f) mainContentWidth = windowSize.X - 250f;
}
if (ImGui.IsItemHovered())
{
    ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
}

// Side pane - calculate remaining width
ImGui.SameLine();
float sidePaneWidth = windowSize.X - mainContentWidth - 16f; // Account for splitter and spacing
ImGui.BeginChild("SidePane", new float2(sidePaneWidth, 0));
```

4. **Add go-to buttons.** The S button frames the system's primary star; a vessel picker lists the system's vehicles and takes control of the clicked one:

```csharp
// src/CompendiumMain.cs L195-L204
if (ImGui.Button(" S ##SunButton", new float2(buttonSWidth, 0)))
{
    // Focus camera on the selected system's primary star / barycenter
    var sun = GetSelectedRoot();
    if (sun != null)
    {
        KSA.Universe.MoveCameraTo(sun);
        KSA.Program.SetOrbitCamera(0.3, -0.43, 905); // azimuth, elevation, distancePower
    }
}
```
```csharp
// src/CompendiumMain.cs L223-L244
if (showVesselDropdown)
{
    ImGui.SetNextWindowPos(ImGui.GetItemRectMin() + new float2(0, ImGui.GetItemRectSize().Y), ImGuiCond.Always, (float2?)null);
    if (ImGui.Begin("##VesselPicker", ref showVesselDropdown, ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
    {
        var vessels = Universe.CurrentSystem?.All.UnsafeAsList().OfType<Vehicle>().ToList();
        bool foundVessels = false;

        if (vessels != null)
        {
            foreach (var v in vessels)
        {
            foundVessels = true;
            bool isCurrent = Program.ControlledVehicle != null && Program.ControlledVehicle.Id.ToString() == v.Id;
            
            if (isCurrent) ImGui.PushStyleColor(ImGuiCol.Text, new float4(0.4f, 1.0f, 0.4f, 1.0f));
            ImString vesselLabel = new ImString(v.Id.ToString());
            if (ImGui.Selectable(vesselLabel))
            {
                Program.ControlledVehicle = v;
                KSA.Universe.MoveCameraTo(v);
                showVesselDropdown = false;
```

5. **Contain failures.** One `try` wraps the whole draw. On an exception the catch pops the style if it was pushed and closes the child and window, each in its own `try`, so a bug does not leave ImGui's stacks unbalanced:

```csharp
// src/CompendiumMain.cs L1070-L1090
    ImGui.EndChild(); // End side pane

    ConsoleStyle.PopWidgetStyle();
    consoleStylePushed = false;
    ImGui.End();

}
catch (Exception ex)
{
    Console.WriteLine($"Compendium OnAfterUi error: {ex.Message}");
    Console.WriteLine($"Stack trace: {ex.StackTrace}");
    
    // Try to clean up ImGui state on exception

    if (consoleStylePushed)
    {
        try { ConsoleStyle.PopWidgetStyle(); } catch { }
    }
    try { ImGui.EndChild(); } catch { }
    try { ImGui.End(); } catch { }
}
```

## Complete example

| File | Role |
|---|---|
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L56-L144) | Hook, universe guard, window setup |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L195-L254) | Go-to buttons and vessel picker |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L385-L405) | Splitter and side pane |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1070-L1090) | Teardown and failure containment |

## Variations and gotchas

- **Following a body releases the controlled vehicle.** `MoveCameraTo` on a non-vehicle on the main viewport sets `ControlledVehicle` to null; a UI that offers both "go to this planet" and "control this vessel" should say so.
- **The catch is best effort.** It does not know how many scopes were open, so `EndChild`/`End` in the catch can themselves fail (hence the nested `try`s); balance scopes properly in the normal path.
- **Clamp the pane width** against the window size so the splitter cannot lose a pane.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of build-searchable-lists-and-browsers](overview.md)
- [Approach 2: searchable star-system combo in the same window](build-searchable-lists-and-browsers-2.md)
- [Match the stock console UI style](../match-the-stock-console-ui-style/overview.md)
- Mod feature: [Compendium celestial info window](../../../../mods/compendium/features/celestial-info-window.md)

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
