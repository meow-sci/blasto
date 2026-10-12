# UI

ImGui windows, menu bar and ModMenu entries, fonts, draw-list overlays and widgets, hotkeys and input focus, stock-window injection, HUD overlays and settings UIs for KSA mods.

## Input

Mouse and keyboard: cursor rays and picking, grid placement, sharing input between ImGui, mod tools and the game, hotkeys and text entry.

### [Block game hotkeys while typing into an ImGui field](howto/ui/input/block-game-hotkeys-while-typing/overview.md)

Why KSA bindings fire while a mod text box has focus, the key and mouse dispatch chain they travel, and two ways mods stop them

- [Block game hotkeys while typing: prefix on GameSettings.OnKeyAll when ImGui wants text input](howto/ui/input/block-game-hotkeys-while-typing/block-game-hotkeys-while-typing-1.md) — beginner
- [Block game hotkeys while typing: gate game input while a terminal has focus](howto/ui/input/block-game-hotkeys-while-typing/block-game-hotkeys-while-typing-2.md) — advanced

### [Share the keyboard and mouse between ImGui, mod tools and the game](howto/ui/input/coordinate-imgui-focus-with-game-input/overview.md)

How KSA decides who gets a key or click - ImGui, the controller, the vehicle or a hotkey - and five ways mods read input without fighting the game

- [Share input with the game: gate on WantTextInput and WantCaptureMouse in the GUI hook](howto/ui/input/coordinate-imgui-focus-with-game-input/coordinate-imgui-focus-with-game-input-1.md) — beginner
- [Share input with the game: ignore clicks ImGui owns and keep working with the HUD hidden](howto/ui/input/coordinate-imgui-focus-with-game-input/coordinate-imgui-focus-with-game-input-2.md) — beginner
- [Share input with the game: NoNav and NoFocusOnAppearing windows that never take the keyboard](howto/ui/input/coordinate-imgui-focus-with-game-input/coordinate-imgui-focus-with-game-input-3.md) — beginner
- [Share input with the game: WantCaptureKeyboard drops key events; Vehicle.OnKey patches add new key meanings](howto/ui/input/coordinate-imgui-focus-with-game-input/coordinate-imgui-focus-with-game-input-4.md) — advanced
- [Share input with the game: a FixedController subclass swallows OnKey, OnMouseButton and OnScroll](howto/ui/input/coordinate-imgui-focus-with-game-input/coordinate-imgui-focus-with-game-input-5.md) — advanced

### [Turn the mouse cursor into a world ray and pick points on a construction plane](howto/ui/input/cursor-to-world-ray/overview.md)

How ImGui's cursor becomes an ecliptic ray through the camera of the viewport it is over, the camera ego frame the ray lives in, and how five mods verify and use it

- [Cursor to world ray: ScreenToEgoRay with viewport offset and framebuffer scale](howto/ui/input/cursor-to-world-ray/cursor-to-world-ray-1.md) — intermediate
- [Cursor to world ray: viewport lookup, EgoToEcl and a projected round trip](howto/ui/input/cursor-to-world-ray/cursor-to-world-ray-2.md) — intermediate
- [Cursor to world ray: convert to camera framebuffer pixels before ScreenToEgoRay](howto/ui/input/cursor-to-world-ray/cursor-to-world-ray-3.md) — intermediate
- [Cursor to world ray: Cursor.GetEgoRay and Moller-Trumbore tests against in-world quads](howto/ui/input/cursor-to-world-ray/cursor-to-world-ray-4.md) — intermediate
- [Cursor to world ray: the ego frame, EgoToEcl and picking points on a construction plane](howto/ui/input/cursor-to-world-ray/cursor-to-world-ray-5.md) — advanced

### [Pick the vessel, part, world quad or terrain point under the cursor](howto/ui/input/pick-objects-under-the-cursor/overview.md)

How a mod decides what the mouse is over in the 3D view, from screen-space nearest through hull ray casts to terrain marches, and how it acts on the answer

- [Pick objects under the cursor: screen-space nearest with a per-item reach](howto/ui/input/pick-objects-under-the-cursor/pick-objects-under-the-cursor-1.md) — beginner
- [Pick objects under the cursor: Part.RayCastEgo hull test plus a terrain march](howto/ui/input/pick-objects-under-the-cursor/pick-objects-under-the-cursor-2.md) — intermediate
- [Pick objects under the cursor: ray hit to texture UV for interactive in-world UI](howto/ui/input/pick-objects-under-the-cursor/pick-objects-under-the-cursor-3.md) — advanced
- [Pick objects under the cursor: clearance-stepped terrain march with bisection, then a buffered TeleportToLocation](howto/ui/input/pick-objects-under-the-cursor/pick-objects-under-the-cursor-4.md) — advanced
- [Pick objects under the cursor: Ray.RaycastWatertight against parts, then a terrain march with bisection](howto/ui/input/pick-objects-under-the-cursor/pick-objects-under-the-cursor-5.md) — advanced

### [Place pieces on a grid with a snapping ghost, top-down camera and world gizmos](howto/ui/input/place-pieces-on-a-grid-with-a-ghost/overview.md)

The parts of a world placement tool - cursor ray into a local frame, ray-plane cell pick, a ghost that snaps to ports, a borrowed top-down camera, and gizmos and meshes drawn into the world

- [Place pieces on a grid: ray-plane cell pick, port-snapping ghost, top-down camera and per-viewport meshes](howto/ui/input/place-pieces-on-a-grid-with-a-ghost/place-pieces-on-a-grid-with-a-ghost-1.md) — advanced
- [Place pieces with a ghost: gizmo rings anchored with GetPositionEgo](howto/ui/input/place-pieces-on-a-grid-with-a-ghost/place-pieces-on-a-grid-with-a-ghost-2.md) — intermediate

### [Read typed text, key chords and the clipboard from ImGui](howto/ui/input/read-typed-text-and-key-chords-from-imgui/overview.md)

How text, chords and clipboard reach a mod through ImGui's IO and the GLFW-backed platform clipboard, and two mods that use them

- [Read typed text and key chords from ImGui: character queue versus IsKeyPressed, with clipboard paste chords](howto/ui/input/read-typed-text-and-key-chords-from-imgui/read-typed-text-and-key-chords-from-imgui-1.md) — advanced
- [Copy to the clipboard from ImGui: selectable rows that build text only on click](howto/ui/input/read-typed-text-and-key-chords-from-imgui/read-typed-text-and-key-chords-from-imgui-2.md) — beginner

### [Implement rebindable hotkeys with ImGui key state](howto/ui/input/rebindable-hotkeys/overview.md)

How mods bind, poll and re-capture hotkeys from ImGui key state instead of KSA's input bindings, with enum-name JSON and key-plus-modifier TOML variants

- [Rebindable hotkeys: enum names in JSON and a capture loop over the ImGuiKey range](howto/ui/input/rebindable-hotkeys/rebindable-hotkeys-1.md) — beginner
- [Rebindable hotkeys: key plus modifiers with a modal capture, stored in TOML](howto/ui/input/rebindable-hotkeys/rebindable-hotkeys-2.md) — intermediate

## Windows

Mod windows that look and behave like the game's: ImGuiWindow subclasses, stock styling, tabs, settings panels, searchable lists, live data, alerts and loading-time dialogs.

### [Build a mod settings window (and developer panel)](howto/ui/windows/build-a-settings-window/overview.md)

KSA has no settings-window API for mods, so six mods show six ImGui layouts - tabbed tables with colour pickers, label-gutter forms, ColorButton popups with INI, small auto-sized windows with tooltips, an XKCD colour catalogue and a developer panel

- [Settings window: slider-plus-input rows, ColorPicker4 on a float4 and Reset buttons](howto/ui/windows/build-a-settings-window/build-a-settings-window-1.md) — intermediate
- [Settings window: label-gutter form tables and full-width widgets](howto/ui/windows/build-a-settings-window/build-a-settings-window-2.md) — intermediate
- [Settings window: ColorButton popups and a hand-parsed INI file](howto/ui/windows/build-a-settings-window/build-a-settings-window-3.md) — beginner
- [Settings window: a menu entry, a small auto-sized window and wrapped tooltips](howto/ui/windows/build-a-settings-window/build-a-settings-window-4.md) — beginner
- [Settings window: a reflected XKCD colour catalogue behind a filterable picker](howto/ui/windows/build-a-settings-window/build-a-settings-window-5.md) — intermediate
- [Settings window: a developer panel opened from a menu, ModMenu or a floating button](howto/ui/windows/build-a-settings-window/build-a-settings-window-6.md) — intermediate

### [Organise a mod window: tabs, button tabs, tables and pop-out sections](howto/ui/windows/build-a-tabbed-imgui-window/overview.md)

KSA gives mods plain Dear ImGui for tabs, so five mods show five structures - a real tab bar, a per-craft window with panes, hand-drawn tab buttons, button tabs over a table, and sections that pop out into windows

- [Tabbed window: a closeable ImGui tab bar with programmatic selection](howto/ui/windows/build-a-tabbed-imgui-window/build-a-tabbed-imgui-window-1.md) — intermediate
- [Tabbed window: a per-craft window with tabs, pop-out panes and a reopen button](howto/ui/windows/build-a-tabbed-imgui-window/build-a-tabbed-imgui-window-2.md) — intermediate
- [Tabbed window: title-bar-coloured custom tab buttons](howto/ui/windows/build-a-tabbed-imgui-window/build-a-tabbed-imgui-window-3.md) — beginner
- [Tabbed window: button tabs driving a table of collapsing headers](howto/ui/windows/build-a-tabbed-imgui-window/build-a-tabbed-imgui-window-4.md) — intermediate
- [Tabbed window: collapsible sections that pop out into their own windows](howto/ui/windows/build-a-tabbed-imgui-window/build-a-tabbed-imgui-window-5.md) — advanced

### [Build searchable lists, two-pane browsers and file pickers in ImGui](howto/ui/windows/build-searchable-lists-and-browsers/overview.md)

How Brutal's ImInputString behaves in filter boxes, and six ways mods build searchable combos, ranked search lists, two-pane browsers and file pickers

- [Searchable lists: a live substring filter inside a combo, with ImInputString length evaluated](howto/ui/windows/build-searchable-lists-and-browsers/build-searchable-lists-and-browsers-1.md) — intermediate
- [Searchable lists: SetKeyboardFocusHere on IsWindowAppearing and SetItemDefaultFocus](howto/ui/windows/build-searchable-lists-and-browsers/build-searchable-lists-and-browsers-2.md) — beginner
- [Searchable lists: InputTextWithHint with ranked results and reveal-on-pick](howto/ui/windows/build-searchable-lists-and-browsers/build-searchable-lists-and-browsers-3.md) — intermediate
- [Searchable lists: a filterable colour catalogue list](howto/ui/windows/build-searchable-lists-and-browsers/build-searchable-lists-and-browsers-4.md) — beginner
- [Searchable lists: a draggable splitter between two panes, a universe guard and a go-to button](howto/ui/windows/build-searchable-lists-and-browsers/build-searchable-lists-and-browsers-5.md) — intermediate
- [Searchable lists: a file browser that copies picks into a flat shared catalogue](howto/ui/windows/build-searchable-lists-and-browsers/build-searchable-lists-and-browsers-6.md) — advanced

### [Make mod UI look like the game: console widgets, window skin and global theme](howto/ui/windows/match-the-stock-console-ui-style/overview.md)

How KSA's console style is built from public ConsoleStyle and ConsoleWidgets helpers, and four ways mods use them or the global ImGuiStyle to match or re-theme the UI

- [Match the stock UI: ConsoleWidgets rows inside an ImGuiWindow subclass without console chrome](howto/ui/windows/match-the-stock-console-ui-style/match-the-stock-console-ui-style-1.md) — beginner
- [Match the stock UI: a plain ImGui.Begin window plus PushWidgetStyle, with custom tab buttons](howto/ui/windows/match-the-stock-console-ui-style/match-the-stock-console-ui-style-2.md) — intermediate
- [Match the stock UI: ConsoleStyle window skin unpinned from the main viewport and mirrored row helpers](howto/ui/windows/match-the-stock-console-ui-style/match-the-stock-console-ui-style-3.md) — intermediate
- [Re-theme every KSA and mod window through the global ImGuiStyle (capture, restore and TOML presets)](howto/ui/windows/match-the-stock-console-ui-style/match-the-stock-console-ui-style-4.md) — advanced

### [Show a modal ImGui dialog during loading, before the game loop runs](howto/ui/windows/show-a-dialog-during-loading/overview.md)

How KSA's own ConfirmMods and SelectSystem dialogs pump private frames inside the Program constructor, and two ways mods copy that pattern - a restart question and a fail-fast error dialog

- [Loading dialog: a modal question modelled on ConfirmMods, with its own frame pump](howto/ui/windows/show-a-dialog-during-loading/show-a-dialog-during-loading-1.md) — advanced
- [Loading dialog: a PopupTask loop with ErrorAndExit](howto/ui/windows/show-a-dialog-during-loading/show-a-dialog-during-loading-2.md) — advanced

### [Show transient alerts and game-styled popups in game](howto/ui/windows/show-alerts-and-popups-in-game/overview.md)

How KSA draws its centred timed alerts and its console-styled Popup modals, and two ways mods use them - TimedAlert from main-thread hooks and a Popup subclass that registers itself

- [Alerts: TimedAlert.Create and CreateWarning from main-thread hooks](howto/ui/windows/show-alerts-and-popups-in-game/show-alerts-and-popups-in-game-1.md) — beginner
- [Popups: a Popup subclass that registers itself and is drawn by Popup.DrawAll](howto/ui/windows/show-alerts-and-popups-in-game/show-alerts-and-popups-in-game-2.md) — intermediate

### [Show live vehicle and editor data in an ImGui window or HUD](howto/ui/windows/show-live-vehicle-data-in-a-window/overview.md)

Where the controlled vehicle's settled physics, orbit and engine state (or the editor's part tree) can be read safely, and three ways to present it - a StarMap GUI hook, a postfix on Vehicle.OnDrawUi, a postfix on VehicleEditor.OnDrawUi

- [Live vehicle data: a plain ImGui window from StarMapAfterGui with guarded Begin and End](howto/ui/windows/show-live-vehicle-data-in-a-window/show-live-vehicle-data-in-a-window-1.md) — beginner
- [Live vehicle data: Harmony postfix on Vehicle.OnDrawUi filtered to the main viewport](howto/ui/windows/show-live-vehicle-data-in-a-window/show-live-vehicle-data-in-a-window-2.md) — advanced
- [Live vehicle data: Harmony postfix on VehicleEditor.OnDrawUi for the part tree being built](howto/ui/windows/show-live-vehicle-data-in-a-window/show-live-vehicle-data-in-a-window-3.md) — advanced

### [Build a mod window on the game's ImGuiWindow base class, with stable ids and saved placement](howto/ui/windows/subclass-the-stock-imguiwindow/overview.md)

How KSA.ImGuiWindow gives mod windows stock chrome, menu integration and static registration, what it does not persist, and when a plain ImGui.Begin window is the better choice

- [Stock window base class: a lazily created ImGuiWindow drawn from StarMapAfterGui](howto/ui/windows/subclass-the-stock-imguiwindow/subclass-the-stock-imguiwindow-1.md) — beginner
- [Stock window base class: guarded DrawContent with title-bar close detection](howto/ui/windows/subclass-the-stock-imguiwindow/subclass-the-stock-imguiwindow-2.md) — beginner
- [Stock window base class: IStaticWindow auto-registration with a mod-owned layout persisted to config](howto/ui/windows/subclass-the-stock-imguiwindow/subclass-the-stock-imguiwindow-3.md) — intermediate
- [Stable window ids: Label###stable-id for renameable windows and a fixed id for plain windows](howto/ui/windows/subclass-the-stock-imguiwindow/subclass-the-stock-imguiwindow-4.md) — beginner
- [Chrome-less plain-ImGui windows: alpha-0 title bar, InvisibleButton drag guard and resize snapped to a character grid](howto/ui/windows/subclass-the-stock-imguiwindow/subclass-the-stock-imguiwindow-5.md) — advanced

## Overlays

Full-screen overlays and HUDs: choosing the draw list, staying visible and stepping when the HUD is hidden, and instrument-style HUD drawing.

### [Draw full-screen click-through overlays and choose the right ImGui draw list](howto/ui/overlays/draw-a-fullscreen-click-through-overlay/overview.md)

How mods paint over the game view with ImGui - input-less windows, the background and foreground draw lists, click-through lock mode and per-viewport lists - and what each choice means for z-order, input and secondary viewports

- [Full-screen overlay: a NoInputs, NoBackground window sized to the main viewport](howto/ui/overlays/draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-1.md) — beginner
- [Overlays without a window: ImGuiHelper.GetOverlayDrawList](howto/ui/overlays/draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-2.md) — beginner
- [Overlays without a window: ImGui.GetForegroundDrawList anchored to WorkPos and WorkSize](howto/ui/overlays/draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-3.md) — intermediate
- [Click-through lock mode: a window that ignores the mouse until a hot zone refocuses it](howto/ui/overlays/draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-4.md) — intermediate
- [Overlays on secondary and torn-off viewports: per-surface draw list and per-viewport ego transforms](howto/ui/overlays/draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-5.md) — advanced

### [Draw HUD instruments: arc gauges, gradients, scrolling tapes and animated intros](howto/ui/overlays/draw-hud-instruments-with-the-draw-list/overview.md)

Building broadcast-style and fighter-style instruments from ImGui draw-list primitives, and the draw-list limits that shape the techniques

- [HUD instruments: PathArcTo plus PathStroke arcs and vertex-alpha gradients](howto/ui/overlays/draw-hud-instruments-with-the-draw-list/draw-hud-instruments-with-the-draw-list-1.md) — intermediate
- [HUD instruments: overlapping staged intro and outro animation with easing and edge-in stagger](howto/ui/overlays/draw-hud-instruments-with-the-draw-list/draw-hud-instruments-with-the-draw-list-2.md) — intermediate
- [HUD instruments: clip rects per region, scrolling tapes and endpoint rotation](howto/ui/overlays/draw-hud-instruments-with-the-draw-list/draw-hud-instruments-with-the-draw-list-3.md) — intermediate

### [Keep mod UI and per-frame work running when the HUD is hidden (F2)](howto/ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md)

Why StarMap's GUI hooks stop when the player hides the HUD, which game calls still run, and four ways mods keep drawing or stepping

- [Keep drawing when the HUD is hidden: Harmony postfix on Program.OnFrameEditor](howto/ui/overlays/draw-ui-while-the-hud-is-hidden/draw-ui-while-the-hud-is-hidden-1.md) — intermediate
- [Keep drawing when the HUD is hidden: reflection-installed postfix on the private Program.DrawFps](howto/ui/overlays/draw-ui-while-the-hud-is-hidden/draw-ui-while-the-hud-is-hidden-2.md) — advanced
- [Keep per-frame work running when the HUD is hidden: replay StarMap GUI work from a prefix on OnDrawUiConsole](howto/ui/overlays/draw-ui-while-the-hud-is-hidden/draw-ui-while-the-hud-is-hidden-3.md) — intermediate
- [Run per-frame mod logic once per frame from whichever hook fires first (FrameLatch, pre-render prefix and sim-time steps)](howto/ui/overlays/draw-ui-while-the-hud-is-hidden/draw-ui-while-the-hud-is-hidden-4.md) — advanced

## Draw lists

Custom drawing inside windows with ImDrawList: charts, widgets, text grids, canvases, diagrams and fonts.

### [Build pannable, zoomable and 3D canvases inside an ImGui window](howto/ui/draw-lists/build-a-pan-zoom-canvas-in-imgui/overview.md)

A clipped canvas with a full-size InvisibleButton for pan, zoom about the cursor, hover and click, and a 3D preview canvas with wireframe gizmos, ray picking and undo

- [Pan and zoom a 2D canvas: InvisibleButton, wheel key ownership, clip rect and priority label culling](howto/ui/draw-lists/build-a-pan-zoom-canvas-in-imgui/build-a-pan-zoom-canvas-in-imgui-1.md) — advanced
- [Build a 3D orbit canvas with CPU-projected gizmos, ray picking and undo](howto/ui/draw-lists/build-a-pan-zoom-canvas-in-imgui/build-a-pan-zoom-canvas-in-imgui-2.md) — advanced

### [Build custom-drawn ImGui widgets, icons, trees and alert tiles on the draw list](howto/ui/draw-lists/build-custom-drawn-imgui-widgets/overview.md)

Laying out rows, tiles, gauges, toggles, icons, glyphs, trees and rule-driven alert tiles yourself with ImDrawList and InvisibleButton hit-testing instead of stock widgets or textures

- [Build a draw-list widget kit: rows, gauges, toggles and sections with a runtime-editable theme](howto/ui/draw-lists/build-custom-drawn-imgui-widgets/build-custom-drawn-imgui-widgets-1.md) — intermediate
- [Draw icon buttons from primitives, with no textures or icon font](howto/ui/draw-lists/build-custom-drawn-imgui-widgets/build-custom-drawn-imgui-widgets-2.md) — beginner
- [Share one glyph function between a canvas and its legend](howto/ui/draw-lists/build-custom-drawn-imgui-widgets/build-custom-drawn-imgui-widgets-3.md) — intermediate
- [Write a generic tree view with static abstract adapters, ChannelsSplit and ImGui storage](howto/ui/draw-lists/build-custom-drawn-imgui-widgets/build-custom-drawn-imgui-widgets-4.md) — advanced
- [Evaluate threshold rules once per frame and render them as banner, tile outline, warning row and graph line](howto/ui/draw-lists/build-custom-drawn-imgui-widgets/build-custom-drawn-imgui-widgets-5.md) — intermediate

### [Draw charts, scopes and relief maps with the ImGui draw list](howto/ui/draw-lists/draw-a-chart-with-imdrawlist/overview.md)

Plotting time series, functions of altitude and map-like instruments from ImDrawList primitives inside a KSA mod window, with the game data each one samples

- [Chart a time series with nice axes, extrema, a dashed threshold and a hover crosshair](howto/ui/draw-lists/draw-a-chart-with-imdrawlist/draw-a-chart-with-imdrawlist-1.md) — intermediate
- [Plot a game function against altitude with InvisibleButton space reservation](howto/ui/draw-lists/draw-a-chart-with-imdrawlist/draw-a-chart-with-imdrawlist-2.md) — beginner
- [Draw a craft-centred polar scope and a cached hill-shaded terrain map](howto/ui/draw-lists/draw-a-chart-with-imdrawlist/draw-a-chart-with-imdrawlist-3.md) — advanced

### [Draw per-frame text efficiently: monospace grids and allocation-free labels](howto/ui/draw-lists/draw-a-monospace-text-grid/overview.md)

Rendering a terminal-style cell grid with run batching, and formatting per-frame ImGui text without allocations using cached strings, stackalloc buffers and span builders

- [Draw a monospace cell grid with merged background rects, batched glyph runs and shape-drawn blocks](howto/ui/draw-lists/draw-a-monospace-text-grid/draw-a-monospace-text-grid-1.md) — advanced
- [Cache a display string until the shown value changes](howto/ui/draw-lists/draw-a-monospace-text-grid/draw-a-monospace-text-grid-2.md) — beginner
- [Format per-frame labels on the stack with stackalloc, SpanBuilder and the game unit formatters](howto/ui/draw-lists/draw-a-monospace-text-grid/draw-a-monospace-text-grid-3.md) — intermediate

### [Lay out a tree or graph diagram and keep it usable at scale](howto/ui/draw-lists/lay-out-a-tree-or-graph-diagram/overview.md)

A game-type-free layout pipeline bounded on the draw thread, plus aggregation, search-to-reveal, stable scene composition and vessel-driven re-rooting for diagrams with thousands of nodes

- [Lay out a tree in three modes with a game-free pipeline, Barnes-Hut and an iteration budget](howto/ui/draw-lists/lay-out-a-tree-or-graph-diagram/lay-out-a-tree-or-graph-diagram-1.md) — advanced
- [Keep a huge diagram usable with aggregation, search-to-reveal and a hard node ceiling](howto/ui/draw-lists/lay-out-a-tree-or-graph-diagram/lay-out-a-tree-or-graph-diagram-2.md) — advanced
- [Compose cached per-system layouts around a home layout that never moves](howto/ui/draw-lists/lay-out-a-tree-or-graph-diagram/lay-out-a-tree-or-graph-diagram-3.md) — advanced
- [Re-root a diagram when the controlled vessel changes: reference comparison after PrepareFrame](howto/ui/draw-lists/lay-out-a-tree-or-graph-diagram/lay-out-a-tree-or-graph-diagram-4.md) — advanced

### [Load a custom TTF font into KSA's ImGui atlas and keep it across font rebuilds](howto/ui/draw-lists/load-a-custom-font-into-imgui/overview.md)

How to add a bundled font to the shared ImGui atlas at the right moment, draw it at any size, avoid KSA auto-loading it, and survive FontManager.RegenerateFonts clearing the atlas

- [Add a bundled TTF to the ImGui atlas at StarMapImmediateLoad and draw it at any size](howto/ui/draw-lists/load-a-custom-font-into-imgui/load-a-custom-font-into-imgui-1.md) — beginner
- [Add fonts lazily on the first UI frame with the game's own ImFontConfig fields](howto/ui/draw-lists/load-a-custom-font-into-imgui/load-a-custom-font-into-imgui-2.md) — intermediate
- [Add a font family at StarMapAllModsLoaded under a renamed extension so KSA does not auto-load it](howto/ui/draw-lists/load-a-custom-font-into-imgui/load-a-custom-font-into-imgui-3.md) — intermediate
- [Keep mod fonts alive when the game rebuilds its font atlas](howto/ui/draw-lists/load-a-custom-font-into-imgui/load-a-custom-font-into-imgui-4.md) — advanced

## Menus

Getting into the game's own UI: the menu bar, ModMenu with and without a hard dependency, injecting into stock windows and menus, and cockpit gauges.

### [Add a menu that uses ModMenu when present and the game menu bar otherwise](howto/ui/menus/add-a-menu-with-modmenu-fallback/overview.md)

How to show one mod menu through ModMenu or through KSA's own menu bar or a standalone window depending on what is installed, without showing it twice

- [Show the same menu through ModMenu or a Harmony postfix, chosen once with ModLibrary.Find](howto/ui/menus/add-a-menu-with-modmenu-fallback/add-a-menu-with-modmenu-fallback-1.md) — intermediate
- [Name an active-flag property in ModMenuEntry and skip your own menu when ModMenu sets it](howto/ui/menus/add-a-menu-with-modmenu-fallback/add-a-menu-with-modmenu-fallback-2.md) — intermediate
- [Render one settings tree as a ModMenu menu or as a hotkey-toggled window](howto/ui/menus/add-a-menu-with-modmenu-fallback/add-a-menu-with-modmenu-fallback-3.md) — beginner

### [Add a menu to the game's top menu bar](howto/ui/menus/add-items-to-the-game-menu-bar/overview.md)

How KSA draws its menu bar as its own Menu Bar window, the supported DrawProgramMenusHook extension point for adding menus, and the older BeginMainMenuBar approach that does not join it

- [Add a menu with a Harmony postfix on Program.DrawProgramMenusHook](howto/ui/menus/add-items-to-the-game-menu-bar/add-items-to-the-game-menu-bar-1.md) — beginner
- [Add a menu with BeginMainMenuBar from StarMapBeforeGui, and why it is not KSA's bar](howto/ui/menus/add-items-to-the-game-menu-bar/add-items-to-the-game-menu-bar-2.md) — beginner
- [Decorate the menu bar itself with a transpiler snapshot and a hook prefix](howto/ui/menus/add-items-to-the-game-menu-bar/add-items-to-the-game-menu-bar-3.md) — advanced

### [Inject UI into the game's own windows and menus, including the orbit right-click menu](howto/ui/menus/inject-ui-into-a-stock-window/overview.md)

How KSA's stock settings, orbit context menu, transfer planner and burn editors are drawn, and four Harmony techniques for adding controls to them at an exact spot

- [Add sections to the Settings > Mods page with a one-call transpiler](howto/ui/menus/inject-ui-into-a-stock-window/inject-ui-into-a-stock-window-1.md) — advanced
- [Add shortcuts to the orbit right-click menu with a transpiler anchored on UTF-8 label literals](howto/ui/menus/inject-ui-into-a-stock-window/inject-ui-into-a-stock-window-2.md) — advanced
- [Take over what CREATE does in the Transfer Planner with a same-signature call swap and a click gate](howto/ui/menus/inject-ui-into-a-stock-window/inject-ui-into-a-stock-window-3.md) — advanced
- [Add a block under the stock burn editors with Harmony postfixes](howto/ui/menus/inject-ui-into-a-stock-window/inject-ui-into-a-stock-window-4.md) — intermediate

### [List a mod in ModMenu with the ModMenuEntry attribute](howto/ui/menus/list-a-mod-in-modmenu/overview.md)

How a KSA mod gets an entry in the ModMenu community mod by tagging a static method with ModMenuEntry, and the four ways mods obtain or avoid the attribute type

- [Reference ModMenu.Attributes, tag a static method and ship the DLL beside your mod](howto/ui/menus/list-a-mod-in-modmenu/list-a-mod-in-modmenu-1.md) — beginner
- [Vendor ModMenu.Attributes.dll in your repository and let StarMap's per-mod resolver find it](howto/ui/menus/list-a-mod-in-modmenu/list-a-mod-in-modmenu-2.md) — intermediate
- [Copy the ModMenuEntry attribute into your mod so there is no dependency and no extra DLL](howto/ui/menus/list-a-mod-in-modmenu/list-a-mod-in-modmenu-3.md) — beginner
- [Draw a ModMenu page without referencing the game: DynamicMethod wrappers for ImGui.MenuItem and SetItemTooltip](howto/ui/menus/list-a-mod-in-modmenu/list-a-mod-in-modmenu-4.md) — advanced

### [Add to, drive and hide the stock cockpit gauges and navball](howto/ui/menus/work-with-stock-cockpit-gauges/overview.md)

How KSA's XML-defined gauge canvases are loaded, drawn and fed, and five ways mods add a button, rewrite a button's state, force a panel visible, hide the flight HUD or paint on the navball

- [Add a stock-looking gauge button: register an enum at immediate load and append the Button with an XML patch](howto/ui/menus/work-with-stock-cockpit-gauges/work-with-stock-cockpit-gauges-1.md) — advanced
- [Re-enable a stock gauge button and rewrite its tooltip with Harmony postfixes on GaugeButtonFlightComputer](howto/ui/menus/work-with-stock-cockpit-gauges/work-with-stock-cockpit-gauges-2.md) — advanced
- [Show the stock engine panel for a craft with no engines: IsContextVisible and AreAllEnginesInactive postfixes plus a throttle write](howto/ui/menus/work-with-stock-cockpit-gauges/work-with-stock-cockpit-gauges-3.md) — advanced
- [Hide the stock flight HUD with GaugeCanvas.SetEnabled and restore only what you changed](howto/ui/menus/work-with-stock-cockpit-gauges/work-with-stock-cockpit-gauges-4.md) — intermediate
- [Draw a marker on the stock navball: replicate the game's marker maths and paint on the foreground draw list](howto/ui/menus/work-with-stock-cockpit-gauges/work-with-stock-cockpit-gauges-5.md) — advanced
