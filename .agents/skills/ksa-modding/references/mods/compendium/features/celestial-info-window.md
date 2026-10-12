---
type: Mod Feature
title: Celestial compendium window
description: A two-pane ImGui window drawn from StarMapAfterGui that joins JSON category data with the live body tree of the selected star system, with category buttons, collapsible body lists, a draggable splitter, per-view side pane, a star-system combo with search and a vessel picker
mod: compendium
category: ui
techniques: [imgui-window, imgui-drawlist, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types:
  - KSA.Universe
  - KSA.CelestialSystem
  - KSA.Celestial
  - KSA.Astronomical
  - KSA.IParentBody
  - KSA.Program
  - KSA.Vehicle
  - KSA.ConsoleStyle
  - KSA.ConsoleWidgets
  - KSA.LookupCollection
  - Brutal.ImGuiApi.ImGui
tags: [imgui, window, ui, celestials, categories, splitter, starmap]
---

The "Compendium" window lists every body of the selected star system by category (Stars, Planets,
Dwarf Planets, Asteroids, Comets and so on, plus Other for bodies without data). The left pane holds
the category buttons and the body list; the right pane shows the selected body, category, subgroup,
a terms glossary, or a start page with the mod's logo and global orbit-line toggles. Top-row buttons
reset the view (M), focus the camera on the system's star (S), pick the controlled vessel (V) and open
the glossary (D). The window remembers whether it is open across sessions, and ImGui's own `imgui.ini`
keeps its position and size.[^cmp-compendiummain] [^cmp-cdmsettings]

## How it works

1. **Wait for a universe.** Each frame the hook returns early if the window is closed. While
   `Universe.WorldSun` is null it shows "Waiting for universe to load...". `WorldSun` is first assigned
   in `Universe.LoadSystem` together with `CurrentSystem` and `Roots`
   ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L56-L92), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L232-L256)).[^ksa-universe] On the first
   frame with a universe it runs the one-time JSON attachment described in
   [Body statistics derived from live game data](derived-body-properties.md).
2. **Category tree, rebuilt lazily.** `[StarMapAllModsLoaded]` already calls `CategoryLoader()`, but no
   universe exists yet, so the tree is marked as built without celestials
   ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1267-L1288)). The draw hook rebuilds it when it is empty, was built
   without celestials, or was built for another system root ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L93-L120)).
   `CategoryLoader` collects the root's celestials, treats bodies whose parent is not a `Celestial` as
   top level, takes category names from the JSON `ListGroups` of top-level bodies (with a hard-coded
   fallback list), adds each top-level body and its children (as dotted parent paths) to every category
   it lists, puts unmatched top-level bodies in Other, attaches category text from `ListGroupsData`,
   drops empty categories and adds a Stars category ([src/CdmCategoryLoader.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmCategoryLoader.cs#L13-L265)).[^cmp-cdmcategoryloader]
3. **Window and panes.** `SetNextWindowPos`/`SetNextWindowSize` with `ImGuiCond.FirstUseEver`, a
   background alpha from the opacity slider, `ImGui.Begin("Compendium", ref CompendiumWindow)` (the
   close button writes the flag), then the game's own console look via `ConsoleStyle.PushWidgetStyle`
   ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L128-L144), [current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L575-L650)).[^ksa-consolestyle] The
   left pane is a `BeginChild` of fixed width; a thin `Button("##splitter")` between the panes changes
   that width by `ImGui.GetIO().MouseDelta.X` while it is held, clamps it, and shows the resize cursor
   on hover; the right pane is a second child that takes the rest ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L387-L405)).
4. **Top row.** Right-aligned buttons are sized with `CalcTextSize` and placed with `SetCursorPosX`. S
   calls `Universe.MoveCameraTo(root)` then `Program.SetOrbitCamera(0.3, -0.43, 905)` for a zoomed-out
   view of the system ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L163-L210), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2527-L2532),
   [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3041-L3050)).[^ksa-program] V opens a borderless, auto-sized window positioned
   under the button that lists every `Vehicle` in `CurrentSystem.All` and, on click, sets
   `Program.ControlledVehicle` and moves the camera to it ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L211-L255),
   [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L66), [current/decomp/KSA/LookupCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LookupCollection.cs#L210)).[^ksa-celestialsystem] [^ksa-lookupcollection]
5. **Star-system combo.** `DrawSystemSelector` lists every root of `Universe.Roots` sorted by display
   name in a `BeginCombo`, focuses an `InputText` search box when the popup appears, filters by
   substring and marks the current entry with `SetItemDefaultFocus`; choosing a system resets the view
   and forces a category rebuild ([src/CdmStarSystems.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmStarSystems.cs#L116-L172)).[^cmp-cdmstarsystems]
6. **Category buttons and body list.** Category keys are sorted with Stars and Planets first and Other
   last ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L309-L385)). `PrintSelectedCategoryByKey` draws from a per-category
   display cache: a `CollapsingHeader` per parent body, its moons directly, other children grouped by
   their JSON `OrbitLineGroup` in `TreeNode`s, and for Asteroids and Comets the top-level bodies grouped
   by orbit group ([src/CdmPrintSelectedCategory.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmPrintSelectedCategory.cs#L32-L94), [src/CdmPrintSelectedCategory.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmPrintSelectedCategory.cs#L230-L341)).[^cmp-cdmprintselectedcategory]
   Selecting a body, group or category switches the right pane through a `showWindow` string.
7. **Right pane.** The body page shows JSON-derived statistics, a Wikipedia link with
   `ImGui.TextLinkOpenURL`, a collapsing "More Celestial Properties" section, a focus button, orbit-line
   mode and color controls, target controls, subgroup toggles and the JSON text, factoids and
   "visited by" list ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L495-L830)). Category and subgroup pages have bulk
   orbit-line controls and description text ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L831-L1069)).
8. **Failure containment.** Everything sits in one `try`. The catch logs the exception, pops the
   console style if it was pushed, and calls `EndChild` and `End` once each inside their own `try`
   blocks ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1077-L1090)). This is best effort rather than exact: the catch
   does not know how many scopes were open (see Pitfalls).

## Lifecycle and timing

- **`frame:draw-ui`.** `[StarMapAfterGui]` is a StarMap Harmony postfix on `Program.OnDrawUiViewports`
  ([StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L26-L36)).[^starmap-programpatcher] In `Program.OnFrame`
  that call sits inside `if (DrawUI)` between `PrepareImGui` (ImGui `NewFrame`) and `ImGui.Render`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2261-L2286)), after `OnFrameViewports` has updated this frame's cameras. ImGui
  calls are legal and the window disappears with the rest of the HUD.
- **What it reads.** Celestial positions and the body tree were refreshed at `ApplyOrbitSolvers` /
  `ApplyVehicleSolvers` earlier in the frame; the next solver step is already running on its own copies,
  so reading main-thread objects here needs no lock and is one step behind the workers.
- **What it writes.** UI state, camera follow (`Universe.MoveCameraTo`, `Program.SetOrbitCamera`) and
  `Program.ControlledVehicle`, a static property whose setter only clears the previous vehicle's held
  player input ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502-L515)) and that the game itself also assigns outside the input
  buffers, for example in `Camera.SetFollow` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L602-L618)).[^ksa-camera] Target
  changes are deferred (see [the settarget feature](native-target-selection.md)).
- **`startup:all-mods-loaded`.** `[StarMapAllModsLoaded]` is a StarMap postfix on `ModLibrary.LoadAll`,
  before any universe exists ([StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L17-L28)); the early
  `CategoryLoader` call there finds no bodies, so every category is empty and removed, and the real tree
  is built on the first frame with a universe.[^starmap-modlibrarypatches]

## Key code

| Path | Role |
|---|---|
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L56-L144) | Universe gate, one-time attach, lazy category rebuild, window setup |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L146-L307) | Top-row buttons, vessel picker, font settings |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L387-L492) | Splitter, side pane, glossary and start page |
| [src/CdmCategoryLoader.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmCategoryLoader.cs#L13-L265) | Category tree from JSON and the live body tree |
| [src/CdmPrintSelectedCategory.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmPrintSelectedCategory.cs#L32-L362) | Cached body list with headers and subgroups |
| [src/CdmStarSystems.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmStarSystems.cs#L130-L172) | Star-system combo with search |

## Snippets

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

```csharp
// src/CompendiumMain.cs L387-L405
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

```csharp
// src/CompendiumMain.cs L223-L254
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
            }
            if (isCurrent) ImGui.PopStyleColor();
        }
        if (!foundVessels)
        {
            ImGui.Text("No vessels in current system");
        }
        }
        ImGui.End();
    }
```

```csharp
// src/CompendiumMain.cs L1077-L1090
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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Universe.WorldSun`, `CurrentSystem`, `Roots`, `LoadSystem` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L106-L114) | Universe-loaded gate and system list |
| `Universe.MoveCameraTo` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2527-L2532) | Focus buttons (hovered viewport's camera) |
| `Program.SetOrbitCamera` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3041-L3050) | Zoomed-out system view |
| `Program.ControlledVehicle` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502-L515) | Vessel picker |
| `CelestialSystem.All`, `LookupCollection.UnsafeAsList` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L66) | Vehicle list |
| `ConsoleStyle.PushWidgetStyle`, `PopWidgetStyle` | [current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L575-L650) | Game console look for widgets |
| `ConsoleWidgets.Button` | [current/decomp/KSA/ConsoleWidgets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleWidgets.cs#L142-L160) | Console-styled buttons |
| `ImGui.Begin`, `BeginChild`, `BeginCombo`, `InputText`, `CollapsingHeader`, `TreeNode`, `TextLinkOpenURL` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L6348) | Window content |

## Pitfalls and version notes

- **`WorldSun` is not "the system".** It follows the camera: `Program.Render` calls
  `Universe.UpdateWorldSun` with the main camera position every frame ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2460)), so
  Compendium keeps its own selected root and only falls back to the root of `WorldSun`.
- **Focusing a body releases the vessel.** `Universe.MoveCameraTo` calls
  `camera.SetFollow(obj, ..., hoveredViewport.IsMain())`, and `SetFollow` with `changeControl` sets
  `Program.ControlledVehicle = target as Vehicle`, which is null for a celestial
  ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2527-L2532), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L602-L618), [current/decomp/KSA/ViewportEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportEx.cs#L72-L75)).
  After "Focus Camera on ..." over the main viewport no vehicle is controlled, and the target buttons
  show "No Controlled Vehicle" until a vessel is picked again.
- **One-time attach.** The JSON-to-body attachment runs once per session (a static flag), so a later
  system load does not recompute it.
- **Per-frame allocations.** Labels are new `ImString`s every frame and the vessel picker runs LINQ over
  all bodies while open. Harmless at UI rate.
- **Top row depends on fonts.** The M/S/V/D buttons, the vessel picker and the font settings are
  inside `if (fontNames.Length > 0)`, so with no fonts loaded (an empty `Fonts` folder, or a patch
  failure that skips `LoadFonts`) the window shows only a "No fonts found" line in their place
  ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L146-L307)).
- **Hidden HUD.** `StarMapAfterGui` does not fire when `Program.DrawUI` is false.
- **`End` must follow every `Begin`.** The waiting branch calls `ImGui.End()` only when
  `ImGui.Begin` returned true, so collapsing the window before a universe loads leaves the window
  open on ImGui's stack; ImGui requires `End` whatever `Begin` returns (the main branch does this
  correctly) ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L73-L82)).
- **The catch can pop the wrong scope.** It always calls one `EndChild` and one `End`. An exception
  thrown before `Begin` (for example in `CategoryLoader`) or while a nested child, tree node or the
  vessel-picker window is open ends the wrong scope, and fonts pushed with `PushTheFont` are not
  popped. Tracking what was opened (as the code already does for the console style) and unwinding
  exactly that is the safe version of this pattern.
- **No ModMenu, no reopen.** Without ModMenu installed, closing the window with its X leaves no in-game
  way to reopen it except editing `settings.ini` (see [ModMenu entry that toggles the window](modmenu-window-toggle.md)).

## See also

- [Star systems and the body tree](star-systems-and-body-tree.md)
- [Cross-mod JSON catalog](cross-mod-json-catalog.md)
- [Body statistics derived from live game data](derived-body-properties.md)
- [Atmosphere density and pressure graphs](atmosphere-profile-graph.md)
- [Custom fonts in the ImGui atlas](custom-fonts-in-imgui.md)
- [Logo image as an ImGui texture](logo-image-as-imgui-texture.md)
- [Orbit line visibility and color](orbit-line-visibility-and-color.md)
- [Pointer lines to the selected body](body-pointer-overlay.md)
- [Searchable lists: SetKeyboardFocusHere on IsWindowAppearing and SetItemDefaultFocus](../../../howto/ui/windows/build-searchable-lists-and-browsers/build-searchable-lists-and-browsers-2.md) - how-to article demonstrating this feature
- [Searchable lists: a draggable splitter between two panes, a universe guard and a go-to button](../../../howto/ui/windows/build-searchable-lists-and-browsers/build-searchable-lists-and-browsers-5.md) - how-to article demonstrating this feature

[^cmp-compendiummain]: Compendium: src/CompendiumMain.cs
[^cmp-cdmsettings]: Compendium: src/CdmSettings.cs
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^cmp-cdmcategoryloader]: Compendium: src/CdmCategoryLoader.cs
[^ksa-consolestyle]: KSA.ConsoleStyle (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-lookupcollection]: KSA.LookupCollection (decompiled, build 2026.10.10.5554)
[^cmp-cdmstarsystems]: Compendium: src/CdmStarSystems.cs
[^cmp-cdmprintselectedcategory]: Compendium: src/CdmPrintSelectedCategory.cs
[^starmap-programpatcher]: StarMap: StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^starmap-modlibrarypatches]: StarMap: StarMap.Core/Patches/ModLibraryPatches.cs
